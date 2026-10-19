import SwiftUI

/// 设置页：服务器连接 + 代理 + 软件更新（像素级对应 Qt SettingsPage）。
struct SettingsView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    /// 代理模式（对应 Qt 直连/系统代理/自定义三选一）
    enum ProxyMode {
        case direct, system, custom
    }

    // 表单
    @State private var serverUrl = ""
    @State private var proxyMode: ProxyMode = .direct
    @State private var proxyUrl = ""
    @State private var autoCheckUpdate = true

    // 服务器测试（对应 ServerUrlTestRow）
    @State private var testing = false
    @State private var testMessage = ""
    @State private var testSuccess: Bool?  // nil = 进行中
    @State private var showHelp = false
    @State private var helpHover = false

    // 弹窗
    @State private var alert: ActiveAlert?

    var body: some View {
        PageContainer {
            PageHeader(title: "设置", subtitle: "服务器连接配置，保存后即时生效。")
            AppCard {
                VStack(alignment: .leading, spacing: 12) {
                    formGrid
                    HairlineDivider()
                    HStack {
                        Spacer()
                        AppButton("保存") { save() }
                    }
                }
                .padding(20)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
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

    // MARK: - 表单（QFormLayout：label 列右对齐，字段列撑宽，行距 10）

    private var formGrid: some View {
        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 10) {
            GridRow {
                formLabel("服务器地址:")
                serverTestRow
            }
            GridRow {
                formLabel("")
                hintLabel("这是 iTrader 智能交易服务器的地址，需与服务器部署地址一致")
            }
            GridRow {
                formLabel("代理服务器:")
                proxyRow
            }
            GridRow {
                formLabel("")
                hintLabel("仅影响天勤行情/交易连接，iTrader 服务器始终直连；直连会忽略系统代理。socks5 代理需安装 python-socks")
            }
            GridRow {
                formLabel("")
                HairlineDivider()
            }
            GridRow {
                formLabel("软件更新:")
                updateRow
            }
            GridRow {
                formLabel("")
                autoCheckRow
            }
        }
    }

    private func formLabel(_ text: String) -> some View {
        Text(text)
            .font(AppTheme.bodyFont)
            .foregroundColor(AppTheme.ink(scheme))
            .gridColumnAlignment(.trailing)
    }

    private func hintLabel(_ text: String) -> some View {
        Text(text)
            .font(AppTheme.dimFont)
            .foregroundColor(AppTheme.dim(scheme))
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: 服务器地址行（对应 ServerUrlTestRow：? 按钮 + 输入框 + 测试连接 + 结果行）

    private var serverTestRow: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                helpButton
                AppTextField(placeholder: "https://itrader.redboil.us.ci", text: $serverUrl)
                AppButton("测试连接", variant: .secondary, disabled: testing) { testServer() }
            }
            if !testMessage.isEmpty {
                Text(testMessage)
                    .font(AppTheme.bodyFont)
                    .foregroundColor(statusColor(testSuccess))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// 20x20 圆形问号按钮，点击弹出地址说明（对应 Qt QToolTip）
    private var helpButton: some View {
        Button { showHelp = true } label: {
            Text("?")
                .font(.system(size: 14, weight: .semibold))
                .foregroundColor(AppTheme.primary(scheme))
                .frame(width: 20, height: 20)
                .background(helpHover ? AppTheme.sidebarHover(scheme) : .clear)
                .cornerRadius(10)
                .overlay(Circle().stroke(AppTheme.hairline(scheme), lineWidth: 1))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .onHover { helpHover = $0 }
        .popover(isPresented: $showHelp, arrowEdge: .bottom) {
            Text("这是 iTrader 智能交易服务器的地址")
                .font(AppTheme.dimFont)
                .padding(10)
        }
    }

    // MARK: 代理服务器行（三选一 radio + 自定义地址输入框）

    private var proxyRow: some View {
        HStack(spacing: 12) {
            proxyRadio("直连（默认）", mode: .direct)
            proxyRadio("系统代理", mode: .system)
            proxyRadio("自定义", mode: .custom)
            AppTextField(
                placeholder: "http://127.0.0.1:7890 或 socks5://127.0.0.1:1080",
                text: $proxyUrl,
                disabled: proxyMode != .custom
            )
        }
    }

    private func proxyRadio(_ title: String, mode: ProxyMode) -> some View {
        RadioRow(
            symbol: title,
            isOn: Binding(
                get: { proxyMode == mode },
                set: { if $0 { proxyMode = mode } }
            ),
            expanding: false
        )
    }

    // MARK: 软件更新行（当前版本 + 检查更新 + 状态；新版本时提供下载/安装入口）

    private var updateRow: some View {
        HStack(spacing: 8) {
            Text("当前版本 v\(appState.backendVersion)")
                .font(AppTheme.bodyFont)
                .foregroundColor(AppTheme.ink(scheme))
            AppButton(
                "检查更新",
                variant: .secondary,
                disabled: appState.updatePhase == .checking || appState.updatePhase == .downloading
            ) {
                appState.checkUpdate()
            }
            updateStatusArea
        }
    }

    /// 状态区（对应 update_status_label，占满剩余宽度）；新版本就绪时给出操作按钮
    @ViewBuilder
    private var updateStatusArea: some View {
        switch appState.updatePhase {
        case .available(let version, let hasPatch):
            AppButton("下载 v\(version)\(hasPatch ? "（差量）" : "")") { appState.downloadUpdate() }
        case .ready(let kind):
            AppButton("安装并重启后端（\(kind)）") { appState.applyUpdate() }
        default:
            HStack(spacing: 0) {
                if let message = updateStatusMessage {
                    Text(message)
                        .font(AppTheme.bodyFont)
                        .foregroundColor(updateStatusColor)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
        }
    }

    private var updateStatusMessage: String? {
        switch appState.updatePhase {
        case .checking:
            return "正在检查更新..."
        case .downloading:
            return "正在下载更新..."
        case .latest(let message):
            return message
        case .failed(let message):
            return message
        default:
            return nil
        }
    }

    /// Qt set_update_status：None 进行中 WARNING / 成功 SUCCESS / 失败 DANGER（固定色，不随主题）
    private var updateStatusColor: Color {
        switch appState.updatePhase {
        case .latest: return Color(hex: 0x34C759)
        case .failed: return Color(hex: 0xE3342F)
        default: return Color(hex: 0xE8A33D)
        }
    }

    private var autoCheckRow: some View {
        Button { autoCheckUpdate.toggle() } label: {
            HStack(spacing: 10) {
                Image(systemName: autoCheckUpdate ? "checkmark.square.fill" : "square")
                    .foregroundColor(autoCheckUpdate ? AppTheme.primary(scheme) : AppTheme.dim(scheme))
                Text("启动时自动检查更新，发现新版本时提示")
                    .font(AppTheme.bodyFont)
                    .foregroundColor(AppTheme.ink(scheme))
                Spacer(minLength: 0)
            }
            .frame(minHeight: 30)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }

    // MARK: - 动作

    /// Qt set_update_status 同款状态色（固定值，不随主题）
    private func statusColor(_ success: Bool?) -> Color {
        switch success {
        case .none: return Color(hex: 0xE8A33D)
        case .some(true): return Color(hex: 0x34C759)
        case .some(false): return Color(hex: 0xE3342F)
        }
    }

    private func testServer() {
        let url = serverUrl.trimmingCharacters(in: .whitespaces)
        guard !url.isEmpty else {
            testMessage = "请先输入服务器地址"
            testSuccess = false
            return
        }
        testing = true
        testMessage = "正在测试连接..."
        testSuccess = nil
        Task {
            let message = await appState.testServer(url: url)
            testing = false
            testMessage = message
            testSuccess = message.contains("成功")
        }
    }

    private func save() {
        let url = serverUrl.trimmingCharacters(in: .whitespaces)
        guard !url.isEmpty else {
            alert = .inputError("服务器地址不能为空")
            return
        }
        // 对应 Qt _current_proxy_value：系统代理存 "system"，直连存空串
        let proxyValue: String
        switch proxyMode {
        case .direct: proxyValue = ""
        case .system: proxyValue = "system"
        case .custom: proxyValue = proxyUrl.trimmingCharacters(in: .whitespaces)
        }
        appState.saveConfig(serverUrl: url, proxyUrl: proxyValue, autoCheckUpdate: autoCheckUpdate)
        alert = .configSaved
    }

    /// 用当前配置回填表单（对应 Qt set_config：启动时及保存后调用）
    private func fillFromConfig() {
        serverUrl = appState.config.serverUrl
        let proxy = appState.config.proxyUrl.trimmingCharacters(in: .whitespaces)
        if proxy == "system" {
            proxyMode = .system
            proxyUrl = ""
        } else if !proxy.isEmpty {
            proxyMode = .custom
            proxyUrl = proxy
        } else {
            proxyMode = .direct
            proxyUrl = ""
        }
        autoCheckUpdate = appState.config.autoCheckUpdate
    }
}
