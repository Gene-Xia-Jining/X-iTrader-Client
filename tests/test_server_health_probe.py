import asyncio
import unittest
from unittest.mock import patch

import httpx

from src.infrastructure.api import client as api_client
from src.infrastructure.api.client import TokenApiService


def _ok_response() -> httpx.Response:
    request = httpx.Request("GET", "http://192.0.2.1:3080/api/health")
    return httpx.Response(200, json={"status": "ok"}, request=request)


def _ok_response_body(body: bytes) -> httpx.Response:
    """200 但响应体不是 JSON：解析失败发生在请求层之外，不该触发重试。"""
    request = httpx.Request("GET", "http://192.0.2.1:3080/api/health")
    return httpx.Response(200, content=body, request=request)


def _status_error(status: int) -> httpx.HTTPStatusError:
    request = httpx.Request("GET", "http://192.0.2.1:3080/api/health")
    response = httpx.Response(status, request=request)
    return httpx.HTTPStatusError("boom", request=request, response=response)


class _FakeClient:
    """按脚本依次返回结果或抛异常，记录每次调用的超时预算。"""

    def __init__(self, outcomes):
        self._outcomes = list(outcomes)
        self.timeouts: list[float] = []
        self.requests: list[tuple[str, str]] = []
        self.is_closed = False

    async def request(self, method, url, timeout=None, **kwargs):
        self.timeouts.append(timeout)
        self.requests.append((method, url))
        outcome = self._outcomes.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        return outcome

    async def aclose(self):
        self.is_closed = True


class HealthProbeRetryTest(unittest.TestCase):
    def _run(self, outcomes, timeout=5.0):
        fake = _FakeClient(outcomes)
        with patch.object(api_client, "_shared_client", return_value=fake):
            result = asyncio.run(api_client.check_server_health("http://192.0.2.1:3080", timeout))
        return fake, result

    def test_success_needs_no_retry(self):
        fake, result = self._run([_ok_response()])
        self.assertEqual(result, {"status": "ok"})
        self.assertEqual(fake.timeouts, [5.0])

    def test_connect_timeout_retries_with_shorter_budget(self):
        fake, result = self._run([httpx.ConnectTimeout("timed out"), _ok_response()])
        self.assertEqual(result, {"status": "ok"})
        # 重试只给半个预算，避免一轮探测拖成两个心跳周期
        self.assertEqual(fake.timeouts, [5.0, 2.5])

    def test_second_failure_propagates(self):
        with self.assertRaises(httpx.ConnectTimeout):
            self._run([httpx.ConnectTimeout("timed out"), httpx.ConnectTimeout("timed out")])

    def test_5xx_is_retried(self):
        fake, result = self._run([_status_error(502), _ok_response()])
        self.assertEqual(result, {"status": "ok"})
        self.assertEqual(len(fake.timeouts), 2)

    def test_4xx_is_not_retried(self):
        # 地址/权限问题重试没有意义，立刻把错误交给上层展示
        with self.assertRaises(httpx.HTTPStatusError):
            self._run([_status_error(404), _ok_response()])

    def test_unparseable_body_is_not_retried(self):
        fake = _FakeClient([_ok_response_body(b"not json")])
        with patch.object(api_client, "_shared_client", return_value=fake):
            with self.assertRaises(ValueError):
                asyncio.run(api_client.check_server_health("http://192.0.2.1:3080"))
        self.assertEqual(len(fake.timeouts), 1)


