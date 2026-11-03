using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Xunit;
using static PE.TestHost.Tests.FakeAgent;

namespace PE.TestHost.Tests;

public class AgentModeTests
{
    private const string Status = "{\"uptimeSec\":12,\"protocol\":1,\"missionActive\":true,\"missionScene\":\"mp_test\",\"peerCount\":2,\"modules\":[\"Native\",\"PersistentEmpires\"]}";
    private const string FailingRun = "{\"summary\":{\"passed\":1,\"failed\":1,\"skipped\":0,\"durationMs\":9},\"tests\":[" +
        "{\"name\":\"A\",\"status\":\"passed\",\"durationMs\":4,\"message\":null}," +
        "{\"name\":\"B\",\"status\":\"failed\",\"durationMs\":5,\"message\":\"boom\"}]}";
    private const string PassingRun = "{\"summary\":{\"passed\":1,\"failed\":0,\"skipped\":0,\"durationMs\":4},\"tests\":[" +
        "{\"name\":\"A\",\"status\":\"passed\",\"durationMs\":4,\"message\":null}]}";

    private static IEnumerable<string>? Standard(int id, string cmd, JsonNode? args) => cmd switch
    {
        "ping" => new[] { Ok(id, "{\"pong\":true}") },
        "status" => new[] { Ok(id, Status) },
        "exec" => new[] { Ok(id, $"{{\"output\":\"ran {args!["command"]}\"}}") },
        _ => new[] { Err(id, "unknown_command", cmd) },
    };

    private static async Task<(int code, string stdout, string stderr)> Run(int port, string command, params string[] extra)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var args = new List<string> { "--run", command, "--connect", $"127.0.0.1:{port}", "--log-file", "none", "--log-level", "debug" };
        args.AddRange(extra);
        var code = await Program.RunAsync(args.ToArray(), o, e, TextReader.Null, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    private static JsonObject Parse(string stdout)
    {
        Assert.DoesNotContain('\u001b', stdout);
        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return JsonNode.Parse(stdout)!.AsObject();
    }

    [Fact]
    public async Task HelloHandshakeAndPing()
    {
        using var agent = new FakeAgent(Standard);
        var r = await Run(agent.Port, "ping");
        Assert.Equal(0, r.code);
        Assert.Equal("pong", r.stdout.Trim());
        Assert.Contains("hello", r.stderr);
        Assert.Contains("\"cmd\":\"ping\"", agent.Requests.Single());
    }

    [Fact]
    public async Task StatusJson()
    {
        using var agent = new FakeAgent(Standard);
        var j = Parse((await Run(agent.Port, "status", "--json")).stdout);
        Assert.True(j["ok"]!.GetValue<bool>());
        Assert.Equal("status", j["command"]!.GetValue<string>());
        Assert.Equal(0, j["exitCode"]!.GetValue<int>());
        Assert.Null(j["error"]);
        Assert.Equal("mp_test", j["result"]!["missionScene"]!.GetValue<string>());
        Assert.Equal(2, j["result"]!["modules"]!.AsArray().Count);
    }

    [Fact]
    public async Task ExecSendsCommandArg()
    {
        using var agent = new FakeAgent(Standard);
        var r = await Run(agent.Port, "exec list_players now");
        Assert.Equal(0, r.code);
        Assert.Equal("ran list_players now", r.stdout.Trim());
    }

    [Fact]
    public async Task FailingTestExitsOne()
    {
        using var agent = new FakeAgent((id, cmd, a) => new[] { Ok(id, FailingRun) });
        var r = await Run(agent.Port, "test Foo", "--json");
        Assert.Equal(1, r.code);
        var j = Parse(r.stdout);
        Assert.False(j["ok"]!.GetValue<bool>());
        Assert.Equal(1, j["exitCode"]!.GetValue<int>());
        Assert.Equal("tests_failed", j["error"]!["code"]!.GetValue<string>());
        Assert.Equal(1, j["result"]!["summary"]!["failed"]!.GetValue<int>());
        Assert.Contains("\"filter\":\"Foo\"", agent.Requests.Single());
    }

    [Fact]
    public async Task AllPassExitsZero()
    {
        using var agent = new FakeAgent((id, cmd, a) => new[] { Ok(id, PassingRun) });
        var r = await Run(agent.Port, "test");
        Assert.Equal(0, r.code);
        Assert.Contains("passed 1, failed 0", r.stdout);
    }

    [Fact]
    public async Task NotImplementedExitsTwo()
    {
        using var agent = new FakeAgent((id, cmd, a) => new[] { Err(id, "not_implemented", "test runner not built yet") });
        var r = await Run(agent.Port, "test", "--json");
        Assert.Equal(2, r.code);
        Assert.Equal("not_implemented", Parse(r.stdout)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConnectionRefusedExitsTwo()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var r = await Run(port, "status", "--json");
        Assert.Equal(2, r.code);
        Assert.Equal("connect_failed", Parse(r.stdout)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task TimeoutExitsTwo()
    {
        using var agent = new FakeAgent((id, cmd, a) => null);
        var r = await Run(agent.Port, "ping", "--json", "--timeout", "1");
        Assert.Equal(2, r.code);
        Assert.Equal("timeout", Parse(r.stdout)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task EventsInterleavedWithResponsesAreNotResponses()
    {
        using var agent = new FakeAgent((id, cmd, a) => new[]
        {
            Ev("test.started", "{\"name\":\"A\"}"), Ev("log", "{\"line\":\"x\"}"),
            Ok(id, PassingRun), Ev("test.finished", "{}"),
        });
        var r = await Run(agent.Port, "test", "--json");
        Assert.Equal(0, r.code);
        Assert.Single(Parse(r.stdout)["result"]!["tests"]!.AsArray());
        Assert.Contains("event test.started", r.stderr);
    }

    [Fact]
    public async Task EventBeforeResponseWithMismatchedIdIsIgnored()
    {
        using var agent = new FakeAgent((id, cmd, a) => new[] { Ok(id + 100, "{}"), Ok(id, "{\"pong\":true}") });
        Assert.Equal(0, (await Run(agent.Port, "ping")).code);
    }

    [Fact]
    public async Task BadArgsExitTwo()
    {
        var o = new StringWriter();
        var code = await Program.RunAsync(new[] { "--run", "ping", "--timeout", "abc", "--json" }, o, new StringWriter(), TextReader.Null, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Equal("bad_args", Parse(o.ToString())["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void LoggerRotatesAndKeepsFive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pe-testhost-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "t.log");
        var log = new Logger(TextWriter.Null, LogLevel.Trace, file, maxBytes: 200, keep: 5);
        Parallel.For(0, 200, i => log.Info(Logger.Test, new string('x', 50) + i));
        Assert.True(File.Exists(file + ".5"));
        Assert.False(File.Exists(file + ".6"));
        Directory.Delete(dir, true);
    }
}
