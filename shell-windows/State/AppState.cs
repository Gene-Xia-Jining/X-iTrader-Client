using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using XTrader.Shell.Core;
// 属性 EngineStatus 与类型同名：用别名消除 E.I 形式的解析歧义，确保取的是类型
using EngineStatusModel = XTrader.Shell.State.EngineStatus;

namespace XTrader.Shell.State;

// MARK: - 模型（协议字段手工解析，见 src/backend/PROTOCOL.md）

/// <summary>一条会话日志（对应 Swift 的 LogLine）</summary>
public sealed class LogLine(DateTime time, string level, string message)
{
    public DateTime Time { get; } = time;
    public string Level { get; } = level;
    public string Message { get; } = message;

    /// <summary>日志配色随主题（对应 Qt viewmodels 的 log_colors）</summary>
    public string Color(bool dark) => Level switch
    {
        "ERROR" => dark ? "#FF453A" : "#C5241F",
        "WARNING" => dark ? "#FFD60A" : "#B8780A",
        "SUCCESS" => dark ? "#30D158" : "#1D9552",
        _ => dark ? "#FFFFFF" : "#1D1D1F",
    };
}

/// <summary>
/// 「测试连接」验证通过的一组快期凭据：手机号 + 密码摘要。
/// 只留摘要，避免为了一个 UI 状态多存一份明文。
/// </summary>
public sealed class VerifiedTqCredentials
{
    /// <summary>
    /// 落盘键：重启后模拟页步骤 3 胶囊仍要显示为已验证。
    /// 手机号与摘要存同一个键（<c>手机号|摘要</c>），写入一次完成，
    /// 不会出现「号码是新的、摘要是旧的」这种跨两次写的中间态。
    /// </summary>
    private const string PrefKey = "sim.verifiedTqCredentials";

    public VerifiedTqCredentials(string account, string password)
    {
        Account = account.Trim();
        PasswordDigest = Digest(password);
    }

    /// <summary>回读落盘记录时用（摘要已是摘要，不再二次计算）</summary>
    private VerifiedTqCredentials()
    {
    }

    public string Account { get; private set; } = "";

    /// <summary>空串表示这次验证沿用的是已存密码</summary>
    public string PasswordDigest { get; private set; } = "";

    public bool Matches(string account, string password) =>
        Account == account.Trim() && PasswordDigest == Digest(password);

    /// <summary>恢复上次验证通过的记录；只回读手机号与摘要，没有任何明文</summary>
    internal static VerifiedTqCredentials? Load()
    {
        var raw = ShellPrefs.Get(PrefKey);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        var parts = raw.Split('|', 2);
        return parts.Length != 2 || parts[0].Length == 0
            ? null
            : new VerifiedTqCredentials { Account = parts[0], PasswordDigest = parts[1] };
    }

    internal void Save() => ShellPrefs.Set(PrefKey, $"{Account}|{PasswordDigest}");

    internal static void Clear() => ShellPrefs.Set(PrefKey, "");

    /// <summary>只用于本地比较与记忆验证结果，不涉及任何传输</summary>
    private static string Digest(string password) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(password)));
}

public sealed class AccountInfo
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "live";
    public string Label { get; init; } = "";
    public string TqAccount { get; init; } = "";
    public string Broker { get; init; } = "";
    public string TradeAccount { get; init; } = "";
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public string Strategy { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool HasTqPassword { get; init; }
    public bool HasTradePassword { get; init; }

    /// <summary>已存密码的明文长度，仅用于按真实长度绘制掩码圆点（密码本身不回传）</summary>
    public int TqPasswordLength { get; init; }
    public int TradePasswordLength { get; init; }

    public bool IsSim => Kind == "sim";

    public static AccountInfo? From(JsonObject dict)
    {
        if (dict["id"]?.GetValue<string>() is not { } id)
        {
            return null;
        }
        return new AccountInfo
        {
            Id = id,
            Kind = dict["kind"]?.GetValue<string>() ?? "live",
            Label = dict["label"]?.GetValue<string>() ?? "",
            TqAccount = dict["tq_account"]?.GetValue<string>() ?? "",
            Broker = dict["broker"]?.GetValue<string>() ?? "",
            TradeAccount = dict["trade_account"]?.GetValue<string>() ?? "",
            Symbols = JsonHelpers.StringList(dict["symbols"]),
            Strategy = dict["strategy"]?.GetValue<string>() ?? "",
            Enabled = dict["enabled"]?.GetValue<bool>() ?? true,
            HasTqPassword = dict["has_tq_password"]?.GetValue<bool>() ?? false,
            HasTradePassword = dict["has_trade_password"]?.GetValue<bool>() ?? false,
            TqPasswordLength = dict["tq_password_length"]?.GetValue<int>() ?? 0,
            TradePasswordLength = dict["trade_password_length"]?.GetValue<int>() ?? 0,
        };
    }
}

/// <summary>服务器提供的交易策略（/api/strategies 目录项）</summary>
public sealed class StrategyInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Alias { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>token 申请记录（服务器 /api/tokens/requests 返回项）</summary>
public sealed class TokenRecord
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "pending";
    public DateTime? CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public int UseCount { get; init; }

    /// <summary>服务器是否还留有可领取的明文（首次领取后即为 false）</summary>
    public bool Claimable { get; init; }

    /// <summary>状态中文文案（对应服务器 status 字段）</summary>
    public string StatusText => Status switch
    {
        "pending" => "待审核",
        "approved" => "已通过",
        "rejected" => "已拒绝",
        _ => Status,
    };

