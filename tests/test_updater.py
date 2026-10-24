import asyncio
import contextlib
import os
import plistlib
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

import httpx

from src.infrastructure import updater
from src.infrastructure.updater import (
    RELEASES_API_URL,
    ReleaseInfo,
    apply_update_and_restart,
    cleanup_stale_backups,
    download_release,
    extract_update,
    fetch_latest_release,
    is_newer,
    parse_version,
)


def _make_zip(path: Path, entries: dict) -> None:
    """entries: {zip内路径: 内容字节}；内容为 callable 时按符号链接条目处理。"""
    with zipfile.ZipFile(path, "w") as zf:
        for name, data in entries.items():
            if callable(data):
                info = zipfile.ZipInfo(name)
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
                zf.writestr(info, data())
            else:
                zf.writestr(name, data)


class VersionCompareTest(unittest.TestCase):
    def test_parse_version(self):
        self.assertEqual(parse_version("0.0.2"), (0, 0, 2))
        self.assertEqual(parse_version("v0.1"), (0, 1))
        self.assertEqual(parse_version(" 1.2.3 "), (1, 2, 3))
        self.assertIsNone(parse_version("abc"))
        self.assertIsNone(parse_version(""))

    def test_is_newer(self):
        self.assertTrue(is_newer("0.0.3", "0.0.2"))
        self.assertFalse(is_newer("0.0.2", "0.0.2"))
        self.assertFalse(is_newer("0.0.1", "0.0.2"))
        # 位数不同的按整数比较（不是字符串比较）
        self.assertTrue(is_newer("0.0.10", "0.0.2"))
        self.assertFalse(is_newer("0.0.2", "0.0.10"))
        self.assertTrue(is_newer("0.1", "0.0.2"))
        self.assertTrue(is_newer("1.0.0", "1.0"))
        self.assertTrue(is_newer("v0.0.3", "0.0.2"))
        # 任一版本非法时保守视为不更新
        self.assertFalse(is_newer("bad", "0.0.2"))
        self.assertFalse(is_newer("0.0.3", ""))


class SelectAssetsTest(unittest.TestCase):
    ASSETS = [
        {"name": "x-itrader-client-0.0.3-macos.dmg", "browser_download_url": "https://x/macos.dmg"},
        {"name": "x-itrader-client-0.0.3-windows.zip", "browser_download_url": "https://x/win.zip"},
        {"name": "x-itrader-client-0.0.3-src.zip", "browser_download_url": "https://x/src.zip"},
    ]

    def test_darwin_selects_macos_dmg_and_patch(self):
        with mock.patch.object(sys, "platform", "darwin"):
            full, patch = updater._select_assets(self.ASSETS)
        self.assertEqual(full, "https://x/macos.dmg")
        self.assertEqual(patch, "https://x/src.zip")

    def test_windows_selects_windows_zip_and_patch(self):
        with mock.patch.object(sys, "platform", "win32"):
            full, patch = updater._select_assets(self.ASSETS)
        self.assertEqual(full, "https://x/win.zip")
        self.assertEqual(patch, "https://x/src.zip")

    def test_unknown_platform_has_no_full(self):
        with mock.patch.object(sys, "platform", "linux"):
            full, patch = updater._select_assets(self.ASSETS)
        self.assertEqual(full, "")
        self.assertEqual(patch, "https://x/src.zip")

    def test_darwin_does_not_treat_zip_full_as_patch(self):
        # 若 Release 只有 zip 全量包（旧命名），darwin 不应把它当成补丁包
        assets = [
            {"name": "x-itrader-client-0.0.3-macos.zip", "browser_download_url": "https://x/macos.zip"},
        ]
        with mock.patch.object(sys, "platform", "darwin"):
            full, patch = updater._select_assets(assets)
        self.assertEqual(full, "")
        self.assertEqual(patch, "")


