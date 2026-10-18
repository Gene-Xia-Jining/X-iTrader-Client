import sqlite3
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator, Optional

from ...domain.repositories import TokenStore
from ..security.crypto import PasswordCipher, load_or_create_key


class DbTokenStore(TokenStore):
    """API token 加密存储到 SQLite（与配置共用 data/client.db）。

    token 经 PasswordCipher 加密后入库，load_token() 返回明文，
    对 TokenApiService 与调用方透明。构造时一次性迁移旧的
    tokens/api_token.txt 明文文件（导入后删除）。
    """

    def __init__(self, db_path: str = "data/client.db"):
        self.db_path = str(db_path)
        Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)
        key_path = Path(self.db_path).parent / ".secret.key"
        self._cipher = PasswordCipher(load_or_create_key(key_path))
        self._init_table()
        self._migrate_legacy_file()

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
                CREATE TABLE IF NOT EXISTS api_token (
                    id INTEGER PRIMARY KEY DEFAULT 1,
                    token TEXT NOT NULL DEFAULT '',
                    CHECK (id = 1)
                )
            """)
            # 记录当前 token 来自哪条申请记录，供界面标出「使用中」并进行切换
            columns = {row[1] for row in db.execute("PRAGMA table_info(api_token)")}
            if "token_id" not in columns:
                db.execute("ALTER TABLE api_token ADD COLUMN token_id TEXT")
            # 历史凭据缓存：服务器只存 hash，明文仅在领取窗口出现一次，
            # 不在本地留一份就无法切回用过的 Token。同样加密存储。
            db.execute("""
                CREATE TABLE IF NOT EXISTS api_token_history (
                    token_id TEXT PRIMARY KEY,
                    token TEXT NOT NULL,
                    saved_at INTEGER NOT NULL
                )
            """)
            db.commit()

    def _migrate_legacy_file(self):
        """导入旧版 FileTokenStore 的明文文件后删除；清理失败仅忽略——
        token 已入库，重复迁移幂等且无功能影响。"""
        legacy = Path(self.db_path).parent / "tokens" / "api_token.txt"
        try:
            if not legacy.exists():
                return
            token = legacy.read_text(encoding="utf-8").strip()
            if token and not self.have_token():
                self.save_token(token)
            legacy.unlink()
            legacy.parent.rmdir()
        except OSError:
            pass

    def have_token(self) -> bool:
        return bool(self.load_token())

    def load_token(self) -> Optional[str]:
        with self._conn() as db:
            row = db.execute("SELECT token FROM api_token WHERE id = 1").fetchone()
        if row and row[0]:
            # 密钥丢失导致解密失败时视为无 token，走重新申请流程
            return self._cipher.decrypt(row[0]) or None
        return None

    def load_token_id(self) -> Optional[str]:
        """当前生效 token 对应的申请记录 id（旧数据可能为空）。"""
        with self._conn() as db:
            row = db.execute("SELECT token_id FROM api_token WHERE id = 1").fetchone()
        return row[0] if row and row[0] else None

    def save_token(self, token: str, token_id: Optional[str] = None) -> None:
        with self._conn() as db:
            db.execute(
                "INSERT INTO api_token (id, token, token_id) VALUES (1, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET token = excluded.token,"
                " token_id = excluded.token_id",
                (self._cipher.encrypt(token), token_id),
            )
            if token_id:
                db.execute(
                    "INSERT INTO api_token_history (token_id, token, saved_at)"
                    " VALUES (?, ?, ?)"
                    " ON CONFLICT(token_id) DO UPDATE SET token = excluded.token",
                    (token_id, self._cipher.encrypt(token), int(time.time())),
                )
            db.commit()

    def load_cached_token(self, token_id: str) -> Optional[str]:
        """取该申请记录曾在本地用过的明文 token（用于切换回旧记录）。"""
        with self._conn() as db:
            row = db.execute(
                "SELECT token FROM api_token_history WHERE token_id = ?",
                (token_id,),
            ).fetchone()
        if row and row[0]:
            return self._cipher.decrypt(row[0]) or None
        return None

    def activate_cached_token(self, token_id: str) -> bool:
        """把缓存里的 token 设为当前使用；没有缓存返回 False。"""
        token = self.load_cached_token(token_id)
        if not token:
            return False
        self.save_token(token, token_id)
        return True

    def delete_cached_token(self, token_id: str) -> None:
        """记录被删除后清掉对应缓存，避免留下无法再用的凭据。"""
        with self._conn() as db:
            db.execute("DELETE FROM api_token_history WHERE token_id = ?", (token_id,))
            db.commit()

    def cached_token_ids(self) -> set[str]:
        """本地缓存过明文的记录 id 集合（界面据此判断哪些行可切换）。"""
        with self._conn() as db:
            rows = db.execute("SELECT token_id FROM api_token_history").fetchall()
        return {row[0] for row in rows if row[0]}

    def delete_token(self) -> None:
        with self._conn() as db:
            db.execute("DELETE FROM api_token WHERE id = 1")
            db.commit()
