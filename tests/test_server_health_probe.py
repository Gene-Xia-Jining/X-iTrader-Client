import asyncio
import unittest
from unittest.mock import patch

import httpx

from src.infrastructure.api import client as api_client


def _ok_response() -> httpx.Response:
    request = httpx.Request("GET", "http://192.0.2.1:3080/api/health")
    return httpx.Response(200, json={"status": "ok"}, request=request)


def _status_error(status: int) -> httpx.HTTPStatusError:
    request = httpx.Request("GET", "http://192.0.2.1:3080/api/health")
    response = httpx.Response(status, request=request)
    return httpx.HTTPStatusError("boom", request=request, response=response)


class _FakeClient:
    """按脚本依次返回结果或抛异常，记录每次调用的超时预算。"""

    def __init__(self, outcomes):
        self._outcomes = list(outcomes)
        self.timeouts: list[float] = []
        self.is_closed = False

    async def get(self, url, timeout=None):
        self.timeouts.append(timeout)
        outcome = self._outcomes.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        return outcome

    async def aclose(self):
        self.is_closed = True


class HealthProbeRetryTest(unittest.TestCase):
    def _run(self, outcomes, timeout=5.0):
        fake = _FakeClient(outcomes)
        with patch.object(api_client, "_health_client", return_value=fake):
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
        fake = _FakeClient([ValueError("Expecting value")])
        with patch.object(api_client, "_health_client", return_value=fake):
            with self.assertRaises(ValueError):
                asyncio.run(api_client.check_server_health("http://192.0.2.1:3080"))
        self.assertEqual(len(fake.timeouts), 1)


class HealthClientReuseTest(unittest.TestCase):
    def test_same_loop_reuses_one_client(self):
        async def run():
            first = api_client._health_client()
            second = api_client._health_client()
            try:
                return first, second
            finally:
                await api_client.aclose_health_client()

        first, second = asyncio.run(run())
        self.assertIs(first, second)

    def test_reset_drops_cached_client(self):
        async def run():
            first = api_client._health_client()
            api_client.reset_health_client()
            second = api_client._health_client()
            try:
                return first, second
            finally:
                await api_client.aclose_health_client()

        first, second = asyncio.run(run())
        self.assertIsNot(first, second)

    def test_close_leaves_no_cached_client(self):
        async def run():
            api_client._health_client()
            await api_client.aclose_health_client()
            loop = asyncio.get_running_loop()
            return loop in api_client._health_clients

        self.assertFalse(asyncio.run(run()))

    def test_keepalive_outlives_heartbeat_interval(self):
        # 心跳 10s 一次，池内连接活得比它短就退化成每次冷启动
        async def run():
            c = api_client._health_client()
            try:
                return c._transport._pool._keepalive_expiry
            finally:
                await api_client.aclose_health_client()

        self.assertGreater(asyncio.run(run()), 10)


if __name__ == "__main__":
    unittest.main()