class FetchLatestReleaseTest(unittest.TestCase):
    @staticmethod
    def _payload(assets=None, tag="0.0.3"):
        return {
            "tag_name": tag,
            "body": "release notes",
            "assets": assets
            if assets is not None
            else [
                {"name": "x-itrader-client-0.0.3-macos.dmg", "browser_download_url": "https://x/macos.dmg"},
                {"name": "x-itrader-client-0.0.3-windows.zip", "browser_download_url": "https://x/win.zip"},
                {"name": "x-itrader-client-0.0.3-src.zip", "browser_download_url": "https://x/src.zip"},
            ],
        }

    def setUp(self):
        # 这些用例只验证 GitHub 分支：关掉默认本地源，避免开发者本机的安装包混入结果
        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        stack.enter_context(mock.patch.object(updater, "DEFAULT_LOCAL_SOURCE", ""))

    def _fetch(self, payload, status=200):
        def handler(request: httpx.Request) -> httpx.Response:
            assert str(request.url) == RELEASES_API_URL
            return httpx.Response(status, json=payload)

        return asyncio.run(fetch_latest_release(transport=httpx.MockTransport(handler)))

    def test_parses_release_with_patch(self):
        with mock.patch.object(sys, "platform", "darwin"):
            release = self._fetch(self._payload())
        self.assertIsInstance(release, ReleaseInfo)
        self.assertEqual(release.version, "0.0.3")
        self.assertEqual(release.notes, "release notes")
        self.assertEqual(release.full_url, "https://x/macos.dmg")
        self.assertTrue(release.has_patch)
        self.assertEqual(release.patch_url, "https://x/src.zip")

    def test_returns_none_without_platform_asset(self):
        with mock.patch.object(sys, "platform", "linux"):
            release = self._fetch(self._payload())
        self.assertIsNone(release)

    def test_returns_none_without_tag(self):
        with mock.patch.object(sys, "platform", "darwin"):
            release = self._fetch(self._payload(assets=[], tag=""))
        self.assertIsNone(release)

    def test_http_error_propagates(self):
        with mock.patch.object(sys, "platform", "darwin"):
            with self.assertRaises(httpx.HTTPStatusError):
                self._fetch({}, status=404)


class ExtractUpdateTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.stage = self.dir / "update"
        self.zip_path = self.dir / "update.zip"

    def tearDown(self):
        self._tmp.cleanup()

    def test_mac_full_zip(self):
        _make_zip(self.zip_path, {
            "X-iTrader-Client.app/Contents/Info.plist": b"plist",
            "X-iTrader-Client.app/Contents/MacOS/X-iTrader-Client": b"MZ",
        })
        item = extract_update(self.zip_path, self.stage)
        self.assertEqual(item, self.stage / "stage" / "X-iTrader-Client.app")
        self.assertTrue((item / "Contents" / "MacOS" / "X-iTrader-Client").is_file())

    def test_windows_full_zip(self):
        # Windows 全量包是扁平布局：壳与后端可执行文件同根级，_internal/ 放后端依赖
        _make_zip(self.zip_path, {
            "X-iTrader.exe": b"MZ",
            "X-iTrader-Backend.exe": b"MZ",
            "_internal/src/__init__.py": b"x",
        })
        item = extract_update(self.zip_path, self.stage)
        self.assertEqual(item, self.stage / "stage")
        self.assertTrue((item / "_internal" / "src" / "__init__.py").is_file())

    def test_patch_zip(self):
        _make_zip(self.zip_path, {
            "src/__init__.py": b"v2",
            "src/infrastructure/updater.py": b"x",
        })
        item = extract_update(self.zip_path, self.stage)
        self.assertEqual(item, self.stage / "stage" / "src")
        self.assertTrue((item / "__init__.py").is_file())

    def test_invalid_zip_raises(self):
        _make_zip(self.zip_path, {"random.txt": b"x"})
        with self.assertRaises(ValueError):
            extract_update(self.zip_path, self.stage)

    def test_zip_slip_rejected(self):
        _make_zip(self.zip_path, {
            "X-iTrader-Client.app/Contents/Info.plist": b"plist",
            "../evil.txt": b"evil",
        })
        with self.assertRaises(ValueError) as cm:
            extract_update(self.zip_path, self.stage)
        self.assertIn("非法路径", str(cm.exception))

    def test_symlink_entry_restored(self):
        _make_zip(self.zip_path, {
            "X-iTrader-Client.app/Contents/Info.plist": b"plist",
            "X-iTrader-Client.app/Contents/Resources/target.txt": b"data",
            "X-iTrader-Client.app/Contents/Resources/link": lambda: "target.txt",
        })
        item = extract_update(self.zip_path, self.stage)
        link = item / "Contents" / "Resources" / "link"
        self.assertTrue(link.is_symlink())
        self.assertEqual(os.readlink(link), "target.txt")


