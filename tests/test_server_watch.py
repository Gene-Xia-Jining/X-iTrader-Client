import asyncio
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from src.backend.service import BackendService
from src.domain.entities import TradingConfiguration


def _make_service(server_url: str) -> tuple[BackendService, list]:
    service = BackendService(Path(tempfile.mkdtemp()))
    service.bootstrap.save_config(
        TradingConfiguration(
            server_url=server_url,
            auto_trade=False,
            database_path="data/client.db",
        )
    )
    events: list = []
    service._broadcast_fn = lambda name, data: events.append((name, data))
    return service, events


def probe(service):
    asyncio.run(service.probe_server())


class ProbeServerTest(unittest.TestCase):
    def test_online_broadcasts_once(self):
        service, events = _make_service("http://192.0.2.1:3080")
        with patch("src.backend.service.check_server_health") as check:
            probe(service)
            probe(service)
        self.assertEqual(check.call_count, 2)
        self.assertTrue(service._server_online)
        # 首次由离线转在线广播一次；状态不变不重复广播
        self.assertEqual(
            events,
            [("status", {"server_online": True, "server_error": ""})],
        )

    def test_offline_broadcasts_on_outage(self):
        service, events = _make_service("http://192.0.2.1:3080")
        with patch("src.backend.service.check_server_health") as check:
            probe(service)
            check.side_effect = RuntimeError("refused")
            probe(service)
        self.assertFalse(service._server_online)
        self.assertEqual(
            events,
            [
                ("status", {"server_online": True, "server_error": ""}),
                ("status", {"server_online": False, "server_error": "RuntimeError（refused）"}),
            ],
        )

    def test_initial_offline_broadcasts_failure_reason(self):
        # 初始即离线：连通性经 app.status 初始拉取已得到 false，
        # 但失败原因只在这里产生，仍需广播一次让界面拿得到
        service, events = _make_service("http://192.0.2.1:3080")
        with patch("src.backend.service.check_server_health", side_effect=RuntimeError("refused")):
            probe(service)
        self.assertFalse(service._server_online)
        self.assertEqual(
            events,
            [("status", {"server_online": False, "server_error": "RuntimeError（refused）"})],
        )

    def test_empty_url_stays_offline_without_probe(self):
        service, events = _make_service("")
        with patch("src.backend.service.check_server_health") as check:
            probe(service)
        check.assert_not_called()
        self.assertFalse(service._server_online)
        self.assertEqual(events, [])

    def test_recovers_after_outage(self):
        service, events = _make_service("http://192.0.2.1:3080")
        with patch("src.backend.service.check_server_health") as check:
            probe(service)
            check.side_effect = RuntimeError("down")
            probe(service)
            check.side_effect = None
            probe(service)
        self.assertTrue(service._server_online)
        self.assertEqual(
            events,
            [
                ("status", {"server_online": True, "server_error": ""}),
                ("status", {"server_online": False, "server_error": "RuntimeError（down）"}),
                ("status", {"server_online": True, "server_error": ""}),
            ],
        )

    def test_reason_change_while_offline_broadcasts_again(self):
        # 离线期间原因变了（如连接被拒 → 超时），界面上的说明要跟着更新
        service, events = _make_service("http://192.0.2.1:3080")
        with patch("src.backend.service.check_server_health") as check:
            check.side_effect = RuntimeError("refused")
            probe(service)
            check.side_effect = RuntimeError("timeout")
            probe(service)
            probe(service)  # 原因不变，不重复广播
        self.assertEqual(
            events,
            [
                ("status", {"server_online": False, "server_error": "RuntimeError（refused）"}),
                ("status", {"server_online": False, "server_error": "RuntimeError（timeout）"}),
            ],
        )

    def test_engine_status_carries_error(self):
        service, _ = _make_service("http://192.0.2.1:3080")
        with patch("src.backend.service.check_server_health", side_effect=RuntimeError("refused")):
            probe(service)
        self.assertEqual(
            service.engine_status()["server_error"],
            "RuntimeError（refused）",
        )
        self.assertFalse(service.engine_status()["server_online"])


if __name__ == "__main__":
    unittest.main()
