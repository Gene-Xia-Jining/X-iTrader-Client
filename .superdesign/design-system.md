# iTrader — Design System

## Product Context
- **产品**：iTrader 智能交易客户端（PySide6 桌面应用，macOS .app / Windows .exe），主窗口 1180×760。
- **职责**：向服务器提交交易品种、接收服务器信号、按目标仓位通过 TqSdk 调仓（持仓到位才算成功）、查询并展示账户资金/持仓/委托/成交、SQLite 记录信号执行状态与历史成交。客户端不做策略计算，也不订阅 K 线。
- **JTBD**：交易者需要一眼确认「连着服务器没有、自动交易跑没跑、最近发生了什么」，并低频地配置账号/品种/服务器地址；高频看状态与日志，低频改配置。
- **关键页面**：仪表盘（状态总览）、Token 管理、交易日志、交易账号、设置。侧边栏导航 + 顶部操作条 + 底部状态栏。

## Design Language（硬约束）
Apple 设计语言（来源 `DESIGN.md` / `theme.py`）。**只使用下方定义的字体、颜色、间距与组件样式，不得引入体系外的颜色/字体/阴影。**

### Colors — Light (default)
- 画布/内容区：`#f5f5f7` (parchment)；卡片/侧边栏/菜单/状态栏：`#ffffff`；软胶囊底：`#fafafc` (pearl)
- 正文 `#1d1d1f`；次级 `#7a7a7a`；弱化 `#cccccc`；强调正文 `#333333`
- 边框 hairline `#e0e0e0`；soft divider `#f0f0f0`
- 唯一交互色 Action Blue：`#0066cc`（hover/focus `#0071e3`）；on-primary `#ffffff`
- 语义色：成功 `#34c759` · 警告 `#e8a33d` · 危险 `#e3342f`(hover `#c5241f`)；运行状态点：在线绿 `#2ecc71` / 离线红 `#e74c3c`

### Colors — Dark
- 内容区 `#272729`；侧边栏/菜单/状态栏 `#252527`；输入框 `#2a2a2c`
- 正文 `#ffffff`；次级 `#cccccc`；弱化 `#86868b`；边框 `#3a3a3c`、hover `#48484a`
- 深色交互色 Sky Link Blue `#2997ff`（hover `#4aa3ff`）；success 按钮 `#30d158`

### Typography
- 栈：`-apple-system, 'SF Pro Text', 'SF Pro Display', system-ui, 'Segoe UI', 'PingFang SC', 'Helvetica Neue', Arial, sans-serif`；等宽 `'SF Mono', Menlo, Consolas, monospace`
- 基准 15px / ls -0.1px / 400；页面标题 22px / 600 / ls -0.4px；副标题 15px muted；大数字 metric 26px / 600 / ls -0.3px；辅助 13px
- 字重只用 400 / 600；禁止 500；正文不小于 13px

### Geometry & Spacing
- 圆角：卡片 18px；输入框/侧边栏项 8px；按钮/状态胶囊/搜索 9999px 胶囊；复选框 5px
- 按钮：min-height 36px，padding 10px 22px，600 字重；侧边栏项 44px 高
- 间距：卡片内 16-20px，页面边距 24px，区块间 20px，表单行距 10px
- 阴影：**卡片、按钮一律无阴影**；仅 1px hairline 边框分层。区块分隔优先靠表面色变化（白/parchment）

### Buttons（胶囊 = 动作信号）
- Primary：Action Blue 底白字胶囊；Secondary：透明底蓝字 + 1px hairline 边框；Danger：`#e3342f` 白字；Success：`#34c759` 白字（深色 `#30d158`）；Flat：透明底灰字，hover 浅灰底
- 状态标签（如「自动交易：开」）用 Success/Secondary 变体切换表达运行态

### StatusPill
- pearl 底 + 1px hairline + 全圆角胶囊；内含 10px 语义色圆点 + 15px/600 状态文字（如「未连接」红 /「已连接」绿 /「已停止」红 /「运行中」绿）

### Cards & Forms
- 卡片：白底 1px hairline 18px 圆角，内边距 16-20px，无阴影
- 输入框：白底 1px `#e0e0e0` 8px 圆角，focus 边框 Action Blue；占位符 `#cccccc`
- 表单：右对齐标签 + 行距 10px；主要「保存」右对齐

### App Shell（每页共用）
- 顶部 head：parchment 底，左「iTrader」22px/600 + 下行「智能交易客户端」15px muted；右侧胶囊按钮组（自动交易开关 / 主题切换 / 退出）
- 左 sidebar：白底 240px + 右 hairline；5 项导航（仪表盘 / Token 管理 / 交易日志 / 交易账号 / 设置），44px 高左对齐，选中浅灰底 + hairline 描边
- 底部状态栏：白底顶 hairline，灰字「就绪」/最近日志摘要
- 页面内容：PageHeader（22px 标题 + 15px 副标题）+ 白卡网格

### Motion
- 桌面 Qt 环境动效极简：仅按钮按压态（视觉上等价 scale 0.95 的反馈由变色承担）与状态点颜色过渡；不引入装饰性动画
