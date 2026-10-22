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

    def test_trade_history_repository_and_date_range(self):
        """测试 SQLiteTradeHistoryRepository 的落盘与时间范围筛选。"""
        from datetime import datetime, timedelta, timezone
        from src.infrastructure.db.repositories import SQLiteTradeHistoryRepository

        beijing = timezone(timedelta(hours=8))
        now = datetime.now(beijing)
        today_str = now.strftime("%Y-%m-%d %H:%M:%S")
        two_days_ago = (now - timedelta(days=2)).strftime("%Y-%m-%d 10:00:00")
        ten_days_ago = (now - timedelta(days=10)).strftime("%Y-%m-%d 09:30:00")

        repo = self.service._trade_history_repo
        trades = [
            {
                "trade_id": "t1",
                "order_id": "o1",
                "symbol": "SHFE.rb2501",
                "direction": "BUY",
                "direction_text": "买",
                "offset": "OPEN",
                "offset_text": "开仓",
                "price": 3800.0,
                "volume": 1,
                "trade_time": today_str,
                "realized_profit": None,
                "source": "趋势策略",
            },
            {
                "trade_id": "t2",
                "order_id": "o2",
                "symbol": "SHFE.rb2501",
                "direction": "SELL",
                "direction_text": "卖",
                "offset": "CLOSE",
                "offset_text": "平仓",
                "price": 3850.0,
                "volume": 1,
                "trade_time": two_days_ago,
                "realized_profit": 500.0,
                "source": "趋势策略",
            },
            {
                "trade_id": "t3",
                "order_id": "o3",
                "symbol": "DCE.i2501",
                "direction": "SELL",
                "direction_text": "卖",
                "offset": "CLOSE",
                "offset_text": "平仓",
                "price": 800.0,
                "volume": 2,
                "trade_time": ten_days_ago,
                "realized_profit": -2000.0,
                "source": "手动/外部",
            },
        ]

        repo.save_trades("l1", trades)

        # 1. 查 today
        today_trades = repo.query_trades("l1", date_range="today")
        self.assertEqual(len(today_trades), 1)
        self.assertEqual(today_trades[0]["trade_id"], "t1")

        # 2. 查 3d（今天与2天前）
        three_day_trades = repo.query_trades("l1", date_range="3d")
        self.assertEqual(len(three_day_trades), 2)
        self.assertEqual([t["trade_id"] for t in three_day_trades], ["t1", "t2"])

        # 3. 查 all（包括10天前的）
        all_trades = repo.query_trades("l1", date_range="all")
        self.assertEqual(len(all_trades), 3)

        # 4. 通过 trading_records 接口带 date_range 查询
        resp = self.run_records({"account_id": "l1", "date_range": "3d"})
        acc_rec = resp["accounts"][0]
        self.assertEqual(len(acc_rec["trades"]), 2)

    def test_calc_trade_realized_profit_and_source(self):
        """测试平仓盈亏计算与来源标注。"""
        from types import SimpleNamespace
        from src.domain.entities import Account
        from src.infrastructure.trading.tqsdk_records import _order_rows, _trade_rows

        acc = Account(
            id="test1",
            kind="sim",
            label="测试",
            tq_account="x",
            tq_password="y",
            symbols=["SHFE.rb2501"],
            strategy="经典趋势",
        )

        orders = {
            "o1": SimpleNamespace(
                order_id="o1",
                exchange_id="SHFE",
                instrument_id="rb2501",
                direction="BUY",
                offset="OPEN",
                volume_orign=2,
                volume_left=0,
                limit_price=3800.0,
                trade_price=3800.0,
                status="FINISHED",
                last_msg="全部成交",
                insert_date_time=1700000000000000000,
            ),
            "o2": SimpleNamespace(
                order_id="o2",
                exchange_id="CZCE",
                instrument_id="MA501",
                direction="BUY",
                offset="OPEN",
                volume_orign=1,
                volume_left=1,
                limit_price=2500.0,
                trade_price=None,
                status="FINISHED",
                last_msg="柜台撤单: [10001]资金不足",
                insert_date_time=1700000010000000000,
            ),
        }
        order_list = _order_rows(orders, account=acc)
        # 委托来源标注与 last_msg
        self.assertEqual(order_list[0]["source"], "手动/外部")  # MA501 不在 symbols 里
        self.assertEqual(order_list[0]["last_msg"], "柜台撤单: [10001]资金不足")
        self.assertEqual(order_list[1]["source"], "经典趋势")  # rb2501 属于策略

        trades = {
            "t1": SimpleNamespace(
                trade_id="t1",
                order_id="o1",
                exchange_id="SHFE",
                instrument_id="rb2501",
                direction="BUY",
                offset="OPEN",
                price=3800.0,
                volume=2,
                trade_date_time=1700000000000000000,
                commission=6.0,
            ),
            "t2": SimpleNamespace(
                trade_id="t2",
                order_id="o3",
                exchange_id="SHFE",
                instrument_id="rb2501",
                direction="SELL",
                offset="CLOSE",
                price=3850.0,
                volume=1,
                trade_date_time=1700000020000000000,
                commission=3.0,
            ),
            "t3": SimpleNamespace(
                trade_id="t3",
                order_id="o4",
                exchange_id="SHFE",
                instrument_id="rb2501",
                direction="SELL",
                offset="CLOSE",
                price=3790.0,
                volume=1,
                trade_date_time=1700000030000000000,
                commission=3.0,
            ),
        }
        trade_list = _trade_rows(trades, account=acc)
        by_id = {t["trade_id"]: t for t in trade_list}

        # 开仓 realized_profit 为 None
        self.assertIsNone(by_id["t1"]["realized_profit"])
        # 平多盈利：(3850 - 3800) * 1手 * rb乘数10 = +500.0
        self.assertEqual(by_id["t2"]["realized_profit"], 500.0)
        # 平多亏损：(3790 - 3800) * 1手 * rb乘数10 = -100.0
        self.assertEqual(by_id["t3"]["realized_profit"], -100.0)
        self.assertEqual(by_id["t1"]["source"], "经典趋势")



if __name__ == "__main__":
    unittest.main()
