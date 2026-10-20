import Foundation
import CryptoKit
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
    /// 已存密码的明文长度，仅用于按真实长度绘制掩码圆点（密码本身不回传）
    let tqPasswordLength: Int
    let tradePasswordLength: Int

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
            hasTradePassword: dict["has_trade_password"] as? Bool ?? false,
            tqPasswordLength: dict["tq_password_length"] as? Int ?? 0,
            tradePasswordLength: dict["trade_password_length"] as? Int ?? 0
        )
    }
}

/// 服务器提供的交易策略（/api/strategies 目录项）
struct StrategyInfo: Identifiable, Equatable {
    let id: String
    let name: String
    let description: String
}

/// 「测试连接」验证通过的一组快期凭据：手机号 + 密码摘要。
/// 密码只留摘要，避免为了一个 UI 状态多存一份明文。
struct VerifiedTqCredentials: Equatable {
    let account: String
    let passwordDigest: String

    /// 落盘位置：重启后模拟页步骤 3 胶囊仍要显示为已验证。
    /// 手机号与摘要存在同一个键里（`手机号|摘要`），写入是一次原子的，
    /// 不会出现「号码更新了、摘要还是旧的」这种跨越两次写的中间态
    private static let defaultsKey = "sim.verifiedTqCredentials"

