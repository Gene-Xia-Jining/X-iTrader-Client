import asyncio
import json
import logging
import weakref
from dataclasses import fields
from typing import Optional, Union

import httpx

from ...domain.entities import StrategySignal
from ...domain.repositories import StrategyStreamClient
from ...domain.repositories import TokenStore

logger = logging.getLogger(__name__)


# 服务端 /api/tokens/create 返回的机器可读错误码（见服务端 http_errors.py）
CODE_DEVICE_KEY_MISSING = "device_key_missing"
CODE_DEVICE_REVOKED = "device_revoked"
CODE_DEVICE_KEY_INVALID = "device_key_invalid"


class DeviceCredentialError(Exception):
    """设备凭据相关的服务端拒绝，带机器可读 code。

    为什么要区分：``device_revoked`` 是管理员意图（应重新登记并自愈），
    ``device_key_invalid`` 说明本地数据坏了（应停下并报错）。两者都表现为
    401，若合成一个通用 HTTP 错误，客户端只能一律失败，无法自愈。
    """

    def __init__(self, code: str, detail: str = ""):
        self.code = code
        self.detail = detail
        super().__init__(detail or code)

    @property
    def recoverable(self) -> bool:
        """重新登记设备就能恢复（设备被撤销 / 从未登记）。"""
        return self.code in (CODE_DEVICE_REVOKED, CODE_DEVICE_KEY_MISSING)


def raise_device_credential_error(response) -> None:
    """把服务端 401 转成 DeviceCredentialError（无 code 时按不可恢复处理）。

    旧版服务端不返回 code：此时无法判断原因，一律当作**不可恢复**，
    宁可停下报错也不盲目重新登记——盲登记会在真正的密钥损坏场景下
    反复覆盖本地凭据，把可诊断的问题变成静默循环。
    """
    code = ""
    detail = ""
    try:
        payload = response.json()
        code = str(payload.get("code") or "")
        detail = str(payload.get("detail") or "")
    except Exception:
        detail = response.text[:200]
    if not code:
        raise response_http_error(response)
    raise DeviceCredentialError(code, detail)


def response_http_error(response):
    """构造一个与 httpx raise_for_status 等价的异常，供上面兜底使用。"""
    try:
        response.raise_for_status()
    except httpx.HTTPStatusError as exc:
        return exc
    # 理论上走不到（调用方只在非 2xx 时进来），保底给个可读错误
    return RuntimeError(f"HTTP {response.status_code}: {response.text[:200]}")


def parse_signal(payload: dict) -> StrategySignal:
    """按 StrategySignal 已声明的字段过滤后构造。

    服务器是 payload 的定义方，可能先于客户端新增字段（如 strategy_id）。
    不过滤会因多余关键字直接抛 TypeError，整条信号流断掉；这里只取认识的
    字段，新增字段对旧客户端降级为「忽略」而不是「报错」。
    """
    known = {f.name for f in fields(StrategySignal)}
    unknown = set(payload) - known
    if unknown:
        logger.debug("signal_ignored_fields fields=%s", sorted(unknown))
    return StrategySignal(**{k: v for k, v in payload.items() if k in known})

# 所有 API 请求共用的常驻 client：按 event loop 缓存，进程内复用 TCP/TLS 连接。
# 实测这台机器到服务器的新建握手要 1-13s（TLS 单独就占 1.4-4.6s，抖动时超过
# 20s），复用连接只要 0.5-2.4s。若每个请求各自新建连接，5s 默认超时会把绝大多数
# 请求判成 ConnectTimeout——界面显示「已连接」而 Token/品种/策略全部「获取失败」
# 就是这么来的：健康探测复用了连接，其它请求没有。
_shared_clients: weakref.WeakKeyDictionary = weakref.WeakKeyDictionary()

# 失败后重试一次：复用连接被对端或中间设备回收时首跳必失败，换新连接即恢复；
# 链路抖动时第二次也常能挤进预算。重试只给半个预算，避免一次请求拖成两倍等待。
_RETRY_TIMEOUT_RATIO = 0.5

# 业务请求的默认超时：要覆盖冷启动握手（实测最长 13s），而不是 httpx 的 5s。
# 热连接通常 1-2s，所以只有首跳真的付这个成本。
DEFAULT_API_TIMEOUT = 15.0