    public static TokenRecord? From(JsonObject dict)
    {
        if (dict["token_id"]?.GetValue<string>() is not { } id)
        {
            return null;
        }
        static DateTime? UnixSeconds(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }
            var seconds = value.TryGetValue<double>(out var d) ? d
                : value.TryGetValue<long>(out var l) ? l
                : double.NaN;
            return double.IsNaN(seconds)
                ? null
                : DateTimeOffset.FromUnixTimeSeconds((long)seconds).LocalDateTime;
        }
        return new TokenRecord
        {
            Id = id,
            Description = dict["description"]?.GetValue<string>() ?? "",
            Status = dict["status"]?.GetValue<string>() ?? "pending",
            CreatedAt = UnixSeconds(dict["created_at"]),
            ExpiresAt = UnixSeconds(dict["expires_at"]),
            UseCount = dict["use_count"]?.GetValue<int>() ?? 0,
            Claimable = dict["claimable"]?.GetValue<bool>() ?? false,
        };
    }
}

public sealed class AccountRuntime
{
    public string Id { get; init; } = "";
    public bool Running { get; set; }
    public bool ServerConnected { get; set; }
}

public sealed class EngineStatus
{
    public bool AutoTrade { get; set; }
    public bool TradingActive { get; set; }

    /// <summary>iTrader 服务器连通性（后端心跳常驻探测，与引擎是否运行无关）</summary>
    public bool ServerOnline { get; set; }

    /// <summary>最近一次探测失败的原因（在线时为空串），来自后端心跳</summary>
    public string ServerError { get; set; } = "";

    public List<AccountRuntime> Accounts { get; set; } = [];

    public static EngineStatus From(JsonObject dict)
    {
        var status = new EngineStatus
        {
            AutoTrade = dict["auto_trade"]?.GetValue<bool>() ?? false,
            TradingActive = dict["trading_active"]?.GetValue<bool>() ?? false,
            ServerOnline = dict["server_online"]?.GetValue<bool>() ?? false,
            ServerError = dict["server_error"]?.GetValue<string>() ?? "",
        };
        if (dict["accounts"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject obj || obj["account_id"]?.GetValue<string>() is not { } id)
                {
                    continue;
                }
                status.Accounts.Add(new AccountRuntime
                {
                    Id = id,
                    Running = obj["running"]?.GetValue<bool>() ?? false,
                    ServerConnected = obj["server_connected"]?.GetValue<bool>() ?? false,
                });
            }
        }
        return status;
    }
}

public sealed class ConfigInfo
{
    public string ServerUrl { get; set; } = "";
    public string ProxyUrl { get; set; } = "";

    public static ConfigInfo From(JsonObject dict) => new()
    {
        ServerUrl = dict["server_url"]?.GetValue<string>() ?? "",
        ProxyUrl = dict["proxy_url"]?.GetValue<string>() ?? "",
    };
}

public enum ConnState { Starting, Connected, Disconnected, Stopped }

/// <summary>更新流程状态（对应 Swift 的 UpdatePhase）</summary>
public abstract record UpdatePhase
{
    public sealed record Idle : UpdatePhase;
    public sealed record Checking : UpdatePhase;
    public sealed record Available(string Version, bool HasPatch) : UpdatePhase;
    public sealed record Latest(string Message) : UpdatePhase;
    public sealed record Downloading : UpdatePhase;
    public sealed record Ready(string Kind) : UpdatePhase;
    public sealed record Failed(string Message) : UpdatePhase;

    /// <summary>检查/下载/安装进行中：此时不应重复触发检查</summary>
    public bool IsBusy => this is Checking or Downloading or Ready;
}

// MARK: - AppState

