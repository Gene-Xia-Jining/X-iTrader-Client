using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XTrader.Shell.Core;

/// <summary>RPC 调用失败（对应 Swift 侧 RpcError）。</summary>
internal sealed class RpcException : Exception
{
    internal string ServerType { get; }

    internal RpcException(string message, string serverType = "Error") : base(message)
    {
        ServerType = serverType;
    }

    /// <summary>Error/Warning 之外的运行时异常保留类型名前缀，便于排障。</summary>
    internal string DisplayMessage =>
        ServerType is "RuntimeError" or "ValueError" ? Message : $"{ServerType}: {Message}";
}

/// <summary>
/// 本地 RPC 客户端：127.0.0.1 TCP + NDJSON，协议见 src/backend/PROTOCOL.md。
///
/// 响应与事件共用一条连接：按「有 event 键 → 事件；有 id 键 → 响应」分派。
/// 请求不设客户端超时——各端点在服务端均有界（网络探测 5s、TqApi 登录数秒、
/// 更新下载走事件推送），断连时所有挂起请求统一失败。
/// </summary>
internal sealed class RpcClient : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _token;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly object _stateLock = new();
    private int _nextId = 1;
    private bool _closed;

    /// <summary>事件回调（在读取线程触发，调用方负责切回 UI 线程）</summary>
    internal event Action<string, JsonObject>? EventReceived;

    /// <summary>连断回调</summary>
    internal event Action? Disconnected;

    internal RpcClient(BackendInfo info)
    {
        _port = info.Port;
        _token = info.Token;
    }

    /// <summary>连接并完成握手；失败抛出，由调用方重试。</summary>
    internal async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            return;
        }

        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port, ct);
        _client = client;
        _stream = client.GetStream();

        // 握手：服务端校验 token 后回 ok/version/pid，失败断开
        var handshake = new JsonObject { ["v"] = 1, ["token"] = _token };
        await SendLineAsync(handshake, ct);

        var line = await ReadLineAsync(ct)
            ?? throw new RpcException("后端握手无响应，连接已关闭");
        var obj = Parse(line);
        if (obj?["ok"]?.GetValue<bool>() != true)
        {
            var err = obj?["error"]?.GetValue<string>() ?? "未知错误";
            _closed = true;
            client.Close();
            throw new RpcException($"后端握手失败: {err}");
        }

        // 握手后开始常驻读取循环
        _ = Task.Run(() => ReadLoopAsync(ct), CancellationToken.None);
    }

    /// <summary>发起请求并等待响应。</summary>
    internal async Task<JsonObject> CallAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        if (_closed || _stream is null)
        {
            throw new RpcException("与后端的连接已断开");
        }

        int id;
        TaskCompletionSource<JsonObject> tcs;
        lock (_stateLock)
        {
            id = _nextId++;
            tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
        }

        var payload = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };

        try
        {
            await SendLineAsync(payload, ct);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _pending.Remove(id);
            }
            throw new RpcException($"发送请求失败: {ex.Message}");
        }

        using var reg = ct.Register(() =>
        {
            lock (_stateLock)
            {
                if (_pending.Remove(id, out var pending))
                {
                    pending.TrySetCanceled(ct);
                }
            }
        });
        return await tcs.Task;
    }

    internal async ValueTask DisposeAsync()
    {
        _closed = true;
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
            }
        }
        catch
        {
            // 关闭路径的异常无需上报
        }
        _client?.Dispose();
        _stream = null;
        _client = null;
        FailAllPending("与后端的连接已断开");
    }

    // -------- 收发 --------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!_closed)
            {
                var line = await ReadLineAsync(ct);
                if (line is null)
                {
                    break;  // 对端关闭
                }
                if (line.Length == 0)
                {
                    continue;
                }
                HandleLine(line);
            }
        }
        catch
        {
            // 读取异常按断连处理
        }
        finally
        {
            if (!_closed)
            {
                _closed = true;
                FailAllPending("与后端的连接已断开");
                Disconnected?.Invoke();
            }
        }
    }

    private async Task SendLineAsync(JsonObject obj, CancellationToken ct)
    {
        var stream = _stream ?? throw new RpcException("与后端的连接已断开");
        var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString() + "\n");
        await _sendLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 读取一行 UTF-8（\n 结尾）。返回 null 表示对端关闭。
    /// 跨多次 socket 读取保留缓冲区：一次 Read 可能拿到半行或一行半，
    /// 多余字节留在 <see cref="_recv"/> 供下次取用。
    /// </summary>
    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new RpcException("与后端的连接已断开");
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                if (TryExtractLine(out var buffered))
                {
                    return buffered;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0)
                {
                    // 对端关闭：把缓冲区剩余内容作为最后一行返回（若有）
                    if (_recv.Count == 0)
                    {
                        return null;
                    }
                    var tail = Encoding.UTF8.GetString(_recv.ToArray());
                    _recv.Clear();
                    return tail.TrimEnd('\r');
                }
                _recv.Write(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>缓冲区里若已有完整行（\n 结尾）则取出并返回，同时移除该行。</summary>
    private bool TryExtractLine(out string line)
    {
        var data = _recv.GetBuffer();
        var length = (int)_recv.Length;
        var newline = Array.IndexOf(data, (byte)'\n', 0, length);
        if (newline < 0)
        {
            line = "";
            return false;
        }
        line = Encoding.UTF8.GetString(data, 0, newline).TrimEnd('\r');
        // 把换行之后的剩余字节前移（_recv 容量不收缩，复用同一块缓冲）
        var remainder = length - (newline + 1);
        if (remainder > 0)
        {
            Buffer.BlockCopy(data, newline + 1, data, 0, remainder);
        }
        _recv.SetLength(remainder);
        _recv.Position = remainder;
        return true;
    }

    // 持久接收缓冲：存尚未组成完整行的字节
    private readonly MemoryStream _recv = new();

    private void HandleLine(string line)
    {
        var obj = Parse(line);
        if (obj is null)
        {
            return;
        }

        // 事件优先（无 id）
        if (obj["event"] is JsonValue eventName)
        {
            var data = obj["data"] as JsonObject ?? new JsonObject();
            EventReceived?.Invoke(eventName.GetValue<string>(), data);
            return;
        }

        // 响应（按 id 匹配）
        if (obj["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id))
        {
            TaskCompletionSource<JsonObject>? tcs;
            lock (_stateLock)
            {
                _pending.Remove(id, out tcs);
            }
            if (tcs is null)
            {
                return;
            }
            if (obj["ok"]?.GetValue<bool>() == true)
            {
                tcs.TrySetResult(obj["result"] as JsonObject ?? new JsonObject());
            }
            else
            {
                var error = obj["error"]?.GetValue<string>() ?? "未知错误";
                var type = obj["type"]?.GetValue<string>() ?? "Error";
                tcs.TrySetException(new RpcException(error, type));
            }
        }
    }

    private void FailAllPending(string message)
    {
        List<TaskCompletionSource<JsonObject>> pending;
        lock (_stateLock)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var tcs in pending)
        {
            tcs.TrySetException(new RpcException(message));
        }
    }

    private static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch
        {
            return null;  // 坏行忽略，不断开连接（与协议一致）
        }
    }
}
