import SwiftUI

/// 仪表盘卡片高度偏好键，用于测量卡片高度并适配占位框
private struct DashboardCardHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 180
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        let next = nextValue()
        if next > 0 {
            value = next
        }
    }
}

/// 仪表盘：模拟 / 实盘两张账户汇总卡片，单列纵向排列，可拖动互换顺序并在拖动时展示占位框。
struct DashboardView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    @State private var order = DashboardCard.loadOrder()
    @State private var summaries: [DashboardCard: DashboardSummary] = [:]
    @State private var loading = false
    @State private var errorText = ""
    /// 请求代号：账户变化会立即重发，旧的返回结果不能覆盖新数据
    @State private var requestSeq = 0

    /// 当前被拖拽的卡片
    @State private var dragCard: DashboardCard?
    /// 当前拖动手势的纵向位移
    @State private var dragOffsetY: CGFloat = 0
    /// 拖动是否越过互换阈值（动态让位与落点占位展示）
    @State private var isSwappedInHover = false
    /// 卡片实际渲染高度（默认 180）
    @State private var cardHeight: CGFloat = 180
    /// 自动交易胶囊悬停状态
    @State private var pillHovering: [DashboardCard: Bool] = [:]

    private let cardSpacing: CGFloat = 16
    private var slotDistance: CGFloat { cardHeight + cardSpacing }

    var body: some View {
        PageContainer {
            VStack(spacing: cardSpacing) {
                ForEach(Array(order.enumerated()), id: \.element.id) { index, card in
                    cardSlot(card: card, index: index)
                }
            }
            .onPreferenceChange(DashboardCardHeightKey.self) { height in
                if height > 50 && abs(cardHeight - height) > 1 && dragCard == nil {
                    cardHeight = height
                }
            }

            if !errorText.isEmpty {
                Text("账户数据读取失败: \(errorText)")
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.danger(scheme))
                    .fixedSize(horizontal: false, vertical: true)
            }

            FaqCardView(items: Self.faqItems, expandedKey: Self.faqExpandedKey)
        }
        .onAppear(perform: load)
        .onChange(of: appState.accounts) { _ in load() }
        .onChange(of: appState.connState) { state in
            if state == .connected { load() }
        }
        .onReceive(appState.$recordsRevision) { _ in
            syncFromCache()
        }
    }

    // MARK: - 常见问题

    /// 展开状态单独存键：模拟页那份是 simulation.faqExpanded，两页互不干扰
    private static let faqExpandedKey = "dashboard.faqExpanded"

    private static let faqItems: [FaqCardView.Item] = [
        FaqCardView.Item(
            question: "快期免费版和专业版有什么区别？",
            answer: "快期是交易终端，「免费 / 收费」由你拿到的安装包决定，而不是账户分两套：\n\n"
            + "1，单期货公司版本。由期货公司官网提供，只能登录同一家期货公司的账号，可免费使用，"
            + "基础的行情、下单、持仓与结算功能都有。\n"
            + "2，全期货公司版本（快期专业版）。由信易科技官网提供，收费产品，注册快期账户后支持 15 天免费试用。\n\n"
            + "专业版多出来的主要是面向专业交易者的功能：\n"
            + "1，多账户、多柜台同时登录（CTP、易盛、融航、杰宜斯、恒生等）。\n"
            + "2，智能交易：大单拆分算法下单、移仓换月、自定义组合、篮子下单。\n"
            + "3，风险管理：多账户风险预警、实控人分组、电话 / 邮件通知。\n"
            + "4，数据服务：历史 tick 与任意周期 K 线下载、基本面数据，以及本地 API 与天勤 Python API 取数。\n\n"
            + "价格以信易科技官网为准（专业版年费含快期专业版 + 天勤量化专业版两个产品）。"
            + "本客户端只做单账户自动交易，不需要多账户、算法下单这些专业功能，免费版即可跑通。"
        ),
        FaqCardView.Item(
            question: "天勤免费版和专业版有什么区别？",
            answer: "天勤量化（TqSdk）是 Python 行情与交易库，大部分功能免费；本客户端就用它来登录行情、下单和读取账户。\n\n"
            + "1，实盘期货公司范围。免费版目前只支持 3 家期货公司：宏源期货、徽商期货、银河期货"
            + "（即「快速开始」里「天勤免费版」那一组）；专业版可选范围大得多，同一家公司还常有多个席位可选。\n"
            + "2，账户数。免费版和专业版都支持一个快期账户绑定 3 个实盘账户，要更多需另外购买。\n"
            + "3，行情。免费版已提供全部期货、商品期权和中金所期权行情；专业版额外提供 A 股股票行情、"
            + "更稳定的行情服务器（免费版先升级、观察后再轮到专业版）。\n"
            + "4，数据与工具。专业版才有 DataDownloader 历史数据下载（tick 级与任意周期 K 线）、"
            + "EDB 数据服务（库存、现货、宏观等基本面指标）和股票模拟交易。\n"
            + "5，服务。专业版年费用户有专属讨论组，工作时间优先答疑。\n"
            + "6，价格。专业版 14888 元/年（原价 19888 元/年），企业版 30000 元/年（原价 50000 元/年），免费版免费；"
            + "具体以信易科技官网的购买页为准。\n\n"
            + "对使用本客户端的意义：单账户、指定名单内期货公司、期货品种自动交易，免费版完全够用；"
            + "只有当你所在的期货公司不在免费名单里，或需要历史数据做回测时，才需要专业版。"
        ),
        FaqCardView.Item(
            question: "天勤和快期是什么关系？",
            answer: "两者同属信易科技，是同一套账户体系下的两个产品：快期是给人看盘下单的交易终端"
            + "（客户端 / APP / 专业版），天勤是给程序调用的量化接口（TqSdk，Python 库，负责行情、数据与交易）。\n\n"
            + "1，账户通用。同一个手机号在快期客户端或天勤官网注册的账号，既能登录快期终端，"
            + "也能给天勤 TqSdk 做认证；本客户端填的「快期账号 / 快期密码」就是它。\n"
            + "2，权限只认账号。能登录哪些期货公司由账号本身决定，快期终端和天勤 TqSdk 认的是同一份权限，"
            + "不会各算一套；天勤侧的专属能力（如历史数据下载、股票模拟交易）则只看天勤订阅的版本。\n"
            + "3，模拟账户互通。天勤的模拟账户（TqKq）就是快期模拟账户，本客户端里的模拟下单"
            + "会同步出现在快期客户端和手机 APP 的持仓与成交里。\n"
            + "4，分工不同。本客户端用天勤 TqSdk 在后台登录行情、按信号调仓、读取账户数据；"
            + "你可以随时打开快期终端核对同一个账户的持仓和成交。\n"
            + "5，购买联动。天勤专业版按年订阅，是一份套餐，同时包含等时长的快期专业版客户端权限；"
            + "所以付费之后两边都能用，不需要再单独买一次快期。\n\n"
            + "简单记：天勤是给程序用的接口，快期是给人用的终端，背后的账户和资金是同一份。"
        ),
    ]

    // MARK: - 卡片槽位与拖动占位

    private func cardSlot(card: DashboardCard, index: Int) -> some View {
        let isDragging = dragCard == card
        return ZStack(alignment: .topLeading) {
            // 底层：占位框（当该卡片被提起拖动时显现，指示目标落点）
            if isDragging {
                placeholderView
                    .offset(y: placeholderOffset(forIndex: index))
            }

            // 上层：实际卡片
            summaryCard(card, isDragging: isDragging)
                .offset(y: cardOffset(forCard: card, index: index))
                .zIndex(isDragging ? 10 : 1)
                .contentShape(RoundedRectangle(cornerRadius: 18))
                .gesture(cardDragGesture(for: card, index: index))
                .onHover { hovering in
                    if hovering && dragCard == nil {
                        NSCursor.openHand.push()
                    } else {
                        NSCursor.pop()
                    }
                }
        }
    }

    /// 拖动中展示的虚线占位框
    private var placeholderView: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 18)
                .strokeBorder(
                    AppTheme.primary(scheme).opacity(0.45),
                    style: StrokeStyle(lineWidth: 1.5, dash: [6, 4])
                )
                .background(
                    RoundedRectangle(cornerRadius: 18)
                        .fill(AppTheme.primary(scheme).opacity(0.04))
                )

            HStack(spacing: 6) {
                Image(systemName: "arrow.up.and.down")
                    .font(.system(size: 13, weight: .medium))
                Text("放置于此处")
                    .font(AppTheme.dimFont)
            }
            .foregroundColor(AppTheme.primary(scheme).opacity(0.65))
        }
        .frame(height: cardHeight)
        .frame(maxWidth: .infinity)
    }

    /// 真实卡片的垂直位移
    private func cardOffset(forCard card: DashboardCard, index: Int) -> CGFloat {
        if dragCard == card {
            return dragOffsetY
        }
        guard dragCard != nil else { return 0 }

        // 未被拖动的卡片：在拖动越过阈值时顺滑让位
        if isSwappedInHover {
            return index == 1 ? -slotDistance : slotDistance
        }
        return 0
    }

    /// 占位框的垂直位移（指示松手后的落点位置）
    private func placeholderOffset(forIndex index: Int) -> CGFloat {
        guard isSwappedInHover else { return 0 }
        return index == 0 ? slotDistance : -slotDistance
    }

    /// 卡片拖动手势处理
    private func cardDragGesture(for card: DashboardCard, index: Int) -> some Gesture {
        DragGesture(minimumDistance: 6)
            .onChanged { value in
                if dragCard == nil {
                    dragCard = card
                }
                dragOffsetY = value.translation.height

                let threshold: CGFloat = 60
                let hysteresis: CGFloat = 35

                if index == 0 {
                    // 上方卡片向下拖动
                    if !isSwappedInHover && dragOffsetY > threshold {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.8)) {
                            isSwappedInHover = true
                        }
                    } else if isSwappedInHover && dragOffsetY < hysteresis {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.8)) {
                            isSwappedInHover = false
                        }
                    }
                } else {
                    // 下方卡片向上拖动
                    if !isSwappedInHover && dragOffsetY < -threshold {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.8)) {
                            isSwappedInHover = true
                        }
                    } else if isSwappedInHover && dragOffsetY > -hysteresis {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.8)) {
                            isSwappedInHover = false
                        }
                    }
                }
            }
            .onEnded { _ in
                let shouldSwap = isSwappedInHover
                withAnimation(.spring(response: 0.28, dampingFraction: 0.8)) {
                    if shouldSwap, let other = order.first(where: { $0 != card }) {
                        swap(card, other)
                    }
                    dragCard = nil
                    dragOffsetY = 0
                    isSwappedInHover = false
                }
            }
    }

    // MARK: - 卡片

    private func summaryCard(_ card: DashboardCard, isDragging: Bool) -> some View {
        let summary = summaries[card] ?? DashboardSummary()
        return AppCard {
            VStack(alignment: .leading, spacing: 14) {
                headerRow(card, summary)
                metricsRow(summary)
                HairlineDivider()
                positionsSection(summary)
            }
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .background(
                GeometryReader { geo in
                    Color.clear.preference(key: DashboardCardHeightKey.self, value: geo.size.height)
                }
            )
        }
        .overlay(
            RoundedRectangle(cornerRadius: 18)
                .stroke(AppTheme.primary(scheme).opacity(isDragging ? 1 : 0), lineWidth: 2)
        )
        .shadow(
            color: Color.black.opacity(isDragging ? (scheme == .dark ? 0.45 : 0.12) : 0),
            radius: isDragging ? 12 : 0,
            x: 0,
            y: isDragging ? 6 : 0
        )
        .scaleEffect(isDragging ? 1.01 : 1.0)
    }

    private func headerRow(_ card: DashboardCard, _ summary: DashboardSummary) -> some View {
        HStack(spacing: 8) {
            // 拖动手柄固定在左上角，作为卡片可拖的视觉入口
            Image(systemName: "line.3.horizontal")
                .font(.system(size: 13, weight: .semibold))
                .foregroundColor(AppTheme.dim(scheme))
                .help("拖动可调整卡片顺序")
            Text(card.title)
                .font(AppTheme.bodyFont.weight(.semibold))
                .foregroundColor(AppTheme.ink(scheme))
            Text("\(summary.configuredCount) 个账户")
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .padding(.horizontal, 8)
                .padding(.vertical, 2)
                .background(AppTheme.soft(scheme))
                .cornerRadius(9)
            Spacer(minLength: 8)
            statusRow(card, summary)
            autoTradePill(for: card, summary: summary)
        }
    }

    /// 自动交易胶囊按钮（模拟与实盘分别开关）
    private func autoTradePill(for card: DashboardCard, summary: DashboardSummary) -> some View {
        let isRunning = appState.isKindRunning(card.rawValue)
        let canToggle = summary.configuredCount > 0 && !appState.startChecking
        let hovering = pillHovering[card] ?? false

        let fg: Color = !canToggle
            ? AppTheme.dim(scheme).opacity(0.4)
            : (isRunning ? .white : AppTheme.ink(scheme))

        let bg: Color = !canToggle
            ? .clear
            : (isRunning
                ? (hovering ? AppTheme.successHover(scheme) : AppTheme.success(scheme))
                : (hovering ? AppTheme.sidebarHover(scheme) : .clear))

        let border: Color = !canToggle
            ? AppTheme.divider(scheme).opacity(0.5)
            : (isRunning ? .clear : (hovering ? AppTheme.dim(scheme) : AppTheme.hairline(scheme)))

        return Button {
            appState.toggleTrading(!isRunning, kind: card.rawValue)
        } label: {
            Text(isRunning ? "自动交易：开" : "自动交易：关")
                .font(AppTheme.dimFont.weight(isRunning ? .semibold : .medium))
                .foregroundColor(fg)
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .background(bg)
                .overlay(
                    Capsule()
                        .stroke(border, lineWidth: isRunning ? 0 : 1)
                )
                .clipShape(Capsule())
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .disabled(!canToggle)
        .onHover { isHover in
            pillHovering[card] = isHover
        }
        .pointerCursor()
        .help(
            summary.configuredCount == 0
                ? "尚未配置\(card.title)账户"
                : (isRunning ? "点击停止\(card.title)自动交易" : "点击开启\(card.title)自动交易")
        )
    }

    private func metricsRow(_ summary: DashboardSummary) -> some View {
        let floatProfit = summary.total(\.floatProfit)
        return HStack(alignment: .top, spacing: 12) {
            metric("账户权益", money(summary.total(\.balance)), AppTheme.ink(scheme), weight: .semibold)
            metric("可用资金", money(summary.total(\.available)), AppTheme.ink(scheme))
            metric("保证金", money(summary.total(\.margin)), AppTheme.ink(scheme))
            metric("风险度", formatPercent(summary.riskRatio), AppTheme.ink(scheme))
            metric("浮动盈亏", signed(floatProfit), profitColor(floatProfit))
        }
    }

    private func metric(_ title: String, _ value: String, _ color: Color, weight: Font.Weight = .medium) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
            Text(value)
                .font(AppTheme.bodyFont.weight(weight))
                .foregroundColor(color)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: - 持仓表格

    private let floatProfitHelp = "按当前市场最新价与开仓均价计算的未实现盈亏。反映当前所有未平仓持仓以最新市价全部平仓时预估的累计盈亏金额。"
    private let openPriceHelp = "当前持仓在建立仓位时的实际成交均价。若同一合约多次开仓，按各笔成交手数加权平均计算，不会随每日结算价变动。"

    private func positionsSection(_ summary: DashboardSummary) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Text("当前持仓")
                    .font(AppTheme.dimFont.weight(.medium))
                    .foregroundColor(AppTheme.dim(scheme))
                if !summary.positions.isEmpty {
                    Text("\(summary.positions.count)")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.dim(scheme))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 1)
                        .background(AppTheme.soft(scheme))
                        .cornerRadius(6)
                }
                Spacer(minLength: 0)
            }
            positionTable(summary.positions)
        }
    }

    private func positionTable(_ rows: [PositionRecord]) -> some View {
        let maxVarietyCount = rows.map { displayVariety($0.symbol).count }.max() ?? 2
        let varietyWidth: CGFloat = max(80, CGFloat(maxVarietyCount) * 8.5 + 16)
        let widths: [CGFloat?] = [varietyWidth, nil, 40, 44, nil, nil, nil, nil, nil]
        let alignments: [Alignment] = [.leading, .leading, .center, .trailing, .trailing, .trailing, .trailing, .trailing, .trailing]

        return recordTable(
            headers: ["品种", "合约", "方向", "手数", "开仓均价", "当前价", "浮动盈亏", "手续费", "保证金"],
            headerHelps: [
                "开仓均价": openPriceHelp,
                "浮动盈亏": floatProfitHelp,
            ],
            widths: widths,
            alignments: alignments,
            maxHeight: nil,
            empty: "当前无持仓",
            isEmpty: rows.isEmpty
        ) {
            ForEach(rows) { row in
                tableRow(
                    cells: [
                        .text(displayVariety(row.symbol)),
                        .text(row.symbol),
                        .colored(row.direction, row.direction == "多" ? AppTheme.statusRed : AppTheme.statusGreen),
                        .text("\(row.volume)"),
                        .text(formatPrice(row.openPrice)),
                        .text(formatPrice(row.lastPrice)),
                        .colored(signed(row.floatProfit), profitColor(row.floatProfit)),
                        .text(money(row.commission)),
                        .text(money(row.margin)),
                    ],
                    widths: widths,
                    alignments: alignments
                )
            }
        }
    }

    private enum TableCell {
        case text(String)
        case colored(String, Color)
        case headerWithHelp(String, String)
    }

    private func recordTable<Rows: View>(
        headers: [String],
        headerHelps: [String: String] = [:],
        widths: [CGFloat?],
        alignments: [Alignment]? = nil,
        maxHeight: CGFloat? = 220,
        empty: String,
        isEmpty: Bool,
        @ViewBuilder rows: () -> Rows
    ) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            tableRow(
                cells: headers.map { header in
                    if let help = headerHelps[header] {
                        return .headerWithHelp(header, help)
                    }
                    return .colored(header, AppTheme.dim(scheme))
                },
                widths: widths,
                alignments: alignments,
                font: AppTheme.dimFont.weight(.semibold)
            )
            .padding(.vertical, 7)

            if isEmpty {
                Text(empty)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding(.vertical, 14)
            } else {
                HairlineDivider()
                if let maxHeight {
                    ScrollView(.vertical, showsIndicators: true) {
                        VStack(alignment: .leading, spacing: 0) {
                            rows()
                        }
                    }
                    .frame(maxHeight: maxHeight)
                } else {
                    VStack(alignment: .leading, spacing: 0) {
                        rows()
                    }
                }
            }
        }
        .background(AppTheme.chrome(scheme))
        .cornerRadius(12)
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
    }

    private func tableRow(
        cells: [TableCell],
        widths: [CGFloat?],
        alignments: [Alignment]? = nil,
        font: Font = AppTheme.dimFont
    ) -> some View {
        HStack(spacing: 8) {
            ForEach(Array(cells.enumerated()), id: \.offset) { index, cell in
                let align: Alignment = {
                    if let alignments, index < alignments.count {
                        return alignments[index]
                    }
                    return index == 0 ? .leading : .trailing
                }()
                let colWidth = index < widths.count ? widths[index] : nil
                cellView(cell, font: font)
                    .frame(width: colWidth, alignment: align)
                    .frame(maxWidth: colWidth == nil ? .infinity : nil, alignment: align)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }

    private func cellView(_ cell: TableCell, font: Font) -> some View {
        Group {
            switch cell {
            case .text(let text):
                Text(text).foregroundColor(AppTheme.ink(scheme))
            case .colored(let text, let color):
                Text(text).foregroundColor(color)
            case .headerWithHelp(let title, let help):
                HStack(spacing: 3) {
                    Text(title)
                        .foregroundColor(AppTheme.dim(scheme))
                    MetricHelpButton(title: title, message: help)
                }
            }
        }
        .font(font)
        .lineLimit(1)
        .truncationMode(.middle)
    }

    private func formatPrice(_ value: Double?) -> String {
        guard let value else { return "—" }
        return value.truncatingRemainder(dividingBy: 1) == 0
            ? String(format: "%.0f", value)
            : String(format: "%.2f", value)
    }

    private func displayVariety(_ symbol: String) -> String {
        let clean = symbol.components(separatedBy: ".").last ?? symbol
        let code = clean.uppercased()
        let letters = String(code.prefix { $0.isLetter })
        guard !letters.isEmpty else { return clean }
        if let name = appState.serverSymbolNames[letters], !name.isEmpty {
            return "\(letters)｜\(name)"
        }
        return letters
    }

    private func statusRow(_ card: DashboardCard, _ summary: DashboardSummary) -> some View {
        Text(statusText(card, summary))
            .font(AppTheme.dimFont)
            .foregroundColor(summary.failedRecords.isEmpty ? AppTheme.dim(scheme) : AppTheme.danger(scheme))
            .lineLimit(1)
            .truncationMode(.tail)
    }

    private func statusText(_ card: DashboardCard, _ summary: DashboardSummary) -> String {
        if summary.configuredCount == 0 {
            return "尚未配置\(card.title)账户"
        }
        if loading && summary.records.isEmpty {
            return "正在读取账户数据…"
        }
        if let failed = summary.failedRecords.first {
            let count = summary.failedRecords.count
            return count > 1
                ? "\(count) 个账户读取失败：\(failed.error)"
                : "\(failed.label) 读取失败：\(failed.error)"
        }
        return "数据来自快期账户，仅包含当前交易日"
    }

    // MARK: - 拖动排序

    /// 两张卡片互换位置并持久化（只有两张，拖动即交换）
    private func swap(_ card: DashboardCard, _ other: DashboardCard) {
        guard let from = order.firstIndex(of: card),
              let to = order.firstIndex(of: other) else {
            return
        }
        order.swapAt(from, to)
        DashboardCard.saveOrder(order)
    }

    // MARK: - 取数（trading.records 一次返回全部账户，两卡按 kind 分组）

    private func syncFromCache() {
        guard appState.connState == .connected else { return }
        let configured = appState.accounts
        for card in DashboardCard.allCases {
            var summary = summaries[card] ?? DashboardSummary()
            summary.configuredCount = configured.filter { $0.kind == card.rawValue }.count
            if let cached = appState.cachedRecords[card.rawValue] {
                summary.records = cached
            }
            summaries[card] = summary
        }
    }

    private func load() {
        let configured = appState.accounts
        for card in DashboardCard.allCases {
            var summary = summaries[card] ?? DashboardSummary()
            summary.configuredCount = configured.filter { $0.kind == card.rawValue }.count
            if let cached = appState.cachedRecords[card.rawValue], !cached.isEmpty {
                summary.records = cached
            }
            summaries[card] = summary
        }

        guard appState.connState == .connected else {
            loading = false
            clearRecords()
            return
        }
        guard configured.contains(where: { $0.kind == DashboardCard.sim.rawValue || $0.kind == DashboardCard.live.rawValue }) else {
            loading = false
            errorText = ""
            clearRecords()
            return
        }

        requestSeq += 1
        let seq = requestSeq
        loading = true
        errorText = ""
        Task {
            do {
                let resp = try await appState.call(method: "trading.records")
                guard seq == requestSeq else { return }
                let records = (resp["accounts"] as? [[String: Any]] ?? []).compactMap(AccountRecords.from)
                for card in DashboardCard.allCases {
                    let cardRecords = records.filter { $0.kind == card.rawValue }
                    appState.cachedRecords[card.rawValue] = cardRecords
                }
                syncFromCache()
                loading = false
            } catch {
                guard seq == requestSeq else { return }
                loading = false
                errorText = error.localizedDescription
            }
        }
    }

    // MARK: - 格式化

    /// 断开或没有可查询账户时清空旧数据，避免显示上一次连接的汇总
    private func clearRecords() {
        errorText = ""
        for card in DashboardCard.allCases {
            summaries[card]?.records = []
        }
    }

    private func money(_ value: Double?) -> String {
        guard let value else { return "—" }
        return String(format: "%.2f", value)
    }

    private func signed(_ value: Double?) -> String {
        guard let value else { return "—" }
        return String(format: "%+.2f", value)
    }

    private func formatPercent(_ value: Double?) -> String {
        guard let value else { return "—" }
        return String(format: "%.1f%%", value * 100)
    }

    /// 盈亏染色：正红负绿（国内期货习惯，与账户信息 tab 一致）
    private func profitColor(_ value: Double?) -> Color {
        guard let value, value != 0 else { return AppTheme.ink(scheme) }
        return value > 0 ? AppTheme.statusRed : AppTheme.statusGreen
    }
}

// MARK: - 卡片类型与汇总

/// 仪表盘两张卡片：模拟 / 实盘账户汇总。rawValue 即账户 kind。
enum DashboardCard: String, CaseIterable, Identifiable {
    case sim
    case live

    var id: String { rawValue }

    var title: String { self == .sim ? "模拟交易" : "实盘交易" }

    private static let orderKey = "dashboard.cardOrder"

    /// 保存的左右顺序；缺失或内容异常时回到默认（模拟在前）
    static func loadOrder() -> [DashboardCard] {
        let saved = UserDefaults.standard.string(forKey: orderKey) ?? ""
        let parsed = saved.split(separator: ",").compactMap { DashboardCard(rawValue: String($0)) }
        return Set(parsed) == Set(allCases) ? parsed : allCases
    }

    static func saveOrder(_ order: [DashboardCard]) {
        UserDefaults.standard.set(order.map(\.rawValue).joined(separator: ","), forKey: orderKey)
    }
}

/// 一类账户的当日汇总：只累加读取成功的账户，失败项单独提示
struct DashboardSummary {
    var configuredCount = 0
    var records: [AccountRecords] = []

    var failedRecords: [AccountRecords] {
        records.filter { !$0.error.isEmpty }
    }

    private var okRecords: [AccountRecords] {
        records.filter { $0.error.isEmpty }
    }

    /// 全部成功账户的持仓列表
    var positions: [PositionRecord] {
        okRecords.flatMap(\.positions)
    }

    /// 合计；所有账户该字段都未就绪（或没有账户）时返回 nil，显示为「—」
    func total(_ keyPath: KeyPath<AccountSnapshot, Double?>) -> Double? {
        let values = okRecords.compactMap { $0.account[keyPath: keyPath] }
        return values.isEmpty ? nil : values.reduce(0, +)
    }

    /// 综合风险度：单账户取其自身风险度，多账户按总保证金 ÷ 总权益计算；未就绪或权益非正时返回 nil
    var riskRatio: Double? {
        if okRecords.count == 1 {
            return okRecords.first?.account.riskRatio
        }
        guard let margin = total(\.margin),
              let balance = total(\.balance),
              balance > 0 else {
            return nil
        }
        return margin / balance
    }
}