    static func digest(_ password: String) -> String {
        // 只用于本地比较与记忆验证结果，SHA256 足够；不涉及任何传输
        SHA256.hash(data: Data(password.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    /// 密码为空表示这次验证沿用的是已存密码
    init(account: String, password: String) {
        self.account = account.trimmingCharacters(in: .whitespaces)
        passwordDigest = Self.digest(password)
    }

    private init(account: String, passwordDigest: String) {
        self.account = account
        self.passwordDigest = passwordDigest
    }

    func matches(account: String, password: String) -> Bool {
        self.account == account.trimmingCharacters(in: .whitespaces)
            && passwordDigest == Self.digest(password)
    }

    /// 恢复上次验证通过的记录；只回读手机号与摘要，没有任何明文。
    /// 手机号为空即视为未验证（摘要空串是合法值，表示沿用已存密码）
    static func loadFromDefaults() -> VerifiedTqCredentials? {
        guard let raw = UserDefaults.standard.string(forKey: defaultsKey) else { return nil }
        let parts = raw.split(separator: "|", maxSplits: 1, omittingEmptySubsequences: false)
        guard parts.count == 2, !parts[0].isEmpty else { return nil }
        return VerifiedTqCredentials(account: String(parts[0]), passwordDigest: String(parts[1]))
    }

    func saveToDefaults() {
        UserDefaults.standard.set("\(account)|\(passwordDigest)", forKey: Self.defaultsKey)
    }

    static func clearDefaults() {
        UserDefaults.standard.removeObject(forKey: defaultsKey)
    }
}

/// token 申请记录（服务器 /api/tokens/requests 返回项）
struct TokenRecord: Identifiable, Equatable {
    let id: String
    let description: String
    let status: String
    let createdAt: Date?
    let expiresAt: Date?
    let useCount: Int
    /// 服务器是否还留有可领取的明文（首次领取后即为 false）
    let claimable: Bool

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
            useCount: (dict["use_count"] as? NSNumber)?.intValue ?? 0,
            claimable: dict["claimable"] as? Bool ?? false
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
    /// 服务器提供的交易策略（模拟页第 7 步、实盘页单选）
    @Published var serverStrategies: [StrategyInfo] = []
    @Published var strategiesMessage = ""
    @Published var strategiesFetching = false
    @Published var tokenStatus = ""
    /// token 申请记录（服务器返回，倒序）
    @Published var tokenRecords: [TokenRecord] = []
    /// 当前生效的 token 来自哪条申请记录（nil 表示本地无可用 token）
    @Published var activeTokenId: String?
    /// 本地缓存过明文、可切换回去的记录 id
    @Published var cachedTokenIds: Set<String> = []
    @Published var updatePhase = UpdatePhase.idle
    /// 下载进度百分比；-1 表示总大小未知
    @Published var updateProgress = -1
    /// 已下载更新包的类型（"patch" 差量 / "full" 完整）；安装完成后据此决定重连还是退出
    @Published private(set) var pendingUpdateMode = ""
    /// 正在安装（update.apply 已发出，等后端替换文件并退出）：弹窗据此区分
    /// 「已下载待安装」和「安装中」
    @Published private(set) var updateInstalling = false
    @Published var lastActionError = ""
    /// 自动交易启动被预检拦截的问题列表；非空时顶栏弹出清单，用户确认后清空
    @Published var startBlockedIssues: [String] = []
    /// 启动检查进行中（顶栏按钮置灰防重复点击）
    @Published var startChecking = false
    /// 「测试连接」验证通过的那组快期凭据（手机号 + 密码摘要），跨页面、跨重启保留。
    /// 只存摘要不存明文：这里只用于让步骤 3 胶囊显示绿色，不参与任何登录
    @Published private(set) var verifiedTqCredentials: VerifiedTqCredentials? =
        VerifiedTqCredentials.loadFromDefaults()

    private var backend: BackendProcess?
    private var client: RpcClient?
    private var started = false
    private var signalSources: [DispatchSourceSignal] = []
    /// 进行中的连接循环：断开事件由 RPC 断链与进程退出两处触发，去重避免并发多份循环
    private var reconnectTask: Task<Void, Never>?
    /// 本次启动是否已做过静默更新检查（顶栏角标只在启动时探一次）
    private var didAutoCheckUpdate = false
    /// 弹窗里已确认更新：下载完成后自动安装并重启
    private var autoApplyUpdate = false
    /// 差量更新后的重连等待窗口：期间忽略断链/进程退出触发的重连，
    /// 否则壳会抢在 updater 拉起新后端前自己 spawn 一个，两个后端互相拒绝
    private var waitingForUpdateBackend = false
    /// 当前连着的后端进程 pid：差量更新后据此识别「新后端已接管」
    private var connectedBackendPid: Int32?

    // -------- 启动 / 重连 --------

    func start() {
        guard !started else { return }
        started = true
        installExitHooks()
        reconnect()
    }

    /// 触发一次重连（已有的连接循环未结束时不重复启动）
    private func reconnect() {
        guard reconnectTask == nil else { return }
        reconnectTask = Task {
            await connectLoop()
            reconnectTask = nil
        }
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
                connectedBackendPid = Int32(info.pid)
                appendLog(level: "SUCCESS", message: "已连接后端 \(info.version) (pid \(info.pid))")
                backendVersion = info.version
                // 差量更新安装完成后的首次连接：暂存包已生效，状态回到 idle
                if case .ready = updatePhase {
                    updatePhase = .idle
                    updateProgress = -1
                    pendingUpdateMode = ""
                    updateInstalling = false
                }
                try await refreshAll()
                checkUpdateOnConnect()
                return
            } catch {
                connState = .disconnected
                appendLog(level: "ERROR", message: "连接后端失败: \(error.localizedDescription)，3 秒后重试")
                try? await Task.sleep(nanoseconds: 3_000_000_000)
            }
        }
    }

    /// 差量更新安装后：后端由 updater 重新拉起，等它写出新的 backend.json 再重连。
    /// 不能按固定时长等，也不能立刻重连——旧后端退出时会删掉 backend.json，
    /// 壳若抢先调 connectOrStart 会自己 spawn 一个后端，与 updater 拉起的那个互相拒绝。
    private func reconnectAfterUpdate() {
        waitingForUpdateBackend = true
        let oldPid = connectedBackendPid
        Task {
            // 最多等 30 秒；超时仍重连，由 connectOrStart 兜底 spawn
            for _ in 0..<150 {
                if let url = backend?.infoURL,
                   let info = BackendProcess.readInfo(from: url),
                   let pid = Int32(exactly: info.pid),
                   pid != oldPid,
                   BackendProcess.isPidAlive(info.pid) {
                    break
                }
                try? await Task.sleep(nanoseconds: 200_000_000)
            }
            waitingForUpdateBackend = false
            reconnect()
        }
    }

    private func handleDisconnect() {
        // 后端退出（quit/update）或崩溃：区分展示，连接循环在 quit 场景外继续重连
        client = nil
        guard connState != .stopped else { return }
        connState = .disconnected
        guard !waitingForUpdateBackend else { return }
        appendLog(level: "WARNING", message: "与后端断开，正在重连…")
        reconnect()
    }

    private func handleBackendExit() {
        client = nil
        guard connState != .stopped else { return }
        connState = .disconnected
        guard !waitingForUpdateBackend else { return }
        appendLog(level: "ERROR", message: "后端进程已退出，正在重启…")
        reconnect()
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
                appendLog(level: "INFO", message: "收到信号: \(signal["symbol"] ?? "?") 目标仓位 \(signal["target_position"] ?? "?")")
            }
        case "trade_executed":
            appendLog(level: "SUCCESS", message: "交易已执行: \(data["symbol"] ?? "?") 仓位 \(data["target_position"] ?? "?")")
        case "trade_failed":
            appendLog(level: "ERROR", message: "交易失败: \(data["symbol"] ?? "?") \(data["error"] ?? "")")
        case "update_progress":
            updateProgress = data["pct"] as? Int ?? -1
        case "update_ready":
            // mode 是后加的字段：旧版后端只发中文 kind，按文案回退判断，
            // 否则差量更新会被误当成完整更新而直接退出壳
            let kind = data["kind"] as? String ?? ""
            pendingUpdateMode = (data["mode"] as? String)
                ?? (kind.contains("差量") ? "patch" : "full")
            updatePhase = .ready(kind: kind)
            updateProgress = -1
            // 用户确认过「更新」：下载完直接装，不再要求点第二次
            if autoApplyUpdate {
                autoApplyUpdate = false
                applyUpdate()
            }
        case "update_failed":
            autoApplyUpdate = false
            pendingUpdateMode = ""
            updateProgress = -1
            updatePhase = .failed(data["message"] as? String ?? "下载失败")
        case "update_cancelled":
            autoApplyUpdate = false
            pendingUpdateMode = ""
            updateProgress = -1
            updatePhase = .idle
        case "backend_stopping":
            appendLog(level: "WARNING", message: "后端正在退出（\(data["reason"] ?? "")）")
            if data["reason"] as? String == "update" && pendingUpdateMode == "patch" {
                // 差量更新只替换 src，后端由 updater 重新拉起：壳留在前台等重连
                client = nil
                connState = .disconnected
                appendLog(level: "INFO", message: "差量更新已安装，等待后端重启后重连")
                reconnectAfterUpdate()
            } else {
                // 完整更新会启动新的壳进程，旧壳随后退出（避免两个壳争同一后端）
                connState = .stopped
                pendingUpdateMode = ""
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                    NSApp.terminate(nil)
                }
            }
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

