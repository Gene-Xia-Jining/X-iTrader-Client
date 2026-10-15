import asyncio
import tempfile
import unittest
from pathlib import Path

from src.backend.service import BackendService
from src.domain.entities import TradingConfiguration
from src.infrastructure.api.db_token_store import DbTokenStore


class FakeTokenService:
    """替身：真实加密存储 + 打桩的网络方法，避免测试依赖服务器。"""

    def __init__(self, db_path: str, claim_result=None, valid=True, records=None):
        self.token_store = DbTokenStore(db_path)
        self._claim_result = claim_result
        self._valid = valid
        self._records = records or []
        self.claim_calls = 0
        self.validate_calls = 0
        self.list_calls = 0

    async def claim_token(self):
        self.claim_calls += 1
        return self._claim_result

    async def validate_token(self, token):
        self.validate_calls += 1
        return self._valid

    async def list_token_requests(self):
        self.list_calls += 1
        return self._records


def _make_service(server_url: str = "http://192.0.2.1:3080"):
    workdir = Path(tempfile.mkdtemp())
    service = BackendService(workdir)
    service.bootstrap.save_config(
        TradingConfiguration(
            server_url=server_url,
            auto_trade=False,
            database_path="data/client.db",
        )
    )
    events: list = []
    service._broadcast_fn = lambda name, data: events.append((name, data))
    return service, events, str(workdir / "data/client.db")


def _run(service, fake, online=True):
    service.bootstrap._token_service = fake
    service._server_online = online
    asyncio.run(service._watch_token_once())


class TokenClaimTest(unittest.TestCase):
    def test_claim_saves_token_and_broadcasts(self):
        service, events, db_path = _make_service()
        fake = FakeTokenService(
            db_path,
            claim_result={"token_id": "t1", "token": "tok-abc"},
            records=[{"token_id": "t1", "status": "approved"}],
        )
        _run(service, fake)

        self.assertEqual(fake.claim_calls, 1)
        self.assertEqual(fake.token_store.load_token(), "tok-abc")
        self.assertEqual(len(events), 1)
        name, data = events[0]
        self.assertEqual(name, "token_status")
        self.assertEqual(data["status"], "已通过，token 已加密保存")

    def test_no_pending_token_does_not_save_or_broadcast(self):
        service, events, db_path = _make_service()
        fake = FakeTokenService(db_path, claim_result=None)
        _run(service, fake)

        self.assertEqual(fake.claim_calls, 1)
        self.assertIsNone(fake.token_store.load_token())
        self.assertEqual(events, [])

    def test_existing_valid_token_is_not_reclaimed(self):
        service, events, db_path = _make_service()
        fake = FakeTokenService(db_path, valid=True)
        fake.token_store.save_token("existing")
        _run(service, fake)

        self.assertEqual(fake.validate_calls, 1)
        self.assertEqual(fake.claim_calls, 0)
        self.assertEqual(fake.token_store.load_token(), "existing")

    def test_invalid_token_is_cleared_for_reclaim(self):
        service, events, db_path = _make_service()
        fake = FakeTokenService(db_path, valid=False)
        fake.token_store.save_token("stale")
        _run(service, fake)

        self.assertIsNone(fake.token_store.load_token())
        self.assertEqual(fake.claim_calls, 0)

    def test_empty_server_url_skips_claim(self):
        service, events, db_path = _make_service(server_url="")
        fake = FakeTokenService(db_path, claim_result={"token_id": "t1", "token": "x"})
        _run(service, fake)

        self.assertEqual(fake.claim_calls, 0)
        self.assertIsNone(fake.token_store.load_token())

    def test_offline_server_skips_claim(self):
        # 心跳离线时静默跳过，避免每轮刷失败日志；恢复后由心跳驱动再领
        service, events, db_path = _make_service()
        fake = FakeTokenService(db_path, claim_result={"token_id": "t1", "token": "x"})
        _run(service, fake, online=False)

        self.assertEqual(fake.claim_calls, 0)
        self.assertIsNone(fake.token_store.load_token())
        self.assertEqual(events, [])


class TokenStatusPayloadTest(unittest.TestCase):
    def _payload(self, fake, **kwargs):
        service, _, _ = _make_service(**kwargs)
        service.bootstrap._token_service = fake
        return asyncio.run(service._token_status_payload())

    def _fake(self, **kwargs):
        return FakeTokenService(str(Path(tempfile.mkdtemp()) / "client.db"), **kwargs)

    def test_pending_record_reports_pending(self):
        fake = self._fake(records=[{"token_id": "t1", "status": "pending"}])
        payload = self._payload(fake)
        self.assertEqual(payload["status"], "待审核")
        self.assertEqual(len(payload["records"]), 1)

    def test_no_records_reports_not_applied(self):
        fake = self._fake(records=[])
        payload = self._payload(fake)
        self.assertEqual(payload["status"], "未申请")

    def test_rejected_record_reports_rejected(self):
        fake = self._fake(records=[{"token_id": "t1", "status": "rejected"}])
        payload = self._payload(fake)
        self.assertEqual(payload["status"], "已拒绝")

    def test_approved_record_triggers_claim(self):
        fake = self._fake(
            claim_result={"token_id": "t1", "token": "tok-xyz"},
            records=[{"token_id": "t1", "status": "approved"}],
        )
        payload = self._payload(fake)
        self.assertEqual(fake.claim_calls, 1)
        self.assertEqual(payload["status"], "已通过，token 已加密保存")
        self.assertEqual(fake.token_store.load_token(), "tok-xyz")

    def test_approved_without_claimable_token_reports_missing(self):
        fake = self._fake(
            claim_result=None,
            records=[{"token_id": "t1", "status": "approved"}],
        )
        payload = self._payload(fake)
        self.assertEqual(payload["status"], "已通过，但服务器未返回 token")

    def test_existing_token_short_circuits_to_saved(self):
        fake = self._fake(records=[{"token_id": "t1", "status": "pending"}])
        fake.token_store.save_token("already")
        payload = self._payload(fake)
        self.assertEqual(payload["status"], "已通过，token 已加密保存")
        self.assertEqual(fake.claim_calls, 0)

    def test_unconfigured_server_reports_plainly(self):
        fake = self._fake()
        payload = self._payload(fake, server_url="")
        self.assertEqual(payload["status"], "未配置服务器")


if __name__ == "__main__":
    unittest.main()