class ExtractDmgTest(unittest.TestCase):
    """dmg 分派：mock hdiutil/ditto，验证挂载→复制→卸载的调用与错误处理。"""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.stage = self.dir / "update"
        self.dmg_path = self.dir / "x-itrader-client-0.0.3-macos.dmg"
        self.dmg_path.write_bytes(b"dmg")

    def tearDown(self):
        self._tmp.cleanup()

    def _attach_stdout(self, mount_point: str) -> bytes:
        return plistlib.dumps({
            "system-entities": [
                {"mount-point": mount_point},
            ]
        })

    def test_dmg_routes_to_hdiutil_and_ditto(self):
        mount = self.dir / "vol"
        # 真实 dmg 挂载点内含 X-iTrader-Client.app（加 Applications 快捷方式）
        (mount / "X-iTrader-Client.app").mkdir(parents=True)

        def fake_run(cmd, **kwargs):
            if cmd[0] == "hdiutil" and cmd[1] == "attach":
                return subprocess.CompletedProcess(cmd, 0, self._attach_stdout(str(mount)), "")
            if cmd[0] == "ditto":
                # 模拟 ditto 复制出 .app
                (Path(cmd[2]) / "Contents").mkdir(parents=True)
                (Path(cmd[2]) / "Contents" / "Info.plist").write_text("plist")
                return subprocess.CompletedProcess(cmd, 0, "", "")
            if cmd[0] == "hdiutil" and cmd[1] == "detach":
                return subprocess.CompletedProcess(cmd, 0, "", "")
            if cmd[0] == "xattr":
                return subprocess.CompletedProcess(cmd, 0, "", "")
            raise AssertionError(f"未预期的命令: {cmd}")

        with mock.patch.object(updater.subprocess, "run", side_effect=fake_run):
            item = extract_update(self.dmg_path, self.stage)

        self.assertEqual(item, self.stage / "stage" / "X-iTrader-Client.app")
        self.assertTrue((item / "Contents" / "Info.plist").is_file())

    def test_dmg_detaches_even_when_app_missing(self):
        mount = self.dir / "vol"
        mount.mkdir()  # 挂载点内没有 .app
        detach_calls = []

        def fake_run(cmd, **kwargs):
            if cmd[0] == "hdiutil" and cmd[1] == "attach":
                return subprocess.CompletedProcess(cmd, 0, self._attach_stdout(str(mount)), "")
            if cmd[0] == "hdiutil" and cmd[1] == "detach":
                detach_calls.append(cmd)
                return subprocess.CompletedProcess(cmd, 0, "", "")
            raise AssertionError(f"未预期的命令: {cmd}")

        with mock.patch.object(updater.subprocess, "run", side_effect=fake_run):
            with self.assertRaises(ValueError):
                extract_update(self.dmg_path, self.stage)

        # 异常路径也必须卸载，避免残留挂载
        self.assertEqual(len(detach_calls), 1)


