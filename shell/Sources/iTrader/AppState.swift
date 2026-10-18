import Foundation
import iTraderCore
import AppKit
import SwiftUI

// MARK: - 模型（协议字段手工解析，见 src/backend/PROTOCOL.md）

struct LogLine: Identifiable {
    let id = UUID()
    let time: Date
    let level: String
    let message: String

    /// 日志配色随主题（对应 Qt viewmodels 的 log_colors）
    func color(for scheme: ColorScheme) -> Color {
        switch level {
        case "ERROR": return scheme == .dark ? Color(hex: 0xFF453A) : Color(hex: 0xC5241F)
        case "WARNING": return scheme == .dark ? Color(hex: 0xFFD60A) : Color(hex: 0xB8780A)
        case "SUCCESS": return scheme == .dark ? Color(hex: 0x30D158) : Color(hex: 0x1D9552)
        default: return scheme == .dark ? .white : Color(hex: 0x1D1D1F)
        }
    }
}

struct AccountInfo: Identifiable, Equatable {
    let id: String
    let kind: String
    let label: String
    let tqAccount: String
    let broker: String
    let tradeAccount: String
    let symbols: [String]
    let strategy: String
    let enabled: Bool
    let hasTqPassword: Bool
    let hasTradePassword: Bool

    var isSim: Bool { kind == "sim" }

    static func from(_ dict: [String: Any]) -> AccountInfo? {
        guard let id = dict["id"] as? String else { return nil }
        return AccountInfo(
            id: id,
            kind: dict["kind"] as? String ?? "live",
            label: dict["label"] as? String ?? "",
            tqAccount: dict["tq_account"] as? String ?? "",
            broker: dict["broker"] as? String ?? "",
            tradeAccount: dict["trade_account"] as? String ?? "",
            symbols: dict["symbols"] as? [String] ?? [],
            strategy: dict["strategy"] as? String ?? "",
            enabled: dict["enabled"] as? Bool ?? true,
            hasTqPassword: dict["has_tq_password"] as? Bool ?? false,
            hasTradePassword: dict["has_trade_password"] as? Bool ?? false
        )
    }
}

/// 服务器提供的交易策略（/api/strategies 目录项）
struct StrategyInfo: Identifiable, Equatable {
    let id: String
    let name: String
    let description: String
}

/// token 申请记录（服务器 /api/tokens/requests 返回项）
struct TokenRecord: Identifiable, Equatable {
    let id: String
    let description: String
    let status: String
    let createdAt: Date?
    let expiresAt: Date?
    let useCount: Int

    /// 状态中文文案（对应服务器 status 字段）
    var statusText: String {
        switch status {
        case "pending": return "待审核"
        case "approved": return "已通过"
        case "rejected": return "已拒绝"
        default: return status
        }
    }

    static func from(_ dict: [String: Any]) -> TokenRecord? {
        guard let id = dict["token_id"] as? String else { return nil }
        // JSONSerialization 把 JSON 数字解成 NSNumber，Int/Double 都经由它转换
        let created = (dict["created_at"] as? NSNumber)?.doubleValue
        let expires = (dict["expires_at"] as? NSNumber)?.doubleValue
        return TokenRecord(
            id: id,
            description: dict["description"] as? String ?? "",
            status: dict["status"] as? String ?? "pending",
            createdAt: created.map { Date(timeIntervalSince1970: $0) },
            expiresAt: expires.map { Date(timeIntervalSince1970: $0) },
            useCount: (dict["use_count"] as? NSNumber)?.intValue ?? 0
        )
    }
}

struct AccountRuntime: Identifiable {
    let id: String
    var running: Bool
    var serverConnected: Bool
}

struct EngineStatus {
    var autoTrade = false
    var tradingActive = false
    /// iTrader 服务器连通性（后端心跳常驻探测，与引擎是否运行无关）
    var serverOnline = false
    var accounts: [AccountRuntime] = []

