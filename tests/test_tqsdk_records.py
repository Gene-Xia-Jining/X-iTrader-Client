"""tqsdk_records 的行转换：nan 转 None、时间转北京时间、持仓按方向拆行。

不连接天勤：用假对象替换 tqsdk 的 Entity，只验证纯转换逻辑。
"""

import unittest

from src.infrastructure.trading.tqsdk_records import (
    _account_snapshot,
    _number,
    _order_rows,
    _position_rows,
    _time_text,
    _trade_rows,
)


class FakeEntity:
    def __init__(self, **kwargs):
        self.__dict__.update(kwargs)

    def __getitem__(self, key):
        return self.__dict__[key]


class NumberTest(unittest.TestCase):
    def test_nan_and_inf_become_none(self):
        # tqsdk 未就绪字段是 nan，JSON 不允许，必须转 None
        self.assertIsNone(_number(float("nan")))
        self.assertIsNone(_number(float("inf")))
        self.assertIsNone(_number(None))
        self.assertEqual(_number(1.5), 1.5)
        self.assertEqual(_number(0), 0.0)


class TimeTextTest(unittest.TestCase):
    def test_nanoseconds_to_beijing_time(self):
        # 2024-01-01 00:00:00 UTC = 北京时间 08:00:00.000
        self.assertEqual(_time_text(1704067200 * 10**9), "2024-01-01 08:00:00.000")
        # 带毫秒的纳秒
        self.assertEqual(_time_text(1704067200 * 10**9 + 123 * 10**6), "2024-01-01 08:00:00.123")

    def test_missing_time(self):
        self.assertIsNone(_time_text(0))
        self.assertIsNone(_time_text(None))


class PositionRowsTest(unittest.TestCase):
    def test_split_long_and_short_directions(self):
        positions = {
            "SHFE.rb2405": FakeEntity(
                exchange_id="SHFE",
                instrument_id="rb2405",
                pos_long=2,
                pos_short=3,
                open_price_long=3800.0,
                open_price_short=float("nan"),
                position_price_long=3790.0,
                position_price_short=3810.0,
                float_profit_long=120.0,
                float_profit_short=float("nan"),
                margin_long=7600.0,
                margin_short=11400.0,
            )
        }
        trades = {
            "t1": FakeEntity(
                trade_id="t1", order_id="o1", exchange_id="SHFE", instrument_id="rb2405",
                direction="BUY", offset="OPEN", price=3800.0, volume=2,
                trade_date_time=0, commission=10.0,
            )
        }
        rows = _position_rows(positions, trades=trades)
        self.assertEqual([r["direction"] for r in rows], ["多", "空"])
        self.assertEqual(rows[0]["symbol"], "SHFE.rb2405")
        self.assertEqual(rows[0]["volume"], 2)
        # rb 乘数 10，2 手多头，开仓 3800，浮动盈亏 120 -> 当前价 = 3800 + 120/(2*10) = 3806.0
        self.assertEqual(rows[0]["last_price"], 3806.0)
        self.assertEqual(rows[0]["commission"], 10.0)
        self.assertIsNone(rows[1]["open_price"])
        self.assertIsNone(rows[1]["commission"])
        self.assertEqual(rows[1]["margin"], 11400.0)

    def test_zero_volume_direction_skipped(self):
        positions = {
            "DCE.m2405": FakeEntity(
                exchange_id="DCE",
                instrument_id="m2405",
                pos_long=1,
                pos_short=0,
                open_price_long=3000.0,
                open_price_short=float("nan"),
                position_price_long=3000.0,
                position_price_short=float("nan"),
                float_profit_long=0.0,
                float_profit_short=float("nan"),
                margin_long=3000.0,
                margin_short=0.0,
            )
        }
        self.assertEqual(len(_position_rows(positions)), 1)
        self.assertEqual(_position_rows(positions)[0]["direction"], "多")


class OrderRowsTest(unittest.TestCase):
    def test_nan_volumes_fall_back_to_zero(self):
        """tqsdk 手数字段初始值是 nan，不能直接 int() 否则抛 ValueError。"""
        orders = {
            "o1": FakeEntity(
                order_id="o1", exchange_id="SHFE", instrument_id="rb2405",
                direction="BUY", offset="OPEN",
                volume_orign=float("nan"), volume_left=float("nan"),
                limit_price=float("nan"), trade_price=float("nan"),
                status="", last_msg="", insert_date_time=0,
            )
        }
        row = _order_rows(orders)[0]
        self.assertEqual(row["volume_orign"], 0)
        self.assertEqual(row["volume_left"], 0)

    def test_newest_first_and_text_mapping(self):
        orders = {
            "o1": FakeEntity(
                order_id="o1",
                exchange_id="SHFE",
                instrument_id="rb2405",
                direction="BUY",
                offset="OPEN",
                volume_orign=1,
                volume_left=0,
                limit_price=float("nan"),
                trade_price=3800.0,
                status="FINISHED",
                last_msg="全部成交",
                insert_date_time=1704067200 * 10**9,
            ),
            "o2": FakeEntity(
                order_id="o2",
                exchange_id="SHFE",
                instrument_id="rb2405",
                direction="SELL",
                offset="CLOSETODAY",
                volume_orign=1,
                volume_left=1,
                limit_price=3900.0,
                trade_price=float("nan"),
                status="ALIVE",
                last_msg="已报",
                insert_date_time=(1704067200 + 60) * 10**9,
            ),
        }
        rows = _order_rows(orders)
        self.assertEqual([r["order_id"] for r in rows], ["o2", "o1"])
        self.assertEqual(rows[1]["direction_text"], "买")
        self.assertEqual(rows[1]["offset_text"], "开仓")
        self.assertEqual(rows[0]["offset_text"], "平今")
        self.assertEqual(rows[0]["status_text"], "已报")
        self.assertIsNone(rows[1]["limit_price"])


class TradeRowsTest(unittest.TestCase):
    def test_trade_rows(self):
        trades = {
            "t1": FakeEntity(
                trade_id="t1",
                order_id="o1",
                exchange_id="SHFE",
                instrument_id="rb2405",
                direction="BUY",
                offset="OPEN",
                price=3800.0,
                volume=1,
                trade_date_time=(1704067200 + 30) * 10**9,
            )
        }
        rows = _trade_rows(trades)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["offset_text"], "开仓")
        self.assertEqual(rows[0]["trade_time"], "2024-01-01 08:00:30.000")


class AccountSnapshotTest(unittest.TestCase):
    def test_nan_fields_become_none(self):
        acc = FakeEntity(
            balance=100000.0,
            available=float("nan"),
            margin=10000.0,
            float_profit=0.0,
            position_profit=0.0,
            close_profit=float("nan"),
            commission=5.0,
            risk_ratio=0.1,
            pre_balance=100000.0,
            static_balance=100000.0,
        )
        snapshot = _account_snapshot(acc)
        self.assertEqual(snapshot["balance"], 100000.0)
        self.assertIsNone(snapshot["available"])
        self.assertIsNone(snapshot["close_profit"])


if __name__ == "__main__":
    unittest.main()
