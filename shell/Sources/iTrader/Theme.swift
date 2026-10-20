import SwiftUI

// MARK: - 设计令牌（对应 src/presentation/theme.py：Apple 风格明暗两套）

enum AppTheme {
    // 表面色
    static func canvas(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x272729) : Color(hex: 0xF5F5F7) }
    static func chrome(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x252527) : Color(hex: 0xFFFFFF) }
    static func card(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x272729) : Color(hex: 0xFFFFFF) }
    static func hairline(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x3A3A3C) : Color(hex: 0xE0E0E0) }
    static func divider(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x3A3A3C) : Color(hex: 0xF0F0F0) }
    static func ink(_ s: ColorScheme) -> Color { s == .dark ? Color.white : Color(hex: 0x1D1D1F) }
    static func dim(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x86868B) : Color(hex: 0x7A7A7A) }
    static func primary(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x2997FF) : Color(hex: 0x0066CC) }
    static func primaryHover(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x4AA3FF) : Color(hex: 0x0071E3) }
    static func danger(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0xFF453A) : Color(hex: 0xE3342F) }
    static func dangerHover(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0xFF5A50) : Color(hex: 0xC5241F) }
    static func success(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x30D158) : Color(hex: 0x34C759) }
    static func successHover(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x46DD6A) : Color(hex: 0x30D158) }
    static func inputBg(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x2A2A2C) : Color(hex: 0xFFFFFF) }
    static func sidebarHover(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x303032) : Color(hex: 0xF0F0F0) }
    /// 侧栏选中态：主题蓝底 + 白字（与 Windows 壳的 Palette.SidebarSelected 对齐）
    static func sidebarSelected(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x2997FF) : Color(hex: 0x0066CC) }
    static func logBg(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x252527) : Color(hex: 0xFAFAFC) }
    static func soft(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0x2A2A2C) : Color(hex: 0xFAFAFC) }
    static func warning(_ s: ColorScheme) -> Color { s == .dark ? Color(hex: 0xFFD60A) : Color(hex: 0xB8780A) }

    // 固定色
    static let link = Color(hex: 0x007AFF)
    static let statusGreen = Color(hex: 0x2ECC71)
    static let statusRed = Color(hex: 0xE74C3C)

    // 字号（QSS 像素值：正文 15、辅助 13、标题 22/600、指标 26/600、日志 12 等宽）
    static let bodyFont = Font.system(size: 15)
    static let dimFont = Font.system(size: 13)
    static let titleFont = Font.system(size: 22, weight: .semibold)
    static let metricFont = Font.system(size: 26, weight: .semibold)
    static let monoFont = Font.system(size: 12, design: .monospaced)
}

extension Color {
    init(hex: UInt32) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255
        )
    }
}

// MARK: - 通用组件

/// 1px 发丝分隔线（QFrame#divider）
struct HairlineDivider: View {
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        Rectangle()
            .fill(AppTheme.divider(scheme))
            .frame(height: 1)
    }
}

/// 白色圆角卡片（QFrame#card：radius 18、1px 发丝边框）
struct AppCard<Content: View>: View {
    @Environment(\.colorScheme) private var scheme
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        content
            .background(AppTheme.card(scheme))
            .cornerRadius(18)
            .overlay(
                RoundedRectangle(cornerRadius: 18)
                    .stroke(AppTheme.hairline(scheme), lineWidth: 1)
            )
    }
}

/// 页面标题 + 副标题（PageHeader）
struct PageHeader: View {
    let title: String
    let subtitle: String
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(AppTheme.titleFont)
                .kerning(-0.4)
                .foregroundColor(AppTheme.ink(scheme))
            Text(subtitle)
                .font(AppTheme.bodyFont)
                .foregroundColor(AppTheme.dim(scheme))
        }
    }
}

/// 页面容器（BasePage：外边距 24、行距 16、内容顶对齐）
struct PageContainer<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                content
                Spacer(minLength: 0)
            }
            .padding(24)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

/// 主题按钮（QSS QPushButton 胶囊样式及行内小按钮覆盖样式）
struct AppButton: View {
    enum Variant { case primary, secondary, danger, success, flat }
    enum Size {
        case regular   // 头部/页面级：高 36、胶囊圆角 18、水平 22、15px
        case inline    // 卡片行内：高 36、圆角 8、水平 14、13px
    }