class ApplyUpdateTest(unittest.TestCase):
    """frozen 态替换逻辑：mock sys/Popen，在 tmp 目录内真实执行文件操作。"""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def _frozen(self, platform: str, executable: Path, meipass: Path | None = None):
        """进入 frozen 环境模拟，返回 Popen mock。"""
        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        stack.enter_context(mock.patch.object(updater, "is_frozen", return_value=True))
        stack.enter_context(mock.patch.object(sys, "platform", platform))
        if meipass is not None:
            stack.enter_context(
                mock.patch.object(sys, "_MEIPASS", str(meipass), create=True)
            )
        stack.enter_context(mock.patch.object(sys, "executable", str(executable)))
        return stack.enter_context(mock.patch.object(updater.subprocess, "Popen"))

    def test_non_frozen_rejected(self):
        item = self.dir / "stage" / "src"
        item.mkdir(parents=True)
        with self.assertRaises(RuntimeError):
            apply_update_and_restart(item, "patch")

    def test_full_update_darwin(self):
        app = self.dir / "Applications" / "X-iTrader-Client.app"
        exe = app / "Contents" / "MacOS" / "X-iTrader-Client"
        exe.parent.mkdir(parents=True)
        exe.write_text("old")
        new_app = self.dir / "stage" / "X-iTrader-Client.app"
        (new_app / "Contents" / "MacOS").mkdir(parents=True)
        (new_app / "Contents" / "MacOS" / "X-iTrader-Client").write_text("new")

        popen = self._frozen("darwin", exe)
        binary = apply_update_and_restart(new_app, "full")

        # macOS 的 /var 是 /private/var 符号链接，路径统一用 resolve 后比较
        self.assertEqual(binary.resolve(), (app / "Contents" / "MacOS" / "X-iTrader-Client").resolve())
        self.assertEqual((app / "Contents" / "MacOS" / "X-iTrader-Client").read_text(), "new")
        # 旧 .app 改名让位保留，供新进程启动时清理
        backups = list(self.dir.glob("Applications/X-iTrader-Client.app.old-*"))
        self.assertEqual(len(backups), 1)
        self.assertEqual((backups[0] / "Contents" / "MacOS" / "X-iTrader-Client").read_text(), "old")
        popen.assert_called_once()
        cmd_args, kwargs = popen.call_args
        self.assertIn("/usr/bin/open -n", cmd_args[0][2])
        self.assertTrue(kwargs.get("start_new_session"))
        self.assertTrue(kwargs.get("close_fds"))

    def test_full_update_windows(self):
        # Windows 扁平布局：后端 X-iTrader-Backend.exe 的父目录即安装根，
        # 根级必须有壳 X-iTrader.exe（全量更新时被替换、之后作为重启目标）
        progdir = self.dir / "X-iTrader-Client"
        backend_exe = progdir / "X-iTrader-Backend.exe"
        shell_exe = progdir / "X-iTrader.exe"
        progdir.mkdir()
        backend_exe.write_bytes(b"old-backend")
        shell_exe.write_bytes(b"old-shell")
        (progdir / "_internal").mkdir()
        new_dir = self.dir / "stage"
        new_dir.mkdir()
        (new_dir / "X-iTrader.exe").write_bytes(b"new-shell")
        (new_dir / "X-iTrader-Backend.exe").write_bytes(b"new-backend")
        (new_dir / "_internal").mkdir()

        # sys.executable 是后端进程；安装根取其父目录
        popen = self._frozen("win32", backend_exe)
        binary = apply_update_and_restart(new_dir, "full")

        self.assertEqual(binary.resolve(), shell_exe.resolve())
        self.assertEqual(shell_exe.read_bytes(), b"new-shell")
        backups = list(self.dir.glob("X-iTrader-Client.old-*"))
        self.assertEqual(len(backups), 1)
        popen.assert_called_once()
        cmd_args, kwargs = popen.call_args
        self.assertIn("X-iTrader.exe", cmd_args[0])
        self.assertTrue(kwargs.get("shell"))

    def test_patch_update_replaces_src_only(self):
        meipass = self.dir / "meipass"
        src = meipass / "src"
        src.mkdir(parents=True)
        (src / "__init__.py").write_text("old")
        exe = self.dir / "app" / "iTrader"
        exe.parent.mkdir(parents=True)
        exe.write_text("binary")
        new_src = self.dir / "stage" / "src"
        new_src.mkdir(parents=True)
        (new_src / "__init__.py").write_text("new")

        popen = self._frozen("darwin", exe, meipass=meipass)
        binary = apply_update_and_restart(new_src, "patch")

        self.assertEqual(binary, exe)
        self.assertEqual((src / "__init__.py").read_text(), "new")
        backups = list(meipass.glob("src.old-*"))
        self.assertEqual(len(backups), 1)
        self.assertEqual((backups[0] / "__init__.py").read_text(), "old")
        popen.assert_called_once_with(
            [str(exe)],
            cwd=str(exe.parent),
            close_fds=True,
            # 差量更新由旧后端直接拉起新后端：新进程要跳过父进程监控并等待接管
            env=mock.ANY,
        )
        args, kwargs = popen.call_args
        self.assertEqual(kwargs["env"][updater.RESTART_ENV], "1")

    def test_swap_failure_rolls_back(self):
        app = self.dir / "X-iTrader-Client.app"
        exe = app / "Contents" / "MacOS" / "X-iTrader-Client"
        exe.parent.mkdir(parents=True)
        exe.write_text("old")
        # 新包缺 Contents/MacOS/X-iTrader-Client 导致 _launch_binary_of 抛错，应回滚
        new_app = self.dir / "stage" / "X-iTrader-Client.app"
        new_app.mkdir(parents=True)

        popen = self._frozen("darwin", exe)
        with self.assertRaises(RuntimeError):
            apply_update_and_restart(new_app, "full")

        self.assertEqual(exe.read_text(), "old")
        self.assertFalse(list(self.dir.glob("Applications/*.old-*")))
        popen.assert_not_called()


