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

    // 表单当前展示值对应的已落库值：既用于「未改动才回填」，也用于避免重复提交
    @State private var savedServerUrl = ""
    @State private var savedProxy = ""
    @State private var savedAutoCheck = true

    // 服务器测试（对应 ServerUrlTestRow）
    @State private var testing = false
    @State private var showHelp = false
    @State private var helpHover = false

    // 弹窗
    @State private var alert: ActiveAlert?

    var body: some View {
        PageContainer {
            AppCard {
                formGrid
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

    // MARK: 服务器地址行（对应 ServerUrlTestRow：? 按钮 + 输入框 + 测试连接；结果走弹窗）

    private var serverTestRow: some View {
        HStack(spacing: 8) {
            helpButton
            AppTextField(
                placeholder: "https://itrader.redboil.us.ci",
                text: $serverUrl,
                onCommit: saveServerIfChanged
            )
            AppButton(testing ? "测试中..." : "测试连接", variant: .secondary, disabled: testing) {
                testServer()
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
                disabled: proxyMode != .custom,
                onCommit: saveProxyIfChanged
            )
        }
    }

    private func proxyRadio(_ title: String, mode: ProxyMode) -> some View {
        RadioRow(
            symbol: title,
            isOn: Binding(
                get: { proxyMode == mode },
                set: { if $0 { changeProxyMode(mode) } }
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
        Button {
            autoCheckUpdate.toggle()
            saveAutoCheckIfChanged()
        } label: {
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

    private func testServer() {
        let url = serverUrl.trimmingCharacters(in: .whitespaces)
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

    /// 服务器地址输入完成（回车或失焦）即保存；空值视为放弃修改，回退到仍在生效的地址。
    /// 与已落库值相同则不重复提交（config_changed 会让同一套值再回填一次）。
    private func saveServerIfChanged() {
        let url = serverUrl.trimmingCharacters(in: .whitespaces)
        guard !url.isEmpty else {
            serverUrl = savedServerUrl
            return
        }
        serverUrl = url
        guard url != savedServerUrl else { return }
        savedServerUrl = url
        appState.saveConfig(serverUrl: url, proxyUrl: nil)
    }

    /// 代理模式切换即保存：直连/系统代理本身就是完整取值；切到自定义时先等用户填地址
    private func changeProxyMode(_ mode: ProxyMode) {
        guard proxyMode != mode else { return }
        proxyMode = mode
        switch mode {
        case .direct:
            proxyUrl = ""
            commitProxy("")
        case .system:
            proxyUrl = ""
            commitProxy("system")
        case .custom:
            // 直连/系统代理没有可回填的地址，输入框留空等用户填写
            proxyUrl = savedProxy == "system" ? "" : savedProxy
        }
    }

    /// 代理地址输入完成（回车或失焦）即保存；只在自定义模式下生效，空值视为放弃修改
    private func saveProxyIfChanged() {
        guard proxyMode == .custom else { return }
        let value = proxyUrl.trimmingCharacters(in: .whitespaces)
        guard !value.isEmpty else {
            proxyUrl = savedProxy == "system" ? "" : savedProxy
            return
        }
        proxyUrl = value
        commitProxy(value)
    }

    private func commitProxy(_ value: String) {
        guard value != savedProxy else { return }
        savedProxy = value
        appState.saveConfig(serverUrl: nil, proxyUrl: value)
    }

    private func saveAutoCheckIfChanged() {
        guard autoCheckUpdate != savedAutoCheck else { return }
        savedAutoCheck = autoCheckUpdate
        appState.saveConfig(serverUrl: nil, proxyUrl: nil, autoCheckUpdate: autoCheckUpdate)
    }

    /// 用当前配置回填表单（对应 Qt set_config：启动时及 config_changed 后调用）。
    /// 用户正在编辑（与已落库值不同）的字段不回填，避免把输入到一半的内容覆盖掉。
    private func fillFromConfig() {
        let config = appState.config

        let trimmedServer = serverUrl.trimmingCharacters(in: .whitespaces)
        if trimmedServer.isEmpty || trimmedServer == savedServerUrl {
            serverUrl = config.serverUrl
        }
        savedServerUrl = config.serverUrl

        let proxy = config.proxyUrl.trimmingCharacters(in: .whitespaces)
        let trimmedProxy = proxyUrl.trimmingCharacters(in: .whitespaces)
        // 自定义模式下输入框为空：用户刚切过来还没填，别把单选跳回直连
        let awaitingCustomInput = proxyMode == .custom && trimmedProxy.isEmpty
        let editingProxy = !trimmedProxy.isEmpty && trimmedProxy != savedProxy
        if !awaitingCustomInput && !editingProxy {
            switch proxy {
            case "system":
                proxyMode = .system
                proxyUrl = ""
            case "":
                proxyMode = .direct
                proxyUrl = ""
            default:
                proxyMode = .custom
                proxyUrl = proxy
            }
        }
        savedProxy = proxy

        autoCheckUpdate = config.autoCheckUpdate
        savedAutoCheck = config.autoCheckUpdate
    }
}
