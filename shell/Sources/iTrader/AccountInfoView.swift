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
    let source: String

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
            insertTime: dict["insert_time"] as? String ?? "",
            source: dict["source"] as? String ?? "智能信号"
        )
    }
}

/// 成交行
struct TradeRecord: Identifiable {
    let id: String
    let orderId: String
    let symbol: String
    let directionText: String
    let offsetText: String
    let price: Double?
    let volume: Int
    let tradeTime: String
    let commission: Double?
    let realizedProfit: Double?
    let source: String

    static func from(_ dict: [String: Any]) -> TradeRecord? {
        guard let id = dict["trade_id"] as? String, !id.isEmpty else { return nil }
        return TradeRecord(
            id: id,
            orderId: dict["order_id"] as? String ?? "",
            symbol: dict["symbol"] as? String ?? "",
            directionText: dict["direction_text"] as? String ?? "",
            offsetText: dict["offset_text"] as? String ?? "",
            price: dict.double("price"),
            volume: dict.int("volume") ?? 0,
            tradeTime: dict["trade_time"] as? String ?? "",
            commission: dict.double("commission"),
            realizedProfit: dict.double("realized_profit"),
            source: dict["source"] as? String ?? "智能信号"
        )
    }
}

/// 成交明细弹窗载体
struct TradeDetailModal: Identifiable {
    var id: String { trade.id }
    let trade: TradeRecord
    let accountLabel: String
}

/// 历史时间筛选范围
enum RecordDateRange: String, CaseIterable {
    case all = "all"
    case oneYear = "1y"
    case thirtyDays = "30d"
    case today = "today"

