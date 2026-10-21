"""engine.start 启动前预检与拦截语义。"""

import asyncio
import tempfile
import unittest
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


def _start_with(service, health=None, session=None, token=True):
    """在可控依赖下调用 engine_start，返回捕获的异常（None 表示未抛）。"""
    service.bootstrap.token_service.token_store.have_token = lambda: token
    health_mock = health or _health_ok
    session_mock = session or (lambda account, auth: None)
    with patch("src.backend.service.check_server_health", new=health_mock), \
         patch("src.backend.service.test_account_session", new=session_mock), \
         patch.object(service, "_start_engines", new=_noop):
        try:
            asyncio.run(service.engine_start({}))
        except EngineStartBlockedError as e:
            return e
    return None


async def _noop():
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


def _collect_progress(service) -> list[tuple[str, str, int, int]]:
    """把启动进度广播收进列表：engine.start 期间壳只能靠它看进度。"""
    events: list[tuple[str, str, int, int]] = []

    def on_broadcast(name: str, data: dict) -> None:
        if name == "engine_start_progress":
            events.append((data["stage"], data["text"], data["step"], data["total"]))

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
        _start_with(service)
        stages = [stage for stage, _, _, _ in events]
        self.assertEqual(stages, ["server", "accounts", "tqsdk", "tqsdk"])

    def test_tqsdk_probe_reports_completion_count(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        service.bootstrap.save_account(
            _live_account(id="live", label="实盘账户")
        )
        events = _collect_progress(service)
        _start_with(service)
        tqsdk = [(text, step, total) for stage, text, step, total in events if stage == "tqsdk"]
        # 起始 0/2，每完成一个账户推进一次，最后落在 2/2
        self.assertEqual(tqsdk[0], ("天勤握手 0/2…", 0, 2))
        self.assertEqual(tqsdk[-1], ("天勤握手 2/2…", 2, 2))
        self.assertEqual(sorted(step for _, step, _ in tqsdk), [0, 1, 2])

    def test_no_probe_skips_tqsdk_stage(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account(tq_password=""))
        events = _collect_progress(service)
        _start_with(service)
        self.assertNotIn("tqsdk", [stage for stage, _, _, _ in events])

    def test_start_engines_reports_session_progress(self):
        service = _make_service()
        service.bootstrap.save_account(_sim_account())
        service.bootstrap.save_account(_live_account(id="live", label="实盘账户"))
        service.bootstrap.token_service.token_store.have_token = lambda: True
        events = _collect_progress(service)

        async def fake_start(self) -> None:
            return None

        with patch("src.backend.service.check_server_health", new=_health_ok), \
             patch("src.backend.service.test_account_session", return_value=None), \
             patch.object(service.bootstrap, "build_engine", new=_fake_build_engine), \
             patch("src.application.trading_engine.TradingEngine.start", new=fake_start):
            asyncio.run(service.engine_start({}))

        engines = [(text, step, total) for stage, text, step, total in events if stage == "engines"]
        # 文案里的数字与 step 必须一致，否则进度条和文字会打架
        self.assertEqual(engines[0], ("启动交易会话 0/2…", 0, 2))
        self.assertEqual(engines[-1][1:], (2, 2))

    def test_stop_broadcasts_stage(self):
        service = _make_service()
        events = _collect_progress(service)
        asyncio.run(service.engine_stop({}))
        self.assertIn("stop", [stage for stage, _, _, _ in events])


if __name__ == "__main__":
    unittest.main()
