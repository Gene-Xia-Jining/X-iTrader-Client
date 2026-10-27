using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XTrader.Shell.Core;

/// <summary>data/backend.json 的内容：后端启动后写入，壳据此连接。</summary>
internal sealed class BackendInfo
{
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

/// <summary>
/// 后端进程管理：定位启动命令、spawn、等待发现文件、复用存活实例、终止。
///
/// Windows 上程序目录的布局：
/// <code>
///   X-iTrader.exe                       壳（根级）
///   backend\runtime\python.exe          嵌入式 Python 与依赖
///   backend\code\backend_entry.py       源码入口
/// </code>
/// 启动命令固定为 <c>backend\runtime\python.exe -E -s backend\code\backend_entry.py</c>。
/// 布局探测顺序：
/// - 同目录：与壳 exe 同目录的 backend\ 子树（发布包的实际形态）。
/// - 开发态：ITRADER_PYTHON 指定解释器 + 仓库根（含 src/backend/__main__.py），
///   以 `python -m src.backend` 启动，便于本机调试后端而无需先打包。
///
/// 后端 workdir 固定为数据根（后端自身也会 chdir 到同一路径），
/// data/ 与程序安装位置无关。
/// </summary>
internal sealed class BackendProcess : IDisposable
{
    /// <summary>后端入口脚本（相对 backend\ 根），与壳的路径约定。</summary>
    internal const string EntryScriptName = "backend_entry.py";

    private enum LaunchPlanKind { Packaged, Python }

    private readonly LaunchPlanKind _kind;
    private readonly string _executable;
    /// <summary>入口脚本：Packaged 下传给解释器；Python 模式为空（用 -m）。</summary>
    private readonly string _entryScript;
    private readonly string _repoRoot;
    private Process? _process;

    /// <summary>崩溃/退出回调（后台线程触发）</summary>
    internal event Action? Exited;

    /// <summary>业务数据根（后端 workdir，也是 backend.json 所在目录的父级）</summary>
    internal string WorkDir { get; }

    internal string InfoFile => Path.Combine(WorkDir, "data", "backend.json");

    /// <summary>后端最近若干行输出（stdout+stderr），启动失败时用于展示真正原因。</summary>
    internal string OutputTail
    {
        get
        {
            lock (_outputLock)
            {
                return string.Join(Environment.NewLine, _output);
            }
        }
    }

    private readonly object _outputLock = new();
    private readonly Queue<string> _output = new();
    /// <summary>只保留末尾若干行：排障够用，避免长期运行堆积无限增长</summary>
    private const int OutputTailLines = 20;

    internal BackendInfo? Info { get; private set; }

    internal BackendProcess(string? repoRootOverride = null, string? workDirOverride = null)
    {
        WorkDir = workDirOverride ?? (repoRootOverride is null ? AppPaths.DataRoot : repoRootOverride);
        Directory.CreateDirectory(WorkDir);

        if (repoRootOverride is not null)
        {
            _kind = LaunchPlanKind.Python;
            _repoRoot = repoRootOverride;
            _entryScript = "";
            _executable = PythonPath(_repoRoot);
            if (!File.Exists(_executable))
            {
                throw new BackendException($"未找到 Python 解释器: {_executable}");
            }
            return;
        }

        var (kind, executable, entryScript) = DetectPlan();
        _kind = kind;
        _executable = executable;
        _entryScript = entryScript;
        _repoRoot = "";
        if (!File.Exists(_executable))
        {
            throw new BackendException($"未找到后端可执行文件: {_executable}");
        }
    }

    /// <summary>已有后端在运行（backend.json 中 pid 存活）时直接复用，不 spawn 新进程。</summary>
    internal async Task<BackendInfo> ConnectOrStartAsync(CancellationToken ct = default)
    {
        if (ReadInfo() is { } existing && IsPidAlive(existing.Pid))
        {
            Info = existing;
            return existing;
        }
        return await StartAsync(ct);
    }

    internal async Task<BackendInfo> StartAsync(CancellationToken ct = default)
    {
        if (_process is not null)
        {
            throw new BackendException("后端已在运行");
        }

        try { File.Delete(InfoFile); } catch { /* 不存在或占用，均不影响后续等待 */ }

        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,  // 后端是 console=True 的 exe，避免弹出黑窗
        };
        if (_kind == LaunchPlanKind.Python)
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("src.backend");
            psi.Environment["PYTHONPATH"] = _repoRoot;
        }
        else
        {
            // -E -s：隔离环境变量与 user site，保证只认包内 site-packages
            psi.ArgumentList.Add("-E");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(_entryScript);
        }

