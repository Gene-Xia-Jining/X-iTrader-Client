import SwiftUI

/// 模拟交易页：快期模拟账户 4 步引导卡片（像素级对应 Qt SimulationPage）。
struct SimulationView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    // 表单
    @State private var phone = ""
    @State private var password = ""
    @State private var passwordVisible = false
    // 已存模拟账户 => 后端不回传密码，但会返回「已设置」与长度，用于按真实长度绘制掩码圆点
    @State private var hasStoredPassword = false
    @State private var storedPasswordLength = 0
    // 品种单选（服务器品种；已订阅但不在服务器列表的品种保留显示）
    @State private var selectedSymbol = ""
    @State private var keptSymbols: [String] = []
    // 交易策略单选（服务器策略目录）
    @State private var selectedStrategy = ""
    // 测试连接进行中（按钮置灰）
    @State private var testing = false
    // 步骤 4 / 5 的说明弹窗（原步骤下方的提示行）
    @State private var showSymbolHelp = false
    @State private var symbolHelpHover = false
    @State private var showStrategyHelp = false
    @State private var strategyHelpHover = false
    // 弹窗（对应 Qt QMessageBox）
    @State private var alert: ActiveAlert?

    var body: some View {
        PageContainer {
            AppCard {
                VStack(alignment: .leading, spacing: 12) {
                    guideLabel("在开始实盘交易之前，您应该通过 1-3 个月的模拟交易建立信心。\n\n我们支持快期的模拟交易，您需要：")
                    guideLabel("1，下载快期客户端：")
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

                    HStack(spacing: 6) {
                        guideLabel("4，选择交易品种：")
                        helpButton(symbolHelpText, isPresented: $showSymbolHelp, isHovering: $symbolHelpHover)
                    }
                    symbolPicker

                    HairlineDivider()

                    HStack(spacing: 6) {
                        guideLabel("5，选择交易策略：")
                        helpButton(strategyHelpText, isPresented: $showStrategyHelp, isHovering: $strategyHelpHover)
                    }
                    strategyPicker
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

    /// 20x20 圆形问号按钮，点击弹出说明文案（样式同设置页 helpButton）；步骤 4 / 5 共用
    private func helpButton(
        _ text: String,
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
            // 不指定前景色，由 popover 自身外观决定（同设置页 helpButton）；多行文案定宽换行
            Text(text)
                .font(AppTheme.dimFont)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 300, alignment: .leading)
                .padding(10)
        }
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
        // 已存密码且未输入新密码时，用自绘圆点表示「密码已设置」：
        // placeholder 是浅灰辅助色，看起来像未填写的提示，不像真实密码。
        // 明文模式下若取回密码失败（password 仍为空）也保留圆点，避免误以为密码被清空
        let showMask = hasStoredPassword && password.isEmpty
        return ZStack(alignment: .leading) {
            if showMask { storedPasswordMask }
            // 输入框放在上层且保持透明背景，聚焦时的插入点才能压在圆点之上
            Group {
                if passwordVisible {
                    TextField("", text: $password)
                } else {
                    SecureField("", text: $password)
                }
            }
            .textFieldStyle(.plain)
            .font(AppTheme.bodyFont)
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

    /// 已存密码的自绘掩码：按真实密码长度绘制圆点（后端只回传长度，不回传明文），
    /// 尺寸对齐设计稿——直径 6.5pt、中心间距 9pt
    private var storedPasswordMask: some View {
        let count = storedPasswordLength > 0 ? storedPasswordLength : 10
        return HStack(spacing: 2.5) {
            ForEach(0..<count, id: \.self) { _ in
                Circle()
                    .fill(AppTheme.ink(scheme))
                    .frame(width: 6.5, height: 6.5)
            }
        }
        .allowsHitTesting(false)
    }

    /// 眼睛图标按钮：图标表示点击后的效果（隐藏时睁眼=查看，明文时闭眼=隐藏）
    private var eyeButton: some View {
        Button {
            let nextVisible = !passwordVisible
            if nextVisible && password.isEmpty && hasStoredPassword {
                Task {
                    let simId = appState.accounts.first(where: { $0.kind == "sim" })?.id
                    let savedPwd = await appState.getAccountPassword(accountId: simId, kind: "sim", field: "tq_password")
                    if !savedPwd.isEmpty {
                        password = savedPwd
                    }
                    passwordVisible = true
                }
            } else {
                passwordVisible = nextVisible
            }
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

    // MARK: - 品种区（单选胶囊，对应 Qt SymbolPicker；刷新入口在顶部栏）

    private var symbolPicker: some View {
        VStack(alignment: .leading, spacing: 6) {
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
            // 数量提示已移入步骤 4 问号弹窗，行内只保留获取中 / 失败等瞬时状态
            if !pickerHint.isEmpty {
                Text(pickerHint)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                    .fixedSize(horizontal: false, vertical: true)
            }
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

    /// 品种区状态行：只保留获取中 / 失败 / 尚未获取等瞬时状态，数量提示在问号弹窗内
    private var pickerHint: String {
        if appState.symbolsFetching { return "正在获取品种..." }
        if appState.symbolsMessage.hasPrefix("获取失败") {
            return "获取品种失败: \(appState.symbolsMessage)"
        }
        if !appState.serverSymbols.isEmpty { return "" }
        return "尚未获取品种"
    }

    /// 步骤 4 问号弹窗文案：模拟账户范围说明 + 可选品种数量
    private var symbolHelpText: String {
        let note = "提示：模拟帐户不支持组合/套利和期权交易，仅国内商品和股指国债。"
        guard !appState.serverSymbols.isEmpty else { return note }
        return note + "\n共 \(appState.serverSymbols.count) 个品种，选择一个订阅，选中即生效"
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
            // 数量与提示文案已移入步骤 5 问号弹窗，行内只保留获取中 / 失败等瞬时状态
            if !strategyHint.isEmpty {
                Text(strategyHint)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    /// 策略区状态行：只保留获取中 / 失败 / 尚未获取等瞬时状态，数量提示在问号弹窗内
    private var strategyHint: String {
        if appState.strategiesFetching { return "正在获取策略..." }
        if appState.strategiesMessage.hasPrefix("获取失败") {
            return "获取策略失败: \(appState.strategiesMessage)"
        }
        if !appState.serverStrategies.isEmpty { return "" }
        return "尚未获取策略"
    }

    /// 步骤 5 问号弹窗文案：策略来源说明 + 可选策略数量
    private var strategyHelpText: String {
        let note = "提示：策略由服务器计算交易信号，客户端按信号执行开仓 / 平仓。"
        guard !appState.serverStrategies.isEmpty else { return note }
        return note + "\n共 \(appState.serverStrategies.count) 个策略，选择一个，选中即生效"
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
        guard let sim = appState.accounts.first(where: { $0.kind == "sim" }) else {
            hasStoredPassword = false
            storedPasswordLength = 0
            return
        }
        // 后端不回传密码，但会返回「是否已设置」与长度，用于按真实长度绘制掩码圆点
        hasStoredPassword = sim.hasTqPassword
        storedPasswordLength = sim.tqPasswordLength
        guard phone.isEmpty else { return }
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
        // 新账户必须设置密码；已存且已有密码的账户留空沿用旧密码（后端约定）
        let isNew = !appState.accounts.contains { $0.kind == "sim" }
        guard !password.isEmpty || (!isNew && hasStoredPassword) else {
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
            let newPasswordLength = password.count
            await appState.runAction(method: "accounts.save", params: params)
            if appState.lastActionError.isEmpty {
                alert = .saveSuccess
                if newPasswordLength > 0 {
                    password = ""
                    hasStoredPassword = true
                    storedPasswordLength = newPasswordLength
                }
            } else {
                alert = .inputError(appState.lastActionError)
            }
        }
    }

    private func testConnection() {
        let account = phone.trimmingCharacters(in: .whitespaces)
        guard !account.isEmpty else {
            alert = .inputError("手机号不能为空")
            return
        }
        guard !password.isEmpty || hasStoredPassword else {
            alert = .inputError("密码不能为空")
            return
        }
        testing = true
        let simId = appState.accounts.first(where: { $0.kind == "sim" })?.id
        Task {
            let message = await appState.testTqAuth(account: account, password: password, accountId: simId)
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
/// label 为显示文本（如「CU｜沪铜」），选中语义仍由外层 symbol 比较决定。
/// 文字对齐官网正文：13pt 常规字重（同「天勤官网：」行），未选中沿用深色。
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
                .font(AppTheme.dimFont)
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