    static func from(_ dict: [String: Any]) -> EngineStatus {
        var s = EngineStatus()
        s.autoTrade = dict["auto_trade"] as? Bool ?? false
        s.tradingActive = dict["trading_active"] as? Bool ?? false
        s.serverOnline = dict["server_online"] as? Bool ?? false
        s.accounts = (dict["accounts"] as? [[String: Any]] ?? []).compactMap { item in
            guard let id = item["account_id"] as? String else { return nil }
            return AccountRuntime(
                id: id,
                running: item["running"] as? Bool ?? false,
                serverConnected: item["server_connected"] as? Bool ?? false
            )
        }
        return s
    }
}

struct ConfigInfo: Equatable {
    var serverUrl = ""
    var proxyUrl = ""
    var autoCheckUpdate = true

    static func from(_ dict: [String: Any]) -> ConfigInfo {
        var c = ConfigInfo()
        c.serverUrl = dict["server_url"] as? String ?? ""
        c.proxyUrl = dict["proxy_url"] as? String ?? ""
        c.autoCheckUpdate = dict["auto_check_update"] as? Bool ?? true
        return c
    }
}

enum ConnState {
    case starting
    case connected
    case disconnected
    case stopped  // 后端主动退出（quit / update.apply）
}

enum UpdatePhase: Equatable {
    case idle
    case checking
    case available(version: String, hasPatch: Bool)
    case latest(String)
    case downloading
    case ready(kind: String)
    case failed(String)
}

// MARK: - AppState

@MainActor
final class AppState: ObservableObject {
    @Published var connState: ConnState = .starting
    @Published var backendVersion = ""
    @Published var logs: [LogLine] = []
    @Published var engineStatus = EngineStatus()
    @Published var accounts: [AccountInfo] = []
    @Published var config = ConfigInfo()
    @Published var serverSymbols: [String] = []
    /// 品种中文名映射（symbol -> name），来自服务器品种库；无名的品种不在表中
    @Published var serverSymbolNames: [String: String] = [:]
    @Published var symbolsMessage = ""
    @Published var symbolsFetching = false
    /// 服务器提供的交易策略（模拟/实盘页第 5 步单选）
    @Published var serverStrategies: [StrategyInfo] = []
    @Published var strategiesMessage = ""
    @Published var strategiesFetching = false
    @Published var tokenStatus = ""
    /// token 申请记录（服务器返回，倒序）
    @Published var tokenRecords: [TokenRecord] = []
    @Published var updatePhase = UpdatePhase.idle
    @Published var lastActionError = ""
    /// 自动交易启动被预检拦截的问题列表；非空时顶栏弹出清单，用户确认后清空
    @Published var startBlockedIssues: [String] = []
    /// 启动检查进行中（顶栏按钮置灰防重复点击）
    @Published var startChecking = false

    private var backend: BackendProcess?
    private var client: RpcClient?
    private var started = false
    private var signalSources: [DispatchSourceSignal] = []

    // -------- 启动 / 重连 --------

    func start() {
        guard !started else { return }
        started = true
        installExitHooks()
        Task { await connectLoop() }
    }

