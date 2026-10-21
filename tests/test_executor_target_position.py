"""TqSdkTradingExecutor.set_target_position 的成交确认语义。

不连接天勤：用假的 TqApi / TargetPosTask 复现 tqsdk 的推进模型，验证
「只有持仓真正到位才算成功」——单次 wait_update 正常返回绝不代表下单成功。
"""

import asyncio
import unittest

from src.infrastructure.trading.tqsdk_executor import TqSdkTradingExecutor


class _FakePosition:
    def __init__(self, pos=0):
        self.pos = pos


class _FakeOrder:
    def __init__(self, instrument_id, is_error=False, last_msg="", direction="BUY", volume_orign=1):
        self.instrument_id = instrument_id
        self.is_error = is_error
        self.last_msg = last_msg
        self.direction = direction
        self.volume_orign = volume_orign


class _FakeQuote:
    def __init__(self, underlying_symbol, trading_time=None):
        self.underlying_symbol = underlying_symbol
        self.trading_time = trading_time or {}


class _FakeApi:
    """按「每推进一次就走一步」的脚本模拟 tqsdk：steps 里每项是本次 wait_update 后的持仓。"""

    def __init__(self, underlying_symbol="SHFE.rb2701", steps=(), error_after=None,
                 trading_time=None):
        self.underlying_symbol = underlying_symbol
        self.steps = list(steps)
        self.error_after = error_after
        self.trading_time = trading_time or {}
        self.updates = 0
        self.closed = False

    def get_quote(self, symbol):
        # 主连带标的合约，实盘合约不带
        underlying = self.underlying_symbol if symbol.startswith("KQ.") else ""
        return _FakeQuote(underlying, self.trading_time)

    def get_position(self, symbol):
        done = min(self.updates, len(self.steps))
        pos = self.steps[done - 1] if done else 0
        return _FakePosition(pos)

    def get_order(self, account=None):
        orders = {oid: _FakeOrder("rb2701") for oid in getattr(self, "preexisting", [])}
        if self.error_after is not None and self.updates >= self.error_after:
            orders["new-bad-order"] = _FakeOrder("rb2701", True, "开仓资金不足")
        return orders

    def wait_update(self, deadline=None):
        self.updates += 1
        return True

    def close(self):
        self.closed = True


class _FakeAccount:
    def _check_valid(self, account):
        return account


class _FakeApiWithAccount(_FakeApi):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._account = _FakeAccount()


class _FakeTask:
    def __init__(self, api, symbol, account=None, price="ACTIVE"):
        self.api = api
        self.symbol = symbol
        self.volumes = []
        self.cancelled = False

    def set_target_volume(self, volume):
        self.volumes.append(volume)

    def cancel(self):
        self.cancelled = True

    def is_finished(self):
        return self.cancelled


class _FakeSession:
    def __init__(self, api):
        self.account_id = "acc"
        self.account_obj = object()
        self.api = api
        self._tasks = {}


