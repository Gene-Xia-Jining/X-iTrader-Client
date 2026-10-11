"""headless 后端服务。

从 AppController（Qt）中抽取与 UI 无关的应用逻辑：配置/账户管理、引擎启停、
token 管理、自动更新。方法被 rpc 层以「方法名 → 协程」注册表方式调用，
EventBus 事件经 _on_event 序列化后广播给所有已连接的 RPC 客户端。

与 Qt 版的行为差异（刻意的）：
- accounts.list 不返回密码字段，客户端表单留空 = 不修改（save_account 沿用旧值）；
- 引擎状态含每账户 server_connected，供远端 UI 展示连接状态。
"""

import asyncio
import os
import shutil
from dataclasses import fields
from pathlib import Path
from typing import Any, Optional

from .. import __version__
from ..application.bootstrap import Bootstrap
from ..application.trading_engine import TradingEngine
from ..domain.entities import Account, TradingConfiguration
from ..domain.events import (
    ConfigChangedEvent,
    EventBus,
    LogEvent,
    LogLevel,
    SignalReceivedEvent,
    StatusChangedEvent,
    TradeExecutedEvent,
    TradeFailedEvent,
    TradingStartedEvent,
    TradingStoppedEvent,
)
from ..infrastructure.api.client import check_server_health, fetch_server_symbols
from ..infrastructure.proxy import apply_proxy_environment, server_host
from ..infrastructure.trading.tqsdk_executor import test_tq_auth_connection
from ..infrastructure.updater import (
    ReleaseInfo,
    apply_update_and_restart,
    cleanup_stale_backups,
    download_release,
    extract_update,
    fetch_latest_release,
    is_newer,
)


def _dataclass_dict(obj) -> dict:
    """dataclass → JSON dict；datetime 转 ISO 字符串，其余字段原样。"""
    result = {}
    for f in fields(obj):
        value = getattr(obj, f.name)
        if hasattr(value, "isoformat"):
            value = value.isoformat()
        result[f.name] = value
    return result


