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
    // 服务器地址已并入顶栏服务器弹窗，「设置」页只保留代理配置
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
    /// 服务器信息弹窗（点击顶栏服务器状态胶囊）
    @State private var showServerInfo = false
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
    /// 服务器状态胶囊的悬停态（胶囊本身即弹窗入口，不再单独放问号）
    @State private var serverPillHovering = false

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
        .sheet(isPresented: $showServerInfo) {
            ServerInfoSheet(appState: appState) {
                showServerInfo = false
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

    /// 启动检查期间按钮显示当前阶段文案（如「自动交易：天勤握手 1/2…」），
    /// 让用户知道预检里的天勤握手在跑，而不是以为按钮没反应
    private var startButtonTitle: String {
        if appState.startChecking {
            let text = appState.startProgressText
            return text.isEmpty ? "自动交易：检查中…" : "自动交易：\(text)"
        }
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

    /// 服务器状态胶囊：尺寸与「自动交易」按钮一致（高 36、圆角 18）。
    /// 整颗胶囊即入口，点击弹出服务器信息（地址、连通性、探测方式）。
    private var serverStatusPill: some View {
        let color = serverConnected ? AppTheme.statusGreen : AppTheme.statusRed
        return Button { showServerInfo = true } label: {
            Text("服务器状态：\(serverConnected ? "已连接" : "未连接")")
                .font(AppTheme.bodyFont.weight(.semibold))
                .foregroundColor(color)
                .padding(.horizontal, 22)
                .frame(minHeight: 36)
                .background(serverPillHovering ? AppTheme.canvas(scheme) : .clear)
                .overlay(
                    RoundedRectangle(cornerRadius: 18)
                        .stroke(serverPillHovering ? AppTheme.dim(scheme) : AppTheme.hairline(scheme), lineWidth: 1)
                )
                .cornerRadius(18)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { serverPillHovering = $0 }
        .pointerCursor()
        .help("点击查看服务器信息")
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
        let checking = appState.startChecking
        let disabled = appState.connState != .connected || checking
        // 进度填充是绿色，走完即自动交易开启态的同一个绿：文字在填充上要能读，
        // 所以进行中用 ink（浅色深字/深色白字），填满后与开启态一致转白字
        let progressDone = checking && appState.startProgressPct >= 100
        let fg: Color = progressDone
            ? .white
            : (checking
                ? AppTheme.ink(scheme)
                : (disabled
                    ? (scheme == .dark ? Color(hex: 0x86868B) : Color(hex: 0xCCCCCC))
                    : (active ? .white : AppTheme.primary(scheme))))
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
                    // 宽度随文案自适应（留出最长的阶段文案即可，不额外占位）
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
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
        // 进度直接填在胶囊底色上（整颗从左向右），文字仍在上层保持可读
        .background {
            ZStack(alignment: .leading) {
                bg
                startProgressFill
            }
        }
        .overlay(
            RoundedRectangle(cornerRadius: 18)
                .stroke(border, lineWidth: active || disabled ? 0 : 1)
        )
        // 裁剪到胶囊形状：否则填充的右边缘在半途会露出直角，与圆角胶囊打架
        .clipShape(RoundedRectangle(cornerRadius: 18))
    }

    /// 启动进度：整颗胶囊从左向右被绿色填满，宽度即完成百分比。
    /// 走满后与「自动交易：开」的实色绿完全一致，视觉上直接过渡到开启态。
    /// pct=-1（如停止操作）不填充，只显示文字。
    @ViewBuilder
    private var startProgressFill: some View {
        if appState.startChecking, appState.startProgressPct >= 0 {
            let done = appState.startProgressPct >= 100
            GeometryReader { geo in
                Rectangle()
                    // 未走满时压低不透明度，让覆盖其上的文字保持可读；
                    // 走满后转实色，与开启态无缝衔接
                    .fill(AppTheme.success(scheme).opacity(done ? 1 : 0.55))
                    .frame(width: geo.size.width * CGFloat(appState.startProgressPct) / 100)
            }
            .allowsHitTesting(false)
        }
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

// MARK: - 侧边栏导航按钮（SidebarButton：高 44、胶囊、文字左对齐）
/// 外观对齐交易记录页的「模拟交易 / 实盘交易」分段胶囊：
/// 选中为实心主题蓝 + 白字半粗，未选中为透明底 + 发丝描边，
/// 悬停时底色与描边各提一档（同 AppButton.secondary 的悬停约定）。
private struct SidebarNavButton: View {
    let title: String
    let isSelected: Bool
    let action: () -> Void

    @Environment(\.colorScheme) private var scheme
    @State private var hovering = false

    /// 胶囊圆角取按钮高度的一半（44 / 2），与分段胶囊按自身高度取圆角一致
    private static let cornerRadius: CGFloat = 22

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(AppTheme.bodyFont.weight(isSelected ? .semibold : .regular))
                .foregroundColor(isSelected ? .white : AppTheme.dim(scheme))
                .padding(.horizontal, 24)
                .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                .background(backgroundColor)
                .overlay(
                    RoundedRectangle(cornerRadius: Self.cornerRadius)
                        .stroke(borderColor, lineWidth: 1)
                )
                .cornerRadius(Self.cornerRadius)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .pointerCursor()
    }

    private var backgroundColor: Color {
        if isSelected { return AppTheme.sidebarSelected(scheme) }
        return hovering ? AppTheme.sidebarHover(scheme) : .clear
    }

    private var borderColor: Color {
        if isSelected { return AppTheme.sidebarSelected(scheme) }
        return hovering ? AppTheme.dim(scheme) : AppTheme.hairline(scheme)
    }
}
// MARK: - 服务器信息弹窗

/// 点击顶栏服务器状态胶囊后弹出的信息面板。
///
/// 只展示后端心跳探到的既有状态与配置，不发起额外请求：连通性由后端每 10 秒
/// 探测 /api/health 后随 status 事件推送。直接观察 appState 而不是由调用方传值，
/// 弹窗开着时状态变了也会跟着刷新。地址编辑走「编辑」按钮的二级弹窗，
/// 代理只作用于天勤连接，留在「设置」页。
private struct ServerInfoSheet: View {
    @ObservedObject var appState: AppState
    let onClose: () -> Void

    @Environment(\.colorScheme) private var scheme

    /// 「编辑服务器信息」二级弹窗
    @State private var showEditor = false
    @State private var closeHovering = false

    private var connected: Bool { appState.engineStatus.serverOnline }
    private var serverUrl: String { appState.config.serverUrl }
    /// 最近一次探测失败的原因，由后端心跳随 status 事件下发
    private var serverError: String { appState.engineStatus.serverError }

    private var statusColor: Color {
        connected ? AppTheme.statusGreen : AppTheme.statusRed
    }

    private var trimmedUrl: String {
        serverUrl.trimmingCharacters(in: .whitespaces)
    }

    /// 未连接时的原因文本：后端还没探过（空串）就说明还没拿到结果
    private var failureReason: String {
        guard !connected else { return "" }
        return serverError.isEmpty ? "尚未探测到结果" : serverError
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Circle()
                    .fill(statusColor)
                    .frame(width: 9, height: 9)
                Text("服务器信息")
                    .font(AppTheme.titleFont)
                    .foregroundColor(AppTheme.ink(scheme))
                Spacer(minLength: 0)
                // 右上角关闭：弹窗不再有底部按钮行
                Button { onClose() } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundColor(AppTheme.dim(scheme))
                        .frame(width: 28, height: 28)
                        .background(closeHovering ? AppTheme.sidebarHover(scheme) : .clear)
                        .cornerRadius(14)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .onHover { closeHovering = $0 }
                .pointerCursor()
                .keyboardShortcut(.cancelAction)
                .help("关闭")
            }
            .padding(.horizontal, 20)
            .padding(.top, 16)
            .padding(.bottom, 12)

            HairlineDivider()

            VStack(alignment: .leading, spacing: 0) {
                infoRow("服务器类型", value: "策略信号服务器")
                // 地址行带「编辑」入口：紧跟在取值之后，编辑即打开二级弹窗
                HStack(alignment: .top, spacing: 12) {
                    Text("服务器地址")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.dim(scheme))
                        .frame(width: 84, alignment: .leading)
                    HStack(alignment: .firstTextBaseline, spacing: 10) {
                        valueText(
                            trimmedUrl.isEmpty ? "未配置" : trimmedUrl,
                            valueColor: trimmedUrl.isEmpty ? AppTheme.warning(scheme) : AppTheme.ink(scheme),
                            selectable: true
                        )
                        editButton
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(.vertical, 7)
                infoRow("连接状态", value: connected ? "正常" : "不可达", valueColor: statusColor)
                // 失败原因单独成行并允许选中复制，方便用户排查或发给运维
                if !connected {
                    infoRow(
                        "失败原因",
                        value: failureReason,
                        valueColor: AppTheme.danger(scheme),
                        selectable: true
                    )
                }
                infoRow("探测方式", value: "每 10 秒请求 /api/health")
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 6)

            Text("客户端从该服务器获取交易品种、交易策略与交易信号。")
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 20)
                .padding(.top, 10)
                .padding(.bottom, 20)
        }
        .frame(width: 460)
        .background(AppTheme.canvas(scheme))
        .sheet(isPresented: $showEditor) {
            ServerEditSheet(appState: appState) { showEditor = false }
        }
    }

    /// 地址行尾部的文字按钮（样式同 Token 列表里的「切换」）
    private var editButton: some View {
        Button { showEditor = true } label: {
            Text("编辑")
                .font(AppTheme.dimFont.weight(.semibold))
                .foregroundColor(AppTheme.primary(scheme))
                .frame(height: 22)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help("修改服务器地址")
    }

    /// 一行「标签 + 取值」；长地址允许换行与选中复制
    private func infoRow(
        _ label: String,
        value: String,
        valueColor: Color? = nil,
        selectable: Bool = false
    ) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text(label)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .frame(width: 84, alignment: .leading)
            valueText(value, valueColor: valueColor, selectable: selectable)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 7)
    }

    /// 取值文本：只有地址这类需要复制的行开启选中（`.enabled`/`.disabled` 是不同类型，不能三元取）
    @ViewBuilder
    private func valueText(_ value: String, valueColor: Color?, selectable: Bool) -> some View {
        let text = Text(value)
            .font(AppTheme.bodyFont)
            .foregroundColor(valueColor ?? AppTheme.ink(scheme))
            .fixedSize(horizontal: false, vertical: true)
        if selectable {
            text.textSelection(.enabled)
        } else {
            text.textSelection(.disabled)
        }
    }
}

// MARK: - 编辑服务器信息弹窗

/// 服务器信息弹窗的「编辑服务器」二级弹窗：改地址 + 测试连接。
/// 与模拟页第 4 步、设置页共用同一份配置：输入完成（回车或失焦）即保存。
private struct ServerEditSheet: View {
    @ObservedObject var appState: AppState
    let onClose: () -> Void

    @Environment(\.colorScheme) private var scheme

    /// 地址输入框的占位文案（与后端首次运行写入的默认地址一致）
    private static let urlPlaceholder = "https://itrader.redboil.us.ci"

    @State private var urlDraft = ""
    /// 当前展示值对应的已落库值：既用于「未改动才回填」，也用于避免重复提交
    @State private var savedUrl = ""
    @State private var testing = false
    @State private var alert: ServerAlert?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("编辑服务器")
                .font(AppTheme.titleFont)
                .foregroundColor(AppTheme.ink(scheme))
                .padding(.horizontal, 20)
                .padding(.top, 20)
                .padding(.bottom, 14)

            HairlineDivider()

            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    AppTextField(
                        placeholder: Self.urlPlaceholder,
                        text: $urlDraft,
                        onCommit: saveUrlIfChanged
                    )
                    AppButton(testing ? "测试中..." : "测试连接", variant: .secondary, size: .inline, disabled: testing) {
                        testServer()
                    }
                }
                Text("这是 iTrader 智能交易服务器的地址，需与服务器部署地址一致")
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 16)

            HairlineDivider()

            HStack {
                Spacer()
                AppButton("好", variant: .secondary) { onClose() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(20)
        }
        .frame(width: 480)
        .background(AppTheme.canvas(scheme))
        .onAppear(perform: fillFromConfig)
        .onChange(of: appState.config) { _ in fillFromConfig() }
        .alert(item: $alert) { alert in
            Alert(
                title: Text(alert.title),
                message: Text(alert.message),
                dismissButton: .default(Text("好"))
            )
        }
    }

    /// 输入完成即保存；空值视为放弃修改，回退到仍在生效的地址
    private func saveUrlIfChanged() {
        let url = urlDraft.trimmingCharacters(in: .whitespaces)
        guard !url.isEmpty else {
            urlDraft = savedUrl
            return
        }
        urlDraft = url
        guard url != savedUrl else { return }
        savedUrl = url
        appState.saveConfig(serverUrl: url, proxyUrl: nil)
    }

    private func testServer() {
        let url = urlDraft.trimmingCharacters(in: .whitespaces)
        guard !url.isEmpty else {
            alert = .inputError("服务器地址不能为空")
            return
        }
        testing = true
        Task {
            let message = await appState.testServer(url: url)
            testing = false
            alert = .testResult(message: message)
        }
    }

    /// 用当前配置回填地址；用户正在编辑（与已落库值不同）时不覆盖
    private func fillFromConfig() {
        let config = appState.config
        let trimmedDraft = urlDraft.trimmingCharacters(in: .whitespaces)
        if trimmedDraft.isEmpty || trimmedDraft == savedUrl {
            urlDraft = config.serverUrl
        }
        savedUrl = config.serverUrl
    }
}

/// 服务器弹窗内的一次性提示（输入错误 / 测试连接结果）
private enum ServerAlert: Identifiable {
    case inputError(String)
    case testResult(message: String)

    var id: String {
        switch self {
        case .inputError(let msg): return "error-\(msg)"
        case .testResult(let msg): return "test-\(msg)"
        }
    }

    var title: String {
        switch self {
        case .inputError: return "输入错误"
        case .testResult: return "测试连接"
        }
    }

    var message: String {
        switch self {
        case .inputError(let msg), .testResult(let msg): return msg
        }
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