def _shared_client() -> httpx.AsyncClient:
    """取当前 event loop 专属的 API client，首次调用时创建。

    按 loop 缓存而非全局单例：client 绑定 loop，跨 loop 复用会在反复新建
    loop 的场景（测试里的 asyncio.run）绑定到已关闭的 loop。
    """
    loop = asyncio.get_running_loop()
    client = _shared_clients.get(loop)
    if client is None or client.is_closed:
        client = httpx.AsyncClient(
            # 心跳间隔 10s，池内连接必须活得比它久，否则每次仍是冷启动；
            # 连接数要同时容纳心跳 + 品种/策略/Token 的并发查询
            limits=httpx.Limits(max_connections=8, keepalive_expiry=60.0),
        )
        _shared_clients[loop] = client
    return client


def reset_shared_client() -> None:
    """丢弃缓存的连接，供代理设置变化后调用。

    httpx 在构造 client 时读取代理环境变量，改了 env 只有重建才生效。
    """
    client = _take_shared_client()
    if client is not None:
        # 同步上下文无法 await，交给 loop 关闭；loop 已停也只是连接随 GC 回收
        asyncio.get_running_loop().create_task(client.aclose())


async def aclose_shared_client() -> None:
    """关闭当前 loop 的连接，供后端退出时调用。"""
    client = _take_shared_client()
    if client is not None:
        await client.aclose()


def _take_shared_client() -> Optional[httpx.AsyncClient]:
    """摘掉当前 loop 缓存的 client 并返回（未建过或已关闭返回 None）。"""
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:
        _shared_clients.clear()
        return None
    client = _shared_clients.pop(loop, None)
    return None if client is None or client.is_closed else client


def _join_url(server_url: str, path: str) -> str:
    url = server_url.strip()
    if "://" not in url:
        url = "http://" + url
    return url.rstrip("/") + path


def _health_url(server_url: str) -> str:
    return _join_url(server_url, "/api/health")


async def _api_request(
    method: str,
    url: str,
    timeout: float = DEFAULT_API_TIMEOUT,
    retry: bool = False,
    raise_for_status: bool = True,
    **kwargs,
) -> httpx.Response:
    """用常驻 client 发请求；retry 时对连接层异常与 5xx 重试一次。

    只给读类请求和幂等写入打开 retry：``tokens/create`` 重发会多出一条申请
    记录，``tokens/claim`` 重发时服务器已把明文取走（第二次 404），会把刚领到
    的 Token 弄丢——两者都不幂等，宁可失败让用户重试。

    raise_for_status=False 供把 404 当正常业务返回的接口使用（设备登记、
    领取、删除记录），由调用方自己解释状态码；此时仍然只重试 5xx。
    """
    client = _shared_client()
    budget = timeout
    attempt = 0
    while True:
        error: Optional[Exception] = None
        try:
            response = await client.request(method, url, timeout=budget, **kwargs)
        except (httpx.TransportError, httpx.HTTPStatusError) as exc:
            error = exc
        else:
            if raise_for_status:
                try:
                    response.raise_for_status()
                    return response
                except httpx.HTTPStatusError as exc:
                    error = exc
            elif response.status_code >= 500:
                error = httpx.HTTPStatusError(
                    f"HTTP {response.status_code}",
                    request=response.request,
                    response=response,
                )
            else:
                return response

        if not retry or attempt or not _retryable(error):
            raise error
        logger.debug(
            "api_request_retry method=%s url=%s first=%s",
            method,
            url,
            error.__class__.__name__,
        )
        attempt += 1
        budget *= _RETRY_TIMEOUT_RATIO


async def _get_json(url: str, timeout: float, headers: Optional[dict] = None) -> dict:
    response = await _api_request("GET", url, timeout=timeout, retry=True, headers=headers or {})
    return response.json()


async def check_server_health(server_url: str, timeout: float = 5.0) -> dict:
    """探测服务器连通性（/api/health 无需认证），失败时抛出异常。"""
    url = _health_url(server_url)
    return await _get_json(url, timeout)


def _retryable(error: Exception) -> bool:
    """连接层异常一律可重试；5xx 是服务端瞬时不可用，重试一次也合理。

    4xx 是地址或权限问题，重试无意义；JSON 解析失败（ValueError）重试也无益。
    """
    if isinstance(error, httpx.TransportError):
        return True
    return isinstance(error, httpx.HTTPStatusError) and error.response.status_code >= 500


