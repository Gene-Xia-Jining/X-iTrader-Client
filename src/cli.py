import asyncio
import os
import sys
from pathlib import Path

from .application.bootstrap import Bootstrap
from .domain.events import LogEvent, LogLevel
from .infrastructure.paths import data_root, is_packaged, migrate_legacy_data_root

async def _ensure_token(bs: Bootstrap, request_input) -> bool:
    """确保有一个可用 token；无效时申请新的（含设备凭据自愈）。

    原先调用的是不存在的 ``request_new_token``，任何走到这个分支的 CLI
    启动都会 AttributeError；这里改用真实方法并补齐设备登记。
    """
    from .infrastructure.api.client import DeviceCredentialError

    token_service = bs.token_service
    if token_service.token_store.have_token():
        token = token_service.token_store.load_token()
        if await token_service.validate_token(token):
            return True
        print("⚠️  Token 无效，请求新 token...")

    await bs.ensure_device_registered()
    description = request_input("Enter token description (optional): ").strip()
    try:
        result = await token_service.request_token(description)
    except DeviceCredentialError as exc:
        if not exc.recoverable:
            print(f"❌ 设备凭据无效（{exc.code}）：{exc.detail}")
            return False
        print("⚠️  设备凭据已失效，正在重新登记本机设备…")
        if not await bs.recover_device_credentials():
            print("❌ 重新登记设备失败，请让管理员确认后重试")
            return False
        result = await token_service.request_token(description)

    # 服务器只返回申请记录，明文要靠 claim 取（自动批准时才有）
    return bool(result.get("token_id"))

async def run_headless():
    if is_packaged():
        migrate_legacy_data_root()
        workdir = data_root()
        workdir.mkdir(parents=True, exist_ok=True)
        os.chdir(workdir)
    Path("data").mkdir(parents=True, exist_ok=True)
    bs = Bootstrap()

    # 没有可用 token 就别往下跑：订阅会被服务器 401 拒掉，界面只会看到连接错误
    if not await _ensure_token(bs, input):
        print("没有可用的 API Token，已退出。请先在服务器端批准本设备的申请。")
        return

    def _print_log(evt: LogEvent):
        level_prefix = {
            LogLevel.INFO: "[INFO]",
            LogLevel.WARNING: "[WARN]",
            LogLevel.ERROR: "[ERR ]",
            LogLevel.SUCCESS: "[ OK ]",
        }.get(evt.level, "[LOG]")
        ts = evt.timestamp.strftime("%H:%M:%S")
        print(f"{ts} {level_prefix} {evt.message}")

    bs.event_bus.subscribe(LogEvent, _print_log)

    enabled = [a for a in bs.accounts if a.enabled]
    if not enabled:
        print("没有已启用的交易账户，请先在应用中配置账户。")
        return
    tasks = [
        asyncio.create_task((await bs.build_engine(account.id)).start())
        for account in enabled
    ]
    try:
        await asyncio.gather(*tasks)
    finally:
        await bs.shutdown()

def main():
    try:
        asyncio.run(run_headless())
    except KeyboardInterrupt:
        print("\n用户中断，正在退出...")
        sys.exit(0)

if __name__ == "__main__":
    main()
