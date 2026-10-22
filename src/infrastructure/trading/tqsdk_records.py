"""账户交易数据快照（当日资金 / 持仓 / 委托 / 成交）。

提供两种模式：
1. AccountRecordsManager / AccountRecordsWatcher：常驻只读会话守护，保持 TqApi 长连接并监听
   柜台 WebSocket 推送，维护内存快照并向 UI 广播实时更新（毫秒级查询，推拉结合）。
2. fetch_account_records：单次临时会话查询（用于单元测试或管理器未就绪时的降级路径）。

认证凭据统一经 get_shared_auth 缓存复用，避免每次新建连接重复向 ShinnyTech 发起 HTTP 取 token。
"""

import copy
import logging
import math
import threading
import time
from datetime import datetime, timedelta, timezone
from typing import Callable, Optional

from ...domain.entities import Account
from ...domain.repositories import TradeHistoryRepository

_logger = logging.getLogger("tqsdk.records")

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


# -------- 第三层：快期认证凭据缓存复用 --------

_auth_cache_lock = threading.Lock()
_auth_cache: dict[tuple[str, str], object] = {}


def get_shared_auth(account: Optional[str], password: Optional[str]) -> Optional[object]:
    """获取已登录的 TqAuth 实例（进程生命周期内缓存）。

    TqAuth.login() 每次调用都会打 ShinnyTech 的 HTTPS 网关取 token（耗时 1.1~1.3s）。
    这里包装为幂等单例，避免重复取 token。
    """
    if not account or not password:
        return None
    key = (account, password)
    with _auth_cache_lock:
        cached = _auth_cache.get(key)
        if cached is not None:
            return cached

        from tqsdk import TqAuth

        class _LoggedInAuth(TqAuth):
            _already_logged_in = False

            def login(self) -> None:
                if self._already_logged_in:
                    return
                super().login()
                self._already_logged_in = True

        auth_obj = _LoggedInAuth(account, password)
        auth_obj.init(mode="real")
        auth_obj.login()
        _auth_cache[key] = auth_obj
        return auth_obj


def clear_auth_cache() -> None:
    """清除凭据缓存（在配置彻底重置或凭据失效时调用）。"""
    with _auth_cache_lock:
        _auth_cache.clear()


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


_COMMODITY_MULTIPLIERS = {
    "rb": 10, "hc": 10, "i": 100, "j": 100, "jm": 60,
    "cu": 5, "al": 5, "zn": 5, "ni": 1, "sn": 1, "au": 1000, "ag": 15,
    "m": 10, "y": 10, "p": 10, "c": 10, "cs": 10, "a": 10, "b": 10,
    "ta": 5, "ma": 10, "eg": 10, "eb": 5, "sa": 20, "ur": 20, "fg": 20,
    "if": 300, "ih": 300, "ic": 200, "im": 200, "t": 10000, "tf": 10000,
}


def _extract_variety(symbol: str) -> str:
    """从合约代码（如 SHFE.rb2501、KQ.m@DCE.i）提取品种小写前缀（如 rb、i）。"""
    code = symbol.split(".")[-1]
    letters = []
    for ch in code:
        if ch.isalpha():
            letters.append(ch)
        elif letters:
            break
    return "".join(letters).lower()


def _get_multiplier(api, symbol: str) -> float:
    """获取合约乘数：优先读 quote.volume_multiple，兜底查常见品种速查表，最终为 1.0。"""
    if api is not None:
        try:
            quote = api.get_quote(symbol)
            vm = _number(getattr(quote, "volume_multiple", None))
            if vm and vm > 0:
                return float(vm)
        except Exception:
            pass
    variety = _extract_variety(symbol)
    return float(_COMMODITY_MULTIPLIERS.get(variety, 1.0))


def _source_label(account: Account, symbol: str) -> str:
    """根据账户策略配置与品种标记来源：策略品种标为策略名或智能信号，否则标为手动/外部。"""
    configured_symbols = account.symbols or []
    is_strategy_symbol = False
    for s in configured_symbols:
        if symbol == s or symbol.startswith(s + ".") or s.startswith(symbol + "."):
            is_strategy_symbol = True
            break
        # 兼容主连/实际合约的映射判断
        if _extract_variety(symbol) == _extract_variety(s):
            is_strategy_symbol = True
            break
    if is_strategy_symbol:
        return account.strategy if account.strategy else "智能信号"
    return "手动/外部"


