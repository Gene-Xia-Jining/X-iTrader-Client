# iTrader 本地 RPC 协议 v1

headless Python 后端与 UI 壳（SwiftUI / CLI）之间的本地通信协议。

## 传输与发现

- 传输：`127.0.0.1` TCP，NDJSON（每行一个 UTF-8 JSON，`\n` 结尾）。
- 发现：后端启动时把连接信息写入 `data/backend.json`：

  ```json
  {"port": 52134, "token": "<64位hex>", "pid": 12345, "version": "0.0.2"}
  ```

- 壳进程负责后端生命周期：spawn → 读 backend.json → 连接；后端崩溃时壳可读 `pid` 探测后重启（重启路径与配置变更一致，信号不重复执行由 SQLite 状态兜底）。
- 同机只允许一个后端实例：端口被占用或 backend.json 中 pid 存活时，新后端启动失败。

## 握手

连接后 3 秒内，客户端发首行，服务端应答：

```jsonc
→ {"v": 1, "token": "<backend.json 中的 token>"}
← {"ok": true, "version": "0.0.2", "pid": 12345}
← {"ok": false, "error": "认证失败"}        // 随后断开
```

## 请求 / 响应

```jsonc
→ {"id": 1, "method": "engine.start", "params": {}}
← {"id": 1, "ok": true,  "result": {...}}
← {"id": 1, "ok": false, "error": "服务器未连接: ...", "type": "RuntimeError"}
← {"id": 1, "ok": false, "error": "账户「A」未配置快期账号；…", "type": "EngineStartBlockedError",
   "data": {"issues": ["账户「A」未配置快期账号", ...]}}
```

- 每连接请求**串行**处理，响应按完成顺序返回（客户端按 `id` 匹配）。
- `type` 为服务端异常类名（`ValueError`/`RuntimeError`/`TqAuthError` 等），供壳展示。
- 异常若实现 `to_payload()`，响应会附带 `data` 字段携带结构化信息；
  当前 `EngineStartBlockedError` 用 `data.issues`（字符串数组）回传启动前预检问题清单，
  供客户端以弹窗列表形式展示。

## 事件（服务端 → 客户端，无 id）

| event | data | 说明 |
|---|---|---|
| `log` | `{message, level, timestamp}` | level: INFO/WARNING/ERROR/SUCCESS |
| `status` | `{trading_active, server_connected, account_id}` | 引擎状态变化；心跳探测到连通性/失败原因变化时仅含 `{server_online, server_error}` |
| `trading_started` / `trading_stopped` | `{account_id}` | |
| `signal_received` | `{signal: {id, symbol, side, price, position, updated_at}}` | 服务器信号到达 |
| `trade_executed` | `{symbol, target_position}` | 调仓指令已交付 |
| `trade_failed` | `{symbol, error}` | |
| `trading_records_updated` | `{kind, account_id, record}` | 账户交易数据变动（资金/持仓/委托/成交），后台常驻 watcher 监听到快期推送后自动广播 |
| `config_changed` | `{}` | 配置/账户已保存（引擎运行中则下次启动生效） |
| `token_status` | `{status, records: [...]}` | token 审批状态变化；后台轮询领取成功后广播，壳据此刷新 Token 页 |
| `update_progress` | `{pct, received, total}` | pct=-1 表示总大小未知 |
| `engine_start_progress` | `{stage, text, pct}` | `engine.start`/`engine.stop` 执行中的进度（该调用返回前过程状态无从获取）。`stage` 依次为 `server`/`accounts`/`tqsdk`/`engines`（stop 为 `stop`，且没有可握手的账户时 `tqsdk` 会被跳过）；`text` 是可直接展示的中文阶段文案，壳自行加前缀；`pct` 是**整次启动**的百分比（按各阶段经验权重折算，起步即 ≥1，正常走到 100），`pct=-1` 表示没有可画的进度（停止操作），壳此时只显示文案。预检被拦时 `pct` 停在拦截点，不会走满 |
| `update_ready` | `{kind, mode}` | kind 是给人看的中文包名（"差量更新包"/"完整更新包"）；mode 为 `"patch"`/`"full"`，壳据此决定装完是重连还是退出 |
| `update_failed` / `update_cancelled` | `{message}` / `{}` | |
| `backend_stopping` | `{reason: "quit"\|"update"}` | 后端即将退出，壳应断开并等待 |

