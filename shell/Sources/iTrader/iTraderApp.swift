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
                .onAppear { appState.start() }
        }
        .defaultSize(width: 1180, height: 760)
        .windowResizability(.contentMinSize)

        MenuBarExtra {
            EmptyView()
                .environmentObject(appState)
                .environment(\.dynamicTypeSize, .xLarge)
        } label: {
            Button {
                NSApplication.shared.windows.first?.makeKeyAndOrderFront(nil)
            } label: {
                Image(systemName: "gauge.with.dots.needle.bottom.50percent")
                    .frame(width: 18, height: 18)
            }
            .buttonStyle(.plain)
            .contextMenu {
                MenuBarView()
                    .environmentObject(appState)
                    .environment(\.dynamicTypeSize, .xLarge)
            }
        }
        .menuBarExtraStyle(.window)
    }
}

// MARK: - App Delegate to handle dock icon click
class AppDelegate: NSObject, NSApplicationDelegate {
    
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Handle application open events (like dock icon click when already running)
        NSAppleEventManager.shared().setEventHandler(
            self,
            andSelector: #selector(handleGetURLEvent(_:withReply:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL)
        )
    }
    
    @objc func handleGetURLEvent(_ event: NSAppleEventDescriptor, withReply reply: NSAppleEventDescriptor) {
        // This gets called when dock icon is clicked
        NSApp.windows.first?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
    
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        // This gets called when dock icon is clicked and app is already running
        if !flag {
            NSApp.windows.first?.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
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
    }

    @Environment(\.colorScheme) private var scheme

    // ---- 顶部栏：品牌标题 + 自动交易/主题/退出（QFrame#head）----

    private var headBar: some View {
        HStack(spacing: 12) {
            Spacer()
            AppButton(
                appState.engineStatus.tradingActive ? "自动交易：开" : "自动交易：关",
                variant: appState.engineStatus.tradingActive ? .success : .secondary
            ) {
                appState.toggleTrading(!appState.engineStatus.tradingActive)
            }
            .disabled(appState.connState != .connected)
            AppButton(dark ? "浅色" : "深色", variant: .secondary) { dark.toggle() }
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
        appState.engineStatus.accounts.contains { $0.serverConnected }
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