    var title: String {
        switch self {
        case .all: return "全部日期"
        case .oneYear: return "1 年"
        case .thirtyDays: return "30天"
        case .today: return "今日"
        }
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

/// 显示模式：账户信息（资金、持仓、当日委托各独立成框） / 历史成交（成交记录与时间范围筛选）
enum AccountInfoMode {
    case info
    case trades
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

// MARK: - 一类账户的账户信息

/// 按账户类别取数并展示资金、持仓、当日委托与历史成交。
/// 实盘交易页与模拟交易页的「账户信息」tab 共用这一份实现，只按 kind 区分账户类别。
struct AccountInfoView: View {
    let kind: RecordKind
    var mode: AccountInfoMode = .info
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
    /// 历史时间范围筛选
    @State private var selectedDateRange: RecordDateRange = .today
    /// 点击查看明细的成交
    @State private var selectedTradeModal: TradeDetailModal? = nil
    /// 历史成交的品种过滤器（空串 = 全部品种）
    @State private var filterVariety: String = ""
    /// 历史成交的策略过滤器（空串 = 全部策略）
    @State private var filterStrategy: String = ""
    /// 历史成交排序：默认最新在前，按钮切到最新在后
    @State private var newestFirst = true

    /// 历史成交偏好持久化键（按账户类别区分，如 sim / live）
    private var dateRangeKey: String { "trades.dateRange.\(kind.rawValue)" }
    private var varietyKey: String { "trades.filterVariety.\(kind.rawValue)" }
    private var strategyKey: String { "trades.filterStrategy.\(kind.rawValue)" }
    private var newestFirstKey: String { "trades.newestFirst.\(kind.rawValue)" }

    private let floatProfitHelp = "按当前市场最新价与开仓均价计算的未实现盈亏。反映当前所有未平仓持仓以最新市价全部平仓时预估的累计盈亏金额。"
    private let positionProfitHelp = "按当前最新价与持仓成本（历史持仓按上一交易日结算价、当日开仓按开仓均价）计算的盯市盈亏。用于反映当日持仓所产生的盈亏变动及每日结算。"
    private let riskRatioHelp = "保证金占用占账户总权益的比例（计算公式：保证金 ÷ 账户权益 × 100%）。风险度越高代表仓位与杠杆越重；当风险度达到或超过 100% 时，可用资金将为负，可能面临追加保证金或被强行平仓的风险。"
    private let openPriceHelp = "当前持仓在建立仓位时的实际成交均价。若同一合约多次开仓，按各笔成交手数加权平均计算，不会随每日结算价变动。"
    private let positionPriceHelp = "当前持仓用于每日结算与盯市的基准成本价。当日新开仓等于开仓均价；跨交易日的历史持仓在每日结算后调整为上一交易日的结算价。"
    private let closeProfitHelp = "本交易日内已平仓合约实现的累计盈亏金额（平仓价与持仓成本价之差）。当日未进行平仓操作时为 0.00。"
    private let commissionHelp = "本交易日内因开仓、平仓等交易产生的累计手续费支出。隔夜历史持仓产生的手续费已在以往结算中扣减并计入昨日权益；若当日无新成交则为 0.00。"

    init(kind: RecordKind, mode: AccountInfoMode = .info, notConfiguredHint: String) {
        self.kind = kind
        self.mode = mode
        self.notConfiguredHint = notConfiguredHint

        if mode == .trades {
            let savedRange = UserDefaults.standard.string(forKey: "trades.dateRange.\(kind.rawValue)")
            let initialRange = RecordDateRange(rawValue: savedRange ?? "") ?? .all
            self._selectedDateRange = State(initialValue: initialRange)

            let initialVariety = UserDefaults.standard.string(forKey: "trades.filterVariety.\(kind.rawValue)") ?? ""
            self._filterVariety = State(initialValue: initialVariety)

            let initialStrategy = UserDefaults.standard.string(forKey: "trades.filterStrategy.\(kind.rawValue)") ?? ""
            self._filterStrategy = State(initialValue: initialStrategy)

            let savedOrder = UserDefaults.standard.object(forKey: "trades.newestFirst.\(kind.rawValue)") as? Bool
            self._newestFirst = State(initialValue: savedOrder ?? true)
        } else {
            self._selectedDateRange = State(initialValue: .today)
            self._filterVariety = State(initialValue: "")
            self._filterStrategy = State(initialValue: "")
            self._newestFirst = State(initialValue: true)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if mode == .trades {
                tradesToolBar
            } else {
                HStack(spacing: 12) {
                    Text(loading ? "正在读取账户数据…" : hintText)
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.dim(scheme))
                    Spacer()
                    AppButton("刷新", variant: .secondary, disabled: loading) { load() }
                }
            }

            if !errorText.isEmpty {
                errorCard(errorText)
            }

            ForEach(records) { record in
                accountCards(record)
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
        .sheet(item: $selectedTradeModal) { modal in
            tradeDetailSheet(modal)
        }
        .onAppear {
            // 缓存只有当日快照；历史范围下不能拿它当初始数据，否则会先闪出当日记录
            if selectedDateRange == .today,
               let cached = appState.cachedRecords[kind.rawValue], !cached.isEmpty {
                records = cached
                hasLoaded = true
            }
            load()
        }
        .onReceive(appState.$recordsRevision) { _ in
            if selectedDateRange == .today, let cached = appState.cachedRecords[kind.rawValue], !cached.isEmpty {
                records = cached
                hasLoaded = true
            }
        }
    }

    /// 历史成交工具栏：日期、品种、策略各过滤器分组呈水平顺序排列
    private var tradesToolBar: some View {
        HStack(spacing: 10) {
            // 1. 日期过滤器分组
            filterSegmentGroup(
                items: RecordDateRange.allCases.map { ($0.rawValue, $0.title) },
                selectedId: selectedDateRange.rawValue
            ) { key in
                guard let range = RecordDateRange(rawValue: key), selectedDateRange != range else { return }
                selectedDateRange = range
                UserDefaults.standard.set(range.rawValue, forKey: dateRangeKey)
                load()
            }

            // 2. 品种过滤器分组（若当前有成交记录则展示）
            let varieties = availableVarieties(from: allTrades)
            if !varieties.isEmpty {
                filterSegmentGroup(
                    items: [("", "全部品种")] + varieties.map { ($0, $0) },
                    selectedId: filterVariety
                ) { v in
                    filterVariety = v
                    UserDefaults.standard.set(v, forKey: varietyKey)
                }
            }

            // 3. 策略过滤器分组（若当前有成交记录则展示）
            let strategies = availableStrategies(from: allTrades)
            if !strategies.isEmpty {
                filterSegmentGroup(
                    items: [("", "全部策略")] + strategies.map { ($0, $0) },
                    selectedId: filterStrategy
                ) { s in
                    filterStrategy = s
                    UserDefaults.standard.set(s, forKey: strategyKey)
                }
            }

            Spacer()

            if loading {
                Text(records.isEmpty ? "正在读取…" : "正在刷新…")
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
            }

            // 页面底色就是 canvas，secondary 默认的 canvas 悬停底等于没反应，
            // 复用 onChrome 那档更明显的 sidebarHover
            AppButton(newestFirst ? "最新在前" : "最新在后", variant: .secondary, onChrome: true) {
                newestFirst.toggle()
                UserDefaults.standard.set(newestFirst, forKey: newestFirstKey)
            }

            AppButton("刷新", variant: .secondary, disabled: loading, onChrome: true) { load() }
        }
    }

    /// 分段胶囊里的单个切片：选中态用 soft 底，未选中时悬停提亮一档
    private struct FilterSlice: View {
        let title: String
        let isSelected: Bool
        let scheme: ColorScheme
        let action: () -> Void

        @State private var hovering = false

        var body: some View {
            Button(action: action) {
                Text(title)
                    .font(AppTheme.dimFont.weight(isSelected ? .semibold : .regular))
                    .foregroundColor(isSelected ? AppTheme.ink(scheme) : AppTheme.dim(scheme))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 4)
                    .background(
                        isSelected
                            ? AppTheme.soft(scheme)
                            : (hovering ? AppTheme.sidebarHover(scheme) : Color.clear)
                    )
                    .cornerRadius(10)
            }
            .buttonStyle(.plain)
            .onHover { hovering = $0 }
            .pointerCursor()
        }
    }

    /// 通用分段选择胶囊组（与日期筛选器风格一致：外层深色底边框，内层切片单选）
    private func filterSegmentGroup(
        items: [(id: String, title: String)],
        selectedId: String,
        onSelect: @escaping (String) -> Void
    ) -> some View {
        HStack(spacing: 4) {
            ForEach(items, id: \.id) { item in
                FilterSlice(
                    title: item.title,
                    isSelected: selectedId == item.id,
                    scheme: scheme
                ) {
                    onSelect(item.id)
                }
            }
        }
        .padding(2)
        .background(AppTheme.chrome(scheme))
        .cornerRadius(12)
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
    }


    /// 当前所有账户的成交合并（过滤器选项从这里汇总，避免各账户列表不一致）
    private var allTrades: [TradeRecord] {
        records.flatMap(\.trades)
    }

    private var hintText: String {
        let count = appState.accounts.filter { $0.kind == kind.rawValue }.count
        if count == 0 {
            return notConfiguredHint
        }
        if loading {
            return records.isEmpty ? "正在读取账户数据…" : "正在刷新账户数据…"
        }
        if mode == .trades {
            return ""
        }
        return "数据来自快期账户，包含当日实时推送"
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
                let range = mode == .trades ? selectedDateRange.rawValue : "today"
                let resp = try await appState.call(
                    method: "trading.records",
                    params: [
                        "kind": kind.rawValue,
                        "date_range": range
                    ]
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
                if selectedDateRange == .today {
                    appState.cachedRecords[kind.rawValue] = value
                }
                if mode == .trades {
                    let trades = value.flatMap(\.trades)
                    let varieties = availableVarieties(from: trades)
                    if !varieties.isEmpty && !filterVariety.isEmpty && !varieties.contains(filterVariety) {
                        filterVariety = ""
                        UserDefaults.standard.set("", forKey: varietyKey)
                    }
                    let strategies = availableStrategies(from: trades)
                    if !strategies.isEmpty && !filterStrategy.isEmpty && !strategies.contains(filterStrategy) {
                        filterStrategy = ""
                        UserDefaults.standard.set("", forKey: strategyKey)
                    }
                }
            case .failure(let error):
                if records.isEmpty {
                    records = []
                }
                errorText = "读取失败: \(error.localizedDescription)"
            }
        }
    }

    // ---- 账户卡片与分区（持仓与当日委托独立成框）----

    /// 账户标题：单账户时省略（页面本身已区分模拟/实盘），多账户时只留账户名用于区分归属
    private func accountHeader(_ record: AccountRecords) -> some View {
        Text(record.label)
            .font(AppTheme.bodyFont.weight(.semibold))
            .foregroundColor(AppTheme.ink(scheme))
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    @ViewBuilder
    private func accountCards(_ record: AccountRecords) -> some View {
        if mode == .trades {
            // 历史成交不再套一层卡片外框：表格自带底色与描边，直接铺在页面上
            tradeTable(filterTrades(record.trades), accountLabel: record.label)
        } else if !record.error.isEmpty {
            AppCard {
                VStack(alignment: .leading, spacing: 14) {
                    if records.count > 1 {
                        accountHeader(record)
                    }
                    Text("读取失败: \(record.error)")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.danger(scheme))
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(20)
            }
        } else {
            // 账户信息：资金、持仓、当日委托各独立成框
            AppCard {
                VStack(alignment: .leading, spacing: 14) {
                    if records.count > 1 {
                        accountHeader(record)
                    }
                    metricsRow(record.account)
                }
                .padding(20)
            }

            AppCard {
                VStack(alignment: .leading, spacing: 12) {
                    sectionTitle("持仓", record: record)
                    positionTable(record.positions)
                }
                .padding(20)
            }

            AppCard {
                VStack(alignment: .leading, spacing: 12) {
                    sectionTitle("当日委托", record: record)
                    orderTable(record.orders)
                }
                .padding(20)
            }
        }
    }

    /// 分区标题：多账户时补一个账户名，避免几张没有归属的持仓/委托框混在一起
    private func sectionTitle(_ title: String, record: AccountRecords) -> some View {
        HStack(spacing: 8) {
            Text(title)
                .font(AppTheme.bodyFont.weight(.semibold))
                .foregroundColor(AppTheme.ink(scheme))
            if records.count > 1 {
                Text(record.label)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
            }
        }
    }


    /// 资金指标：账户与资金四项 + 盈亏与费用。
    /// 直接铺在账户卡片上（不再套内层底色面板），按 5 列排两行减少占用高度。
    private func metricsRow(_ account: AccountSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 0) {
                metric("账户权益", formatMoney(account.balance), nil)
                metric("可用资金", formatMoney(account.available), nil)
                metric("保证金", formatMoney(account.margin), nil)
                metric("风险度", formatPercent(account.riskRatio), nil, help: riskRatioHelp)
                metric("昨日权益", formatMoney(account.preBalance), nil)
            }
            HStack(alignment: .top, spacing: 0) {
                metric("浮动盈亏", formatSigned(account.floatProfit), profitColor(account.floatProfit), help: floatProfitHelp)
                metric("持仓盈亏", formatSigned(account.positionProfit), profitColor(account.positionProfit), help: positionProfitHelp)
                metric("平仓盈亏", formatSigned(account.closeProfit), profitColor(account.closeProfit), help: closeProfitHelp)
                metric("手续费", formatMoney(account.commission), nil, help: commissionHelp)
                // 占位保持两行列宽一致：第二行只有 4 项，缺的这格不能把上面的列挤宽
                Color.clear.frame(maxWidth: .infinity)
            }
        }
    }

    private func metric(_ title: String, _ value: String, _ color: Color?, help: String? = nil) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 4) {
                Text(title)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                if let help {
                    MetricHelpButton(title: title, message: help)
                }
            }
            Text(value)
                .font(AppTheme.bodyFont.weight(.medium))
                .foregroundColor(color ?? AppTheme.ink(scheme))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // ---- 表格区 ----

