"""BackendService.trading_records 的账户筛选与错误隔离。

不连接天勤：替换 service 模块里的 fetch_account_records。
"""

import asyncio
import tempfile
import unittest
from pathlib import Path

import src.backend.service as service_module
from src.backend.service import BackendService
from src.domain.entities import Account


class TradingRecordsTest(unittest.TestCase):
    def setUp(self):
        self.workdir = Path(tempfile.mkdtemp(prefix="itrader-records-"))
        (self.workdir / "data").mkdir()
        self.service = BackendService(self.workdir)
        self._original = service_module.fetch_account_records
        self.calls = []

        def fake_fetch(account, auth, timeout=20.0):
            self.calls.append((account.id, auth))
            if account.id == "l2":
                raise RuntimeError("天勤连接失败")
            return {
                "account_id": account.id,
                "kind": account.kind,
                "label": account.label,
                "account": {"balance": 1.0},
                "positions": [],
                "orders": [],
                "trades": [],
            }

        service_module.fetch_account_records = fake_fetch
        for account in (
            Account(
                id="l1", kind="live", label="实盘A", tq_account="a", tq_password="p",
                broker="N宏源期货", trade_account="1", trade_password="2",
            ),
            Account(id="l2", kind="live", label="实盘B", tq_account="b", tq_password="q"),
            Account(id="s1", kind="sim", label="模拟", tq_account="c", tq_password="r"),
        ):
            self.service.bootstrap.config_service.save_account(account)

    def tearDown(self):
        service_module.fetch_account_records = self._original

    def run_records(self, params):
        return asyncio.run(self.service.trading_records(params))

    def test_kind_filters_accounts(self):
        result = self.run_records({"kind": "live"})
        self.assertEqual([r["account_id"] for r in result["accounts"]], ["l1", "l2"])
        result = self.run_records({"kind": "sim"})
        self.assertEqual([r["account_id"] for r in result["accounts"]], ["s1"])

    def test_single_account_failure_isolated(self):
        """一个账户取数失败不能拖掉另一个账户的结果。"""
        result = self.run_records({"kind": "live"})
        by_id = {r["account_id"]: r for r in result["accounts"]}
        self.assertEqual(by_id["l2"]["error"], "天勤连接失败")
        self.assertEqual(by_id["l2"]["positions"], [])
        # 成功项不带 error 字段（客户端按可空处理），账户数据完整回传
        self.assertNotIn("error", by_id["l1"])
        self.assertEqual(by_id["l1"]["account"], {"balance": 1.0})

    def test_uses_each_account_credentials(self):
        """每个账户读自己的快期权限凭据，不是共用第一个。"""
        self.run_records({"kind": "live"})
        self.assertEqual(sorted(self.calls), [("l1", ("a", "p")), ("l2", ("b", "q"))])

    def test_account_id_wins_over_kind(self):
        result = self.run_records({"account_id": "s1", "kind": "live"})
        self.assertEqual([r["account_id"] for r in result["accounts"]], ["s1"])

    def test_rejects_unknown_kind(self):
        with self.assertRaises(ValueError):
            self.run_records({"kind": "bad"})

    def test_rejects_unknown_account(self):
        with self.assertRaises(ValueError):
            self.run_records({"account_id": "nope"})

    def test_empty_kind_raises_readable_error(self):
        self.run_records({"account_id": "s1"})
        self.service.bootstrap.config_service.delete_account("s1")
        with self.assertRaises(ValueError) as ctx:
            self.run_records({"kind": "sim"})
        self.assertIn("尚未配置模拟账户", str(ctx.exception))

    def test_records_manager_snapshot_and_broadcast(self):
        """验证 AccountRecordsManager 快照返回与变更事件广播。"""
        broadcasts = []
        self.service.attach_broadcast(lambda ev, data: broadcasts.append((ev, data)))

        # 模拟 Watcher 收到柜台更新并回调
        mock_snapshot = {
            "account_id": "s1",
            "kind": "sim",
            "label": "模拟",
            "account": {"balance": 100000.0},
            "positions": [],
            "orders": [],
            "trades": [],
        }
        self.service._on_records_updated("s1", "sim", mock_snapshot)

        self.assertEqual(len(broadcasts), 1)
        event_name, data = broadcasts[0]
        self.assertEqual(event_name, "trading_records_updated")
        self.assertEqual(data["kind"], "sim")
        self.assertEqual(data["account_id"], "s1")
        self.assertEqual(data["record"]["account"]["balance"], 100000.0)


if __name__ == "__main__":
    unittest.main()
