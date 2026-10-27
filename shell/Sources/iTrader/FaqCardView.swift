import SwiftUI

/// 常见问题卡片（模拟交易页 / 仪表盘共用）：
/// 整行可点展开/收起，展开后只列问题，点问题弹窗看详细。
/// 展开状态按 expandedKey 存进 UserDefaults，各页面用各自的键，互不影响。
struct FaqCardView: View {
    /// 每条问答：列表里只显示问题，答案在弹窗里展开
    struct Item: Identifiable {
        let question: String
        let answer: String
        var id: String { question }
    }

    let items: [Item]
    /// 展开状态的 UserDefaults 键
    let expandedKey: String

    @Environment(\.colorScheme) private var scheme
    @State private var expanded: Bool
    @State private var active: Item?

    init(items: [Item], expandedKey: String) {
        self.items = items
        self.expandedKey = expandedKey
        _expanded = State(initialValue: UserDefaults.standard.bool(forKey: expandedKey))
    }

    var body: some View {
        AppCard {
            VStack(alignment: .leading, spacing: 0) {
                // 整行可点：避免只能点小箭头，标题区域也就成了命中区
                Button {
                    withAnimation(.easeInOut(duration: 0.18)) {
                        expanded.toggle()
                    }
                    UserDefaults.standard.set(expanded, forKey: expandedKey)
                } label: {
                    HStack(spacing: 8) {
                        Text("常见问题")
                            .font(AppTheme.bodyFont.weight(.semibold))
                            .foregroundColor(AppTheme.ink(scheme))
                        Spacer(minLength: 12)
                        Text(expanded ? "收起" : "展开")
                            .font(AppTheme.dimFont)
                            .foregroundColor(AppTheme.primary(scheme))
                        Image(systemName: "chevron.down")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundColor(AppTheme.primary(scheme))
                            .rotationEffect(.degrees(expanded ? 180 : 0))
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .pointerCursor()

                if expanded {
                    VStack(alignment: .leading, spacing: 0) {
                        HairlineDivider()
                            .padding(.bottom, 4)
                        ForEach(Array(items.enumerated()), id: \.offset) { index, item in
                            // 只列问题，点开弹窗看详细：答案较长，平铺会把卡片撑得很高
                            Button {
                                active = item
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
                            if index < items.count - 1 {
                                HairlineDivider()
                            }
                        }
                    }
                    .padding(.top, 12)
                }
            }
            .padding(20)
        }
        .sheet(item: $active) { item in
            detail(item)
        }
    }

    /// 问答详情弹窗：问题作标题，答案按正文；「关闭」放在标题行右侧（同其它弹窗）
    private func detail(_ item: Item) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Text(item.question)
                    .font(AppTheme.bodyFont.weight(.semibold))
                    .foregroundColor(AppTheme.ink(scheme))
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 12)
                AppButton("关闭", variant: .secondary, size: .inline) {
                    active = nil
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
}
