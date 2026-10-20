import sqlite3
import tempfile
import unittest
from pathlib import Path

from cryptography.fernet import Fernet

from src.domain.entities import Account, TradingConfiguration
from src.infrastructure.security.crypto import PasswordCipher, load_or_create_key
from src.infrastructure.config.config_service import (
    ConfigService,
    DEFAULT_SERVER_URL,
    LEGACY_DEFAULT_SERVER_URLS,
)


def _make_config(**overrides):
    fields = dict(
        server_url="http://127.0.0.1:3080",
        auto_trade=False,
        database_path="data/client.db",
    )
    fields.update(overrides)
    return TradingConfiguration(**fields)


def _cipher(tmp: Path) -> PasswordCipher:
    return PasswordCipher(load_or_create_key(tmp / ".secret.key"))


def _make_account(**overrides):
    fields = dict(
        id="acc-1",
        kind="live",
        label="实盘账户",
        tq_account="13800000000",
        tq_password="tq-pwd",
        broker="宏源期货",
        trade_account="123456",
        trade_password="trade-pwd",
        symbols=["SHFE.au2510"],
        enabled=True,
    )
    fields.update(overrides)
    return Account(**fields)


class LoadOrCreateKeyTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_creates_key_file_once(self):
        path = self.dir / ".secret.key"
        key = load_or_create_key(path)
        self.assertTrue(path.exists())
        self.assertEqual(load_or_create_key(path), key)
        # 同目录两个 cipher 用同一密钥，密文可互换解密
        c1, c2 = PasswordCipher(key), PasswordCipher(key)
        self.assertEqual(c2.decrypt(c1.encrypt("pwd")), "pwd")


class PasswordCipherTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.cipher = _cipher(Path(self._tmp.name))

    def tearDown(self):
        self._tmp.cleanup()

    def test_roundtrip(self):
        token = self.cipher.encrypt("p@ss 密码123")
        self.assertNotIn("p@ss", token)
        self.assertTrue(token.startswith("enc:v1:"))
        self.assertEqual(self.cipher.decrypt(token), "p@ss 密码123")

    def test_empty_stays_empty(self):
        self.assertEqual(self.cipher.encrypt(""), "")
        self.assertEqual(self.cipher.decrypt(""), "")

    def test_legacy_plaintext_passthrough(self):
        # 历史明文（无前缀）读取时原样返回
        self.assertEqual(self.cipher.decrypt("old-plain-pwd"), "old-plain-pwd")

    def test_wrong_key_returns_empty(self):
        token = self.cipher.encrypt("pwd")
        other = PasswordCipher(Fernet.generate_key())
        self.assertEqual(other.decrypt(token), "")


class ConfigServiceEncryptionTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.db_path = Path(self._tmp.name) / "client.db"
        self.service = ConfigService(str(self.db_path))

    def tearDown(self):
        self._tmp.cleanup()

    def _stored_account(self, column: str, account_id: str) -> str:
        conn = sqlite3.connect(self.db_path)
        try:
            return conn.execute(
                f"SELECT {column} FROM accounts WHERE id = ?", (account_id,)
            ).fetchone()[0]
        finally:
            conn.close()

    def test_passwords_stored_encrypted(self):
        self.service.save_account(_make_account())
        for column, plain in [
            ("tq_password", "tq-pwd"),
            ("trade_password", "trade-pwd"),
        ]:
            stored = self._stored_account(column, "acc-1")
            self.assertTrue(stored.startswith("enc:v1:"), f"{column} 未加密: {stored}")
            self.assertNotIn(plain, stored)

    def test_load_accounts_returns_plaintext(self):
        self.service.save_account(_make_account())
        accounts = self.service.load_accounts()
        self.assertEqual(len(accounts), 1)
        self.assertEqual(accounts[0].tq_password, "tq-pwd")
        self.assertEqual(accounts[0].trade_password, "trade-pwd")
        self.assertEqual(accounts[0].symbols, ["SHFE.au2510"])

    def test_update_account_reencrypts_password(self):
        self.service.save_account(_make_account())
        self.service.save_account(_make_account(tq_password="new-pwd"))
        accounts = self.service.load_accounts()
        self.assertEqual(accounts[0].tq_password, "new-pwd")

    def test_delete_account_removes_row(self):
        self.service.save_account(_make_account())
        self.service.delete_account("acc-1")
        self.assertEqual(self.service.load_accounts(), [])
        self.assertIsNone(self.service.get_account("acc-1"))

    def test_strategy_roundtrip(self):
        self.service.save_account(_make_account(strategy="turtle_trading"))
        self.assertEqual(self.service.get_account("acc-1").strategy, "turtle_trading")
        # 更新其他字段（带原 strategy 值）不清空选择
        self.service.save_account(_make_account(tq_password="new-pwd", strategy="turtle_trading"))
        self.assertEqual(self.service.get_account("acc-1").strategy, "turtle_trading")
        # 显式传空字符串 = 清除选择
        self.service.save_account(_make_account(strategy=""))
        self.assertEqual(self.service.get_account("acc-1").strategy, "")

    def test_legacy_db_without_strategy_column_upgrades(self):
        # 旧库：accounts 表存在但没有 strategy 列，打开时应自动补列且旧数据可读
        self.service.save_account(_make_account())
        conn = sqlite3.connect(self.db_path)
        conn.execute("ALTER TABLE accounts DROP COLUMN strategy")
        conn.commit()
        conn.close()
        reopened = ConfigService(str(self.db_path))
        self.assertEqual(reopened.get_account("acc-1").strategy, "")
        account = reopened.get_account("acc-1")
        account.strategy = "ratio_reversion"
        reopened.save_account(account)
        self.assertEqual(reopened.get_account("acc-1").strategy, "ratio_reversion")

    def test_legacy_plaintext_db_loads_and_upgrades_on_save(self):
        # 模拟历史明文账户数据：accounts 表存在但密码列直接存明文（无 enc:v1: 前缀）
        self.service.save_account(_make_account())
        conn = sqlite3.connect(self.db_path)
        conn.execute("UPDATE accounts SET tq_password = 'plain-tq', trade_password = 'plain-trade' WHERE id = 'acc-1'")
        conn.commit()
        conn.close()

        # 读取：旧明文直通
        account = self.service.get_account("acc-1")
        self.assertEqual(account.tq_password, "plain-tq")
        # 保存：升级为密文
        account.tq_password = "plain-tq"
        self.service.save_account(account)
        self.assertTrue(self._stored_account("tq_password", "acc-1").startswith("enc:v1:"))
        self.assertEqual(self.service.get_account("acc-1").tq_password, "plain-tq")

    def test_legacy_app_config_migrates_to_accounts(self):
        # 旧库：单账户凭据存在 app_config 单行里，accounts 表为空。
        # 首次打开应迁移为 accounts 表一条记录，且密码同密钥密文直接搬入不重加密。
        # load() 首次运行会写入默认配置行，先确保 app_config 有行可改
        self.service.load()
        conn = sqlite3.connect(self.db_path)
        conn.execute("""
            UPDATE app_config SET
                tq_account = '13800000000', tq_password = 'enc:v1:old-cipher',
                trade_account = '123456', trade_password = 'enc:v1:old-trade',
                symbols = '["SHFE.au2510"]'
            WHERE id = 1
        """)
        conn.commit()
        conn.close()

        # 迁移在构造时执行，需重新实例化触发
        service = ConfigService(str(self.db_path))
        accounts = service.load_accounts()
        self.assertEqual(len(accounts), 1)
        # 填了资金账号与交易密码即视为实盘
        self.assertEqual(accounts[0].kind, "live")
        self.assertEqual(accounts[0].tq_account, "13800000000")
        self.assertEqual(accounts[0].trade_account, "123456")
        self.assertEqual(accounts[0].symbols, ["SHFE.au2510"])

        # 幂等：再次打开不重复迁移
        self.assertEqual(len(ConfigService(str(self.db_path)).load_accounts()), 1)

    def test_legacy_empty_app_config_does_not_migrate(self):
        # 未配置任何账户的旧库不应生成迁移记录
        self.assertEqual(self.service.load_accounts(), [])

    def test_legacy_sim_columns_merged_then_account_migrated(self):
        # 更老的历史库：账户凭据存在 sim_account/sim_password 列（tq 为空）。
        # sim→tq 搬迁先执行（同密钥密文直接复制），随后 tq 凭据迁移进 accounts 表，
        # sim 列被 DROP。
        sim_pwd_cipher = self.service._cipher.encrypt("sim-pwd")
        # load() 首次运行会写入默认配置行，先确保 app_config 有行可改
        self.service.load()
        conn = sqlite3.connect(self.db_path)
        conn.execute("ALTER TABLE app_config ADD COLUMN sim_account TEXT NOT NULL DEFAULT ''")
        conn.execute("ALTER TABLE app_config ADD COLUMN sim_password TEXT NOT NULL DEFAULT ''")
        conn.execute(
            "UPDATE app_config SET tq_account = '', sim_account = '13800000000',"
            " sim_password = ? WHERE id = 1",
            (sim_pwd_cipher,),
        )
        conn.commit()
        conn.close()

        # 迁移在构造时执行，需重新实例化触发
        service = ConfigService(str(self.db_path))
        conn = sqlite3.connect(self.db_path)
        try:
            columns = [r[1] for r in conn.execute("PRAGMA table_info(app_config)")]
        finally:
            conn.close()
        self.assertNotIn("sim_account", columns)
        self.assertNotIn("sim_password", columns)

        accounts = service.load_accounts()
        self.assertEqual(len(accounts), 1)
        self.assertEqual(accounts[0].kind, "sim")
        self.assertEqual(accounts[0].tq_account, "13800000000")
        self.assertEqual(accounts[0].tq_password, "sim-pwd")

    def test_key_file_created_next_to_db(self):
        self.service.save(_make_config(database_path=str(self.db_path)))
        self.assertTrue((Path(self._tmp.name) / ".secret.key").exists())

    def test_first_run_writes_default_server_url(self):
        # 首次运行没有配置行：写入默认服务器地址，设置页与模拟页读的是同一行
        config = self.service.load()
        self.assertEqual(config.server_url, DEFAULT_SERVER_URL)
        self.assertEqual(DEFAULT_SERVER_URL, "https://itrader.redboil.us.ci")
        self.assertEqual(self.service.load().server_url, config.server_url)

    def test_legacy_default_server_url_replaced_on_upgrade(self):
        # 旧库里的历史默认地址（内网/本机）升级后换成当前默认值，模拟页不再显示旧地址
        for legacy in LEGACY_DEFAULT_SERVER_URLS:
            with self.subTest(legacy=legacy):
                db_path = Path(self._tmp.name) / f"legacy{len(legacy)}.db"
                service = ConfigService(str(db_path))
                service.save(_make_config(server_url=legacy))
                self.assertEqual(service.load().server_url, legacy)

                # 重新打开库触发迁移
                reopened = ConfigService(str(db_path))
                self.assertEqual(reopened.load().server_url, DEFAULT_SERVER_URL)

    def test_custom_server_url_kept(self):
        # 用户自己填的地址不是历史默认值，升级时必须保留
        self.service.save(_make_config(server_url="https://example.com/api"))
        reopened = ConfigService(str(self.db_path))
        self.assertEqual(reopened.load().server_url, "https://example.com/api")


if __name__ == "__main__":
    unittest.main()