class BackendService:
    def __init__(self, workdir: Path):
        self.workdir = workdir
        self.bootstrap = Bootstrap(str(workdir / "data/client.db"))
        self.event_bus = self.bootstrap.event_bus
        # 每个启用账户一个引擎，可同时运行
        self._engines: dict[str, TradingEngine] = {}
        self._engine_tasks: dict[str, asyncio.Task] = {}
        self._auto_trade = False
        # 服务器连通性：常驻心跳任务周期探测 /api/health，启动即连、断线自动恢复
        self._server_online = False
        self._server_watch_task: Optional[asyncio.Task] = None
        # save_config 置位后心跳立即探测一次（新地址即时生效，不等下个周期）
        self._server_check_now = asyncio.Event()
        # 自动更新状态：已发现的版本 / 下载任务 / 暂存路径 / 包类型
        self._release: Optional[ReleaseInfo] = None
        self._update_task: Optional[asyncio.Task] = None
        self._staged_item: Optional[Path] = None
        self._update_kind = "full"
        # RPC 广播函数由 rpc 层注入：fn(event_name, data_dict)
        self._broadcast_fn = None
        # update_apply / quit 完成清理后置位，主循环据此优雅退出
        self.exit_requested = asyncio.Event()

    # -------- 生命周期 --------

    async def startup(self) -> None:
        # 清理上次更新遗留的 .old-* 备份（失败不影响启动）
        cleanup_stale_backups()
        self._apply_proxy_env(self.bootstrap.config)
        self._subscribe_events()
        self._server_watch_task = asyncio.create_task(self._server_watch_loop())

    async def shutdown(self) -> None:
        await self.stop_engines()
        if self._server_watch_task is not None:
            self._server_watch_task.cancel()
            try:
                await self._server_watch_task
            except asyncio.CancelledError:
                pass
        await self.bootstrap.shutdown()

    # -------- 服务器心跳 --------

    async def _server_watch_loop(self) -> None:
        """周期探测服务器连通性。状态变化时广播 status 事件并写日志。"""
        while True:
            try:
                await self.probe_server()
            except asyncio.CancelledError:
                raise
            except Exception as e:
                self._log(f"服务器心跳异常: {e}", LogLevel.WARNING)
            try:
                await asyncio.wait_for(self._server_check_now.wait(), timeout=10)
            except asyncio.TimeoutError:
                pass
            self._server_check_now.clear()

    async def probe_server(self) -> None:
        server_url = self.bootstrap.config.server_url
        online = False
        if server_url:
            try:
                await check_server_health(server_url)
                online = True
            except Exception:
                online = False
        if online != self._server_online:
            self._server_online = online
            if server_url:
                self._log(
                    "服务器已连接" if online else f"服务器连接断开: {server_url}",
                    LogLevel.INFO if online else LogLevel.WARNING,
                )
            self._broadcast("status", {"server_online": online})

    # -------- 查询 --------

    async def app_status(self, params: dict) -> dict:
        return {
            "version": __version__,
            "pid": os.getpid(),
            "config": self.config_dict(),
            "accounts": self.accounts_list(),
            "engines": self.engine_status(),
        }

    def config_dict(self) -> dict:
        return _dataclass_dict(self.bootstrap.config)

    def accounts_list(self) -> list[dict]:
        # 不回传密码：客户端表单留空 = 不修改，与 save_account 的沿用语义配套
        result = []
        for account in self.bootstrap.accounts:
            item = _dataclass_dict(account)
            item.pop("tq_password")
            item.pop("trade_password")
            result.append(item)
        return result

    def engine_status(self) -> dict:
        accounts = [
            {
                "account_id": account_id,
                "running": engine.is_running,
                "server_connected": engine.server_connected,
            }
            for account_id, engine in self._engines.items()
        ]
        return {
            "auto_trade": self._auto_trade,
            "trading_active": any(e.is_running for e in self._engines.values()),
            "server_online": self._server_online,
            "accounts": accounts,
        }

    # -------- 配置 --------

    def _apply_proxy_env(self, config: TradingConfiguration):
        """按配置设置代理环境变量（默认不走代理）。"""
        hosts = [server_host(config.server_url)] if config.server_url else []
        apply_proxy_environment(config.proxy_url, hosts)

    async def save_config(self, params: dict) -> dict:
        """保存全局配置（与账户无关的项），未提供的字段沿用当前值。"""
        current = self.bootstrap.config
        new_config = TradingConfiguration(
            server_url=params.get("server_url", current.server_url),
            auto_trade=current.auto_trade,
            proxy_url=params.get("proxy_url", current.proxy_url),
            auto_check_update=params.get("auto_check_update", current.auto_check_update),
            skipped_version=params.get("skipped_version", current.skipped_version),
            database_path=current.database_path,
        )
        self.bootstrap.save_config(new_config)
        self._apply_proxy_env(new_config)
        self._server_check_now.set()  # 新地址立即探测，不等下个心跳周期
        await self._on_accounts_changed()
        return {}

    async def save_account(self, params: dict) -> dict:
        """新增或更新账户记录，未提供的字段沿用已有值。

        kind 必填；未携带 account_id 时落到固定 id（sim→legacy、live→legacy-live），
        与旧版单账户配置的迁移记录对齐。
        """
        kind = params.get("kind")
        if kind not in ("sim", "live"):
            raise ValueError("kind 必须是 sim 或 live")
        symbols_raw = params.get("symbols")
        if isinstance(symbols_raw, str):
            symbols = [s.strip() for s in symbols_raw.split(",") if s.strip()]
        elif isinstance(symbols_raw, list):
            symbols = [str(s) for s in symbols_raw]
        else:
            symbols = None  # 未提供时沿用已有账户的品种

        account_id = params.get("account_id") or ("legacy" if kind == "sim" else "legacy-live")
        existing = self.bootstrap.config_service.get_account(account_id)
        account = Account(
            id=account_id,
            kind=kind,
            label=params.get("label") or (existing.label if existing else ("模拟账户" if kind == "sim" else "实盘账户")),
            tq_account=params.get("tq_account") or (existing.tq_account if existing else ""),
            tq_password=params.get("tq_password") or (existing.tq_password if existing else ""),
            broker=params.get("broker") or (existing.broker if existing else ""),
            trade_account=params.get("trade_account") or (existing.trade_account if existing else ""),
            trade_password=params.get("trade_password") or (existing.trade_password if existing else ""),
            symbols=symbols if symbols is not None else (list(existing.symbols) if existing else []),
            enabled=params.get("enabled", existing.enabled if existing else True),
        )
        self.bootstrap.save_account(account)
        await self._on_accounts_changed()
        return {"account_id": account_id}

    async def delete_account(self, params: dict) -> dict:
        account_id = params.get("account_id")
        if not account_id:
            raise ValueError("account_id 不能为空")
        self.bootstrap.delete_account(account_id)
        await self._on_accounts_changed()
        return {}

    async def _on_accounts_changed(self) -> None:
        """配置或账户变更后的状态同步。引擎运行中不重建（下次启动生效）。"""
        self.event_bus.publish(ConfigChangedEvent())
        if any(e.is_running for e in self._engines.values()):
            self._log("配置已更新（下次启动生效）", LogLevel.WARNING)
        else:
            self.bootstrap.reset_engine()
            self._engines.clear()
            self._engine_tasks.clear()
            self._log("配置已保存", LogLevel.INFO)

    # -------- 连接测试与品种 --------

    async def test_tq_auth(self, params: dict) -> dict:
        account = params.get("account", "")
        password = params.get("password", "")
        if not account:
            raise ValueError("account 不能为空")
        # TqApi 构造是阻塞网络调用，放线程池避免卡住 asyncio 事件循环
        await asyncio.to_thread(test_tq_auth_connection, account, password)
        return {"message": "连接成功，账户密码验证通过"}

    async def test_server(self, params: dict) -> dict:
        server_url = params.get("server_url") or self.bootstrap.config.server_url
        await check_server_health(server_url)
        return {"message": "连接成功"}

    async def fetch_symbols(self, params: dict) -> dict:
        server_url = self.bootstrap.config.server_url
        if not server_url:
            raise ValueError("未配置服务器地址")
        symbols = await fetch_server_symbols(
            server_url, token_service=self.bootstrap.token_service
        )
        return {"symbols": symbols, "message": f"获取成功，共 {len(symbols)} 个品种"}

    # -------- Token --------

    async def token_status(self, params: dict) -> dict:
        token_service = self.bootstrap.token_service
        data = await token_service.get_token_requests()
        status = "待审核"
        for request in data.get("tokens", data.get("requests", [])):
            if request.get("status") == "approved":
                token = await token_service.load_approved_token(data)
                if token:
                    status = "已通过，token 已加密保存"
                else:
                    status = "已通过，但服务器未返回 token"
                break
        return {"status": status}

    async def request_token(self, params: dict) -> dict:
        await self.bootstrap.token_service.request_token(
            params.get("description") or "iTrader 智能交易系统"
        )
        return {"status": "已提交申请，等待服务器审核"}

    # -------- 引擎 --------

    def _log(self, message: str, level: LogLevel = LogLevel.INFO) -> None:
        self.event_bus.publish(LogEvent(message=message, level=level))

    def _enabled_account_ids(self) -> list[str]:
        return [a.id for a in self.bootstrap.accounts if a.enabled]

    def set_auto_trade(self, params: dict) -> dict:
        value = bool(params.get("value"))
        for engine in self._engines.values():
            engine.set_auto_trade(value)
        self._auto_trade = value
        return {}

    async def engine_start(self, params: dict) -> dict:
        """开启自动交易：先探测服务器连通性，未连接则拒绝启动。"""
        try:
            await check_server_health(self.bootstrap.config.server_url)
        except Exception as e:
            raise RuntimeError(f"服务器未连接: {e}") from e
        self.set_auto_trade({"value": True})
        await self._start_engines()
        return self.engine_status()

    async def engine_stop(self, params: dict) -> dict:
        self.set_auto_trade({"value": False})
        await self.stop_engines()
        return self.engine_status()

    async def _start_engines(self) -> None:
        """启动全部已启用账户的引擎。单个账户失败不影响其余账户。"""
        started = False
        for account_id in self._enabled_account_ids():
            if account_id in self._engines and self._engines[account_id].is_running:
                continue
            try:
                engine = await self.bootstrap.build_engine(account_id)
                engine.set_auto_trade(self._auto_trade)
                self._engines[account_id] = engine
                self._engine_tasks[account_id] = asyncio.create_task(engine.start())
                started = True
            except Exception as e:
                self._log(f"启动账户 {account_id} 失败: {e}", LogLevel.ERROR)
        if started:
            self._log("自动交易已开启", LogLevel.SUCCESS)

    async def stop_engines(self) -> None:
        """停止全部账户引擎。执行器由 shutdown 统一关闭。"""
        stopped = False
        for account_id, engine in list(self._engines.items()):
            try:
                await engine.stop()
                task = self._engine_tasks.get(account_id)
                if task is not None and not task.done():
                    try:
                        await asyncio.wait_for(task, timeout=5)
                    except Exception:
                        pass
                stopped = True
            except Exception as e:
                self._log(f"停止账户 {account_id} 失败: {e}", LogLevel.ERROR)
            finally:
                self._engines.pop(account_id, None)
                self._engine_tasks.pop(account_id, None)
        if stopped:
            self._log("自动交易已停止", LogLevel.INFO)

    # -------- 自动更新 --------

    async def update_check(self, params: dict) -> dict:
        silent = bool(params.get("silent"))
        try:
            release = await fetch_latest_release()
        except Exception as e:
            if not silent:
                raise RuntimeError(f"检查更新失败: {e}") from e
            return {"status": "error", "message": str(e)}
        if release is None:
            if not silent:
                raise RuntimeError("未找到可用的更新包")
            return {"status": "none", "message": "未找到可用的更新包"}
        if not is_newer(release.version, __version__):
            return {"status": "latest", "message": f"已是最新版本（v{__version__}）"}
        # 静默检查尊重「跳过此版本」；手动检查不受影响
        if silent and release.version == self.bootstrap.config.skipped_version:
            return {"status": "skipped", "message": f"已跳过版本 v{release.version}"}
        self._release = release
        return {
            "status": "available",
            "message": f"发现新版本 v{release.version}",
            "version": release.version,
            "has_patch": release.has_patch,
        }

    async def update_download(self, params: dict) -> dict:
        release = self._release
        if release is None:
            raise ValueError("请先检查更新")
        if self._update_task is not None and not self._update_task.done():
            raise RuntimeError("已有下载任务进行中")
        # 有补丁包时差量更新，否则回退全量
        self._update_kind = "patch" if release.has_patch else "full"
        url = release.patch_url if release.has_patch else release.full_url
        self._update_task = asyncio.create_task(self._download_update(url))
        return {"status": "downloading"}

    async def _download_update(self, url: str) -> None:
        stage_dir = self.workdir / "data" / "update"
        last_pct = -1
        try:
            def progress(received: int, total: int):
                nonlocal last_pct
                if total <= 0:
                    self._broadcast("update_progress", {"pct": -1, "received": received, "total": total})
                    return
                pct = min(100, int(received * 100 / total))
                if pct != last_pct:
                    last_pct = pct
                    self._broadcast("update_progress", {"pct": pct, "received": received, "total": total})

            zip_path = await download_release(url, stage_dir, progress_cb=progress)
            self._staged_item = await asyncio.to_thread(extract_update, zip_path, stage_dir)
        except asyncio.CancelledError:
            self._clear_stage_dir()
            self._broadcast("update_cancelled", {})
            raise
        except Exception as e:
            self._clear_stage_dir()
            self._broadcast("update_failed", {"message": f"下载更新失败: {e}"})
            return
        self._broadcast(
            "update_ready",
            {"kind": "差量更新包" if self._update_kind == "patch" else "完整更新包"},
        )

    def update_cancel(self, params: dict) -> dict:
        if self._update_task is not None:
            self._update_task.cancel()
        return {}

    async def update_apply(self, params: dict) -> dict:
        """先停引擎释放连接，再替换安装物并启动新进程，最后退出后端。"""
        if self._staged_item is None:
            raise ValueError("没有已下载的更新包")
        await self.stop_engines()
        await self.bootstrap.shutdown()
        try:
            apply_update_and_restart(self._staged_item, self._update_kind)
        except Exception as e:
            raise RuntimeError(f"安装更新失败: {e}") from e
        self._broadcast("backend_stopping", {"reason": "update"})
        self.exit_requested.set()
        return {}

    def _clear_stage_dir(self):
        # 暂存目录只存可重新下载的更新包，清理失败仅占磁盘，不影响功能
        shutil.rmtree(self.workdir / "data" / "update", ignore_errors=True)
        self._staged_item = None

    # -------- 退出 --------

    async def quit(self, params: dict) -> dict:
        await self.shutdown()
        self._broadcast("backend_stopping", {"reason": "quit"})
        self.exit_requested.set()
        return {}

    # -------- 事件桥 --------

    def attach_broadcast(self, fn) -> None:
        """注入广播函数 fn(event_name: str, data: dict)。"""
        self._broadcast_fn = fn

    def _broadcast(self, event_name: str, data: dict) -> None:
        if self._broadcast_fn is not None:
            self._broadcast_fn(event_name, data)

    def _subscribe_events(self) -> None:
        self.event_bus.subscribe(LogEvent, self._on_event(LogEvent, "log"))
        self.event_bus.subscribe(StatusChangedEvent, self._on_event(StatusChangedEvent, "status"))
        self.event_bus.subscribe(TradingStartedEvent, self._on_event(TradingStartedEvent, "trading_started"))
        self.event_bus.subscribe(TradingStoppedEvent, self._on_event(TradingStoppedEvent, "trading_stopped"))
        self.event_bus.subscribe(SignalReceivedEvent, self._on_event(SignalReceivedEvent, "signal_received"))
        self.event_bus.subscribe(TradeExecutedEvent, self._on_event(TradeExecutedEvent, "trade_executed"))
        self.event_bus.subscribe(TradeFailedEvent, self._on_event(TradeFailedEvent, "trade_failed"))
        self.event_bus.subscribe(ConfigChangedEvent, self._on_event(ConfigChangedEvent, "config_changed"))

    def _on_event(self, event_type: type, name: str):
        def handler(event) -> None:
            data = _dataclass_dict(event) if event.__dataclass_fields__ else {}
            if isinstance(event, SignalReceivedEvent):
                data = {"signal": _dataclass_dict(event.signal)}
            self._broadcast(name, data)
        return handler


