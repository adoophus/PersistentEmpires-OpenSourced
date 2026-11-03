using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PE.TestHost;

public sealed record Outcome(bool Ok, int ExitCode, JsonNode? Result, string? ErrorCode, string? ErrorMessage, string Human);

public sealed class Options
{
    public string? Run; public bool Json; public int TimeoutSec = 60;
    public string Host = "127.0.0.1"; public int Port = DefaultPort;
    public LogLevel Level = LogLevel.Info; public string? LogFile = Logger.DefaultFile;

    public static int DefaultPort =>
        int.TryParse(Environment.GetEnvironmentVariable("PE_TESTAGENT_PORT"), out var p) ? p : 7900;

    public static bool TryParseEndpoint(string s, out string host, out int port)
    {
        host = ""; port = 0;
        var i = s.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(s[(i + 1)..], out port) || port is < 1 or > 65535) return false;
        host = s[..i];
        return true;
    }

    /// <summary>Returns an error message, or null when args are valid.</summary>
    public string? Parse(string[] a)
    {
        for (var i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            try
            {
                switch (a[i])
                {
                    case "--run": Run = Next(); break;
                    case "--json": Json = true; break;
                    case "--timeout": if (!int.TryParse(Next(), out TimeoutSec) || TimeoutSec <= 0) return "invalid --timeout"; break;
                    case "--connect": if (!TryParseEndpoint(Next(), out Host, out Port)) return "invalid --connect, expected ip:port"; break;
                    case "--log-level": if (!Logger.TryParseLevel(Next(), out Level)) return "invalid --log-level (trace|debug|info|warn|error)"; break;
                    case "--log-file": var f = Next(); LogFile = f == "none" ? null : f; break;
                    default: return $"unknown argument '{a[i]}'";
                }
            }
            catch (ArgumentException e) { return e.Message; }
        }
        return null;
    }
}

