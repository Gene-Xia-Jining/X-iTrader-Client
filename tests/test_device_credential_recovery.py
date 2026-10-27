"""设备凭据错误分类与自愈决策测试。

服务端把设备鉴权失败分成三类（见服务端 http_errors.py），客户端据此
决定「重新登记自愈」还是「停下报错」——分错方向的后果分别是无限循环
和用户数据静默丢失，所以这块必须有测试兜住。
"""
import unittest
from unittest import mock

from src.application.bootstrap import Bootstrap
from src.infrastructure.api.client import (
    CODE_DEVICE_KEY_INVALID,
    CODE_DEVICE_KEY_MISSING,
    CODE_DEVICE_REVOKED,
    DeviceCredentialError,
)


class ErrorClassificationTest(unittest.TestCase):
    def test_revoked_is_recoverable(self):
        self.assertTrue(DeviceCredentialError(CODE_DEVICE_REVOKED).recoverable)

    def test_missing_key_is_recoverable(self):
        # 从没登记过：重新登记即可恢复
        self.assertTrue(DeviceCredentialError(CODE_DEVICE_KEY_MISSING).recoverable)

    def test_key_invalid_is_not_recoverable(self):
        """密钥不匹配说明本地数据坏了，清凭据重登记会掩盖真正的原因。"""
        self.assertFalse(DeviceCredentialError(CODE_DEVICE_KEY_INVALID).recoverable)

    def test_unknown_code_is_not_recoverable(self):
        """未知 code 一律不自动重登记：宁可停下，也不要盲目覆盖本地身份。"""
        self.assertFalse(DeviceCredentialError("something_new").recoverable)

    def test_carries_detail(self):
        err = DeviceCredentialError(CODE_DEVICE_REVOKED, "设备凭据已失效")
        self.assertEqual(err.detail, "设备凭据已失效")
        self.assertIn("设备凭据已失效", str(err))


class _FakeResponse:
    def __init__(self, status_code, payload=None, text=""):
        self.status_code = status_code
        self._payload = payload
        self.text = text

    def json(self):
        if self._payload is None:
            raise ValueError("no json")
        return self._payload

    def raise_for_status(self):
        import httpx
        request = httpx.Request("POST", "http://x/api/tokens/create")
        raise httpx.HTTPStatusError("boom", request=request, response=httpx.Response(self.status_code))


class RaiseDeviceErrorTest(unittest.TestCase):
    def test_parses_code_and_detail(self):
        from src.infrastructure.api.client import raise_device_credential_error

        resp = _FakeResponse(401, {"detail": "设备凭据已失效", "code": CODE_DEVICE_REVOKED})
        with self.assertRaises(DeviceCredentialError) as ctx:
            raise_device_credential_error(resp)
        self.assertEqual(ctx.exception.code, CODE_DEVICE_REVOKED)
        self.assertTrue(ctx.exception.recoverable)

    def test_old_server_without_code_raises_http_error(self):
        """旧服务端不返回 code：无法判断原因，按不可恢复处理（不盲目重登记）。"""
        import httpx
        from src.infrastructure.api.client import raise_device_credential_error

        resp = _FakeResponse(401, {"detail": "Invalid or expired token"})
        with self.assertRaises(httpx.HTTPStatusError):
            raise_device_credential_error(resp)

    def test_non_json_body_raises_http_error(self):
        import httpx
        from src.infrastructure.api.client import raise_device_credential_error

        resp = _FakeResponse(401, None, "<html>gateway error</html>")
        with self.assertRaises(httpx.HTTPStatusError):
            raise_device_credential_error(resp)


class RecoverCredentialsTest(unittest.TestCase):
    """recover_device_credentials 必须先清旧密钥，否则登记拿不到新密钥。"""

    def _bootstrap(self):
        bs = Bootstrap()
        store = mock.Mock()
        store.load.return_value = ("dev-1", "old-key")
        bs._device_store = store
        bs._token_service = mock.Mock()
        return bs, store

    def test_clears_key_before_registering(self):
        import asyncio

        bs, store = self._bootstrap()

        async def fake_ensure():
            # 登记前本地密钥必须已经被清空，否则服务端会认为「已登记」而不重发
            self.assertEqual(store.save.call_args_list[0].args, ("dev-1", None))
            bs._token_service.set_device_identity.assert_called_with("dev-1", None)
            return True

        bs.ensure_device_registered = fake_ensure
        ok = asyncio.run(bs.recover_device_credentials())
        self.assertTrue(ok)

    def test_reports_failure_when_register_fails(self):
        import asyncio

        bs, _ = self._bootstrap()

        async def fake_ensure():
            return False

        bs.ensure_device_registered = fake_ensure
        self.assertFalse(asyncio.run(bs.recover_device_credentials()))


if __name__ == "__main__":
    unittest.main()
