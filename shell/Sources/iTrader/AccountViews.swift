import SwiftUI

/// 期货公司分组（与 Qt 版 BROKER_GROUPS 保持一致）
let BROKER_GROUPS: [(group: String, brokers: [String])] = [
    ("天勤免费版", ["宏源期货", "徽商期货", "银河期货"]),
    ("天勤专业版", ["东方汇金", "光大期货", "国泰君安"]),
]

/// 实盘交易页：账户配置（快速开始）+ 实盘账户的交易记录，两个 tab 切换。
/// 每张账户卡片持有独立的期货公司单选（跨账户不互斥），支持多个实盘账户并存。
struct AccountView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme
    /// 新建未保存卡片的 id（前端生成，对应 Qt live-{uuid4.hex[:8]}，保存时传给后端）
    @State private var draftIDs: [String] = []

    /// 两个 tab：「快速开始」是原来的账户配置，「交易记录」是实盘账户的执行明细
    private enum LiveTab: String, CaseIterable {
        case start
        case records

        var title: String { self == .start ? "快速开始" : "交易记录" }
    }

    /// tab 选择存在 UserDefaults，重启后回到上次查看的那一栏
    private static let tabKey = "live.tab"
    @State private var tab: LiveTab = LiveTab(
        rawValue: UserDefaults.standard.string(forKey: AccountView.tabKey) ?? ""
    ) ?? .start

    private var savedAccounts: [AccountInfo] {
        appState.accounts.filter { $0.kind == "live" }
    }

    var body: some View {
        PageContainer {
            // 标题常驻：切到「交易记录」tab 也能看出这是实盘交易页
            PageHeader(
                title: "实盘交易",
                subtitle: "添加多个实盘账户，每个账户独立选择期货公司与品种，保存后即时生效。"
            )
            CapsuleTabBar(tabs: LiveTab.allCases, title: \.title, selection: $tab)

            if tab == .start {
                AppCard {
                    VStack(alignment: .leading, spacing: 12) {
                        AppButton("＋ 添加实盘账户", variant: .secondary) { addDraft() }
                        brokerHint
                        // 已保存账户卡在前，新建草稿卡在后（对应 Qt set_accounts + _on_add_clicked）
                        VStack(alignment: .leading, spacing: 10) {
                            ForEach(savedAccounts) { account in
                                AccountCardForm(accountID: account.id, account: account)
                            }
                            ForEach(draftIDs, id: \.self) { id in
                                AccountCardForm(accountID: id, account: nil, onDismissed: {
                                    draftIDs.removeAll { $0 == id }
                                })
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .padding(20)
                }
            } else {
                AccountRecordsView(
                    kind: .live,
                    notConfiguredHint: "尚未配置实盘账户，请先在「快速开始」里添加账户"
                )
            }
        }
        .onChange(of: tab) { value in
            UserDefaults.standard.set(value.rawValue, forKey: Self.tabKey)
        }
    }

    private func addDraft() {
        draftIDs.append("live-" + UUID().uuidString.prefix(8).lowercased())
    }

    /// 期货公司支持范围说明（带 tqsdk-brokers 外链）
    private var brokerHint: some View {
        var prefix = AttributedString("期货公司按天勤版本分组，各账户可独立选择；受限于 TqSdk，支持范围参见：")
        prefix.font = AppTheme.dimFont
        prefix.foregroundColor = AppTheme.dim(scheme)
        var link = AttributedString("tqsdk-brokers")
        link.font = AppTheme.dimFont
        link.link = URL(string: "https://www.shinnytech.com/articles/reference/tqsdk-brokers")
        link.foregroundColor = AppTheme.link
        link.underlineStyle = .single
        return Text(prefix + link)
            .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: - 单个实盘账户卡片（对应 Qt _AccountCard；account 为 nil 时是新建表单）

private struct AccountCardForm: View {
    let accountID: String
    let account: AccountInfo?
    /// 新建卡保存成功 / 未保存卡删除后从草稿列表移除
    var onDismissed: (() -> Void)? = nil

    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    @State private var label = ""
    @State private var broker = ""
    @State private var tradeAccount = ""
    @State private var tradePassword = ""
    @State private var tqAccount = ""
    @State private var tqPassword = ""
    @State private var enabled = true
    @State private var selectedSymbol = ""
    @State private var selectedStrategy = ""
    @State private var alert: ActiveAlert?

    var body: some View {
        AppCard {
            VStack(alignment: .leading, spacing: 10) {
                headRow
                brokerRows
                dimLabel("资金账号:")
                AppTextField(placeholder: "资金账号 (实盘必填)", text: $tradeAccount)
                dimLabel("交易密码:")
                AppTextField(placeholder: "交易密码 (实盘必填)", secure: true, text: $tradePassword)
                dimLabel("快期账号:")
                AppTextField(placeholder: "快期账号", text: $tqAccount)
                dimLabel("快期密码:")
                AppTextField(placeholder: "快期密码", secure: true, text: $tqPassword)
                dimLabel("交易品种:")
                symbolPicker
                dimLabel("交易策略:")
                strategyPicker
                HStack {
                    Spacer()
                    AppButton("保存") { save() }
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
        }
        .onAppear(perform: fill)
        .onChange(of: account) { _ in fill() }
        .alert(item: $alert) { alert in
            Alert(
                title: Text(alert.title),
                message: Text(alert.message),
                dismissButton: .default(Text("好"))
            )
        }
    }

    // MARK: 标题行（账户名称输入框 + 启用开关 + 删除）

    private var headRow: some View {
        HStack(spacing: 10) {
            AppTextField(placeholder: "账户名称（如：光大-主力）", text: $label)
            enabledCheck
            AppButton("删除", variant: .danger) { delete() }
        }
    }

    /// 启用 checkbox（对应 Qt QCheckBox：macOS 原生渲染，spacing 10、行高 30）
    private var enabledCheck: some View {
        Button { enabled.toggle() } label: {
            HStack(spacing: 10) {
                Image(systemName: enabled ? "checkmark.square.fill" : "square")
                    .foregroundColor(enabled ? AppTheme.primary(scheme) : AppTheme.dim(scheme))
                Text("启用")
                    .font(AppTheme.bodyFont)
                    .foregroundColor(AppTheme.ink(scheme))
            }
            .frame(minHeight: 30)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }

    // MARK: 期货公司单选（每卡片独立一组，按天勤版本分行）

    private var brokerRows: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(BROKER_GROUPS, id: \.group) { group in
                HStack(spacing: 10) {
                    Text("\(group.group):")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.dim(scheme))
                    ForEach(group.brokers, id: \.self) { name in
                        RadioRow(
                            symbol: name,
                            isOn: Binding(
                                get: { broker == name },
                                set: { if $0 { broker = name } }
                            ),
                            expanding: false
                        )
                    }
                    Spacer(minLength: 0)
                }
            }
        }
    }

    // MARK: 品种单选（与模拟页共用服务器品种列表，卡片内不带刷新按钮）

    private var symbolPicker: some View {
        VStack(alignment: .leading, spacing: 6) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 160), spacing: 10)], alignment: .leading, spacing: 10) {
                ForEach(allSymbols, id: \.self) { symbol in
                    RadioRow(
                        symbol: appState.displaySymbol(symbol),
                        isOn: Binding(
                            get: { selectedSymbol == symbol },
                            set: { if $0 { selectedSymbol = symbol } }
                        )
                    )
                }
            }
            Text(pickerHint)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// 已选但服务器未返回的品种保留显示（对应 Qt set_symbols 的 merge 规则）
    private var allSymbols: [String] {
        var result = appState.serverSymbols
        if !selectedSymbol.isEmpty && !result.contains(selectedSymbol) {
            result.append(selectedSymbol)
        }
        return result
    }

    // MARK: 策略单选（与模拟页共用服务器策略目录，单选后保存生效）

    private var strategyPicker: some View {
        VStack(alignment: .leading, spacing: 6) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 160), spacing: 10)], alignment: .leading, spacing: 10) {
                ForEach(strategyOptions) { strategy in
                    RadioRow(
                        symbol: strategy.name,
                        isOn: Binding(
                            get: { selectedStrategy == strategy.id },
                            set: { if $0 { selectedStrategy = strategy.id } }
                        )
                    )
                    .help(strategy.description)
                }
            }
            Text(strategyHint)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// 已选策略不在服务器目录里时保留显示，避免服务器临时不可用时选中态凭空消失
    private var strategyOptions: [StrategyInfo] {
        guard !selectedStrategy.isEmpty,
              !appState.serverStrategies.contains(where: { $0.id == selectedStrategy }) else {
            return appState.serverStrategies
        }
        return appState.serverStrategies + [StrategyInfo(id: selectedStrategy, name: selectedStrategy, description: "")]
    }

    private var strategyHint: String {
        if appState.strategiesFetching { return "正在获取策略..." }
        if appState.strategiesMessage.hasPrefix("获取失败") {
            return "获取策略失败: \(appState.strategiesMessage)"
        }
        if strategyOptions.isEmpty { return "服务器暂无可选策略" }
        return "共 \(strategyOptions.count) 个策略，选择一个订阅，保存后生效"
    }

    /// 对应 Qt SymbolPicker 的 hint 文案规则
    private var pickerHint: String {
        if appState.symbolsFetching { return "正在获取品种..." }
        if appState.symbolsMessage.hasPrefix("获取失败") {
            return "获取品种失败: \(appState.symbolsMessage)"
        }
        let merged = allSymbols
        if !merged.isEmpty {
            return "共 \(merged.count) 个品种，选择一个订阅，保存后生效"
        }
        return "服务器暂无可订阅品种"
    }

    private var hasOptions: Bool { !allSymbols.isEmpty }

    private func dimLabel(_ text: String) -> some View {
        Text(text)
            .font(AppTheme.dimFont)
            .foregroundColor(AppTheme.dim(scheme))
    }

    // MARK: - 动作

    /// 用账户记录回填表单（对应 Qt fill；后端不回传密码，密码框保持留空 = 沿用旧值）
    private func fill() {
        let names = BROKER_GROUPS.flatMap(\.brokers)
        if let account {
            label = account.label
            broker = names.contains(account.broker) ? account.broker : (names.first ?? "")
            tradeAccount = account.tradeAccount
            tqAccount = account.tqAccount
            enabled = account.enabled
            selectedSymbol = account.symbols.first ?? ""
            selectedStrategy = account.strategy
        } else if broker.isEmpty {
            // 新卡片默认选中第一个期货公司（对应 Qt checked_name）
            broker = names.first ?? ""
        }
    }

    private func save() {
        // 校验顺序与文案对应 Qt _on_save_clicked；密码留空 = 沿用旧值（仅新卡必填）
        if broker.isEmpty {
            alert = .inputError("请选择期货公司")
            return
        }
        if (!tradeAccount.isEmpty && tradePassword.isEmpty && account == nil)
            || (tradeAccount.isEmpty && !tradePassword.isEmpty) {
            alert = .inputError("资金账号与交易密码需同时填写")
            return
        }
        if tqAccount.trimmingCharacters(in: .whitespaces).isEmpty {
            alert = .inputError("快期账号不能为空")
            return
        }
        if tqPassword.isEmpty && account == nil {
            alert = .inputError("快期密码不能为空")
            return
        }
        if hasOptions && selectedSymbol.isEmpty {
            alert = .inputError("请选择一个交易品种")
            return
        }
        if !strategyOptions.isEmpty && selectedStrategy.isEmpty {
            alert = .inputError("请选择一个交易策略")
            return
        }

        var params: [String: Any] = [
            "account_id": accountID,
            "kind": "live",
            "label": label.trimmingCharacters(in: .whitespaces),
            "broker": broker,
            "trade_account": tradeAccount.trimmingCharacters(in: .whitespaces),
            "tq_account": tqAccount.trimmingCharacters(in: .whitespaces),
            "enabled": enabled,
        ]
        if !tradePassword.isEmpty { params["trade_password"] = tradePassword }
        if !tqPassword.isEmpty { params["tq_password"] = tqPassword }
        if hasOptions && !selectedSymbol.isEmpty { params["symbols"] = [selectedSymbol] }
        // 空串是后端的「清除选择」语义，只有确实有选中值时才提交
        if !selectedStrategy.isEmpty { params["strategy"] = selectedStrategy }

        Task {
            await appState.runAction(method: "accounts.save", params: params)
            if appState.lastActionError.isEmpty {
                alert = .accountSaved
                // 新卡保存成功后从草稿区移除，accounts 刷新后出现正式卡（对应 Qt set_accounts 重建）
                if account == nil { onDismissed?() }
            } else {
                alert = .inputError(appState.lastActionError)
            }
        }
    }

    private func delete() {
        if let account {
            Task {
                await appState.runAction(method: "accounts.delete", params: ["account_id": account.id])
            }
        } else {
            onDismissed?()
        }
    }
}
