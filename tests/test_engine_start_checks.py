"""engine.start 启动前预检与拦截语义。"""

import asyncio
import tempfile
import unittest
from contextlib import ExitStack
from pathlib import Path
from unittest.mock import patch

from src.backend.service import BackendService, EngineStartBlockedError
from src.domain.entities import Account, TradingConfiguration


def _make_service() -> BackendService:
    service = BackendService(Path(tempfile.mkdtemp()))
    service.bootstrap.save_config(
        TradingConfiguration(
            server_url="http://192.0.2.1:3080",
            auto_trade=False,
            database_path="data/client.db",
        )
    )
    return service


def _sim_account(**overrides) -> Account:
    base = dict(
        id="legacy",
        kind="sim",
        label="模拟账户",
        tq_account="13800000000",
        tq_password="secret",
        symbols=["SHFE.au2601"],
        strategy="supertrend",
        enabled=True,
    )
    base.update(overrides)
    return Account(**base)


def _live_account(**overrides) -> Account:
    base = dict(
        id="legacy-live",
        kind="live",
        label="实盘账户",
        tq_account="13800000000",
        tq_password="secret",
        broker="H海通期货",
        trade_account="022631",
        trade_password="123456",
        symbols=["SHFE.au2601"],
        strategy="supertrend",
        enabled=True,
    )
    base.update(overrides)
    return Account(**base)


async def _health_ok(url):
    return {}


def _start_with(service, health=None, session=None, token=True, real_start=False):
    """在可控依赖下调用 engine_start，返回捕获的异常（None 表示未抛）。

    real_start=True 时让 _start_engines 真正执行（引擎用替身），
    这样进度会一路推到 100%；默认跳过，测试只关心预检本身。
    """
    service.bootstrap.token_service.token_store.have_token = lambda: token
    health_mock = health or _health_ok
    session_mock = session or (lambda account, auth: None)
    patches = [
        patch("src.backend.service.check_server_health", new=health_mock),
        patch("src.backend.service.test_account_session", new=session_mock),
    ]
    if real_start:
        patches += [
            patch.object(service.bootstrap, "build_engine", new=_fake_build_engine),
            patch("src.application.trading_engine.TradingEngine.start", new=_fake_engine_start),
        ]
    else:
        patches.append(patch.object(service, "_start_engines", new=_noop))
    with ExitStack() as stack:
        for p in patches:
            stack.enter_context(p)
        try:
            asyncio.run(service.engine_start({}))
        except EngineStartBlockedError as e:
            return e
    return None


async def _noop():
    return None


async def _fake_engine_start(self) -> None:
    return None


async def _fake_build_engine(account_id: str):
    """替身引擎：只满足 _start_engines 用到的 is_running / set_auto_trade / start。"""

    class _Engine:
        is_running = False
        server_connected = False

        def set_auto_trade(self, value: bool) -> None:
            pass

        async def start(self) -> None:
            return None

    return _Engine()


def _collect_progress(service) -> list[tuple[str, str, int]]:
    """把启动进度广播收进列表：engine.start 期间壳只能靠它看进度。"""
    events: list[tuple[str, str, int]] = []

    def on_broadcast(name: str, data: dict) -> None:
        if name == "engine_start_progress":
            events.append((data["stage"], data["text"], data["pct"]))

    service.attach_broadcast(on_broadcast)
    return events


