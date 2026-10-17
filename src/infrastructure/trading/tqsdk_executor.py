import asyncio
import threading
from dataclasses import dataclass, field
from typing import Optional

from ...domain.entities import Account
from ...domain.services import TradingExecutor


def test_tq_auth_connection(account: str, password: str) -> None:
    """验证快期权限账户能否登录：构造 TqApi 完成 auth 握手后立即关闭。

    阻塞调用（TqApi 构造会等待连接建立），需在工作线程中执行。
    失败时抛出 tqsdk 异常（如 TqAuthError 密码错误、TqConnectionError 网络不通）。
    """
    from tqsdk import TqApi, TqAuth

    api = TqApi(auth=TqAuth(account, password))
    api.close()


def test_account_session(account: Account, auth: tuple[Optional[str], Optional[str]]) -> None:
    """验证某账户的完整 TqApi 会话能否建立（auth + 账户对象）。

    与 _start_session 同样的构造路径：实盘走 TqAccount、模拟走 TqKq，
    构造中 tqsdk 会同步完成 auth.login 与账户权限校验，任一步失败都抛
    异常。握手成功后立即 close，不留长连接。供 engine_start 的启动前预检
    使用——区别于 test_tq_auth_connection 只验快期权限账户，本函数能发现
    实盘期货公司/资金账号配置错误（tqsdk 在构造时即校验账户授权）。

    阻塞调用，需在工作线程中执行（与 test_tq_auth_connection 一致）。
    """
    from tqsdk import TqAccount, TqApi, TqAuth, TqKq

    if account.kind == "live":
        account_obj = TqAccount(account.broker, account.trade_account, account.trade_password)
    else:
        # 模拟账户全局仅一个（快期多模拟账户需专业版，暂不支持），用主模拟账号
        account_obj = TqKq()

    api = TqApi(
        account_obj,
        auth=TqAuth(auth[0], auth[1]) if auth[0] and auth[1] else None,
    )
    api.close()


@dataclass
class _AccountSession:
    """单个账户的 TqApi 会话，独占一个守护线程与事件循环。"""

    account_id: str
    account_obj: object
    api: object
    loop: asyncio.AbstractEventLoop
    thread: threading.Thread
    _tasks: dict[str, object] = field(default_factory=dict)


class TqSdkTradingExecutor(TradingExecutor):
    """多账户交易执行器。

    每个账户持有独立的 TqApi 与守护线程：模拟账户用 TqKq（快期模拟，与快期
    客户端/APP/专业版互通），实盘账户用 TqAccount。

    隔离理由：TqApi 每个实例自建 asyncio.SelectorEventLoop（baseApi.py:25），
    且 wait_update 会 _set_running_loop(None)（Python 3.14 下额外 swap current
    task），并发交错行为未验证。TqApi docstring 亦明确「一个线程中应该只有一
    个 TqApi 实例」。官方推荐的共享单 TqApi + TqMultiAccount 要求所有账户共享
    同一快期权限账户；这里各账户可独立权限账户，故按线程隔离。
    """

    def __init__(
        self,
        accounts: list[Account],
        auths: dict[str, tuple[str, str]],
        close_timeout: float = 10.0,
    ):
        self._close_timeout = close_timeout
        # 先建会话，避免调用方在会话未就绪时投递到已关闭的 loop
        self._sessions: dict[str, _AccountSession] = {
            account.id: self._start_session(account, auths.get(account.id, (None, None)))
            for account in accounts
        }
        self._closed = False
        self._closing = False

    def _start_session(
        self, account: Account, auth: tuple[Optional[str], Optional[str]]
    ) -> _AccountSession:
        """在独立守护线程中初始化该账户的 TqApi。

        会话未就绪会抛异常（不静默降级），异常发生在调用方线程，调用方可据此
        终止本次启动并给出明确提示。
        """
        from concurrent.futures import Future
        from tqsdk import TqAccount, TqApi, TqAuth, TqKq

        if account.kind == "live":
            account_obj = TqAccount(account.broker, account.trade_account, account.trade_password)
        else:
            # 模拟账户全局仅一个（快期多模拟账户需专业版，暂不支持），用主模拟账号
            account_obj = TqKq()

        future: Future = Future()
        loop = asyncio.new_event_loop()

        def run() -> None:
            asyncio.set_event_loop(loop)
            try:
                api = TqApi(
                    account_obj,
                    auth=TqAuth(auth[0], auth[1]) if auth[0] and auth[1] else None,
                )
                future.set_result(
                    _AccountSession(account.id, account_obj, api, loop, threading.current_thread())
                )
            except Exception as e:
                future.set_exception(e)
            finally:
                loop.run_forever()

        thread = threading.Thread(target=run, name=f"tq-{account.id}", daemon=True)
        thread.start()
        return future.result()

    def _submit(self, account_id: str, fn, timeout: Optional[float] = None):
        """把调用投递到该账户 TqApi 的守护线程执行并等待返回。

        必须走该账户专属线程：TqApi.wait_update 会改动当前线程的 running loop，
        跨线程共享会互相破坏。
        """
        if self._closed:
            raise RuntimeError("执行器已关闭")
        session = self._sessions.get(account_id)
        if session is None:
            raise ValueError(f"未知账户: {account_id}")
        future = asyncio.run_coroutine_threadsafe(asyncio.to_thread(fn), session.loop)
        return future.result(timeout)

    def _get_task(self, session: _AccountSession, symbol: str) -> object:
        """按品种取调仓任务；同一 (账户, 品种) 复用同一实例。"""
        from tqsdk import TargetPosTask

        task = session._tasks.get(symbol)
        if task is None:
            # 多账户模式下 TargetPosTask 必须显式指定账户实例；该实例须与构造
            # TqApi 时传入的同一对象，否则 TqMultiAccount._check_valid 校验失败
            task = TargetPosTask(session.api, symbol, account=session.account_obj, price="ACTIVE")
            session._tasks[symbol] = task
        return task

    async def set_target_position(self, symbol: str, target_position: int, account_id: str = "") -> None:
        if not account_id:
            raise ValueError("account_id 不能为空")

        def _set():
            session = self._sessions[account_id]
            self._get_task(session, symbol).set_target_volume(target_position)
            session.api.wait_update()

        self._submit(account_id, _set)

    def close(self) -> None:
        """关闭全部账户的 TqApi 会话。幂等；单个账户失败不影响其余账户。

        _closed 必须在投递完成后才置位：_submit 会拒绝已关闭执行器的调用，
        先置位会让 close 自身抛「执行器已关闭」，会话实际从未被释放。
        这里用独立的 _closing 标记防止重入。
        """
        if self._closed or self._closing:
            return
        self._closing = True
        try:
            for session in list(self._sessions.values()):
                try:
                    self._submit(
                        session.account_id,
                        lambda api=session.api: api.close(),
                        timeout=self._close_timeout,
                    )
                except Exception:
                    # 单账户关闭失败不阻断其余账户；线程为 daemon，进程退出时随
                    # 进程回收，这里无需重试
                    pass
        finally:
            self._closed = True
            self._closing = False
