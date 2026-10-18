import sqlite3
import tempfile
import unittest
from pathlib import Path

from cryptography.fernet import Fernet

from src.infrastructure.api.db_token_store import DbTokenStore
from src.infrastructure.config.config_service import ConfigService
from src.infrastructure.security.crypto import PasswordCipher
from tests.test_crypto import _make_account


class DbTokenStoreTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.db_path = self.dir / "client.db"
        self.store = DbTokenStore(str(self.db_path))

    def tearDown(self):
        self._tmp.cleanup()

    def _stored(self) -> str:
        conn = sqlite3.connect(self.db_path)
        try:
            return conn.execute("SELECT token FROM api_token WHERE id = 1").fetchone()[0]
        finally:
            conn.close()

    def test_save_load_roundtrip(self):
        self.assertFalse(self.store.have_token())
        self.assertIsNone(self.store.load_token())
        self.store.save_token("token-abc")
        self.assertTrue(self.store.have_token())
        self.assertEqual(self.store.load_token(), "token-abc")

    def test_token_stored_encrypted(self):
        self.store.save_token("secret-token")
        stored = self._stored()
        self.assertTrue(stored.startswith("enc:v1:"))
        self.assertNotIn("secret-token", stored)

    def test_delete_token(self):
        self.store.save_token("token-abc")
        self.store.delete_token()
        self.assertIsNone(self.store.load_token())
        self.assertFalse(self.store.have_token())

    def test_load_returns_none_when_key_lost(self):
        self.store.save_token("token-abc")
        # 用另一把密钥重置库中密文，模拟密钥丢失
        other = PasswordCipher(Fernet.generate_key())
        conn = sqlite3.connect(self.db_path)
        conn.execute("UPDATE api_token SET token = ?", (other.encrypt("x"),))
        conn.commit()
        conn.close()
        self.assertIsNone(self.store.load_token())

    def test_migrates_legacy_file(self):
        legacy_dir = self.dir / "tokens"
        legacy_dir.mkdir()
        (legacy_dir / "api_token.txt").write_text("legacy-token\n", encoding="utf-8")
        migrated = DbTokenStore(str(self.db_path))
        self.assertEqual(migrated.load_token(), "legacy-token")
        self.assertFalse((legacy_dir / "api_token.txt").exists())
        # 目录空则一并清理
        self.assertFalse(legacy_dir.exists())

    def test_migration_does_not_overwrite_existing_token(self):
        self.store.save_token("new-token")
        legacy_dir = self.dir / "tokens"
        legacy_dir.mkdir()
        (legacy_dir / "api_token.txt").write_text("old-token", encoding="utf-8")
        DbTokenStore(str(self.db_path))
        self.assertEqual(self.store.load_token(), "new-token")

    def test_shares_key_with_config_service(self):
        # 同目录的 ConfigService 与 DbTokenStore 共享 .secret.key，互不影响
        config_service = ConfigService(str(self.db_path))
        config_service.save_account(_make_account())
        self.store.save_token("token-abc")
        self.assertEqual(config_service.get_account("acc-1").tq_password, "tq-pwd")
        self.assertEqual(self.store.load_token(), "token-abc")
        self.assertTrue((self.dir / ".secret.key").exists())

    # ---- 切换用的凭据缓存 ----

    def test_save_token_records_source_id(self):
        self.store.save_token("tok-1", "rec-1")
        self.assertEqual(self.store.load_token_id(), "rec-1")
        self.assertEqual(self.store.cached_token_ids(), {"rec-1"})

    def test_activate_cached_token_switches_back(self):
        self.store.save_token("tok-1", "rec-1")
        self.store.save_token("tok-2", "rec-2")
        self.assertEqual(self.store.load_token_id(), "rec-2")

        self.assertTrue(self.store.activate_cached_token("rec-1"))
        self.assertEqual(self.store.load_token(), "tok-1")
        self.assertEqual(self.store.load_token_id(), "rec-1")

    def test_activate_cached_token_without_cache_returns_false(self):
        self.assertFalse(self.store.activate_cached_token("rec-x"))
        self.assertIsNone(self.store.load_token())

    def test_cached_token_stored_encrypted(self):
        self.store.save_token("secret-token", "rec-1")
        conn = sqlite3.connect(self.db_path)
        try:
            stored = conn.execute(
                "SELECT token FROM api_token_history WHERE token_id = 'rec-1'"
            ).fetchone()[0]
        finally:
            conn.close()
        self.assertTrue(stored.startswith("enc:v1:"))
        self.assertNotIn("secret-token", stored)

    def test_delete_cached_token(self):
        self.store.save_token("tok-1", "rec-1")
        self.store.delete_cached_token("rec-1")
        self.assertEqual(self.store.cached_token_ids(), set())
        self.assertFalse(self.store.activate_cached_token("rec-1"))


if __name__ == "__main__":
    unittest.main()
