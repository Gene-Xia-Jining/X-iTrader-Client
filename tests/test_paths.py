import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from src.infrastructure import paths
from src.infrastructure.paths import (
    APP_DIR_NAME,
    PACKAGED_ENV,
    data_root,
    is_packaged,
    legacy_data_root,
    migrate_legacy_data_root,
)


class LayoutTest(unittest.TestCase):
    """数据根必须落在用户主目录下，且与 .app 安装位置无关。"""

    def test_root_under_home(self):
        with mock.patch.object(Path, "home", return_value=Path("/home/u")):
            self.assertEqual(data_root(), Path("/home/u") / APP_DIR_NAME)

    def test_app_dir_name(self):
        self.assertEqual(APP_DIR_NAME, ".X-iTrader-Client")


class PackagedFlagTest(unittest.TestCase):
    """打包态判定：只有生产入口设置的 ITRADER_PACKAGED=1 才算打包态。

    后端已从 PyInstaller frozen 进程换成嵌入式解释器 + 入口脚本，
    sys.frozen 不再可用，判定统一走环境标记。
    """

    def test_false_without_env(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertFalse(is_packaged())

    def test_true_with_env(self):
        with mock.patch.dict(os.environ, {PACKAGED_ENV: "1"}):
            self.assertTrue(is_packaged())

    def test_other_value_is_not_packaged(self):
        # 只有精确的 "1" 生效，避免误配值把开发态当生产态
        with mock.patch.dict(os.environ, {PACKAGED_ENV: "yes"}):
            self.assertFalse(is_packaged())


class MigrateTest(unittest.TestCase):
    """旧数据根 ~/.iTrader → ~/.X-iTrader-Client 的一次性迁移。"""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.fake_home = Path(self._tmp.name)
        patcher = mock.patch.object(Path, "home", return_value=self.fake_home)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.legacy = legacy_data_root()
        self.new = data_root()

    def test_no_legacy_returns_false_and_creates_nothing(self):
        self.assertFalse(migrate_legacy_data_root())
        self.assertFalse(self.new.exists())

    def test_moves_data_and_removes_empty_legacy(self):
        (self.legacy / "data" / "tokens").mkdir(parents=True)
        (self.legacy / "data" / "client.db").write_text("db")
        (self.legacy / "data" / "tokens" / "t.json").write_text("{}")

        self.assertTrue(migrate_legacy_data_root())

        self.assertEqual((self.new / "data" / "client.db").read_text(), "db")
        self.assertEqual((self.new / "data" / "tokens" / "t.json").read_text(), "{}")
        # 迁移后旧根为空，应被清除
        self.assertFalse(self.legacy.exists())

    def test_does_not_overwrite_existing_new_data(self):
        (self.legacy / "data").mkdir(parents=True)
        (self.legacy / "data" / "client.db").write_text("old")
        (self.new / "data").mkdir(parents=True)
        (self.new / "data" / "client.db").write_text("new")

        migrate_legacy_data_root()

        # 新根已有同名项时跳过，绝不覆盖
        self.assertEqual((self.new / "data" / "client.db").read_text(), "new")
        # 该项未移动，旧根残留非空，故不删除
        self.assertTrue(self.legacy.exists())

    def test_migrates_data_when_runtime_already_exists(self):
        # 引导下载可能先建好 runtime/，此时 data/ 仍须迁入
        (self.new / "runtime" / "backend").mkdir(parents=True)
        (self.legacy / "data").mkdir(parents=True)
        (self.legacy / "data" / "client.db").write_text("db")

        self.assertTrue(migrate_legacy_data_root())

        self.assertEqual((self.new / "data" / "client.db").read_text(), "db")
        self.assertTrue((self.new / "runtime" / "backend").is_dir())

    def test_idempotent(self):
        (self.legacy / "data").mkdir(parents=True)
        (self.legacy / "data" / "client.db").write_text("db")

        self.assertTrue(migrate_legacy_data_root())
        # 第二次调用：旧根已不存在，无副作用
        self.assertFalse(migrate_legacy_data_root())
        self.assertEqual((self.new / "data" / "client.db").read_text(), "db")

    def test_legacy_file_not_directory_is_ignored(self):
        # 旧根路径被占用为文件时不应崩溃
        self.legacy.write_text("not a dir")
        self.assertFalse(migrate_legacy_data_root())


if __name__ == "__main__":
    unittest.main()
