"""TqSdkTradingExecutor.close 的幂等与释放语义。

不连接天勤：直接构造无会话实例，只验证 close 自身的状态机。
"""

import unittest

from src.infrastructure.trading.tqsdk_executor import TqSdkTradingExecutor


class ExecutorCloseTest(unittest.TestCase):
    def _executor(self) -> TqSdkTradingExecutor:
        return TqSdkTradingExecutor(accounts=[], auths={})

    def test_close_is_idempotent(self):
        executor = self._executor()
        executor.close()
        executor.close()
        self.assertTrue(executor._closed)

    def test_close_dispatches_before_marking_closed(self):
        """close 必须先投递关闭调用再置 _closed，否则 _submit 会拒绝自己。"""
        executor = self._executor()
        seen = []

        class FakeSession:
            account_id = "acc"
            api = object()

        executor._sessions = {"acc": FakeSession()}
        executor._submit = lambda account_id, fn, timeout=None: seen.append(account_id)
        executor.close()
        self.assertEqual(seen, ["acc"])

    def test_close_continues_after_one_failure(self):
        executor = self._executor()

        class FakeSession:
            def __init__(self, account_id):
                self.account_id = account_id
                self.api = object()

        executor._sessions = {"a": FakeSession("a"), "b": FakeSession("b")}
        attempts = []

        def submit(account_id, fn, timeout=None):
            attempts.append(account_id)
            if account_id == "a":
                raise RuntimeError("boom")

        executor._submit = submit
        executor.close()
        self.assertEqual(attempts, ["a", "b"])
        self.assertTrue(executor._closed)

    def test_submit_rejected_after_close(self):
        executor = self._executor()
        executor.close()
        with self.assertRaises(RuntimeError):
            executor._submit("acc", lambda: None)


if __name__ == "__main__":
    unittest.main()