def describe_server_error(error: Exception) -> str:
    """把健康探测的异常转成一句可读原因，供界面直接展示。

    网络异常的原始 str() 常常只是 "[Errno 61] Connection refused"，而
    HTTPStatusError 的原文会带上完整 URL 反而难读；这里按类型给出中文主因，
    有额外信息时附在括号里，便于用户判断是地址、网络还是服务端的问题。
    """
    raw = str(error).strip()
    if isinstance(error, (httpx.InvalidURL, httpx.UnsupportedProtocol)):
        return f"服务器地址无效（{raw}）" if raw else "服务器地址无效"
    if isinstance(error, httpx.TimeoutException):
        main = "请求超时"
    elif isinstance(error, httpx.ConnectError):
        main = "无法建立连接"
    elif isinstance(error, httpx.HTTPStatusError):
        # 原文包含请求 URL 与状态行，对定位没有额外帮助，只留状态码
        return f"服务器返回 HTTP {error.response.status_code}"
    elif isinstance(error, ValueError):
        main = "服务器返回内容无法解析"
    else:
        main = error.__class__.__name__
    return f"{main}（{raw}）" if raw else main


async def fetch_server_symbols(
    server_url: str,
    token_service: Optional["TokenApiService"] = None,
    timeout: float = DEFAULT_API_TIMEOUT,
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
    timeout: float = DEFAULT_API_TIMEOUT,
) -> list[dict]:
    """获取服务器交易所列表（/api/exchanges），失败时抛出异常。"""
    data = await _get_server_json(server_url, "/api/exchanges", token_service, timeout)
    return data.get("exchanges", [])


async def fetch_server_strategies(
    server_url: str,
    token_service: Optional["TokenApiService"] = None,
    timeout: float = DEFAULT_API_TIMEOUT,
) -> list[dict]:
    """获取服务器支持的交易策略列表（/api/strategies），失败时抛出异常。

    返回 [{"id": 策略标识, "name": 显示名, "description": 简介}]。
    """
    data = await _get_server_json(server_url, "/api/strategies", token_service, timeout)
    result: list[dict] = []
    for item in data.get("strategies", []):
        if isinstance(item, dict):
            alias = str(item.get("alias") or item.get("name") or "")
            result.append({
                "id": str(item.get("id", "")),
                "name": str(item.get("name") or alias or item.get("id", "")),
                "alias": alias,
                "description": str(item.get("description") or ""),
            })
    return result


async def submit_server_symbol(
    server_url: str,
    symbol: str,
    exchange: str,
    token_service: Optional["TokenApiService"] = None,
    timeout: float = DEFAULT_API_TIMEOUT,
) -> None:
    """向服务器品种库提交品种并关联交易所（/api/symbols，需认证），失败时抛出异常。"""
    url = _join_url(server_url, "/api/symbols")
    headers = _auth_headers(token_service)
    await _api_request(
        "POST",
        url,
        timeout=timeout,
        json={"symbol": symbol, "exchanges": [exchange]},
        headers=headers,
    )


async def _get_server_json(
    server_url: str,
    path: str,
    token_service: Optional["TokenApiService"],
    timeout: float,
) -> dict:
    url = _join_url(server_url, path)
    return await _get_json(url, timeout, _auth_headers(token_service))


def _auth_headers(token_service: Optional["TokenApiService"]) -> dict:
    if token_service is None:
        return {}
    return token_service.get_auth_headers()


