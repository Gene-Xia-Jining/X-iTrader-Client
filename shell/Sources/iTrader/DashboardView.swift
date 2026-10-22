import SwiftUI

/// 仪表盘：模拟 / 实盘两张账户汇总卡片，左右顺序可拖动互换。
struct DashboardView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    @State private var order = DashboardCard.loadOrder()
    @State private var summaries: [DashboardCard: DashboardSummary] = [:]
    @State private var loading = false
    @State private var errorText = ""
    /// 请求代号：账户变化会立即重发，旧的返回结果不能覆盖新数据
    @State private var requestSeq = 0
    /// 正在拖动的卡片与位移（松手时按位移方向决定是否换位）
    @State private var dragCard: DashboardCard?
    @State private var dragOffset: CGSize = .zero

    var body: some View {
        PageContainer {
            HStack(alignment: .top, spacing: 16) {
                ForEach(order) { card in
                    summaryCard(card)
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
    }

    // MARK: - 卡片

    private func summaryCard(_ card: DashboardCard) -> some View {
        let summary = summaries[card] ?? DashboardSummary()
        let dragging = dragCard == card
        return AppCard {
            VStack(alignment: .leading, spacing: 14) {
                headerRow(card, summary)
                balanceBlock(summary)
                HairlineDivider()
                metricsRow(summary)
                statusRow(card, summary)
            }
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .overlay(
            RoundedRectangle(cornerRadius: 18)
                .stroke(AppTheme.primary(scheme).opacity(dragging ? 1 : 0), lineWidth: 2)
        )
        .offset(x: dragging ? dragOffset.width : 0)
        .zIndex(dragging ? 1 : 0)
        .contentShape(RoundedRectangle(cornerRadius: 18))
        .gesture(
            DragGesture(minimumDistance: 10)
                .onChanged { value in
                    dragCard = card
                    dragOffset = value.translation
                }
                .onEnded { value in
                    let moved = abs(value.translation.width) > 80
                    withAnimation(.easeInOut(duration: 0.2)) {
                        if moved, let other = order.first(where: { $0 != card }) {
                            swap(card, other)
                        }
                        dragCard = nil
                        dragOffset = .zero
                    }
                }
        )
        .onHover { hovering in
            if hovering {
                NSCursor.openHand.push()
            } else {
                NSCursor.pop()
            }
        }
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
            Spacer(minLength: 0)
        }
    }

    private func balanceBlock(_ summary: DashboardSummary) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("账户权益")
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
            Text(money(summary.total(\.balance)))
                .font(AppTheme.metricFont)
                .kerning(-0.3)
                .foregroundColor(AppTheme.ink(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.5)
        }
    }

    private func metricsRow(_ summary: DashboardSummary) -> some View {
        let profit = summary.total(\.floatProfit)
        return HStack(alignment: .top, spacing: 12) {
            metric("可用资金", money(summary.total(\.available)), AppTheme.ink(scheme))
            metric("保证金", money(summary.total(\.margin)), AppTheme.ink(scheme))
            metric("浮动盈亏", signed(profit), profitColor(profit))
        }
    }

    private func metric(_ title: String, _ value: String, _ color: Color) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
            Text(value)
                .font(AppTheme.bodyFont.weight(.medium))
                .foregroundColor(color)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func statusRow(_ card: DashboardCard, _ summary: DashboardSummary) -> some View {
        Text(statusText(card, summary))
            .font(AppTheme.dimFont)
            .foregroundColor(summary.failedRecords.isEmpty ? AppTheme.dim(scheme) : AppTheme.danger(scheme))
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
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

    private func load() {
        let configured = appState.accounts
        for card in DashboardCard.allCases {
            var summary = summaries[card] ?? DashboardSummary()
            summary.configuredCount = configured.filter { $0.kind == card.rawValue }.count
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
                    var summary = summaries[card] ?? DashboardSummary()
                    summary.configuredCount = configured.filter { $0.kind == card.rawValue }.count
                    summary.records = records.filter { $0.kind == card.rawValue }
                    summaries[card] = summary
                }
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

    /// 合计；所有账户该字段都未就绪（或没有账户）时返回 nil，显示为「—」
    func total(_ keyPath: KeyPath<AccountSnapshot, Double?>) -> Double? {
        let values = okRecords.compactMap { $0.account[keyPath: keyPath] }
        return values.isEmpty ? nil : values.reduce(0, +)
    }
}
