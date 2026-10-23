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
            metric("手续费", money(summary.total(\.commission)), AppTheme.ink(scheme))
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
        let widths: [CGFloat?] = [varietyWidth, nil, 40, 44, nil, nil, nil, nil]
        let alignments: [Alignment] = [.leading, .leading, .center, .trailing, .trailing, .trailing, .trailing, .trailing]

        return recordTable(
            headers: ["品种", "合约", "方向", "手数", "开仓均价", "当前价", "浮动盈亏", "保证金"],
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