        // 后端日志默认丢弃（发现文件是主通道）；调 ITRADER_BACKEND_VERBOSE=1 时继承输出便于排障
        var verbose = Environment.GetEnvironmentVariable("ITRADER_BACKEND_VERBOSE") is not null;
        if (!verbose)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        var proc = new Process { StartInfo = psi };
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            _process = null;
            Exited?.Invoke();
        };

        if (!verbose)
        {
            // 重定向后必须消费，否则管道写满会阻塞后端。输出不能直接丢弃：
            // 后端若在写出 backend.json 前就退出（缺依赖、路径不可写等），
            // 唯一线索就是 stderr，丢了就只能看到「后端进程已退出」在循环里刷屏。
            proc.OutputDataReceived += (_, e) => CaptureOutput(e.Data);
            proc.ErrorDataReceived += (_, e) => CaptureOutput(e.Data);
        }

        proc.Start();
        if (!verbose)
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        _process = proc;

        if (await WaitForInfoAsync(TimeSpan.FromSeconds(15), ct) is not { } found)
        {
            // 把后端自己的输出带出去：崩溃、缺依赖、data 目录不可写都会在这里
            // 留下痕迹，没有它只能在界面上看到一句「等待超时」
            var tail = OutputTail;
            throw new BackendException(
                tail.Length > 0
                    ? $"等待 {InfoFile} 超时，后端启动失败：{Environment.NewLine}{tail}"
                    : $"等待 {InfoFile} 超时，后端可能启动失败（后端无输出）");
        }
        Info = found;
        return found;
    }

    /// <summary>
    /// 终止后端。Windows 无 SIGINT，优雅退出走后端自己的 API（app.quit RPC，
    /// 由 AppState 负责）；这里仅做兜底 Kill，用于壳异常退出等场景。
    /// </summary>
    internal void Terminate()
    {
        var proc = _process;
        if (proc is null || proc.HasExited)
        {
            return;
        }
        try
        {
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
        }
        catch
        {
            // 进程可能刚好自行退出
        }
    }

    /// <summary>向存活的后端进程发 Ctrl 信号不可行，改为判断 pid 存活以便复用。</summary>
    internal static bool IsPidAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    internal static BackendInfo? ReadInfoFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<BackendInfo>(stream);
        }
        catch
        {
            // 后端正在写入（文件半成品）或格式异常：视为尚未就绪，调用方会重试
            return null;
        }
    }

    private BackendInfo? ReadInfo() => ReadInfoFrom(InfoFile);

    /// <summary>收后端输出末尾若干行。在后台读取线程上调用。</summary>
    private void CaptureOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lock (_outputLock)
        {
            _output.Enqueue(line);
            while (_output.Count > OutputTailLines)
            {
                _output.Dequeue();
            }
        }
    }

    private async Task<BackendInfo?> WaitForInfoAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadInfo() is { } info)
            {
                return info;
            }
            await Task.Delay(200, ct);
        }
        return null;
    }

    private static string PythonPath(string repoRoot)
    {
        var overridePath = Environment.GetEnvironmentVariable("ITRADER_PYTHON");
        return !string.IsNullOrEmpty(overridePath)
            ? overridePath
            : Path.Combine(repoRoot, ".venv", "Scripts", "python.exe");
    }

    /// <summary>按序探测可用的启动方式，返回 (类型, 解释器路径, 入口脚本)。</summary>
    private static (LaunchPlanKind, string, string) DetectPlan()
    {
        // 1) 发布包：与壳同级的 backend\（runtime\ + code\），见 specs/build_backend.py
        var backendRoot = Path.Combine(AppContext.BaseDirectory, "backend");
        if (PackagedPlan(backendRoot) is { } packaged)
        {
            return (LaunchPlanKind.Packaged, packaged.python, packaged.entry);
        }

        // 2) 开发态：从当前目录向上找含 src/backend/__main__.py 的仓库根
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "backend", "__main__.py")))
            {
                return (LaunchPlanKind.Python, PythonPath(dir.FullName), "");
            }
        }

        throw new BackendException(
            $"未找到后端。已尝试：\n  {backendRoot}\\runtime\\python.exe\n  {backendRoot}\\code\\{EntryScriptName}");
    }

    /// <summary>在 backend 根下探测解释器与入口脚本；不完整时返回 null。</summary>
    private static (string python, string entry)? PackagedPlan(string backendRoot)
    {
        var python = Path.Combine(backendRoot, "runtime", "python.exe");
        var entry = Path.Combine(backendRoot, "code", EntryScriptName);
        return File.Exists(python) && File.Exists(entry) ? (python, entry) : null;
    }

    public void Dispose()
    {
        _process?.Dispose();
    }
}

/// <summary>后端启动/定位失败。</summary>
internal sealed class BackendException(string message) : Exception(message);