/// <summary>
/// 应用状态单一来源（对应 Swift 的 AppState）。负责后端进程生命周期、
/// RPC 连接与重连、事件分发、配置与更新流程状态。
///
/// 所有属性变更都通过 <see cref="_dispatcher"/> 切回 UI 线程后触发
/// PropertyChanged，视图可直接绑定。
/// </summary>
public sealed class AppState : INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher;

    private BackendProcess? _backend;
    private RpcClient? _client;
    private bool _started;
    private int _shutdown;
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>本次启动是否已做过静默更新检查（顶栏角标只在启动时探一次）</summary>
    private bool _didAutoCheckUpdate;
    /// <summary>弹窗里已确认更新：下载完成后自动安装并重启</summary>
    private bool _autoApplyUpdate;
    /// <summary>已下载更新包的类型（"patch"/"full"）；安装后据此决定重连还是退出</summary>
    private string _pendingUpdateMode = "";
    /// <summary>
    /// 差量更新后的重连等待窗口：期间忽略断链/进程退出触发的重连，
    /// 否则壳会抢在 updater 拉起新后端前自己 spawn 一个，两个后端互相拒绝
    /// </summary>
    private bool _waitingForUpdateBackend;
    /// <summary>更新前那一个后端进程的 pid：用来识别「新后端已接管」</summary>
    private int _oldBackendPid;

    internal AppState(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 请求壳进程退出（完整更新装好、新壳已启动时用）。由主窗口订阅后
    /// 收尾托盘并 Application.Exit——AppState 不直接持有 UI 生命周期。
    /// </summary>
    internal event Action? ExitRequested;

    // -------- 状态 --------

    private ConnState _connState = ConnState.Starting;
    public ConnState ConnState
    {
        get => _connState;
        private set => Set(ref _connState, value);
    }

    private string _backendVersion = "";
    public string BackendVersion
    {
        get => _backendVersion;
        private set => Set(ref _backendVersion, value);
    }

    public ObservableCollection<LogLine> Logs { get; } = [];

    private EngineStatus _engineStatus = new();
    public EngineStatus EngineStatus
    {
        get => _engineStatus;
        private set => Set(ref _engineStatus, value);
    }

    private IReadOnlyList<AccountInfo> _accounts = [];
    public IReadOnlyList<AccountInfo> Accounts
    {
        get => _accounts;
        private set => Set(ref _accounts, value);
    }

    private ConfigInfo _config = new();
    public ConfigInfo Config
    {
        get => _config;
        private set => Set(ref _config, value);
    }

    private IReadOnlyList<string> _serverSymbols = [];
    public IReadOnlyList<string> ServerSymbols
    {
        get => _serverSymbols;
        private set => Set(ref _serverSymbols, value);
    }

    /// <summary>品种中文名映射（symbol -&gt; name），无名的品种不在表中</summary>
    private IReadOnlyDictionary<string, string> _serverSymbolNames = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ServerSymbolNames
    {
        get => _serverSymbolNames;
        private set => Set(ref _serverSymbolNames, value);
    }

    /// <summary>交易记录实时更新事件通知：(kind, records)</summary>
    public event Action<string, IReadOnlyList<AccountRecords>>? RecordsUpdated;
    private readonly Dictionary<string, List<AccountRecords>> _cachedRecords = [];

    public IReadOnlyList<AccountRecords> GetCachedRecords(string kind)
    {
        lock (_cachedRecords)
        {
            return _cachedRecords.TryGetValue(kind, out var list) ? list.ToList() : [];
        }
    }

    public void SetCachedRecords(string kind, IReadOnlyList<AccountRecords> records)
    {
        lock (_cachedRecords)
        {
            _cachedRecords[kind] = records.ToList();
        }
    }

    private string _symbolsMessage = "";
    public string SymbolsMessage
    {
        get => _symbolsMessage;
        private set => Set(ref _symbolsMessage, value);
    }

    private bool _symbolsFetching;
    public bool SymbolsFetching
    {
        get => _symbolsFetching;
        private set => Set(ref _symbolsFetching, value);
    }

    private IReadOnlyList<StrategyInfo> _serverStrategies = [];
    public IReadOnlyList<StrategyInfo> ServerStrategies
    {
        get => _serverStrategies;
        private set => Set(ref _serverStrategies, value);
    }

    private string _strategiesMessage = "";
    public string StrategiesMessage
    {
        get => _strategiesMessage;
        private set => Set(ref _strategiesMessage, value);
    }

    private bool _strategiesFetching;
    public bool StrategiesFetching
    {
        get => _strategiesFetching;
        private set => Set(ref _strategiesFetching, value);
    }

    private string _tokenStatus = "";
    public string TokenStatus
    {
        get => _tokenStatus;
        private set => Set(ref _tokenStatus, value);
    }

    private IReadOnlyList<TokenRecord> _tokenRecords = [];
    public IReadOnlyList<TokenRecord> TokenRecords
    {
        get => _tokenRecords;
        private set => Set(ref _tokenRecords, value);
    }

    private string? _activeTokenId;
    /// <summary>当前生效的 token 来自哪条申请记录（null 表示本地无可用 token）</summary>
    public string? ActiveTokenId
    {
        get => _activeTokenId;
        private set => Set(ref _activeTokenId, value);
    }

    private IReadOnlySet<string> _cachedTokenIds = new HashSet<string>();
    /// <summary>本地缓存过明文、可切换回去的记录 id</summary>
    public IReadOnlySet<string> CachedTokenIds
    {
        get => _cachedTokenIds;
        private set => Set(ref _cachedTokenIds, value);
    }

    /// <summary>该记录是否在本地留有明文缓存（后端列表的 claimable 只管服务器侧）</summary>
    public bool HasCachedToken(string tokenId) => CachedTokenIds.Contains(tokenId);

    private IReadOnlyList<string> _startBlockedIssues = [];
    /// <summary>自动交易启动被预检拦截的问题列表；非空时主窗口弹出清单，用户确认后清空</summary>
    public IReadOnlyList<string> StartBlockedIssues
    {
        get => _startBlockedIssues;
        private set => Set(ref _startBlockedIssues, value);
    }

    private bool _startChecking;
    /// <summary>启动检查进行中（按钮置灰防重复点击）</summary>
    public bool StartChecking
    {
        get => _startChecking;
        private set => Set(ref _startChecking, value);
    }

    private string _startProgressText = "";
    /// <summary>
    /// 启停的当前阶段文案（如「测试天勤连接…」），空串表示没有在跑。
    /// engine.start 是一次 RPC 调用，过程状态只能靠后端 engine_start_progress 事件推来。
    /// </summary>
    public string StartProgressText
    {
        get => _startProgressText;
        private set => Set(ref _startProgressText, value);
    }

    private int _startProgressPct = -1;
    /// <summary>
    /// 整次启动的百分比（后端按各阶段权重折算）；-1 表示该阶段没有可画的进度
    /// （如停止操作），此时只显示文案不画进度条。
    /// </summary>
    public int StartProgressPct
    {
        get => _startProgressPct;
        private set => Set(ref _startProgressPct, value);
    }

    /// <summary>清空启停进度（操作结束或开始时调用，避免残留上一轮的数字）</summary>
    private void ResetStartProgress()
    {
        StartProgressText = "";
        StartProgressPct = -1;
    }

    internal void ClearStartBlockedIssues() => StartBlockedIssues = [];

    private VerifiedTqCredentials? _verifiedTqCredentials = XTrader.Shell.State.VerifiedTqCredentials.Load();
    /// <summary>
    /// 「测试连接」验证通过的那组快期凭据，跨页面、跨重启保留：
    /// 模拟页第 3 步胶囊据此显示绿色。只存摘要，不参与任何登录。
    /// </summary>
    public VerifiedTqCredentials? VerifiedTqCredentials
    {
        get => _verifiedTqCredentials;
        private set
        {
            // 属性与类型同名，类内只能用完全限定名引用类型
            if (value is null)
            {
                XTrader.Shell.State.VerifiedTqCredentials.Clear();
            }
            else
            {
                value.Save();
            }
            Set(ref _verifiedTqCredentials, value);
        }
    }

    private UpdatePhase _updatePhase = new UpdatePhase.Idle();
    public UpdatePhase UpdatePhase
    {
        get => _updatePhase;
        private set
        {
            if (Set(ref _updatePhase, value))
            {
                // 角标由 phase 派生，需随 phase 一起通知
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateBadgeVisible)));
            }
        }
    }

    /// <summary>
    /// 顶栏版本按钮是否显示新版本角标（仅「发现新版本」态；
    /// 下载/安装阶段胶囊直接显示进度文案）
    /// </summary>
    public bool UpdateBadgeVisible => _updatePhase is UpdatePhase.Available;

    private int _updateProgress = -1;
    /// <summary>下载进度百分比；-1 表示总大小未知</summary>
    public int UpdateProgress
    {
        get => _updateProgress;
        private set => Set(ref _updateProgress, value);
    }

    private bool _updateInstalling;
    /// <summary>
    /// 正在安装（update.apply 已发出，等后端替换文件并退出）。
    /// 弹窗据此区分「已下载待安装」与「安装中」。
    /// </summary>
    public bool UpdateInstalling
    {
        get => _updateInstalling;
        private set => Set(ref _updateInstalling, value);
    }

    private string _lastActionError = "";
    public string LastActionError
    {
        get => _lastActionError;
        private set => Set(ref _lastActionError, value);
    }

    // -------- 启动 / 重连 --------

    internal void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _ = Task.Run(() => ConnectLoopAsync(_lifetime.Token));
    }

    /// <summary>
    /// 壳退出时收尾：先尽力让后端优雅退出（app.quit，后端据此释放 TqApi 连接），
    /// 再兜底 Kill，避免孤儿后端常驻。
    /// </summary>
    internal void Shutdown()
    {
        // 退出路径可能被多次触发（Application.Exit + Window.Closed），只执行一次
        if (Interlocked.Exchange(ref _shutdown, 1) == 1)
        {
            return;
        }
        _lifetime.Cancel();

        // 优雅退出：给 app.quit 一点时间；失败或超时再强杀
        try
        {
            if (_client is not null && ConnState != ConnState.Stopped)
            {
                _client.CallAsync("app.quit").Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // 连接已断/后端已退出：交给下面的 Kill 兜底
        }

        try
        {
            _client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 关闭路径异常无需上报
        }
        _client = null;

        _backend?.Terminate();
        _backend?.Dispose();
        _backend = null;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _backend ??= CreateBackend();
                var info = await _backend.ConnectOrStartAsync(ct);

                var rpc = new RpcClient(info);
                rpc.EventReceived += (name, data) => Post(() => HandleEvent(name, data));
                rpc.Disconnected += () => Post(HandleDisconnect);

                await rpc.ConnectAsync(ct);
                _client = rpc;

                Post(() =>
                {
                    ConnState = ConnState.Connected;
                    BackendVersion = info.Version;
                    AppendLog("SUCCESS", $"已连接后端 {info.Version} (pid {info.Pid})");
                    // 差量更新安装完成后的首次连接：暂存包已生效，状态回到 idle
                    if (UpdatePhase is UpdatePhase.Ready)
                    {
                        UpdateProgress = -1;
                        _pendingUpdateMode = "";
                        UpdateInstalling = false;
                        UpdatePhase = new UpdatePhase.Idle();
                    }
                });
                await RefreshAllAsync(ct);
                // 连接成功即静默检查一次更新（失败不打扰）；排在状态回填之后，
                // 保证版本号已就绪，胶囊有内容可显示
                Post(CheckUpdateOnConnect);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    ConnState = ConnState.Disconnected;
                    AppendLog("ERROR", $"连接后端失败: {ex.Message}，3 秒后重试");
                });
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private BackendProcess CreateBackend()
    {
        var proc = new BackendProcess();
        proc.Exited += () => Post(HandleBackendExit);
        return proc;
    }

    private void HandleDisconnect()
    {
        _client = null;
        if (ConnState == ConnState.Stopped)
        {
            return;
        }
        ConnState = ConnState.Disconnected;
        if (_waitingForUpdateBackend)
        {
            return;
        }
        AppendLog("WARNING", "与后端断开，正在重连…");
        _ = Task.Run(() => ConnectLoopAsync(_lifetime.Token));
    }

    private void HandleBackendExit()
    {
        _client = null;
        if (ConnState == ConnState.Stopped)
        {
            return;
        }
        ConnState = ConnState.Disconnected;
        if (_waitingForUpdateBackend)
        {
            return;
        }
        AppendLog("ERROR", "后端进程已退出，正在重启…");
        // 后端在写出 backend.json 前就崩溃时，它的 stderr 是唯一线索；
        // 没有这行，界面上只会无限重复「后端进程已退出」，无从定位
        var tail = _backend?.OutputTail;
        if (!string.IsNullOrWhiteSpace(tail))
        {
            AppendLog("ERROR", $"后端输出：{tail}");
        }
        AppendLog("ERROR", $"完整日志：{AppPaths.BackendLogFile}");
        _ = Task.Run(() => ConnectLoopAsync(_lifetime.Token));
    }

    /// <summary>
    /// 差量更新安装后：后端由 updater 重新拉起，等它写出新的 backend.json 再重连。
    /// 不能固定等几秒：旧后端退出时会删掉 backend.json，壳若抢先调 connectOrStart
    /// 会自己 spawn 一个后端，与 updater 拉起的那个互相拒绝。
    /// </summary>
    private async Task ReconnectAfterUpdateAsync(CancellationToken ct)
    {
        _waitingForUpdateBackend = true;
        try
        {
            var infoFile = _backend?.InfoFile;
            // 最多等 30 秒；超时仍重连，由 ConnectOrStartAsync 兜底
            for (var i = 0; infoFile is not null && i < 150; i++)
            {
                var info = BackendProcess.ReadInfoFrom(infoFile);
                if (info is not null && info.Pid != _oldBackendPid && BackendProcess.IsPidAlive(info.Pid))
                {
                    break;
                }
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _waitingForUpdateBackend = false;
        }
        _ = Task.Run(() => ConnectLoopAsync(_lifetime.Token));
    }

    // -------- RPC 封装 --------

    internal async Task<JsonObject> CallAsync(string method, JsonObject? parameters = null)
    {
        var rpc = _client ?? throw new RpcException("与后端的连接已断开");
        try
        {
            return await rpc.CallAsync(method, parameters);
        }
        catch (RpcException ex)
        {
            // 换成为界面准备的文案，但保留服务端类型与结构化问题清单
            // （engine.start 的预检问题就靠 Issues 传出去）
            throw new RpcException(ex.DisplayMessage, ex.ServerType, ex.Issues);
        }
    }

    private async Task RunActionAsync(string method, JsonObject? parameters = null)
    {
        try
        {
            Post(() => LastActionError = "");
            _ = await CallAsync(method, parameters);
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                LastActionError = ex.Message;
                AppendLog("ERROR", $"{method} 失败: {ex.Message}");
            });
        }
    }

    private async Task RefreshAllAsync(CancellationToken ct)
    {
        try
        {
            var status = await CallAsync("app.status");
            Post(() =>
            {
                if (status["version"]?.GetValue<string>() is { Length: > 0 } version)
                {
                    BackendVersion = version;
                }
                if (status["config"] is JsonObject cfg)
                {
                    Config = ConfigInfo.From(cfg);
                }
                Accounts = JsonHelpers.ObjectList(status["accounts"]).Select(AccountInfo.From).OfType<AccountInfo>().ToList();
                if (status["engines"] is JsonObject engines)
                {
                    EngineStatus = EngineStatusModel.From(engines);
                }
            });
        }
        catch (Exception ex)
        {
            Post(() => AppendLog("ERROR", $"刷新状态失败: {ex.Message}"));
        }
    }

    // -------- 事件处理 --------

    private void HandleEvent(string name, JsonObject data)
    {
        switch (name)
        {
            case "log":
                AppendLog(
                    data["level"]?.GetValue<string>() ?? "INFO",
                    data["message"]?.GetValue<string>() ?? "");
                break;
            case "status":
                var engine = EngineStatus;
                engine.TradingActive = data["trading_active"]?.GetValue<bool>() ?? engine.TradingActive;
                // 心跳广播只带全局 server_online（无 account_id）；引擎广播两者都带
                if (data["server_online"]?.GetValue<bool>() is { } online)
                {
                    engine.ServerOnline = online;
                }
                if (data["server_error"]?.GetValue<string>() is { } serverError)
                {
                    engine.ServerError = serverError;
                }
                if (data["account_id"]?.GetValue<string>() is { } accountId)
                {
                    var runtime = engine.Accounts.FirstOrDefault(a => a.Id == accountId);
                    if (runtime is not null)
                    {
                        runtime.ServerConnected = data["server_connected"]?.GetValue<bool>() ?? false;
                    }
                }
                break;
            case "trading_started":
            case "trading_stopped":
                _ = RefreshEngineOnlyAsync();
                break;
            case "config_changed":
                _ = RefreshAllAsync(_lifetime.Token);
                break;
            case "token_status":
                ApplyTokenStatus(data);
                break;
            case "signal_received":
                if (data["signal"] is JsonObject signal)
                {
                    // target_position 是 JSON 数字，GetValue<string>() 会抛异常
                    AppendLog("INFO", $"收到信号: {signal["symbol"]?.GetValue<string>() ?? "?"} 目标仓位 {JsonHelpers.Int(signal["target_position"])?.ToString() ?? "?"}");
                }
                break;
            case "trade_executed":
                AppendLog("SUCCESS", $"交易已执行: {data["symbol"]?.GetValue<string>() ?? "?"} 仓位 {data["target_position"]?.GetValue<string>() ?? "?"}");
                break;
            case "trade_failed":
                AppendLog("ERROR", $"交易失败: {data["symbol"]?.GetValue<string>() ?? "?"} {data["error"]?.GetValue<string>() ?? ""}");
                break;
            case "trading_records_updated":
                var recordKind = data["kind"]?.GetValue<string>() ?? "";
                if (data["record"] is JsonObject recordObj && AccountRecords.From(recordObj) is { } newRec)
                {
                    List<AccountRecords> list;
                    lock (_cachedRecords)
                    {
                        if (!_cachedRecords.TryGetValue(recordKind, out list!))
                        {
                            list = [];
                            _cachedRecords[recordKind] = list;
                        }
                        var idx = list.FindIndex(r => r.Id == newRec.Id);
                        if (idx >= 0)
                        {
                            list[idx] = newRec;
                        }
                        else
                        {
                            list.Add(newRec);
                        }
                    }
                    RecordsUpdated?.Invoke(recordKind, list);
                }
                break;
            case "update_progress":
                UpdateProgress = JsonHelpers.Int(data["pct"]) ?? -1;
                break;
            case "engine_start_progress":
                // 只在开关操作进行中采纳：过期事件（如别的客户端发起的启停）不该
                // 让本机顶栏显示一个不存在的进度
                if (StartChecking)
                {
                    StartProgressText = data["text"]?.GetValue<string>() ?? "";
                    StartProgressPct = JsonHelpers.Int(data["pct"]) ?? -1;
                }
                break;
            case "update_ready":
                // mode 是后加的字段：旧版后端只发中文 kind，按文案回退判断，
                // 否则差量更新会被误当成完整更新而直接退出壳
                var kind = data["kind"]?.GetValue<string>() ?? "";
                _pendingUpdateMode = data["mode"]?.GetValue<string>()
                    ?? (kind.Contains("差量") ? "patch" : "full");
                UpdateProgress = -1;
                UpdatePhase = new UpdatePhase.Ready(kind);
                // 用户确认过「更新」：下载完直接装，不再要求点第二次
                if (_autoApplyUpdate)
                {
                    _autoApplyUpdate = false;
                    ApplyUpdate();
                }
                break;
            case "update_failed":
                _autoApplyUpdate = false;
                _pendingUpdateMode = "";
                UpdateProgress = -1;
                UpdatePhase = new UpdatePhase.Failed(data["message"]?.GetValue<string>() ?? "下载失败");
                break;
            case "update_cancelled":
                _autoApplyUpdate = false;
                _pendingUpdateMode = "";
                UpdateProgress = -1;
                UpdatePhase = new UpdatePhase.Idle();
                break;
            case "backend_stopping":
                AppendLog("WARNING", $"后端正在退出（{data["reason"]?.GetValue<string>() ?? ""}）");
                if (data["reason"]?.GetValue<string>() == "update" && _pendingUpdateMode == "patch")
                {
                    // 差量更新只替换 src，后端由 updater 重新拉起：壳留在前台等重连
                    _oldBackendPid = _backend?.Info?.Pid ?? 0;
                    _client = null;
                    ConnState = ConnState.Disconnected;
                    AppendLog("INFO", "差量更新已安装，等待后端重启后重连");
                    _ = Task.Run(() => ReconnectAfterUpdateAsync(_lifetime.Token));
                }
                else
                {
                    // 完整更新：updater 启动了新的壳进程，旧壳必须退出，
                    // 否则两个壳会争同一个后端（单实例互斥也拦不住已运行的进程）
                    _pendingUpdateMode = "";
                    ConnState = ConnState.Stopped;
                    _ = Task.Delay(1500).ContinueWith(_ => Post(() => ExitRequested?.Invoke()));
                }
                break;
        }
    }

    private async Task RefreshEngineOnlyAsync()
    {
        try
        {
            var engines = await CallAsync("engine.status");
            Post(() => EngineStatus = EngineStatusModel.From(engines));
        }
        catch
        {
            // 刷新失败静默：下一次事件或重连会再来
        }
    }

    /// <summary>
    /// 追加一条会话日志。自身负责切回 UI 线程：调用点既有 UI 线程（事件处理）
    /// 也有后台线程（RPC 失败路径），统一在这里 marshalling，调用方无需关心。
    /// </summary>
    private void AppendLog(string level, string message) => Post(() =>
    {
        Logs.Add(new LogLine(DateTime.Now, level, message));
        while (Logs.Count > 500)
        {
            Logs.RemoveAt(0);
        }
    });

    // -------- 业务动作 --------

    /// <summary>
    /// 自动交易开关。开启时后端先做启动前预检，不通过则返回问题列表：
    /// 这里发布到 <see cref="StartBlockedIssues"/> 由主窗口弹窗拦住，
    /// 而不是只写一条日志让用户猜哪里没配好。
    /// 支持按 kind 分别开关（"sim" 或 "live"）。
    /// </summary>
    internal void ToggleTrading(bool on, string? kind = null)
    {
        if (StartChecking)
        {
            return;
        }
        StartChecking = true;
        StartBlockedIssues = [];
        ResetStartProgress();
        _ = Task.Run(async () =>
        {
            try
            {
                Post(() => LastActionError = "");
                var p = new JsonObject();
                if (!string.IsNullOrEmpty(kind))
                {
                    p["kind"] = kind;
                }
                _ = await CallAsync(on ? "engine.start" : "engine.stop", p);
            }
            catch (RpcException ex)
            {
                if (ex.Issues.Count > 0)
                {
                    Post(() => StartBlockedIssues = ex.Issues);
                    var kindLabel = kind == "sim" ? "模拟" : (kind == "live" ? "实盘" : "");
                    // AppendLog 自带 UI 线程调度，不必再套一层 Post
                    AppendLog("WARNING", $"{kindLabel}自动交易未启动：共 {ex.Issues.Count} 项检查未通过");
                }
                else
                {
                    Post(() => LastActionError = ex.DisplayMessage);
                    AppendLog("ERROR", $"engine.start 失败: {ex.DisplayMessage}");
                }
            }
            catch (Exception ex)
            {
                Post(() => LastActionError = ex.Message);
                AppendLog("ERROR", $"engine.start 失败: {ex.Message}");
            }
            finally
            {
                await RefreshEngineOnlyAsync();
                Post(() =>
                {
                    StartChecking = false;
                    ResetStartProgress();
                });
            }
        });
    }

    /// <summary>判定某种类型（sim 或 live）的账户当前是否有引擎在运行</summary>
    public bool IsKindRunning(string kind) =>
        EngineStatus.Accounts.Any(acc =>
            acc.Running && Accounts.FirstOrDefault(a => a.Id == acc.Id)?.Kind == kind);

    internal void SetAutoTrade(bool value) => _ = Task.Run(() =>
        RunActionAsync("engine.set_auto_trade", new JsonObject { ["value"] = value }));

    internal void SaveConfig(string? serverUrl, string? proxyUrl)
    {
        var parameters = new JsonObject();
        if (serverUrl is not null) parameters["server_url"] = serverUrl;
        if (proxyUrl is not null) parameters["proxy_url"] = proxyUrl;
        _ = Task.Run(() => RunActionAsync("config.save", parameters));
    }

    internal void FetchSymbols()
    {
        if (SymbolsFetching)
        {
            return;
        }
        SymbolsFetching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                Post(() => LastActionError = "");
                var resp = await CallAsync("symbols.fetch");
                var symbols = new List<string>();
                var names = new Dictionary<string, string>();
                if (resp["symbols"] is JsonArray list)
                {
                    foreach (var item in list)
                    {
                        // 新后端：{symbol, name}；旧后端：纯代码字符串
                        if (item is JsonObject obj)
                        {
                            var symbol = obj["symbol"]?.GetValue<string>() ?? "";
                            if (symbol.Length == 0) continue;
                            symbols.Add(symbol);
                            if (obj["name"]?.GetValue<string>() is { Length: > 0 } name)
                            {
                                names[symbol] = name;
                            }
                        }
                        else if (item?.GetValue<string>() is { Length: > 0 } legacy)
                        {
                            symbols.Add(legacy);
                        }
                    }
                }
                Post(() =>
                {
                    ServerSymbols = symbols;
                    ServerSymbolNames = names;
                    SymbolsMessage = resp["message"]?.GetValue<string>() ?? "";
                });
            }
            catch (Exception ex)
            {
                Post(() => SymbolsMessage = $"获取失败: {ex.Message}");
            }
            finally
            {
                Post(() => SymbolsFetching = false);
            }
        });
    }

    internal void FetchStrategies()
    {
        if (StrategiesFetching)
        {
            return;
        }
        StrategiesFetching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                Post(() => LastActionError = "");
                var resp = await CallAsync("strategies.fetch");
                var list = JsonHelpers.ObjectList(resp["strategies"])
                    .Select(item =>
                    {
                        var id = item["id"]?.GetValue<string>() ?? "";
                        var name = item["name"]?.GetValue<string>() ?? "";
                        var alias = item["alias"]?.GetValue<string>() ?? (name.Length > 0 ? name : id);
                        return new StrategyInfo
                        {
                            Id = id,
                            Name = name.Length > 0 ? name : alias,
                            Alias = alias,
                            Description = item["description"]?.GetValue<string>() ?? "",
                        };
                    })
                    .Where(s => s.Id.Length > 0)
                    .ToList();
                Post(() =>
                {
                    ServerStrategies = list;
                    StrategiesMessage = resp["message"]?.GetValue<string>() ?? "";
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    ServerStrategies = [];
                    StrategiesMessage = $"获取失败: {ex.Message}";
                });
            }
            finally
            {
                Post(() => StrategiesFetching = false);
            }
        });
    }

    /// <summary>品种显示文本：有中文名时「代码｜中文」，否则纯代码</summary>
    internal string DisplaySymbol(string symbol) =>
        ServerSymbolNames.TryGetValue(symbol, out var name) && name.Length > 0
            ? $"{symbol}｜{name}"
            : symbol;

    /// <summary>测试快期账户：密码留空时后端沿用该账户已存密码（accountId 用于定位）</summary>
    internal async Task<string> TestTqAuthAsync(string account, string password, string? accountId = null)
    {
        try
        {
            var parameters = new JsonObject
            {
                ["account"] = account,
                ["password"] = password,
            };
            if (accountId is not null)
            {
                parameters["account_id"] = accountId;
            }
            var resp = await CallAsync("accounts.test_tq_auth", parameters);
            // 验证通过即记下这组凭据：模拟页步骤 3 胶囊据此显示绿色，切页面回来仍有效
            var verified = new VerifiedTqCredentials(account, password);
            Post(() => VerifiedTqCredentials = verified);
            return resp["message"]?.GetValue<string>() ?? "连接成功";
        }
        catch (Exception ex)
        {
            Post(() => VerifiedTqCredentials = null);
            return $"连接失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 新密码落库、输入框随之清空后调用：落库的正是刚验证过的那份密码时，
    /// 把验证记录改写成「沿用已存密码」的形态，绿色继续有效；
    /// 否则凭据已经变了，清掉验证记录。
    /// </summary>
    internal void SettleVerifiedTqCredentials(string afterSavingPassword, string account)
    {
        var verified = VerifiedTqCredentials;
        if (verified is null)
        {
            return;
        }
        var next = verified.Matches(account, afterSavingPassword)
            ? new VerifiedTqCredentials(account, "")
            : null;
        Post(() => VerifiedTqCredentials = next);
    }

    /// <summary>读取已存密码明文（用户点击眼睛图标时受控调用）；失败返回空串</summary>
    internal async Task<string> GetAccountPasswordAsync(
        string? accountId = null,
        string? kind = null,
        string field = "tq_password")
    {
        try
        {
            var parameters = new JsonObject { ["field"] = field };
            if (accountId is not null) parameters["account_id"] = accountId;
            if (kind is not null) parameters["kind"] = kind;
            var resp = await CallAsync("accounts.get_password", parameters);
            return resp["password"]?.GetValue<string>() ?? "";
        }
        catch
        {
            // 取不到明文时由调用方保留掩码显示，不弹错误打断流程
            return "";
        }
    }

    /// <summary>保存账户；成功返回 null，失败返回错误文案供页面弹窗展示</summary>
    internal async Task<string?> SaveAccountAsync(JsonObject parameters)
    {
        try
        {
            _ = await CallAsync("accounts.save", parameters);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>删除账户；成功返回 null，失败返回错误文案</summary>
    internal async Task<string?> DeleteAccountAsync(string accountId)
    {
        try
        {
            _ = await CallAsync("accounts.delete", new JsonObject { ["account_id"] = accountId });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal async Task<string> TestServerAsync(string? url)
    {
        try
        {
            var parameters = new JsonObject();
            if (url is not null) parameters["server_url"] = url;
            var resp = await CallAsync("server.test", parameters);
            return resp["message"]?.GetValue<string>() ?? "连接成功";
        }
        catch (Exception ex)
        {
            return $"连接失败: {ex.Message}";
        }
    }

    internal void RefreshTokenStatus() => _ = Task.Run(async () =>
    {
        try
        {
            var resp = await CallAsync("tokens.status");
            Post(() => ApplyTokenStatus(resp));
        }
        catch
        {
            // 状态刷新失败静默，用户可手动重试
        }
    });

    private void ApplyTokenStatus(JsonObject data)
    {
        TokenStatus = data["status"]?.GetValue<string>() ?? "";
        TokenRecords = JsonHelpers.ObjectList(data["records"]).Select(TokenRecord.From).OfType<TokenRecord>().ToList();
        // 空字符串按「无生效记录」处理：后端的 null 与 "" 语义一致
        var active = data["active_token_id"]?.GetValue<string>();
        ActiveTokenId = string.IsNullOrEmpty(active) ? null : active;
        CachedTokenIds = JsonHelpers.StringList(data["cached_token_ids"]).ToHashSet(StringComparer.Ordinal);
    }

    internal void RequestToken(string description) => _ = Task.Run(async () =>
    {
        try
        {
            var resp = await CallAsync("tokens.request", new JsonObject { ["description"] = description });
            Post(() => TokenStatus = resp["status"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex)
        {
            Post(() => AppendLog("ERROR", $"tokens.request 失败: {ex.Message}"));
        }
    });

    /// <summary>提交 Token 申请并返回回执文案（模拟页第 5 步就地显示结果，不再弹窗）</summary>
    internal async Task<string> RequestTokenResultAsync(string description)
    {
        try
        {
            var resp = await CallAsync("tokens.request", new JsonObject { ["description"] = description });
            var status = resp["status"]?.GetValue<string>() ?? "已提交申请";
            Post(() => TokenStatus = status);
            return status;
        }
        catch (Exception ex)
        {
            return $"申请失败: {ex.Message}";
        }
    }

    /// <summary>删除一条申请记录；成功返回 null，失败返回错误文案供页面弹窗展示</summary>
    internal async Task<string?> DeleteTokenRecordAsync(string tokenId)
    {
        try
        {
            var resp = await CallAsync("tokens.delete", new JsonObject { ["token_id"] = tokenId });
            Post(() => ApplyTokenStatus(resp));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 切换当前使用的 Token；成功返回 null，失败返回错误文案。
    /// 后端切换后会停掉引擎（信号流请求头固定了旧 token），因此顺带刷新引擎状态。
    /// </summary>
    internal async Task<string?> SwitchTokenAsync(string tokenId)
    {
        try
        {
            var resp = await CallAsync("tokens.switch", new JsonObject { ["token_id"] = tokenId });
            Post(() => ApplyTokenStatus(resp));
            await RefreshEngineOnlyAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 连接成功后的静默探活：每次启动只做一次，总是执行（没有开关）。
    /// 失败不打扰用户——顶栏点版本胶囊可手动重试。
    /// </summary>
    private void CheckUpdateOnConnect()
    {
        if (_didAutoCheckUpdate)
        {
            return;
        }
        _didAutoCheckUpdate = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var resp = await CallAsync("update.check", new JsonObject { ["silent"] = true });
                if (resp["status"]?.GetValue<string>() != "available")
                {
                    return;
                }
                Post(() => UpdatePhase = new UpdatePhase.Available(
                    resp["version"]?.GetValue<string>() ?? "",
                    resp["has_patch"]?.GetValue<bool>() ?? false));
            }
            catch
            {
                // 静默检查失败保持 idle：用户点版本胶囊可手动重试
            }
        });
    }

    /// <summary>
    /// 手动检查更新。只查不下载：发现新版本由壳弹确认框，用户确认后才走
    /// <see cref="ConfirmUpdate"/>。
    /// </summary>
    internal void CheckUpdate() => _ = Task.Run(async () =>
    {
        Post(() => UpdatePhase = new UpdatePhase.Checking());
        try
        {
            var resp = await CallAsync("update.check");
            var status = resp["status"]?.GetValue<string>();
            Post(() => UpdatePhase = status switch
            {
                "available" => new UpdatePhase.Available(
                    resp["version"]?.GetValue<string>() ?? "",
                    resp["has_patch"]?.GetValue<bool>() ?? false),
                "latest" => new UpdatePhase.Latest(resp["message"]?.GetValue<string>() ?? "已是最新版本"),
                _ => new UpdatePhase.Failed(resp["message"]?.GetValue<string>() ?? "未找到可用更新"),
            });
        }
        catch (Exception ex)
        {
            Post(() => UpdatePhase = new UpdatePhase.Failed(ex.Message));
        }
    });

    /// <summary>用户在确认框里点了「更新」：下载并安装重启。</summary>
    internal void ConfirmUpdate() => StartDownload(autoApply: true);

    private void StartDownload(bool autoApply)
    {
        _autoApplyUpdate = autoApply;
        // 立即进入下载态：调用方（顶栏胶囊）据此马上给出反馈，不必等 RPC 返回
        Post(() =>
        {
            UpdateProgress = -1;
            UpdatePhase = new UpdatePhase.Downloading();
        });
        _ = Task.Run(async () =>
        {
            try
            {
                _ = await CallAsync("update.download");
            }
            catch (Exception ex)
            {
                _autoApplyUpdate = false;
                Post(() => UpdatePhase = new UpdatePhase.Failed(ex.Message));
            }
        });
    }

    /// <summary>
    /// 取消下载：立刻回到 idle（不必等后端的 update_cancelled 广播），
    /// 再让后端取消下载任务并清掉暂存目录。
    /// </summary>
    internal void CancelUpdate()
    {
        _autoApplyUpdate = false;
        Post(() =>
        {
            UpdateProgress = -1;
            UpdatePhase = new UpdatePhase.Idle();
        });
        _ = Task.Run(() => RunActionAsync("update.cancel"));
    }

    internal void ApplyUpdate() => _ = Task.Run(async () =>
    {
        Post(() => UpdateInstalling = true);
        try
        {
            _ = await CallAsync("update.apply");
        }
        catch (Exception ex)
        {
            // 安装失败（如 Windows 目录被占用）要把原因回给弹窗，
            // 否则用户只看到界面停在「正在安装」
            Post(() =>
            {
                _autoApplyUpdate = false;
                UpdateInstalling = false;
                UpdatePhase = new UpdatePhase.Failed($"安装更新失败：{ex.Message}");
            });
        }
    });

    internal void QuitBackend()
    {
        ConnState = ConnState.Stopped;
        _ = Task.Run(() => RunActionAsync("app.quit"));
    }

    // -------- 基础设施 --------

    /// <summary>切回 UI 线程执行（属性变更与集合更新必须在 UI 线程）。</summary>
    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    /// <summary>
    /// 属性赋值 + 变更通知。约定：所有调用点都必须在 UI 线程——要么本就在 UI
    /// 线程（事件处理、Post 回调内），要么经 <see cref="Post"/> 切回后再赋值。
    /// </summary>
    /// <returns>值确有变化并已发出通知时返回 true；调用方可据此连带刷新派生属性。</returns>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>JsonNode 读取小工具（协议里的列表字段形态不固定）。</summary>
internal static class JsonHelpers
{
    internal static IReadOnlyList<string> StringList(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }
        var result = new List<string>();
        foreach (var item in array)
        {
            if (item?.GetValue<string>() is { Length: > 0 } value)
            {
                result.Add(value);
            }
        }
        return result;
    }

    internal static IEnumerable<JsonObject> ObjectList(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>可空数值：JSON null（后端把 TqSdk 的 nan 转成了 null）返回 null。</summary>
    internal static double? Double(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary>可空整数：JSON 数字可能是 long 或 double，统一按数值解析。</summary>
    internal static int? Int(JsonNode? node) =>
        Double(node) is { } number ? (int)number : null;
}
