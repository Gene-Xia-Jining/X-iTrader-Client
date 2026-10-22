import SwiftUI

// MARK: - 模型（协议字段见 src/backend/PROTOCOL.md 的 trading.records）

/// 账户资金快照。TqSdk 未就绪的字段后端转 null，因此全部可空。
struct AccountSnapshot {
    var balance: Double?
    var available: Double?
    var margin: Double?
    var floatProfit: Double?
    var positionProfit: Double?
    var closeProfit: Double?
    var commission: Double?
    var riskRatio: Double?
    var preBalance: Double?
    var staticBalance: Double?

    static func from(_ dict: [String: Any]) -> AccountSnapshot {
        AccountSnapshot(
            balance: dict.double("balance"),
            available: dict.double("available"),
            margin: dict.double("margin"),
            floatProfit: dict.double("float_profit"),
            positionProfit: dict.double("position_profit"),
            closeProfit: dict.double("close_profit"),
            commission: dict.double("commission"),
            riskRatio: dict.double("risk_ratio"),
            preBalance: dict.double("pre_balance"),
            staticBalance: dict.double("static_balance")
        )
    }
}

/// 持仓行：一个合约的多空各成一行
struct PositionRecord: Identifiable {
    var id: String { "\(symbol)-\(direction)" }
    let symbol: String
    let direction: String
    let volume: Int
    let openPrice: Double?
    let positionPrice: Double?
    let floatProfit: Double?
    let margin: Double?

    static func from(_ dict: [String: Any]) -> PositionRecord? {
        guard let symbol = dict["symbol"] as? String, !symbol.isEmpty else { return nil }
        return PositionRecord(
            symbol: symbol,
            direction: dict["direction"] as? String ?? "",
            volume: dict.int("volume") ?? 0,
            openPrice: dict.double("open_price"),
            positionPrice: dict.double("position_price"),
            floatProfit: dict.double("float_profit"),
            margin: dict.double("margin")
        )
    }
}

/// 委托单行
struct OrderRecord: Identifiable {
    let id: String
    let symbol: String
    let directionText: String
    let offsetText: String
    let volumeOrigin: Int
    let volumeLeft: Int
    let limitPrice: Double?
    let tradePrice: Double?
    let statusText: String
    let lastMsg: String
    let insertTime: String

    static func from(_ dict: [String: Any]) -> OrderRecord? {
        guard let id = dict["order_id"] as? String, !id.isEmpty else { return nil }
        return OrderRecord(
            id: id,
            symbol: dict["symbol"] as? String ?? "",
            directionText: dict["direction_text"] as? String ?? "",
            offsetText: dict["offset_text"] as? String ?? "",
            volumeOrigin: dict.int("volume_orign") ?? 0,
            volumeLeft: dict.int("volume_left") ?? 0,
            limitPrice: dict.double("limit_price"),
            tradePrice: dict.double("trade_price"),
            statusText: dict["status_text"] as? String ?? "",
            lastMsg: dict["last_msg"] as? String ?? "",
            insertTime: dict["insert_time"] as? String ?? ""
        )
    }
}

/// 成交行
struct TradeRecord: Identifiable {
    let id: String
    let symbol: String
    let directionText: String
    let offsetText: String
    let price: Double?
    let volume: Int
    let tradeTime: String

    static func from(_ dict: [String: Any]) -> TradeRecord? {
        guard let id = dict["trade_id"] as? String, !id.isEmpty else { return nil }
        return TradeRecord(
            id: id,
            symbol: dict["symbol"] as? String ?? "",
            directionText: dict["direction_text"] as? String ?? "",
            offsetText: dict["offset_text"] as? String ?? "",
            price: dict.double("price"),
            volume: dict.int("volume") ?? 0,
            tradeTime: dict["trade_time"] as? String ?? ""
        )
    }
}

/// 单个账户的交易记录快照（取数失败时 error 非空，其余为空）
struct AccountRecords: Identifiable {
    let id: String
    let kind: String
    let label: String
    let error: String
    let account: AccountSnapshot
    let positions: [PositionRecord]
    let orders: [OrderRecord]
    let trades: [TradeRecord]