def _order_rows(orders, account: Optional[Account] = None) -> list[dict]:
    rows = []
    for order in orders.values():
        sym = _symbol(_text(order.exchange_id), _text(order.instrument_id))
        source = _source_label(account, sym) if account else "智能信号"
        rows.append(
            {
                "order_id": _text(order.order_id),
                "symbol": sym,
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
                "source": source,
            }
        )
    # 新委托在前：insert_date_time 是纳秒，直接按字符串时间倒序等价于时间倒序
    rows.sort(key=lambda row: row["insert_time"] or "", reverse=True)
    return rows


def _trade_rows(
    trades,
    account: Optional[Account] = None,
    api=None,
    positions_map: Optional[dict] = None,
) -> list[dict]:
    # 先按时间升序排序以进行 FIFO 平仓盈亏匹配
    raw_list = []
    for trade in trades.values():
        sym = _symbol(_text(trade.exchange_id), _text(trade.instrument_id))
        source = _source_label(account, sym) if account else "智能信号"
        raw_list.append(
            {
                "trade_id": _text(trade.trade_id),
                "order_id": _text(trade.order_id),
                "symbol": sym,
                "direction": _text(trade.direction),
                "direction_text": _DIRECTION_TEXT.get(_text(trade.direction), _text(trade.direction)),
                "offset": _text(trade.offset),
                "offset_text": _OFFSET_TEXT.get(_text(trade.offset), _text(trade.offset)),
                "price": _number(trade.price),
                "volume": _int(trade.volume),
                "trade_time": _time_text(trade.trade_date_time),
                "commission": _number(getattr(trade, "commission", None)),
                "source": source,
                "_raw_time": getattr(trade, "trade_date_time", 0) or 0,
            }
        )
    raw_list.sort(key=lambda x: x["_raw_time"])

    # 计算单笔平仓盈亏
    open_pools: dict[str, dict[str, list[dict]]] = {}  # symbol -> {"BUY": [...], "SELL": [...]}
    multipliers_cache: dict[str, float] = {}

    for row in raw_list:
        sym = row["symbol"]
        offset = row["offset"]
        direction = row["direction"]
        price = row["price"]
        vol = row["volume"]

        if sym not in multipliers_cache:
            multipliers_cache[sym] = _get_multiplier(api, sym)
        multiplier = multipliers_cache[sym]

        if sym not in open_pools:
            open_pools[sym] = {"BUY": [], "SELL": []}

        if offset == "OPEN":
            row["realized_profit"] = None
            if price is not None and vol > 0:
                open_pools[sym][direction].append({"price": price, "volume": vol})
        elif offset in ("CLOSE", "CLOSETODAY", "CLOSEYESTERDAY"):
            # 平多（SELL）平的是之前的 BUY；平空（BUY）平的是之前的 SELL
            target_direction = "BUY" if direction == "SELL" else "SELL"
            pool = open_pools[sym][target_direction]
            remaining_to_close = vol
            matched_cost = 0.0
            matched_volume = 0

            while pool and remaining_to_close > 0:
                head = pool[0]
                matched_vol = min(head["volume"], remaining_to_close)
                matched_cost += head["price"] * matched_vol
                matched_volume += matched_vol
                head["volume"] -= matched_vol
                remaining_to_close -= matched_vol
                if head["volume"] <= 0:
                    pool.pop(0)

            # 若开仓池已用尽但仍有平仓手数（平昨仓），尝试从持仓均价拿开仓价
            if remaining_to_close > 0 and positions_map:
                pos = positions_map.get(sym)
                if pos:
                    fallback_price = (
                        _number(getattr(pos, "open_price_long", None))
                        if target_direction == "BUY"
                        else _number(getattr(pos, "open_price_short", None))
                    )
                    if fallback_price is not None:
                        matched_cost += fallback_price * remaining_to_close
                        matched_volume += remaining_to_close
                        remaining_to_close = 0

            if matched_volume > 0 and price is not None:
                avg_open_price = matched_cost / matched_volume
                # 平多：(平仓价 - 开仓价) * 手数 * 乘数
                # 平空：(开仓价 - 平仓价) * 手数 * 乘数
                point_diff = (price - avg_open_price) if direction == "SELL" else (avg_open_price - price)
                profit = point_diff * matched_volume * multiplier
                row["realized_profit"] = round(profit, 2)
            else:
                row["realized_profit"] = None
        else:
            row["realized_profit"] = None

        row.pop("_raw_time", None)

    # 最终输出按成交时间倒序
    raw_list.sort(key=lambda r: r["trade_time"] or "", reverse=True)
    return raw_list


def _build_snapshot_dict(account: Account, api, acc) -> dict:
    pos_map = api.get_position()
    return {
        "account_id": account.id,
        "kind": account.kind,
        "label": account.label or account.id,
        "account": _account_snapshot(acc),
        "positions": _position_rows(pos_map),
        "orders": _order_rows(api.get_order(), account=account),
        "trades": _trade_rows(api.get_trade(), account=account, api=api, positions_map=pos_map),
    }