    private func positionTable(_ rows: [PositionRecord]) -> some View {
        let maxVarietyCount = rows.map { displayVariety($0.symbol).count }.max() ?? 2
        let varietyWidth: CGFloat = max(80, CGFloat(maxVarietyCount) * 8.5 + 16)
        let widths: [CGFloat?] = [varietyWidth, nil, 40, 44, nil, nil, nil, nil]
        let alignments: [Alignment] = [.leading, .leading, .center, .trailing, .trailing, .trailing, .trailing, .trailing]

        return recordTable(
            headers: ["品种", "合约", "方向", "手数", "开仓均价", "持仓均价", "浮动盈亏", "保证金"],
            headerHelps: [
                "开仓均价": openPriceHelp,
                "持仓均价": positionPriceHelp,
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
                        .text(formatPrice(row.positionPrice)),
                        .colored(formatSigned(row.floatProfit), profitColor(row.floatProfit) ?? AppTheme.ink(scheme)),
                        .text(formatMoney(row.margin)),
                    ],
                    widths: widths,
                    alignments: alignments
                )
            }
        }
    }

    private func orderTable(_ rows: [OrderRecord]) -> some View {
        let totalOrigin = rows.reduce(0) { $0 + $1.volumeOrigin }
        let filledCount = rows.filter { $0.volumeOrigin > $0.volumeLeft }.count
        let canceledCount = rows.filter { $0.volumeLeft > 0 && $0.statusText == "已完" }.count

        let maxOrderSourceCount = rows.map { displaySource($0.source).count }.max() ?? 2
        let orderSourceWidth: CGFloat = max(70, CGFloat(maxOrderSourceCount) * 8.5 + 16)
        let orderWidths: [CGFloat?] = [nil, nil, 36, 40, 40, 40, nil, nil, 60, orderSourceWidth]
        let orderAlignments: [Alignment] = [.leading, .leading, .center, .center, .trailing, .trailing, .trailing, .trailing, .center, .leading]

        return recordTable(
            headers: ["委托时间", "合约", "买卖", "开平", "手数", "成交", "委托价", "成交价", "状态", "来源"],
            widths: orderWidths,
            alignments: orderAlignments,
            maxHeight: nil,
            empty: "无委托记录",
            isEmpty: rows.isEmpty
        ) {
            ForEach(rows) { row in
                let isAbnormal = row.volumeLeft > 0 && row.statusText == "已完"
                let statusColor = isAbnormal ? AppTheme.danger(scheme) : AppTheme.dim(scheme)

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
                        .withTip(row.statusText, statusColor, row.lastMsg),
                        .text(displaySource(row.source)),
                    ],
                    widths: orderWidths,
                    alignments: orderAlignments
                )
            }
        } footer: {
            if !rows.isEmpty {
                summaryRow(
                    content: "共 \(rows.count) 笔委托 · 报单 \(totalOrigin) 手 · 已成 \(filledCount) 笔"
                        + (canceledCount > 0 ? " · 已撤/废单 \(canceledCount) 笔" : "")
                )
            }
        }
    }

    /// 从合约代码提取品种代码与名称（格式为「英文码｜中文」，如「SA｜纯碱」）
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

    /// 解析策略的编码别名（如 supertrend -> 1003）
    private func strategyAlias(for key: String) -> String {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return "" }
        if let matched = appState.serverStrategies.first(where: {
            $0.id.caseInsensitiveCompare(trimmed) == .orderedSame ||
            $0.name.caseInsensitiveCompare(trimmed) == .orderedSame ||
            $0.alias.caseInsensitiveCompare(trimmed) == .orderedSame
        }) {
            if !matched.alias.isEmpty {
                return matched.alias
            }
            if !matched.name.isEmpty && matched.name.caseInsensitiveCompare(matched.id) != .orderedSame {
                return matched.name
            }
            return matched.id
        }
        // 内置策略兜底映射（即使网络尚未拉取到策略列表也能正确显示）
        if trimmed.lowercased() == "supertrend" {
            return "1003"
        }
        return trimmed
    }

    /// 来源显示：策略｜1003 格式，手动显示为 手动
    private func displaySource(_ source: String) -> String {
        let trimmed = source.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty || trimmed == "手动/外部" || trimmed == "手动" {
            return "手动"
        }
        if trimmed.hasPrefix("策略｜") {
            let inner = String(trimmed.dropFirst(3))
            return "策略｜\(strategyAlias(for: inner))"
        }
        if trimmed.hasPrefix("策略|") {
            let inner = String(trimmed.dropFirst(3))
            return "策略｜\(strategyAlias(for: inner))"
        }
        if trimmed.hasPrefix("策略 | ") {
            let inner = String(trimmed.dropFirst(5))
            return "策略｜\(strategyAlias(for: inner))"
        }
        if trimmed == "智能信号" {
            if let sim = appState.accounts.first(where: { $0.kind == kind.rawValue }),
               !sim.strategy.isEmpty {
                return "策略｜\(strategyAlias(for: sim.strategy))"
            }
            if let first = appState.serverStrategies.first, !first.alias.isEmpty || !first.name.isEmpty {
                let alias = !first.alias.isEmpty ? first.alias : first.name
                return "策略｜\(alias)"
            }
            return "策略｜智能信号"
        }
        let alias = strategyAlias(for: trimmed)
        return "策略｜\(alias)"
    }

    /// 从当前所有成交记录中提取可用且去重的品种列表（格式如「SA｜纯碱」）
    private func availableVarieties(from trades: [TradeRecord]) -> [String] {
        var set = Set<String>()
        var list: [String] = []
        for trade in trades {
            let v = displayVariety(trade.symbol)
            if !v.isEmpty && !set.contains(v) {
                set.insert(v)
                list.append(v)
            }
        }
        return list.sorted()
    }

    /// 从当前所有成交记录中提取可用且去重的策略列表
    private func availableStrategies(from trades: [TradeRecord]) -> [String] {
        var set = Set<String>()
        var list: [String] = []
        for trade in trades {
            let s = displaySource(trade.source)
            if !s.isEmpty && !set.contains(s) {
                set.insert(s)
                list.append(s)
            }
        }
        return list.sorted()
    }

    /// 根据选中的品种与策略过滤成交记录，并根据最新在前/在后排序
    private func filterTrades(_ trades: [TradeRecord]) -> [TradeRecord] {
        let filtered = trades.filter { trade in
            let matchVariety = filterVariety.isEmpty || displayVariety(trade.symbol) == filterVariety
            let matchStrategy = filterStrategy.isEmpty || displaySource(trade.source) == filterStrategy
            return matchVariety && matchStrategy
        }
        return newestFirst ? filtered : filtered.reversed()
    }

    /// 成交编号展示：显示成交编号后8位（宽度自适应），兜底显示行号
    private func displayTradeNo(_ trade: TradeRecord, fallbackIndex: Int) -> String {
        let trimmed = trade.id.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty {
            return trimmed.count > 8 ? String(trimmed.suffix(8)) : trimmed
        }
        return "\(fallbackIndex)"
    }

    /// 解析成交时间为日期和精确到毫秒的时间
    private func parseTradeTime(_ raw: String) -> (date: String, clock: String) {
        let parts = raw.split(separator: " ")
        let dateStr = parts.first.map(String.init) ?? raw
        var clockStr = parts.count > 1 ? String(parts[1]) : ""
        if !clockStr.isEmpty && !clockStr.contains(".") && clockStr.count == 8 {
            clockStr += ".000"
        }
        return (dateStr, clockStr)
    }

    private func tradeTable(_ rows: [TradeRecord], accountLabel: String) -> some View {
        let totalVol = rows.reduce(0) { $0 + $1.volume }
        let openVol = rows.filter { $0.offsetText == "开仓" }.reduce(0) { $0 + $1.volume }
        let closeVol = rows.filter { $0.offsetText != "开仓" }.reduce(0) { $0 + $1.volume }
        let closeProfits = rows.compactMap { $0.realizedProfit }
        let hasCloseProfit = !closeProfits.isEmpty
        let totalRealized = closeProfits.reduce(0, +)

        // 编号列宽度自适应：根据成交编号后8位实际长度动态计算
        let maxNoCount = rows.enumerated().map { displayTradeNo($0.element, fallbackIndex: $0.offset + 1).count }.max() ?? 2
        let noColWidth: CGFloat = max(42, CGFloat(maxNoCount) * 8.0 + 16)

        // 动态计算品种列和来源列的自适应宽度，留足文本边距，避免出现截断省略号
        let maxVarietyCount = rows.map { displayVariety($0.symbol).count }.max() ?? 2
        let varietyWidth: CGFloat = max(80, CGFloat(maxVarietyCount) * 8.5 + 16)

        let maxSourceCount = rows.map { displaySource($0.source).count }.max() ?? 2
        let sourceWidth: CGFloat = max(70, CGFloat(maxSourceCount) * 8.5 + 16)

        let headers = ["编号", "日期", "时间", "品种", "来源", "开平", "买卖", "成交价", "手数", "平仓盈亏"]
        let widths: [CGFloat?] = [noColWidth, 80, 92, varietyWidth, sourceWidth, 40, 36, 68, 40, 76]
        let alignments: [Alignment] = [.center, .leading, .leading, .leading, .leading, .center, .center, .trailing, .trailing, .trailing]

        return recordTable(
            headers: headers,
            widths: widths,
            alignments: alignments,
            trailingSpacer: true,
            maxHeight: 460,
            empty: "无成交记录",
            isEmpty: rows.isEmpty
        ) {
            ForEach(Array(rows.enumerated()), id: \.element.id) { index, row in
                let profitCell: Cell = {
                    if row.offsetText == "开仓" {
                        return .colored("—", AppTheme.dim(scheme))
                    }
                    if let profit = row.realizedProfit {
                        return .colored(formatSigned(profit), profitColor(profit) ?? AppTheme.ink(scheme))
                    }
                    return .colored("—", AppTheme.dim(scheme))
                }()

                let timeInfo = parseTradeTime(row.tradeTime)
                let dateStr = timeInfo.date
                let clockStr = timeInfo.clock
                let variety = displayVariety(row.symbol)
                let sourceDisplay = displaySource(row.source)
                let tradeNo = displayTradeNo(row, fallbackIndex: index + 1)

                Button {
                    selectedTradeModal = TradeDetailModal(trade: row, accountLabel: accountLabel)
                } label: {
                    tableRow(
                        cells: [
                            .text(tradeNo),
                            .text(dateStr),
                            .text(clockStr),
                            .text(variety),
                            .text(sourceDisplay),
                            .text(row.offsetText),
                            .colored(row.directionText, sideColor(row.directionText)),
                            .text(formatPrice(row.price)),
                            .text("\(row.volume)"),
                            profitCell,
                        ],
                        widths: widths,
                        alignments: alignments,
                        trailingSpacer: true
                    )
                    .contentShape(Rectangle())
                }
                .buttonStyle(TableRowButtonStyle(scheme: scheme))
                .pointerCursor()
            }
        } footer: {
            if !rows.isEmpty {
                summaryRow(
                    content: "共 \(rows.count) 笔成交 · 累计 \(totalVol) 手（开仓 \(openVol) 手 / 平仓 \(closeVol) 手）",
                    highlighted: hasCloseProfit ? (" · 平仓盈亏合计: " + formatSigned(totalRealized), profitColor(totalRealized) ?? AppTheme.ink(scheme)) : nil
                )
            }
        }
    }

    private func sourceBadge(_ source: String) -> Cell {
        let isSmart = source != "手动/外部" && source != "手动"
        let fg = isSmart ? AppTheme.primary(scheme) : AppTheme.dim(scheme)
        let bg = isSmart ? AppTheme.primary(scheme).opacity(0.12) : AppTheme.soft(scheme)
        return .badge(source, fg, bg)
    }

    // ---- 成交明细弹窗 ----

    private func tradeDetailSheet(_ detail: TradeDetailModal) -> some View {
        let trade = detail.trade
        let variety = displayVariety(trade.symbol)
        let sourceDisplay = displaySource(trade.source)

        return VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("成交明细")
                    .font(AppTheme.titleFont)
                    .foregroundColor(AppTheme.ink(scheme))
                Spacer()
                AppButton("关闭", variant: .secondary, size: .inline) {
                    selectedTradeModal = nil
                }
            }

            HairlineDivider()

            VStack(spacing: 12) {
                detailRow("合约代码", trade.symbol, isHighlighted: true)
                detailRow("交易品种", variety)
                detailRow("买卖方向", trade.directionText, color: sideColor(trade.directionText))
                detailRow("开平性质", trade.offsetText)
                detailRow("成交价格", formatPrice(trade.price))
                detailRow("成交手数", "\(trade.volume) 手")
                if trade.offsetText != "开仓", let profit = trade.realizedProfit {
                    detailRow("平仓盈亏", formatSigned(profit), color: profitColor(profit))
                }
                if let commission = trade.commission {
                    detailRow("手续费", formatMoney(commission))
                }
                detailRow("来源策略", sourceDisplay == "手动" ? "手动下单" : sourceDisplay)
                detailRow("成交时间", trade.tradeTime)
                if !trade.id.isEmpty {
                    detailRow("成交编号", trade.id)
                }
                if !trade.orderId.isEmpty {
                    detailRow("委托编号", trade.orderId)
                }
                detailRow("所属账户", detail.accountLabel)
            }
            .padding(14)
            .background(AppTheme.soft(scheme))
            .cornerRadius(12)
        }
        .padding(20)
        .frame(width: 380)
    }

    private func detailRow(_ label: String, _ value: String, color: Color? = nil, isHighlighted: Bool = false) -> some View {
        HStack {
            Text(label)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .frame(width: 72, alignment: .leading)
            Spacer()
            Text(value)
                .font(isHighlighted ? AppTheme.bodyFont.weight(.semibold) : AppTheme.bodyFont)
                .foregroundColor(color ?? (isHighlighted ? AppTheme.primary(scheme) : AppTheme.ink(scheme)))
        }
    }

    private func summaryRow(content: String, highlighted: (String, Color)? = nil) -> some View {
        VStack(spacing: 0) {
            HairlineDivider()
            HStack(spacing: 2) {
                Text(content)
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                if let (highText, highColor) = highlighted {
                    Text(highText)
                        .font(AppTheme.dimFont.weight(.semibold))
                        .foregroundColor(highColor)
                }
                Spacer()
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
        }
    }

    private enum Cell {
        case text(String)
        case colored(String, Color)
        case badge(String, Color, Color)
        case capsule(String)
        case withTip(String, Color, String)
        case headerWithHelp(String, String)
    }

    private func recordTable<Rows: View>(
        headers: [String],
        headerHelps: [String: String] = [:],
        widths: [CGFloat?],
        alignments: [Alignment]? = nil,
        trailingSpacer: Bool = false,
        maxHeight: CGFloat? = 420,
        empty: String,
        isEmpty: Bool,
        @ViewBuilder rows: () -> Rows
    ) -> some View {
        recordTable(
            headers: headers,
            headerHelps: headerHelps,
            widths: widths,
            alignments: alignments,
            trailingSpacer: trailingSpacer,
            maxHeight: maxHeight,
            empty: empty,
            isEmpty: isEmpty,
            rows: rows,
            footer: { EmptyView() }
        )
    }

    private func recordTable<Rows: View, Footer: View>(
        headers: [String],
        headerHelps: [String: String] = [:],
        widths: [CGFloat?],
        alignments: [Alignment]? = nil,
        trailingSpacer: Bool = false,
        maxHeight: CGFloat? = 420,
        empty: String,
        isEmpty: Bool,
        @ViewBuilder rows: () -> Rows,
        @ViewBuilder footer: () -> Footer
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
                font: AppTheme.dimFont.weight(.semibold),
                trailingSpacer: trailingSpacer
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
                footer()
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
        alignments: [Alignment]? = nil,
        font: Font = AppTheme.dimFont,
        trailingSpacer: Bool = false
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
                    .frame(
                        width: colWidth,
                        alignment: align
                    )
                    .frame(
                        maxWidth: (colWidth == nil && !trailingSpacer) ? .infinity : nil,
                        alignment: align
                    )
            }
            if trailingSpacer {
                Spacer(minLength: 0)
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
            case .badge(let text, let fg, let bg):
                Text(text)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(fg)
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(bg)
                    .cornerRadius(6)
            case .capsule(let text):
                Text(text)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(AppTheme.ink(scheme))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(AppTheme.chrome(scheme))
                    .cornerRadius(6)
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .stroke(AppTheme.hairline(scheme), lineWidth: 1)
                    )
            case .withTip(let text, let color, let tip):
                HStack(spacing: 3) {
                    Text(text).foregroundColor(color)
                    if !tip.isEmpty && tip != "全部成交" {
                        Image(systemName: "exclamationmark.circle.fill")
                            .font(.system(size: 10))
                            .foregroundColor(AppTheme.danger(scheme))
                    }
                }
                .help(tip.isEmpty ? text : tip)
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

/// 表格行悬停高亮按钮样式
struct TableRowButtonStyle: ButtonStyle {
    let scheme: ColorScheme

    func makeBody(configuration: Configuration) -> some View {
        TableRowButtonContent(configuration: configuration, scheme: scheme)
    }

    private struct TableRowButtonContent: View {
        let configuration: Configuration
        let scheme: ColorScheme
        @State private var hovering = false

        var body: some View {
            configuration.label
                .background(
                    configuration.isPressed
                        ? AppTheme.soft(scheme)
                        : (hovering ? AppTheme.sidebarHover(scheme) : Color.clear)
                )
                .cornerRadius(6)
                .onHover { hovering = $0 }
        }
    }
}