# RPC 方法注册表：method 名 → 协程（params dict 为唯一入参）。
# 纯同步方法在此包成协程，rpc 层无需区分。
def build_routes(service: BackendService) -> dict:
    async def accounts_list(params: dict) -> dict:
        return {"accounts": service.accounts_list()}

    async def engine_status(params: dict) -> dict:
        return service.engine_status()

    async def set_auto_trade(params: dict) -> dict:
        return service.set_auto_trade(params)

    async def update_cancel(params: dict) -> dict:
        return service.update_cancel(params)

    return {
        "app.status": service.app_status,
        "config.save": service.save_config,
        "accounts.list": accounts_list,
        "accounts.save": service.save_account,
        "accounts.delete": service.delete_account,
        "accounts.test_tq_auth": service.test_tq_auth,
        "server.test": service.test_server,
        "symbols.fetch": service.fetch_symbols,
        "tokens.status": service.token_status,
        "tokens.request": service.request_token,
        "engine.start": service.engine_start,
        "engine.stop": service.engine_stop,
        "engine.set_auto_trade": set_auto_trade,
        "engine.status": engine_status,
        "update.check": service.update_check,
        "update.download": service.update_download,
        "update.cancel": update_cancel,
        "update.apply": service.update_apply,
        "app.quit": service.quit,
    }
