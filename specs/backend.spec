# -*- mode: python ; coding: utf-8 -*-

import os
import sys

from PyInstaller.building.datastruct import Tree
from PyInstaller.utils.hooks import collect_all, collect_submodules

# SPECPATH 是 PyInstaller 注入的 spec 文件所在目录
ROOT = os.path.abspath(os.path.join(SPECPATH, '..'))
SRC = os.path.join(ROOT, 'src')

# 版本号唯一来源是 src/__init__.py（仅用于交叉确认，产物不单独记录版本）
sys.path.insert(0, ROOT)
from src import __version__ as VERSION  # noqa: E402,F401

# src 整个目录以源码外置（不打进 PYZ）：差量更新只需替换 _internal/src，
# 二进制与 PYZ 不变；updater.src_install_dir() 指向此处。入口 backend_entry.py
# 用 importlib 动态导入，避免静态分析把 src 收进 PYZ 而使外置失效。
# 排除 __pycache__/.pyc/.DS_Store，避免开发态缓存与系统文件混进包。
# Tree 项为 (dest, src, kind) 且 dest 是完整文件路径；datas 语义是
# (源文件, 目标目录)，故取 dest 的目录部分。
datas = [
    (src_file, os.path.dirname(dest))
    for dest, src_file, _kind in Tree(
        SRC, prefix='src', excludes=['*__pycache__*', '*.pyc', '*.DS_Store']
    )
]

# headless 后端（src/backend 入口）依赖树不含 Qt/presentation，全部排除；
# src 已由上方 datas 外置，其第三方依赖静态分析看不到，必须完整显式收集。
binaries = []
hiddenimports = [
    *collect_submodules('httpx'),
    *collect_submodules('aiosqlite'),
]

# cryptography（含 Fernet、hazmat 等全部子模块及 cffi 绑定）
crypto_datas, crypto_binaries, crypto_hiddenimports = collect_all('cryptography')
datas += crypto_datas
binaries += crypto_binaries
hiddenimports += crypto_hiddenimports

# tqsdk 运行时必需数据（含 expired_quotes.json.lzma）。
# 过滤掉交互 Web 前端与示例：本客户端只走 TqApi/TargetPosTask 下单路径，
# 不启动 tqsdk 的 Web 服务（TqApi(web_gui=False) 默认关闭），web/ 与 demo/
# 永远不会被读取。注意 hiddenimports 也要过滤：demo 下 90 个模块虽无 datas
# 文件，但作为模块会被收进 PYZ。
_tq_datas, tq_binaries, tq_hiddenimports = collect_all('tqsdk')
_drop = ('tqsdk/web', 'tqsdk/demo')
datas += [d for d in _tq_datas if not d[1].startswith(_drop)]
binaries += tq_binaries
hiddenimports += [
    m for m in tq_hiddenimports
    if m != 'tqsdk.web' and not m.startswith('tqsdk.web.')
    and m != 'tqsdk.demo' and not m.startswith('tqsdk.demo.')
]

a = Analysis(
    [os.path.join(SPECPATH, 'backend_entry.py')],
    pathex=[ROOT],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[os.path.join(SPECPATH, 'runtime_hook.py')],
    excludes=[
        # UI 层依赖（旧 GUI 版专属），headless 后端不导入
        'PySide6',
        'shiboken6',
        # tqsdk/tafunc.py 顶层 `from scipy import stats`，但 stats 仅被期权定价
        # 私有函数使用，客户端不会触发；由 runtime_hook.py 提供等价 stub
        'scipy',
        # pydantic 全项目零引用（src 与 tqsdk 均不使用）。它被 yarl/_url.py 的
        # TYPE_CHECKING 分支 import 而误纳入依赖图（yarl 运行时只用
        # find_spec("pydantic_core") 探测，不导入真包），pydantic 本体 0.2M +
        # pydantic_core 3.9M 因此纯属虚增。
        'pydantic',
        'pydantic_core',
    ],
    noarchive=False,
)

# tqsdk 自带 PyInstaller entry-point hook（tqsdk/__pyinstaller/hook-tqsdk.py）在
# Analysis 阶段会 collect_data_files('tqsdk', includes=['web', ...]) 无条件把 web
# 前端塞进 a.datas，spec 上方的过滤管不到它（那份只作用于 collect_all 返回值）。
# 这里在 Analysis 之后按同一规则兜底剔除，web 目录因此不再进入产物。
a.datas = [d for d in a.datas if not d[0].startswith(_drop)]

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='backend',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=True,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    name='backend',
)
