import uuid
from pathlib import Path
from typing import Optional

from ..domain.entities import TradingConfiguration
from ..domain.events import EventBus
from ..infrastructure.api.client import (
    ServerStreamClient,
    TokenApiService,
)
from ..infrastructure.api.db_token_store import DbTokenStore
from ..infrastructure.config.config_service import ConfigService
from ..infrastructure.db.repositories import SQLiteTradeCommandRepository
from ..infrastructure.security.device_identity import (
    DeviceIdentityStore,
    load_or_create_device_id,
    read_legacy_client_id,
)
from ..infrastructure.trading.tqsdk_executor import TqSdkTradingExecutor
from .trading_engine import TradingEngine, TradingEngineDeps

def load_or_generate_client_id() -> str:
    """客户端自报标识（服务器无法验证，仅作展示与历史 token 归属）。

    身份体系上线后，真正的身份是 device_id + device_key（见
    infrastructure.security.device_identity）；这里保留原语义以兼容旧数据。
    """
    id_path = Path("data/client_id.txt")
    if id_path.exists():
        return id_path.read_text(encoding="utf-8").strip()
    client_id = f"client-{uuid.uuid4().hex[:8]}"
    id_path.parent.mkdir(parents=True, exist_ok=True)
    id_path.write_text(client_id, encoding="utf-8")
    return client_id