class SharedClientUsageTest(unittest.TestCase):
    """所有接口都必须走同一条常驻连接。

    曾经只有健康探测复用连接，其它接口各自新建：同一条链路上新建 TLS 握手
    要 1-13s，业务请求沿用 5s 超时，于是界面显示「已连接」而品种/策略/Token
    全部 ConnectTimeout。这里锁住「同一个 client 实例」，防止退回老做法。
    """

    def test_symbols_and_strategies_share_one_client(self):
        fake = _FakeClient([
            httpx.Response(
                200,
                json={"symbols": [{"symbol": "SA", "name": "纯碱"}]},
                request=httpx.Request("GET", "http://192.0.2.1:3080/api/symbols"),
            ),
            httpx.Response(
                200,
                json={"strategies": [{"id": "s1", "name": "策略一"}]},
                request=httpx.Request("GET", "http://192.0.2.1:3080/api/strategies"),
            ),
        ])
        with patch.object(api_client, "_shared_client", return_value=fake):
            async def run():
                symbols = await api_client.fetch_server_symbols("http://192.0.2.1:3080")
                strategies = await api_client.fetch_server_strategies("http://192.0.2.1:3080")
                return symbols, strategies

            symbols, strategies = asyncio.run(run())

        self.assertEqual(symbols, [{"symbol": "SA", "name": "纯碱"}])
        self.assertEqual(strategies[0]["id"], "s1")
        self.assertEqual(
            [url for _, url in fake.requests],
            [
                "http://192.0.2.1:3080/api/symbols",
                "http://192.0.2.1:3080/api/strategies",
            ],
        )

    def test_business_timeout_covers_cold_handshake(self):
        # 冷启动握手实测最长 13s；默认超时必须留出余量，否则首跳必失败
        self.assertGreaterEqual(api_client.DEFAULT_API_TIMEOUT, 15.0)

    def test_token_requests_share_one_client(self):
        fake = _FakeClient([
            httpx.Response(
                200,
                json={"tokens": [{"token_id": "t1", "status": "pending"}]},
                request=httpx.Request("GET", "http://192.0.2.1:3080/api/tokens/requests/c1"),
            ),
            httpx.Response(
                200,
                json={"tokens": []},
                request=httpx.Request("GET", "http://192.0.2.1:3080/api/tokens/list/c1"),
            ),
        ])
        service = TokenApiService("http://192.0.2.1:3080", None, "c1")
        with patch.object(api_client, "_shared_client", return_value=fake):
            records = asyncio.run(service.list_token_requests())
            asyncio.run(service.list_tokens())

        self.assertEqual(records[0]["token_id"], "t1")
        self.assertEqual(len(fake.requests), 2)

    def test_claim_is_not_retried(self):
        """领取 Token 不幂等：服务器取走明文后重发只会 404，把 Token 弄丢。"""
        fake = _FakeClient([httpx.ConnectTimeout("timed out"), _ok_response()])
        service = TokenApiService("http://192.0.2.1:3080", None, "c1")
        with patch.object(api_client, "_shared_client", return_value=fake):
            with self.assertRaises(httpx.ConnectTimeout):
                asyncio.run(service.claim_token())
        self.assertEqual(len(fake.requests), 1)

    def test_create_token_is_not_retried(self):
        fake = _FakeClient([httpx.ConnectTimeout("timed out"), _ok_response()])
        service = TokenApiService("http://192.0.2.1:3080", None, "c1")
        with patch.object(api_client, "_shared_client", return_value=fake):
            with self.assertRaises(httpx.ConnectTimeout):
                asyncio.run(service.request_token())
        self.assertEqual(len(fake.requests), 1)


class SharedClientReuseTest(unittest.TestCase):
    def test_same_loop_reuses_one_client(self):
        async def run():
            first = api_client._shared_client()
            second = api_client._shared_client()
            try:
                return first, second
            finally:
                await api_client.aclose_shared_client()

        first, second = asyncio.run(run())
        self.assertIs(first, second)

    def test_reset_drops_cached_client(self):
        async def run():
            first = api_client._shared_client()
            api_client.reset_shared_client()
            second = api_client._shared_client()
            try:
                return first, second
            finally:
                await api_client.aclose_shared_client()

        first, second = asyncio.run(run())
        self.assertIsNot(first, second)

    def test_close_leaves_no_cached_client(self):
        async def run():
            api_client._shared_client()
            await api_client.aclose_shared_client()
            loop = asyncio.get_running_loop()
            return loop in api_client._shared_clients

        self.assertFalse(asyncio.run(run()))

    def test_keepalive_outlives_heartbeat_interval(self):
        # 心跳 10s 一次，池内连接活得比它短就退化成每次冷启动
        async def run():
            c = api_client._shared_client()
            try:
                return c._transport._pool._keepalive_expiry
            finally:
                await api_client.aclose_shared_client()

        self.assertGreater(asyncio.run(run()), 10)


if __name__ == "__main__":
    unittest.main()
