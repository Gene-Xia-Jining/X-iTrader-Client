import SwiftUI

/// Token 管理页：申请、刷新并查看服务器审批状态（像素级对应 Qt TokenPage）。
struct TokenManageView: View {
    @EnvironmentObject var appState: AppState
    @Environment(\.colorScheme) private var scheme

    @State private var description = ""

    /// 列含义说明弹窗（点击表头问号触发）
    private struct ColumnHelp {
        let title: String
        let message: String
    }

    /// 页面弹窗：同一视图只挂一个 .alert，避免多个 alert 修饰符互相抢占
    private enum PageAlert: Identifiable {
        case columnHelp(ColumnHelp)
        case tokenStatus(String)
        case confirmSwitch(TokenRecord)
        case confirmDelete(TokenRecord)
        case actionFailed(title: String, message: String)

        var id: String {
            switch self {
            case .columnHelp(let help): return "help-\(help.title)"
            case .tokenStatus(let status): return "status-\(status)"
            case .confirmSwitch(let record): return "switch-\(record.id)"
            case .confirmDelete(let record): return "delete-\(record.id)"
            case .actionFailed(let title, let message): return "failed-\(title)-\(message)"
            }
        }
    }
    @State private var alert: PageAlert?
    /// 切换进行中（按钮置灰防重复点击）
    @State private var switching = false

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
                            alert = .tokenStatus(appState.tokenStatus)
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
                            // 记录按申请时间倒序（最新在前），正序编号才是「历史第几笔」
                            recordRow(
                                index: appState.tokenRecords.count - index,
                                record: record
                            )
                        }
                    }
                }
                .padding(20)
            }
        }
        .onAppear { appState.refreshTokenStatus() }
        .alert(item: $alert) { alert in
            switch alert {
            case .columnHelp(let help):
                return Alert(
                    title: Text(help.title),
                    message: Text(help.message),
                    dismissButton: .default(Text("好"))
                )
            case .tokenStatus(let status):
                return Alert(
                    title: Text("Token 审批状态"),
                    message: Text(status),
                    dismissButton: .default(Text("好"))
                )
            case .confirmSwitch(let record):
                return Alert(
                    title: Text("切换当前使用的 Token"),
                    message: Text(switchConfirmMessage(record)),
                    primaryButton: .default(Text("切换")) { performSwitch(record) },
                    secondaryButton: .cancel(Text("取消"))
                )
            case .confirmDelete(let record):
                return Alert(
                    title: Text("删除申请记录"),
                    message: Text(deleteConfirmMessage(record)),
                    primaryButton: .destructive(Text("删除")) { performDelete(record) },
                    secondaryButton: .cancel(Text("取消"))
                )
            case .actionFailed(let title, let message):
                return Alert(
                    title: Text(title),
                    message: Text(message),
                    dismissButton: .default(Text("好"))
                )
            }
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
                helpMessage: "该笔申请的序号，按申请时间从早到晚排列：最早申请的为 1 号。列表本身仍按时间倒序显示，最新记录在最上面。",
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
            headerItem(
                title: "操作",
                helpMessage: "切换：把该条记录的 Token 设为当前使用。\n删除：移除这条申请记录；若删掉的是当前生效的 Token，本地凭据会随之失效。",
                width: 96
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
            alert = .columnHelp(ColumnHelp(title: title, message: helpMessage))
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
            actionCell(for: record)
                .frame(width: 96, alignment: .leading)
        }
        .font(AppTheme.dimFont)
        .foregroundColor(AppTheme.ink(scheme))
    }

    /// 操作列：当前生效显示「使用中」，否则给「切换」；删除按钮每行都有
    @ViewBuilder
    private func actionCell(for record: TokenRecord) -> some View {
        HStack(spacing: 8) {
            if record.id == appState.activeTokenId {
                Text("使用中")
                    .font(AppTheme.dimFont.weight(.semibold))
                    .foregroundColor(AppTheme.success(scheme))
                    .help("该记录对应的 Token 正在被客户端使用")
            } else {
                switchButton(for: record)
            }
            deleteButton(for: record)
        }
    }

    private func switchButton(for record: TokenRecord) -> some View {
        Button {
            alert = .confirmSwitch(record)
        } label: {
            Text("切换")
                .font(AppTheme.dimFont.weight(.semibold))
                .foregroundColor(canSwitch(record) ? AppTheme.primary(scheme) : AppTheme.dim(scheme).opacity(0.5))
                .frame(height: 22)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!canSwitch(record) || switching)
        .help(switchHint(record))
    }

    /// 只有已通过、且本地缓存或服务器仍留有明文的记录才能切过去
    private func canSwitch(_ record: TokenRecord) -> Bool {
        record.status == "approved" && (record.claimable || appState.hasCachedToken(record.id))
    }

    private func switchHint(_ record: TokenRecord) -> String {
        if record.status != "approved" { return "只有审批通过的记录可以切换为当前使用" }
        if canSwitch(record) { return "把这条 Token 设为当前使用" }
        return "该记录的明文已不可获取（首次领取后服务器不再保留），无法切换"
    }

    private func deleteButton(for record: TokenRecord) -> some View {
        Button {
            alert = .confirmDelete(record)
        } label: {
            Image(systemName: "trash")
                .font(.system(size: 12))
                .foregroundColor(AppTheme.danger(scheme))
                .frame(width: 22, height: 22)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help("删除这条申请记录")
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

    /// 切换确认文案：切换会停掉自动交易（信号流要带新凭据重建），必须提前说明
    private func switchConfirmMessage(_ record: TokenRecord) -> String {
        let summary = record.description.isEmpty ? "（无说明）" : record.description
        var text = "将「\(summary)」这条记录的 Token 设为当前使用。"
        if appState.engineStatus.tradingActive {
            text += "\n\n当前自动交易正在运行，切换后会被停止，需要重新开启（信号流必须用新 Token 重新连接）。"
        }
        return text
    }

    private func performSwitch(_ record: TokenRecord) {
        switching = true
        Task {
            let error = await appState.switchToken(tokenId: record.id)
            switching = false
            if let error {
                alert = .actionFailed(title: "切换失败", message: error)
            }
        }
    }

    /// 删除确认文案：删掉已通过的记录会让本地 Token 失效，需在确认前讲清楚
    private func deleteConfirmMessage(_ record: TokenRecord) -> String {
        let summary = record.description.isEmpty ? "（无说明）" : record.description
        if record.status == "approved" {
            return "将删除申请记录「\(summary)」。\n\n这条记录对应的 Token 已通过审批，删除后服务器不再认可它；若本地正在使用该 Token，需要重新申请并等待审批。"
        }
        return "将删除申请记录「\(summary)」。删除后不可恢复。"
    }

    private func performDelete(_ record: TokenRecord) {
        Task {
            if let error = await appState.deleteTokenRecord(tokenId: record.id) {
                alert = .actionFailed(title: "删除失败", message: error)
            }
        }
    }
}
