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
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from .. import __version__

HANDSHAKE_TIMEOUT = 3.0
_MAX_LINE_BYTES = 1024 * 1024  # 单行上限 1MB，防止异常客户端撑爆内存

# 差量更新后等待旧后端退出的上限（秒）。旧后端在 apply_update_and_restart
# 返回后立即退出，实际只需数百毫秒；留足余量避免把慢退出误判成冲突。
_RESTART_WAIT_SECONDS = 10.0


@dataclass(eq=False)  # eq=True 会置 __hash__=None，无法作为 set 元素
class _Connection:
    writer: asyncio.StreamWriter
    authed: bool = False


def _pid_alive(pid: int) -> bool:
    """判断 pid 是否仍存活。跨平台，无法判定时返回 True（宁可保守拒绝启动）。

    Windows 上不能用 os.kill(pid, 0) 探测：它走 GenerateConsoleCtrlEvent，
    对不存在的 pid 也可能不抛错，会把上次崩溃遗留的 backend.json 误判成
    「后端仍在运行」，导致新后端每次启动都直接退出——界面表现为
    「后端进程已退出，正在重启」无限循环。
    """
    if pid <= 0:
        return False
    if sys.platform == "win32":
        try:
            import ctypes
            from ctypes import wintypes

            # SYNCHRONIZE 足以 WaitForSingleObject；进程不存在时 OpenProcess 直接失败
            SYNCHRONIZE = 0x00100000
            PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
            ERROR_INVALID_PARAMETER = 87
            WAIT_TIMEOUT = 258
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.OpenProcess.restype = wintypes.HANDLE
            kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
            kernel32.WaitForSingleObject.restype = wintypes.DWORD
            kernel32.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
            kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        except (ImportError, AttributeError, OSError):
            return True  # 拿不到 Win32 接口：判定不出就按存活处理，不误清活后端的文件
        handle = None
        try:
            handle = kernel32.OpenProcess(SYNCHRONIZE, False, pid)
            if not handle:
                # 权限受限时 SYNCHRONIZE 可能被拒，退一步用查询权限再试
                handle = kernel32.OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION, False, pid
                )
            if not handle:
                # 只有「pid 不存在」才判死；权限不足说明进程还在，按存活处理，
                # 否则会误清后端仍在使用的 backend.json，放任两个后端同时写库
                return ctypes.get_last_error() != ERROR_INVALID_PARAMETER
            # 0 = WAIT_OBJECT_0（已退出），WAIT_TIMEOUT = 仍在运行
            return kernel32.WaitForSingleObject(handle, 0) == WAIT_TIMEOUT
        except OSError:
            return True  # 判定失败：保守当作存活，不掩盖真冲突
        finally:
            if handle:
                kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True  # 进程存在但属于其它用户
    except OSError:
        return True


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
            f"iTrader 后端已启动: {self._host}:{self._port} (pid={os.getpid()}, {__version__})",
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
        """已有后端在运行则拒绝启动，避免两个后端同时操作 SQLite 与 TqApi 会话。

        差量更新例外：新后端由旧后端自己拉起（见 updater.apply_update_and_restart
        的 patch 分支），此刻旧后端往往还没退完。这里改为等它删掉 backend.json
        （stop() 里的退出清理）再接管；超时仍按原逻辑拒绝，不掩盖真冲突。
        """
        if not self._info_path.exists():
            return
        restarted = os.environ.get("ITRADER_RESTART_FROM_UPDATE") == "1"
        deadline = time.monotonic() + (_RESTART_WAIT_SECONDS if restarted else 0)
        stale_port = None
        while self._info_path.exists():
            try:
                info = json.loads(self._info_path.read_text(encoding="utf-8"))
                pid = int(info.get("pid", 0) or 0)
            except (OSError, ValueError):
                # 半写入/损坏或刚被删掉：当作残留，直接覆盖
                break
            stale_port = info.get("port", stale_port)
            # Windows 上 os.kill(pid, 0) 不是存活探测（会走 GenerateConsoleCtrlEvent，
            # 对已退出的 pid 也会「成功」），改用 OpenProcess + WaitForSingleObject
            # 这类真实存活判定。判定不出结果时按「仍存活」处理：宁可拒绝启动，
            # 也不要让两个后端同时操作 SQLite 与 TqApi。
            if pid > 0 and not _pid_alive(pid):
                break  # 进程已死：残留文件，可覆盖
            if time.monotonic() >= deadline:
                raise RuntimeError(
                    f"后端已在运行 (pid={pid}, port={stale_port})，"
                    f"请先停止或删除 {self._info_path}"
                )
            time.sleep(0.2)
        try:
            self._info_path.unlink()
        except FileNotFoundError:
            pass  # 旧后端刚好删掉了它，正是我们要的结果

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