def fetch_account_records(
    account: Account,
    auth: tuple[Optional[str], Optional[str]],
    timeout: float = 20.0,
    history_repo: Optional[TradeHistoryRepository] = None,
) -> dict:
    """查询单个账户的当日资金、持仓、委托与成交（单次临时会话）。

    账户尚未连接成功时字段可能是空值；等待 account.balance 就绪（最多 timeout
    秒）后返回当时能拿到的数据，而不是无限等待。
    """
    from tqsdk import TqAccount, TqApi, TqKq

    auth_obj = get_shared_auth(auth[0], auth[1])

    if account.kind == "live":
        account_obj = TqAccount(account.broker, account.trade_account, account.trade_password)
    else:
        account_obj = TqKq()

    api = TqApi(account_obj, auth=auth_obj)
    try:
        deadline = time.time() + timeout
        acc = api.get_account()
        while _number(acc.balance) is None and time.time() < deadline:
            api.wait_update(deadline=deadline)

        snapshot = _build_snapshot_dict(account, api, acc)
        if history_repo is not None and snapshot.get("trades"):
            try:
                history_repo.save_trades(account.id, snapshot["trades"])
            except Exception as e:
                _logger.warning("历史成交持久化异常: %s", e)
        return snapshot
    finally:
        try:
            api.close()
        except Exception:
            pass



# -------- 第二层：常驻只读会话守护与实时更新管理器 --------


class AccountRecordsWatcher:
    """单个账户的交易记录常驻监听会话。

    独占一个守护线程持续运行 TqApi.wait_update()：
    - 内存中维护最新的资金、持仓、委托、成交快照；
    - 当快期推送数据变更时，重新抽取快照并触发 on_update 回调；
    - 查询时从内存快照直接返回（0 毫秒响应）；
    - 若网络中断或凭据异常，捕获并在后台退避重试，不影响主流程。
    """

    def __init__(
        self,
        account: Account,
        on_update: Optional[Callable[[str, str, dict], None]] = None,
        history_repo: Optional[TradeHistoryRepository] = None,
    ):
        self.account = account
        self._on_update = on_update
        self._history_repo = history_repo
        self._stopped = False

        self._ready_event = threading.Event()
        self._snapshot_lock = threading.Lock()
        self._snapshot: Optional[dict] = None
        self._error: str = ""
        self._fingerprint = self._calc_fingerprint(account)
        self._thread = threading.Thread(
            target=self._run,
            name=f"records-watcher-{account.id}",
            daemon=True,
        )

    @staticmethod
    def _calc_fingerprint(account: Account) -> tuple:
        return (
            account.id,
            account.kind,
            account.enabled,
            account.tq_account,
            account.tq_password,
            account.broker,
            account.trade_account,
            account.trade_password,
        )

    @property
    def is_configured(self) -> bool:
        """检查账户是否有最基本的凭据以建立会话。"""
        if not self.account.enabled:
            return False
        if not self.account.tq_account or not self.account.tq_password:
            return False
        if self.account.kind == "live":
            if not self.account.broker or not self.account.trade_account or not self.account.trade_password:
                return False
        return True

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stopped = True
        self._ready_event.set()

    def get_snapshot(self, wait_timeout: float = 0.5) -> dict:
        """获取最新快照。若尚未 ready，最多等待 wait_timeout 秒。"""
        if not self._ready_event.is_set():
            self._ready_event.wait(timeout=wait_timeout)
        with self._snapshot_lock:
            if self._snapshot is not None:
                return copy.deepcopy(self._snapshot)
            return {
                "account_id": self.account.id,
                "kind": self.account.kind,
                "label": self.account.label or self.account.id,
                "error": self._error or ("正在连接快期获取数据…" if self.is_configured else "账户尚未配置完整"),
                "account": {},
                "positions": [],
                "orders": [],
                "trades": [],
            }

    def _run(self) -> None:
        if not self.is_configured:
            self._error = "账户尚未配置完整或已停用"
            self._ready_event.set()
            return

        from tqsdk import TqAccount, TqApi, TqKq

        while not self._stopped:
            api = None
            try:
                auth_obj = get_shared_auth(self.account.tq_account, self.account.tq_password)
                if self.account.kind == "live":
                    account_obj = TqAccount(
                        self.account.broker,
                        self.account.trade_account,
                        self.account.trade_password,
                    )
                else:
                    account_obj = TqKq()

                api = TqApi(account_obj, auth=auth_obj)
                acc = api.get_account()

                # 等待首次数据截面同步就绪
                deadline = time.time() + 20.0
                while _number(acc.balance) is None and time.time() < deadline and not self._stopped:
                    api.wait_update(deadline=min(deadline, time.time() + 1.0))

                if self._stopped:
                    break

                # 初始快照就绪
                self._error = ""
                self._refresh_and_notify(api, acc)
                self._ready_event.set()

                # 常驻收包推进循环：有数据变化时更新快照并广播
                while not self._stopped:
                    changed = api.wait_update(deadline=time.time() + 1.0)
                    if changed and not self._stopped:
                        self._refresh_and_notify(api, acc)

            except Exception as e:
                _logger.warning("账户 %s 交易记录监听异常: %s", self.account.id, e)
                self._error = str(e)
                self._ready_event.set()
            finally:
                if api is not None:
                    try:
                        api.close()
                    except Exception:
                        pass

            # 异常断线后休眠 15 秒（可被 stop 打断）后重试
            for _ in range(30):
                if self._stopped:
                    break
                time.sleep(0.5)

    def _refresh_and_notify(self, api, acc) -> None:
        new_snapshot = _build_snapshot_dict(self.account, api, acc)
        with self._snapshot_lock:
            old_snapshot = self._snapshot
            if old_snapshot == new_snapshot:
                return
            self._snapshot = new_snapshot

        if self._history_repo is not None and new_snapshot.get("trades"):
            try:
                self._history_repo.save_trades(self.account.id, new_snapshot["trades"])
            except Exception as e:
                _logger.warning("历史成交自动持久化异常: %s", e)

        if self._on_update is not None and not self._stopped:
            try:
                self._on_update(self.account.id, self.account.kind, new_snapshot)
            except Exception as e:
                _logger.error("记录更新广播回调异常: %s", e)



