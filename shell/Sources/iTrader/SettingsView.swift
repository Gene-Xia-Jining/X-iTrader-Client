import SwiftUI

/// 设置页：代理服务器配置（像素级对应 Qt SettingsPage）。
/// 服务器地址与测试连接已合并到顶栏服务器弹窗（点击服务器状态胶囊）；
/// 软件更新已合并到顶栏版本胶囊（点击即检查更新）。
struct SettingsView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    /// 代理模式（对应 Qt 直连/系统代理/自定义三选一）
    enum ProxyMode {
        case direct, system, custom
    }

    // 表单
    @State private var proxyMode: ProxyMode = .direct
    @State private var proxyUrl = ""

    // 表单当前展示值对应的已落库值：既用于「未改动才回填」，也用于避免重复提交
    @State private var savedProxy = ""

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
    }

    // MARK: - 表单（QFormLayout：label 列右对齐，字段列撑宽，行距 10）

    private var formGrid: some View {
        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 10) {
            GridRow {
                formLabel("代理服务器:")
                proxyRow
            }
            GridRow {
                formLabel("")
                hintLabel("仅影响天勤行情/交易连接，iTrader 服务器始终直连；直连会忽略系统代理。socks5 代理需安装 python-socks")
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

    // MARK: - 动作

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

    /// 用当前配置回填表单（对应 Qt set_config：启动时及 config_changed 后调用）。
    /// 用户正在编辑（与已落库值不同）的字段不回填，避免把输入到一半的内容覆盖掉。
    private func fillFromConfig() {
        let config = appState.config

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
    }
}
