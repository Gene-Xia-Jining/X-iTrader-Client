项目说明

> 这是 iTrader 智能交易系统，使用 Python + TqSdk。
>
> 使用 ./DESIGN.md 作为设计参考。

职责

> 客户端负责：
>
> > 向服务器提交交易品种
> > 获取服务器交易信号
> > 按服务器目标仓位通过 TqSdk 调仓（开仓 / 平仓）
> > 查询并展示账户资金、持仓、委托、成交
> > 使用 SQLite 记录信号执行状态与历史成交
>
> 客户端不负责策略计算。
>

TqSdk 的实际职责

> * **交易执行**：`TargetPosTask` 以 ACTIVE 追价把持仓调到信号的目标手数，持续推进
>   `wait_update` 直到 `get_position` 确认到位才算成功；超时或拒单判失败并撤单。
> * **账户数据**：读取资金 / 持仓 / 委托 / 成交，另有按账户常驻的监听会话推送快照；
>   当日成交会沉淀到本地 SQLite（`trade_history`），支持跨交易日按时间区间查询。
> * **行情元信息**（不做策略计算）：`quote.trading_time` 用于非交易时段门控，
>   `quote.last_price` / `volume_multiple` 用于持仓估值与合约乘数，
>   `quote.underlying_symbol` 用于把主连换成可下单的实际合约。
> * **认证与连通**：`TqAuth` 登录快期权限账户、启动前交易网关预探测、
>   `TqApi` 握手校验账户权限。
>
> 客户端不订阅 K 线、不计算指标，行情只服务于上述用途。
>

技术栈

> Python
> TqSdk
> SQLite

平台支持：

> macOS .app
> Windows .exe

规则

> 交易方向由服务器决定。
> 实际交易价格使用客户端实时市场价格（`TargetPosTask` 追价成交，不使用服务器信号价）。
> 主连（`KQ.m@` / `KQ.i@`）只能看行情，下单前必须换成 `underlying_symbol` 的实际合约。
> 交易成功 = 持仓到位；单次 `wait_update` 不代表成交，拒单判定只认本次新产生的委托。
> 不要在客户端重复实现服务器策略。
> 同一个交易信号不要重复执行。
> SQLite 用于记录信号及执行状态。
> 修改交易逻辑后必须进行验证。
> 保持 macOS / Windows 兼容。
> 不要为了小功能引入复杂架构或不必要的依赖。

## 依赖管理

> * 依赖以 `requirements.txt` 为唯一来源（锁定版本，含全部直接依赖与传递依赖）。
> * 不要创建或恢复 `pyproject.toml`，不要引入 poetry / `pip install .` 等包管理工具。
> * 开发态建环境用 uv：`uv venv .venv --python 3.14` + `uv pip install -r requirements.txt`（`.venv` 内也可 `pip install -r requirements.txt`）。
> * 构建期允许用 uv（下载 Python 运行时、往包内 site-packages 装依赖）；uv 只存在于 `build.sh`/`specs/build_backend.py` 与 CI，产物与运行时均不含 uv。

## 构建

> * `./build.sh` 执行 macOS `.app` 打包（SwiftUI 壳 + 嵌入式 Python 后端组装），产物位于仓库上一级：`../X-iTrader-Client.app`。
> * 后端不再用 PyInstaller 冻结，改由 `specs/build_backend.py` 拼装：python-build-standalone 运行时（`uv python install`，缓存在 `build/py-runtime`）+ uv 装进包内 site-packages + 源码外置。产物为 `build/backend/{runtime,code}`。
> * 壳的启动命令是 `runtime/bin/python3.X -E -s code/backend_entry.py`（Windows 为 `runtime\python.exe -E -s code\backend_entry.py`），解释器按版本号探测、不硬编码。
> * `./build_dmg.sh` 把该 `.app` 封装成可分发的 `../X-iTrader-Client-{version}-Mac.dmg`（拖拽安装布局，需先跑 `build.sh`）。本地验证更新不需要它。
> * CI（`.github/workflows/build.yml`）在 push main 时自动构建并发布 Release：macOS 发 `X-iTrader-Client-{version}-Mac.dmg`，Windows 发 `X-iTrader-Client-{version}-Windows.zip`，另在仅 src 变化时发全平台共用的 `x-itrader-client-{version}-src.zip` 补丁包。
> * 客户端自动更新（`src/infrastructure/updater.py`）：GitHub 源 macOS 走 dmg（hdiutil 挂载 + ditto 提取）、Windows 走 zip；**本地源 macOS 只认 `.app` 目录（ditto 复制），不扫 dmg**（历史 dmg 会遮住刚打包的新版本）。补丁包恒为 zip，替换的是 `backend/code/src`。
> * 打包态判定统一用 `paths.is_packaged()`（环境标记 `ITRADER_PACKAGED=1`，由 `backend_entry.py` 设置），**不要再用 `sys.frozen` / `sys._MEIPASS`**——嵌入运行时下两者都不存在。

## Windows 壳（WinUI 3）

> * 源码在 `shell-windows/`（C# / .NET 8 的 WinUI 3 工程 `X-iTrader.Shell.csproj`），与 mac 的 SwiftUI 壳平级，同样只做「后端进程管理 + RPC 通信 + 界面」，不含交易逻辑。
> * 发布形态为 **unpackaged + 自包含**（`SelfContained=true` + `WindowsAppSDKSelfContained=true`）：.NET 8 运行时与 Windows App SDK 原生库都打进产物，目标机**无需安装任何运行环境**，解压双击即用；不再随包分发 `安装运行环境.cmd`，根目录 `README.txt` 是面向用户的说明。
> * 自包含模式下 SDK 会自动关闭 bootstrapper 并启用 UndockedRegFreeWinUI，因此不再存在 Windows App Runtime 版本线精确匹配的问题。`Microsoft.WindowsAppSDK.Runtime` 只用于框架依赖部署，自包含时**不引用**（引用它会带进 onnxruntime / DirectML 等无关大文件）。
> * 体积控制：自包含的硬成本是 .NET 运行时（约 70MB）+ WASDK 原生库（约 52MB）。配置层面只做了两件事——用 `SatelliteResourceLanguages` 裁掉非中英文附属程序集，以及在自包含拷贝前排除 WinUI 的 86 种语言 `.mui` 资源（保留 zh-CN / zh-TW / en-us）。`PublishReadyToRun` / `PublishSingleFile` / `PublishTrimmed` 均未开启：前两者不减小体积，后者 WinUI 3 官方不支持。
> * 分发包布局：根级 `X-iTrader.exe`（壳）+ `backend\runtime\python.exe`（嵌入式 Python 与依赖）+ `backend\code\`（源码），壳的运行时文件散落在根级与子目录。**安装根靠根级 `X-iTrader.exe` 定位**（`updater.py` 的 `app_install_root()`），不是靠后端 exe 的父目录——后端已在 `backend\runtime\` 下。
> * 本地 macOS 无法构建 `net8.0-windows`，Windows 壳的编译与运行只能靠 CI（`windows-latest`）验证。
> * 已知限制：Windows 的**全量**自更新会因目录被占用（壳与后端都在安装目录内且正在运行）而失败，`src` 差量更新不受影响；后续需改为延迟替换或壳侧更新。