    /// 壳退出时终止后端，避免孤儿后端常驻（后端收到 SIGINT 走优雅退出）。
    /// AppKit 优雅退出走 willTerminateNotification；SIGTERM/SIGINT 不经过它，
    /// 需要单独接管（否则 pkill/kill 直接杀壳，后端成孤儿）。
    private func installExitHooks() {
        NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.backend?.terminate()
            }
        }
        for sig in [SIGTERM, SIGINT] {
            signal(sig, SIG_IGN)  // 屏蔽默认终止行为，交给 DispatchSource
            let source = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            source.setEventHandler { [weak self] in
                self?.backend?.terminate()
                exit(0)
            }
            source.resume()
            signalSources.append(source)
        }
    }

    private func connectLoop() async {
        while !Task.isCancelled {
            do {
                let proc = try backend ?? {
                    let p = try BackendProcess()
                    p.onExit = { [weak self] in
                        Task { @MainActor in self?.handleBackendExit() }
                    }
                    backend = p
                    return p
                }()
                let info = try await proc.connectOrStart()
                let rpc = RpcClient(info: info)
                await rpc.setHandlers(
                    event: { [weak self] name, data in
                        Task { @MainActor in self?.handleEvent(name: name, data: data) }
                    },
                    disconnect: { [weak self] in
                        Task { @MainActor in self?.handleDisconnect() }
                    }
                )
                try await rpc.connect()
                client = rpc
                connState = .connected
                appendLog(level: "SUCCESS", message: "已连接后端 v\(info.version) (pid \(info.pid))")
                backendVersion = info.version
                try await refreshAll()
                return
            } catch {
                connState = .disconnected
                appendLog(level: "ERROR", message: "连接后端失败: \(error.localizedDescription)，3 秒后重试")
                try? await Task.sleep(nanoseconds: 3_000_000_000)
            }
        }
    }

    private func handleDisconnect() {
        // 后端退出（quit/update）或崩溃：区分展示，连接循环在 quit 场景外继续重连
        client = nil
        guard connState != .stopped else { return }
        connState = .disconnected
        appendLog(level: "WARNING", message: "与后端断开，正在重连…")
        Task { await connectLoop() }
    }

    private func handleBackendExit() {
        client = nil
        guard connState != .stopped else { return }
        connState = .disconnected
        appendLog(level: "ERROR", message: "后端进程已退出，正在重启…")
        Task { await connectLoop() }
    }

    // -------- RPC 封装 --------

    func call(method: String, params: [String: Any] = [:]) async throws -> [String: Any] {
        guard let rpc = client else { throw RpcError.connectionClosed }
        return try await rpc.call(method: method, params: params)
    }

    func runAction(method: String, params: [String: Any] = [:]) async {
        do {
            lastActionError = ""
            _ = try await call(method: method, params: params)
        } catch {
            lastActionError = error.localizedDescription
            appendLog(level: "ERROR", message: "\(method) 失败: \(error.localizedDescription)")
        }
    }

    private func refreshAll() async {
        if let status = try? await call(method: "app.status") {
            backendVersion = status["version"] as? String ?? backendVersion
            if let cfg = status["config"] as? [String: Any] { config = ConfigInfo.from(cfg) }
            accounts = (status["accounts"] as? [[String: Any]] ?? []).compactMap(AccountInfo.from)
            if let engines = status["engines"] as? [String: Any] { engineStatus = EngineStatus.from(engines) }
        }
    }

    // -------- 事件处理 --------

    private func handleEvent(name: String, data: [String: Any]) {
        switch name {
        case "log":
            appendLog(
                level: data["level"] as? String ?? "INFO",
                message: data["message"] as? String ?? ""
            )
        case "status":
            engineStatus.tradingActive = data["trading_active"] as? Bool ?? engineStatus.tradingActive
            // 心跳广播只带全局 server_online（无 account_id）；引擎广播两者都带
            if let online = data["server_online"] as? Bool {
                engineStatus.serverOnline = online
            }
            if let accountID = data["account_id"] as? String,
               let idx = engineStatus.accounts.firstIndex(where: { $0.id == accountID }) {
                engineStatus.accounts[idx].serverConnected = data["server_connected"] as? Bool ?? false
            }
        case "trading_started", "trading_stopped":
            Task { await refreshEngineOnly() }
        case "config_changed":
            Task { await refreshAll() }
        case "token_status":
            applyTokenStatus(data)
        case "signal_received":
            if let signal = data["signal"] as? [String: Any] {
                appendLog(level: "INFO", message: "收到信号: \(signal["symbol"] ?? "?") 目标仓位 \(signal["position"] ?? "?")")
            }
        case "trade_executed":
            appendLog(level: "SUCCESS", message: "交易已执行: \(data["symbol"] ?? "?") 仓位 \(data["target_position"] ?? "?")")
        case "trade_failed":
            appendLog(level: "ERROR", message: "交易失败: \(data["symbol"] ?? "?") \(data["error"] ?? "")")
        case "update_progress":
            break  // 第一版不做进度条，完成/失败事件驱动状态
        case "update_ready":
            updatePhase = .ready(kind: data["kind"] as? String ?? "")
        case "update_failed":
            updatePhase = .failed(data["message"] as? String ?? "下载失败")
        case "update_cancelled":
            updatePhase = .idle
        case "backend_stopping":
            connState = .stopped
            appendLog(level: "WARNING", message: "后端正在退出（\(data["reason"] ?? "")）")
        default:
            break
        }
    }

    private func refreshEngineOnly() async {
        if let engines = try? await call(method: "engine.status") {
            engineStatus = EngineStatus.from(engines)
        }
    }

    private func appendLog(level: String, message: String) {
        logs.append(LogLine(time: Date(), level: level, message: message))
        if logs.count > 500 { logs.removeFirst(logs.count - 500) }
    }

    // -------- 业务动作 --------

    /// 自动交易开关。开启时后端会先做启动前预检，不通过则返回问题列表：
    /// 这里不写日志了事，而是发布到 startBlockedIssues 由顶栏弹窗展示并拦住。
    func toggleTrading(_ on: Bool) {
        guard !startChecking else { return }
        startChecking = true
        startBlockedIssues = []
        Task {
            defer { startChecking = false }
            do {
                lastActionError = ""
                _ = try await call(method: on ? "engine.start" : "engine.stop")
            } catch let error as RpcError {
                let issues = error.issues
                if !issues.isEmpty {
                    startBlockedIssues = issues
                    appendLog(level: "WARNING", message: "自动交易未启动：共 \(issues.count) 项检查未通过")
                } else {
                    lastActionError = error.localizedDescription
                    appendLog(level: "ERROR", message: "engine.start 失败: \(error.localizedDescription)")
                }
            } catch {
                lastActionError = error.localizedDescription
                appendLog(level: "ERROR", message: "engine.start 失败: \(error.localizedDescription)")
            }
            await refreshEngineOnly()
        }
    }

    func setAutoTrade(_ value: Bool) {
        Task {
            await runAction(method: "engine.set_auto_trade", params: ["value": value])
        }
    }

    func saveConfig(serverUrl: String?, proxyUrl: String?, autoCheckUpdate: Bool? = nil) {
        var params: [String: Any] = [:]
        if let serverUrl { params["server_url"] = serverUrl }
        if let proxyUrl { params["proxy_url"] = proxyUrl }
        if let autoCheckUpdate { params["auto_check_update"] = autoCheckUpdate }
        Task { await runAction(method: "config.save", params: params) }
    }

    func fetchSymbols() {
        guard !symbolsFetching else { return }
        symbolsFetching = true
        Task {
            defer { symbolsFetching = false }
            do {
                lastActionError = ""
                let resp = try await call(method: "symbols.fetch")
                var symbols: [String] = []
                var names: [String: String] = [:]
                if let list = resp["symbols"] as? [[String: Any]] {
                    for item in list {
                        let symbol = item["symbol"] as? String ?? ""
                        guard !symbol.isEmpty else { continue }
                        symbols.append(symbol)
                        if let name = item["name"] as? String, !name.isEmpty {
                            names[symbol] = name
                        }
                    }
                } else if let list = resp["symbols"] as? [String] {
                    symbols = list  // 旧版后端：纯代码列表
                }
                serverSymbols = symbols
                serverSymbolNames = names
                symbolsMessage = resp["message"] as? String ?? ""
            } catch {
                symbolsMessage = "获取失败: \(error.localizedDescription)"
            }
        }
    }

    func fetchStrategies() {
        guard !strategiesFetching else { return }
        strategiesFetching = true
        Task {
            defer { strategiesFetching = false }
            do {
                lastActionError = ""
                let resp = try await call(method: "strategies.fetch")
                let list = (resp["strategies"] as? [[String: Any]] ?? []).compactMap { item -> StrategyInfo? in
                    guard let id = item["id"] as? String, !id.isEmpty else { return nil }
                    return StrategyInfo(
                        id: id,
                        name: item["name"] as? String ?? "",
                        description: item["description"] as? String ?? ""
                    )
                }
                serverStrategies = list
                strategiesMessage = resp["message"] as? String ?? ""
            } catch {
                serverStrategies = []
                strategiesMessage = "获取失败: \(error.localizedDescription)"
            }
        }
    }

    /// 品种显示文本：有中文名时「中文｜代码」，否则纯代码（选中/保存仍用代码本身）
    func displaySymbol(_ symbol: String) -> String {
        if let name = serverSymbolNames[symbol], !name.isEmpty {
            return "\(name)｜\(symbol)"
        }
        return symbol
    }

    func testTqAuth(account: String, password: String, accountId: String? = nil) async -> String {
        do {
            var params: [String: Any] = ["account": account, "password": password]
            if let accountId { params["account_id"] = accountId }
            let resp = try await call(
                method: "accounts.test_tq_auth",
                params: params
            )
            return resp["message"] as? String ?? "连接成功"
        } catch {
            return "连接失败: \(error.localizedDescription)"
        }
    }

    func getAccountPassword(accountId: String? = nil, kind: String? = nil, field: String = "tq_password") async -> String {
        do {
            var params: [String: Any] = ["field": field]
            if let accountId { params["account_id"] = accountId }
            if let kind { params["kind"] = kind }
            let resp = try await call(method: "accounts.get_password", params: params)
            return resp["password"] as? String ?? ""
        } catch {
            return ""
        }
    }

    func testServer(url: String?) async -> String {
        do {
            var params: [String: Any] = [:]
            if let url { params["server_url"] = url }
            let resp = try await call(method: "server.test", params: params)
            return resp["message"] as? String ?? "连接成功"
        } catch {
            return "连接失败: \(error.localizedDescription)"
        }
    }

    func refreshTokenStatus() {
        Task {
            if let resp = try? await call(method: "tokens.status") {
                applyTokenStatus(resp)
            }
        }
    }

    /// 后端 tokens.status / token_status 事件共用：更新状态文案与申请记录
    private func applyTokenStatus(_ data: [String: Any]) {
        tokenStatus = data["status"] as? String ?? ""
        tokenRecords = (data["records"] as? [[String: Any]] ?? []).compactMap(TokenRecord.from)
    }

    func requestToken(description: String) {
        Task {
            if let resp = try? await call(method: "tokens.request", params: ["description": description]) {
                tokenStatus = resp["status"] as? String ?? ""
            }
        }
    }

    func checkUpdate() {
        Task {
            updatePhase = .checking
            do {
                let resp = try await call(method: "update.check")
                switch resp["status"] as? String {
                case "available":
                    updatePhase = .available(
                        version: resp["version"] as? String ?? "",
                        hasPatch: resp["has_patch"] as? Bool ?? false
                    )
                case "latest":
                    updatePhase = .latest(resp["message"] as? String ?? "已是最新版本")
                default:
                    updatePhase = .failed(resp["message"] as? String ?? "未找到可用更新")
                }
            } catch {
                updatePhase = .failed(error.localizedDescription)
            }
        }
    }

    func downloadUpdate() {
        Task {
            do {
                _ = try await call(method: "update.download")
                updatePhase = .downloading
            } catch {
                updatePhase = .failed(error.localizedDescription)
            }
        }
    }

    func cancelUpdate() {
        Task { await runAction(method: "update.cancel") }
    }

    func applyUpdate() {
        Task { await runAction(method: "update.apply") }
    }

    func quitBackend() {
        connState = .stopped
        Task { await runAction(method: "app.quit") }
    }
}
