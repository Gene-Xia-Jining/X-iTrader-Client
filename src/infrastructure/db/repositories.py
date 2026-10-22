import sqlite3
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Optional

import aiosqlite

from ...domain.entities import TradeCommand, TradeCommandStatus
from ...domain.repositories import TradeCommandRepository, TradeHistoryRepository

_BEIJING = timezone(timedelta(hours=8))


class SQLiteTradeCommandRepository(TradeCommandRepository):
    def __init__(self, db_path: str):
        self.db_path = db_path
        Path(db_path).parent.mkdir(parents=True, exist_ok=True)

    async def init(self):
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute("""
                CREATE TABLE IF NOT EXISTS trade_commands (
                    account_id TEXT NOT NULL DEFAULT '',
                    id TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    side TEXT,
                    price REAL NOT NULL,
                    target_position INTEGER NOT NULL,
                    received_at INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    executed_at INTEGER,
                    order_id TEXT,
                    error TEXT,
                    PRIMARY KEY (account_id, id)
                )
            """)
            await db.execute("""
                CREATE INDEX IF NOT EXISTS idx_trade_commands_symbol
                ON trade_commands(symbol)
            """)
            await db.execute("""
                CREATE INDEX IF NOT EXISTS idx_trade_commands_status
                ON trade_commands(status)
            """)
            await self._migrate_account_id(db)
            await db.commit()

    @staticmethod
    async def _migrate_account_id(db) -> None:
        """把旧版单账户表（id PRIMARY KEY）重建为 (account_id, id) 复合主键。

        SQLite 不支持 ALTER PRIMARY KEY，只能重建表。历史数据回填 account_id=''。
        """
        cursor = await db.execute("PRAGMA table_info(trade_commands)")
        columns = {row[1] for row in await cursor.fetchall()}
        if "account_id" in columns:
            return
        await db.execute("PRAGMA foreign_keys=OFF")
        await db.execute("""
            CREATE TABLE trade_commands_new (
                account_id TEXT NOT NULL DEFAULT '',
                id TEXT NOT NULL,
                symbol TEXT NOT NULL,
                side TEXT,
                price REAL NOT NULL,
                target_position INTEGER NOT NULL,
                received_at INTEGER NOT NULL,
                status TEXT NOT NULL,
                executed_at INTEGER,
                order_id TEXT,
                error TEXT,
                PRIMARY KEY (account_id, id)
            )
        """)
        await db.execute("""
            INSERT INTO trade_commands_new (
                account_id, id, symbol, side, price, target_position,
                received_at, status, executed_at, order_id, error
            )
            SELECT '', id, symbol, side, price, target_position,
                   received_at, status, executed_at, order_id, error
            FROM trade_commands
        """)
        await db.execute("DROP TABLE trade_commands")
        await db.execute("ALTER TABLE trade_commands_new RENAME TO trade_commands")
        await db.execute("""
            CREATE INDEX idx_trade_commands_symbol
            ON trade_commands(symbol)
        """)
        await db.execute("""
            CREATE INDEX idx_trade_commands_status
            ON trade_commands(status)
        """)
        await db.execute("PRAGMA foreign_keys=ON")

    async def exists(self, account_id: str, command_id: str) -> bool:
        async with aiosqlite.connect(self.db_path) as db:
            cursor = await db.execute(
                "SELECT 1 FROM trade_commands WHERE account_id = ? AND id = ? LIMIT 1",
                (account_id, command_id),
            )
            return await cursor.fetchone() is not None

    async def add(self, command: TradeCommand) -> None:
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute(
                """
                INSERT INTO trade_commands (
                    account_id, id, symbol, side, price, target_position, received_at, status
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    command.account_id,
                    command.id,
                    command.symbol,
                    command.side,
                    command.price,
                    command.target_position,
                    command.received_at,
                    command.status.value,
                ),
            )
            await db.commit()

    async def get(self, account_id: str, command_id: str) -> Optional[TradeCommand]:
        async with aiosqlite.connect(self.db_path) as db:
            db.row_factory = aiosqlite.Row
            cursor = await db.execute(
                "SELECT * FROM trade_commands WHERE account_id = ? AND id = ? LIMIT 1",
                (account_id, command_id),
            )
            row = await cursor.fetchone()
            if row is None:
                return None
            return TradeCommand(
                account_id=row["account_id"],
                id=row["id"],
                symbol=row["symbol"],
                side=row["side"],
                price=row["price"],
                target_position=row["target_position"],
                received_at=row["received_at"],
                status=TradeCommandStatus(row["status"]),
                executed_at=row["executed_at"],
                order_id=row["order_id"],
                error=row["error"],
            )

    async def set_status(
        self,
        account_id: str,
        command_id: str,
        status: TradeCommandStatus,
        error: Optional[str] = None,
        order_id: Optional[str] = None,
    ) -> None:
        async with aiosqlite.connect(self.db_path) as db:
            if status == TradeCommandStatus.EXECUTING:
                await db.execute(
                    "UPDATE trade_commands SET status = ? WHERE account_id = ? AND id = ?",
                    (status.value, account_id, command_id),
                )
            elif status == TradeCommandStatus.EXECUTED:
                await db.execute(
                    """
                    UPDATE trade_commands
                    SET status = ?, executed_at = ?, order_id = ?
                    WHERE account_id = ? AND id = ?
                    """,
                    (status.value, int(time.time()), order_id, account_id, command_id),
                )
            elif status == TradeCommandStatus.FAILED:
                if error is None:
                    raise ValueError("错误信息不能为空")
                await db.execute(
                    """
                    UPDATE trade_commands
                    SET status = ?, error = ?
                    WHERE account_id = ? AND id = ?
                    """,
                    (status.value, error, account_id, command_id),
                )
            elif status == TradeCommandStatus.PENDING:
                await db.execute(
                    "UPDATE trade_commands SET status = ? WHERE account_id = ? AND id = ?",
                    (status.value, account_id, command_id),
                )
            else:
                raise ValueError(f"未知状态: {status}")
            await db.commit()


class SQLiteTradeHistoryRepository(TradeHistoryRepository):
    """本地 SQLite 历史成交仓储。

    用于持久化沉淀 TqSdk 当日成交并在跨交易日后提供追溯查询能力。
    采用标准库 sqlite3 同步接口以在常驻守护线程与 RPC 事件流中安全复用。
    """

    def __init__(self, db_path: str):
        self.db_path = db_path
        Path(db_path).parent.mkdir(parents=True, exist_ok=True)
        self.init()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.db_path, timeout=10.0)
        conn.row_factory = sqlite3.Row
        return conn

    def init(self) -> None:
        with self._connect() as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS trade_history (
                    account_id TEXT NOT NULL,
                    trade_id TEXT NOT NULL,
                    order_id TEXT NOT NULL DEFAULT '',
                    symbol TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    direction_text TEXT NOT NULL DEFAULT '',
                    offset TEXT NOT NULL,
                    offset_text TEXT NOT NULL DEFAULT '',
                    price REAL NOT NULL,
                    volume INTEGER NOT NULL,
                    trade_time TEXT NOT NULL,
                    trade_date TEXT NOT NULL,
                    commission REAL,
                    realized_profit REAL,
                    source TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (account_id, trade_id)
                )
            """)
            conn.execute("""
                CREATE INDEX IF NOT EXISTS idx_trade_history_account_date
                ON trade_history(account_id, trade_date)
            """)
            conn.commit()

    def save_trades(self, account_id: str, trades: list[dict]) -> None:
        """批量保存或更新成交记录（幂等 upsert）。"""
        if not trades or not account_id:
            return
        today_str = datetime.now(_BEIJING).strftime("%Y-%m-%d")
        rows = []
        for t in trades:
            trade_id = str(t.get("trade_id") or "")
            if not trade_id:
                continue
            trade_time = str(t.get("trade_time") or "")
            trade_date = trade_time[:10] if len(trade_time) >= 10 else today_str
            rows.append(
                (
                    account_id,
                    trade_id,
                    str(t.get("order_id") or ""),
                    str(t.get("symbol") or ""),
                    str(t.get("direction") or ""),
                    str(t.get("direction_text") or ""),
                    str(t.get("offset") or ""),
                    str(t.get("offset_text") or ""),
                    float(t.get("price") or 0.0),
                    int(t.get("volume") or 0),
                    trade_time,
                    trade_date,
                    t.get("commission"),
                    t.get("realized_profit"),
                    str(t.get("source") or ""),
                )
            )
        if not rows:
            return
        with self._connect() as conn:
            conn.executemany(
                """
                INSERT INTO trade_history (
                    account_id, trade_id, order_id, symbol, direction,
                    direction_text, offset, offset_text, price, volume,
                    trade_time, trade_date, commission, realized_profit, source
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(account_id, trade_id) DO UPDATE SET
                    order_id=excluded.order_id,
                    symbol=excluded.symbol,
                    direction=excluded.direction,
                    direction_text=excluded.direction_text,
                    offset=excluded.offset,
                    offset_text=excluded.offset_text,
                    price=excluded.price,
                    volume=excluded.volume,
                    trade_time=excluded.trade_time,
                    trade_date=excluded.trade_date,
                    commission=COALESCE(excluded.commission, trade_history.commission),
                    realized_profit=COALESCE(excluded.realized_profit, trade_history.realized_profit),
                    source=COALESCE(NULLIF(excluded.source, ''), trade_history.source)
                """,
                rows,
            )
            conn.commit()

    def query_trades(
        self, account_id: str, date_range: str = "today", limit: int = 500
    ) -> list[dict]:
        """按日期范围查询成交历史。

        date_range 选项：
        - "today": 当日
        - "3d": 近 3 天（含今日）
        - "7d": 近 7 天（含今日）
        - "all": 全部历史
        """
        now = datetime.now(_BEIJING)
        where_clause = "account_id = ?"
        params: list = [account_id]

        if date_range == "today":
            where_clause += " AND trade_date >= ?"
            params.append(now.strftime("%Y-%m-%d"))
        elif date_range == "3d":
            start_date = (now - timedelta(days=2)).strftime("%Y-%m-%d")
            where_clause += " AND trade_date >= ?"
            params.append(start_date)
        elif date_range == "7d":
            start_date = (now - timedelta(days=6)).strftime("%Y-%m-%d")
            where_clause += " AND trade_date >= ?"
            params.append(start_date)
        # "all" 不添加日期下限

        sql = f"""
            SELECT trade_id, order_id, symbol, direction, direction_text,
                   offset, offset_text, price, volume, trade_time,
                   commission, realized_profit, source
            FROM trade_history
            WHERE {where_clause}
            ORDER BY trade_time DESC, trade_id DESC
            LIMIT ?
        """
        params.append(limit)

        with self._connect() as conn:
            cursor = conn.execute(sql, params)
            results = []
            for row in cursor.fetchall():
                results.append(
                    {
                        "trade_id": row["trade_id"],
                        "order_id": row["order_id"],
                        "symbol": row["symbol"],
                        "direction": row["direction"],
                        "direction_text": row["direction_text"],
                        "offset": row["offset"],
                        "offset_text": row["offset_text"],
                        "price": row["price"],
                        "volume": row["volume"],
                        "trade_time": row["trade_time"],
                        "commission": row["commission"],
                        "realized_profit": row["realized_profit"],
                        "source": row["source"],
                    }
                )
            return results

