"""交易网关预探测：把 tqsdk 的笼统超时换成能定位责任方的错误。

不连接天勤：只验证「什么情况该拦截、什么情况该放行」的判定逻辑。
"""

import unittest
from unittest.mock import patch

import websockets.exceptions as ws_exceptions

from src.domain.entities import Account
from src.infrastructure.trading.tqsdk_executor import (
    TradingGatewayUnavailableError,
    _check_trading_gateway,
    _lookup_trading_gateway,
    _preflight_trading_gateway,
    test_account_session,
)

GATEWAY = "wss://otg-sim.shinnytech.com/trade"


def _sim_account(**overrides) -> Account:
    base = dict(
        id="legacy",
        kind="sim",
        label="模拟账户",
        tq_account="13800000000",
        tq_password="secret",
        symbols=["SHFE.au2601"],
    )
    base.update(overrides)
    return Account(**base)


def _live_account(**overrides) -> Account:
    base = dict(
        id="live-1",
        kind="live",
        label="实盘账户",
        tq_account="13800000000",
        tq_password="secret",
        broker="H宏源期货",
        trade_account="022631",
        trade_password="123456",
        symbols=["SHFE.au2601"],
    )
    base.update(overrides)
    return Account(**base)


class _Response:
    def __init__(self, status_code, body):
        self.status_code = status_code
        self.body = body


class _FakeInvalidStatus(Exception):
    """模拟 websockets 的 InvalidStatus：只暴露探测代码实际读取的两个字段。"""

    def __init__(self, status_code, body=b""):
        self.response = _Response(status_code, body)
        super().__init__(f"HTTP {status_code}")


class _OkConn:
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class CheckTradingGatewayTest(unittest.TestCase):
    def _connect_raising(self, exc):
        def connect(url, open_timeout):
            raise exc

        return patch("websockets.sync.client.connect", new=connect)

    def test_server_rejection_reports_status_and_body(self):
        exc = _FakeInvalidStatus(500, b'{"error":"websocket: bad handshake"}')
        with patch.object(ws_exceptions, "InvalidStatus", _FakeInvalidStatus), \
             self._connect_raising(exc):
            with self.assertRaises(TradingGatewayUnavailableError) as ctx:
                _check_trading_gateway(GATEWAY)
        message = str(ctx.exception)
        self.assertIn("HTTP 500", message)
        self.assertIn("bad handshake", message)
        self.assertIn("天勤服务端故障", message)

    def test_4xx_response_is_not_a_server_fault(self):
        # 实盘网关对未认证的裸握手返回 401：网关活着、正常拒客，不能拦启动
        for status in (401, 403, 404):
            with self.subTest(status=status):
                exc = _FakeInvalidStatus(status, b'{"error":"unauthorized"}')
                with patch.object(ws_exceptions, "InvalidStatus", _FakeInvalidStatus), \
                     self._connect_raising(exc):
                    _check_trading_gateway(GATEWAY)

    def test_other_5xx_codes_also_block(self):
        exc = _FakeInvalidStatus(503, b'{"error":"unavailable"}')
        with patch.object(ws_exceptions, "InvalidStatus", _FakeInvalidStatus), \
             self._connect_raising(exc):
            with self.assertRaises(TradingGatewayUnavailableError):
                _check_trading_gateway(GATEWAY)

    def test_timeout_is_not_reported_as_server_fault(self):
        # 超时可能是本地网络抖动，交给 tqsdk 自己重连，不擅自断言
        with self._connect_raising(TimeoutError("connect timed out")):
            _check_trading_gateway(GATEWAY)

    def test_dns_failure_is_not_reported_as_server_fault(self):
        with self._connect_raising(OSError("name resolution failed")):
            _check_trading_gateway(GATEWAY)

    def test_successful_handshake_passes(self):
        with patch("websockets.sync.client.connect", new=lambda url, open_timeout: _OkConn()):
            _check_trading_gateway(GATEWAY)


class LookupTradingGatewayTest(unittest.TestCase):
    def _urlopen_returning(self, payload):
        import io
        import json

        return patch(
            "urllib.request.urlopen",
            new=lambda url, timeout: io.BytesIO(json.dumps(payload).encode()),
        )

    def test_reads_url_from_broker_config(self):
        payload = {"快期模拟": {"trade_environment": "simulation", "url": GATEWAY}}
        with self._urlopen_returning(payload):
            self.assertEqual(_lookup_trading_gateway("快期模拟"), GATEWAY)

    def test_missing_broker_returns_none(self):
        with self._urlopen_returning({"别的公司": {"url": GATEWAY}}):
            self.assertIsNone(_lookup_trading_gateway("快期模拟"))

    def test_network_error_returns_none(self):
        def boom(url, timeout):
            raise OSError("dns failure")

        with patch("urllib.request.urlopen", new=boom):
            self.assertIsNone(_lookup_trading_gateway("快期模拟"))


class PreflightTradingGatewayTest(unittest.TestCase):
    def _record_lookup(self):
        looked_up = []
        return looked_up, patch(
            "src.infrastructure.trading.tqsdk_executor._lookup_trading_gateway",
            new=lambda broker_id, timeout=5.0: looked_up.append(broker_id),
        )

    def test_sim_account_uses_fixed_broker_id(self):
        looked_up, lookup = self._record_lookup()
        with lookup:
            _preflight_trading_gateway(_sim_account())
        self.assertEqual(looked_up, ["快期模拟"])

    def test_live_account_uses_saved_broker(self):
        looked_up, lookup = self._record_lookup()
        with lookup:
            _preflight_trading_gateway(_live_account())
        self.assertEqual(looked_up, ["H宏源期货"])

    def test_live_account_without_broker_skips_lookup(self):
        looked_up, lookup = self._record_lookup()
        with lookup:
            _preflight_trading_gateway(_live_account(broker=""))
        self.assertEqual(looked_up, [], "没有期货公司时无从查网关")

    def test_unresolvable_gateway_skips_probe(self):
        checked = []
        with patch(
            "src.infrastructure.trading.tqsdk_executor._lookup_trading_gateway",
            new=lambda broker_id, timeout=5.0: None,
        ), patch(
            "src.infrastructure.trading.tqsdk_executor._check_trading_gateway",
            new=lambda url, timeout=5.0: checked.append(url),
        ):
            _preflight_trading_gateway(_sim_account())
        self.assertEqual(checked, [])

    def test_gateway_failure_blocks_before_tqsdk_starts(self):
        def boom(url, timeout=5.0):
            raise TradingGatewayUnavailableError("天勤交易柜台暂时不可用")

        with patch(
            "src.infrastructure.trading.tqsdk_executor._lookup_trading_gateway",
            new=lambda broker_id, timeout=5.0: GATEWAY,
        ), patch("src.infrastructure.trading.tqsdk_executor._check_trading_gateway", new=boom):
            with self.assertRaises(TradingGatewayUnavailableError):
                test_account_session(_sim_account(), ("13800000000", "secret"))


if __name__ == "__main__":
    unittest.main()
