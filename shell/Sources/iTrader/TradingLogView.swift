import AppKit
import SwiftUI

/// 系统日志页：内存会话日志（像素级对应 Qt LogPage 的 logView）。
struct TradingLogView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

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

            LogTextView(
                lines: appState.logs,
                dark: scheme == .dark
            )
            // NSScrollView 没有固有尺寸，不显式撑满就会塌成内容高度
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(AppTheme.logBg(scheme))
            .cornerRadius(11)
            .overlay(
                RoundedRectangle(cornerRadius: 11)
                    .stroke(AppTheme.hairline(scheme), lineWidth: 1)
            )
        }
        .padding(24)
    }
}

/// 日志正文：用只读 NSTextView 承载。
///
/// 为什么不用 SwiftUI 的 Text(.textSelection(.enabled))：那样每行或整段 AttributedString
/// 的复制只能取到第一行（实测拖选跨四行、Cmd+C 只得到第一行）。NSTextView 的选中与复制
/// 是 AppKit 原生实现，跨行选择、三击整行、Cmd+A 全选都能正确复制。
private struct LogTextView: NSViewRepresentable {
    let lines: [LogLine]
    let dark: Bool

    /// Qt QTextEdit 追加式时间戳 [HH:mm:ss]
    private static let timeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter
    }()

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .noBorder

        guard let textView = scroll.documentView as? NSTextView else { return scroll }
        textView.isEditable = false
        textView.isSelectable = true
        // 允许跨行拖选后直接 Cmd+C，也能三击选中整行
        textView.isRichText = true
        textView.drawsBackground = false
        textView.textContainerInset = NSSize(width: 12, height: 12)
        textView.textContainer?.widthTracksTextView = true
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let textView = scroll.documentView as? NSTextView else { return }

        let attributed = Self.attributed(lines: lines, dark: dark)
        // 文本未变就不重设，避免每次状态刷新都清掉用户的选区
        if textView.textStorage?.string != attributed.string {
            textView.textStorage?.setAttributedString(attributed)
            // 追加式日志：内容变化后滚到底部，与 Qt logView 行为一致
            textView.scrollToEndOfDocument(nil)
        }
    }

    /// 每行一个段落，按级别着色（对应 Qt viewmodels 的 log_colors）
    private static func attributed(lines: [LogLine], dark: Bool) -> NSAttributedString {
        let result = NSMutableAttributedString()
        let paragraph = NSMutableParagraphStyle()
        // 原来每行靠 2pt 垂直 padding 拉开，改用行距补回观感
        paragraph.lineSpacing = 2

        let font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        for (index, line) in lines.enumerated() {
            let color = NSColor(line.color(for: dark ? .dark : .light))
            let text = index == 0
                ? Self.format(line)
                : "\n" + Self.format(line)
            result.append(NSAttributedString(
                string: text,
                attributes: [
                    .font: font,
                    .foregroundColor: color,
                    .paragraphStyle: paragraph,
                ]
            ))
        }
        return result
    }

    private static func format(_ line: LogLine) -> String {
        "[\(timeFormatter.string(from: line.time))] [\(line.level)] \(line.message)"
    }
}