class TokenApiService:
    def __init__(
        self,
        server_url: str,
        token_store: TokenStore,
        client_id: str,
        device_identity_store=None,
    ):
        self.server_url = server_url.rstrip("/")
        self.token_store = token_store
        self.client_id = client_id
        # 设备身份（可选）：提供时 token 申请会带上 device_id + device_key，
        # 服务器据此把 token 绑定到可验证的设备上（见服务端 doc/IDENTITY.md）
        self.device_identity_store = device_identity_store
        self.device_id: Optional[str] = None
        self.device_key: Optional[str] = None

    def set_device_identity(self, device_id: Optional[str], device_key: Optional[str]) -> None:
        """登记成功后回填，之后所有 token 申请都带这对凭据。"""
        self.device_id = device_id
        self.device_key = device_key

    async def register_device(self, device_id: str, legacy_client_id: Optional[str] = None) -> Optional[dict]:
        """向服务器登记设备，成功后自动保存 device_key。

        返回服务器响应；失败抛异常由调用方决定是否降级（老服务器没有这个
        接口，会 404——此时保持旧的纯 client_id 行为即可，不应阻断启动）。
        """
        from ..security.device_identity import device_label
        import platform as _platform

        url = f"{self.server_url}/api/auth/device/register"
        body = {
            "device_id": device_id,
            "device_label": device_label(),
            "platform": _platform.system().lower(),
        }
        if legacy_client_id:
            body["legacy_client_id"] = legacy_client_id
        response = await _api_request(
            "POST", url, timeout=10.0, raise_for_status=False, json=body
        )
        if response.status_code == 404:
            # 旧版服务器：没有身份体系，退回纯 client_id 路径
            logger.info("server_has_no_device_registry, fallback to client_id only")
            return None
        response.raise_for_status()
        payload = response.json()

        # 服务器对已登记设备不重发密钥（防止知道 device_id 就冒充），
        # 此时必须沿用本地已保存的密钥，否则会把内存里的身份抹成 None，
        # 后续 token 申请就退化成不经校验的纯 client_id 路径。
        new_key = payload.get("device_key") or self.device_key
        self.set_device_identity(payload.get("device_id") or self.device_id, new_key)
        if self.device_identity_store is not None:
            self.device_identity_store.save(payload.get("device_id"), new_key)
        return payload

    async def request_token(self, description: str = "") -> dict:
        """Request a token approval; pending tokens are not saved.

        设备凭据被服务端拒绝时抛 ``DeviceCredentialError``（带 code），
        由调用方决定是重新登记自愈还是报错；其余非 2xx 仍是 HTTPStatusError。
        """
        url = f"{self.server_url}/api/tokens/create"
        body = {
            "client_id": self.client_id,
            "description": description,
            "expires_in_days": 90,
        }
        if self.device_id and self.device_key:
            body["device_id"] = self.device_id
            body["device_key"] = self.device_key
        # 不重试：重发会在服务器上多出一条申请记录。
        # 401 必须由这里自己解释（设备凭据被拒），不能让 raise_for_status 先抛
        response = await _api_request("POST", url, raise_for_status=False, json=body)
        if response.status_code == 401:
            raise_device_credential_error(response)
        response.raise_for_status()
        return response.json()

    async def get_token_requests(self) -> dict:
        url = f"{self.server_url}/api/tokens/requests/{self.client_id}"
        return await _get_json(url, DEFAULT_API_TIMEOUT)

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
        # 不重试：服务器取走明文后第二次会 404，重试会把刚领到的 Token 弄丢
        response = await _api_request("POST", url, raise_for_status=False, json=body)
        if response.status_code == 404:
            return None
        response.raise_for_status()
        return response.json()

    async def delete_token_request(self, token_id: str) -> None:
        """删除一条申请记录（含已批准记录）；记录不存在时抛错，避免界面假装成功。"""
        url = f"{self.server_url}/api/tokens/delete"
        response = await _api_request(
            "POST", url, raise_for_status=False, json={"token_id": token_id}
        )
        if response.status_code == 404:
            raise ValueError("申请记录不存在（可能已被删除）")
        response.raise_for_status()

    async def validate_token(self, token: str) -> bool:
        """校验 token；只有服务器明确否定时才返回 False。

        401/403 是「token 无效/被吊销」的确定答案，其余非 200（5xx、429 等）
        说明这次问不出结果，必须抛异常而不是返回 False——调用方据此区分
        「该删本地凭据」和「网络/网关故障，留着稍后再试」。曾因把 502 当成
        失效而删掉本地唯一一份明文 token，服务器只存 hash，只能重新申请。
        """
        url = f"{self.server_url}/api/tokens/validate"
        response = await _api_request(
            "GET",
            url,
            retry=True,
            raise_for_status=False,
            headers={"Authorization": f"Bearer {token}"},
        )
        if response.status_code in (401, 403):
            return False
        response.raise_for_status()
        return True

    async def list_tokens(self):
        url = f"{self.server_url}/api/tokens/list/{self.client_id}"
        return await _get_json(url, DEFAULT_API_TIMEOUT)

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
                            yield parse_signal(json.loads(data))
                        event = None
                        data = None