class Bootstrap:
    def __init__(
        self,
        db_path: str = "data/client.db",
    ):
        self.db_path = db_path
        self.config_service = ConfigService(db_path)
        self.event_bus = EventBus()
        self._config: Optional[TradingConfiguration] = None
        self._trade_repo: Optional[SQLiteTradeCommandRepository] = None
        # 执行器单例：一个实例管理全部账户（每账户独立 TqApi 会话）
        self._trading_executor: Optional[TqSdkTradingExecutor] = None
        # 流客户端与引擎按账户各建一个：品种是账户属性，各自订阅自己的品种
        self._stream_clients: dict[str, ServerStreamClient] = {}
        self._engines: dict[str, TradingEngine] = {}
        self._token_store: Optional[DbTokenStore] = None
        self._token_service: Optional[TokenApiService] = None
        self._device_store: Optional[DeviceIdentityStore] = None

    @property
    def config(self) -> TradingConfiguration:
        if self._config is None:
            self._config = self.config_service.load()
        return self._config

    def save_config(self, config: TradingConfiguration) -> None:
        self._config = config
        self.config_service.save(config)

    @property
    def accounts(self) -> list:
        return self.config_service.load_accounts()

    def save_account(self, account) -> None:
        self.config_service.save_account(account)

    def delete_account(self, account_id: str) -> None:
        self.config_service.delete_account(account_id)

    @property
    def token_store(self) -> DbTokenStore:
        if self._token_store is None:
            self._token_store = DbTokenStore(self.db_path)
        return self._token_store

    @property
    def device_identity_store(self) -> DeviceIdentityStore:
        if self._device_store is None:
            self._device_store = DeviceIdentityStore(self.db_path)
        return self._device_store

    @property
    def token_service(self) -> TokenApiService:
        if self._token_service is None:
            self._token_service = TokenApiService(
                server_url=self.config.server_url,
                token_store=self.token_store,
                client_id=load_or_generate_client_id(),
                device_identity_store=self.device_identity_store,
            )
            # 已有登记结果就直接用（不重复登记）；首次由 ensure_device_registered 完成
            device_id, device_key = self.device_identity_store.load()
            if device_id and device_key:
                self._token_service.set_device_identity(device_id, device_key)
        return self._token_service

    async def ensure_device_registered(self) -> bool:
        """确保本机已向服务器登记，返回是否可用 device 身份。

        幂等且容错：已登记直接返回 True；旧服务器（无此接口）或网络失败都
        返回 False，调用方继续走纯 client_id 的旧路径，绝不阻断启动。
        """
        store = self.device_identity_store
        device_id, device_key = store.load()
        device_id = device_id or load_or_create_device_id()

        service = self.token_service
        try:
            payload = await service.register_device(
                device_id, legacy_client_id=read_legacy_client_id()
            )
        except Exception as exc:
            # 登记失败不阻断交易主链路：token 申请会退回纯 client_id 路径
            import logging
            logging.getLogger(__name__).warning(
                "device_register_failed device_id=%s error=%s", device_id, exc
            )
            if device_key:
                service.set_device_identity(device_id, device_key)
            return False

        if payload is None:
            # 旧服务器没有身份体系
            return False
        return bool(payload.get("device_key") or device_key)

    async def recover_device_credentials(self) -> bool:
        """设备凭据被服务端拒绝后重新握手，返回是否拿到新的可用凭据。

        只处理**可恢复**的情形（设备被撤销 / 服务端不认识这台设备）：
        清掉本地旧密钥后重新登记，服务端对已撤销设备会换发新密钥。

        不可恢复的情形（密钥不匹配等）必须原地报错，绝不能清凭据重登记——
        那会把「本地数据坏了」变成「静默换一台新身份」，用户的历史记录
        就此失联，而且每次失败都会再覆盖一次。
        """
        import logging

        logger = logging.getLogger(__name__)
        store = self.device_identity_store
        device_id, old_key = store.load()
        device_id = device_id or load_or_create_device_id()

        # 清掉旧密钥：register_device 对「已登记且未撤销」的设备不重发密钥，
        # 若不清掉，拿到 None 后又会把旧密钥塞回去，等于白跑一趟。
        store.save(device_id, None)
        self.token_service.set_device_identity(device_id, None)

        ok = await self.ensure_device_registered()
        if ok:
            logger.info("device_credentials_recovered device_id=%s", device_id)
        else:
            logger.warning(
                "device_credentials_recover_failed device_id=%s had_key=%s",
                device_id, bool(old_key),
            )
        return ok

    async def init_db(self) -> SQLiteTradeCommandRepository:
        if self._trade_repo is None:
            self._trade_repo = SQLiteTradeCommandRepository(self.db_path)
            await self._trade_repo.init()
        return self._trade_repo

    def build_trading_executor(self) -> TqSdkTradingExecutor:
        """惰性创建执行器：每个启用账户独立 TqApi 会话，共享一个执行器实例。

        只纳入 enabled 账户：停用账户不参与交易，其凭据也不应被构造，
        否则一个停用但凭据错误的账户会拖垮整次启动。账户记录（含 broker）
        来自 accounts 表，快期权限凭据来自每个账户的 tq_account/tq_password。
        """
        if self._trading_executor is None:
            accounts = [a for a in self.accounts if a.enabled]
            self._trading_executor = TqSdkTradingExecutor(
                accounts=accounts,
                auths={a.id: (a.tq_account, a.tq_password) for a in accounts},
            )
        return self._trading_executor

    def build_stream_client(self, account) -> ServerStreamClient:
        """每个账户独立订阅：symbols 取账户自己的品种，strategy 取账户所选策略。"""
        if account.id not in self._stream_clients:
            self._stream_clients[account.id] = ServerStreamClient(
                server_url=self.config.server_url,
                symbols=account.symbols,
                token_service=self.token_service,
                strategy=account.strategy,
            )
        return self._stream_clients[account.id]

    async def build_engine(self, account_id: str) -> TradingEngine:
        account = self.config_service.get_account(account_id)
        if account is None:
            raise ValueError(f"未知账户: {account_id}")
        if account_id not in self._engines:
            trade_repo = await self.init_db()
            executor = self.build_trading_executor()
            if hasattr(executor, "ensure_session"):
                executor.ensure_session(account, (account.tq_account, account.tq_password))
            self._engines[account_id] = TradingEngine(
                deps=TradingEngineDeps(
                    config=self.config,
                    account_id=account_id,
                    trade_repo=trade_repo,
                    stream_client=self.build_stream_client(account),
                    trading_executor=executor,
                    event_bus=self.event_bus,
                    auto_trade=self.config.auto_trade,
                )
            )
        return self._engines[account_id]

    async def stop_all(self) -> None:
        """停止全部账户引擎。执行器由 shutdown 统一关闭。"""
        for account_id, engine in list(self._engines.items()):
            await engine.stop()

    async def shutdown(self) -> None:
        """释放全部资源：先停引擎再关执行器（释放 TqApi 连接）。"""
        await self.stop_all()
        if self._trading_executor is not None:
            self._trading_executor.close()
            self._trading_executor = None

    def reset_engine(self) -> None:
        """配置变更后重置引擎与执行器，下次启动重建。"""
        self._engines.clear()
        self._stream_clients.clear()
        if self._trading_executor is not None:
            self._trading_executor.close()
            self._trading_executor = None

    def reset_token_service(self) -> None:
        """服务器地址变更后重建 token 客户端（其 server_url 构造时固定）。"""
        self._token_service = None

    def reset_stream_clients(self) -> None:
        """丢弃已建的信号流客户端：请求头里的 Token 在建立时固定，
        换了凭据必须重建，否则会一直用旧 Token 订阅。"""
        self._stream_clients.clear()
