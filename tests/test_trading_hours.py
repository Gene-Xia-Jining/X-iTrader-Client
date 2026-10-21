"""交易时段判断（客户端门控的基础）。

覆盖事故时点（盘前 08:10、夜盘前 19:12）、跨零点夜盘，以及「拿不到时段表
就放行」这一关键约定。
"""

import datetime
import unittest

from src.domain.trading_hours import in_trading_period

_DAY = [
    ["09:00:00", "10:15:00"],
    ["10:30:00", "11:30:00"],
    ["13:30:00", "15:00:00"],
]
# 上期所螺纹：有 21:00-23:00 夜盘
_RB = {"day": _DAY, "night": [["21:00:00", "23:00:00"]]}
# 贵金属：夜盘跨零点到次日 02:30
_OVERNIGHT = {"day": _DAY, "night": [["21:00:00", "02:30:00"]]}
# 无夜盘品种（如部分农产品）
_DAY_ONLY = {"day": _DAY, "night": []}


def _at(hour, minute, second=0):
    return datetime.datetime(2026, 10, 21, hour, minute, second)


class TradingPeriodTest(unittest.TestCase):
    def test_day_session_boundaries(self):
        self.assertTrue(in_trading_period(_DAY_ONLY, _at(9, 0, 0)))       # 开盘瞬间在内
        self.assertTrue(in_trading_period(_DAY_ONLY, _at(10, 14, 59)))
        self.assertFalse(in_trading_period(_DAY_ONLY, _at(10, 15, 0)))    # 收盘瞬间在外
        self.assertFalse(in_trading_period(_DAY_ONLY, _at(12, 0)))        # 午休
        self.assertFalse(in_trading_period(_DAY_ONLY, _at(15, 0, 0)))

    def test_incident_moments_are_off_hours(self):
        """真实事故时点：这些时刻产生的信号都不该下单。"""
        self.assertFalse(in_trading_period(_RB, _at(8, 10, 23)))   # 盘前（08:10 的 rb 信号）
        self.assertFalse(in_trading_period(_RB, _at(19, 12, 39)))  # 夜盘前（19:12 的 SA 信号）
        self.assertFalse(in_trading_period(_RB, _at(16, 24, 8)))   # 收盘后（16:24 的 hc 信号）
        self.assertFalse(in_trading_period(_RB, _at(7, 48, 42)))   # 凌晨（07:48 的 SA 信号）

    def test_night_session(self):
        self.assertTrue(in_trading_period(_RB, _at(21, 0, 0)))
        self.assertTrue(in_trading_period(_RB, _at(22, 59, 59)))
        self.assertFalse(in_trading_period(_RB, _at(23, 0, 0)))

    def test_overnight_session_crosses_midnight(self):
        self.assertTrue(in_trading_period(_OVERNIGHT, _at(23, 30)))
        self.assertTrue(in_trading_period(_OVERNIGHT, _at(1, 0)))
        self.assertTrue(in_trading_period(_OVERNIGHT, _at(2, 29, 59)))
        self.assertFalse(in_trading_period(_OVERNIGHT, _at(2, 30)))
        self.assertFalse(in_trading_period(_OVERNIGHT, _at(12, 0)))

    def test_missing_table_fails_open(self):
        """拿不到时段表必须放行：误拦会让开盘期间所有调仓失效。"""
        self.assertTrue(in_trading_period({}))
        self.assertTrue(in_trading_period(None))

    def test_malformed_entries_fail_open(self):
        """时段项全是脏数据时按「拿不到时段表」处理。"""
        self.assertTrue(in_trading_period({"day": [["bad"]]}, _at(12, 0)))

    def test_one_bad_entry_does_not_poison_others(self):
        """单个脏项跳过，其余时段照常生效。"""
        mixed = {"day": [["bad"], ["09:00:00", "10:15:00"]], "night": []}
        self.assertTrue(in_trading_period(mixed, _at(9, 30)))
        self.assertFalse(in_trading_period(mixed, _at(12, 0)))


if __name__ == "__main__":
    unittest.main()
