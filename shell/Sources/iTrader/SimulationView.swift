import SwiftUI

/// 模拟交易页：快期模拟账户 4 步引导卡片（像素级对应 Qt SimulationPage）。
struct SimulationView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    // 表单
    @State private var phone = ""
    @State private var password = ""
    @State private var passwordVisible = false
    // 品种单选（服务器品种；已订阅但不在服务器列表的品种保留显示）
    @State private var selectedSymbol = ""
    @State private var keptSymbols: [String] = []
    // 交易策略单选（服务器策略目录）
    @State private var selectedStrategy = ""
    // 测试连接进行中（按钮置灰）
    @State private var testing = false
    // 弹窗（对应 Qt QMessageBox）
    @State private var alert: ActiveAlert?

    var body: some View {
        PageContainer {
            AppCard {
                VStack(alignment: .leading, spacing: 12) {
                    guideLabel("在开始实盘交易之前，您应该通过 1-3 个月的模拟交易建立信心。\n\n我们支持快期的模拟交易，您需要：")
                    guideLabel("1，下载快期模拟版客户端：")
                    linkRow("手机端：", "https://www.shinnytech.com/products/app")
                    // App Store 是手机端 iOS 渠道的子项，保留 Qt 版的 4 空格缩进
                    linkRow("    App Store：", "https://itunes.apple.com/us/app/快期小q/id1187762307?l=zh&ls=1&mt=8")
                    linkRow("Windows：", "https://www.shinnytech.com/products/q73")

                    HairlineDivider()

                    guideLabel("2，在快期客户端或天勤官网注册：")
                    linkRow("天勤官网：", "https://account.shinnytech.com/")

                    HairlineDivider()

                    guideLabel("3，在这里填入：")
                    inputRow

                    HairlineDivider()

                    guideLabel("4，选择交易品种：")
                    symbolPicker
                    hintLabel("提示：模拟帐户不支持组合/套利和期权交易，仅国内商品和股指国债。")

                    HairlineDivider()

                    guideLabel("5，选择交易策略：")
                    strategyPicker
                    hintLabel("提示：策略由服务器计算交易信号，客户端按信号执行开仓 / 平仓。")
                }
                .padding(20)
            }
        }
        .onAppear(perform: fillFromAccount)
        .onChange(of: appState.accounts) { _ in fillFromAccount() }
        .alert(item: $alert) { alert in
            Alert(
                title: Text(alert.title),
                message: Text(alert.message),
                dismissButton: .default(Text("好"))
            )
        }
    }

    // MARK: - 文本与链接

    private func guideLabel(_ text: String) -> some View {
        Text(text)
            .font(AppTheme.bodyFont)
            .kerning(-0.1)
            .foregroundColor(AppTheme.ink(scheme))
            .fixedSize(horizontal: false, vertical: true)
            .textSelection(.enabled)
    }

    private func hintLabel(_ text: String) -> some View {
        Text(text)
            .font(AppTheme.dimFont)
            .foregroundColor(AppTheme.dim(scheme))
            .fixedSize(horizontal: false, vertical: true)
    }

    /// 链接行：13px 灰前缀 + #007aff 下划线链接（点击打开浏览器）
    private func linkRow(_ prefix: String, _ urlString: String) -> some View {
        var prefixAttr = AttributedString(prefix)
        prefixAttr.font = Font.system(size: 13)
        prefixAttr.foregroundColor = AppTheme.dim(scheme)
        var linkAttr = AttributedString(urlString)
        linkAttr.font = Font.system(size: 13)
        linkAttr.link = URL(string: urlString)
        linkAttr.foregroundColor = AppTheme.link
        linkAttr.underlineStyle = .single
        return Text(prefixAttr + linkAttr)
            .fixedSize(horizontal: false, vertical: true)
            .textSelection(.enabled)
    }

    // MARK: - 输入行（手机号 / 密码 / 眼睛 / 保存 / 测试连接）

    private var inputRow: some View {
        HStack(spacing: 8) {
            Text("手机号：")
                .font(AppTheme.bodyFont)
                .foregroundColor(AppTheme.ink(scheme))
            AppTextField(placeholder: "手机号", text: $phone)
            Text("密码：")
                .font(AppTheme.bodyFont)
                .foregroundColor(AppTheme.ink(scheme))
            passwordField
            eyeButton
            AppButton("保存", variant: .secondary, size: .inline) { save() }
            AppButton(testing ? "测试中..." : "测试连接", variant: .secondary, size: .inline, disabled: testing) {
                testConnection()
            }
        }
    }

    private var passwordField: some View {
        HStack(spacing: 0) {
            if passwordVisible {
                TextField("密码", text: $password)
                    .textFieldStyle(.plain)
                    .font(AppTheme.bodyFont)
            } else {
                SecureField("密码", text: $password)
                    .textFieldStyle(.plain)
                    .font(AppTheme.bodyFont)
            }
        }
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity, minHeight: 36)
        .background(AppTheme.inputBg(scheme))
        .cornerRadius(8)
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
    }

    /// 眼睛图标按钮：图标表示点击后的效果（隐藏时睁眼=查看，明文时闭眼=隐藏）
    private var eyeButton: some View {
        Button {
            passwordVisible.toggle()
        } label: {
            Image(systemName: passwordVisible ? "eye.slash" : "eye")
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: 16, height: 16)
                .foregroundColor(AppTheme.link)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .buttonStyle(.plain)
        .frame(minWidth: 40, maxWidth: 40, minHeight: 36)
        .background(AppTheme.inputBg(scheme))
        .cornerRadius(8)
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
        .pointerCursor()
    }

    // MARK: - 品种区（单选胶囊 + 刷新按钮，对应 Qt SymbolPicker）

    private var symbolPicker: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: 8) {
                FlowLayout(spacing: 10) {
                    ForEach(simAllSymbols, id: \.self) { symbol in
                        SymbolCapsule(label: appState.displaySymbol(symbol), isOn: Binding(
                            get: { selectedSymbol == symbol },
                            set: { isOn in
                                selectedSymbol = isOn ? symbol : ""
                                saveSymbolSelection()
                            }
                        ))
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                AppButton("刷新", variant: .secondary) { appState.fetchSymbols() }
            }
            Text(pickerHint)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// 模拟页可选品种：服务器品种在前；已订阅但不在服务器列表的品种保留显示（merge 规则）
    private var simAllSymbols: [String] {
        var result = appState.serverSymbols
        for s in keptSymbols where !result.contains(s) {
            result.append(s)
        }
        return result
    }

    /// 品种区状态行（对应 Qt SymbolPicker 的 _hint 文案规则）
    private var pickerHint: String {
        if appState.symbolsFetching { return "正在获取品种..." }
        if appState.symbolsMessage.hasPrefix("获取失败") {
            return "获取品种失败: \(appState.symbolsMessage)"
        }
        if !appState.serverSymbols.isEmpty {
            return "共 \(appState.serverSymbols.count) 个品种，选择一个订阅，选中即生效"
        }
        return "尚未获取品种"
    }

    /// 品种选中/取消选中即生效：只提交订阅品种（取消时提交空列表），其余字段沿用已有值；
    /// 账户尚未建立时仅本地选中，待「保存」随手机号/密码一并提交
    private func saveSymbolSelection() {
        guard let sim = appState.accounts.first(where: { $0.kind == "sim" }) else { return }
        let symbols: [String] = selectedSymbol.isEmpty ? [] : [selectedSymbol]
        Task {
            await appState.runAction(method: "accounts.save", params: [
                "kind": "sim",
                "account_id": sim.id,
                "symbols": symbols,
            ])
            if !appState.lastActionError.isEmpty {
                alert = .inputError(appState.lastActionError)
            }
        }
    }

    // MARK: - 策略区（单选胶囊，对应品种区的胶囊样式）

    private var strategyPicker: some View {
        VStack(alignment: .leading, spacing: 6) {
            FlowLayout(spacing: 10) {
                ForEach(appState.serverStrategies) { strategy in
                    SymbolCapsule(label: strategy.name, isOn: Binding(
                        get: { selectedStrategy == strategy.id },
                        set: { isOn in
                            selectedStrategy = isOn ? strategy.id : ""
                            saveStrategySelection()
                        }
                    ))
                    .help(strategy.description)
                }
            }
            Text(strategyHint)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// 策略区状态行（对应品种区 hint 文案规则）
    private var strategyHint: String {
        if appState.strategiesFetching { return "正在获取策略..." }
        if appState.strategiesMessage.hasPrefix("获取失败") {
            return "获取策略失败: \(appState.strategiesMessage)"
        }
        if !appState.serverStrategies.isEmpty {
            return "共 \(appState.serverStrategies.count) 个策略，选择一个，选中即生效"
        }
        return "尚未获取策略"
    }

    /// 策略选中/取消即生效：只提交 strategy（取消时传空串清除），symbols 等字段沿用已有值
    private func saveStrategySelection() {
        guard let sim = appState.accounts.first(where: { $0.kind == "sim" }) else { return }
        Task {
            await appState.runAction(method: "accounts.save", params: [
                "kind": "sim",
                "account_id": sim.id,
                "strategy": selectedStrategy,
            ])
            if !appState.lastActionError.isEmpty {
                alert = .inputError(appState.lastActionError)
            }
        }
    }

    // MARK: - 数据与动作

    /// 用已存模拟账户回填（后端不回传密码，密码留空 = 沿用旧值）；
    /// 仅在手机号为空时填充，避免覆盖用户正在编辑的内容
    private func fillFromAccount() {
        guard phone.isEmpty,
              let sim = appState.accounts.first(where: { $0.kind == "sim" }) else { return }
        phone = sim.tqAccount
        if let first = sim.symbols.first {
            selectedSymbol = first
            if !appState.serverSymbols.contains(first) && keptSymbols.isEmpty {
                keptSymbols = [first]
            }
        }
        if !sim.strategy.isEmpty {
            selectedStrategy = sim.strategy
        }
    }

    private var hasOptions: Bool {
        !appState.serverSymbols.isEmpty || !keptSymbols.isEmpty
    }

    private func save() {
        let account = phone.trimmingCharacters(in: .whitespaces)
        guard !account.isEmpty else {
            alert = .inputError("手机号不能为空")
            return
        }
        // 新账户必须设置密码；已存账户留空沿用旧密码（后端约定）
        let isNew = !appState.accounts.contains { $0.kind == "sim" }
        guard !password.isEmpty || !isNew else {
            alert = .inputError("密码不能为空")
            return
        }

        var params: [String: Any] = [
            "kind": "sim",
            "label": "模拟账户",
            "tq_account": account,
        ]
        if let sim = appState.accounts.first(where: { $0.kind == "sim" }) {
            params["account_id"] = sim.id
        }
        if !password.isEmpty { params["tq_password"] = password }
        if hasOptions && !selectedSymbol.isEmpty {
            params["symbols"] = [selectedSymbol]
        }
        if !selectedStrategy.isEmpty {
            params["strategy"] = selectedStrategy
        }

        Task {
            await appState.runAction(method: "accounts.save", params: params)
            alert = appState.lastActionError.isEmpty
                ? .saveSuccess
                : .inputError(appState.lastActionError)
        }
    }

    private func testConnection() {
        let account = phone.trimmingCharacters(in: .whitespaces)
        guard !account.isEmpty else {
            alert = .inputError("手机号不能为空")
            return
        }
        guard !password.isEmpty else {
            alert = .inputError("密码不能为空")
            return
        }
        testing = true
        Task {
            let message = await appState.testTqAuth(account: account, password: password)
            testing = false
            alert = .testResult(message: message)
        }
    }
}

// MARK: - 弹窗（对应 Qt QMessageBox）

enum ActiveAlert: Identifiable {
    case inputError(String)
    case saveSuccess
    case testResult(message: String)
    case configSaved
    case accountSaved

    var id: String {
        switch self {
        case .inputError(let msg): return "error-\(msg)"
        case .saveSuccess: return "saved"
        case .testResult(let msg): return "test-\(msg)"
        case .configSaved: return "config-saved"
        case .accountSaved: return "account-saved"
        }
    }

    var title: String {
        switch self {
        case .inputError: return "输入错误"
        case .saveSuccess: return "保存成功"
        case .testResult: return "测试连接"
        case .configSaved, .accountSaved: return "保存成功"
        }
    }

    var message: String {
        switch self {
        case .inputError(let msg): return msg
        case .saveSuccess: return "模拟交易账户配置已保存"
        case .testResult(let msg): return msg
        case .configSaved: return "配置已保存并生效"
        case .accountSaved: return "实盘账户已保存"
        }
    }
}

// MARK: - 流式布局（胶囊自然跟随：按内容宽度从左到右排，放不下换行）

/// 简单流式布局，代替按列的网格：子视图保持自然宽度依次排列，超出可用宽度时换行。
struct FlowLayout: Layout {
    /// 水平与垂直间距共用一个值
    var spacing: CGFloat = 10

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = computeRows(proposal: proposal, subviews: subviews)
        let height = rows.isEmpty ? 0
            : rows.reduce(0) { $0 + $1.height } + CGFloat(rows.count - 1) * spacing
        return CGSize(width: rows.map(\.width).max() ?? 0, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in computeRows(proposal: proposal, subviews: subviews) {
            var x = bounds.minX
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y), anchor: .topLeading, proposal: .unspecified)
                x += size.width + spacing
            }
            y += row.height + spacing
        }
    }

    private struct Row {
        var indices: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func computeRows(proposal: ProposedViewSize, subviews: Subviews) -> [Row] {
        let maxWidth = proposal.width ?? .infinity
        var rows: [Row] = []
        var current = Row()
        for (index, view) in subviews.enumerated() {
            let size = view.sizeThatFits(.unspecified)
            let gap = current.indices.isEmpty ? 0 : spacing
            if current.width > 0, current.width + gap + size.width > maxWidth {
                rows.append(current)
                current = Row()
            }
            current.indices.append(index)
            current.width += (current.indices.count == 1 ? 0 : spacing) + size.width
            current.height = max(current.height, size.height)
        }
        if !current.indices.isEmpty { rows.append(current) }
        return rows
    }
}

// MARK: - 单选 radio 行

struct RadioRow: View {
    let symbol: String
    @Binding var isOn: Bool
    /// true 时行尾 Spacer 撑满（网格单元整行可点）；false 时仅内容宽（同一行多个 radio 依次排列）
    var expanding = true
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        Button {
            isOn = true
        } label: {
            HStack(spacing: 10) {
                Image(systemName: isOn ? "largecircle.fill.circle" : "circle")
                    .foregroundColor(isOn ? AppTheme.primary(scheme) : AppTheme.dim(scheme))
                Text(symbol)
                    .font(AppTheme.bodyFont)
                    .foregroundColor(AppTheme.ink(scheme))
                    .lineLimit(1)
                if expanding {
                    Spacer(minLength: 0)
                }
            }
            .frame(minHeight: 30)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }
}

// MARK: - 品种胶囊（单选，无 radio 圆点）

/// 品种胶囊：选中态实心主题色、未选描边；整颗按钮可点，点击切换选中（再点已选中的取消）。
/// label 为显示文本（如「沪铜｜CU」），选中语义仍由外层 symbol 比较决定。
struct SymbolCapsule: View {
    let label: String
    @Binding var isOn: Bool
    @Environment(\.colorScheme) private var scheme
    @State private var hovering = false

    var body: some View {
        Button {
            isOn.toggle()
        } label: {
            Text(label)
                .font(AppTheme.bodyFont.weight(.semibold))
                .lineLimit(1)
                .padding(.horizontal, 14)
                .frame(minHeight: 30)
                .foregroundColor(fgColor)
                .background(bgColor)
                .overlay(
                    RoundedRectangle(cornerRadius: 15)
                        .stroke(borderColor, lineWidth: 1)
                )
                .cornerRadius(15)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .pointerCursor()
    }

    private var fgColor: Color {
        isOn ? .white : AppTheme.ink(scheme)
    }

    private var bgColor: Color {
        if isOn { return hovering ? AppTheme.primaryHover(scheme) : AppTheme.primary(scheme) }
        return hovering ? AppTheme.sidebarHover(scheme) : .clear
    }

    private var borderColor: Color {
        if isOn { return AppTheme.primary(scheme) }
        return hovering ? AppTheme.dim(scheme) : AppTheme.hairline(scheme)
    }
}