    static func from(_ dict: [String: Any]) -> AccountRecords? {
        guard let id = dict["account_id"] as? String else { return nil }
        return AccountRecords(
            id: id,
            kind: dict["kind"] as? String ?? "",
            label: dict["label"] as? String ?? id,
            error: dict["error"] as? String ?? "",
            account: AccountSnapshot.from(dict["account"] as? [String: Any] ?? [:]),
            positions: (dict["positions"] as? [[String: Any]] ?? []).compactMap(PositionRecord.from),
            orders: (dict["orders"] as? [[String: Any]] ?? []).compactMap(OrderRecord.from),
            trades: (dict["trades"] as? [[String: Any]] ?? []).compactMap(TradeRecord.from)
        )
    }
}

/// 账户类别：模拟 / 实盘
enum RecordKind: String, CaseIterable {
    case sim
    case live

    var title: String { self == .sim ? "模拟交易" : "实盘交易" }
}

// JSON 数字经由 NSNumber 解出（JSONSerialization 不区分 Int/Double）
private extension Dictionary where Key == String, Value == Any {
    func double(_ key: String) -> Double? {
        // NSNull（后端 nan→null）落到此处返回 nil
        (self[key] as? NSNumber)?.doubleValue
    }

    func int(_ key: String) -> Int? {
        (self[key] as? NSNumber)?.intValue
    }
}

// MARK: - 一类账户的交易记录