## 方法

| method | params | result |
|---|---|---|
| `app.status` | - | `{version, pid, config, accounts, engines}` 全量初始状态 |
| `config.save` | `{server_url?, proxy_url?, skipped_version?}` | `{}` 未提供的字段沿用 |
| `accounts.list` | - | `{accounts: [...]}` **不含密码**；每项含 `has_tq_password` / `has_trade_password` 与 `tq_password_length` / `trade_password_length`（明文长度，供 UI 按真实长度绘制掩码圆点） |
| `accounts.save` | `{kind: "sim"\|"live", account_id?, label?, tq_account?, tq_password?, broker?, trade_account?, trade_password?, symbols?, strategy?, enabled?}` | `{account_id}` 未提供的字段沿用已有账户；symbols 支持数组或逗号分隔字符串；strategy 为服务器策略 id（空字符串 = 清除选择）；无 account_id 时 sim→`legacy`、live→`legacy-live` |
| `accounts.delete` | `{account_id}` | `{}` |
| `accounts.test_tq_auth` | `{account, password}` | `{message}` 验证快期权限账户 |
| `server.test` | `{server_url?}` | `{message}` 缺省用当前配置 |
| `symbols.fetch` | - | `{symbols: [...], message}` 服务器品种列表，元素为 `{symbol, name}`（name 为中文名，可能为空；旧版后端为纯代码字符串） |
| `strategies.fetch` | - | `{strategies: [...], message}` 服务器策略列表，元素为 `{id, name, description}`（id 用于 accounts.save 的 strategy 与订阅上报） |
| `tokens.status` | - | `{status, records: [...], active_token_id, cached_token_ids}`；`records` 为申请记录，元素 `{token_id, description, status, created_at, expires_at, use_count, claimable}`（created_at/expires_at 为 Unix 秒），倒序；`claimable` 表示服务器是否还留有可领取明文（首次领取后为 false）；`active_token_id` 为当前生效记录 id，`cached_token_ids` 为本地留有明文可切换回去的记录 id 列表；`status` 文案：未申请/待审核/已通过，token 已加密保存/已通过，但服务器未返回 token/已拒绝。已通过但本地无 token 时就地领取一次 |
| `tokens.request` | `{description?}` | `{status}` 提交申请后立即触发一次后台领取轮询 |
| `tokens.delete` | `{token_id}` | `{status, records, active_token_id, cached_token_ids}` 删除一条申请记录后返回刷新后的状态；删掉的若是当前生效记录，本地 token 与该记录的凭据缓存一并清除。记录不存在时抛错 |
| `tokens.switch` | `{token_id}` | `{status, records, active_token_id, cached_token_ids}` 把该记录的 Token 设为当前使用：优先取本地缓存，没有则向服务器定向领取；两者都不可用时抛错。切换后若引擎在运行会被停止（信号流的请求头固定了旧 token），需重新开启自动交易 |
| `engine.start` | - | engines 状态。启动前预检（服务器连通、已启用账户字段齐全（品种、策略必选）、本地 token、天勤握手）；任一项不过抛 `EngineStartBlockedError`（`data.issues` 为问题列表），不置 `auto_trade`、不启动任何引擎。执行中通过 `engine_start_progress` 事件推送阶段进度 |
| `engine.stop` | - | engines 状态。等价 UI 的"自动交易"关；执行中同样推送 `engine_start_progress`（`stage: "stop"`） |
| `engine.set_auto_trade` | `{value}` | `{}` 只改自动执行标志，不动引擎 |
| `engine.status` | - | `{auto_trade, trading_active, server_online, server_error, accounts: [{account_id, running, server_connected}]}`；`server_online` 为后端心跳探测的 iTrader 服务器连通性（启动即测、每 10s 周期、保存配置后立即重测），与引擎是否运行无关；`server_error` 是最近一次探测失败的可读原因（在线时为空串），供界面展示 |
| `trading.records` | `{kind?: "sim"\|"live", account_id?, date_range?: "today"\|"3d"\|"7d"\|"all"}` | `{accounts: [{account_id, kind, label, error?, account, positions, orders, trades}]}` 账户交易数据快照（字段见下）。不传 kind / account_id 时返回全部已配置账户；支持按 `date_range` 筛选或查询历史沉淀的成交记录（默认 today 为当日实时）；多账户并发取数，单账户失败只体现在该项 `error` |
| `update.check` | `{silent?}` | `{status: "latest"\|"available"\|"none"\|"error"\|"skipped", message, version?, has_patch?}`；silent 尊重"跳过此版本" |
| `update.download` | - | `{status: "downloading"}` 进度走 `update_progress` 事件 |
| `update.cancel` | - | `{}` |
| `update.apply` | - | `{}` 停引擎→替换安装物→重启后端→`backend_stopping`；开发态报错 |
| `app.quit` | - | `{}` 停引擎→`backend_stopping`→后端退出 |

