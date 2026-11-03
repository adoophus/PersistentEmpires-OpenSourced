using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;

namespace PE.TestAgent
{
    /// <summary>Server-side control channel. Protocol: see PROTOCOL.md.</summary>
    public class TestAgentSubModule : MBSubModuleBase
    {
        public const string Version = "0.1.0";
        public const int Protocol = 1;

        private static readonly ConcurrentQueue<Action> MainQueue = new ConcurrentQueue<Action>();
        private static readonly DateTime Started = DateTime.UtcNow;
        private TcpListener _listener;
        private volatile bool _stop;

        protected override void OnSubModuleLoad()
        {
            int.TryParse(Environment.GetEnvironmentVariable("PE_TESTAGENT_PORT"), out int port);
            if (port <= 0) port = 7900;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "PE.TestAgent.Accept" }.Start();
                Log.Info("listening on 127.0.0.1:" + port);
            }
            catch (Exception e) { Log.Error("listener failed: " + e); }
        }

        protected override void OnSubModuleUnloaded()
        {
            _stop = true;
            try { _listener?.Stop(); } catch { }
        }

        // Main thread: the only place game state is touched.
        protected override void OnApplicationTick(float dt)
        {
            int n = 0;
            while (n++ < 64 && MainQueue.TryDequeue(out var a))
            {
                try { a(); } catch (Exception e) { Log.Error("main-thread task failed: " + e); }
            }
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); } catch { break; }
                new Thread(() => Serve(c)) { IsBackground = true, Name = "PE.TestAgent.Client" }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            Log.Info("client connected");
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
                {
                    writer.WriteLine("{\"event\":\"hello\",\"data\":{\"agent\":\"PE.TestAgent\",\"agentVersion\":\"" + Version + "\",\"protocol\":" + Protocol + "}}");
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        string reply = Handle(line, out bool shutdown);
                        writer.WriteLine(reply);
                        if (shutdown) { MainQueue.Enqueue(() => Utilities.QuitGame()); break; }
                    }
                }
            }
            catch (Exception e) { Log.Warn("client error: " + e.Message); }
            Log.Info("client disconnected");
        }

        private static string Handle(string line, out bool shutdown)
        {
            shutdown = false;
            int? id;
            string cmd;
            JsonElement args = default;
            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int idv)
                        || !root.TryGetProperty("cmd", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String)
                        return Err(null, "bad_request", "expected {\"id\":int,\"cmd\":string,\"args\":object}");
                    id = idv;
                    cmd = cmdEl.GetString();
                    if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object) args = a.Clone();
                }
            }
            catch (JsonException e) { return Err(null, "bad_request", "malformed JSON: " + e.Message); }

            Log.Debug("cmd " + cmd + " id " + id);
            try
            {
                switch (cmd)
                {
                    case "ping": return Ok(id, new { pong = true });
                    case "status": return Ok(id, OnMain(Status));
                    case "exec":
                        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String)
                            return Err(id, "bad_request", "exec requires args.command (string)");
                        string command = c.GetString();
                        return Ok(id, OnMain(() => new { output = Exec(command) }));
                    case "test": return Err(id, "not_implemented", "test framework is not implemented yet");
                    case "shutdown": shutdown = true; return Ok(id, new { });
                    default: return Err(id, "unknown_command", "unknown command: " + cmd);
                }
            }
            catch (Exception e)
            {
                Log.Error("cmd " + cmd + " failed: " + e);
                return Err(id, "internal_error", e.Message);
            }
        }

        private static object Status()
        {
            var mods = new List<string>();
            foreach (var m in ModuleHelper.GetActiveModules()) mods.Add(m.Id);
            Mission cur = Mission.Current;
            return new
            {
                uptimeSec = (int)(DateTime.UtcNow - Started).TotalSeconds,
                protocol = Protocol,
                missionActive = cur != null,
                missionScene = cur?.SceneName,
                peerCount = GameNetwork.NetworkPeersValid ? GameNetwork.NetworkPeerCount : 0,
                modules = mods
            };
        }

        // CommandLineFunctionality commands return their output. Console-manager commands
        // (multiplayer options etc.) only write to the server log via Debug.Print.
        private static string Exec(string command)
        {
            command = command.Trim();
            int sp = command.IndexOf(' ');
            string name = sp < 0 ? command : command.Substring(0, sp);
            string rest = sp < 0 ? "" : command.Substring(sp + 1).Trim();
            if (CommandLineFunctionality.HasFunctionForCommand(name))
                return CommandLineFunctionality.CallFunction(name, rest, out _);
            var mgr = typeof(Mission).Assembly.GetType("TaleWorlds.MountAndBlade.DedicatedServerConsoleCommandManager")
                      ?? Type.GetType("TaleWorlds.MountAndBlade.DedicatedServerConsoleCommandManager, TaleWorlds.MountAndBlade.Multiplayer")
                      ?? throw new InvalidOperationException("console command manager not available");
            mgr.GetMethod("HandleConsoleCommand", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { command });
            return "(command dispatched; its output is written to the server rgl log)";
        }

        private static T OnMain<T>(Func<T> f)
        {
            T result = default; Exception ex = null;
            var done = new ManualResetEventSlim();
            MainQueue.Enqueue(() => { try { result = f(); } catch (Exception e) { ex = e; } finally { done.Set(); } });
            if (!done.Wait(10000)) throw new TimeoutException("main thread did not service the request in 10s");
            if (ex != null) throw ex;
            return result;
        }

        private static string Ok(int? id, object result) => JsonSerializer.Serialize(new { id, ok = true, result });
        private static string Err(int? id, string code, string message) => JsonSerializer.Serialize(new { id, ok = false, error = new { code, message } });
    }

    internal static class Log
    {
        private static readonly object L = new object();
        private static readonly string File_ = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PE-dev", "logs", "testagent.log");
        public static void Debug(string m) => Write("DEBUG", m);
        public static void Info(string m) => Write("INFO", m);
        public static void Warn(string m) => Write("WARN", m);
        public static void Error(string m) => Write("ERROR", m);
        private static void Write(string level, string m)
        {
            try
            {
                lock (L)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File_));
                    System.IO.File.AppendAllText(File_, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + m + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
