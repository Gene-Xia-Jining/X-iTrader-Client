"""差量更新后新后端接管 backend.json 的行为。

patch 更新由旧后端进程直接拉起新后端（见 updater.apply_update_and_restart），
旧进程此刻往往还没退完。新后端必须等它释放 backend.json 再接管，而不是撞上
单实例保护直接退出——否则「确认更新」装完就没有后端可连了。
"""

import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from src.backend import rpc
from src.infrastructure.updater import RESTART_ENV


class RestartTakeoverTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.workdir = Path(tmp.name)
        (self.workdir / "data").mkdir()
        self.info = self.workdir / "data" / "backend.json"

    def _server(self):
        return rpc.RpcServer(service=None, routes={}, workdir=self.workdir)

    def _write_info(self, pid):
        self.info.write_text(json.dumps({"pid": pid, "port": 1}))

    def test_alive_pid_rejected_without_restart_env(self):
        self._write_info(os.getpid())
        env = {k: v for k, v in os.environ.items() if k != RESTART_ENV}
        with mock.patch.dict(os.environ, env, clear=True):
            with self.assertRaises(RuntimeError):
                self._server()._reject_stale_backend()
        self.assertTrue(self.info.exists())

    def test_restart_env_waits_for_old_backend_then_takes_over(self):
        self._write_info(os.getpid())
        probes = {"n": 0}

        def fake_kill(pid, sig):
            # 第一次探测旧后端仍在；随后模拟它已退出
            probes["n"] += 1
            if probes["n"] == 1:
                return 0
            raise ProcessLookupError

        with mock.patch.dict(os.environ, {RESTART_ENV: "1"}), \
             mock.patch.object(rpc.os, "kill", side_effect=fake_kill), \
             mock.patch.object(rpc.time, "sleep"):
            self._server()._reject_stale_backend()

        self.assertFalse(self.info.exists())
        self.assertGreaterEqual(probes["n"], 2)

    def test_dead_pid_residue_is_taken_over(self):
        """残留 backend.json（pid 已死）必须被接管，而不是拒绝启动。

        Windows 上曾不探测 pid，残留文件会让每个新后端启动即退出，
        界面表现为「后端进程已退出，正在重启」无限循环。
        """
        self._write_info(999999)
        env = {k: v for k, v in os.environ.items() if k != RESTART_ENV}
        with mock.patch.dict(os.environ, env, clear=True), \
             mock.patch.object(rpc, "_pid_alive", return_value=False):
            self._server()._reject_stale_backend()

        self.assertFalse(self.info.exists())

    def test_alive_pid_is_still_rejected_on_any_platform(self):
        """存活判定必须跨平台生效：真冲突仍要拒绝，不能被残留清理逻辑放过。"""
        self._write_info(os.getpid())
        env = {k: v for k, v in os.environ.items() if k != RESTART_ENV}
        with mock.patch.dict(os.environ, env, clear=True), \
             mock.patch.object(rpc, "_pid_alive", return_value=True):
            with self.assertRaises(RuntimeError):
                self._server()._reject_stale_backend()


if __name__ == "__main__":
    unittest.main()
