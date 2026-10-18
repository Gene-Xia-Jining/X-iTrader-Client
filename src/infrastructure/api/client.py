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


async def fetch_server_strategies(
    server_url: str,
    token_service: Optional["TokenApiService"] = None,
    timeout: float = 5.0,
) -> list[dict]:
    """获取服务器支持的交易策略列表（/api/strategies），失败时抛出异常。

    返回 [{"id": 策略标识, "name": 显示名, "description": 简介}]。
    """
    data = await _get_server_json(server_url, "/api/strategies", token_service, timeout)
    result: list[dict] = []
    for item in data.get("strategies", []):
        if isinstance(item, dict):
            result.append({
                "id": str(item.get("id", "")),
                "name": str(item.get("name") or ""),
                "description": str(item.get("description") or ""),
            })
    return result


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

    async def list_token_requests(self) -> list[dict]:
        """该客户端的 token 申请记录（供 UI 展示），失败时抛出异常。"""
        data = await self.get_token_requests()
        return data.get("tokens", data.get("requests", []))

    async def claim_token(self, token_id: Optional[str] = None) -> Optional[dict]:
        """领取已批准且尚未领取的明文 token（一次性）。

        服务器只在「批准 → 领取」窗口内保留明文，无待领取记录时返回 404，
        此处映射为 None（未批准/已被取走都走同一分支）。传入 token_id
        时只领取该条记录，用于在多个已批准记录间切换。
        """
        url = f"{self.server_url}/api/tokens/claim"
        body: dict = {"client_id": self.client_id}
        if token_id:
            body["token_id"] = token_id
        async with httpx.AsyncClient() as client:
            response = await client.post(url, json=body)
            if response.status_code == 404:
                return None
            response.raise_for_status()
            return response.json()

    async def delete_token_request(self, token_id: str) -> None:
        """删除一条申请记录（含已批准记录）；记录不存在时抛错，避免界面假装成功。"""
        url = f"{self.server_url}/api/tokens/delete"
        async with httpx.AsyncClient() as client:
            response = await client.post(url, json={"token_id": token_id})
            if response.status_code == 404:
                raise ValueError("申请记录不存在（可能已被删除）")
            response.raise_for_status()

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
        strategy: str = "",
    ):
        self.server_url = server_url.rstrip("/")
        self.symbols = symbols
        self.token_service = token_service
        self.strategy = strategy

    async def stream(self):
        symbols = ",".join(self.symbols)
        url = f"{self.server_url}/api/stream?symbols={symbols}"
        if self.strategy:
            url += f"&strategy={self.strategy}"

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
