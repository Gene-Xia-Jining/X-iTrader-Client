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
        };
    }
}

/// <summary>服务器提供的交易策略（/api/strategies 目录项）</summary>
public sealed class StrategyInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
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

    public List<AccountRuntime> Accounts { get; set; } = [];

    public static EngineStatus From(JsonObject dict)
    {
        var status = new EngineStatus
        {
            AutoTrade = dict["auto_trade"]?.GetValue<bool>() ?? false,
            TradingActive = dict["trading_active"]?.GetValue<bool>() ?? false,
            ServerOnline = dict["server_online"]?.GetValue<bool>() ?? false,
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
    public bool AutoCheckUpdate { get; set; } = true;

    public static ConfigInfo From(JsonObject dict) => new()
    {
        ServerUrl = dict["server_url"]?.GetValue<string>() ?? "",
        ProxyUrl = dict["proxy_url"]?.GetValue<string>() ?? "",
        AutoCheckUpdate = dict["auto_check_update"]?.GetValue<bool>() ?? true,
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

    internal AppState(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    private UpdatePhase _updatePhase = new UpdatePhase.Idle();
    public UpdatePhase UpdatePhase
    {
        get => _updatePhase;
        private set => Set(ref _updatePhase, value);
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
                    AppendLog("SUCCESS", $"已连接后端 v{info.Version} (pid {info.Pid})");
                });
                await RefreshAllAsync(ct);
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
        AppendLog("ERROR", "后端进程已退出，正在重启…");
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
            throw new RpcException(ex.DisplayMessage, ex.ServerType);
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
                    AppendLog("INFO", $"收到信号: {signal["symbol"]?.GetValue<string>() ?? "?"} 目标仓位 {signal["position"]?.GetValue<string>() ?? "?"}");
                }
                break;
            case "trade_executed":
                AppendLog("SUCCESS", $"交易已执行: {data["symbol"]?.GetValue<string>() ?? "?"} 仓位 {data["target_position"]?.GetValue<string>() ?? "?"}");
                break;
            case "trade_failed":
                AppendLog("ERROR", $"交易失败: {data["symbol"]?.GetValue<string>() ?? "?"} {data["error"]?.GetValue<string>() ?? ""}");
                break;
            case "update_progress":
                break;  // 第一版不做进度条，完成/失败事件驱动状态
            case "update_ready":
                UpdatePhase = new UpdatePhase.Ready(data["kind"]?.GetValue<string>() ?? "");
                break;
            case "update_failed":
                UpdatePhase = new UpdatePhase.Failed(data["message"]?.GetValue<string>() ?? "下载失败");
                break;
            case "update_cancelled":
                UpdatePhase = new UpdatePhase.Idle();
                break;
            case "backend_stopping":
                ConnState = ConnState.Stopped;
                AppendLog("WARNING", $"后端正在退出（{data["reason"]?.GetValue<string>() ?? ""}）");
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

    internal void ToggleTrading(bool on) => _ = Task.Run(async () =>
    {
        await RunActionAsync(on ? "engine.start" : "engine.stop");
        await RefreshEngineOnlyAsync();
    });

    internal void SetAutoTrade(bool value) => _ = Task.Run(() =>
        RunActionAsync("engine.set_auto_trade", new JsonObject { ["value"] = value }));

    internal void SaveConfig(string? serverUrl, string? proxyUrl, bool? autoCheckUpdate = null)
    {
        var parameters = new JsonObject();
        if (serverUrl is not null) parameters["server_url"] = serverUrl;
        if (proxyUrl is not null) parameters["proxy_url"] = proxyUrl;
        if (autoCheckUpdate is not null) parameters["auto_check_update"] = autoCheckUpdate;
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
                    .Select(item => new StrategyInfo
                    {
                        Id = item["id"]?.GetValue<string>() ?? "",
                        Name = item["name"]?.GetValue<string>() ?? "",
                        Description = item["description"]?.GetValue<string>() ?? "",
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

    /// <summary>品种显示文本：有中文名时「中文｜代码」，否则纯代码</summary>
    internal string DisplaySymbol(string symbol) =>
        ServerSymbolNames.TryGetValue(symbol, out var name) && name.Length > 0
            ? $"{name}｜{symbol}"
            : symbol;

    internal async Task<string> TestTqAuthAsync(string account, string password)
    {
        try
        {
            var resp = await CallAsync("accounts.test_tq_auth",
                new JsonObject { ["account"] = account, ["password"] = password });
            return resp["message"]?.GetValue<string>() ?? "连接成功";
        }
        catch (Exception ex)
        {
            return $"连接失败: {ex.Message}";
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

    internal void CheckUpdate() => _ = Task.Run(async () =>
    {
        Post(() => UpdatePhase = new UpdatePhase.Checking());
        try
        {
            var resp = await CallAsync("update.check");
            Post(() => UpdatePhase = resp["status"]?.GetValue<string>() switch
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

    internal void DownloadUpdate() => _ = Task.Run(async () =>
    {
        try
        {
            _ = await CallAsync("update.download");
            Post(() => UpdatePhase = new UpdatePhase.Downloading());
        }
        catch (Exception ex)
        {
            Post(() => UpdatePhase = new UpdatePhase.Failed(ex.Message));
        }
    });

    internal void CancelUpdate() => _ = Task.Run(() => RunActionAsync("update.cancel"));

    internal void ApplyUpdate() => _ = Task.Run(() => RunActionAsync("update.apply"));

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
    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
}