class TargetPositionTest(unittest.TestCase):
    def setUp(self):
        import tqsdk

        self._orig_task = tqsdk.TargetPosTask
        tqsdk.TargetPosTask = _FakeTask

    def tearDown(self):
        import tqsdk

        tqsdk.TargetPosTask = self._orig_task

    def _executor(self, api, fill_timeout=1.0):
        executor = TqSdkTradingExecutor(accounts=[], auths={}, fill_timeout=fill_timeout)
        executor._sessions = {"acc": _FakeSession(api)}
        # 跳过后台线程：直接在调用线程执行 _set
        executor._submit = lambda account_id, fn, timeout=None: fn()
        return executor

    def test_waits_until_position_reached(self):
        """持仓要几次推进才到位时，必须持续推进而不是推进一次就返回。"""
        api = _FakeApi(steps=[0, 0, 1])
        executor = self._executor(api)
        asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 1, "acc"))
        self.assertEqual(api.get_position("SHFE.rb2701").pos, 1)
        self.assertGreaterEqual(api.updates, 3)

    def test_orders_on_underlying_symbol_not_continuous(self):
        """主连只用于行情，下单必须落到实际合约上。"""
        api = _FakeApi(steps=[1])
        executor = self._executor(api)
        created = []
        executor._get_task = lambda session, symbol, order_symbol: created.append(order_symbol) or _FakeTask(
            session.api, order_symbol
        )
        asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 1, "acc"))
        self.assertEqual(created, ["SHFE.rb2701"])

    def test_plain_contract_is_used_as_is(self):
        api = _FakeApi(underlying_symbol="")
        self.assertEqual(executor_symbol(api, "SHFE.rb2701"), "SHFE.rb2701")

    def test_timeout_raises_and_cancels(self):
        """永远不成交（如非交易时段）必须报错，不能假装成功。"""
        api = _FakeApi(steps=[0, 0, 0, 0, 0])
        executor = self._executor(api, fill_timeout=0.01)
        with self.assertRaises(TimeoutError):
            asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 1, "acc"))

    def test_rejected_order_raises_immediately(self):
        """交易所拒单要立刻失败，且带上原因。"""
        api = _FakeApi(steps=[0, 0, 0], error_after=1)
        executor = self._executor(api, fill_timeout=5.0)
        with self.assertRaises(RuntimeError) as ctx:
            asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 1, "acc"))
        self.assertIn("开仓资金不足", str(ctx.exception))

    def test_closing_to_zero_succeeds(self):
        """平到 0 手也是合法目标，不能把 0 当成「未到位」。"""
        api = _FakeApi(steps=[0])
        executor = self._executor(api)
        asyncio.run(executor.set_target_position("SHFE.rb2701", 0, "acc"))

    def test_historical_rejected_order_does_not_fail_new_run(self):
        """同一合约上历史遗留的错单不能让之后每次调仓都误判失败。"""
        api = _FakeApi(steps=[1])
        api.preexisting = ["old-bad-order"]
        api.get_order = lambda account=None: {
            "old-bad-order": _FakeOrder("rb2701", True, "历史旧错单"),
        }
        executor = self._executor(api)
        asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 1, "acc"))
        self.assertEqual(api.get_position("SHFE.rb2701").pos, 1)

    def test_same_symbol_reuses_task(self):
        """同一实际合约连续调仓必须复用同一任务，不能每次重建（会丢未成交跟踪）。"""
        api = _FakeApi(steps=[1, 1])
        session = _FakeSession(api)
        executor = TqSdkTradingExecutor(accounts=[], auths={}, fill_timeout=1.0)
        executor._sessions = {"acc": session}
        executor._submit = lambda account_id, fn, timeout=None: fn()
        asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 1, "acc"))
        first = session._tasks["KQ.m@SHFE.rb"][0]
        api.steps = [2]
        asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 2, "acc"))
        self.assertIs(session._tasks["KQ.m@SHFE.rb"][0], first)
        self.assertEqual(first.volumes, [1, 2])

    def test_rollover_rebinds_task(self):
        """主力换月后必须用新合约重建任务，不能继续往旧合约下单。"""
        api = _FakeApi(underlying_symbol="SHFE.rb2701", steps=[1])
        session = _FakeSession(api)
        old = _FakeTask(api, "SHFE.rb2609")
        session._tasks["KQ.m@SHFE.rb"] = (old, "SHFE.rb2609")
        executor = TqSdkTradingExecutor(accounts=[], auths={}, fill_timeout=1.0)
        executor._sessions = {"acc": session}
        executor._submit = lambda account_id, fn, timeout=None: fn()

        asyncio.run(executor.set_target_position("KQ.m@SHFE.rb", 1, "acc"))
        self.assertTrue(old.cancelled)
        self.assertEqual(session._tasks["KQ.m@SHFE.rb"][1], "SHFE.rb2701")


class TradingTimeQueryTest(unittest.TestCase):
    """executor.trading_time：供上层做非交易时段门控的时段表来源。

    拿不到时段表一律返回 None（调用方放行），不能因为查询失败挡住调仓。
    """

    def _executor(self, api, sessions=None):
        executor = TqSdkTradingExecutor(accounts=[], auths={}, trading_time_wait=0.05)
        executor._sessions = sessions if sessions is not None else {"acc": _FakeSession(api)}
        executor._submit = lambda account_id, fn, timeout=None: fn()
        return executor

    def test_returns_normalized_dict(self):
        """tqsdk 的 TradingTime 是 MutableMapping，要转成普通 dict 跨线程使用。"""
        api = _FakeApi(trading_time={
            "day": [["09:00:00", "10:15:00"]],
            "night": [["21:00:00", "23:00:00"]],
        })
        result = self._executor(api).trading_time("KQ.m@SHFE.rb", "acc")
        self.assertEqual(result, {
            "day": [["09:00:00", "10:15:00"]],
            "night": [["21:00:00", "23:00:00"]],
        })
        self.assertIsInstance(result["day"][0], list)

    def test_unknown_account_returns_none(self):
        """会话不存在时不抛错，返回 None 让调用方放行。"""
        executor = self._executor(_FakeApi())
        self.assertIsNone(executor.trading_time("KQ.m@SHFE.rb", "missing"))

    def test_empty_trading_time_returns_none(self):
        """行情未就绪（时段表为空）时返回 None，而不是空 dict。"""
        executor = self._executor(_FakeApi(trading_time={}))
        self.assertIsNone(executor.trading_time("KQ.m@SHFE.rb", "acc"))

    def test_submit_failure_returns_none(self):
        """投递失败（会话已关闭等）同样降级为 None，不阻断调仓。"""
        executor = TqSdkTradingExecutor(accounts=[], auths={})
        executor._sessions = {"acc": _FakeSession(_FakeApi())}

        def boom(account_id, fn, timeout=None):
            raise RuntimeError("执行器已关闭")

        executor._submit = boom
        self.assertIsNone(executor.trading_time("KQ.m@SHFE.rb", "acc"))


def executor_symbol(api, symbol):
    return TqSdkTradingExecutor._tradable_symbol(api, symbol)


if __name__ == "__main__":
    unittest.main()
