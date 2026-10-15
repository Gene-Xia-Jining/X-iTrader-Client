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

> * 可以运行 `./build_app.sh` 执行 macOS `.app` 打包（SwiftUI 壳 + PyInstaller onedir 后端组装）。
> * 打包产物位于 `dist/X-iTrader-Client.app`。
