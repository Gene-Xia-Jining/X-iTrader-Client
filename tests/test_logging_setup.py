"""后端日志落盘的回归测试。

重点不是格式化细节，而是三件会静默失效的事：轮转上限、噪音过滤、
print 与异常真的进文件。
"""

import io
import logging
import sys
import tempfile
import unittest
from pathlib import Path

from src.infrastructure import logging_setup
from src.infrastructure.logging_setup import (
    BACKUP_COUNT,
    MAX_BYTES,
    _NoiseFilter,
    setup_logging,
)


class LoggingSetupTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.dir = Path(self._tmp.name)
        self._saved = (sys.stdout, sys.stderr)
        self.addCleanup(self._restore)
        # 接管前先把原流换成内存流：setup_logging 会 tee 到它，
        # 不换的话测试里的 print 会直接打到 test runner 的终端上
        sys.stdout = io.StringIO()
        sys.stderr = io.StringIO()

    def _restore(self):
        sys.stdout, sys.stderr = self._saved
        # setup_logging 是进程级一次性的，测试间必须复位才能换目录重配
        logging_setup._configured = False
        root = logging.getLogger()
        for h in list(root.handlers):
            root.removeHandler(h)
            h.close()

    def test_print_and_errors_go_to_file(self):
        path = setup_logging(self.dir)
        print("标准输出这条要进文件")
        print("标准错误这条也要进", file=sys.stderr)
        logging.getLogger("event").warning("业务告警")
        logging.shutdown()

        text = path.read_text(encoding="utf-8")
        self.assertIn("标准输出这条要进文件", text)
        self.assertIn("标准错误这条也要进", text)
        self.assertIn("业务告警", text)

    def test_rotates_and_caps_total_size(self):
        path = setup_logging(self.dir)
        log = logging.getLogger("backend")
        # 每次约 200 字节，写满 5MB 需要两万余条；写成整行以便准确计大小
        line = "x" * 200
        for i in range(60000):
            log.info("%s %d", line, i)
        logging.shutdown()

        files = sorted(self.dir.iterdir())
        # 最多 MAX_BYTES 大小的主文件 + BACKUP_COUNT 份备份
        self.assertLessEqual(len(files), BACKUP_COUNT + 1)
        for f in files:
            self.assertLessEqual(f.stat().st_size, MAX_BYTES)
        self.assertTrue(path.exists())

    def test_noise_filter_keeps_warnings(self):
        f = _NoiseFilter()
        info = logging.LogRecord("TqSdk", logging.INFO, "", 0, "x", None, None)
        warn = logging.LogRecord("TqSdk", logging.WARNING, "", 0, "x", None, None)
        app_info = logging.LogRecord("backend", logging.INFO, "", 0, "x", None, None)

        self.assertFalse(f.filter(info), "三方库 INFO 流水不应写盘")
        self.assertTrue(f.filter(warn), "三方库告警必须保留")
        self.assertTrue(f.filter(app_info), "应用自身 INFO 必须保留")

    def test_second_call_does_not_add_handler(self):
        setup_logging(self.dir)
        before = len(logging.getLogger().handlers)
        setup_logging(self.dir)
        self.assertEqual(len(logging.getLogger().handlers), before)

    def test_reentrant_write_does_not_recurse(self):
        """文件 handler 出错时 logging 会回写 sys.stderr——不能被再次转发，否则爆栈。"""
        calls = []

        class ExplodingHandler(logging.Handler):
            def emit(self, record):
                calls.append(record.getMessage())
                # 模拟 handler 内部失败：logging 会把错误写回 sys.stderr
                sys.stderr.write("handler 出错回写\n")

        setup_logging(self.dir)
        root = logging.getLogger()
        root.handlers.clear()
        root.addHandler(ExplodingHandler())

        sys.stderr.write("触发一次\n")  # 正常结束即证明没有无限递归
        self.assertEqual(calls, ["触发一次"])


if __name__ == "__main__":
    unittest.main()