class CleanupStaleBackupsTest(unittest.TestCase):
    def test_removes_old_backups_darwin(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name) / "Applications"
        app = root / "X-iTrader-Client.app"
        (app / "Contents").mkdir(parents=True)
        stale = root / "X-iTrader-Client.app.old-123"
        stale.mkdir()
        (stale / "junk").write_text("x")

        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        stack.enter_context(mock.patch.object(sys, "platform", "darwin"))
        stack.enter_context(
            mock.patch.object(sys, "executable", str(app / "Contents" / "MacOS" / "X-iTrader-Client"))
        )
        stack.enter_context(mock.patch.object(updater, "is_frozen", return_value=True))
        updater.cleanup_stale_backups()

        self.assertFalse(stale.exists())
        self.assertTrue(app.exists())

    def test_removes_old_backups_windows(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name) / "Programs"
        progdir = root / "X-iTrader-Client"
        progdir.mkdir(parents=True)
        exe = progdir / "X-iTrader-Backend.exe"
        exe.write_bytes(b"MZ")
        stale = root / "X-iTrader-Client.old-456"
        stale.mkdir()
        (stale / "junk").write_text("x")

        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        stack.enter_context(mock.patch.object(sys, "platform", "win32"))
        stack.enter_context(
            mock.patch.object(sys, "executable", str(exe))
        )
        stack.enter_context(mock.patch.object(updater, "is_frozen", return_value=True))
        updater.cleanup_stale_backups()

        self.assertFalse(stale.exists())
        self.assertTrue(progdir.exists())


