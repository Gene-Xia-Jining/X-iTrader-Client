"""TradingEngine 的非交易时段门控。

服务器已按交易时段门控信号，这里是第二道防线：漏下来的信号若在非交易时段
交给执行器，快期模拟柜台会照常撮合，账户里就多出一笔本不该有的持仓。

时钟通过 TradingEngineDeps.clock 注入，测试不依赖真实时间。
"""

import asyncio
import datetime
import tempfile
import unittest

from src.application.trading_engine import TradingEngine, TradingEngineDeps
from src.domain.entities import (
    StrategySignal,
    TradeCommandStatus,
    TradingConfiguration,
)
from src.domain.events import EventBus
from src.domain.services import TradingExecutor
from src.infrastructure.db.repositories import SQLiteTradeCommandRepository

# 上期所螺纹白盘：09:00-10:15 / 10:30-11:30 / 13:30-15:00，无夜盘
_RB_TRADING_TIME = {
    "day": [
        ["09:00:00", "10:15:00"],
        ["10:30:00", "11:30:00"],
        ["13:30:00", "15:00:00"],
    ],
    "night": [],
}


class _FakeExecutor(TradingExecutor):
    def __init__(self, trading_time=_RB_TRADING_TIME, raise_on_query=False):
        self.calls = []
        self._trading_time = trading_time
        self._raise = raise_on_query

    async def set_target_position(self, symbol, target_position, account_id=""):
        self.calls.append((symbol, target_position, account_id))

    async def close(self):
        pass

    def trading_time(self, symbol, account_id=""):
        if self._raise:
            raise RuntimeError("行情未就绪")
        return self._trading_time


class _FakeStream:
    def __init__(self, signals=()):
        self._signals = list(signals)

    async def stream(self):
        for signal in self._signals:
            yield signal


def _signal(signal_id="sig-1", symbol="KQ.m@SHFE.rb", target=1):
    return StrategySignal(
        id=signal_id,
        symbol=symbol,
        side="long",
        price=3083.0,
        target_position=target,
        updated_at=1792541423,
    )


def _at(hour, minute, second=0):
    return datetime.datetime(2026, 10, 21, hour, minute, second)


class OffHoursGateTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.repo = SQLiteTradeCommandRepository(f"{self._tmp.name}/client.db")
        asyncio.run(self.repo.init())

    def tearDown(self):
        self._tmp.cleanup()

    def _deps(self, executor, auto_trade=True, clock=None):
        """构造引擎依赖；clock 为 None 时保留默认值（生产走的正是这条路径）。"""
        deps = TradingEngineDeps(
            config=TradingConfiguration(
                server_url="http://192.0.2.1:3080", auto_trade=auto_trade
            ),
            account_id="acc",
            trade_repo=self.repo,
            stream_client=_FakeStream(),
            trading_executor=executor,
            event_bus=EventBus(),
            auto_trade=auto_trade,
        )
        if clock is not None:
            deps.clock = clock
        return deps

    def _engine(self, executor, auto_trade=True, clock=None):
        return TradingEngine(deps=self._deps(executor, auto_trade, clock))

    def _status(self, signal_id="sig-1"):
        return asyncio.run(self.repo.get("acc", signal_id)).status

    def test_off_hours_signal_is_pending_and_not_executed(self):
        """盘前 08:10（事故时点）：不调用执行器，记录停在 pending 而非 failed。"""
        executor = _FakeExecutor()
        engine = self._engine(executor, clock=lambda: _at(8, 10, 23))
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(executor.calls, [])
        self.assertEqual(self._status(), TradeCommandStatus.PENDING)

    def test_off_hours_never_marked_executed(self):
        """核心约束：非交易时段绝不能出现 executed 记录。"""
        executor = _FakeExecutor()
        engine = self._engine(executor, clock=lambda: _at(8, 10, 23))
        asyncio.run(engine._handle_signal(_signal()))
        self.assertNotEqual(self._status(), TradeCommandStatus.EXECUTED)

    def test_evening_signal_blocked_before_night_session(self):
        """夜盘前 19:12（另一个事故时点）同样拦截。"""
        executor = _FakeExecutor()
        engine = self._engine(executor, clock=lambda: _at(19, 12, 39))
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(executor.calls, [])

    def test_in_hours_signal_executes(self):
        """交易时段内正常执行，门控不能误伤。"""
        executor = _FakeExecutor()
        engine = self._engine(executor, clock=lambda: _at(10, 0))
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(executor.calls, [("KQ.m@SHFE.rb", 1, "acc")])
        self.assertEqual(self._status(), TradeCommandStatus.EXECUTED)

    def test_missing_trading_time_fails_open(self):
        """拿不到时段表时放行，不能把开盘期间的调仓全部拦掉。"""
        executor = _FakeExecutor(trading_time=None)
        engine = self._engine(executor, clock=lambda: _at(8, 10, 23))
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(executor.calls, [("KQ.m@SHFE.rb", 1, "acc")])

    def test_query_error_fails_open(self):
        """查询异常按可交易处理（有日志），不能静默拦单。"""
        executor = _FakeExecutor(raise_on_query=True)
        engine = self._engine(executor, clock=lambda: _at(8, 10, 23))
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(executor.calls, [("KQ.m@SHFE.rb", 1, "acc")])

    def test_auto_trade_off_records_only(self):
        """自动交易关闭时只记录不执行，与时段无关。"""
        executor = _FakeExecutor()
        engine = self._engine(executor, auto_trade=False)
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(executor.calls, [])
        self.assertEqual(self._status(), TradeCommandStatus.PENDING)

    def test_default_clock_is_callable(self):
        """生产路径不注入 clock，默认值必须可直接调用。

        曾经默认值是 None，门控一执行就抛 TypeError('NoneType' object is not
        callable)，异常被 _run_loop 当成连接错误吞掉：信号永远停在 pending，
        界面只显示「收到信号」，账户一笔都没动。
        """
        executor = _FakeExecutor()
        engine = TradingEngine(deps=self._deps(executor))
        self.assertTrue(callable(engine._clock))
        # 真的走一遍门控：无论当前是否在交易时段，都不得抛异常
        asyncio.run(engine._handle_signal(_signal()))
        self.assertNotEqual(self._status(), TradeCommandStatus.EXECUTING)

    def test_pending_signal_can_execute_when_market_opens(self):
        """停在 pending 的信号，交易时段内重投要能执行（不得永久卡死）。"""
        executor = _FakeExecutor()
        clock = {"now": _at(8, 10, 23)}
        engine = self._engine(executor, clock=lambda: clock["now"])

        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(self._status(), TradeCommandStatus.PENDING)

        clock["now"] = _at(9, 30)
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(executor.calls, [("KQ.m@SHFE.rb", 1, "acc")])
        self.assertEqual(self._status(), TradeCommandStatus.EXECUTED)

    def test_executed_signal_not_repeated(self):
        """已执行的信号重投不得重复下单。"""
        executor = _FakeExecutor()
        # 显式给交易时段内的时钟：这条断言与时段无关，不能被门控拦成 0 次
        engine = self._engine(executor, clock=lambda: _at(10, 0))
        asyncio.run(engine._handle_signal(_signal()))
        asyncio.run(engine._handle_signal(_signal()))
        self.assertEqual(len(executor.calls), 1)


if __name__ == "__main__":
    unittest.main()
