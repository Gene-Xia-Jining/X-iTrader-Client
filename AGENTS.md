项目说明

> 这是 iTrader 智能交易系统，使用 Python + TqSdk。
>
> 使用 ./DESIGN.md 作为设计参考。

职责

> 客户端负责：
>
> > 向服务器提交交易品种
> > 获取服务器交易信号
> > 使用 TqSdk 获取实时行情
> > 根据服务器信号执行开仓 / 平仓
> > 使用 SQLite 记录信号执行状态
>
> 客户端不负责策略计算。
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
> 实际交易价格使用客户端实时市场价格。
> 不要在客户端重复实现服务器策略。
> 同一个交易信号不要重复执行。
> SQLite 用于记录信号及执行状态。
> 修改交易逻辑后必须进行验证。
> 保持 macOS / Windows 兼容。
> 不要为了小功能引入复杂架构或不必要的依赖。

## 依赖管理

> * 依赖以 `requirements.txt` 为唯一来源（锁定版本，含全部直接依赖）。
> * 不要创建或恢复 `pyproject.toml`，不要引入 uv / poetry / `pip install .` 等包管理工具。
> * 安装依赖使用 `pip install -r requirements.txt`（在 `.venv` 中）。
>

## 构建

> * `./build_app.sh` 执行 macOS `.app` 打包（SwiftUI 壳 + PyInstaller onedir 后端组装），产物位于仓库上一级：`../X-iTrader-Client.app`。
> * `./build_dmg.sh` 把该 `.app` 封装成可分发的 `../iTrader-{version}-macos.dmg`（拖拽安装布局，需先跑 `build_app.sh`）。
> * CI（`.github/workflows/build.yml`）在 push main 时自动构建并发布 Release：macOS 发 dmg，Windows 发 zip，另在仅 src 变化时发全平台共用的 src 补丁包。
> * 客户端自动更新（`src/infrastructure/updater.py`）macOS 走 dmg（hdiutil 挂载 + ditto 提取）、Windows 走 zip，补丁包恒为 zip。

## Windows 壳（WinUI 3）

> * 源码在 `shell-windows/`（C# / .NET 8 的 WinUI 3 工程 `X-iTrader.Shell.csproj`），与 mac 的 SwiftUI 壳平级，同样只做「后端进程管理 + RPC 通信 + 界面」，不含交易逻辑。
> * 发布形态为 **unpackaged + 框架依赖**：产物不内置 .NET 8 Desktop Runtime 与 Windows App Runtime，首次使用需由随包的 `安装运行环境.cmd`（或壳内 `RuntimeBootstrap`）引导安装；根目录 `README.txt` 是面向用户的说明。
> * 分发包为**扁平布局**：根级 `X-iTrader.exe`（壳）+ `X-iTrader-Backend.exe`（后端）+ `_internal\`（后端依赖，含 `src\`）。该命名与布局是硬约束——`updater.py` 的 Windows 分支把「后端 exe 的父目录」当安装根，并要求根级有 `X-iTrader.exe` 作为全量更新后的重启目标。
> * 后端产物名由 `specs/backend.spec` 按平台决定：Windows 为 `X-iTrader-Backend`，macOS 保持 `backend`（不改动 mac 现有打包链）。
> * 本地 macOS 无法构建 `net8.0-windows`，Windows 壳的编译与运行只能靠 CI（`windows-latest`）验证。
> * 已知限制：Windows 的**全量**自更新会因目录被占用（壳与后端 exe 都在安装目录内且正在运行）而失败，`src` 差量更新不受影响；后续需改为延迟替换或壳侧更新。

