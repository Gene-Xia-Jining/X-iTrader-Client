"""交易时段判断（第二条防线）。

服务器已按交易时段门控信号（X-iTrader-Server/src/trading_hours.py），这里再
拦一道的理由是成本极低而后果很重：客户端一旦在非交易时段把信号交给执行器，
快期模拟柜台会照常撮合成交，用户账户里就多出一笔本不该有的持仓。服务器漏改、
旧版服务器、时段表解析差异都可能让信号漏下来。

时段表来自 tqsdk quote 的 trading_time 字段（品种级），而不是硬编码的交易所
作息表——各品种夜盘安排不同，硬编码必然与交易所调整脱节。

业务时间固定按北京时间：期货交易时段不随运行机器时区变化。中国无夏令时，
用固定偏移而非 zoneinfo（Windows 没有系统 tz 数据库），与 tqsdk_records 同一约定。
"""

import datetime
from typing import Optional

_BEIJING = datetime.timezone(datetime.timedelta(hours=8))


def _seconds(hhmmss: str) -> int:
    hour, minute, second = (int(part) for part in hhmmss.split(":"))
    return hour * 3600 + minute * 60 + second


def beijing_now() -> datetime.datetime:
    """当前北京时间。

    作为时段判断的默认时钟直接挂在 TradingEngineDeps.clock 上：默认值必须是
    可调用对象，否则调用方漏传时会在门控里抛 TypeError，把「非交易时段拦截」
    变成「信号处理链路整体崩溃」。
    """
    return datetime.datetime.now(_BEIJING)


def in_trading_period(
    trading_time, now: Optional[datetime.datetime] = None
) -> bool:
    """当前是否落在 trading_time 的任一交易时段内。

    trading_time 形如::

        {"day":   [["09:00:00", "10:15:00"], ["10:30:00", "11:30:00"], ...],
         "night": [["21:00:00", "23:00:00"]]}

    夜盘可跨零点（贵金属到次日 02:30），此时 end 早于 start，按跨越判断。

    trading_time 为空表示行情/交易日历尚未就绪：返回 True 放行。拿不到时段表
    就把信号当非交易时段拦掉，会让开盘期间的调仓全部失效，比偶尔漏拦更糟——
    漏拦还有执行器自身的成交确认与超时兜底。
    """
    if not trading_time:
        return True

    current = now or datetime.datetime.now(_BEIJING)
    now_s = current.hour * 3600 + current.minute * 60 + current.second

    parsed = 0
    for periods in trading_time.values():
        for period in periods:
            try:
                start, end = period[0], period[1]
                start_s, end_s = _seconds(start), _seconds(end)
            except (TypeError, ValueError, IndexError):
                # 单个时段项格式异常时跳过，不让它拖垮整次判断；
                # 若全部异常则视为拿不到时段表
                continue
            parsed += 1
            if start_s <= end_s:
                if start_s <= now_s < end_s:
                    return True
            elif now_s >= start_s or now_s < end_s:
                return True
    # 一项都没解析出来 = 没有可用的时段表，按「拿不到就放行」处理
    return parsed == 0


# 自测：与服务器端 trading_hours.py 的用例保持一致
if __name__ == "__main__":
    day = [["09:00:00", "10:15:00"], ["10:30:00", "11:30:00"], ["13:30:00", "15:00:00"]]
    day_only = {"day": day, "night": []}
    with_night = {"day": day, "night": [["21:00:00", "23:00:00"]]}
    overnight = {"day": day, "night": [["21:00:00", "02:30:00"]]}

    def at(hour, minute, second=0):
        return datetime.datetime(2026, 10, 21, hour, minute, second, tzinfo=_BEIJING)

    assert in_trading_period(day_only, at(9, 0, 0))
    assert in_trading_period(day_only, at(10, 14, 59))
    assert not in_trading_period(day_only, at(10, 15, 0))
    assert not in_trading_period(day_only, at(12, 0))
    assert not in_trading_period(day_only, at(15, 0, 0))
    # 事故时点
    assert not in_trading_period(with_night, at(8, 10, 23))
    assert not in_trading_period(with_night, at(19, 12, 39))
    assert in_trading_period(with_night, at(21, 0, 0))
    assert in_trading_period(with_night, at(22, 59, 59))
    assert not in_trading_period(with_night, at(23, 0, 0))
    # 跨零点夜盘
    assert in_trading_period(overnight, at(23, 30))
    assert in_trading_period(overnight, at(1, 0))
    assert in_trading_period(overnight, at(2, 29, 59))
    assert not in_trading_period(overnight, at(2, 30))
    # 时段表缺失 / 脏数据
    assert in_trading_period({})
    assert in_trading_period(None)
    assert in_trading_period({"day": [["bad"]]}, at(12, 0))

    print("trading_hours(client): 全部 assert 通过")
