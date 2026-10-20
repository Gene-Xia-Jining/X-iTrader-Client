import unittest

import httpx

from src.infrastructure.api.client import describe_server_error


def _status_error(status: int) -> httpx.HTTPStatusError:
    request = httpx.Request("GET", "http://192.0.2.1:3080/api/health")
    response = httpx.Response(status, request=request)
    return httpx.HTTPStatusError("boom", request=request, response=response)


class DescribeServerErrorTest(unittest.TestCase):
    def test_connect_error_keeps_reason(self):
        error = httpx.ConnectError("[Errno 61] Connection refused")
        self.assertEqual(
            describe_server_error(error),
            "无法建立连接（[Errno 61] Connection refused）",
        )

    def test_timeout(self):
        error = httpx.ConnectTimeout("timed out")
        self.assertEqual(describe_server_error(error), "请求超时（timed out）")

    def test_http_status_shows_code_only(self):
        # 原文含完整 URL 与状态行，对定位没有额外帮助，只保留状态码
        self.assertEqual(describe_server_error(_status_error(500)), "服务器返回 HTTP 500")

    def test_invalid_url(self):
        error = httpx.InvalidURL("Invalid port: 'abc'")
        self.assertEqual(describe_server_error(error), "服务器地址无效（Invalid port: 'abc'）")

    def test_unparseable_body(self):
        # response.json() 解析失败抛 ValueError（JSONDecodeError 是其子类）
        error = ValueError("Expecting value: line 1 column 1 (char 0)")
        self.assertEqual(
            describe_server_error(error),
            "服务器返回内容无法解析（Expecting value: line 1 column 1 (char 0)）",
        )

    def test_unknown_error_falls_back_to_class_name(self):
        self.assertEqual(describe_server_error(RuntimeError("refused")), "RuntimeError（refused）")

    def test_empty_message_has_no_empty_parentheses(self):
        self.assertEqual(describe_server_error(httpx.ConnectError("")), "无法建立连接")


if __name__ == "__main__":
    unittest.main()
