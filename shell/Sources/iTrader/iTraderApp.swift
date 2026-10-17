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
                .frame(minWidth: 960, minHeight: 620)
                .onAppear {
                    appState.start()
                    appDelegate.appState = appState
                }
        }
        .defaultSize(width: 1180, height: 760)
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

// MARK: - 路由（与 Qt 版侧边栏顺序一致）

enum AppRoute: String, CaseIterable, Identifiable {
    case dashboard, token, log, sim, live, settings

    var id: String { rawValue }

    var title: String {
        switch self {
        case .dashboard: return "仪表盘"
        case .token: return "Token 管理"
        case .log: return "交易日志"
        case .sim: return "模拟交易"
        case .live: return "实盘交易"
        case .settings: return "设置"
        }
    }
}

// MARK: - 根布局：head 栏 + 侧边栏 + 页面 + 底部状态栏（对应 Qt MainWindow）

struct RootView: View {
    @EnvironmentObject var appState: AppState
    @State private var selection: AppRoute = .dashboard
    @State private var dark = false

    var body: some View {
        VStack(spacing: 0) {
            headBar
            HStack(spacing: 0) {
                sidebar
                page
            }
            statusBar
        }
        .background(AppTheme.canvas(scheme))
        .preferredColorScheme(dark ? .dark : .light)
        .sheet(isPresented: startBlockedBinding) {
            StartBlockedSheet(issues: appState.startBlockedIssues) {
                appState.startBlockedIssues = []
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
            Spacer()
            AppButton(
                startButtonTitle,
                variant: appState.engineStatus.tradingActive ? .success : .secondary
            ) {
                appState.toggleTrading(!appState.engineStatus.tradingActive)
            }
            .disabled(appState.connState != .connected || appState.startChecking)
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

    // ---- 侧边栏：240 宽纯文字导航（QFrame#sidebar）----

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(AppRoute.allCases) { route in
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
        .frame(width: 240, alignment: .topLeading)
        .background(AppTheme.canvas(scheme))
        .overlay(alignment: .trailing) {
            Rectangle().fill(AppTheme.hairline(scheme)).frame(width: 1)
        }
    }

    @ViewBuilder
    private var page: some View {
        switch selection {
        case .dashboard: DashboardView(selection: $selection)
        case .token: TokenManageView()
        case .log: TradingLogView()
        case .sim: SimulationView()
        case .live: AccountView()
        case .settings: SettingsView()
        }
    }

    // ---- 底部状态栏：交易状态 + 服务器/交易指示器（QStatusBar）----

    private var tradingStatusText: String {
        let running = appState.engineStatus.accounts.filter(\.running).count
        return running > 0 ? "运行中 (\(running))" : "已停止"
    }

    private var serverConnected: Bool {
        appState.engineStatus.serverOnline
    }

    private var statusBar: some View {
        HStack(spacing: 0) {
            Text("交易状态: \(tradingStatusText)")
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .padding(.leading, 10)
            Spacer()
            statusIndicator(dot: serverConnected ? AppTheme.statusGreen : AppTheme.statusRed,
                            text: "服务器: \(serverConnected ? "已连接" : "未连接")")
            Spacer().frame(width: 24)
            statusIndicator(dot: appState.engineStatus.tradingActive ? AppTheme.statusGreen : AppTheme.statusRed,
                            text: "交易: \(tradingStatusText)")
        }
        .padding(.trailing, 10)
        .padding(.vertical, 6)
        .background(AppTheme.chrome(scheme))
        .overlay(alignment: .top) {
            Rectangle().fill(AppTheme.divider(scheme)).frame(height: 1)
        }
    }

    private func statusIndicator(dot: Color, text: String) -> some View {
        HStack(spacing: 6) {
            Circle().fill(dot).frame(width: 6, height: 6)
            Text(text)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
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
                .foregroundColor(isSelected ? AppTheme.ink(scheme) : AppTheme.dim(scheme))
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
