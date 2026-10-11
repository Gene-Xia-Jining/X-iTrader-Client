import json
from typing import Optional, Union

import httpx

from ...domain.entities import StrategySignal
from ...domain.repositories import StrategyStreamClient
from ...domain.repositories import TokenStore

async def check_server_health(server_url: str, timeout: float = 5.0) -> dict:
    """探测服务器连通性（/api/health 无需认证），失败时抛出异常。"""
    url = server_url.strip()
    if "://" not in url:
        url = "http://" + url
    url = url.rstrip("/") + "/api/health"
    async with httpx.AsyncClient(timeout=timeout) as client:
        response = await client.get(url)
        response.raise_for_status()
        return response.json()


async def fetch_server_symbols(
    server_url: str,
    token_service: Optional["TokenApiService"] = None,
    timeout: float = 5.0,
) -> list[dict]:
    """获取服务器支持的交易品种列表（/api/symbols），失败时抛出异常。

    返回 [{"symbol": 代码, "name": 中文名(可能为空)}]；兼容旧版服务器的纯代码字符串列表。
    """
    data = await _get_server_json(server_url, "/api/symbols", token_service, timeout)
    result: list[dict] = []
    for item in data.get("symbols", []):
        if isinstance(item, dict):
            result.append({
                "symbol": str(item.get("symbol", "")),
                "name": str(item.get("name") or ""),
            })
        else:
            result.append({"symbol": str(item), "name": ""})
    return result


async def fetch_server_exchanges(
    server_url: str,
    token_service: Optional["TokenApiService"] = None,
    timeout: float = 5.0,
) -> list[dict]:
    """获取服务器交易所列表（/api/exchanges），失败时抛出异常。"""
    data = await _get_server_json(server_url, "/api/exchanges", token_service, timeout)
    return data.get("exchanges", [])


async def submit_server_symbol(
    server_url: str,
    symbol: str,
    exchange: str,
    token_service: Optional["TokenApiService"] = None,
    timeout: float = 5.0,
) -> None:
    """向服务器品种库提交品种并关联交易所（/api/symbols，需认证），失败时抛出异常。"""
    url = server_url.strip()
    if "://" not in url:
        url = "http://" + url
    url = url.rstrip("/") + "/api/symbols"
    headers = _auth_headers(token_service)
    async with httpx.AsyncClient(timeout=timeout) as client:
        response = await client.post(
            url,
            json={"symbol": symbol, "exchanges": [exchange]},
            headers=headers,
        )
        response.raise_for_status()


async def _get_server_json(
    server_url: str,
    path: str,
    token_service: Optional["TokenApiService"],
    timeout: float,
) -> dict:
    url = server_url.strip()
    if "://" not in url:
        url = "http://" + url
    url = url.rstrip("/") + path
    async with httpx.AsyncClient(timeout=timeout) as client:
        response = await client.get(url, headers=_auth_headers(token_service))
        response.raise_for_status()
        return response.json()


def _auth_headers(token_service: Optional["TokenApiService"]) -> dict:
    if token_service is None:
        return {}
    return token_service.get_auth_headers()


class TokenApiService:
    def __init__(self, server_url: str, token_store: TokenStore, client_id: str):
        self.server_url = server_url.rstrip("/")
        self.token_store = token_store
        self.client_id = client_id

    async def request_token(self, description: str = "") -> dict:
        """Request a token approval; pending tokens are not saved."""
        url = f"{self.server_url}/api/tokens/create"
        async with httpx.AsyncClient() as client:
            response = await client.post(
                url,
                json={
                    "client_id": self.client_id,
                    "description": description,
                    "expires_in_days": 90,
                },
            )
            response.raise_for_status()
            return response.json()

    async def get_token_requests(self) -> dict:
        url = f"{self.server_url}/api/tokens/requests/{self.client_id}"
        async with httpx.AsyncClient() as client:
            response = await client.get(url)
            response.raise_for_status()
            return response.json()

    async def load_approved_token(self, requests: dict) -> Optional[str]:
        for request in requests.get("tokens", requests.get("requests", [])):
            if request.get("status") == "approved" and request.get("token"):
                self.token_store.save_token(request["token"])
                return request["token"]
        return None

    approved_token = load_approved_token

    async def validate_token(self, token: str) -> bool:
        url = f"{self.server_url}/api/tokens/validate"
        async with httpx.AsyncClient() as client:
            response = await client.get(
                url,
                headers={"Authorization": f"Bearer {token}"},
            )
            return response.status_code == 200

    async def list_tokens(self):
        url = f"{self.server_url}/api/tokens/list/{self.client_id}"
        async with httpx.AsyncClient() as client:
            response = await client.get(url)
            response.raise_for_status()
            return response.json()

    def get_auth_headers(self, token: Optional[str] = None) -> dict:
        if token is None:
            token = self.token_store.load_token()
        if not token:
            return {}
        return {"Authorization": f"Bearer {token}"}

class ServerStreamClient(StrategyStreamClient):
    def __init__(
        self,
        server_url: str,
        symbols: list[str],
        token_service: Union[TokenApiService, None] = None,
    ):
        self.server_url = server_url.rstrip("/")
        self.symbols = symbols
        self.token_service = token_service

    async def stream(self):
        symbols = ",".join(self.symbols)
        url = f"{self.server_url}/api/stream?symbols={symbols}"

        headers = {"Accept": "text/event-stream"}
        if self.token_service is not None:
            headers.update(self.token_service.get_auth_headers())

        async with httpx.AsyncClient(timeout=None) as client:
            async with client.stream("GET", url, headers=headers) as response:
                response.raise_for_status()
                event = None
                data = None
                async for line in response.aiter_lines():
                    if line.startswith("event:"):
                        event = line[6:].strip()
                    elif line.startswith("data:"):
                        data = line[5:].strip()
                    elif not line:
                        if event == "trading_result" and data:
                            yield StrategySignal(**json.loads(data))
                        event = None
                        data = None
