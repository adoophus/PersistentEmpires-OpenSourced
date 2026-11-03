using System.Text;

namespace PE.TestHost;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>Thread-safe timestamped logger: console (stderr in agent mode) plus size-rotated file.</summary>
public sealed class Logger
{
    public const string Engine = "engine", Net = "net", Test = "test";

    private readonly object _lock = new();
    private readonly TextWriter _console;
    private readonly string? _file;
    private readonly long _maxBytes;
    private readonly int _keep;

    public LogLevel Level { get; set; }

    public Logger(TextWriter console, LogLevel level, string? filePath, long maxBytes = 1_048_576, int keep = 5)
    {
        _console = console; Level = level; _file = filePath; _maxBytes = maxBytes; _keep = keep;
    }

    public static string DefaultFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PE-dev", "logs", "testhost.log");

    public static bool TryParseLevel(string s, out LogLevel l) => Enum.TryParse(s, true, out l) && Enum.IsDefined(l);

    public void Trace(string cat, string msg) => Write(LogLevel.Trace, cat, msg);
    public void Debug(string cat, string msg) => Write(LogLevel.Debug, cat, msg);
    public void Info(string cat, string msg) => Write(LogLevel.Info, cat, msg);
    public void Warn(string cat, string msg) => Write(LogLevel.Warn, cat, msg);
    public void Error(string cat, string msg) => Write(LogLevel.Error, cat, msg);

    public void Write(LogLevel level, string cat, string msg)
    {
        if (level < Level) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] [{cat}] {msg}";
        lock (_lock)
        {
            try { _console.WriteLine(line); } catch (IOException) { }
            if (_file == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                Rotate();
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* logging must never throw */ }
        }
    }

    private void Rotate()
    {
        if (!File.Exists(_file) || new FileInfo(_file).Length < _maxBytes) return;
        File.Delete($"{_file}.{_keep}");
        for (var i = _keep - 1; i >= 1; i--)
            if (File.Exists($"{_file}.{i}")) File.Move($"{_file}.{i}", $"{_file}.{i + 1}");
        File.Move(_file!, $"{_file}.1");
    }
}
