"""设备身份测试：稳定 device_id + device_key 加密存储。

对应服务端 doc/IDENTITY.md 的 R1 / R2 验收点。
"""
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from src.infrastructure.security import device_identity
from src.infrastructure.security.device_identity import (
    DeviceIdentityStore,
    _derive_from_seed,
    load_or_create_device_id,
    read_legacy_client_id,
)


class DeviceIdTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="itrade-dev-"))
        self.path = self.tmp / "device_id"

    def test_first_call_creates_file(self):
        device_id = load_or_create_device_id(self.path)
        self.assertTrue(self.path.exists())
        self.assertEqual(len(device_id), 32)

    def test_stable_across_calls(self):
        first = load_or_create_device_id(self.path)
        second = load_or_create_device_id(self.path)
        self.assertEqual(first, second)

    def test_survives_file_deletion_when_platform_uuid_available(self):
        """R1 核心：删掉文件不等于换身份——由硬件标识派生出同一个值。"""
        with mock.patch.object(device_identity, "platform_uuid", return_value="HW-UUID-1"):
            first = load_or_create_device_id(self.path)
            self.path.unlink()
            second = load_or_create_device_id(self.path)
        self.assertEqual(first, second)

    def test_derived_value_matches_seed_deterministically(self):
        self.assertEqual(_derive_from_seed("abc"), _derive_from_seed("abc"))
        self.assertNotEqual(_derive_from_seed("abc"), _derive_from_seed("abd"))

    def test_random_when_no_platform_uuid(self):
        """没有硬件标识时退回随机 uuid4 全量（128 bit），不再退化成 32 bit。"""
        with mock.patch.object(device_identity, "platform_uuid", return_value=None):
            first = load_or_create_device_id(self.path)
            self.path.unlink()
            second = load_or_create_device_id(self.path)
        self.assertNotEqual(first, second)
        self.assertEqual(len(first), 32)

    def test_device_id_is_not_plain_hardware_uuid(self):
        """硬件标识只作种子，绝不上送原值（隐私 + 可伪造）。"""
        with mock.patch.object(device_identity, "platform_uuid", return_value="HW-UUID-1"):
            device_id = load_or_create_device_id(self.path)
        self.assertNotIn("HW-UUID-1", device_id)
        self.assertNotEqual(device_id, "HW-UUID-1")

    def test_file_mode_is_private(self):
        load_or_create_device_id(self.path)
        mode = self.path.stat().st_mode & 0o777
        self.assertEqual(mode, 0o600)


class LegacyClientIdTest(unittest.TestCase):
    def test_none_when_missing(self):
        cwd = os.getcwd()
        tmp = tempfile.mkdtemp()
        os.chdir(tmp)
        try:
            self.assertIsNone(read_legacy_client_id())
        finally:
            os.chdir(cwd)

    def test_reads_existing_file(self):
        cwd = os.getcwd()
        tmp = Path(tempfile.mkdtemp())
        os.chdir(tmp)
        try:
            (tmp / "data").mkdir()
            (tmp / "data" / "client_id.txt").write_text("client-abc12345\n", encoding="utf-8")
            self.assertEqual(read_legacy_client_id(), "client-abc12345")
        finally:
            os.chdir(cwd)


class DeviceIdentityStoreTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="itrade-store-"))
        self.db = str(self.tmp / "client.db")
        self.store = DeviceIdentityStore(self.db)

    def test_empty_by_default(self):
        self.assertEqual(self.store.load(), (None, None))

    def test_roundtrip_encrypted(self):
        self.store.save("dev-1", "secret-key")
        device_id, device_key = self.store.load()
        self.assertEqual(device_id, "dev-1")
        self.assertEqual(device_key, "secret-key")

    def test_key_not_stored_in_plaintext(self):
        import sqlite3
        self.store.save("dev-1", "plain-secret")
        with sqlite3.connect(self.db) as db:
            raw = db.execute("SELECT device_key FROM device_identity").fetchone()[0]
        self.assertNotIn("plain-secret", raw)

    def test_save_without_key_keeps_existing(self):
        """服务器不重发密钥时（重复登记），本地密钥不能被抹掉。"""
        self.store.save("dev-1", "secret-key")
        self.store.save("dev-1", None)
        _, device_key = self.store.load()
        self.assertEqual(device_key, "secret-key")

    def test_new_device_id_drops_old_key(self):
        """换了 device_id 就不能沿用旧密钥（配错设备会被服务器拒绝）。"""
        self.store.save("dev-1", "secret-key")
        self.store.save("dev-2", None)
        device_id, device_key = self.store.load()
        self.assertEqual(device_id, "dev-2")
        self.assertIsNone(device_key)

    def test_clear(self):
        self.store.save("dev-1", "secret-key")
        self.store.clear()
        self.assertEqual(self.store.load(), (None, None))


if __name__ == "__main__":
    unittest.main()
