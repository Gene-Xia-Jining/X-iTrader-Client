import SwiftUI

/// 模拟交易页：7 步引导（快速开始）+ 模拟账户的交易记录，两个 tab 切换。
struct SimulationView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    /// 三个 tab：「快速开始」是原来的 7 步引导，「交易记录」是资金、持仓与当日委托，「历史成交」是成交记录
    private enum SimTab: String, CaseIterable {
        case start
        case records
        case trades

        var title: String {
            switch self {
            case .start: return "快速开始"
            case .records: return "交易记录"
            case .trades: return "历史成交"
            }
        }
    }

    /// tab 选择存在 UserDefaults，重启后回到上次查看的那一栏
    private static let tabKey = "simulation.tab"
    @State private var tab: SimTab = SimTab(
        rawValue: UserDefaults.standard.string(forKey: SimulationView.tabKey) ?? ""
    ) ?? .start
    @State private var showTradesHelp = false

    // 表单
    @State private var phone = ""
    @State private var password = ""
    @State private var passwordVisible = false
    /// 眼睛按钮取回的已存明文：按此值回填时不算用户修改，避免失焦重复提交密码
    @State private var revealedPassword = ""
    /// 上一次从后端回填的账号：只有输入框未被编辑时才跟随 accounts 刷新，避免冲掉正在输入的内容
    @State private var lastAccountPhone = ""
    /// 第 3 步自动保存的结果提示（成功 / 失败），空串不显示
    @State private var accountHint = ""
    @State private var accountHasError = false
    @FocusState private var passwordFocused: Bool
    // 已存模拟账户 => 后端不回传密码，但会返回「已设置」与长度，用于按真实长度绘制掩码圆点
    @State private var hasStoredPassword = false
    @State private var storedPasswordLength = 0
    // 服务器地址（与设置页同一份配置；留空时后端已写入默认地址）
    @State private var serverUrl = ""
    /// 上一次从后端回填的地址：只有输入框未被编辑时才跟随配置刷新，避免冲掉正在输入的内容
    @State private var lastConfigServerUrl = ""
    @State private var serverTesting = false
    @State private var showServerHelp = false
    @State private var serverHelpHover = false
    // 品种单选（服务器品种；已订阅但不在服务器列表的品种保留显示）
    @State private var selectedSymbol = ""
    @State private var keptSymbols: [String] = []
    // 交易策略单选（服务器策略目录）
    @State private var selectedStrategy = ""
    // 测试连接进行中（按钮置灰）
    @State private var testing = false
    // 步骤 6 / 7 的说明弹窗（原步骤下方的提示行）
    @State private var showSymbolHelp = false
    @State private var symbolHelpHover = false
    // 步骤 6 的品种选择弹窗
    @State private var showSymbolPicker = false
    /// 弹窗的品类过滤器（全部品种 / 国内商品 / 股指 / 国债）：单选
    @State private var symbolFilter: SymbolFilter = .all
    /// 当前过滤器下的品种胶囊列表实际高度：弹窗据此自适应高度，避免底部留白
    @State private var symbolListHeight: CGFloat = 0
    /// 「全部品种」那一档的列表高度：弹窗顶边以它为准，切换过滤器只收缩下边距
    @State private var symbolMaxListHeight: CGFloat = 0
    // 步骤 5「申请 Token」的说明弹窗 + 申请中的回执
    @State private var showTokenHelp = false
    @State private var tokenHelpHover = false
    @State private var tokenRequesting = false
    /// 本次申请的返回值；空串时提示行改为跟随后端实时状态
    @State private var tokenMessage = ""
    /// 已申请过（待审核 / 有有效 token）时点击胶囊改为弹出申请记录
    @State private var showTokenRecords = false
    // 步骤 1 的下载入口弹窗（手机端 / App Store / Windows 三个链接）
    @State private var showDownloadLinks = false
    // 步骤 3 的账号信息弹窗（手机号 / 密码 / 测试连接）
    @State private var showAccountEditor = false
    /// 步骤 3 胶囊右端问号的说明弹层
    @State private var showAccountHelp = false
    @State private var accountHelpHover = false
    /// 弹窗内「测试连接」的结果：弹层开着时不再叠 alert，就地显示
    @State private var testHint = ""
    @State private var testHintError = false
    // 弹窗（对应 Qt QMessageBox）
    @State private var alert: ActiveAlert?
    // 常见问题展开状态：存在 UserDefaults，重启后保持上次的展开/收起
    private static let faqExpandedKey = "simulation.faqExpanded"
    @State private var faqExpanded = UserDefaults.standard.bool(forKey: SimulationView.faqExpandedKey)
    /// 点开的那条常见问题：非空即弹出详情
    @State private var faqQuestion: FaqItem?

    var body: some View {
        PageContainer {
            tabBar

            if tab == .start {
                startGuide
                faqCard
            } else if tab == .records {
                AccountRecordsView(
                    kind: .sim,
                    mode: .records,
                    notConfiguredHint: "尚未配置模拟账户，请先在「快速开始」里保存账户"
                )
            } else {
                AccountRecordsView(
                    kind: .sim,
                    mode: .trades,
                    notConfiguredHint: "尚未配置模拟账户，请先在「快速开始」里保存账户"
                )
            }
        }
        .onAppear {
            fillFromAccount()
            fillServerUrl()
            // 第 5 步要在胶囊后显示当前审批状态，进页面时拉一次
            appState.refreshTokenStatus()
        }
        .onChange(of: appState.accounts) { _ in fillFromAccount() }
        .onChange(of: appState.config) { _ in fillServerUrl() }
        .onChange(of: showAccountEditor) { presented in
            // 弹窗被 Esc 等非按钮方式关闭时输入框不会失焦，补一次保存
            if !presented { saveAccountIfChanged() }
        }
        .alert(item: $alert) { alert in
            Alert(
                title: Text(alert.title),
                message: Text(alert.message),
                dismissButton: .default(Text("好"))
            )
        }
        .sheet(isPresented: $showAccountEditor) {
            accountEditor
                .onAppear { setTestHint("", isError: false) }
        }
        .sheet(isPresented: $showDownloadLinks) {
            downloadLinks
        }
        .sheet(isPresented: $showTokenRecords) {
            tokenRecordsDialog
        }
        .sheet(item: $faqQuestion) { item in
            faqDetail(item)
        }
        // 步骤 6 用自绘弹层而非 sheet：macOS 的 sheet 点空白处不会关闭
        .overlay {
            if showSymbolPicker {
                symbolPickerOverlay
            }
        }
        .onChange(of: tab) { value in
            UserDefaults.standard.set(value.rawValue, forKey: Self.tabKey)
            // 切到交易记录/历史成交时把品种弹层一并收起，避免遮罩留在记录列表上
            showSymbolPicker = false
        }
    }

    // ---- 顶部 tab（等高胶囊，选中态实心，与侧栏选中态同风格）----

    private var tabBar: some View {
        CapsuleTabBar(
            tabs: SimTab.allCases,
            title: \.title,
            selection: $tab,
            hasTrailing: { $0 == .trades }
        ) { item in
            if item == .trades {
                AppHelpButton(text: tradesHelpText, isPresented: $showTradesHelp, isOn: tab == .trades)
            }
        }
    }

    private let tradesHelpText = """
    数据包含本地 SQLite 历史沉淀成交记录。

    • 本地沉淀：快期账户通常仅保留当日实时成交推送，客户端将每笔成交自动持久化至本地 SQLite 数据库，跨交易日长期留存。
    • 盈亏计算：自动按先进先出原则匹配开平仓，统计单笔平仓盈亏与累计平仓收益。
    • 多维追溯：支持今日、近3天、近7天及全部历史筛选，点击任意成交行可查看合约与订单详细信息。
    """

    // MARK: - 快速开始（7 步引导）

    private var startGuide: some View {
        AppCard {
            VStack(alignment: .leading, spacing: 12) {
                guideLabel("在开始实盘交易之前，您应该通过 1-3 个月的模拟交易建立信心。\n\n我们支持快期的模拟交易，您需要：")

                HStack(spacing: 8) {
                    guideLabel("1，")
                    entryCapsule("下载快期客户端") { showDownloadLinks = true }
                }

                guideLabelWithLink("2，在快期客户端或天勤官网注册：", "https://account.shinnytech.com/")

                HStack(spacing: 8) {
                    guideLabel("3，")
                    accountEntryCapsule
                }

                serverRow

                // 第 5 步：胶囊内直接向服务器申请 Token，结果紧随其后显示
                HStack(spacing: 8) {
                    guideLabel("5，")
                    tokenEntryCapsule
                    if !tokenHint.isEmpty {
                        Text(tokenHint)
                            .font(AppTheme.dimFont)
                            .foregroundColor(tokenHintColor)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                HStack(spacing: 8) {
                    guideLabel("6，")
                    symbolEntryCapsule
                }

                // 标题与策略胶囊同一行：策略多时在标题右侧换行，不再单占一行
                HStack(alignment: .top, spacing: 8) {
                    guideLabel("7，选择交易策略：")
                        .padding(.top, 5)
                    strategyPicker
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(20)
        }
    }

    // MARK: - 常见问题（可展开；展开后只列问题，点问题弹窗看详细）

    /// 每条问答：列表里只显示问题，答案在弹窗里展开
    struct FaqItem: Identifiable {
        let question: String
        let answer: String
        var id: String { question }
    }

    private static let faqItems: [FaqItem] = [
        FaqItem(
            question: "模拟交易和实盘交易有什么区别？",
            answer: "模拟交易用的是快期模拟账户，资金是虚拟的，与快期客户端 / APP / 专业版里的模拟账户互通。"
            + "把回测和模拟当作实盘前的体检，确认下单、平仓、信号接收都正常后再转到实盘。"
        ),
        FaqItem(
            question: "为什么点击「自动交易」没有开启？",
            answer: "开启前客户端会先做一次启动前预检：服务器连通、账户的手机号 / 密码 / 品种 / 策略是否齐全、"
            + "Token 是否已领取、天勤账户能否登录。任一项不通过都会弹出问题清单，按清单逐项处理后重新开启即可。"
        ),
        FaqItem(
            question: "为什么改了配置要重新开启自动交易？",
            answer: "自动交易运行中修改账户、品种或策略不会立即生效，客户端会在日志里提示「下次启动生效」，"
            + "避免正在持仓的账户策略被中途换掉。想让改动生效，先关掉自动交易再重新开启。"
        ),
        FaqItem(
            question: "自动交易关着的时候收到信号会怎样？",
            answer: "信号会照常记录到交易记录里，但不会下单。这些信号不会在重新开启后补做——"
            + "恢复自动交易只对之后新到的信号生效。"
        ),
        FaqItem(
            question: "交易价格是怎么定的？",
            answer: "方向、目标仓位都由服务器决定，客户端只负责执行；成交价用的是客户端实时行情，"
            + "以对手价优先成交，不沿用信号里的参考价。"
        ),
        FaqItem(
            question: "如何选择交易品种？",
            answer: "第 6 步选的是品种（如螺纹钢、甲醇），不是具体合约：具体月份合约由服务器信号给出，"
            + "客户端只按实时行情以对手价成交。一个账户同一时间只能选一个品种。\n\n"
            + "选品种可以按三点筛：\n"
            + "1，权限。模拟账户只支持国内商品和股指 / 国债，不支持组合、套利和期权。"
            + "铁矿石、棕榈油、PTA、豆粕、原油属于特定品种，股指和国债也要单独开通，"
            + "转实盘前要先在期货公司确认权限，否则下单会报权限不足。\n"
            + "2，流动性。看持仓量和盘口深度，不要只看成交量——成交量最大的品种盘口未必厚，滑点会更大。"
            + "螺纹钢、甲醇、玻璃、纯碱、玉米、热卷、白糖、PVC 等持仓量大、盘口厚，适合起步。\n"
            + "3，单手规模。保证金 = 价格 × 合约单位 × 保证金比例。黄金一手保证金约 18–20 万，"
            + "纯碱只要约 2000 元，相差 90 倍：同样手数下风险完全不同，资金不多时先避开黄金、原油、股指这类大合约。\n\n"
            + "建议先用一个自己熟悉、流动性好的普通商品跑顺流程，稳定后再换成其他品种。"
        ),
        FaqItem(
            question: "期货的交易时间是什么时候？",
            answer: "国内期货按北京时间交易，分白盘和夜盘，周末和法定节假日休市。模拟账户的时段与实盘一致。\n\n"
            + "1，商品期货白盘：09:00–10:15、10:30–11:30、13:30–15:00，上午 10:15–10:30 休息。\n"
            + "2，商品期货夜盘：21:00 开始，收盘时间按品种不同——螺纹钢、热卷、甲醇、玻璃、纯碱、白糖、PVC 等 23:00 收；"
            + "铜、铝、锌、镍等 01:00 收；黄金、白银、原油 02:30 收。\n"
            + "3，中金所股指 / 国债：09:30–11:30、13:00–15:00，国债到 15:15，都没有夜盘。\n\n"
            + "休市时段下单不会成交，会被记为执行失败，开盘后不会自动补做。"
            + "鸡蛋、苹果、红枣、硅铁、锰硅等品种没有夜盘，具体时段以交易所公告为准。"
        ),
        FaqItem(
            question: "同一个信号会被重复执行吗？",
            answer: "不会。每条信号在本地 SQLite 里按账户登记，已执行过的信号再次收到时只记一条日志，不会重复下单。"
        ),
        FaqItem(
            question: "关闭客户端后会继续交易吗？",
            answer: "不会。自动交易不会在下次启动时自动开启，需要手动再点一次；"
            + "关闭客户端期间也不会接收信号，重新开启后不会补做关闭期间的信号。"
        ),
        FaqItem(
            question: "出问题的时候去哪里看？",
            answer: "「系统日志」页有完整的运行日志，包含连接、信号、下单与错误信息；"
            + "交易执行明细可以在本页「交易记录」tab 查看模拟账户，实盘账户在「实盘交易」页的「交易记录」tab 查看。"
        ),
    ]

    private var faqCard: some View {
        AppCard {
            VStack(alignment: .leading, spacing: 0) {
                // 整行可点：避免只能点小箭头，标题区域也就成了命中区
                Button {
                    withAnimation(.easeInOut(duration: 0.18)) {
                        faqExpanded.toggle()
                    }
                    UserDefaults.standard.set(faqExpanded, forKey: Self.faqExpandedKey)
                } label: {
                    HStack(spacing: 8) {
                        Text("常见问题")
                            .font(AppTheme.bodyFont.weight(.semibold))
                            .foregroundColor(AppTheme.ink(scheme))
                        Spacer(minLength: 12)
                        Text(faqExpanded ? "收起" : "展开")
                            .font(AppTheme.dimFont)
                            .foregroundColor(AppTheme.primary(scheme))
                        Image(systemName: "chevron.down")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundColor(AppTheme.primary(scheme))
                            .rotationEffect(.degrees(faqExpanded ? 180 : 0))
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .pointerCursor()

                if faqExpanded {
                    VStack(alignment: .leading, spacing: 0) {
                        HairlineDivider()
                            .padding(.bottom, 4)
                        ForEach(Array(Self.faqItems.enumerated()), id: \.offset) { index, item in
                            // 只列问题，点开弹窗看详细：答案较长，平铺会把卡片撑得很高
                            Button {
                                faqQuestion = item
                            } label: {
                                HStack(spacing: 8) {
                                    Text(item.question)
                                        .font(AppTheme.bodyFont)
                                        .foregroundColor(AppTheme.ink(scheme))
                                        .fixedSize(horizontal: false, vertical: true)
                                    Spacer(minLength: 12)
                                    Image(systemName: "chevron.right")
                                        .font(.system(size: 11, weight: .semibold))
                                        .foregroundColor(AppTheme.dim(scheme))
                                }
                                .padding(.vertical, 8)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .pointerCursor()
                            if index < Self.faqItems.count - 1 {
                                HairlineDivider()
                            }
                        }
                    }
                    .padding(.top, 12)
                }
            }
            .padding(20)
        }
    }

    /// 常见问题详情弹窗：问题作标题，答案按正文；「关闭」放在标题行右侧（同其它弹窗）
    private func faqDetail(_ item: FaqItem) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Text(item.question)
                    .font(AppTheme.bodyFont.weight(.semibold))
                    .foregroundColor(AppTheme.ink(scheme))
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 12)
                AppButton("关闭", variant: .secondary, size: .inline) {
                    faqQuestion = nil
                }
            }

            HairlineDivider()

            Text(item.answer)
                .font(AppTheme.bodyFont)
                .foregroundColor(AppTheme.dim(scheme))
                // 答案多为长段落，默认行距偏挤
                .lineSpacing(6)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
        }
        .padding(20)
        .frame(width: 520)
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

    /// 正文后紧跟一条外链：两段拼在同一个 Text 里，行内折行时不会断开成两行
    private func guideLabelWithLink(_ text: String, _ urlString: String) -> some View {
        var plain = AttributedString(text)
        plain.font = AppTheme.bodyFont
        plain.foregroundColor = AppTheme.ink(scheme)
        var link = AttributedString(urlString)
        link.font = AppTheme.bodyFont
        link.link = URL(string: urlString)
        link.foregroundColor = AppTheme.link
        link.underlineStyle = .single
        return Text(plain + link)
            .kerning(-0.1)
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

    /// 20x20 圆形问号按钮，点击弹出说明文案（样式同设置页 helpButton）；步骤 4–7 共用
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

    // MARK: - 入口胶囊（文字按钮 + 可选右侧问号；步骤 1 与步骤 3 共用）

    /// 胶囊整体是描边圆角按钮：点文字区域执行 action，点右侧问号弹出说明。
    private func entryCapsule(
        _ title: String,
        titleColor: Color? = nil,
        helpText: String? = nil,
        isHelpPresented: Binding<Bool>? = nil,
        isHelpHovering: Binding<Bool>? = nil,
        action: @escaping () -> Void
    ) -> some View {
        HStack(spacing: 6) {
            Button(action: action) {
                Text(title)
                    .font(AppTheme.dimFont.weight(.semibold))
                    .foregroundColor(titleColor ?? AppTheme.primary(scheme))
            }
            .buttonStyle(.plain)
            .pointerCursor()

            if let helpText, let isHelpPresented, let isHelpHovering {
                helpButton(helpText, isPresented: isHelpPresented, isHovering: isHelpHovering)
            }
        }
        .padding(.leading, 14)
        .padding(.trailing, helpText == nil ? 14 : 10)
        .frame(minHeight: 30)
        .overlay(
            Capsule().stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
    }

    /// 步骤 3 入口胶囊：点文字打开账号弹窗，点右侧问号看说明。
    /// 手机号与密码都填好后，文案改为当前用户手机号，避免再打开弹窗才知道登录的是谁。
    private var accountEntryCapsule: some View {
        entryCapsule(
            accountEntryLabel,
            titleColor: accountEntryColor,
            helpText: accountHelpText,
            isHelpPresented: $showAccountHelp,
            isHelpHovering: $accountHelpHover
        ) {
            showAccountEditor = true
        }
    }

    /// 手机号与密码都不为空即视为已填好（已存密码时输入框留空，按已设置计算）
    private var hasAccountInfo: Bool {
        let account = phone.trimmingCharacters(in: .whitespaces)
        let hasPassword = !password.isEmpty || hasStoredPassword
        return !account.isEmpty && hasPassword
    }

    private var accountEntryLabel: String {
        guard hasAccountInfo else { return "填入快期/天勤用户信息" }
        return "当前用户：\(phone.trimmingCharacters(in: .whitespaces))"
    }

    /// 只有「测试连接」通过的那组手机号 / 密码还在时才算已验证：
    /// 改了任一项胶囊立刻回到主题色，避免把旧验证结果当成本次凭据有效
    private var accountVerified: Bool {
        guard hasAccountInfo,
              let verified = appState.verifiedTqCredentials else { return false }
        return verified.matches(account: phone, password: comparablePassword)
    }

    /// 眼睛按钮取回的已存明文与输入框留空指的是同一份凭据，
    /// 比较前统一成空串，免得只是点了一下眼睛就把绿色消掉
    private var comparablePassword: String {
        password == revealedPassword ? "" : password
    }

    private var accountEntryColor: Color {
        accountVerified ? AppTheme.success(scheme) : AppTheme.primary(scheme)
    }

    /// 步骤 3 问号弹窗文案：说明这两个字段从哪来、什么时候保存
    private var accountHelpText: String {
        "填写快期（天勤）账号的注册手机号与密码，客户端用它登录模拟交易账户。输入完成即自动保存。"
    }

    // MARK: - 步骤 5 Token 申请胶囊

    /// 第 5 步胶囊的三种状态：可直接申请 / 已提交待审核 / 已有生效 Token
    private enum TokenCapsuleState {
        case notApplied
        case pending
        case active
    }

    /// 已提交未审核、或已有有效 Token 时，胶囊文字改为「Token 管理」并用语义色，
    /// 点击改为弹出申请记录——此时再点「申请」只会重复提交同一用途的申请。
    private var tokenCapsuleState: TokenCapsuleState {
        let status = appState.tokenStatus
        // 后端只在本地确实持有凭据时才报「已通过…已加密保存」；
        // 「已通过，但服务器未返回 token」还没拿到，继续按可申请对待
        if status.contains("已通过") && !status.contains("未返回") { return .active }
        // 旧数据可能没有 token_id，凭据本身仍在，按有效处理
        if appState.activeTokenId != nil { return .active }
        // 待审核的两种文案：「待审核」「已提交申请，等待服务器审核」
        if status.contains("审核") { return .pending }
        // 刚申请完、后端还未推送记录时，状态文案之外的兜底
        if appState.tokenRecords.first?.status == "pending" { return .pending }
        return .notApplied
    }

    /// 步骤 5 入口胶囊：点文字直接向服务器提交申请，点右侧问号看说明。
    private var tokenEntryCapsule: some View {
        entryCapsule(
            tokenCapsuleTitle,
            titleColor: tokenCapsuleColor,
            helpText: tokenHelpText,
            isHelpPresented: $showTokenHelp,
            isHelpHovering: $tokenHelpHover
        ) {
            if tokenRequesting { return }
            if tokenCapsuleState == .notApplied {
                applyToken()
            } else {
                showTokenRecords = true
            }
        }
    }

    private var tokenCapsuleTitle: String {
        if tokenRequesting { return "申请中..." }
        return tokenCapsuleState == .notApplied ? "申请 Token" : "Token 管理"
    }

    private var tokenCapsuleColor: Color {
        guard !tokenRequesting else { return AppTheme.primary(scheme) }
        switch tokenCapsuleState {
        case .notApplied: return AppTheme.primary(scheme)
        case .pending: return AppTheme.warning(scheme)
        case .active: return AppTheme.success(scheme)
        }
    }

    /// 胶囊后的结果提示：只显示失败回执与「已拒绝」这类异常状态。
    /// 待审核 / 已通过已由胶囊文字与颜色表达，不再占一行重复说明。
    private var tokenHint: String {
        if tokenMessage.contains("失败") { return tokenMessage }
        let status = appState.tokenStatus
        if status.contains("审核") || status.contains("已通过") { return "" }
        if status.isEmpty || status == "未申请" { return "" }
        return tokenMessage.isEmpty ? status : tokenMessage
    }

    private var tokenHintColor: Color {
        let text = tokenHint
        if text.contains("失败") || text.contains("拒绝") || text.contains("错误") {
            return AppTheme.statusRed
        }
        if text.contains("已通过") { return AppTheme.statusGreen }
        return AppTheme.dim(scheme)
    }

    /// 走与后端 Token 申请相同的入口：说明留默认值，提交后由后端事件刷新状态
    private func applyToken() {
        guard !tokenRequesting else { return }
        tokenRequesting = true
        tokenMessage = ""
        Task {
            tokenMessage = await appState.requestTokenResult(description: "iTrader 智能交易系统")
            tokenRequesting = false
        }
    }

    // MARK: - 步骤 5 Token 申请记录弹窗

    private static let tokenTimeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f
    }()

    /// 申请记录弹窗：已申请过（待审核 / 已通过）时点胶囊打开，只读展示进度。
    private var tokenRecordsDialog: some View {
        VStack(alignment: .leading, spacing: 12) {
            // 标题行右侧就放刷新与关闭：记录列表占满剩余高度，底部不再单独留一行按钮
            HStack(spacing: 10) {
                Text("Token 申请记录")
                    .font(AppTheme.bodyFont.weight(.semibold))
                    .foregroundColor(AppTheme.ink(scheme))
                Spacer(minLength: 12)
                AppButton("刷新状态", variant: .secondary, size: .inline) {
                    appState.refreshTokenStatus()
                }
                AppButton("关闭", variant: .secondary, size: .inline) {
                    showTokenRecords = false
                }
            }

            Text(appState.tokenStatus.isEmpty ? "未申请" : appState.tokenStatus)
                .font(AppTheme.dimFont.weight(.semibold))
                .foregroundColor(tokenStatusColor)

            HairlineDivider()

            if appState.tokenRecords.isEmpty {
                Text("暂无申请记录")
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(appState.tokenRecords.enumerated()), id: \.element.id) { index, record in
                            if index > 0 { HairlineDivider() }
                            // 记录按申请时间倒序（最新在前），正序编号才是「历史第几笔」
                            tokenRecordRow(
                                index: appState.tokenRecords.count - index,
                                record: record
                            )
                        }
                    }
                }
                // 记录多时弹窗内滚动，避免窗口被撑高
                .frame(maxHeight: 320)
            }
        }
        .padding(20)
        .frame(width: 520)
        .onAppear { appState.refreshTokenStatus() }
    }

    private func tokenRecordRow(index: Int, record: TokenRecord) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text("\(index)")
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                // 编号是两位以上时不再固定宽度，避免被截断
                .frame(minWidth: 24, alignment: .trailing)
            VStack(alignment: .leading, spacing: 4) {
                Text(record.description.isEmpty ? "（无说明）" : record.description)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.ink(scheme))
                    .fixedSize(horizontal: false, vertical: true)
                Text(tokenRecordTimeLine(record))
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(record.statusText)
                .font(AppTheme.dimFont.weight(.semibold))
                .foregroundColor(tokenRecordColor(record.status))
        }
        .padding(.vertical, 10)
    }

    /// 记录行的时间行：缺字段时用占位符，避免出现 1970 年这种假时间
    private func tokenRecordTimeLine(_ record: TokenRecord) -> String {
        let created = record.createdAt.map { Self.tokenTimeFormatter.string(from: $0) } ?? "—"
        guard let expiresAt = record.expiresAt else { return "申请 \(created)" }
        return "申请 \(created)，过期 \(Self.tokenTimeFormatter.string(from: expiresAt))"
    }

    private func tokenRecordColor(_ status: String) -> Color {
        switch status {
        case "approved": return AppTheme.success(scheme)
        case "rejected": return AppTheme.danger(scheme)
        default: return AppTheme.warning(scheme)
        }
    }

    /// 状态语义色与「Token 管理」页 statusColor 一致
    private var tokenStatusColor: Color {
        let text = appState.tokenStatus
        if text.isEmpty || text == "未申请" { return AppTheme.warning(scheme) }
        if text.contains("已通过") { return AppTheme.success(scheme) }
        if text.contains("失败") || text.contains("错误") { return AppTheme.danger(scheme) }
        return AppTheme.warning(scheme)
    }

    // MARK: - 步骤 1 下载入口弹窗（手机端 / App Store / Windows）

    private var downloadLinks: some View {
        VStack(alignment: .leading, spacing: 12) {
            // 「关闭」放在标题行右侧，与 Token 申请记录弹窗一致，底部不再单独留按钮行
            HStack(spacing: 10) {
                Text("下载快期客户端")
                    .font(AppTheme.bodyFont.weight(.semibold))
                    .foregroundColor(AppTheme.ink(scheme))
                Spacer(minLength: 12)
                AppButton("关闭", variant: .secondary, size: .inline) {
                    showDownloadLinks = false
                }
            }

            HairlineDivider()

            VStack(alignment: .leading, spacing: 6) {
                linkRow("手机端：", "https://www.shinnytech.com/products/app")
                // App Store 是手机端 iOS 渠道的子项，保留 Qt 版的 4 空格缩进
                linkRow("    App Store：", "https://itunes.apple.com/us/app/快期小q/id1187762307?l=zh&ls=1&mt=8")
                linkRow("Windows：", "https://www.shinnytech.com/products/q73")
            }
        }
        .padding(20)
        // 比账号弹窗略宽：App Store 那行链接很长，480 宽会折行
        .frame(width: 580)
    }

    // MARK: - 账号信息弹窗（手机号 / 密码 / 眼睛 / 测试连接；输入完成即保存）

    private var accountEditor: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("填入快期/天勤用户信息")
                .font(AppTheme.bodyFont.weight(.semibold))
                .foregroundColor(AppTheme.ink(scheme))

            HairlineDivider()

            HStack(spacing: 8) {
                fieldLabel("手机号：")
                AppTextField(placeholder: "手机号", text: $phone, onCommit: saveAccountIfChanged)
            }
            HStack(spacing: 8) {
                fieldLabel("密码：")
                passwordField
                eyeButton
            }
            if !accountHint.isEmpty {
                Text(accountHint)
                    .font(AppTheme.bodyFont)
                    .foregroundColor(accountHasError ? Color(hex: 0xE3342F) : Color(hex: 0x34C759))
                    .fixedSize(horizontal: false, vertical: true)
            }

            // 底部一行：测试结果靠左，测试连接紧挨关闭之前（避免结果把按钮行推高）
            HStack(alignment: .center, spacing: 8) {
                if !testHint.isEmpty {
                    Text(testHint)
                        .font(AppTheme.bodyFont)
                        .foregroundColor(testHintError ? Color(hex: 0xE3342F) : Color(hex: 0x34C759))
                        .fixedSize(horizontal: false, vertical: true)
                        // 长错误文案先占空间换行，不去挤右侧两个按钮
                        .layoutPriority(1)
                }
                Spacer(minLength: 12)
                AppButton(testing ? "测试中..." : "测试连接", variant: .secondary, size: .inline, disabled: testing) {
                    testConnection()
                }
                AppButton("关闭", variant: .secondary, size: .inline) {
                    // 保存由 onChange(of: showAccountEditor) 兜底，这里不重复提交
                    showAccountEditor = false
                }
            }
            .padding(.top, 4)
        }
        .padding(20)
        .frame(width: 480)
    }

    /// 弹窗内字段标签：固定宽度右对齐，让两个输入框左边界对齐
    private func fieldLabel(_ text: String) -> some View {
        Text(text)
            .font(AppTheme.bodyFont)
            .foregroundColor(AppTheme.ink(scheme))
            .frame(width: 62, alignment: .trailing)
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
            .focused($passwordFocused)
            .onSubmit { saveAccountIfChanged() }
        }
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity, minHeight: 36)
        .background(AppTheme.inputBg(scheme))
        .cornerRadius(8)
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
        .onChange(of: passwordFocused) { focused in
            // 失焦同样视为输入完成
            if !focused { saveAccountIfChanged() }
        }
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
                        revealedPassword = savedPwd
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

    // MARK: - 服务器地址行（标题 + 输入框 + 测试连接 + 问号同排；结果与手机号一致走弹窗；输入完成即保存）

    private var serverRow: some View {
        HStack(spacing: 8) {
            guideLabel("4，服务器地址：")
                .layoutPriority(1)
            AppTextField(
                placeholder: Self.serverPlaceholder,
                text: $serverUrl,
                onCommit: saveServerIfChanged
            )
            .frame(width: serverFieldWidth)
            AppButton(
                serverTesting ? "测试中..." : "测试连接",
                variant: .secondary,
                size: .inline,
                disabled: serverTesting
            ) {
                testServer()
            }
            helpButton(serverHelpText, isPresented: $showServerHelp, isHovering: $serverHelpHover)
        }
    }

    /// 地址输入框的占位文案；同时作为输入框最小宽度的度量基准
    private static let serverPlaceholder = "https://itrader.redboil.us.ci"

    /// 输入框宽度按当前内容自适应：AppTextField 内部是 maxWidth 撑满，
    /// 这里显式给宽度才能让它随输入增长。上下限防止空值时过窄、
    /// 长地址把同行的「测试连接」和问号挤出卡片。
    private var serverFieldWidth: CGFloat {
        let font = NSFont.systemFont(ofSize: 15)
        let text = serverUrl.isEmpty ? Self.serverPlaceholder : serverUrl
        let textWidth = (text as NSString).size(withAttributes: [.font: font]).width
        // +16 吸收 SwiftUI 与 NSFont 的字距差异并留出光标位置，+24 是控件左右各 12 的内边距
        return min(ceil(textWidth) + 16 + 24, 420)
    }

    /// 步骤 4 问号弹窗文案：说明地址与顶栏服务器弹窗共用同一份配置
    private var serverHelpText: String {
        "这是 iTrader 智能交易服务器的地址，与顶栏「服务器状态」弹窗是同一份配置，输入完成（回车或点到别处）即时保存生效。"
    }

    /// 用配置回填服务器地址；输入框已被编辑（与上次回填值不同）时不覆盖
    private func fillServerUrl() {
        let current = appState.config.serverUrl
        if serverUrl.isEmpty || serverUrl == lastConfigServerUrl {
            serverUrl = current
        }
        lastConfigServerUrl = current
    }

    /// 输入完成即保存（回车或失焦）：与上次回填/提交的地址相同则不重复提交，空值视为放弃修改
    private func saveServerIfChanged() {
        let url = serverUrl.trimmingCharacters(in: .whitespaces)
        guard !url.isEmpty else {
            // 空值不落库，回退到仍在生效的地址
            serverUrl = appState.config.serverUrl
            lastConfigServerUrl = serverUrl
            return
        }
        serverUrl = url
        guard url != lastConfigServerUrl else { return }
        lastConfigServerUrl = url
        appState.saveConfig(serverUrl: url, proxyUrl: nil)
    }

    private func testServer() {
        let url = serverUrl.trimmingCharacters(in: .whitespaces)
        guard !url.isEmpty else {
            alert = .inputError("服务器地址不能为空")
            return
        }
        serverTesting = true
        Task {
            let message = await appState.testServer(url: url)
            serverTesting = false
            alert = .testResult(message: message)
        }
    }

    /// 步骤 5 问号弹窗文案：说明 Token 与服务器鉴权的关系
    private var tokenHelpText: String {
        "客户端连接服务器需要有效的 Token。点「申请 Token」直接向服务器提交申请，"
        + "等待审批通过后客户端会自动领取并保存；已提交或已通过后，点「Token 管理」查看申请记录。"
    }

    // MARK: - 步骤 6 品种入口胶囊与选择弹窗

    /// 步骤 6 入口胶囊：点文字打开品种选择弹窗，点右侧问号看说明。
    /// 默认未选中显示「选择交易品种」；选中后显示已选品种，避免必须打开弹窗才知道当前订阅。
    private var symbolEntryCapsule: some View {
        entryCapsule(
            symbolEntryLabel,
            helpText: symbolHelpText,
            isHelpPresented: $showSymbolHelp,
            isHelpHovering: $symbolHelpHover
        ) {
            showSymbolPicker = true
        }
    }

    /// 选中后与弹窗里的品种胶囊一致，显示「代码｜中文」（如「RB｜螺纹钢」）
    private var symbolEntryLabel: String {
        guard !selectedSymbol.isEmpty else { return "选择交易品种" }
        return "已选择交易品种：\(appState.displaySymbol(selectedSymbol))"
    }

    /// 品种选择弹层：选中即生效并关闭；点遮罩空白处也能关闭。
    /// 顶部一行左侧是品类过滤器（全部品种 / 国内商品 / 股指 / 国债，单选）、右侧是「关闭」。
    /// 顶边按品种最多的「全部品种」那一档固定，切换过滤器只让卡片底边上移
    private func symbolPickerDialog(maxListHeight: CGFloat) -> some View {
        let cardListHeight = min(max(symbolMaxListHeight, 31), maxListHeight)
        return ZStack(alignment: .top) {
            // 幽灵卡片：始终按全部品种排版，只用来量出最高一档的列表高度，不接收点击
            symbolPickerCard(
                symbols: simAllSymbols,
                listHeight: cardListHeight,
                onMeasure: { symbolMaxListHeight = $0 }
            )
            .opacity(0)
            .allowsHitTesting(false)

            // 真卡片按自己这一档的高度收，品种少的过滤器底边随之抬升，不留空行
            symbolPickerCard(
                symbols: filteredSymbols,
                listHeight: min(max(symbolListHeight, 31), maxListHeight),
                onMeasure: { symbolListHeight = $0 }
            )
        }
        .frame(width: 640)
    }

    /// 一张品种选择卡片；listHeight 是列表区高度，onMeasure 回传列表内容的真实高度。
    /// 卡片外观画在这里而不是外层容器上，否则会按幽灵卡片的高度铺满整块
    private func symbolPickerCard(
        symbols: [String],
        listHeight: CGFloat,
        onMeasure: @escaping (CGFloat) -> Void
    ) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                symbolFilterCapsules
                Spacer(minLength: 12)
                AppButton("关闭", variant: .secondary, size: .inline) {
                    showSymbolPicker = false
                }
            }

            // 顶栏与品种列表之间的分隔线
            HairlineDivider()

            ScrollView {
                symbolPicker(symbols)
                    // 量的是换行后的真实内容高度，用它决定列表区高度
                    .background(GeometryReader { proxy in
                        Color.clear.preference(
                            key: SymbolListHeightKey.self,
                            value: proxy.size.height
                        )
                    })
            }
            // 弹层按内容的理想高度收缩，而 ScrollView 的理想高度极小，
            // 因此必须给出确定高度；上限由遮罩层按窗口可用高度算出来
            .frame(height: listHeight)
            .onPreferenceChange(SymbolListHeightKey.self) { onMeasure($0) }
        }
        .padding(20)
        .background(AppTheme.card(scheme))
        .cornerRadius(14)
        .overlay(
            RoundedRectangle(cornerRadius: 14)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
        .shadow(color: .black.opacity(scheme == .dark ? 0.5 : 0.18), radius: 24, y: 10)
        // 卡片整块吃掉点击：否则点在标题、留白处会穿透到遮罩被当成「点空白」
        .contentShape(RoundedRectangle(cornerRadius: 14))
        .onTapGesture {}
    }

    /// 品类过滤器胶囊：单选，点哪颗就只看哪一类，「全部品种」是不过滤
    private var symbolFilterCapsules: some View {
        ForEach(SymbolFilter.allCases) { filter in
            let isOn = symbolFilter == filter
            Button {
                symbolFilter = filter
            } label: {
                Text("\(filter.title) \(filter.count(in: simAllSymbols))")
                    .font(AppTheme.dimFont.weight(isOn ? .semibold : .regular))
                    .padding(.horizontal, 14)
                    .frame(minHeight: 30)
                    .foregroundColor(isOn ? .white : AppTheme.ink(scheme))
                    .background(isOn ? AppTheme.primary(scheme) : Color.clear)
                    .overlay(
                        RoundedRectangle(cornerRadius: 15)
                            .stroke(isOn ? AppTheme.primary(scheme) : AppTheme.hairline(scheme), lineWidth: 1)
                    )
                    .cornerRadius(15)
            }
            .buttonStyle(.plain)
            .pointerCursor()
        }
    }

    /// 步骤 6 的弹层遮罩：点卡片外的空白处关闭（macOS 的 sheet 不具备这个行为）
    private var symbolPickerOverlay: some View {
        GeometryReader { proxy in
            // 列表上限 = 窗口高度扣掉遮罩边距（48）与卡片固定部分（101）
            let maxListHeight = max(proxy.size.height - 149, 120)
            // 顶边按「全部品种」那张最高的卡片居中算，切换过滤器时卡片从同一条顶边往下收
            let tallest = min(max(symbolMaxListHeight, 31), maxListHeight)
            let topInset = max((proxy.size.height - (tallest + 101)) / 2, 24)

            ZStack(alignment: .top) {
                // 用 Button 而不是 onTapGesture：遮罩盖在 ScrollView 上，
                // 手势会被滚动视图吞掉，Button 的命中区域才稳定
                Button { showSymbolPicker = false } label: {
                    Color.black.opacity(scheme == .dark ? 0.45 : 0.2)
                }
                .buttonStyle(.plain)
                .frame(maxWidth: .infinity, maxHeight: .infinity)

                symbolPickerDialog(maxListHeight: maxListHeight)
                    .padding(.top, topInset)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    // MARK: - 品种区（单选胶囊，对应 Qt SymbolPicker；刷新入口在顶部栏）

    /// 品种区（单选胶囊）：symbols 由调用方给，弹窗里按过滤器传筛选后的列表，
    /// 幽灵卡片传全部品种
    private func symbolPicker(_ symbols: [String]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            FlowLayout(spacing: 10) {
                ForEach(symbols, id: \.self) { symbol in
                    SymbolCapsule(label: appState.displaySymbol(symbol), isOn: Binding(
                        get: { selectedSymbol == symbol },
                        set: { isOn in
                            selectedSymbol = isOn ? symbol : ""
                            saveSymbolSelection()
                            // 选中即生效：选完直接关掉弹窗（取消选中留给再次打开后取消）
                            if isOn { showSymbolPicker = false }
                        }
                    ))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            // 数量提示已移入步骤 6 问号弹窗，行内只保留获取中 / 失败等瞬时状态
            if !pickerHint.isEmpty {
                Text(pickerHint)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                    .fixedSize(horizontal: false, vertical: true)
            } else if symbols.isEmpty {
                Text("该分类下暂无可选品种")
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
            }
        }
    }

    /// 按品类过滤器筛出的品种（单选，默认「全部品种」不过滤）
    private var filteredSymbols: [String] {
        simAllSymbols.filter { symbolFilter.matches($0) }
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

    /// 步骤 6 问号弹窗文案：模拟账户范围说明 + 可选品种数量
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
            // 数量与提示文案已移入步骤 7 问号弹窗，行内只保留获取中 / 失败等瞬时状态
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
    /// 手机号在未被编辑时跟随 accounts 刷新，避免覆盖正在输入的内容
    private func fillFromAccount() {
        guard let sim = appState.accounts.first(where: { $0.kind == "sim" }) else {
            hasStoredPassword = false
            storedPasswordLength = 0
            return
        }
        // 后端不回传密码，但会返回「是否已设置」与长度，用于按真实长度绘制掩码圆点
        hasStoredPassword = sim.hasTqPassword
        storedPasswordLength = sim.tqPasswordLength
        if phone.isEmpty || phone == lastAccountPhone {
            phone = sim.tqAccount
        }
        lastAccountPhone = sim.tqAccount
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

    /// 输入完成即保存（回车或失焦）：手机号/密码任一有变化就提交。
    /// 信息还不完整时静默跳过（不打断输入），失败才在行下方提示。
    private func saveAccountIfChanged() {
        let account = phone.trimmingCharacters(in: .whitespaces)
        if phone != account { phone = account }
        guard !account.isEmpty else {
            accountHint = ""
            return
        }

        // 眼睛按钮取回的已存明文不算用户修改，避免失焦时把同一密码再提交一遍
        let newPassword = password == revealedPassword ? "" : password
        let isNew = !appState.accounts.contains { $0.kind == "sim" }
        // 新账户必须设置密码；已存且已有密码的账户留空沿用旧密码（后端约定）
        guard !newPassword.isEmpty || (!isNew && hasStoredPassword) else {
            accountHint = ""
            return
        }

        let sim = appState.accounts.first(where: { $0.kind == "sim" })
        guard sim?.tqAccount != account || !newPassword.isEmpty else {
            accountHint = ""
            return
        }

        var params: [String: Any] = [
            "kind": "sim",
            "label": "模拟账户",
            "tq_account": account,
        ]
        if let sim {
            params["account_id"] = sim.id
        }
        if !newPassword.isEmpty { params["tq_password"] = newPassword }
        // 账户尚未建立时品种/策略只在本地选中，随首次保存一起提交
        if hasOptions && !selectedSymbol.isEmpty {
            params["symbols"] = [selectedSymbol]
        }
        if !selectedStrategy.isEmpty {
            params["strategy"] = selectedStrategy
        }

        Task {
            let newPasswordLength = newPassword.count
            await appState.runAction(method: "accounts.save", params: params)
            if appState.lastActionError.isEmpty {
                accountHint = "已保存"
                accountHasError = false
                if newPasswordLength > 0 {
                    password = ""
                    revealedPassword = ""
                    hasStoredPassword = true
                    storedPasswordLength = newPasswordLength
                    // 输入框已清空、改为沿用已存密码：落库的是刚验证过的那份就继续保持绿色
                    appState.settleVerifiedTqCredentials(afterSavingPassword: newPassword, for: account)
                }
            } else {
                accountHint = appState.lastActionError
                accountHasError = true
            }
        }
    }

    private func testConnection() {
        let account = phone.trimmingCharacters(in: .whitespaces)
        guard !account.isEmpty else {
            setTestHint("手机号不能为空", isError: true)
            return
        }
        guard !password.isEmpty || hasStoredPassword else {
            setTestHint("密码不能为空", isError: true)
            return
        }
        testing = true
        setTestHint("", isError: false)
        let simId = appState.accounts.first(where: { $0.kind == "sim" })?.id
        Task {
            let message = await appState.testTqAuth(account: account, password: password, accountId: simId)
            testing = false
            // 弹层开着时不再叠 alert，结果就地显示在弹窗里；
            // 验证通过的凭据由 AppState 记下，胶囊文字据此变绿
            setTestHint(message, isError: message.hasPrefix("连接失败"))
        }
    }

    private func setTestHint(_ message: String, isError: Bool) {
        testHint = message
        testHintError = isError
    }
}

// MARK: - 品种分类与过滤器

/// 品种分类：模拟账户只支持国内商品与中金所股指 / 国债。
/// 服务器只给品种代码与中文名、没有分类字段，因此按代码判断——
/// 中金所里 IF/IH/IC/IM 是股指，T/TF/TS/TL 是国债，其余都归国内商品。
enum SymbolCategory {
    case commodity
    case index
    case bond

    private static let indexCodes: Set<String> = ["IF", "IH", "IC", "IM"]
    private static let bondCodes: Set<String> = ["T", "TF", "TS", "TL"]

    /// 完整合约（SHFE.au2510）取品种码；裸品种码（RB）直接查表。空串归国内商品
    static func of(_ symbol: String) -> SymbolCategory {
        let code = symbol.split(separator: ".").last.map(String.init) ?? symbol
        let product = String(code.prefix { $0.isLetter }).uppercased()
        if indexCodes.contains(product) { return .index }
        if bondCodes.contains(product) { return .bond }
        return .commodity
    }
}

/// 弹窗顶部的过滤器：「全部品种」+ 三个品类，单选
enum SymbolFilter: String, CaseIterable, Identifiable {
    case all
    case commodity
    case index
    case bond

    var id: String { rawValue }

    var title: String {
        switch self {
        case .all: return "全部品种"
        case .commodity: return "国内商品"
        case .index: return "股指"
        case .bond: return "国债"
        }
    }

    var category: SymbolCategory? {
        switch self {
        case .all: return nil
        case .commodity: return .commodity
        case .index: return .index
        case .bond: return .bond
        }
    }

    func matches(_ symbol: String) -> Bool {
        guard let category else { return true }
        return SymbolCategory.of(symbol) == category
    }

    func count(in symbols: [String]) -> Int {
        symbols.filter { matches($0) }.count
    }
}

// MARK: - 品种列表高度（弹窗自适应用）

/// 收集换行后品种胶囊列表的真实高度
private struct SymbolListHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

// MARK: - 弹窗（对应 Qt QMessageBox）

enum ActiveAlert: Identifiable {
    case inputError(String)
    case testResult(message: String)
    case accountSaved

    var id: String {
        switch self {
        case .inputError(let msg): return "error-\(msg)"
        case .testResult(let msg): return "test-\(msg)"
        case .accountSaved: return "account-saved"
        }
    }

    var title: String {
        switch self {
        case .inputError: return "输入错误"
        case .testResult: return "测试连接"
        case .accountSaved: return "保存成功"
        }
    }

    var message: String {
        switch self {
        case .inputError(let msg): return msg
        case .testResult(let msg): return msg
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