## `trading.records` 返回字段

每个账户一项，`account` / `positions` / `orders` / `trades` 均为**当日**快照（TqSdk 只提供当前交易日数据，历史成交需 TqSdk 专业版，这里不提供）。

```jsonc
{
  "account_id": "legacy-live",
  "kind": "live",
  "label": "实盘账户",
  "error": "…",                       // 仅取数失败时出现；此时其余字段为空
  "account": {
    "balance": 100000.0,              // 账户权益（动态权益）
    "available": 90000.0,             // 可用资金
    "margin": 10000.0,                // 保证金占用
    "float_profit": 120.0,            // 浮动盈亏
    "position_profit": 100.0,         // 持仓盈亏（相对昨结算）
    "close_profit": -20.0,            // 本交易日平仓盈亏
    "commission": 5.0,                // 本交易日手续费
    "risk_ratio": 0.1,                // 风险度
    "pre_balance": 99900.0,           // 昨日权益
    "static_balance": 100000.0        // 静态权益
  },
  "positions": [
    {
      "symbol": "SHFE.rb2405",
      "direction": "多",              // 多 / 空，一个合约的多空各成一行
      "volume": 2,
      "open_price": 3800.0,           // 开仓均价
      "position_price": 3790.0,       // 持仓均价
      "float_profit": 120.0,
      "margin": 7600.0
    }
  ],
  "orders": [
    {
      "order_id": "…",
      "symbol": "SHFE.rb2405",
      "direction": "BUY",
      "direction_text": "买",
      "offset": "OPEN",
      "offset_text": "开仓",          // 开仓 / 平仓 / 平今 / 平昨
      "volume_orign": 1,              // 总报单手数
      "volume_left": 0,               // 未成交手数
      "limit_price": 3800.0,          // 限价单价格，市价单为 null
      "trade_price": 3800.0,          // 平均成交价
      "status": "FINISHED",
      "status_text": "已完",          // 已报 / 已完
      "last_msg": "全部成交",
      "insert_time": "2024-01-01 09:00:00",   // 北京时间，已格式化
      "source": "智能信号"            // 智能信号 / 手动/外部
    }
  ],
  "trades": [
    {
      "trade_id": "…",
      "order_id": "…",
      "symbol": "SHFE.rb2405",
      "direction": "BUY",
      "direction_text": "买",
      "offset": "OPEN",
      "offset_text": "开仓",
      "price": 3800.0,
      "volume": 1,
      "trade_time": "2024-01-01 09:00:01",
      "commission": 5.0,              // 手续费（可空）
      "realized_profit": null,        // 单笔平仓盈亏金额（开仓单为 null）
      "source": "智能信号"            // 智能信号 / 手动/外部
    }
  ]

}
```

- 数值字段在 TqSdk 中可能是 `nan`（数据未就绪），后端统一转 `null`，客户端须按可空处理。
- 时间字段由后端按北京时间（固定 +08:00）格式化成字符串，客户端不再做时区换算。
- 取数走临时只读会话（建 TqApi → 读 → 关），与 `engine` 会话隔离：自动交易未开启也能查，查询失败不影响正在运行的调仓。

## 约定

- `accounts.save` 密码留空 = 不修改；`accounts.list` 不回传密码。
- 账户 `strategy` 随引擎订阅上报：`/api/stream?symbols=...&strategy=<id>`，由服务器按该策略计算信号。
- `engine.start/stop` 与 UI 开关语义绑定（联动 auto_trade）；`engine.set_auto_trade` 用于只改标志。
- 交易语义：`trade_executed` 仅表示调仓指令已交付柜台（TargetPosTask），不是成交回报。
- 配置/账户变更在引擎运行中只生效于下次启动（后端广播 log 事件提示）。
