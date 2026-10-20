import SwiftUI
import iTraderCore
import AppKit

@main
struct iTraderApp: App {
    @StateObject private var appState = AppState()
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate

    var body: some Scene {
        WindowGroup("iTrader 智能交易系统") {
            RootView()
                .environmentObject(appState)
                .environment(\.dynamicTypeSize, .xLarge)
                .frame(minWidth: 0, minHeight: 620)
                .onAppear {
                    appState.start()
                    appDelegate.appState = appState
                }
        }
        .defaultSize(width: 1020, height: 760)
        .windowResizability(.contentMinSize)
    }
}

// MARK: - App Delegate：菜单栏图标（左键唤回主窗口 / 右键菜单）+ Dock 图标点击唤回

@MainActor
class AppDelegate: NSObject, NSApplicationDelegate {

    // RootView.onAppear 注入，"退出后端"菜单项复用 AppState 的 RPC 退出
    weak var appState: AppState?

    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        setupStatusItem()
        NSAppleEventManager.shared().setEventHandler(
            self,
            andSelector: #selector(handleGetURLEvent(_:withReply:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL)
        )
    }

    // ---- 菜单栏图标 ----
    // SwiftUI MenuBarExtra 点击图标只能 toggle 弹出内容、无法区分左右键，
    // 故改用 NSStatusItem 自行分发：左键直接显示主窗口，右键弹出原生菜单。

    private func setupStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "gauge.with.dots.needle.bottom.50percent",
                                     accessibilityDescription: "iTrader")
        // 左右键都触发同一 action，在 action 里按事件类型分发
        item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        item.button?.target = self
        item.button?.action = #selector(statusItemClicked)
        statusItem = item
    }

    @objc private func statusItemClicked() {
        let isRightClick = NSApp.currentEvent.map {
            $0.type == .rightMouseUp || $0.type == .otherMouseUp
        } ?? false
        if isRightClick {
            // 右键：临时挂上菜单再触发一次点击使其弹出，弹完立即清掉，
            // 保证左键仍走 action 而非 menu
            statusItem?.menu = buildMenu()
            statusItem?.button?.performClick(nil)
            statusItem?.menu = nil
        } else {
            showMainWindow()
        }
    }

    private func buildMenu() -> NSMenu {
        let menu = NSMenu()
        let show = NSMenuItem(title: "显示窗口", action: #selector(showMainWindowAction), keyEquivalent: "f")
        show.keyEquivalentModifierMask = [.command]
        let quitBackend = NSMenuItem(title: "退出后端", action: #selector(quitBackendAction), keyEquivalent: "q")
        quitBackend.keyEquivalentModifierMask = [.command, .option]
        let quit = NSMenuItem(title: "退出", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        quit.keyEquivalentModifierMask = [.command]
        menu.addItem(show)
        menu.addItem(.separator())
        menu.addItem(quitBackend)
        menu.addItem(quit)
        return menu
    }

    @objc private func showMainWindowAction() { showMainWindow() }

    @objc private func quitBackendAction() {
        appState?.quitBackend()
    }

    /// 唤回主窗口：最小化则还原，并置前激活。
    /// macOS 14+ 收紧后台激活：activate 只有在真实用户点击的调用栈内才生效；
    /// orderFrontRegardless 兜底保证即使激活被系统拒绝，窗口也强制排到最前可见。
    func showMainWindow() {
        // 排除 NSPanel 等辅助窗口，只找真正的业务主窗口
        let window = NSApp.windows.first { !($0 is NSPanel) && $0.canBecomeKey }
        window?.deminiaturize(nil)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        window?.orderFrontRegardless()
    }

    // ---- Dock 图标点击（已在运行时再次点击 Dock / 通过 URL 唤起）----

    @objc func handleGetURLEvent(_ event: NSAppleEventDescriptor, withReply reply: NSAppleEventDescriptor) {
        showMainWindow()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            showMainWindow()
        }
        return true
    }
}

// MARK: - 路由（侧栏顺序以此声明顺序为准）

enum AppRoute: String, CaseIterable, Identifiable {
    // 声明顺序即侧栏顺序
    case dashboard, token, sim, live, records, log, settings

    var id: String { rawValue }

    /// 侧边栏实际展示的路由。Token 管理暂时隐藏（Token 已在模拟页就地申请、查看），
    /// 页面与路由分发保留，后续需要时把 `.token` 加回即可。
    static var visibleCases: [AppRoute] { allCases.filter { $0 != .token } }

    var title: String {
        switch self {
        case .dashboard: return "仪表盘"
        case .token: return "Token 管理"
        case .log: return "系统日志"
        case .sim: return "模拟交易"
        case .live: return "实盘交易"
        case .records: return "交易记录"
        case .settings: return "设置"
        }
    }
}

// MARK: - 根布局：head 栏 + 侧边栏 + 页面 + 底部状态栏（对应 Qt MainWindow）

struct RootView: View {
    @EnvironmentObject var appState: AppState
    @State private var selection: AppRoute = .dashboard
    @State private var dark = false
    @State private var showServerHelp = false
    @State private var serverHelpHovering = false
    @State private var showAutoTradeHelp = false
    @State private var autoTradeHelpHovering = false
    @State private var autoTradeHovering = false
    /// 「发现新版本」确认框；非空即弹出
    @State private var updatePrompt: UpdatePrompt?
    /// 本次点击触发的检查是否还在等结果：只有用户主动点出来的新版本才弹确认框，
    /// 启动时的静默检查只亮红点
    @State private var awaitingUpdatePrompt = false
    /// 版本胶囊的瞬时提示（已是最新 / 更新失败），几秒后回到版本号
    @State private var versionHint: String?
    /// 瞬时提示的序号：定时清除时用它判定期间是否已有新提示
    @State private var versionHintToken = 0

    var body: some View {
        VStack(spacing: 0) {
            headBar
            HStack(spacing: 0) {
                sidebar
                page
            }
        }
        .background(AppTheme.canvas(scheme))
        .preferredColorScheme(dark ? .dark : .light)
        .sheet(isPresented: startBlockedBinding) {
            StartBlockedSheet(issues: appState.startBlockedIssues) {
                appState.startBlockedIssues = []
            }
        }
        .alert(item: $updatePrompt) { prompt in
            Alert(
                title: Text("发现新版本 \(prompt.version)"),
                message: Text(
                    "\(prompt.hasPatch ? "差量更新包" : "完整更新包")。"
                    + "确认后自动下载并安装，完成后客户端会自动重启。"
                ),
                primaryButton: .default(Text("更新")) { appState.confirmUpdate() },
                secondaryButton: .cancel(Text("取消"))
            )
        }
        .onChange(of: appState.updatePhase) { phase in
            switch phase {
            case .available(let version, let hasPatch):
                guard awaitingUpdatePrompt else { break }
                awaitingUpdatePrompt = false
                updatePrompt = UpdatePrompt(version: version, hasPatch: hasPatch)
            case .latest:
                awaitingUpdatePrompt = false
                showVersionHint("已是最新")
            case .failed:
                awaitingUpdatePrompt = false
                showVersionHint("更新失败")
            default:
                break
            }
        }
    }

    @Environment(\.colorScheme) private var scheme

    /// 启动被拦截时自动弹出问题清单；用户关闭后清空，下次点击重新判定
    private var startBlockedBinding: Binding<Bool> {
        Binding(
            get: { !appState.startBlockedIssues.isEmpty },
            set: { if !$0 { appState.startBlockedIssues = [] } }
        )
    }

    // ---- 顶部栏：品牌标题 + 自动交易/主题/退出（QFrame#head）----

    /// 启动检查期间按钮显示"检查中…"，让用户知道预检（含天勤握手）在跑
    private var startButtonTitle: String {
        if appState.startChecking { return "自动交易：检查中…" }
        return appState.engineStatus.tradingActive ? "自动交易：开" : "自动交易：关"
    }

    private var headBar: some View {
        HStack(spacing: 12) {
            versionPill
            serverStatusPill
            Spacer()
            autoTradePill
            AppButton(dark ? "浅色" : "深色", variant: .secondary) { dark.toggle() }
            // 刷新服务器品种与策略（模拟/实盘页共用），获取中置灰防重复点击
            AppButton(
                "刷新",
                variant: .secondary,
                disabled: appState.symbolsFetching || appState.strategiesFetching
            ) {
                appState.fetchSymbols()
                appState.fetchStrategies()
            }
            AppButton("退出", variant: .danger) { confirmQuit() }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 14)
        .background(AppTheme.chrome(scheme))
        .overlay(alignment: .bottom) {
            Rectangle().fill(AppTheme.hairline(scheme)).frame(height: 1)
        }
    }

    /// Qt 版退出前确认：自动交易运行中需二次确认
    private func confirmQuit() {
        if appState.engineStatus.tradingActive {
            let alert = NSAlert()
            alert.messageText = "确认退出"
            alert.informativeText = "自动交易正在运行，确定要退出吗？"
            alert.addButton(withTitle: "确定")
            alert.addButton(withTitle: "取消")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        NSApp.terminate(nil)
    }

    // ---- 侧边栏：宽度按最长导航标题自适应（QFrame#sidebar）----

    /// 侧栏宽度 = 最宽标题 + 胶囊左右内边距（24×2）+ 侧栏左右留白（14×2）。
    /// 标题固定，用 NSFont 度量一次即可；不依赖 SwiftUI 的弹性分配
    /// （否则 maxWidth: .infinity 的按钮会被 HStack 拉伸、挤占页面区）。
    private static let sidebarWidth: CGFloat = {
        let font = NSFont.systemFont(ofSize: 15)
        let textWidth = AppRoute.visibleCases
            .map { ($0.title as NSString).size(withAttributes: [.font: font]).width }
            .max() ?? 0
        // +2 吸收 SwiftUI 与 NSFont 之间的字距差异，避免最长标题被压到换行
        return ceil(textWidth) + 2 + 48 + 28
    }()

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(AppRoute.visibleCases) { route in
                SidebarNavButton(title: route.title, isSelected: selection == route) {
                    selection = route
                    // 切到模拟/实盘页时自动拉取品种（对应 Qt _switch_page 的 on_fetch_symbols）
                    if route == .sim || route == .live {
                        appState.fetchSymbols()
                        appState.fetchStrategies()
                    }
                }
            }
            Spacer()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 18)
        .frame(width: Self.sidebarWidth, alignment: .topLeading)
        .background(AppTheme.canvas(scheme))
        .overlay(alignment: .trailing) {
            Rectangle().fill(AppTheme.hairline(scheme)).frame(width: 1)
        }
    }

    @ViewBuilder
    private var page: some View {
        switch selection {
        case .dashboard: DashboardView()
        case .token: TokenManageView()
        case .log: TradingLogView()
        case .sim: SimulationView()
        case .live: AccountView()
        case .records: TradeRecordView()
        case .settings: SettingsView()
        }
    }

    // ---- 底部状态栏已移除：服务器状态以胶囊形式并入顶栏左侧 ----

    private var serverConnected: Bool {
        appState.engineStatus.serverOnline
    }

    /// 服务器状态胶囊：尺寸与「自动交易」按钮一致（高 36、圆角 18）；问号在胶囊左侧
    private var serverStatusPill: some View {
        let color = serverConnected ? AppTheme.statusGreen : AppTheme.statusRed
        return HStack(spacing: 8) {
            helpButton(
                text: "这里的服务器指策略信号服务器，地址在「设置」中配置。客户端从它获取交易品种与交易信号，并每 10 秒探测一次连通性。",
                isPresented: $showServerHelp,
                isHovering: $serverHelpHovering
            )
            Text("服务器状态：\(serverConnected ? "已连接" : "未连接")")
                .font(AppTheme.bodyFont.weight(.semibold))
                .foregroundColor(color)
        }
        .padding(.leading, 14)
        .padding(.trailing, 22)
        .frame(minHeight: 36)
        .overlay(
            RoundedRectangle(cornerRadius: 18)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
        .cornerRadius(18)
    }

    /// 版本胶囊：平时显示当前版本；点击即检查更新，发现新版本弹确认框。
    /// 下载中在胶囊底部显示进度条，此时点击即取消下载。
    private var versionPill: some View {
        Button { handleVersionPillClick() } label: {
            HStack(spacing: 6) {
                Text(versionPillText)
                    .font(AppTheme.bodyFont.weight(.semibold))
                    .foregroundColor(versionPillColor)
                    .contentShape(Rectangle())
                if appState.updateBadgeVisible {
                    Circle()
                        .fill(AppTheme.danger(scheme))
                        .frame(width: 7, height: 7)
                }
            }
            .padding(.horizontal, 14)
            .frame(minHeight: 36)
            .overlay(
                RoundedRectangle(cornerRadius: 18)
                    .stroke(AppTheme.hairline(scheme), lineWidth: 1)
            )
            // 进度条叠在胶囊底部，随外层 cornerRadius 一起被裁成圆角
            .overlay(alignment: .bottom) { downloadProgressBar }
            .cornerRadius(18)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help(versionPillTooltip)
    }

    /// 下载进度：胶囊底部一条按百分比延伸的细进度条；总量未知时不显示
    @ViewBuilder
    private var downloadProgressBar: some View {
        if case .downloading = appState.updatePhase, appState.updateProgress >= 0 {
            GeometryReader { geo in
                Rectangle()
                    .fill(AppTheme.primary(scheme))
                    .frame(
                        width: geo.size.width * CGFloat(appState.updateProgress) / 100,
                        height: geo.size.height
                    )
            }
            .frame(height: 3)
            .allowsHitTesting(false)
        }
    }

    /// 胶囊文案：更新流程各阶段优先，其次是几秒内的终态提示，最后是版本号
    private var versionPillText: String {
        switch appState.updatePhase {
        case .checking: return "检查中…"
        case .downloading:
            return appState.updateProgress >= 0 ? "取消 \(appState.updateProgress)%" : "取消下载"
        case .ready: return appState.updateInstalling ? "安装中…" : "待安装…"
        case .available: break  // 只发现未更新：保留版本号，靠红点与 tooltip 提示
        default: break
        }
        if let versionHint { return versionHint }
        return appState.backendVersion.isEmpty ? "-" : appState.backendVersion
    }

    private var versionPillColor: Color {
        switch appState.updatePhase {
        case .downloading, .ready:
            return AppTheme.warning(scheme)
        case .failed:
            return versionHint == nil ? AppTheme.dim(scheme) : AppTheme.danger(scheme)
        case .latest:
            return versionHint == nil ? AppTheme.dim(scheme) : AppTheme.success(scheme)
        default:
            return AppTheme.dim(scheme)
        }
    }

    private var versionPillTooltip: String {
        switch appState.updatePhase {
        case .checking: return "正在检查更新…"
        case .downloading: return "正在下载更新，点击取消"
        case .ready: return "更新包已下载，安装后客户端会重启"
        case .available(let version, _): return "发现新版本 \(version)，点击更新"
        default: return "点击检查更新"
        }
    }

    /// 点击版本胶囊：下载中表示取消，其余情况发起检查（已知有新版本则直接确认框）。
    /// 检查结果由 onChange(updatePhase) 接手。
    private func handleVersionPillClick() {
        switch appState.updatePhase {
        case .downloading:
            appState.cancelUpdate()
            versionHint = nil
        case .ready:
            // 下载完成即自动安装，此态通常一闪而过；若因取消竞争停在这里，
            // 点击即补上安装，否则用户没有出口
            if !appState.updateInstalling { appState.applyUpdate() }
        case .checking:
            return  // 检查进行中，忽略点击
        case .available(let version, let hasPatch):
            updatePrompt = UpdatePrompt(version: version, hasPatch: hasPatch)
        default:
            if appState.updateInstalling { return }
            versionHint = nil
            awaitingUpdatePrompt = true
            appState.checkUpdate()
        }
    }

    /// 终态提示（已是最新 / 更新失败）显示几秒后回到版本号
    private func showVersionHint(_ text: String) {
        versionHint = text
        versionHintToken += 1
        let token = versionHintToken
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) {
            guard token == versionHintToken else { return }
            versionHint = nil
        }
    }

    /// 自动交易胶囊：外观同 AppButton（开关语义色），问号放在胶囊内部右侧
    private var autoTradePill: some View {
        let active = appState.engineStatus.tradingActive
        let disabled = appState.connState != .connected || appState.startChecking
        let fg: Color = disabled
            ? (scheme == .dark ? Color(hex: 0x86868B) : Color(hex: 0xCCCCCC))
            : (active ? .white : AppTheme.primary(scheme))
        let bg: Color = disabled
            ? (active ? AppTheme.hairline(scheme) : .clear)
            : (active ? (autoTradeHovering ? AppTheme.successHover(scheme) : AppTheme.success(scheme))
                      : (autoTradeHovering ? AppTheme.canvas(scheme) : .clear))
        let border: Color = disabled
            ? (active ? .clear : AppTheme.divider(scheme))
            : (active ? .clear : (autoTradeHovering ? AppTheme.dim(scheme) : AppTheme.hairline(scheme)))

        return HStack(spacing: 8) {
            helpButton(
                text: autoTradeHelpText,
                isPresented: $showAutoTradeHelp,
                isHovering: $autoTradeHelpHovering
            )

            Button { appState.toggleTrading(!active) } label: {
                Text(startButtonTitle)
                    .font(AppTheme.bodyFont.weight(.semibold))
                    .foregroundColor(fg)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(disabled)
            .onHover { autoTradeHovering = $0 }
            .pointerCursor()
        }
        .padding(.leading, 14)
        .padding(.trailing, 22)
        .frame(minHeight: 36)
        .background(bg)
        .overlay(
            RoundedRectangle(cornerRadius: 18)
                .stroke(border, lineWidth: active || disabled ? 0 : 1)
        )
        .cornerRadius(18)
    }

    /// 与后端 _preflight_start_checks 的实际检查项逐条对应，改预检时同步这里
    private let autoTradeHelpText = """
        开启后客户端接收服务器推送的交易信号并自动下单。

        开启前会先做启动检查：服务器连通、每个启用账户的品种与策略已选、账户凭据齐全（实盘还需期货公司、资金账号与交易密码）、本地 Token 已领取、天勤账户握手通过。任一项不通过会列出全部问题并拒绝启动。

        关闭只停止接收信号与下单，已持有的仓位不会被自动平仓。运行中修改账户配置需先关闭再重新开启才生效。
        """

    /// 20x20 圆形问号按钮，点击弹出说明（样式同设置页 helpButton）
    private func helpButton(
        text: String,
        isPresented: Binding<Bool>,
        isHovering: Binding<Bool>
    ) -> some View {
        Button { isPresented.wrappedValue = true } label: {
            Text("?")
                .font(.system(size: 14, weight: .semibold))
                .foregroundColor(AppTheme.primary(scheme))
                .frame(width: 20, height: 20)
                .background(isHovering.wrappedValue ? AppTheme.sidebarHover(scheme) : .clear)
                .cornerRadius(10)
                .overlay(Circle().stroke(AppTheme.hairline(scheme), lineWidth: 1))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .onHover { isHovering.wrappedValue = $0 }
        .popover(isPresented: isPresented, arrowEdge: .bottom) {
            // 不指定前景色，由 popover 自身外观决定（同设置页 helpButton）
            Text(text)
                .font(AppTheme.dimFont)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 300, alignment: .leading)
                .padding(10)
        }
    }
}

// MARK: - 侧边栏导航按钮（SidebarButton：高 44、圆角 8、文字左对齐）
private struct SidebarNavButton: View {
    let title: String
    let isSelected: Bool
    let action: () -> Void

    @Environment(\.colorScheme) private var scheme
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(AppTheme.bodyFont)
                .foregroundColor(isSelected ? .white : AppTheme.dim(scheme))
                .padding(.leading, 24)
                .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                .background(backgroundColor)
                .cornerRadius(8)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .pointerCursor()
    }

    private var backgroundColor: Color {
        if isSelected { return AppTheme.sidebarSelected(scheme) }
        return hovering ? AppTheme.sidebarHover(scheme) : .clear
    }
}
// MARK: - 自动交易启动被拦截：问题清单弹窗

/// 启动前预检未通过时展示的问题列表。
///
/// 后端在 engine.start 里逐项检查（服务器、账户字段、token、天勤握手），
/// 全部问题一次返回，这里逐条列出而不是只显示第一项——用户通常要按提示
/// 去多个页面补配置。关掉弹窗即清空，下次点击重新预检。
private struct StartBlockedSheet: View {
    let issues: [String]
    let onClose: () -> Void

    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundColor(AppTheme.warning(scheme))
                Text("自动交易未启动")
                    .font(AppTheme.titleFont)
                    .foregroundColor(AppTheme.ink(scheme))
            }
            .padding(.horizontal, 20)
            .padding(.top, 20)
            .padding(.bottom, 12)

            Text("请先处理以下 \(issues.count) 项问题，再重新开启自动交易：")
                .font(AppTheme.bodyFont)
                .foregroundColor(AppTheme.dim(scheme))
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 20)
                .padding(.bottom, 14)

            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(Array(issues.enumerated()), id: \.offset) { index, issue in
                        HStack(alignment: .top, spacing: 10) {
                            Text("\(index + 1).")
                                .font(AppTheme.monoFont)
                                .foregroundColor(AppTheme.dim(scheme))
                                .frame(width: 22, alignment: .trailing)
                            Text(issue)
                                .font(AppTheme.bodyFont)
                                .foregroundColor(AppTheme.ink(scheme))
                                .fixedSize(horizontal: false, vertical: true)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .textSelection(.enabled)
                        }
                        .padding(.vertical, 8)
                        .padding(.horizontal, 12)
                        .background(AppTheme.soft(scheme))
                        .cornerRadius(10)
                    }
                }
                .padding(.horizontal, 20)
            }
            .frame(maxHeight: 320)

            HStack {
                Spacer()
                AppButton("好", variant: .secondary) { onClose() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(20)
        }
        .frame(width: 520)
        .background(AppTheme.canvas(scheme))
    }
}

// MARK: - 更新确认框

/// 「发现新版本」确认框的数据。`id` 让 SwiftUI 在内容变化时重建弹窗。
private struct UpdatePrompt: Identifiable {
    let id = UUID()
    let version: String
    let hasPatch: Bool
}