    /// 品种显示文本：有中文名时「代码｜中文」，否则纯代码（选中/保存仍用代码本身）
    func displaySymbol(_ symbol: String) -> String {
        if let name = serverSymbolNames[symbol], !name.isEmpty {
            return "\(symbol)｜\(name)"
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
            // 验证通过即记下这组凭据：模拟页步骤 3 胶囊据此显示绿色，
            // 切页面回来、重启之后仍然有效；凭据被改动则自动失配
            setVerifiedTqCredentials(VerifiedTqCredentials(account: account, password: password))
            return resp["message"] as? String ?? "连接成功"
        } catch {
            setVerifiedTqCredentials(nil)
            return "连接失败: \(error.localizedDescription)"
        }
    }

    /// 验证结果的唯一写入口：内存与落盘保持一致，
    /// 调用的地方不会出现「界面绿了但重启后丢失」的情况
    private func setVerifiedTqCredentials(_ credentials: VerifiedTqCredentials?) {
        verifiedTqCredentials = credentials
        if let credentials {
            credentials.saveToDefaults()
        } else {
            VerifiedTqCredentials.clearDefaults()
        }
    }

    /// 新密码落库、输入框随之清空后调用：落库的正是刚验证过的那份密码时，
    /// 把验证记录改写成「沿用已存密码」的形态（摘要为空串），绿色继续有效；
    /// 否则凭据已经变了，清掉验证记录
    func settleVerifiedTqCredentials(afterSavingPassword password: String, for account: String) {
        guard let verified = verifiedTqCredentials else { return }
        setVerifiedTqCredentials(
            verified.matches(account: account, password: password)
                ? VerifiedTqCredentials(account: account, password: "")
                : nil
        )
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
        activeTokenId = data["active_token_id"] as? String
        cachedTokenIds = Set(data["cached_token_ids"] as? [String] ?? [])
    }

