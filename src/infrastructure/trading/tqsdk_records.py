"""账户交易数据快照（当日资金 / 持仓 / 委托 / 成交）。

取数走「临时只读会话」而不是复用 TqSdkTradingExecutor 的会话：
- 执行器的会话只在调仓时 wait_update，没有常驻收包循环，读到的数据可能是旧的；
- 交易记录页在自动交易未开启时也要能查，不能依赖执行器已存在；
- 查询与交易完全隔离，查询失败不影响正在运行的调仓任务。

这与启动预检 test_account_session 是同一模式：建会话 → 读完即关。
阻塞调用，需在工作线程中执行。
"""

import math
import time
from datetime import datetime, timedelta, timezone
from typing import Optional

from ...domain.entities import Account

# 交易时间固定按北京时间展示：期货交易时段不随运行机器的本地时区变化。
# 中国无夏令时，用固定偏移而非 zoneinfo（Windows 没有系统 tz 数据库）。
_BEIJING = timezone(timedelta(hours=8))

_DIRECTION_TEXT = {"BUY": "买", "SELL": "卖"}
_OFFSET_TEXT = {
    "OPEN": "开仓",
    "CLOSE": "平仓",
    "CLOSETODAY": "平今",
    "CLOSEYESTERDAY": "平昨",
}
_ORDER_STATUS_TEXT = {"ALIVE": "已报", "FINISHED": "已完"}


def _number(value) -> Optional[float]:
    """tqsdk 未就绪的字段是 nan；JSON 不允许 NaN，统一转 None。"""
    if value is None:
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    if math.isnan(number) or math.isinf(number):
        return None
    return number


def _text(value) -> str:
    return "" if value is None else str(value)


def _int(value, default: int = 0) -> int:
    """手数类字段：tqsdk 初始值可能是 nan/None，统一落到 default。"""
    number = _number(value)
    return default if number is None else int(number)


def _time_text(nanoseconds) -> Optional[str]:
    """tqsdk 时间字段是 unix 纳秒，转北京时间可读文本（客户端直接展示）。"""
    number = _number(nanoseconds)
    if not number:
        return None
    return datetime.fromtimestamp(number / 1e9, _BEIJING).strftime("%Y-%m-%d %H:%M:%S")


def _symbol(exchange_id: str, instrument_id: str) -> str:
    if exchange_id and instrument_id:
        return f"{exchange_id}.{instrument_id}"
    return instrument_id or exchange_id


def _account_snapshot(account_obj) -> dict:
    acc = account_obj
    return {
        "balance": _number(acc.balance),
        "available": _number(acc.available),
        "margin": _number(acc.margin),
        "float_profit": _number(acc.float_profit),
        "position_profit": _number(acc.position_profit),
        "close_profit": _number(acc.close_profit),
        "commission": _number(acc.commission),
        "risk_ratio": _number(acc.risk_ratio),
        "pre_balance": _number(acc.pre_balance),
        "static_balance": _number(acc.static_balance),
    }


def _position_rows(positions) -> list[dict]:
    """一个合约的多空各成一行（与快期持仓列表一致），无持仓方向不输出。"""
    rows: list[dict] = []
    for symbol, pos in positions.items():
        exchange_id = _text(pos.exchange_id) or symbol.split(".", 1)[0]
        instrument_id = _text(pos.instrument_id) or (symbol.split(".", 1)[-1] if "." in symbol else symbol)
        base = _symbol(exchange_id, instrument_id)
        for direction, volume, open_price, position_price, float_profit, margin in (
            (
                "多",
                pos.pos_long,
                pos.open_price_long,
                pos.position_price_long,
                pos.float_profit_long,
                pos.margin_long,
            ),
            (
                "空",
                pos.pos_short,
                pos.open_price_short,
                pos.position_price_short,
                pos.float_profit_short,
                pos.margin_short,
            ),
        ):
            if not volume:
                continue
            rows.append(
                {
                    "symbol": base,
                    "direction": direction,
                    "volume": _int(volume),
                    "open_price": _number(open_price),
                    "position_price": _number(position_price),
                    "float_profit": _number(float_profit),
                    "margin": _number(margin),
                }
            )
    rows.sort(key=lambda row: (row["symbol"], row["direction"]))
    return rows


