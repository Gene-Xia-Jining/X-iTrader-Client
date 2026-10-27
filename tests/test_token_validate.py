"""Token 校验的三态判定：只有服务器明确否定才算失效。

背景：曾把网关的 502 当成「token 失效」，客户端据此删掉本地唯一一份明文，
而服务器对已签发 token 只存 hash，删掉就只能重新申请。
"""

import asyncio
import unittest
from unittest.mock import patch

import httpx

from src.infrastructure.api.client import TokenApiService

_URL = "http://192.0.2.1:3080/api/tokens/validate"


class _StubClient:
    """替身 AsyncClient：只实现 validate_token 用到的上下文与 get。"""

    def __init__(self, outcome):
        self._outcome = outcome

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc_info):
        return False

    async def get(self, url, headers=None):
        if isinstance(self._outcome, Exception):
            raise self._outcome
        return self._outcome


def _response(status: int) -> httpx.Response:
    return httpx.Response(status, request=httpx.Request("GET", _URL))


def _validate(outcome) -> bool:
    service = TokenApiService("http://192.0.2.1:3080", None, "client-x")
    with patch(
        "src.infrastructure.api.client.httpx.AsyncClient",
        return_value=_StubClient(outcome),
    ):
        return asyncio.run(service.validate_token("tok"))


class ValidateTokenTest(unittest.TestCase):
    def test_ok_status_is_valid(self):
        self.assertTrue(_validate(_response(200)))

    def test_unauthorized_is_invalid(self):
        self.assertFalse(_validate(_response(401)))

    def test_forbidden_is_invalid(self):
        self.assertFalse(_validate(_response(403)))

    def test_gateway_error_raises_instead_of_reporting_invalid(self):
        # 502/503/429 都问不出结论，必须抛出让调用方保留本地凭据
        for status in (429, 500, 502, 503, 504):
            with self.subTest(status=status):
                with self.assertRaises(httpx.HTTPStatusError):
                    _validate(_response(status))

    def test_network_error_raises(self):
        with self.assertRaises(httpx.ConnectError):
            _validate(httpx.ConnectError("connection refused"))


if __name__ == "__main__":
    unittest.main()
