"""设备身份：稳定 device_id + 服务端签发的 device_key。

对应服务端 doc/IDENTITY.md 的 R1 / R2。与旧的 ``data/client_id.txt`` 的区别：

- **绝对路径**：落在 ``~/.X-iTrader-Client/device_id``，不跟 cwd 走，也不在
  ``data/`` 目录下——「清理数据」不该顺手把身份一起删掉。
- **128 bit 熵**：旧值是 ``uuid4().hex[:8]``（32 bit），约 7.7 万个客户端就有
  50% 碰撞概率。
- **文件丢失可重建**：用 macOS ``IOPlatformUUID`` 作种子派生，删除文件不再
  等于「变成一台新机器」。

``device_key`` 由服务器签发、只回传一次，本地经 Fernet 加密保存在
``data/client.db`` 的 device_identity 表（复用现有 .secret.key，不新增机制）。
"""
import hashlib
import os
import platform
import subprocess
import uuid
from pathlib import Path
from typing import Optional

from ..security.crypto import PasswordCipher, load_or_create_key

# 设备 id 文件放在数据根下、但在 data/ 之外（见模块注释）
DEVICE_ID_FILENAME = "device_id"


def app_home() -> Path:
    """``~/.X-iTrader-Client``（与 infrastructure.paths.data_root 同源）。"""
    return Path.home() / ".X-iTrader-Client"


def device_id_path() -> Path:
    return app_home() / DEVICE_ID_FILENAME


def platform_uuid() -> Optional[str]:
    """读取 macOS IOPlatformUUID；其它平台或无命令时返回 None。

    只用作**本地重建种子**，绝不上送服务器：它是硬件标识符，任何本机程序
    都能读到，既不适合当身份暴露，也不足以证明身份（见 IDENTITY.md 3.3）。
    """
    if platform.system() != "Darwin":
        return None
    try:
        output = subprocess.run(
            ["ioreg", "-rd1", "-c", "IOPlatformExpertDevice"],
            capture_output=True, text=True, timeout=5,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    for line in output.splitlines():
        if "IOPlatformUUID" in line:
            # 形如 `    "IOPlatformUUID" = "612F4C16-..."`，取最后一个引号对
            parts = line.split('"')
            if len(parts) >= 4:
                return parts[-2]
    return None


def _derive_from_seed(seed: str) -> str:
    """由硬件标识派生稳定的 32 位十六进制串。"""
    return hashlib.sha256(f"x-itrader-device:{seed}".encode("utf-8")).hexdigest()[:32]


def load_or_create_device_id(path: Optional[Path] = None) -> str:
    """读取或生成 device_id。

    生成优先级：
    1. 已有文件 → 直接用（**绝不复算**，否则改了实现就会漂移）；
    2. IOPlatformUUID 可读 → 由它派生（这样恢复备份/删文件都能回到同一身份）；
    3. 都不可用 → 随机 uuid4 全量。
    """
    path = Path(path) if path else device_id_path()
    if path.exists():
        value = path.read_text(encoding="utf-8").strip()
        if value:
            return value

    seed = platform_uuid()
    device_id = _derive_from_seed(seed) if seed else uuid.uuid4().hex

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(device_id, encoding="utf-8")
    try:
        # 只有本人可读写：密钥不是它，但设备身份也不该被同机其他用户顺手读到
        path.chmod(0o600)
    except OSError:
        pass
    return device_id


def read_legacy_client_id() -> Optional[str]:
    """读取旧版 ``data/client_id.txt``（相对当前 cwd）。

    仅供一次性认领：服务器据此把老 client_id 名下的 token 挂到新身份上，
    用户不必重新批准（见 IDENTITY.md 5.3）。读不到就返回 None。
    """
    legacy = Path("data/client_id.txt")
    try:
        if not legacy.exists():
            return None
        value = legacy.read_text(encoding="utf-8").strip()
        return value or None
    except OSError:
        return None


def device_label() -> str:
    """设备展示名：macOS 用 ComputerName，其它平台退到 hostname。"""
    if platform.system() == "Darwin":
        try:
            name = subprocess.run(
                ["scutil", "--get", "ComputerName"],
                capture_output=True, text=True, timeout=5,
            ).stdout.strip()
            if name:
                return name
        except (OSError, subprocess.SubprocessError):
            pass
    return platform.node() or ""


class DeviceIdentityStore:
    """device_key 的本地加密存储（与 token 共用 data/client.db 与 .secret.key）。

    device_key 由服务器签发且只回传一次，丢失后必须先在管理端撤销设备再
    重新登记，所以要落盘；用与 token 相同的 Fernet 机制加密，不新增依赖。
    """

    def __init__(self, db_path: str = "data/client.db"):
        self.db_path = str(db_path)
        Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)
        key_path = Path(self.db_path).parent / ".secret.key"
        self._cipher = PasswordCipher(load_or_create_key(key_path))
        self._init_table()

    def _connect(self):
        import sqlite3
        return sqlite3.connect(self.db_path)

    def _init_table(self):
        with self._connect() as db:
            db.execute("""
                CREATE TABLE IF NOT EXISTS device_identity (
                    id INTEGER PRIMARY KEY DEFAULT 1,
                    device_id TEXT NOT NULL DEFAULT '',
                    device_key TEXT NOT NULL DEFAULT '',
                    registered_at INTEGER NOT NULL DEFAULT 0,
                    CHECK (id = 1)
                )
            """)
            db.commit()

    def load(self) -> tuple[Optional[str], Optional[str]]:
        """返回 (device_id, 明文 device_key)；未登记时两者为 None。"""
        with self._connect() as db:
            row = db.execute(
                "SELECT device_id, device_key, registered_at FROM device_identity WHERE id = 1"
            ).fetchone()
        if not row or not row[0]:
            return None, None
        key = self._cipher.decrypt(row[1]) if row[1] else None
        return row[0], key or None

    def save(self, device_id: str, device_key: Optional[str]) -> None:
        """保存登记结果。device_key 为 None 时保留原密钥（服务器不重发的情况）。"""
        import time
        existing_id, existing_key = self.load()
        key_to_store = device_key or (existing_key if existing_id == device_id else None)
        with self._connect() as db:
            db.execute(
                "INSERT INTO device_identity (id, device_id, device_key, registered_at)"
                " VALUES (1, ?, ?, ?)"
                " ON CONFLICT(id) DO UPDATE SET device_id = excluded.device_id,"
                " device_key = excluded.device_key, registered_at = excluded.registered_at",
                (
                    device_id,
                    self._cipher.encrypt(key_to_store) if key_to_store else "",
                    int(time.time()),
                ),
            )
            db.commit()

    def clear(self) -> None:
        """设备被撤销时清除本地凭据，下次启动会重新登记。"""
        with self._connect() as db:
            db.execute("DELETE FROM device_identity WHERE id = 1")
            db.commit()