/// 按账户类别取数并展示资金、持仓、当日委托与当日成交。
/// 实盘交易页与模拟交易页的「交易记录」tab 共用这一份实现，只按 kind 区分账户类别。
struct AccountRecordsView: View {
    let kind: RecordKind
    /// 尚未配置这类账户时的引导文案：两个入口指向不同的配置位置
    let notConfiguredHint: String

    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    @State private var records: [AccountRecords] = []
    @State private var loading = false
    /// 是否至少完成过一次取数：未取过就不显示「暂无数据」，避免误导
    @State private var hasLoaded = false
    @State private var errorText = ""
    /// 请求代号：只有最新一次请求的结果会被采用
    @State private var requestSeq = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 10) {
                Text(loading ? "正在读取账户数据…" : hintText)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                Spacer()
                AppButton("刷新", variant: .secondary, disabled: loading) { load() }
            }

            if !errorText.isEmpty {
                errorCard(errorText)
            }

            ForEach(records) { record in
                accountCard(record)
            }

            if records.isEmpty && errorText.isEmpty && !loading && hasLoaded {
                AppCard {
                    Text("暂无数据")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.dim(scheme))
                        .frame(maxWidth: .infinity, alignment: .center)
                        .padding(.vertical, 28)
                }
            }
        }
        .onAppear {
            if let cached = appState.cachedRecords[kind.rawValue], !cached.isEmpty {
                records = cached
                hasLoaded = true
            }
            load()
        }
        .onReceive(appState.$recordsRevision) { _ in
            if let cached = appState.cachedRecords[kind.rawValue], !cached.isEmpty {
                records = cached
                hasLoaded = true
            }
        }
    }

    private var hintText: String {
        let count = appState.accounts.filter { $0.kind == kind.rawValue }.count
        if count == 0 {
            return notConfiguredHint
        }
        if loading {
            return records.isEmpty ? "正在读取账户数据…" : "正在刷新账户数据…"
        }
        return "数据来自快期账户，仅包含当前交易日"
    }

    // ---- 取数 ----

    private func load() {
        guard appState.connState == .connected else {
            errorText = "后端未连接"
            return
        }
        requestSeq += 1
        let requestId = requestSeq
        loading = true
        errorText = ""
        // 不清空现有 records，防止刷新时卡片白屏闪烁
        Task {
            let result: Result<[AccountRecords], Error>
            do {
                let resp = try await appState.call(
                    method: "trading.records",
                    params: ["kind": kind.rawValue]
                )
                result = .success(
                    (resp["accounts"] as? [[String: Any]] ?? []).compactMap(AccountRecords.from)
                )
            } catch {
                result = .failure(error)
            }
            guard requestId == requestSeq else { return }
            loading = false
            hasLoaded = true
            switch result {
            case .success(let value):
                records = value
                appState.cachedRecords[kind.rawValue] = value
            case .failure(let error):
                // 仅当此前完全没有数据时才清空，有旧数据时保留卡片展示
                if records.isEmpty {
                    records = []
                }
                errorText = "读取失败: \(error.localizedDescription)"
            }
        }
    }

    // ---- 账户卡片 ----

    private func accountCard(_ record: AccountRecords) -> some View {
        AppCard {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 8) {
                    Text(record.label)
                        .font(AppTheme.bodyFont.weight(.semibold))
                        .foregroundColor(AppTheme.ink(scheme))
                    Text(record.kind == "sim" ? "模拟" : "实盘")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.dim(scheme))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 2)
                        .background(AppTheme.soft(scheme))
                        .cornerRadius(9)
                    Spacer()
                }

                if !record.error.isEmpty {
                    Text("读取失败: \(record.error)")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.danger(scheme))
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    metricsRow(record.account)
                    section("持仓") { positionTable(record.positions) }
                    section("当日委托") { orderTable(record.orders) }
                    section("当日成交") { tradeTable(record.trades) }
                }
            }
            .padding(20)
        }
    }

    /// 资金指标：权益 / 可用 / 保证金 / 浮动盈亏 / 平仓盈亏 / 风险度
    private func metricsRow(_ account: AccountSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 0) {
                metric("账户权益", formatMoney(account.balance), nil)
                metric("可用资金", formatMoney(account.available), nil)
                metric("保证金", formatMoney(account.margin), nil)
            }
            HStack(spacing: 0) {
                metric("浮动盈亏", formatSigned(account.floatProfit), profitColor(account.floatProfit))
                metric("持仓盈亏", formatSigned(account.positionProfit), profitColor(account.positionProfit))
                metric("平仓盈亏", formatSigned(account.closeProfit), profitColor(account.closeProfit))
            }
            HStack(spacing: 0) {
                metric("手续费", formatMoney(account.commission), nil)
                metric("风险度", formatPercent(account.riskRatio), nil)
                metric("昨日权益", formatMoney(account.preBalance), nil)
            }
        }
        .padding(12)
        .background(AppTheme.soft(scheme))
        .cornerRadius(11)
    }

    private func metric(_ title: String, _ value: String, _ color: Color?) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
            Text(value)
                .font(AppTheme.bodyFont.weight(.medium))
                .foregroundColor(color ?? AppTheme.ink(scheme))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // ---- 表格区 ----

    private func section<Content: View>(
        _ title: String,
        @ViewBuilder table: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(AppTheme.dimFont.weight(.semibold))
                .foregroundColor(AppTheme.dim(scheme))
            table()
        }
    }

    private func positionTable(_ rows: [PositionRecord]) -> some View {
        recordTable(
            headers: ["合约", "方向", "手数", "开仓均价", "持仓均价", "浮动盈亏", "保证金"],
            widths: [nil, 40, 44, nil, nil, nil, nil],
            empty: "当前无持仓",
            isEmpty: rows.isEmpty
        ) {
            ForEach(rows) { row in
                tableRow(
                    cells: [
                        .text(row.symbol),
                        .colored(row.direction, row.direction == "多" ? AppTheme.statusRed : AppTheme.statusGreen),
                        .text("\(row.volume)"),
                        .text(formatPrice(row.openPrice)),
                        .text(formatPrice(row.positionPrice)),
                        .colored(formatSigned(row.floatProfit), profitColor(row.floatProfit) ?? AppTheme.ink(scheme)),
                        .text(formatMoney(row.margin)),
                    ],
                    widths: [nil, 40, 44, nil, nil, nil, nil]
                )
            }
        }
    }

    private func orderTable(_ rows: [OrderRecord]) -> some View {
        recordTable(
            headers: ["委托时间", "合约", "买卖", "开平", "委托手数", "成交手数", "委托价", "成交均价", "状态"],
            widths: [nil, nil, 40, 44, 60, 60, nil, nil, nil],
            empty: "当日无委托",
            isEmpty: rows.isEmpty
        ) {
            ForEach(rows) { row in
                tableRow(
                    cells: [
                        .text(row.insertTime),
                        .text(row.symbol),
                        .colored(row.directionText, sideColor(row.directionText)),
                        .text(row.offsetText),
                        .text("\(row.volumeOrigin)"),
                        .text("\(row.volumeOrigin - row.volumeLeft)"),
                        .text(formatPrice(row.limitPrice)),
                        .text(formatPrice(row.tradePrice)),
                        .text(row.statusText),
                    ],
                    widths: [nil, nil, 40, 44, 60, 60, nil, nil, nil]
                )
            }
        }
    }

    private func tradeTable(_ rows: [TradeRecord]) -> some View {
        recordTable(
            headers: ["成交时间", "合约", "买卖", "开平", "成交价", "手数"],
            widths: [nil, nil, 40, 44, nil, 50],
            empty: "当日无成交",
            isEmpty: rows.isEmpty
        ) {
            ForEach(rows) { row in
                tableRow(
                    cells: [
                        .text(row.tradeTime),
                        .text(row.symbol),
                        .colored(row.directionText, sideColor(row.directionText)),
                        .text(row.offsetText),
                        .text(formatPrice(row.price)),
                        .text("\(row.volume)"),
                    ],
                    widths: [nil, nil, 40, 44, nil, 50]
                )
            }
        }
    }

    private enum Cell {
        case text(String)
        case colored(String, Color)
    }

    private func recordTable<Rows: View>(
        headers: [String],
        widths: [CGFloat?],
        empty: String,
        isEmpty: Bool,
        @ViewBuilder rows: () -> Rows
    ) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            tableRow(
                cells: headers.map { .colored($0, AppTheme.dim(scheme)) },
                widths: widths,
                font: AppTheme.dimFont.weight(.semibold)
            )
            .padding(.vertical, 7)
            if isEmpty {
                Text(empty)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding(.vertical, 18)
            } else {
                HairlineDivider()
                rows()
            }
        }
        .background(AppTheme.soft(scheme))
        .cornerRadius(11)
        .overlay(
            RoundedRectangle(cornerRadius: 11)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
    }

    private func tableRow(
        cells: [Cell],
        widths: [CGFloat?],
        font: Font = AppTheme.dimFont
    ) -> some View {
        HStack(spacing: 8) {
            ForEach(Array(cells.enumerated()), id: \.offset) { index, cell in
                cellView(cell, font: font)
                    .frame(
                        maxWidth: .infinity,
                        alignment: index == 0 ? .leading : .trailing
                    )
                    .frame(width: index < widths.count ? widths[index] : nil)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }

    private func cellView(_ cell: Cell, font: Font) -> some View {
        Group {
            switch cell {
            case .text(let text):
                Text(text).foregroundColor(AppTheme.ink(scheme))
            case .colored(let text, let color):
                Text(text).foregroundColor(color)
            }
        }
        .font(font)
        .lineLimit(1)
        .truncationMode(.middle)
    }

    private func errorCard(_ message: String) -> some View {
        AppCard {
            Text(message)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.danger(scheme))
                .fixedSize(horizontal: false, vertical: true)
                .padding(20)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // ---- 格式化 ----

    private func sideColor(_ text: String) -> Color {
        switch text {
        case "买": return AppTheme.statusRed
        case "卖": return AppTheme.statusGreen
        default: return AppTheme.ink(scheme)
        }
    }

    /// 盈亏染色：正红负绿（国内期货习惯）
    private func profitColor(_ value: Double?) -> Color? {
        guard let value, value != 0 else { return nil }
        return value > 0 ? AppTheme.statusRed : AppTheme.statusGreen
    }

    private func formatMoney(_ value: Double?) -> String {
        guard let value else { return "—" }
        return String(format: "%.2f", value)
    }

    private func formatPrice(_ value: Double?) -> String {
        guard let value else { return "—" }
        // 期货合约小数位不定，固定三位后去掉多余的尾零
        var text = String(format: "%.3f", value)
        while text.hasSuffix("0") { text.removeLast() }
        if text.hasSuffix(".") { text.removeLast() }
        return text
    }

    private func formatSigned(_ value: Double?) -> String {
        guard let value else { return "—" }
        return String(format: "%+.2f", value)
    }

    private func formatPercent(_ value: Double?) -> String {
        guard let value else { return "—" }
        return String(format: "%.1f%%", value * 100)
    }
}