class AccountRecordsManager:
    """管理所有账户的记录监听会话。"""

    def __init__(
        self,
        on_update: Optional[Callable[[str, str, dict], None]] = None,
        history_repo: Optional[TradeHistoryRepository] = None,
    ):
        self._on_update = on_update
        self._history_repo = history_repo
        self._watchers: dict[str, AccountRecordsWatcher] = {}
        self._lock = threading.Lock()

    def sync_accounts(self, accounts: list[Account]) -> None:
        """根据最新账户列表同步 watcher 会话（增/删/凭据变更重连）。"""
        account_map = {a.id: a for a in accounts}
        with self._lock:
            # 清理已删除的账户
            for acc_id, watcher in list(self._watchers.items()):
                if acc_id not in account_map:
                    watcher.stop()
                    del self._watchers[acc_id]
                else:
                    new_acc = account_map[acc_id]
                    if watcher._fingerprint != AccountRecordsWatcher._calc_fingerprint(new_acc):
                        watcher.stop()
                        del self._watchers[acc_id]

            # 启动新增或变更的账户
            for acc_id, acc in account_map.items():
                if acc_id not in self._watchers:
                    watcher = AccountRecordsWatcher(
                        acc,
                        on_update=self._on_update,
                        history_repo=self._history_repo,
                    )
                    self._watchers[acc_id] = watcher
                    watcher.start()

    def get_records(
        self,
        accounts: list[Account],
        wait_timeout: float = 0.5,
        date_range: str = "today",
    ) -> list[dict]:
        """按账户列表获取快照数据（毫秒级）。支持 date_range 历史范围筛选。"""
        records: list[dict] = []
        with self._lock:
            watchers = dict(self._watchers)

        for acc in accounts:
            watcher = watchers.get(acc.id)
            if watcher is not None:
                snapshot = watcher.get_snapshot(wait_timeout=wait_timeout)
            else:
                snapshot = {
                    "account_id": acc.id,
                    "kind": acc.kind,
                    "label": acc.label or acc.id,
                    "error": "未启动监听会话",
                    "account": {},
                    "positions": [],
                    "orders": [],
                    "trades": [],
                }

            # 跨日或全部历史查询：若指定了非 today，从 SQLite 历史库中提取对应时间区间的成交
            if date_range != "today" and self._history_repo is not None:
                try:
                    historical_trades = self._history_repo.query_trades(
                        acc.id, date_range=date_range
                    )
                    snapshot["trades"] = historical_trades
                except Exception as e:
                    _logger.warning("查询历史成交异常: %s", e)

            records.append(snapshot)
        return records


    def close(self) -> None:
        """释放所有常驻会话。"""
        with self._lock:
            for watcher in self._watchers.values():
                watcher.stop()
            self._watchers.clear()
        clear_auth_cache()

