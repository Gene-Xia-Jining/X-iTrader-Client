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
```

- 每连接请求**串行**处理，响应按完成顺序返回（客户端按 `id` 匹配）。
- `type` 为服务端异常类名（`ValueError`/`RuntimeError`/`TqAuthError` 等），供壳展示。

## 事件（服务端 → 客户端，无 id）

| event | data | 说明 |
|---|---|---|
| `log` | `{message, level, timestamp}` | level: INFO/WARNING/ERROR/SUCCESS |
| `status` | `{trading_active, server_connected, account_id}` | 引擎状态变化；心跳探测到连通性变化时仅含 `{server_online}` |
| `trading_started` / `trading_stopped` | `{account_id}` | |
| `signal_received` | `{signal: {id, symbol, side, price, position, updated_at}}` | 服务器信号到达 |
| `trade_executed` | `{symbol, target_position}` | 调仓指令已交付 |
| `trade_failed` | `{symbol, error}` | |
| `config_changed` | `{}` | 配置/账户已保存（引擎运行中则下次启动生效） |
| `update_progress` | `{pct, received, total}` | pct=-1 表示总大小未知 |
| `update_ready` | `{kind}` | "差量更新包"/"完整更新包" |
| `update_failed` / `update_cancelled` | `{message}` / `{}` | |
| `backend_stopping` | `{reason: "quit"\|"update"}` | 后端即将退出，壳应断开并等待 |

## 方法

| method | params | result |
|---|---|---|
| `app.status` | - | `{version, pid, config, accounts, engines}` 全量初始状态 |
| `config.save` | `{server_url?, proxy_url?, auto_check_update?, skipped_version?}` | `{}` 未提供的字段沿用 |
| `accounts.list` | - | `{accounts: [...]}` **不含密码** |
| `accounts.save` | `{kind: "sim"\|"live", account_id?, label?, tq_account?, tq_password?, broker?, trade_account?, trade_password?, symbols?, enabled?}` | `{account_id}` 未提供的字段沿用已有账户；symbols 支持数组或逗号分隔字符串；无 account_id 时 sim→`legacy`、live→`legacy-live` |
| `accounts.delete` | `{account_id}` | `{}` |
| `accounts.test_tq_auth` | `{account, password}` | `{message}` 验证快期权限账户 |
| `server.test` | `{server_url?}` | `{message}` 缺省用当前配置 |
| `symbols.fetch` | - | `{symbols: [...], message}` 服务器品种列表，元素为 `{symbol, name}`（name 为中文名，可能为空；旧版后端为纯代码字符串） |
| `tokens.status` | - | `{status}` |
| `tokens.request` | `{description?}` | `{status}` |
| `engine.start` | - | engines 状态。先探测服务器，未连接则失败；等价 UI 的"自动交易"开 |
| `engine.stop` | - | engines 状态。等价 UI 的"自动交易"关 |
| `engine.set_auto_trade` | `{value}` | `{}` 只改自动执行标志，不动引擎 |
| `engine.status` | - | `{auto_trade, trading_active, server_online, accounts: [{account_id, running, server_connected}]}`；`server_online` 为后端心跳探测的 iTrader 服务器连通性（启动即测、每 10s 周期、保存配置后立即重测），与引擎是否运行无关 |
| `update.check` | `{silent?}` | `{status: "latest"\|"available"\|"none"\|"error"\|"skipped", message, version?, has_patch?}`；silent 尊重"跳过此版本" |
| `update.download` | - | `{status: "downloading"}` 进度走 `update_progress` 事件 |
| `update.cancel` | - | `{}` |
| `update.apply` | - | `{}` 停引擎→替换安装物→重启后端→`backend_stopping`；开发态报错 |
| `app.quit` | - | `{}` 停引擎→`backend_stopping`→后端退出 |

## 约定

- `accounts.save` 密码留空 = 不修改；`accounts.list` 不回传密码。
- `engine.start/stop` 与 UI 开关语义绑定（联动 auto_trade）；`engine.set_auto_trade` 用于只改标志。
- 交易语义：`trade_executed` 仅表示调仓指令已交付柜台（TargetPosTask），不是成交回报。
- 配置/账户变更在引擎运行中只生效于下次启动（后端广播 log 事件提示）。