public static class Program
{
    public static Task<int> Main(string[] args) =>
        RunAsync(args, Console.Out, Console.Error, Console.In, CancellationToken.None);

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, TextReader stdin, CancellationToken ct)
    {
        var o = new Options();
        var bad = o.Parse(args);
        var agentMode = o.Run != null;
        if (bad != null)
        {
            if (args.Contains("--json")) stdout.WriteLine(Envelope(o.Run ?? "", Fail(2, "bad_args", bad), 0));
            else stderr.WriteLine($"error: {bad}");
            return 2;
        }
        var log = new Logger(agentMode ? stderr : stdout, o.Level, o.LogFile);
        return agentMode
            ? await AgentModeAsync(o, log, stdout, ct)
            : await ReplAsync(o, log, stdout, stdin, ct);
    }

    private static AgentClient NewClient(Logger log)
    {
        var c = new AgentClient(log);
        c.EventReceived += (name, data) =>
            log.Info(name.StartsWith("test", StringComparison.Ordinal) ? Logger.Test : Logger.Net, $"event {name}: {data?.ToJsonString()}");
        return c;
    }

    private static async Task<int> AgentModeAsync(Options o, Logger log, TextWriter stdout, CancellationToken ct)
    {
        using var client = NewClient(log);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSec));
        Outcome outcome;
        try
        {
            var (verb, _, bad) = ParseCommand(o.Run!);
            if (bad != null) outcome = Fail(2, "bad_args", bad);
            else
            {
                if (verb != "help") await client.ConnectAsync(o.Host, o.Port, cts.Token);
                outcome = await ExecuteAsync(client, o.Run!, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = Fail(2, "timeout", $"timed out after {o.TimeoutSec}s");
        }
        catch (AgentException e) { outcome = Fail(2, e.Code, e.Message); }
        catch (Exception e) when (e is not OperationCanceledException) { outcome = Fail(2, "internal_error", e.Message); }
        log.Info(Logger.Engine, $"'{o.Run}' -> exit {outcome.ExitCode} in {sw.ElapsedMilliseconds} ms");
        if (o.Json) stdout.WriteLine(Envelope(o.Run!, outcome, sw.ElapsedMilliseconds));
        else stdout.WriteLine(outcome.Human);
        return outcome.ExitCode;
    }

    private static Outcome Fail(int exit, string code, string msg) => new(false, exit, null, code, msg, $"error [{code}]: {msg}");

    public static string Envelope(string command, Outcome r, long ms) => new JsonObject
    {
        ["command"] = command,
        ["ok"] = r.Ok,
        ["exitCode"] = r.ExitCode,
        ["durationMs"] = ms,
        ["result"] = r.Result,
        ["error"] = r.ErrorCode == null ? null : new JsonObject { ["code"] = r.ErrorCode, ["message"] = r.ErrorMessage },
    }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public const string HelpText = """
        commands:
          connect <ip:port>   connect to a PE.TestAgent
          disconnect          close the connection
          status              agent/mission status
          ping                round-trip check
          exec <text>         run a server console command
          test [filter]       run tests matching filter
          help                this text
          quit                exit
        """;

    /// <summary>Runs one REPL-style command line against a connected client.</summary>
    public static async Task<Outcome> ExecuteAsync(AgentClient client, string line, CancellationToken ct)
    {
        var (verb, rest, bad) = ParseCommand(line);
        if (bad != null) return Fail(2, "bad_args", bad);
        try
        {
            switch (verb)
            {
                case "help": return new Outcome(true, 0, null, null, null, HelpText);
                case "ping":
                {
                    var r = await client.SendAsync("ping", null, ct);
                    return Ok(r, r?["pong"]?.GetValue<bool>() == true ? "pong" : "unexpected ping reply");
                }
                case "status":
                {
                    var r = await client.SendAsync("status", null, ct);
                    var mods = string.Join(", ", (r?["modules"] as JsonArray ?? new JsonArray()).Select(m => m?.ToString()));
                    return Ok(r, $"uptime: {r?["uptimeSec"]}s  protocol: {r?["protocol"]}\n" +
                                 $"mission: {(r?["missionActive"]?.GetValue<bool>() == true ? r["missionScene"]?.ToString() : "none")}  peers: {r?["peerCount"]}\n" +
                                 $"modules: {mods}");
                }
                case "exec":
                {
                    var r = await client.SendAsync("exec", new JsonObject { ["command"] = rest }, ct);
                    return Ok(r, r?["output"]?.ToString() ?? "");
                }
                case "test":
                {
                    var r = await client.SendAsync("test", new JsonObject { ["filter"] = rest }, ct);
                    return TestOutcome(r);
                }
                default: return Fail(2, "bad_args", $"unknown command '{verb}' (try 'help')");
            }
        }
        catch (AgentException e) { return Fail(2, e.Code, e.Message); }
        catch (Exception e) when (e is not OperationCanceledException) { return Fail(2, "internal_error", e.Message); }
    }

    /// <summary>Splits and validates a command line without touching the network.</summary>
    public static (string verb, string rest, string? error) ParseCommand(string line)
    {
        line = line.Trim();
        var sp = line.IndexOf(' ');
        var verb = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
        return verb switch
        {
            "help" or "ping" or "status" or "test" => (verb, rest, null),
            "exec" => rest.Length == 0 ? (verb, rest, "usage: exec <text>") : (verb, rest, null),
            _ => (verb, rest, $"unknown command '{verb}' (try 'help')"),
        };
    }

    private static Outcome Ok(JsonNode? r, string human) => new(true, 0, r, null, null, human);

    private static Outcome TestOutcome(JsonNode? r)
    {
        var tests = r?["tests"] as JsonArray ?? new JsonArray();
        static bool IsInt(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out _);
        var sum = r?["summary"];
        if (sum is not JsonObject || !IsInt(sum["passed"]) || !IsInt(sum["failed"]))
            return Fail(2, "protocol_error", "test result has no valid 'summary' (passed/failed)");
        var failed = Math.Max((int)sum["failed"]!, tests.Count(t => t?["status"]?.ToString() == "failed"));
        var human = new System.Text.StringBuilder();
        foreach (var t in tests)
        {
            var st = t?["status"]?.ToString() ?? "?";
            human.AppendLine($"  {st.ToUpperInvariant(),-7} {t?["name"]} ({t?["durationMs"]} ms){(string.IsNullOrEmpty(t?["message"]?.ToString()) ? "" : " - " + t!["message"])}");
        }
        human.Append($"passed {sum?["passed"] ?? 0}, failed {failed}, skipped {sum?["skipped"] ?? 0} in {sum?["durationMs"] ?? 0} ms");
        return failed > 0
            ? new Outcome(false, 1, r, "tests_failed", $"{failed} test(s) failed", human.ToString())
            : new Outcome(true, 0, r, null, null, human.ToString());
    }

    private static async Task<int> ReplAsync(Options o, Logger log, TextWriter stdout, TextReader stdin, CancellationToken ct)
    {
        var client = NewClient(log);
        stdout.WriteLine("PE.TestHost REPL. Type 'help' for commands.");
        async Task Connect(string host, int port)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(o.TimeoutSec, 10)));
            try { await client.ConnectAsync(host, port, cts.Token); stdout.WriteLine($"connected to {host}:{port}"); }
            catch (OperationCanceledException) { stdout.WriteLine("error [timeout]: connect timed out"); }
            catch (AgentException e) { stdout.WriteLine($"error [{e.Code}]: {e.Message}"); }
        }
        await Connect(o.Host, o.Port);
        while (true)
        {
            stdout.Write("pe> ");
            var line = await stdin.ReadLineAsync(ct);
            if (line == null) return 0;
            line = line.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "quit" or "exit": client.Dispose(); return 0;
                case "connect":
                    if (parts.Length < 2 || !Options.TryParseEndpoint(parts[1], out var h, out var p)) stdout.WriteLine("usage: connect <ip:port>");
                    else { client.Dispose(); client = NewClient(log); await Connect(h, p); }
                    break;
                case "disconnect": client.Dispose(); stdout.WriteLine("disconnected"); break;
                default:
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(o.TimeoutSec)))
                    {
                        try { stdout.WriteLine((await ExecuteAsync(client, line, cts.Token)).Human); }
                        catch (OperationCanceledException) { stdout.WriteLine("error [timeout]: no response"); }
                    }
                    break;
            }
        }
    }
}