    /// 该记录是否在本地留有明文缓存（后端列表的 claimable 只管服务器侧）
    func hasCachedToken(_ tokenId: String) -> Bool {
        cachedTokenIds.contains(tokenId)
    }
    func requestToken(description: String) {
        Task { _ = await requestTokenResult(description: description) }
    }

    /// 提交 Token 申请并返回回执文案（模拟页第 5 步就地显示结果，不再弹窗）
    func requestTokenResult(description: String) async -> String {
        do {
            let resp = try await call(method: "tokens.request", params: ["description": description])
            let status = resp["status"] as? String ?? "已提交申请"
            tokenStatus = status
            return status
        } catch {
            return "申请失败: \(error.localizedDescription)"
        }
    }

    /// 删除一条申请记录；成功返回 nil，失败返回错误文案供页面弹窗展示
    func deleteTokenRecord(tokenId: String) async -> String? {
        do {
            let resp = try await call(method: "tokens.delete", params: ["token_id": tokenId])
            applyTokenStatus(resp)
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    /// 切换当前使用的 Token；成功返回 nil，失败返回错误文案
    func switchToken(tokenId: String) async -> String? {
        do {
            let resp = try await call(method: "tokens.switch", params: ["token_id": tokenId])
            applyTokenStatus(resp)
            await refreshEngineOnly()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    // -------- 自动更新 --------

    /// 顶栏版本按钮是否有新版本角标（仅 available 态；下载/就绪阶段已在弹窗内）
    var updateBadgeVisible: Bool {
        if case .available = updatePhase { return true }
        return false
    }

    /// 连接成功后的静默探活：每次启动只做一次，尊重「启动时自动检查更新」开关。
    /// 失败不打扰用户——顶栏点开版本弹窗仍可手动检查。
    private func checkUpdateOnConnect() {
        guard !didAutoCheckUpdate else { return }
        didAutoCheckUpdate = true
        guard config.autoCheckUpdate else { return }
        Task {
            guard
                let resp = try? await call(method: "update.check", params: ["silent": true]),
                resp["status"] as? String == "available"
            else { return }
            updatePhase = .available(
                version: resp["version"] as? String ?? "",
                hasPatch: resp["has_patch"] as? Bool ?? false
            )
        }
    }

    /// 手动检查；`autoApply` 为 true 表示用户在版本弹窗里点了「确认更新」，
    /// 检查到新版本后直接进入下载并在完成后安装重启。
    func checkUpdate(autoApply: Bool = false) {
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
                    if autoApply { startDownload(autoApply: true) }
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

    func downloadUpdate(autoApply: Bool = false) {
        startDownload(autoApply: autoApply)
    }

    /// 版本弹窗里点了「确认更新」：有可用版本就直接下载（下载完自动安装），
    /// 尚未检查过则先检查再走同一条路径。
    func confirmUpdate() {
        if case .available = updatePhase {
            startDownload(autoApply: true)
        } else {
            checkUpdate(autoApply: true)
        }
    }

    private func startDownload(autoApply: Bool) {
        autoApplyUpdate = autoApply
        Task {
            do {
                updateProgress = -1
                _ = try await call(method: "update.download")
                updatePhase = .downloading
            } catch {
                autoApplyUpdate = false
                updatePhase = .failed(error.localizedDescription)
            }
        }
    }

    func cancelUpdate() {
        autoApplyUpdate = false
        updateProgress = -1
        Task { await runAction(method: "update.cancel") }
        updatePhase = .idle
    }

    func applyUpdate() {
        updateInstalling = true
        Task {
            do {
                _ = try await call(method: "update.apply")
            } catch {
                // 安装失败（如 Windows 目录被占用）要把原因回给弹窗，
                // 否则用户只看到界面停在「正在安装」
                autoApplyUpdate = false
                updateInstalling = false
                updatePhase = .failed("安装更新失败：\(error.localizedDescription)")
            }
        }
    }

    func quitBackend() {
        connState = .stopped
        Task { await runAction(method: "app.quit") }
    }
}
