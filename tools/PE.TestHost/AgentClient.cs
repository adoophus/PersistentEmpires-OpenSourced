using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PE.TestHost;

/// <summary>Failure with a stable snake_case code (infra problems and agent-reported errors alike).</summary>
public sealed class AgentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Client for the PE.TestAgent control channel, protocol v1 (line-delimited JSON over TCP).</summary>
public sealed class AgentClient : IDisposable
{
    private readonly Logger _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly TaskCompletionSource<JsonObject> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private int _nextId;

    public AgentClient(Logger log) => _log = log;

    public bool Connected => _tcp?.Connected == true && !_readerDone;
    private volatile bool _readerDone;

    /// <summary>Raised for unsolicited {"event":...} lines (on the reader thread).</summary>
    public event Action<string, JsonNode?>? EventReceived;

    public JsonObject? Hello { get; private set; }

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        _log.Info(Logger.Net, $"connecting to {host}:{port}");
        _tcp = new TcpClient();
        try { await _tcp.ConnectAsync(host, port, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { throw new AgentException("connect_failed", $"cannot connect to {host}:{port}: {e.Message}"); }
        var stream = _tcp.GetStream();
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(() => ReadLoop(new StreamReader(stream, Encoding.UTF8)));
        Hello = await _hello.Task.WaitAsync(ct);
        _log.Info(Logger.Net, $"hello: {Hello["data"]?.ToJsonString()}");
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private async Task ReadLoop(StreamReader reader)
    {
        AgentException? failure = null;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                _log.Trace(Logger.Net, $"<- {line}");
                JsonObject? obj;
                try { obj = JsonNode.Parse(line) as JsonObject; }
                catch (System.Text.Json.JsonException) { obj = null; }
                if (obj == null) { _log.Warn(Logger.Net, $"non-JSON line ignored: {line}"); continue; }
                if (obj.ContainsKey("event"))
                {
                    if (obj["event"] is not JsonValue ev || !ev.TryGetValue<string>(out var name))
                        throw new AgentException("protocol_error", "event name is not a string");
                    if (name == "hello") _hello.TrySetResult(obj);
                    else EventReceived?.Invoke(name, obj["data"]);
                }
                else if (obj["id"] is JsonValue idv && idv.TryGetValue<int>(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(obj);
                    else _log.Warn(Logger.Net, $"response for unknown id ignored: {line}");
                }
                else if (obj["ok"] is JsonValue okv && okv.TryGetValue<bool>(out var ok) && !ok)
                {
                    // Error without an id (e.g. bad_request): cannot be correlated, so fail whatever is pending.
                    var err = obj["error"];
                    failure = new AgentException(Str(err?["code"]) ?? "protocol_error", Str(err?["message"]) ?? "agent reported an error");
                    break;
                }
                else _log.Warn(Logger.Net, $"unmatched message ignored: {line}");
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        catch (Exception e)
        {
            failure = e as AgentException ?? new AgentException("protocol_error", $"reader failed: {e.Message}");
        }
        _readerDone = true;
        if (failure != null)
        {
            _log.Error(Logger.Net, $"[{failure.Code}] {failure.Message}");
            _hello.TrySetException(failure);
            foreach (var kv in _pending) if (_pending.TryRemove(kv.Key, out var t)) t.TrySetException(failure);
            return;
        }
        var closed = new AgentException("connection_closed", "agent closed the connection");
        _hello.TrySetException(closed);
        foreach (var kv in _pending) if (_pending.TryRemove(kv.Key, out var t)) t.TrySetException(closed);
    }

    /// <summary>Sends a command and returns its "result". Throws <see cref="AgentException"/> on ok:false.</summary>
    public async Task<JsonNode?> SendAsync(string cmd, JsonObject? args, CancellationToken ct)
    {
        if (_writer == null || !Connected) throw new AgentException("not_connected", "not connected; use 'connect <ip:port>'");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var req = new JsonObject { ["id"] = id, ["cmd"] = cmd, ["args"] = args ?? new JsonObject() }.ToJsonString();
        _log.Trace(Logger.Net, $"-> {req}");
        try
        {
            await _writer.WriteLineAsync(req.AsMemory(), ct);
            var resp = await tcs.Task.WaitAsync(ct);
            if (resp["ok"] is not JsonValue okv || !okv.TryGetValue<bool>(out var ok))
                throw new AgentException("protocol_error", "response 'ok' is missing or not a boolean");
            if (ok) return resp["result"]?.DeepClone();
            var err = resp["error"];
            throw new AgentException(Str(err?["code"]) ?? "protocol_error",
                Str(err?["message"]) ?? "agent returned an error without details");
        }
        catch (IOException e) { throw new AgentException("connection_closed", e.Message); }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Dispose()
    {
        try { _tcp?.Dispose(); } catch { }
    }
}
