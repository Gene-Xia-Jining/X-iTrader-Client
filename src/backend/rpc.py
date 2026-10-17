"""本地 RPC 传输层：127.0.0.1 TCP + NDJSON + token 握手。

协议（v1，详见 PROTOCOL.md）：
- 服务端绑定 127.0.0.1 随机端口，把 {"port","token","pid","version"} 写入
  data/backend.json 供客户端发现；退出时删除。
- 客户端连接后 3 秒内发首行 {"v":1,"token":"..."} 完成握手，token 不匹配立即断开。
- 请求：{"id":N,"method":"domain.action","params":{...}}
  响应：{"id":N,"ok":true,"result":{...}} / {"id":N,"ok":false,"error":"...","type":"..."}
- 事件（服务端主动推送，无 id）：{"event":"name","data":{...}}
- 每连接请求串行处理；引擎启停等写操作天然互斥，UI 并发调用按序排队。
"""

import asyncio
import json
import os
import secrets
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from .. import __version__

HANDSHAKE_TIMEOUT = 3.0
_MAX_LINE_BYTES = 1024 * 1024  # 单行上限 1MB，防止异常客户端撑爆内存


@dataclass(eq=False)  # eq=True 会置 __hash__=None，无法作为 set 元素
class _Connection:
    writer: asyncio.StreamWriter
    authed: bool = False


class RpcServer:
    def __init__(self, service, routes: dict, workdir: Path, host: str = "127.0.0.1"):
        self._service = service
        self._routes = routes
        self._workdir = workdir
        self._host = host
        self._server: Optional[asyncio.AbstractServer] = None
        self._port = 0
        self._token = ""
        self._conns: set[_Connection] = set()
        self._info_path = workdir / "data" / "backend.json"

    @property
    def port(self) -> int:
        return self._port

    async def start(self) -> None:
        self._reject_stale_backend()
        self._token = secrets.token_hex(16)
        self._server = await asyncio.start_server(
            self._handle_conn, self._host, 0, limit=_MAX_LINE_BYTES
        )
        self._port = self._server.sockets[0].getsockname()[1]
        # 事件广播函数注入服务层（服务层不感知传输细节）
        self._service.attach_broadcast(self.broadcast)
        self._write_info_file()
        print(
            f"iTrader 后端已启动: {self._host}:{self._port} (pid={os.getpid()}, v{__version__})",
            flush=True,
        )

    async def stop(self) -> None:
        if self._server is not None:
            self._server.close()
            try:
                # wait_closed 在部分 Python 版本会等全部活动连接关闭；
                # 退出路径上资源已释放，socket 收尾不应阻塞进程退出
                await asyncio.wait_for(self._server.wait_closed(), timeout=1)
            except asyncio.TimeoutError:
                pass
            self._server = None
        for conn in list(self._conns):
            self._close_conn(conn)
        if self._info_path.exists():
            self._info_path.unlink()

    def _reject_stale_backend(self) -> None:
        """已有后端在运行则拒绝启动，避免两个后端同时操作 SQLite 与 TqApi 会话。"""
        if not self._info_path.exists():
            return
        try:
            info = json.loads(self._info_path.read_text(encoding="utf-8"))
            pid = int(info.get("pid", 0))
            if pid > 0:
                os.kill(pid, 0)  # 不发信号，仅探测进程存活
                raise RuntimeError(
                    f"后端已在运行 (pid={pid}, port={info.get('port')})，请先停止或删除 {self._info_path}"
                )
        except RuntimeError:
            raise
        except (ValueError, OSError, json.JSONDecodeError):
            pass  # 信息文件损坏或进程已死：当作残留文件覆盖
        self._info_path.unlink()

    def _write_info_file(self) -> None:
        self._info_path.parent.mkdir(parents=True, exist_ok=True)
        self._info_path.write_text(
            json.dumps(
                {
                    "port": self._port,
                    "token": self._token,
                    "pid": os.getpid(),
                    "version": __version__,
                }
            ),
            encoding="utf-8",
        )

    # -------- 连接处理 --------

    async def _handle_conn(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        conn = _Connection(writer=writer)
        peer = writer.get_extra_info("peername")
        try:
            await self._handshake(reader, conn)
            self._conns.add(conn)
            while True:
                line = await reader.readline()
                if not line:
                    break
                await self._dispatch(conn, line)
        except (asyncio.IncompleteReadError, ConnectionError, OSError):
            pass
        except asyncio.TimeoutError:
            pass  # 握手超时
        except json.JSONDecodeError:
            pass  # 首行非法 JSON
        finally:
            self._conns.discard(conn)
            self._close_conn(conn)

    async def _handshake(self, reader: asyncio.StreamReader, conn: _Connection) -> None:
        line = await asyncio.wait_for(reader.readline(), timeout=HANDSHAKE_TIMEOUT)
        hello = json.loads(line)
        if hello.get("token") != self._token:
            self._send(conn, {"ok": False, "error": "认证失败"})
            raise ConnectionError("token 不匹配")
        conn.authed = True
        self._send(conn, {"ok": True, "version": __version__, "pid": os.getpid()})

    async def _dispatch(self, conn: _Connection, line: bytes) -> None:
        if not conn.authed:
            return
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            return  # 坏行忽略，不断开（避免一条脏数据杀掉整个连接）
        req_id = msg.get("id")
        method = msg.get("method")
        if req_id is None or not isinstance(method, str):
            return
        params = msg.get("params")
        if not isinstance(params, dict):
            params = {}
        handler = self._routes.get(method)
        if handler is None:
            self._send(
                conn, {"id": req_id, "ok": False, "error": f"未知方法: {method}", "type": "UnknownMethod"}
            )
            return
        try:
            result = await handler(params)
            self._send(conn, {"id": req_id, "ok": True, "result": result})
        except Exception as e:
            # 异常可经 to_payload() 携带结构化字段（如启动拦截的 issues 列表），
            # 供客户端区分展示；不带该方法的异常按原样只回 error/type
            payload = {"id": req_id, "ok": False, "error": str(e), "type": type(e).__name__}
            if hasattr(e, "to_payload") and callable(e.to_payload):
                extra = e.to_payload()
                if isinstance(extra, dict):
                    payload.update(extra)
            self._send(conn, payload)
        finally:
            try:
                await conn.writer.drain()
            except ConnectionError:
                pass

    def _send(self, conn: _Connection, payload: dict) -> None:
        conn.writer.write((json.dumps(payload, ensure_ascii=False, default=str) + "\n").encode("utf-8"))

    # -------- 事件广播 --------

    def broadcast(self, event_name: str, data: dict) -> None:
        """把事件推给所有已握手连接。由服务层在 asyncio 线程内同步调用。"""
        for conn in list(self._conns):
            try:
                self._send(conn, {"event": event_name, "data": data})
            except (ConnectionError, OSError):
                self._close_conn(conn)
                self._conns.discard(conn)

    @staticmethod
    def _close_conn(conn: _Connection) -> None:
        try:
            conn.writer.close()
        except Exception:
            pass
