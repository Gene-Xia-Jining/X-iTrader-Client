import unittest

from src.infrastructure.api.client import parse_signal


class ParseSignalTest(unittest.TestCase):
    """服务器 SSE payload → StrategySignal 的字段契约。

    服务器是 payload 的定义方，客户端必须按它的字段名解析；这里锁住
    真实 payload 的字段，避免再次出现 position / target_position 错位。
    """

    def _payload(self, **overrides):
        payload = {
            "id": "sig-1",
            "symbol": "KQ.m@CZCE.SA",
            "strategy_id": "supertrend",
            "side": "long",
            "price": 1234.5,
            "target_position": 1,
            "updated_at": 1792471142,
        }
        payload.update(overrides)
        return payload

    def test_parses_server_payload(self):
        signal = parse_signal(self._payload())

        self.assertEqual(signal.symbol, "KQ.m@CZCE.SA")
        self.assertEqual(signal.target_position, 1)
        self.assertEqual(signal.strategy_id, "supertrend")

    def test_target_position_is_not_position(self):
        """守住字段名：服务器推 target_position，客户端不能要求 position。"""
        signal = parse_signal(self._payload(target_position=-1))

        self.assertEqual(signal.target_position, -1)
        self.assertFalse(hasattr(signal, "position"))

    def test_ignores_unknown_fields(self):
        """服务器新增字段时旧客户端应降级为忽略，而不是抛 TypeError 断流。"""
        signal = parse_signal(self._payload(future_field="x", another=1))

        self.assertEqual(signal.target_position, 1)

    def test_tolerates_old_server_without_strategy_id(self):
        payload = self._payload()
        del payload["strategy_id"]

        self.assertEqual(parse_signal(payload).strategy_id, "")


if __name__ == "__main__":
    unittest.main()