class LocalSourceTest(unittest.TestCase):
    """本地目录更新源：路径识别、包扫描与本地文件复制。"""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_is_local_source(self):
        self.assertTrue(updater.is_local_source("/tmp/releases"))
        self.assertTrue(updater.is_local_source("~/releases"))
        self.assertTrue(updater.is_local_source("./releases"))
        self.assertTrue(updater.is_local_source("releases"))
        self.assertTrue(updater.is_local_source(str(self.dir)))
        # owner/repo 形式才当作 GitHub 仓库
        self.assertFalse(updater.is_local_source("Gene-Xia-Jining/X-iTrader-Client"))
        self.assertFalse(updater.is_local_source(""))

    def test_find_latest_picks_highest_version(self):
        (self.dir / "x-itrader-client-0.6.1-macos.dmg").write_bytes(b"old")
        (self.dir / "x-itrader-client-0.6.10-macos.dmg").write_bytes(b"new")
        with mock.patch.object(sys, "platform", "darwin"):
            release = updater.find_latest_in_folder(self.dir)
        # 按整数元组比较，0.6.10 高于 0.6.1
        self.assertEqual(release.version, "0.6.10")
        self.assertEqual(release.full_url, str((self.dir / "x-itrader-client-0.6.10-macos.dmg").resolve()))
        self.assertFalse(release.has_patch)

    def test_find_latest_pairs_patch_with_full(self):
        (self.dir / "x-itrader-client-0.6.1-macos.dmg").write_bytes(b"full")
        (self.dir / "x-itrader-client-0.6.1-src.zip").write_bytes(b"patch")
        with mock.patch.object(sys, "platform", "darwin"):
            release = updater.find_latest_in_folder(self.dir)
        self.assertEqual(release.version, "0.6.1")
        self.assertTrue(release.has_patch)
        self.assertTrue(release.patch_url.endswith("-src.zip"))

    def test_find_latest_platform_suffix(self):
        # Windows 只认 zip 全量包，macOS 的 dmg 不参与匹配
        (self.dir / "x-itrader-client-0.6.1-macos.dmg").write_bytes(b"mac")
        (self.dir / "x-itrader-client-0.6.2-windows.zip").write_bytes(b"win")
        with mock.patch.object(sys, "platform", "win32"):
            release = updater.find_latest_in_folder(self.dir)
        self.assertEqual(release.version, "0.6.2")

    def test_find_latest_empty_folder(self):
        self.assertIsNone(updater.find_latest_in_folder(self.dir))
        self.assertIsNone(updater.find_latest_in_folder(self.dir / "missing"))

    def test_fetch_from_local_folder(self):
        dmg = self.dir / "x-itrader-client-0.6.2-macos.dmg"
        dmg.write_bytes(b"dmg")
        with mock.patch.object(sys, "platform", "darwin"):
            release = asyncio.run(fetch_latest_release(source=str(self.dir)))
        self.assertEqual(release.version, "0.6.2")
        self.assertEqual(release.full_url, str(dmg.resolve()))

    def test_fetch_missing_local_folder_raises(self):
        with self.assertRaises(FileNotFoundError):
            asyncio.run(fetch_latest_release(source=str(self.dir / "missing")))

    def test_fetch_env_source_overrides_default(self):
        dmg = self.dir / "x-itrader-client-0.6.3-macos.dmg"
        dmg.write_bytes(b"dmg")
        with mock.patch.dict(os.environ, {"ITRADER_UPDATE_SOURCE": str(self.dir)}):
            with mock.patch.object(sys, "platform", "darwin"):
                release = asyncio.run(fetch_latest_release())
        self.assertEqual(release.version, "0.6.3")

    def test_download_copies_local_file(self):
        src = self.dir / "x-itrader-client-0.6.2-macos.dmg"
        src.write_bytes(b"dmg-content")
        stage = self.dir / "stage"
        progress = []
        archive = asyncio.run(
            download_release(str(src), stage, progress_cb=lambda rcv, total: progress.append((rcv, total)))
        )
        self.assertEqual(archive, stage / "update.dmg")
        self.assertEqual(archive.read_bytes(), b"dmg-content")
        self.assertEqual(progress, [(1, 1)])