class EngineStartPreflightTest(unittest.TestCase):
    def test_no_enabled_account_blocks(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account(enabled=False))
        error = _start_with(service)
        self.assertIsNotNone(error)
        self.assertTrue(any("没有已启用的账户" in i for i in error.issues))
        self.assertFalse(service._auto_trade)

    def test_server_offline_blocks(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())

        async def boom(url):
            raise RuntimeError("refused")

        error = _start_with(service, health=boom)
        self.assertIsNotNone(error)
        self.assertTrue(any("服务器未连接" in i for i in error.issues))
        self.assertFalse(service._auto_trade)

    def test_missing_tq_credentials_blocks_and_skips_probe(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account(tq_password=""))
        probed = []
        error = _start_with(service, session=lambda a, auth: probed.append(a.id))
        self.assertIsNotNone(error)
        self.assertTrue(any("未配置快期密码" in i for i in error.issues))
        self.assertEqual(probed, [], "凭据不全时不应再尝试天勤握手")

    def test_live_fields_required(self):
        service = _make_service()
        service.bootstrap.save_account(
            _live_account(broker="", trade_account="", trade_password="")
        )
        error = _start_with(service)
        self.assertIsNotNone(error)
        joined = "".join(error.issues)
        self.assertIn("未选择期货公司", joined)
        self.assertIn("未填写资金账号", joined)
        self.assertIn("未填写交易密码", joined)

    def test_missing_symbols_blocks(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account(symbols=[]))
        error = _start_with(service)
        self.assertIsNotNone(error)
        self.assertTrue(any("未选择交易品种" in i for i in error.issues))

    def test_missing_strategy_blocks(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account(strategy=""))
        error = _start_with(service)
        self.assertIsNotNone(error)
        self.assertTrue(any("未选择交易策略" in i for i in error.issues))
        self.assertFalse(service._auto_trade)

    def test_live_missing_strategy_blocks(self):
        service = _make_service()
        service.bootstrap.save_account(_live_account(strategy=""))
        error = _start_with(service)
        self.assertIsNotNone(error)
        self.assertTrue(any("账户「实盘账户」未选择交易策略" in i for i in error.issues))

    def test_missing_token_blocks(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        error = _start_with(service, token=False)
        self.assertIsNotNone(error)
        self.assertTrue(any("API token 未领取" in i for i in error.issues))

    def test_tq_session_failure_blocks_with_account_label(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())

        def boom(account, auth):
            raise Exception("用户权限认证失败 (401)")

        error = _start_with(service, session=boom)
        self.assertIsNotNone(error)
        self.assertTrue(
            any("模拟账户" in i and "天勤连接失败" in i for i in error.issues),
            error.issues,
        )

    def test_disabled_account_is_not_probed(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        service.bootstrap.save_account(
            _live_account(id="off", label="停用实盘", enabled=False, tq_password="")
        )
        probed = []

        def record(account, auth):
            probed.append(account.id)

        error = _start_with(service, session=record)
        self.assertIsNone(error)
        self.assertEqual(probed, ["legacy"])

    def test_all_checks_pass_starts_engine(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        error = _start_with(service)
        self.assertIsNone(error)
        self.assertTrue(service._auto_trade)

    def test_accounts_list_masks_password_and_provides_flags(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account(tq_password="secret"))
        service.bootstrap.save_account(
            _live_account(id="acc-empty", tq_password="", trade_password="")
        )
        accounts = service.accounts_list()
        sim = next(a for a in accounts if a["id"] == "legacy")
        self.assertNotIn("tq_password", sim)
        self.assertNotIn("trade_password", sim)
        self.assertTrue(sim["has_tq_password"])
        # 只回传长度，供密码框按真实长度绘制掩码
        self.assertEqual(sim["tq_password_length"], len("secret"))
        self.assertEqual(sim["trade_password_length"], 0)

        live = next(a for a in accounts if a["id"] == "acc-empty")
        self.assertNotIn("tq_password", live)
        self.assertNotIn("trade_password", live)
        self.assertFalse(live["has_tq_password"])
        self.assertFalse(live["has_trade_password"])
        self.assertEqual(live["tq_password_length"], 0)
        self.assertEqual(live["trade_password_length"], 0)

    def test_get_account_password_returns_saved_password(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account(tq_password="sim_secret_123"))
        resp = asyncio.run(service.get_account_password({"kind": "sim", "field": "tq_password"}))
        self.assertEqual(resp.get("password"), "sim_secret_123")

        resp_by_id = asyncio.run(service.get_account_password({"account_id": "legacy", "field": "tq_password"}))
        self.assertEqual(resp_by_id.get("password"), "sim_secret_123")

        resp_none = asyncio.run(service.get_account_password({"account_id": "non_existent"}))
        self.assertEqual(resp_none.get("password"), "")

    def test_start_failure_rolls_back_auto_trade(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        service.bootstrap.token_service.token_store.have_token = lambda: True

        async def failing_start():
            raise EngineStartBlockedError(["启动账户 legacy 失败: boom"])

        with patch("src.backend.service.check_server_health", new=_health_ok), \
             patch("src.backend.service.test_account_session", return_value=None), \
             patch.object(service, "_start_engines", new=failing_start), \
             patch.object(service, "stop_engines", new=_noop):
            with self.assertRaises(EngineStartBlockedError):
                asyncio.run(service.engine_start({}))
        self.assertFalse(service._auto_trade, "启动失败必须回滚 auto_trade")

    def test_error_payload_carries_issues(self):
        error = EngineStartBlockedError(["问题一", "问题二"])
        self.assertEqual(error.to_payload(), {"data": {"issues": ["问题一", "问题二"]}})
        self.assertEqual(str(error), "问题一；问题二")


class EngineStartProgressTest(unittest.TestCase):
    """预检/启动各阶段要广播进度，否则壳只能显示「检查中」无从判断卡在哪。"""

    def test_preflight_broadcasts_stages_in_order(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        events = _collect_progress(service)
        _start_with(service, real_start=True)
        stages = [stage for stage, _, _ in events]
        # 预检阶段按 server → accounts → tqsdk 推进，最后是启动会话
        self.assertEqual(stages[0], "server")
        self.assertLess(stages.index("accounts"), stages.index("tqsdk"))
        self.assertEqual(stages[-1], "engines")

    def test_progress_increases_monotonically_to_100(self):
        """单账户时 pct 也要从低到 100 连续推进——否则进度条根本看不见。"""
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        events = _collect_progress(service)
        _start_with(service, real_start=True)
        pcts = [pct for _, _, pct in events if pct >= 0]
        self.assertGreaterEqual(len(pcts), 4, events)
        self.assertEqual(pcts[-1], 100)
        self.assertEqual(pcts, sorted(pcts), f"pct 必须单调不减：{pcts}")
        # 起步不能是 0：从 0 开始会让进度条在最初一段时间里完全为空白
        self.assertGreater(pcts[0], 0, events)

    def test_single_account_hides_item_counter(self):
        """只有一个账户时不显示「0/1」——看着像没动。"""
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        events = _collect_progress(service)
        _start_with(service)
        texts = [text for _, text, _ in events]
        self.assertIn("测试天勤连接…", texts)
        self.assertFalse(any("/1" in t for t in texts), texts)

    def test_multi_account_counts_items(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        service.bootstrap.save_account(
            _live_account(id="live", label="实盘账户")
        )
        events = _collect_progress(service)
        _start_with(service)
        tqsdk = [text for stage, text, _ in events if stage == "tqsdk"]
        self.assertEqual(tqsdk[0], "测试天勤连接 0/2…")
        self.assertEqual(tqsdk[-1], "测试天勤连接 2/2…")

    def test_blocked_start_does_not_claim_completion(self):
        """预检被拦时进度停在拦截点，不能假装走完——用户看到的是问题清单。"""
        service = _make_service()
        service.bootstrap.save_account(_sim_account(tq_password=""))
        events = _collect_progress(service)
        error = _start_with(service, real_start=True)
        self.assertIsNotNone(error)
        self.assertTrue(any("未配置快期密码" in i for i in error.issues))
        stages = [stage for stage, _, _ in events]
        self.assertNotIn("tqsdk", stages)
        self.assertNotIn("engines", stages)
        self.assertLess([pct for _, _, pct in events if pct >= 0][-1], 100)

    def test_start_engines_reports_session_progress(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        service.bootstrap.save_account(_live_account(id="live", label="实盘账户"))
        service.bootstrap.token_service.token_store.have_token = lambda: True
        events = _collect_progress(service)
        _start_with(service, real_start=True)

        engines = [text for stage, text, _ in events if stage == "engines"]
        self.assertEqual(engines[0], "启动交易会话 0/2…")
        self.assertEqual(engines[-1], "启动交易会话 2/2…")

    def test_stop_broadcasts_stage_without_pct(self):
        """停止没有细分阶段：只给文案，pct=-1 让壳不画进度条。"""
        service = _make_service()
        events = _collect_progress(service)
        asyncio.run(service.engine_stop({}))
        stop = [(text, pct) for stage, text, pct in events if stage == "stop"]
        self.assertEqual(stop, [("正在停止…", -1)])


if __name__ == "__main__":
    unittest.main()
