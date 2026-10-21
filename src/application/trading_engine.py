import asyncio
from dataclasses import dataclass

from ..domain.entities import (
    StrategySignal,
    TradeCommand,
    TradeCommandStatus,
    TradingConfiguration,
)
from ..domain.events import (
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
from ..domain.repositories import (
    StrategyStreamClient,
    TradeCommandRepository,
)
from ..domain.services import TradingExecutor


@dataclass
class TradingEngineDeps:
    config: TradingConfiguration
    account_id: str
    trade_repo: TradeCommandRepository
    stream_client: StrategyStreamClient
    trading_executor: TradingExecutor
    event_bus: EventBus
    auto_trade: bool = False


class TradingEngine:
    def __init__(self, deps: TradingEngineDeps):
        self.config = deps.config
        self.account_id = deps.account_id
        self.trade_repo = deps.trade_repo
        self.stream_client = deps.stream_client
        self.trading_executor = deps.trading_executor
        self.event_bus = deps.event_bus
        self.auto_trade = deps.auto_trade
        self._running = False
        self._server_connected = False

    @property
    def is_running(self) -> bool:
        return self._running

    @property
    def server_connected(self) -> bool:
        return self._server_connected

    def set_auto_trade(self, value: bool) -> None:
        self.auto_trade = value
        self._log(f"自动交易 {'开启' if value else '关闭'}", LogLevel.INFO)

    def _log(self, message: str, level: LogLevel = LogLevel.INFO) -> None:
        self.event_bus.publish(LogEvent(message=message, level=level))

    def _publish_status(self) -> None:
        self.event_bus.publish(
            StatusChangedEvent(
                trading_active=self._running,
                server_connected=self._server_connected,
                account_id=self.account_id,
            )
        )

    async def start(self) -> None:
        if self._running:
            return
        self._running = True
        self.event_bus.publish(TradingStartedEvent(account_id=self.account_id))
        self._log("自动交易已启动", LogLevel.INFO)
        self._publish_status()
        try:
            await self._run_loop()
        finally:
            await self._shutdown()

    async def stop(self) -> None:
        self._running = False

    async def _shutdown(self) -> None:
        self._running = False
        self._server_connected = False
        self.event_bus.publish(TradingStoppedEvent(account_id=self.account_id))
        self._log("自动交易已停止", LogLevel.INFO)
        self._publish_status()

    async def _run_loop(self) -> None:
        while self._running:
            try:
                self._server_connected = True
                self._publish_status()
                self._log(f"连接到服务器: {self.config.server_url}", LogLevel.INFO)
                async for signal in self.stream_client.stream():
                    if not self._running:
                        break
                    await self._handle_signal(signal)
            except Exception as e:
                self._server_connected = False
                self._publish_status()
                if self._running:
                    self._log(f"连接错误: {e}", LogLevel.ERROR)
                    await asyncio.sleep(5)

    async def _handle_signal(self, signal: StrategySignal) -> None:
        self.event_bus.publish(SignalReceivedEvent(signal=signal))
        self._log(
            f"收到策略结果: {signal.symbol} 价位 {signal.price} 仓位 {signal.target_position}",
            LogLevel.INFO,
        )

        existing = await self.trade_repo.get(self.account_id, signal.id)
        if existing is None:
            await self.trade_repo.add(
                TradeCommand(
                    account_id=self.account_id,
                    id=signal.id,
                    symbol=signal.symbol,
                    side=signal.side,
                    price=signal.price,
                    target_position=signal.target_position,
                    received_at=signal.updated_at,
                    status=TradeCommandStatus.PENDING,
                )
            )
        elif existing.status == TradeCommandStatus.EXECUTED:
            self._log(f"已执行过: {signal.id}", LogLevel.WARNING)
            return
        else:
            # 上次失败的（超时、拒单、非交易时段）允许重试：服务器对同一根 K 线
            # 不会重发信号，直接跳过等于永久丢弃这次调仓
            await self.trade_repo.set_status(
                self.account_id, signal.id, TradeCommandStatus.PENDING
            )
            self._log(f"重试上次失败的信号: {signal.id}", LogLevel.INFO)

        if not self.auto_trade:
            self._log("自动交易关闭", LogLevel.INFO)
            return

        try:
            await self.trade_repo.set_status(
                self.account_id, signal.id, TradeCommandStatus.EXECUTING
            )
            await self.trading_executor.set_target_position(
                signal.symbol, signal.target_position, self.account_id
            )
            await self.trade_repo.set_status(
                self.account_id, signal.id, TradeCommandStatus.EXECUTED
            )
            self.event_bus.publish(
                TradeExecutedEvent(
                    symbol=signal.symbol, target_position=signal.target_position
                )
            )
            self._log(
                f"交易执行成功: {signal.symbol} 仓位 {signal.target_position}",
                LogLevel.SUCCESS,
            )
        except Exception as e:
            await self.trade_repo.set_status(
                self.account_id, signal.id, TradeCommandStatus.FAILED, error=str(e)
            )
            self.event_bus.publish(TradeFailedEvent(symbol=signal.symbol, error=str(e)))
            self._log(f"交易执行失败: {e}", LogLevel.ERROR)
