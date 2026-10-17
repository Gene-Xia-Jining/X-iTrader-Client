"""RPC 错误响应携带结构化 data 字段（EngineStartBlockedError.issues 通道）。

不起真实后端：直接构造 RpcServer，喂一行请求，读回写出的 JSON。
"""

import asyncio
import json
import unittest
from pathlib import Path


class _FakeWriter:
    def __init__(self):
        self.chunks: list[bytes] = []

    def write(self, data: bytes) -> None:
        self.chunks.append(data)

    async def drain(self) -> None:
        return None

    def close(self) -> None:
        return None


class _FakeConn:
    authed = True

    def __init__(self):
        self.writer = _FakeWriter()


async def _dispatch_with(handler) -> dict:
    from src.backend.rpc import RpcServer

    server = RpcServer(service=None, routes={"engine.start": handler}, workdir=Path("."))
    conn = _FakeConn()
    await server._dispatch(conn, b'{"id": 7, "method": "engine.start", "params": {}}\n')
    return json.loads(b"".join(conn.writer.chunks).decode("utf-8"))


class RpcErrorPayloadTest(unittest.TestCase):
    def test_plain_exception_has_no_data(self):
        async def handler(params):
            raise RuntimeError("服务器未连接")

        payload = asyncio.run(_dispatch_with(handler))
        self.assertFalse(payload["ok"])
        self.assertEqual(payload["type"], "RuntimeError")
        self.assertNotIn("data", payload)

    def test_exception_with_to_payload_passes_issues(self):
        from src.backend.service import EngineStartBlockedError

        issues = ["账户「A」未配置快期账号", "本地 API token 未领取"]

        async def handler(params):
            raise EngineStartBlockedError(issues)

        payload = asyncio.run(_dispatch_with(handler))
        self.assertFalse(payload["ok"])
        self.assertEqual(payload["type"], "EngineStartBlockedError")
        self.assertEqual(payload["data"]["issues"], issues)
        self.assertIn("未配置快期账号", payload["error"])


if __name__ == "__main__":
    unittest.main()
