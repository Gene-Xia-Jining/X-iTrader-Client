import json
import sqlite3
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator, Optional

from ...domain.entities import Account, TradingConfiguration
from ..security.crypto import PasswordCipher, load_or_create_key


class ConfigService:
    """配置持久化到 SQLite（与交易记录共用 data/client.db）。

    app_config 表存放与账户无关的全局配置；accounts 表存放交易账户。
    使用标准库 sqlite3 同步访问，保持与 Bootstrap.config 同步属性兼容。
    密码字段（tq_password/trade_password）入库前加密，load 返回明文，对消费方透明。
    """

    def __init__(self, db_path: str = "data/client.db"):
        self.db_path = str(db_path)
        Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)
        key_path = Path(self.db_path).parent / ".secret.key"
        self._cipher = PasswordCipher(load_or_create_key(key_path))
        self._init_table()

    @contextmanager
    def _conn(self) -> Iterator[sqlite3.Connection]:
        conn = sqlite3.connect(self.db_path)
        try:
            yield conn
        finally:
            conn.close()

    def _init_table(self):
        with self._conn() as db:
            db.execute("""
                CREATE TABLE IF NOT EXISTS app_config (
                    id INTEGER PRIMARY KEY DEFAULT 1,
                    server_url TEXT NOT NULL,
                    symbols TEXT NOT NULL,
                    auto_trade INTEGER NOT NULL,
                    tq_account TEXT NOT NULL,
                    tq_password TEXT NOT NULL,
                    trade_account TEXT NOT NULL DEFAULT '',
                    trade_password TEXT NOT NULL DEFAULT '',
                    initial_balance REAL NOT NULL,
                    database TEXT NOT NULL,
                    CHECK (id = 1)
                )
            """)
            try:
                db.execute("ALTER TABLE app_config ADD COLUMN trade_account TEXT NOT NULL DEFAULT ''")
            except sqlite3.OperationalError:
                pass
            try:
                db.execute("ALTER TABLE app_config ADD COLUMN trade_password TEXT NOT NULL DEFAULT ''")
            except sqlite3.OperationalError:
                pass
            try:
                db.execute("ALTER TABLE app_config ADD COLUMN proxy_url TEXT NOT NULL DEFAULT ''")
            except sqlite3.OperationalError:
                pass
            try:
                db.execute("ALTER TABLE app_config ADD COLUMN auto_check_update INTEGER NOT NULL DEFAULT 1")
            except sqlite3.OperationalError:
                pass
            try:
                db.execute("ALTER TABLE app_config ADD COLUMN skipped_version TEXT NOT NULL DEFAULT ''")
            except sqlite3.OperationalError:
                pass
            # 快期账户与模拟账户凭据已合并为 tq_account/tq_password：
            # 先把只在模拟页填过的凭据搬入 tq 列（同密钥，密文直接复制），再删除 sim 列
            try:
                db.execute(
                    "UPDATE app_config SET tq_account = sim_account, tq_password = sim_password"
                    " WHERE id = 1 AND tq_account = '' AND sim_account != ''"
                )
            except sqlite3.OperationalError:
                pass  # 旧库无 sim 列
            for column in ("sim_account", "sim_password"):
                try:
                    db.execute(f"ALTER TABLE app_config DROP COLUMN {column}")
                except sqlite3.OperationalError:
                    pass  # 列已删除或旧 SQLite 不支持 DROP COLUMN（残留无害）

            db.execute("""
                CREATE TABLE IF NOT EXISTS accounts (
                    id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    label TEXT NOT NULL,
                    tq_account TEXT NOT NULL,
                    tq_password TEXT NOT NULL,
                    broker TEXT NOT NULL DEFAULT '',
                    trade_account TEXT NOT NULL DEFAULT '',
                    trade_password TEXT NOT NULL DEFAULT '',
                    symbols TEXT NOT NULL DEFAULT '[]',
                    strategy TEXT NOT NULL DEFAULT '',
                    enabled INTEGER NOT NULL DEFAULT 1
                )
            """)
            # 账户级交易策略选择（服务器 /api/strategies 的策略 id）
            try:
                db.execute("ALTER TABLE accounts ADD COLUMN strategy TEXT NOT NULL DEFAULT ''")
            except sqlite3.OperationalError:
                pass  # 列已存在
            # 早版实现带 sim_number 列（TqKq 辅模拟账号编号）；模拟账户现固定单账户，删除
            try:
                db.execute("ALTER TABLE accounts DROP COLUMN sim_number")
            except sqlite3.OperationalError:
                pass  # 列不存在，或旧 SQLite 不支持 DROP COLUMN（残留无害）
            self._migrate_legacy_account(db)
            db.commit()

    def _migrate_legacy_account(self, db) -> None:
        """把旧版 app_config 里的单账户数据迁移为 accounts 表一条记录。

        旧结构把账户凭据存在 app_config 单行里；新结构账户独立成表，
        支持模拟/实盘各多个。仅在 accounts 为空且旧数据有效时迁移，幂等。
        密码是同密钥密文，直接复制不重加密（与 sim_account→tq_account 迁移同范式）。
        """
        count = db.execute("SELECT COUNT(*) FROM accounts").fetchone()[0]
        if count > 0:
            return
        row = db.execute("SELECT * FROM app_config WHERE id = 1").fetchone()
        if row is None:
            return
        columns = [info[1] for info in db.execute("PRAGMA table_info(app_config)")]
        row = dict(zip(columns, row))
        tq_account = row.get("tq_account", "")
        trade_account = row.get("trade_account", "")
        trade_password = row.get("trade_password", "")
        if not tq_account and not trade_account:
            return
        # 填了资金账号与交易密码即视为实盘，否则视为模拟
        kind = "live" if (trade_account and trade_password) else "sim"
        db.execute(
            """
            INSERT INTO accounts (
                id, kind, label, tq_account, tq_password, broker,
                trade_account, trade_password, symbols, enabled
            ) VALUES (?, ?, ?, ?, ?, '', ?, ?, ?, 1)
            """,
            (
                "legacy" if kind == "sim" else "legacy-live",
                kind,
                "模拟账户" if kind == "sim" else "实盘账户",
                tq_account,
                row.get("tq_password", ""),
                trade_account,
                trade_password,
                row.get("symbols", "[]"),
            ),
        )

    def load(self) -> TradingConfiguration:
        with self._conn() as db:
            db.row_factory = sqlite3.Row
            row = db.execute("SELECT * FROM app_config WHERE id = 1 LIMIT 1").fetchone()
        if row is not None:
            return TradingConfiguration(
                server_url=row["server_url"],
                auto_trade=bool(row["auto_trade"]),
                proxy_url=row["proxy_url"] if "proxy_url" in row.keys() else "",
                auto_check_update=bool(row["auto_check_update"]) if "auto_check_update" in row.keys() else True,
                skipped_version=row["skipped_version"] if "skipped_version" in row.keys() else "",
                database_path=row["database"],
            )
        # 首次运行：写入默认配置
        config = TradingConfiguration(
            server_url="http://192.168.100.100:3080",
            auto_trade=False,
            proxy_url="",
            auto_check_update=True,
            skipped_version="",
            database_path=self.db_path,
        )
        self.save(config)
        return config

    def save(self, config: TradingConfiguration) -> None:
        with self._conn() as db:
            db.execute("""
                INSERT INTO app_config
                    (id, server_url, symbols, auto_trade, tq_account, tq_password, trade_account, trade_password, proxy_url, auto_check_update, skipped_version, initial_balance, database)
                VALUES (1, ?, '[]', ?, '', '', '', '', ?, ?, ?, 10000000, ?)
                ON CONFLICT(id) DO UPDATE SET
                    server_url = excluded.server_url,
                    auto_trade = excluded.auto_trade,
                    proxy_url = excluded.proxy_url,
                    auto_check_update = excluded.auto_check_update,
                    skipped_version = excluded.skipped_version,
                    database = excluded.database
            """, (
                config.server_url,
                int(config.auto_trade),
                config.proxy_url,
                int(config.auto_check_update),
                config.skipped_version,
                config.database_path,
            ))
            db.commit()

    def load_accounts(self) -> list[Account]:
        with self._conn() as db:
            db.row_factory = sqlite3.Row
            rows = db.execute("SELECT * FROM accounts ORDER BY kind, id").fetchall()
        return [
            Account(
                id=row["id"],
                kind=row["kind"],
                label=row["label"],
                tq_account=row["tq_account"],
                tq_password=self._cipher.decrypt(row["tq_password"]),
                broker=row["broker"],
                trade_account=row["trade_account"],
                trade_password=self._cipher.decrypt(row["trade_password"]),
                symbols=json.loads(row["symbols"]),
                strategy=row["strategy"],
                enabled=bool(row["enabled"]),
            )
            for row in rows
        ]

    def save_account(self, account: Account) -> None:
        """新增或更新账户（按 id 幂等 upsert）。"""
        with self._conn() as db:
            db.execute(
                """
                INSERT INTO accounts (
                    id, kind, label, tq_account, tq_password, broker,
                    trade_account, trade_password, symbols, strategy, enabled
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    label = excluded.label,
                    tq_account = excluded.tq_account,
                    tq_password = excluded.tq_password,
                    broker = excluded.broker,
                    trade_account = excluded.trade_account,
                    trade_password = excluded.trade_password,
                    symbols = excluded.symbols,
                    strategy = excluded.strategy,
                    enabled = excluded.enabled
                """,
                (
                    account.id,
                    account.kind,
                    account.label,
                    account.tq_account,
                    self._cipher.encrypt(account.tq_password),
                    account.broker,
                    account.trade_account,
                    self._cipher.encrypt(account.trade_password),
                    json.dumps(account.symbols, ensure_ascii=False),
                    account.strategy,
                    int(account.enabled),
                ),
            )
            db.commit()

    def delete_account(self, account_id: str) -> None:
        with self._conn() as db:
            db.execute("DELETE FROM accounts WHERE id = ?", (account_id,))
            db.commit()

    def get_account(self, account_id: str) -> Optional[Account]:
        for account in self.load_accounts():
            if account.id == account_id:
                return account
        return None
