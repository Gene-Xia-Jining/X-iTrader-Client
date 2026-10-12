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
from ..infrastructure.trading.tqsdk_executor import TqSdkTradingExecutor
from .trading_engine import TradingEngine, TradingEngineDeps

def load_or_generate_client_id() -> str:
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
    def token_service(self) -> TokenApiService:
        if self._token_service is None:
            self._token_service = TokenApiService(
                server_url=self.config.server_url,
                token_store=self.token_store,
                client_id=load_or_generate_client_id(),
            )
        return self._token_service

    async def init_db(self) -> SQLiteTradeCommandRepository:
        if self._trade_repo is None:
            self._trade_repo = SQLiteTradeCommandRepository(self.db_path)
            await self._trade_repo.init()
        return self._trade_repo

    def build_trading_executor(self) -> TqSdkTradingExecutor:
        """惰性创建执行器：每个账户独立 TqApi 会话，共享一个执行器实例。

        账户记录（含 broker）来自 accounts 表，快期权限凭据来自每个账户的
        tq_account/tq_password。
        """
        if self._trading_executor is None:
            accounts = self.accounts
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
            self._engines[account_id] = TradingEngine(
                deps=TradingEngineDeps(
                    config=self.config,
                    account_id=account_id,
                    trade_repo=trade_repo,
                    stream_client=self.build_stream_client(account),
                    trading_executor=self.build_trading_executor(),
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
