import asyncio
import datetime
from dataclasses import dataclass
from typing import Callable

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
from ..domain.trading_hours import beijing_now, in_trading_period


@dataclass
class TradingEngineDeps:
    config: TradingConfiguration
    account_id: str
    trade_repo: TradeCommandRepository
    stream_client: StrategyStreamClient
    trading_executor: TradingExecutor
    event_bus: EventBus
    auto_trade: bool = False
    # 交易时段门控的时钟；默认北京时间当前时刻，测试可注入固定时刻
    clock: Callable[[], datetime.datetime] = beijing_now


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
        # 交易时段判断用的时钟，测试可替换（生产用北京时间当前时刻）
        self._clock = deps.clock

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

    async def _in_trading_period(self, symbol: str) -> bool:
        """该品种当前是否可交易；拿不到时段表时放行（返回 True）。

        执行器查询是阻塞调用（要进 TqApi 的守护线程），放线程池避免卡住事件循环。
        时钟通过 in_trading_period 的 now 参数注入，便于测试覆盖各时段。
        """
        try:
            trading_time = await asyncio.to_thread(
                self.trading_executor.trading_time, symbol, self.account_id
            )
        except Exception as e:
            self._log(f"交易时段查询失败，按可交易处理: {e}", LogLevel.WARNING)
            return True
        return in_trading_period(trading_time, now=self._clock())

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

        # 非交易时段不下单：服务器已做了时段门控，这里是第二道防线。快期模拟
        # 柜台在非交易时段也会照常撮合，漏下来就是账户里一笔莫名其妙的持仓。
        # 信号停在 pending（不记失败），等服务器下一笔交易时段的信号再执行；
        # 同一信号重投时上面的分支会把它重新置为 pending 并走正常流程。
        if not await self._in_trading_period(signal.symbol):
            self._log(
                f"非交易时段，暂不执行: {signal.symbol} 仓位 {signal.target_position}",
                LogLevel.WARNING,
            )
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
