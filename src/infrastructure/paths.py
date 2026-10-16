"""应用数据目录与运行时布局（打包态与开发态共用的唯一定义）。

打包后应用的可写数据集中在一个根目录下，与 .app 安装位置无关：

    ~/.X-iTrader-Client/data/               业务数据（client.db、token、.secret.key、配置）
    ~/.X-iTrader-Client/data/update/        自动更新暂存
    ~/.X-iTrader-Client/runtime/backend/    壳首启下载的 PyInstaller 后端运行时

runtime/ 与 data/ 平级分离：整个后端运行时的替换（首次安装或后续更新）只动
runtime/backend，不触碰业务数据。

历史：旧版本数据根是 ~/.iTrader，升级后由 migrate_legacy_data_root() 一次性
迁入新根，避免 token / client_id / 数据库静默失联。
"""

import shutil
from pathlib import Path

# 数据根目录名（位于用户主目录下；Windows 上同样落在 %USERPROFILE% 下）
APP_DIR_NAME = ".X-iTrader-Client"
# 旧版本数据根目录名（一次性迁移的来源）
LEGACY_DIR_NAME = ".iTrader"

RUNTIME_DIR_NAME = "runtime"
BACKEND_DIR_NAME = "backend"


def data_root() -> Path:
    """应用数据根目录（frozen 后端的 workdir）。"""
    return Path.home() / APP_DIR_NAME


def legacy_data_root() -> Path:
    """旧版本数据根目录（仅用于迁移）。"""
    return Path.home() / LEGACY_DIR_NAME


def runtime_dir() -> Path:
    """运行时根目录：存放壳下载的后端及后续其它运行时组件。"""
    return data_root() / RUNTIME_DIR_NAME


def backend_dir() -> Path:
    """壳首启下载解压的后端运行时目录（PyInstaller onedir 内容，含 backend 可执行文件）。"""
    return runtime_dir() / BACKEND_DIR_NAME


def migrate_legacy_data_root() -> bool:
    """把旧数据根 ~/.iTrader 迁入 ~/.X-iTrader-Client，返回是否发生了迁移。

    逐项移动而非整体重命名：引导下载可能先于后端启动创建好 runtime/，此时
    新根已存在却仍需把旧 data/ 搬过来。目标同名的项一律跳过，绝不覆盖新根
    下已有的数据。旧根仅在迁移后为空时才删除。

    运行时发现文件 data/backend.json 会被后端本次启动重写，其 pid/port 也已
    失效，无需特殊处理（壳复用后端前会校验 pid 存活）。
    """
    legacy = legacy_data_root()
    if not legacy.is_dir():
        return False

    target = data_root()
    moved = False
    for entry in legacy.iterdir():
        dest = target / entry.name
        if dest.exists():
            continue
        target.mkdir(parents=True, exist_ok=True)
        shutil.move(str(entry), str(dest))
        moved = True

    # 已空（如只含已迁移的 data/）则清除；残留项不清除，不影响使用
    try:
        legacy.rmdir()
    except OSError:
        pass
    return moved