class DualSourceFetchTest(unittest.TestCase):
    """未显式指定更新源时：本地目录与 GitHub 同时查，取版本更高者。"""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        self.addCleanup(self._tmp.cleanup)
        stack.enter_context(mock.patch.object(updater, "DEFAULT_LOCAL_SOURCE", str(self.dir)))
        stack.enter_context(mock.patch.object(sys, "platform", "darwin"))
        stack.enter_context(mock.patch.dict(os.environ, {}, clear=False))
        os.environ.pop("ITRADER_UPDATE_SOURCE", None)

    def _fetch(self, payload, status=200):
        def handler(request: httpx.Request) -> httpx.Response:
            return httpx.Response(status, json=payload)

        return asyncio.run(fetch_latest_release(transport=httpx.MockTransport(handler)))

    @staticmethod
    def _remote(version: str) -> dict:
        return {
            "tag_name": version,
            "body": "remote notes",
            "assets": [
                {
                    "name": f"x-itrader-client-{version}-macos.dmg",
                    "browser_download_url": f"https://x/{version}.dmg",
                }
            ],
        }

    def _local(self, version: str) -> Path:
        path = self.dir / f"x-itrader-client-{version}-macos.dmg"
        path.write_bytes(b"dmg")
        return path

    def test_remote_newer_wins_over_local(self):
        # 关键回归：本地残留旧包不能遮住线上新版本
        self._local("0.6.1")
        release = self._fetch(self._remote("0.6.2"))
        self.assertEqual(release.version, "0.6.2")
        self.assertEqual(release.full_url, "https://x/0.6.2.dmg")

    def test_local_newer_wins_over_remote(self):
        local = self._local("0.7.0")
        release = self._fetch(self._remote("0.6.2"))
        self.assertEqual(release.version, "0.7.0")
        self.assertEqual(release.full_url, str(local.resolve()))

    def test_same_version_prefers_local(self):
        local = self._local("0.6.2")
        release = self._fetch(self._remote("0.6.2"))
        self.assertEqual(release.version, "0.6.2")
        self.assertEqual(release.full_url, str(local.resolve()))

    def test_no_local_package_falls_back_to_remote(self):
        release = self._fetch(self._remote("0.6.2"))
        self.assertEqual(release.version, "0.6.2")

    def test_github_error_falls_back_to_local(self):
        local = self._local("0.6.1")
        release = self._fetch({}, status=403)
        self.assertEqual(release.version, "0.6.1")
        self.assertEqual(release.full_url, str(local.resolve()))

    def test_github_error_raises_when_no_local(self):
        with self.assertRaises(httpx.HTTPStatusError):
            self._fetch({}, status=403)

    def test_both_empty_returns_none(self):
        self.assertIsNone(self._fetch({"tag_name": "", "assets": []}))


class DevModeFullUpdateTest(unittest.TestCase):
    """开发态（非 frozen）下，若能定位上级目录已安装的 .app，则允许全量更新。"""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_dev_full_update_swaps_sibling_app(self):
        # 模拟 src/infrastructure/updater.py -> 仓库根 -> 上一级/X-iTrader-Client.app
        repo = self.dir / "X-iTrader-Client"
        fake_module = repo / "src" / "infrastructure" / "updater.py"
        fake_module.parent.mkdir(parents=True)
        fake_module.write_text("x")
        app = self.dir / "X-iTrader-Client.app"
        old_exe = app / "Contents" / "MacOS" / "X-iTrader-Client"
        old_exe.parent.mkdir(parents=True)
        old_exe.write_text("old")

        new_app = self.dir / "stage" / "X-iTrader-Client.app"
        (new_app / "Contents" / "MacOS").mkdir(parents=True)
        (new_app / "Contents" / "MacOS" / "X-iTrader-Client").write_text("new")

        with (
            mock.patch.object(updater, "is_frozen", return_value=False),
            mock.patch.object(sys, "platform", "darwin"),
            mock.patch.object(updater, "__file__", str(fake_module)),
            mock.patch.object(updater.subprocess, "Popen") as popen,
        ):
            binary = apply_update_and_restart(new_app, "full")

        self.assertEqual(old_exe.read_text(), "new")
        self.assertEqual(binary.resolve(), old_exe.resolve())
        popen.assert_called_once()

    def test_dev_patch_still_rejected(self):
        item = self.dir / "stage" / "src"
        item.mkdir(parents=True)
        with mock.patch.object(updater, "is_frozen", return_value=False):
            with self.assertRaises(RuntimeError):
                apply_update_and_restart(item, "patch")


class ReleaseInfoTest(unittest.TestCase):
    def test_has_patch(self):
        self.assertTrue(ReleaseInfo("0.0.3", "", "full", "patch").has_patch)
        self.assertFalse(ReleaseInfo("0.0.3", "", "full").has_patch)


if __name__ == "__main__":
    unittest.main()