def _order_rows(orders) -> list[dict]:
    rows = []
    for order in orders.values():
        rows.append(
            {
                "order_id": _text(order.order_id),
                "symbol": _symbol(_text(order.exchange_id), _text(order.instrument_id)),
                "direction": _text(order.direction),
                "direction_text": _DIRECTION_TEXT.get(_text(order.direction), _text(order.direction)),
                "offset": _text(order.offset),
                "offset_text": _OFFSET_TEXT.get(_text(order.offset), _text(order.offset)),
                "volume_orign": _int(order.volume_orign),
                "volume_left": _int(order.volume_left),
                "limit_price": _number(order.limit_price),
                "trade_price": _number(order.trade_price),
                "status": _text(order.status),
                "status_text": _ORDER_STATUS_TEXT.get(_text(order.status), _text(order.status)),
                "last_msg": _text(order.last_msg),
                "insert_time": _time_text(order.insert_date_time),
            }
        )
    # 新委托在前：insert_date_time 是纳秒，直接按字符串时间倒序等价于时间倒序
    rows.sort(key=lambda row: row["insert_time"] or "", reverse=True)
    return rows


def _trade_rows(trades) -> list[dict]:
    rows = []
    for trade in trades.values():
        rows.append(
            {
                "trade_id": _text(trade.trade_id),
                "order_id": _text(trade.order_id),
                "symbol": _symbol(_text(trade.exchange_id), _text(trade.instrument_id)),
                "direction": _text(trade.direction),
                "direction_text": _DIRECTION_TEXT.get(_text(trade.direction), _text(trade.direction)),
                "offset": _text(trade.offset),
                "offset_text": _OFFSET_TEXT.get(_text(trade.offset), _text(trade.offset)),
                "price": _number(trade.price),
                "volume": _int(trade.volume),
                "trade_time": _time_text(trade.trade_date_time),
            }
        )
    rows.sort(key=lambda row: row["trade_time"] or "", reverse=True)
    return rows


def fetch_account_records(
    account: Account,
    auth: tuple[Optional[str], Optional[str]],
    timeout: float = 20.0,
) -> dict:
    """查询单个账户的当日资金、持仓、委托与成交。

    账户尚未连接成功时字段可能是空值；等待 account.balance 就绪（最多 timeout
    秒）后返回当时能拿到的数据，而不是无限等待——交易记录页需要给出反馈。
    """
    from tqsdk import TqAccount, TqApi, TqAuth, TqKq

    if account.kind == "live":
        account_obj = TqAccount(account.broker, account.trade_account, account.trade_password)
    else:
        # 模拟账户全局仅一个（快期多模拟账户需专业版，暂不支持），用主模拟账号
        account_obj = TqKq()

    api = TqApi(
        account_obj,
        auth=TqAuth(auth[0], auth[1]) if auth[0] and auth[1] else None,
    )
    try:
        deadline = time.time() + timeout
        acc = api.get_account()
        # 资金字段就绪说明账户数据已同步；期间持续收包，持仓/委托/成交一并落地
        while _number(acc.balance) is None and time.time() < deadline:
            api.wait_update(deadline=deadline)

        return {
            "account_id": account.id,
            "kind": account.kind,
            "label": account.label or account.id,
            "account": _account_snapshot(acc),
            "positions": _position_rows(api.get_position()),
            "orders": _order_rows(api.get_order()),
            "trades": _trade_rows(api.get_trade()),
        }
    finally:
        # 数据已读到时释放失败不该让整次查询报错（用户看到的应是已取到的记录）。
        # tqsdk 的 close 会自行忽略已断开的连接异常，这里只兜住其余边界情况。
        try:
            api.close()
        except Exception:
            pass
