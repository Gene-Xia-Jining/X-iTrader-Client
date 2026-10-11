import SwiftUI

/// 仪表盘：状态指标卡 + 最近信号 + 最近日志（像素级对应 Qt DashboardPage）。
struct DashboardView: View {
    @EnvironmentObject var appState: AppState
    @Binding var selection: AppRoute
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        PageContainer {
            PageHeader(title: "仪表盘", subtitle: "服务器连接与自动交易运行状态总览。")

            HStack(spacing: 16) {
                StatusStatCard(
                    title: "服务器状态",
                    value: serverConnected ? "已连接" : "未连接",
                    valueColor: serverConnected ? AppTheme.statusGreen : AppTheme.statusRed,
                    secondary: appState.config.serverUrl.isEmpty ? "未配置服务器地址" : appState.config.serverUrl
                )
                StatusStatCard(
                    title: "交易状态",
                    value: tradingStatusText,
                    valueColor: appState.engineStatus.tradingActive ? AppTheme.statusGreen : AppTheme.statusRed,
                    secondary: appState.engineStatus.tradingActive ? "自动交易运行中" : "自动交易未启动"
                )
                ValueStatCard(
                    title: "账户资金",
                    value: "\(enabledLiveCount) 实盘 / \(enabledSimCount) 模拟",
                    secondary: "初始资金 (元)"
                )
                ValueStatCard(
                    title: "活跃品种",
                    value: "\(activeSymbolCount)",
                    secondary: "已订阅行情品种"
                )
            }

            AppCard {
                VStack(alignment: .leading, spacing: 10) {
                    Text("最近信号")
                        .font(AppTheme.bodyFont)
                        .foregroundColor(AppTheme.dim(scheme))
                    Text("暂无信号 — 服务器信号将在此显示")
                        .font(AppTheme.dimFont)
                        .foregroundColor(AppTheme.dim(scheme))
                        .frame(maxWidth: .infinity, alignment: .center)
                }
                .padding(.vertical, 16)
                .padding(.horizontal, 20)
            }

            AppCard {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 10) {
                        Text("最近日志")
                            .font(AppTheme.bodyFont)
                            .foregroundColor(AppTheme.dim(scheme))
                        Spacer()
                        AppButton("清空日志", variant: .flat) { appState.logs.removeAll() }
                    }
                    logPreview
                }
                .padding(.vertical, 16)
                .padding(.horizontal, 20)
            }

            HStack(spacing: 10) {
                AppButton("配置设置", variant: .secondary) { selection = .settings }
                AppButton("Token 管理", variant: .secondary) { selection = .token }
                Spacer()
            }
        }
    }

    // ---- 数据推导（对应 Qt MainViewModel / sync_accounts_to_dashboard）----

    private var serverConnected: Bool {
        appState.engineStatus.serverOnline
    }

    private var tradingStatusText: String {
        let running = appState.engineStatus.accounts.filter(\.running).count
        return running > 0 ? "运行中 (\(running))" : "已停止"
    }

    private var enabledLiveCount: Int {
        appState.accounts.filter { $0.kind == "live" && $0.enabled }.count
    }

    private var enabledSimCount: Int {
        appState.accounts.filter { $0.kind == "sim" && $0.enabled }.count
    }

    private var activeSymbolCount: Int {
        var symbols = Set<String>()
        for account in appState.accounts where account.enabled {
            symbols.formUnion(account.symbols)
        }
        return symbols.count
    }

    /// 最近 4 条日志预览（Qt logPreview maximumBlockCount=4，高 120）
    private var logPreview: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 0) {
                ForEach(appState.logs.suffix(4)) { line in
                    Text("\(timeText(line.time)) [\(line.level)] \(line.message)")
                        .font(AppTheme.monoFont)
                        .foregroundColor(line.color(for: scheme))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 2)
                }
            }
        }
        .frame(height: 120)
    }

    private func timeText(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter.string(from: date)
    }
}

// MARK: - 指标卡（StatusStatCard / ValueStatCard）

/// 状态指标卡：标题 + 状态药丸（点 + 语义色文字）+ 说明行
struct StatusStatCard: View {
    let title: String
    let value: String
    let valueColor: Color
    let secondary: String
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
            HStack(spacing: 10) {
                Circle().fill(valueColor).frame(width: 10, height: 10)
                Text(value)
                    .font(AppTheme.bodyFont.weight(.semibold))
                    .foregroundColor(valueColor)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(AppTheme.soft(scheme))
            .cornerRadius(16)
            .overlay(
                RoundedRectangle(cornerRadius: 16)
                    .stroke(AppTheme.hairline(scheme), lineWidth: 1)
            )
            Text(secondary)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .lineLimit(1)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(AppTheme.card(scheme))
        .cornerRadius(18)
        .overlay(
            RoundedRectangle(cornerRadius: 18)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
    }
}

/// 数值指标卡：标题 + 26px 大数字 + 说明行
struct ValueStatCard: View {
    let title: String
    let value: String
    let secondary: String
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
            Text(value)
                .font(AppTheme.metricFont)
                .kerning(-0.3)
                .foregroundColor(AppTheme.ink(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.5)
            Text(secondary)
                .font(AppTheme.dimFont)
                .foregroundColor(AppTheme.dim(scheme))
                .lineLimit(1)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(AppTheme.card(scheme))
        .cornerRadius(18)
        .overlay(
            RoundedRectangle(cornerRadius: 18)
                .stroke(AppTheme.hairline(scheme), lineWidth: 1)
        )
    }
}