    let title: String
    var variant: Variant = .primary
    var size: Size = .regular
    var disabled = false
    let action: () -> Void

    init(
        _ title: String,
        variant: Variant = .primary,
        size: Size = .regular,
        disabled: Bool = false,
        action: @escaping () -> Void
    ) {
        self.title = title
        self.variant = variant
        self.size = size
        self.disabled = disabled
        self.action = action
    }

    @Environment(\.colorScheme) private var scheme
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(size == .regular ? AppTheme.bodyFont.weight(.semibold) : AppTheme.dimFont.weight(.semibold))
                .foregroundColor(fgColor)
                .padding(.horizontal, size == .regular ? 22 : 14)
                .frame(minWidth: size == .regular ? 0 : 0, minHeight: 36)
                .background(bgColor)
                .overlay(
                    RoundedRectangle(cornerRadius: cornerRadius)
                        .stroke(borderColor, lineWidth: borderWidth)
                )
                .cornerRadius(cornerRadius)
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .onHover { hovering = $0 }
        .pointerCursor()
    }

    private var cornerRadius: CGFloat { size == .regular ? 18 : 8 }

    // QSS disabled：文字 #cccccc/#86868b；secondary 底透明、边框降一档
    private var disabledColor: Color { scheme == .dark ? Color(hex: 0x86868B) : Color(hex: 0xCCCCCC) }

    private var fgColor: Color {
        if disabled { return disabledColor }
        switch variant {
        case .primary, .danger, .success:
            return .white
        case .secondary, .flat:
            return variant == .flat
                ? AppTheme.dim(scheme)
                : AppTheme.primary(scheme)
        }
    }

    private var bgColor: Color {
        if disabled { return variant == .secondary || variant == .flat ? .clear : AppTheme.hairline(scheme) }
        switch variant {
        case .primary:
            return hovering ? AppTheme.primaryHover(scheme) : AppTheme.primary(scheme)
        case .danger:
            return hovering ? AppTheme.dangerHover(scheme) : AppTheme.danger(scheme)
        case .success:
            return hovering ? AppTheme.successHover(scheme) : AppTheme.success(scheme)
        case .secondary:
            return hovering ? AppTheme.canvas(scheme) : .clear
        case .flat:
            return hovering ? AppTheme.sidebarHover(scheme) : .clear
        }
    }

    private var borderColor: Color {
        if disabled { return variant == .secondary ? AppTheme.divider(scheme) : .clear }
        if variant == .secondary {
            return hovering ? AppTheme.dim(scheme) : AppTheme.hairline(scheme)
        }
        return .clear
    }

    private var borderWidth: CGFloat { variant == .secondary ? 1 : 0 }
}

/// 单行输入框（QLineEdit：radius 8、1px 边框、padding 9x12、高 36）
struct AppTextField: View {
    let placeholder: String
    var secure = false
    @Binding var text: String
    var disabled = false
    /// 回车或失焦时回调（用于无需点击保存即生效的字段）
    var onCommit: (() -> Void)? = nil

    @Environment(\.colorScheme) private var scheme
    @FocusState private var focused: Bool

    var body: some View {
        Group {
            if secure {
                SecureField(placeholder, text: $text)
                    .textFieldStyle(.plain)
            } else {
                TextField(placeholder, text: $text)
                    .textFieldStyle(.plain)
            }
        }
        .font(AppTheme.bodyFont)
        .foregroundColor(AppTheme.ink(scheme))
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity, minHeight: 36)
        .background(AppTheme.inputBg(scheme))
        .cornerRadius(8)
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .stroke(focused ? AppTheme.primary(scheme) : AppTheme.hairline(scheme), lineWidth: 1)
        )
        .disabled(disabled)
        .focused($focused)
        .onSubmit { onCommit?() }
        .onChange(of: focused) { isFocused in
            // 失焦同样视为输入完成；未提供回调时保持纯展示
            if !isFocused { onCommit?() }
        }
    }
}

extension View {
    /// 手型光标（Qt setCursor(PointingHandCursor) 的对应物）
    func pointerCursor() -> some View {
        onHover { hovering in
            if hovering {
                NSCursor.pointingHand.push()
            } else {
                NSCursor.pop()
            }
        }
    }
}
