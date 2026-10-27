import asyncio
import os
import sys
from pathlib import Path

from .application.bootstrap import Bootstrap
from .domain.events import LogEvent, LogLevel
from .infrastructure.paths import data_root, is_packaged, migrate_legacy_data_root

async def _ensure_token(bs: Bootstrap, request_input) -> bool:
    token_service = bs.token_service
    if token_service.token_store.have_token():
        token = token_service.token_store.load_token()
        if await token_service.validate_token(token):
            return True
        print("⚠️  Token 无效，请求新 token...")
    description = request_input("Enter token description (optional): ").strip()
    token = await token_service.request_new_token(description)
    return bool(token)

async def run_headless():
    if is_packaged():
        migrate_legacy_data_root()
        workdir = data_root()
        workdir.mkdir(parents=True, exist_ok=True)
        os.chdir(workdir)
    Path("data").mkdir(parents=True, exist_ok=True)
    bs = Bootstrap()

    await _ensure_token(bs, input)

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
