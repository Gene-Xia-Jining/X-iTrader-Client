import SwiftUI

/// 系统日志页：内存会话日志（像素级对应 Qt LogPage 的 logView）。
struct TradingLogView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    /// Qt QTextEdit 追加式时间戳 [HH:mm:ss]
    private static let timeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter
    }()

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            PageHeader(title: "系统日志", subtitle: "实时查看客户端连接、信号、下单和异常信息。")

            HStack(spacing: 10) {
                Text("本地会话日志，保存在内存中")
                    .font(AppTheme.dimFont)
                    .foregroundColor(AppTheme.dim(scheme))
                Spacer()
                AppButton("清空日志", variant: .secondary) { appState.logs.removeAll() }
            }

            logView
        }
        .padding(24)
    }

    private var logView: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(appState.logs) { line in
                        Text("[\(Self.timeFormatter.string(from: line.time))] [\(line.level)] \(line.message)")
                            .font(AppTheme.monoFont)
                            .foregroundColor(line.color(for: scheme))
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 1)
                            .textSelection(.enabled)
                            .id(line.id)
                    }
                }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(AppTheme.logBg(scheme))
            .cornerRadius(11)
            .overlay(
                RoundedRectangle(cornerRadius: 11)
                    .stroke(AppTheme.hairline(scheme), lineWidth: 1)
            )
            .onChange(of: appState.logs.count) { _ in
                if let last = appState.logs.last {
                    proxy.scrollTo(last.id, anchor: .bottom)
                }
            }
        }
    }
}
