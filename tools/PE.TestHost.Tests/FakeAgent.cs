using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PE.TestHost.Tests;

/// <summary>In-process fake PE.TestAgent speaking protocol v1. The handler returns raw lines to send, or null to stay silent.</summary>
public sealed class FakeAgent : IDisposable
{
    public delegate IEnumerable<string>? Handler(int id, string cmd, JsonNode? args);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Handler _handler;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<string> Requests { get; } = new();

    public FakeAgent(Handler handler)
    {
        _handler = handler;
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public static string Ok(int id, string resultJson) => $"{{\"id\":{id},\"ok\":true,\"result\":{resultJson}}}";
    public static string Err(int id, string code, string msg) => $"{{\"id\":{id},\"ok\":false,\"error\":{{\"code\":\"{code}\",\"message\":\"{msg}\"}}}}";
    public static string Ev(string name, string dataJson = "{}") => $"{{\"event\":\"{name}\",\"data\":{dataJson}}}";

    private async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                var c = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => Serve(c));
            }
        }
        catch (Exception) { }
    }

    private async Task Serve(TcpClient c)
    {
        using var _ = c;
        var s = c.GetStream();
        var r = new StreamReader(s, Encoding.UTF8);
        var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            await w.WriteLineAsync(Ev("hello", "{\"agent\":\"PE.TestAgent\",\"agentVersion\":\"0.1.0\",\"protocol\":1}"));
            string? line;
            while ((line = await r.ReadLineAsync()) != null)
            {
                lock (Requests) Requests.Add(line);
                var req = JsonNode.Parse(line)!;
                var replies = _handler(req["id"]!.GetValue<int>(), req["cmd"]!.GetValue<string>(), req["args"]);
                if (replies == null) continue;
                foreach (var l in replies) await w.WriteLineAsync(l);
            }
        }
        catch (IOException) { }
    }

    public void Dispose() => _listener.Stop();
}
