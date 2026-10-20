"""headless 后端入口：python -m src.backend

启动本地 RPC 服务供 UI 壳（Swift）或 CLI 客户端连接。
frozen 打包时工作目录为数据根（见 infrastructure.paths.data_root()，与 .app 位置
无关），开发态用当前目录。
"""

import asyncio
import os
import signal
import sys
import threading
import time
from pathlib import Path

from ..infrastructure.paths import data_root, migrate_legacy_data_root


def _watch_parent(parent_pid: int, on_parent_gone) -> None:
    """壳进程死亡后自杀，避免孤儿后端常驻（覆盖 SIGKILL 等任何壳死亡方式）。

    后端由壳 spawn，壳退出后 PPID 变为 1（launchd 接管）。由 launchd 直接
    启动的进程 PPID 本就是 1，不启用监控（调用方负责判断）。
    """
    while True:
        time.sleep(2)
        if os.getppid() != parent_pid:
            on_parent_gone()
            return


async def run() -> int:
    if getattr(sys, "frozen", False):
        migrate_legacy_data_root()
        workdir = data_root()
        workdir.mkdir(parents=True, exist_ok=True)
        os.chdir(workdir)
    workdir = Path.cwd()
    Path("data").mkdir(parents=True, exist_ok=True)

    # 延迟导入：保证 chdir 先于任何 data/ 相对路径的使用
    from .rpc import RpcServer
    from .service import BackendService, build_routes

    service = BackendService(workdir)
    server = RpcServer(service, build_routes(service), workdir)

    stop_signal = asyncio.Event()

    def _request_stop():
        stop_signal.set()

    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, _request_stop)
        except NotImplementedError:
            pass  # Windows ProactorLoop 不支持；靠 KeyboardInterrupt 兜底

    # 父进程（UI 壳）死亡监控：壳由 shell/ 壳进程 spawn 时 PPID != 1。
    # watcher 运行在非 loop 线程，必须 call_soon_threadsafe 唤醒主循环。
    # 差量更新例外：新后端由旧后端拉起（updater 的 patch 分支），父进程马上就会
    # 退出；此时监控会把刚接管的进程当成孤儿杀掉，故跳过。
    parent_pid = os.getppid()
    if parent_pid != 1 and os.environ.get("ITRADER_RESTART_FROM_UPDATE") != "1":
        threading.Thread(
            target=_watch_parent,
            args=(parent_pid, lambda: loop.call_soon_threadsafe(_request_stop)),
            name="parent-watch",
            daemon=True,
        ).start()

    await service.startup()
    await server.start()
    print("等待 UI 连接，Ctrl+C 退出", flush=True)

    exit_reason = "signal"
    stop_task = asyncio.create_task(stop_signal.wait())
    exit_task = asyncio.create_task(service.exit_requested.wait())
    done, _ = await asyncio.wait(
        {stop_task, exit_task}, return_when=asyncio.FIRST_COMPLETED
    )
    if exit_task in done:
        exit_reason = "requested"
    stop_task.cancel()
    exit_task.cancel()

    await server.stop()
    if exit_reason == "signal":
        # 信号退出也要释放引擎与 TqApi 连接（requested 路径已在 quit/update_apply 里清理）
        await service.shutdown()
    print(f"后端已退出（{exit_reason}）", flush=True)
    return 0


def main():
    try:
        sys.exit(asyncio.run(run()))
    except KeyboardInterrupt:
        print("\n用户中断，正在退出...", flush=True)
        sys.exit(0)
    except RuntimeError as e:
        # 典型场景：已有后端在运行（见 rpc._reject_stale_backend）
        print(f"启动失败: {e}", file=sys.stderr, flush=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
