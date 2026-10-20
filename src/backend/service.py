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
from ..infrastructure.api.client import (
    check_server_health,
    describe_server_error,
    fetch_server_strategies,
    fetch_server_symbols,
)
from ..infrastructure.proxy import apply_proxy_environment, server_host
from ..infrastructure.trading.tqsdk_executor import test_account_session, test_tq_auth_connection
from ..infrastructure.trading.tqsdk_records import fetch_account_records
from ..infrastructure.updater import (
    ReleaseInfo,
    apply_update_and_restart,
    cleanup_stale_backups,
    download_release,
    extract_update,
    fetch_latest_release,
    is_newer,
)


class EngineStartBlockedError(RuntimeError):
    """自动交易启动被预检拦截。message 为分号连接的问题摘要，
    issues 字段保留结构化问题列表供 UI 展示。"""

    def __init__(self, issues: list[str]):
        self.issues = list(issues)
        super().__init__("；".join(self.issues) if self.issues else "启动被拦截")

    def to_payload(self) -> dict:
        return {"data": {"issues": self.issues}}


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
        # 最近一次探测失败的原因（在线时为空串），随 status 事件与 engine.status 下发
        self._server_error = ""
        self._server_watch_task: Optional[asyncio.Task] = None
        # save_config 置位后心跳立即探测一次（新地址即时生效，不等下个周期）
        self._server_check_now = asyncio.Event()
        # token 领取：无本地 token 时周期轮询服务器，已批准则领取并加密落库
        self._token_watch_task: Optional[asyncio.Task] = None
        # 申请 token 后置位立即查一次，不等下个轮询周期
        self._token_check_now = asyncio.Event()
        # 已有本地 token 只需校验一次，避免每轮轮询都打服务器
        self._token_validated = False
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
        self._token_watch_task = asyncio.create_task(self._token_watch_loop())

    async def shutdown(self) -> None:
        await self.stop_engines()
        for task in (self._server_watch_task, self._token_watch_task):
            if task is None:
                continue
            task.cancel()
            try:
                await task
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
        error = ""
        if server_url:
            try:
                await check_server_health(server_url)
                online = True
            except Exception as e:
                # 失败原因留给界面展示：只有布尔值的话用户只能看到「未连接」，
                # 无法区分地址写错、网络不通还是服务端 5xx
                error = describe_server_error(e)
        if online != self._server_online or error != self._server_error:
            was_online = self._server_online
            self._server_online = online
            self._server_error = error
            if server_url and online != was_online:
                self._log(
                    "服务器已连接" if online else f"服务器连接断开: {server_url}（{error}）",
                    LogLevel.INFO if online else LogLevel.WARNING,
                )
            self._broadcast(
                "status",
                {"server_online": online, "server_error": error},
            )

    # -------- Token 领取 --------

    async def _token_watch_loop(self) -> None:
        """周期尝试领取已批准的 token，与心跳同构的常驻任务。"""
        while True:
            try:
                await self._watch_token_once()
            except asyncio.CancelledError:
                raise
            except Exception as e:
                self._log(f"Token 轮询异常: {e}", LogLevel.WARNING)
            try:
                await asyncio.wait_for(self._token_check_now.wait(), timeout=5)
            except asyncio.TimeoutError:
                pass
            self._token_check_now.clear()

    async def _watch_token_once(self) -> None:
        """本地无 token 时尝试领取；领到即广播通知界面刷新。

        本地已有 token 时只做一次启动校验（无效则清除，交给后续轮询重领），
        之后不再打断，避免每轮都打服务器。
        """
        token_service = self.bootstrap.token_service
        store = token_service.token_store

        if store.have_token():
            if self._token_validated:
                return
            self._token_validated = True
            try:
                valid = await token_service.validate_token(store.load_token())
            except Exception:
                # 网络异常不代表 token 失效，保留本地值，下次启动再校验
                return
            if valid:
                return
            store.delete_token()
            self._log("本地 token 已失效，将重新领取", LogLevel.WARNING)
            return

        # 服务器不可达时静默跳过，交给心跳恢复后再领（避免每轮刷失败日志）
        if not self._server_online:
            return

        if not await self._claim_and_save():
            return
        self._log("Token 审批已通过，已自动领取并保存", LogLevel.SUCCESS)
        payload = await self._token_status_payload(try_claim=False)
        self._broadcast("token_status", payload)

    async def _claim_and_save(self, token_id: Optional[str] = None, strict: bool = False) -> bool:
        """领取已批准 token 并加密落库；无待领取记录返回 False。

        token_id 为空时领取最新一条待领取记录（启动时的自动领取），
        指定时只领取该条（用户在记录列表里切换当前使用的 Token）。
        strict=True 时网络异常向上抛，避免把「连不上服务器」说成
        「记录没有可领取的明文」。
        """
        if not self.bootstrap.config.server_url:
            return False
        token_service = self.bootstrap.token_service
        try:
            result = await token_service.claim_token(token_id)
        except Exception as e:
            self._log(f"Token 领取失败: {e}", LogLevel.WARNING)
            if strict:
                raise
            return False
        if not result or not result.get("token"):
            return False
        token_service.token_store.save_token(result["token"], result.get("token_id"))
        return True

    async def _token_status_payload(self, try_claim: bool = True) -> dict:
        """归纳审批状态文案并附带申请记录；已通过但无本地 token 时就地领取一次。"""
        token_service = self.bootstrap.token_service
        if not self.bootstrap.config.server_url:
            return self._payload("未配置服务器", [])

        try:
            records = await token_service.list_token_requests()
        except Exception as e:
            return self._payload(f"查询失败: {e}", [])

        if token_service.token_store.have_token():
            return self._payload("已通过，token 已加密保存", records)
        if not records:
            return self._payload("未申请", records)

        # 记录按 created_at 倒序，首条即最新一次申请
        state = records[0].get("status") or "pending"
        if state == "pending":
            return self._payload("待审核", records)
        if state == "rejected":
            return self._payload("已拒绝", records)

        if try_claim and await self._claim_and_save():
            self._log("Token 审批已通过，已自动领取并保存", LogLevel.SUCCESS)
            return self._payload("已通过，token 已加密保存", records)
        return self._payload("已通过，但服务器未返回 token", records)

    def _payload(self, status: str, records: list[dict]) -> dict:
        """状态文案 + 记录 + 当前生效记录 id + 本地有缓存的记录 id。

        后两项供界面标出「使用中」并判断哪些行可以切换。
        """
        store = self.bootstrap.token_service.token_store
        return {
            "status": status,
            "records": records,
            "active_token_id": store.load_token_id(),
            "cached_token_ids": sorted(store.cached_token_ids()),
        }

    async def switch_token(self, params: dict) -> dict:
        """把指定申请记录的 Token 设为当前使用。

        服务器对已签发 Token 只保存 hash，明文仅在「批准 → 领取」窗口内
        存在一次。因此优先用本地缓存（曾经用过的那份明文），缓存里没有时
        再向服务器领取；两者都不行才报错，而不是让界面显示切换成功。
        """
        token_id = (params.get("token_id") or "").strip()
        if not token_id:
            raise ValueError("缺少 token_id")
        if not self.bootstrap.config.server_url:
            raise ValueError("未配置服务器地址")

        token_service = self.bootstrap.token_service
        store = token_service.token_store
        if store.load_token_id() == token_id and store.have_token():
            raise ValueError("该记录已是当前使用的 Token")

        if store.activate_cached_token(token_id):
            self._log("已切换 Token（使用本地缓存凭据）", LogLevel.INFO)
        elif not await self._claim_and_save(token_id, strict=True):
            raise ValueError(
                "该记录没有可用的明文 Token（首次领取后服务器不再保留，"
                "本地也没有使用记录），请在服务器端重新批准后再切换"
            )

        # 新 token 已落库：重置校验标记，让下一次心跳按新凭据重新校验
        self._token_validated = False
        await self._reset_engines_after_token_change()
        self._log(f"已切换 Token（记录 {token_id[:8]}…）", LogLevel.SUCCESS)
        payload = await self._token_status_payload(try_claim=False)
        self._broadcast("token_status", payload)
        return payload

    async def _reset_engines_after_token_change(self) -> None:
        """切换凭据后停掉并重建引擎：信号流的请求头在建立时固定了旧 token，
        不重建就会一直用旧凭据（服务器 401 后也不会自愈）。停引擎走
        engine_stop 的同一路径，避免界面仍显示自动交易运行中。"""
        if not self._engines:
            return
        self.set_auto_trade({"value": False})
        await self.stop_engines()
        self.bootstrap.reset_stream_clients()
        self._log("Token 已变更，自动交易已停止，请重新开启", LogLevel.WARNING)

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
        # 附带「是否已设置密码」与密码长度，供客户端在密码框按真实长度显示掩码圆点
        result = []
        for account in self.bootstrap.accounts:
            item = _dataclass_dict(account)
            item["has_tq_password"] = bool(account.tq_password)
            item["has_trade_password"] = bool(account.trade_password)
            item["tq_password_length"] = len(account.tq_password)
            item["trade_password_length"] = len(account.trade_password)
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
            "server_error": self._server_error,
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
            skipped_version=params.get("skipped_version", current.skipped_version),
            database_path=current.database_path,
        )
        self.bootstrap.save_config(new_config)
        self._apply_proxy_env(new_config)
        self._server_check_now.set()  # 新地址立即探测，不等下个心跳周期
        # token 客户端与校验结果都绑定旧地址，换地址后重建/重校验
        self.bootstrap.reset_token_service()
        self._token_validated = False
        self._token_check_now.set()
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
        strategy_raw = params.get("strategy")
        strategy = strategy_raw.strip() if isinstance(strategy_raw, str) else None

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
            strategy=strategy if strategy is not None else (existing.strategy if existing else ""),
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

    async def trading_records(self, params: dict) -> dict:
        """查询账户当日交易数据：资金、持仓、委托、成交。

        kind 用于「交易记录」页的模拟/实盘 tab；指定 account_id 时以它为准。
        同时可能有多个实盘账户，故返回 accounts 数组而不是单个账户。
        每个账户读各自的快期模拟/实盘账户（TqKq / TqAccount），与引擎的账户选择一致。
        """
        kind = params.get("kind")
        account_id = (params.get("account_id") or "").strip()
        if kind is not None and kind not in ("sim", "live"):
            raise ValueError("kind 必须是 sim 或 live")

        accounts = list(self.bootstrap.accounts)
        if account_id:
            accounts = [a for a in accounts if a.id == account_id]
            if not accounts:
                raise ValueError(f"未知账户: {account_id}")
        elif kind:
            accounts = [a for a in accounts if a.kind == kind]

        if not accounts:
            hint = "模拟" if kind == "sim" else "实盘" if kind == "live" else ""
            raise ValueError(f"尚未配置{hint}账户")

        # TqApi 构造与取数是阻塞网络调用，放线程池避免卡住 asyncio 事件循环；
        # 单账户失败不阻断其余账户，按账户分别回传错误
        results = await asyncio.gather(
            *(
                asyncio.to_thread(
                    fetch_account_records,
                    acc,
                    (acc.tq_account, acc.tq_password),
                )
                for acc in accounts
            ),
            return_exceptions=True,
        )

        records: list[dict] = []
        for acc, result in zip(accounts, results):
            if isinstance(result, BaseException):
                records.append(
                    {
                        "account_id": acc.id,
                        "kind": acc.kind,
                        "label": acc.label or acc.id,
                        "error": str(result),
                        "account": {},
                        "positions": [],
                        "orders": [],
                        "trades": [],
                    }
                )
            else:
                records.append(result)
        return {"accounts": records}

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
        if not password:
            account_id = params.get("account_id")
            existing = self.bootstrap.config_service.get_account(account_id) if account_id else None
            if not existing:
                existing = next((a for a in self.bootstrap.accounts if a.tq_account == account), None)
            if existing and existing.tq_password:
                password = existing.tq_password
        if not password:
            raise ValueError("密码不能为空")
        # TqApi 构造是阻塞网络调用，放线程池避免卡住 asyncio 事件循环
        await asyncio.to_thread(test_tq_auth_connection, account, password)
        return {"message": "连接成功，账户密码验证通过"}

    async def get_account_password(self, params: dict) -> dict:
        """获取指定账户的保存密码，供客户端在用户点击明文查看时受控读取。"""
        account_id = params.get("account_id")
        kind = params.get("kind")
        field = params.get("field", "tq_password")  # "tq_password" 或 "trade_password"
        if not account_id and not kind:
            raise ValueError("必须提供 account_id 或 kind")

        target = None
        if account_id:
            target = self.bootstrap.config_service.get_account(account_id)
        if not target and kind:
            target = next((a for a in self.bootstrap.accounts if a.kind == kind), None)

        if not target:
            return {"password": ""}

        val = getattr(target, field, "") if hasattr(target, field) else ""
        return {"password": val or ""}

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

    async def fetch_strategies(self, params: dict) -> dict:
        server_url = self.bootstrap.config.server_url
        if not server_url:
            raise ValueError("未配置服务器地址")
        strategies = await fetch_server_strategies(
            server_url, token_service=self.bootstrap.token_service
        )
        return {"strategies": strategies, "message": f"获取成功，共 {len(strategies)} 个策略"}

    # -------- Token --------

    async def token_status(self, params: dict) -> dict:
        """审批状态文案 + 该客户端的申请记录列表（供 UI 展示）。"""
        return await self._token_status_payload()

    async def request_token(self, params: dict) -> dict:
        await self.bootstrap.token_service.request_token(
            params.get("description") or "iTrader 智能交易系统"
        )
        # 立即盯一次审批，不等下个轮询周期
        self._token_check_now.set()
        return {"status": "已提交申请，等待服务器审核"}

    async def delete_token_request(self, params: dict) -> dict:
        """删除一条申请记录并回传刷新后的状态与记录列表。

        删掉的若是当前生效记录，服务器会立即判定本地 token 失效；这里顺手
        校验一次并清掉本地凭据，避免自动交易预检仍显示「已领取」。
        """
        token_id = (params.get("token_id") or "").strip()
        if not token_id:
            raise ValueError("缺少 token_id")
        if not self.bootstrap.config.server_url:
            raise ValueError("未配置服务器地址")
        token_service = self.bootstrap.token_service
        await token_service.delete_token_request(token_id)

        store = token_service.token_store
        store.delete_cached_token(token_id)
        if store.load_token_id() == token_id:
            # 删掉的正是当前生效记录：本地凭据已无对应记录，直接作废
            store.delete_token()
            self._token_validated = False
            self._log("当前使用的 Token 记录已被删除，本地凭据已作废", LogLevel.WARNING)
        elif store.have_token():
            try:
                valid = await token_service.validate_token(store.load_token())
            except Exception:
                # 网络异常不代表 token 失效，保留本地值，交给下次启动校验
                valid = True
            if not valid:
                store.delete_token()
                self._token_validated = False
                self._log("本地 token 已失效（对应申请记录已删除）", LogLevel.WARNING)

        payload = await self._token_status_payload()
        self._broadcast("token_status", payload)
        return payload

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
        """开启自动交易：先做启动前预检，任一检查不过则拒绝启动并返回问题列表。

        预检项：
        - 服务器连通（/api/health）；
        - 已启用账户非空，且每个账户的品种、策略、实盘资金账号等关键字段已填；
        - 本地 API token 已领取（否则 SSE 信号流会被服务器 401 拒绝）；
        - 每个启用账户的快期权限账户能完成 TqApi 握手（含账户权限校验）。

        预检全过后才置 auto_trade=True 并启动引擎；启动阶段若仍有账户失败，
        同样回滚（停掉已启动的引擎、不置 auto_trade）并抛出问题列表，避免
        出现「按钮已显示开启但实际半数账户未起」的不一致状态。
        """
        issues = await self._preflight_start_checks()
        if issues:
            # 拼成多行文案便于日志；结构化 issues 经异常 type 字段回传 UI
            raise EngineStartBlockedError(issues)
        # 预检通过后启动；启动期失败也按问题列表抛出，由本方法回滚
        try:
            self.set_auto_trade({"value": True})
            await self._start_engines()
        except EngineStartBlockedError:
            await self.stop_engines()
            self.set_auto_trade({"value": False})
            raise
        except Exception as e:
            # 启动期非预期异常：同样回滚，避免半启动状态
            await self.stop_engines()
            self.set_auto_trade({"value": False})
            raise EngineStartBlockedError([f"启动过程发生未预期错误: {e}"]) from e
        return self.engine_status()

    async def _preflight_start_checks(self) -> list[str]:
        """启动前预检，返回人类可读的问题列表（空列表 = 通过）。

        单个账户的 TqApi 握手是阻塞网络调用，放线程池并发执行；任一账户
        失败记一条问题，不阻断其余账户的检查（让用户一次看到全部问题）。
        """
        issues: list[str] = []
        config = self.bootstrap.config

        # 1) 服务器连通
        try:
            await check_server_health(config.server_url)
        except Exception as e:
            issues.append(f"服务器未连接: {e}")

        # 2) 已启用账户与关键字段
        enabled_accounts = [a for a in self.bootstrap.accounts if a.enabled]
        if not enabled_accounts:
            issues.append("没有已启用的账户，请先在模拟/实盘页启用至少一个账户")
        for acc in enabled_accounts:
            label = acc.label or acc.id
            if not acc.tq_account:
                issues.append(f"账户「{label}」未配置快期账号")
            if not acc.tq_password:
                issues.append(f"账户「{label}」未配置快期密码")
            if not acc.symbols:
                issues.append(f"账户「{label}」未选择交易品种")
            # 策略决定订阅哪一路信号，未选时服务器会按默认策略推送，与用户预期不一致
            if not acc.strategy:
                issues.append(f"账户「{label}」未选择交易策略")
            if acc.kind == "live":
                if not acc.broker:
                    issues.append(f"实盘账户「{label}」未选择期货公司")
                if not acc.trade_account:
                    issues.append(f"实盘账户「{label}」未填写资金账号")
                if not acc.trade_password:
                    issues.append(f"实盘账户「{label}」未填写交易密码")

        # 3) 本地 API token（SSE 信号流需要）
        token_service = self.bootstrap.token_service
        if not token_service.token_store.have_token():
            issues.append("本地 API token 未领取，请在 Token 管理页申请并等待服务器审批")

        # 4) 天勤账户握手（字段齐全才试，避免重复报密码错误）
        to_probe = [
            acc for acc in enabled_accounts
            if acc.tq_account and acc.tq_password
        ]
        if to_probe:
            results = await asyncio.gather(
                *(
                    asyncio.to_thread(
                        test_account_session,
                        acc,
                        (acc.tq_account, acc.tq_password),
                    )
                    for acc in to_probe
                ),
                return_exceptions=True,
            )
            for acc, exc in zip(to_probe, results):
                if isinstance(exc, Exception):
                    label = acc.label or acc.id
                    issues.append(f"账户「{label}」天勤连接失败: {exc}")

        return issues

    async def engine_stop(self, params: dict) -> dict:
        self.set_auto_trade({"value": False})
        await self.stop_engines()
        return self.engine_status()

    async def _start_engines(self) -> None:
        """启动全部已启用账户的引擎。

        预检已过，但 build_engine 仍可能在 tqsdk 会话构造阶段失败（预检握手
        与正式会话是两次独立连接，偶发网络问题仍可能在此冒出）。任一账户
        失败视为启动不完整，抛 EngineStartBlockedError 让 engine_start 回滚。
        """
        started_ids: list[str] = []
        failures: list[str] = []
        # 与预检一致用 label 展示（无 label 时回落到 id），避免弹窗里
        # 同一账户在两条问题中出现两种称呼
        labels = {a.id: (a.label or a.id) for a in self.bootstrap.accounts}
        for account_id in self._enabled_account_ids():
            if account_id in self._engines and self._engines[account_id].is_running:
                continue
            try:
                engine = await self.bootstrap.build_engine(account_id)
                engine.set_auto_trade(self._auto_trade)
                self._engines[account_id] = engine
                self._engine_tasks[account_id] = asyncio.create_task(engine.start())
                started_ids.append(account_id)
            except Exception as e:
                failures.append(f"账户「{labels.get(account_id, account_id)}」启动失败: {e}")
        if failures:
            # 已启动的引擎交由 engine_stop 回滚，这里只汇总问题抛出
            raise EngineStartBlockedError(failures)
        if started_ids:
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
            return {"status": "latest", "message": f"已是最新版本（{__version__}）"}
        # 静默检查尊重「跳过此版本」；手动检查不受影响
        if silent and release.version == self.bootstrap.config.skipped_version:
            return {"status": "skipped", "message": f"已跳过版本 {release.version}"}
        self._release = release
        return {
            "status": "available",
            "message": f"发现新版本 {release.version}",
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

            archive_path = await download_release(url, stage_dir, progress_cb=progress)
            self._staged_item = await asyncio.to_thread(extract_update, archive_path, stage_dir)
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
            {
                "kind": "差量更新包" if self._update_kind == "patch" else "完整更新包",
                # 机器可读的包类型：壳据此决定安装后是重连（patch）还是退出（full）
                "mode": self._update_kind,
            },
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
        "accounts.get_password": service.get_account_password,
        "accounts.test_tq_auth": service.test_tq_auth,
        "server.test": service.test_server,
        "symbols.fetch": service.fetch_symbols,
        "strategies.fetch": service.fetch_strategies,
        "tokens.status": service.token_status,
        "tokens.request": service.request_token,
        "tokens.delete": service.delete_token_request,
        "tokens.switch": service.switch_token,
        "engine.start": service.engine_start,
        "engine.stop": service.engine_stop,
        "engine.set_auto_trade": set_auto_trade,
        "engine.status": engine_status,
        "trading.records": service.trading_records,
        "update.check": service.update_check,
        "update.download": service.update_download,
        "update.cancel": update_cancel,
        "update.apply": service.update_apply,
        "app.quit": service.quit,
    }
