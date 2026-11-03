# PE.TestHost

Engine-free .NET 10 console client for the **PE.TestAgent** control channel (protocol v1: TCP, UTF-8, one JSON
object per line). It references no TaleWorlds assembly and builds in CI.

Default endpoint: `127.0.0.1:%PE_TESTAGENT_PORT%` (default `7900`).

## REPL

```
dotnet run --project tools/PE.TestHost
```

Commands: `connect <ip:port>`, `disconnect`, `status`, `ping`, `exec <text>`, `test [filter]`, `help`, `quit`.
The REPL tries the default endpoint on start.

## Agent mode

```
PE.TestHost --run "<repl command line>" [--json] [--timeout 60] [--connect ip:port] [--log-level debug|info|warn|error] [--log-file <path>|none]
```

Auto-connects, runs one command, exits. `--timeout` (seconds) covers connect plus the command. Logs go to stderr
and the log file, never stdout. Wrapper: `scripts\test.ps1 [filter] [-Json]`.

### `--json` output

stdout carries exactly one JSON object, no ANSI:

```json
{"command":"test Foo","ok":false,"exitCode":1,"durationMs":42,"result":{...},"error":{"code":"tests_failed","message":"1 test(s) failed"}}
```

`result` is the agent's `result` object (null on failure); `error` is `{code,message}` or null. For `test`, `result` is
`{"summary":{"passed","failed","skipped","durationMs"},"tests":[{"name","status","durationMs","message"}]}`.
Unsolicited `{"event":...}` lines (e.g. `test.started`) are logged to stderr, not treated as responses.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | success / all tests passed |
| 1 | at least one test failed (`error.code = tests_failed`) |
| 2 | infra/setup error: `connect_failed`, `timeout`, `not_implemented`, `connection_closed`, `bad_args`, any other agent error |

## Logging

Timestamped, thread-safe; levels trace/debug/info/warn/error; categories `engine`, `net`, `test`. Console (stderr in
agent mode) plus `%LOCALAPPDATA%\PE-dev\logs\testhost.log`, rotated at 1 MB keeping 5 files (`.1` to `.5`).

## Why there is no `eval <C#>`

Evaluating C# would have to run inside the server process, which is .NET 6 and ships Roslyn 2.8; a client-side
evaluator cannot touch live engine objects. It is deferred to a TestAgent-side spike.
