import SwiftUI

/// Token 管理页：申请、刷新并查看服务器审批状态（像素级对应 Qt TokenPage）。
struct TokenManageView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    @State private var description = ""
    @State private var statusAlert: String?

    private struct ColumnHelp: Identifiable {
        let id = UUID()
        let title: String
        let message: String
    }
    @State private var columnHelp: ColumnHelp?

    var body: some View {
        PageContainer {
            PageHeader(title: "Token 管理", subtitle: "申请、刷新并查看服务器审批状态。")

            AppCard {
                VStack(alignment: .leading, spacing: 12) {
                    HStack(alignment: .top, spacing: 12) {
                        Text("申请说明:")
                            .font(AppTheme.bodyFont)
                            .foregroundColor(AppTheme.ink(scheme))
                            .frame(width: 64, alignment: .trailing)
                        AppTextField(placeholder: "例如：iTrader 智能交易系统", text: $description)
                    }

                    HStack(alignment: .top, spacing: 12) {
                        Text("审批状态:")
                            .font(AppTheme.bodyFont)
                            .foregroundColor(AppTheme.ink(scheme))
                            .frame(width: 64, alignment: .trailing)
                        VStack(alignment: .leading, spacing: 6) {
                            Text(appState.tokenStatus.isEmpty ? "未申请" : appState.tokenStatus)
                                .font(AppTheme.bodyFont.weight(.semibold))
                                .foregroundColor(statusColor)
                            Text("提交申请后等待服务器审批，可通过刷新状态查看进度")
                                .font(AppTheme.dimFont)
                                .foregroundColor(AppTheme.dim(scheme))
                        }
                    }

                    HairlineDivider()

                    HStack(spacing: 10) {
                        AppButton("申请 Token", variant: .primary) { apply() }
                        AppButton("刷新状态", variant: .secondary) { appState.refreshTokenStatus() }
                        AppButton("查看状态", variant: .secondary, disabled: !approved) {
                            statusAlert = appState.tokenStatus
                        }
                        Spacer()
                    }
                }
                .padding(20)
            }

            AppCard {
                VStack(alignment: .leading, spacing: 12) {
                    Text("申请记录")
                        .font(AppTheme.bodyFont.weight(.semibold))
                        .foregroundColor(AppTheme.ink(scheme))

                    if appState.tokenRecords.isEmpty {
                        Text("暂无申请记录")
                            .font(AppTheme.dimFont)
                            .foregroundColor(AppTheme.dim(scheme))
                    } else {
                        recordHeader
                        ForEach(Array(appState.tokenRecords.enumerated()), id: \.element.id) { index, record in
                            HairlineDivider()
                            recordRow(index: index + 1, record: record)
                        }
                    }
                }
                .padding(20)
            }
            .alert(
                columnHelp?.title ?? "",
                isPresented: Binding(get: { columnHelp != nil }, set: { if !$0 { columnHelp = nil } }),
                presenting: columnHelp
            ) { _ in
                Button("好") {}
            } message: { help in
                Text(help.message)
            }
        }
        .onAppear { appState.refreshTokenStatus() }
        .alert(
            "Token 审批状态",
            isPresented: Binding(get: { statusAlert != nil }, set: { if !$0 { statusAlert = nil } }),
            presenting: statusAlert
        ) { _ in
            Button("好") {}
        } message: { status in
            Text(status)
        }
    }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f
    }()

    private var recordHeader: some View {
        HStack(spacing: 12) {
            headerItem(
                title: "编号",
                helpMessage: "当前申请记录的序号（从 1 开始）。",
                width: 52
            )
            headerItem(
                title: "申请说明",
                helpMessage: "向服务器提交 Token 申请时填写的用途或备注说明。"
            )
            headerItem(
                title: "申请时间",
                helpMessage: "向服务器发起该笔 Token 申请的提交时间。",
                width: 130
            )
            headerItem(
                title: "过期时间",
                helpMessage: "Token 的有效期截止时间，过期后需重新申请。",
                width: 130
            )
            headerItem(
                title: "已使用",
                helpMessage: "该 Token 签发后已被客户端用于连接与鉴权的累计调用次数。",
                width: 65
            )
            headerItem(
                title: "状态",
                helpMessage: "当前 Token 的审批状态：\n• 待审核：申请已提交，等待管理员审批\n• 已通过：审批通过，Token 有效可用\n• 已拒绝：申请未获批准",
                width: 70
            )
        }
        .font(AppTheme.dimFont.weight(.semibold))
        .foregroundColor(AppTheme.dim(scheme))
    }

    @ViewBuilder
    private func headerItem(
        title: String,
        helpMessage: String,
        width: CGFloat? = nil
    ) -> some View {
        let button = Button {
            columnHelp = ColumnHelp(title: title, message: helpMessage)
        } label: {
            HStack(spacing: 3) {
                Text(title)
                Image(systemName: "questionmark.circle")
                    .font(.system(size: 10))
                    .foregroundColor(AppTheme.dim(scheme).opacity(0.8))
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .foregroundColor(AppTheme.dim(scheme))
        .help("点击查看「\(title)」含义")

        if let width = width {
            button.frame(width: width, alignment: .leading)
        } else {
            button.frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func recordRow(index: Int, record: TokenRecord) -> some View {
        HStack(spacing: 12) {
            Text("\(index)")
                .frame(width: 52, alignment: .leading)
            Text(record.description.isEmpty ? "—" : record.description)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(record.createdAt.map { Self.timeFormatter.string(from: $0) } ?? "—")
                .frame(width: 130, alignment: .leading)
            Text(record.expiresAt.map { Self.timeFormatter.string(from: $0) } ?? "—")
                .frame(width: 130, alignment: .leading)
            Text("\(record.useCount)")
                .frame(width: 65, alignment: .leading)
            Text(record.statusText)
                .foregroundColor(recordColor(record.status))
                .frame(width: 70, alignment: .leading)
        }
        .font(AppTheme.dimFont)
        .foregroundColor(AppTheme.ink(scheme))
    }

    /// 记录状态语义色（已通过绿 / 已拒绝红 / 待审核黄）
    private func recordColor(_ status: String) -> Color {
        switch status {
        case "approved": return AppTheme.success(scheme)
        case "rejected": return AppTheme.danger(scheme)
        default: return AppTheme.warning(scheme)
        }
    }

    private var approved: Bool {
        appState.tokenStatus.contains("已通过")
    }

    /// 状态语义色（对应 Qt status_color：已通过绿 / 失败错误红 / 其余黄）
    private var statusColor: Color {
        let text = appState.tokenStatus
        if text.isEmpty || text == "未申请" { return AppTheme.warning(scheme) }
        if text.contains("已通过") { return AppTheme.success(scheme) }
        if text.contains("失败") || text.contains("错误") { return AppTheme.danger(scheme) }
        return AppTheme.warning(scheme)
    }

    private func apply() {
        let text = description.trimmingCharacters(in: .whitespaces)
        appState.requestToken(description: text.isEmpty ? "iTrader 智能交易系统" : text)
    }
}
