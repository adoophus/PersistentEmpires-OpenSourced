# Roadmap

Live status is in pinned issue #2. This file holds the plan and the definition of done.

## Phase 0: recon and feasibility (done)
The decision is in `docs/headless-feasibility.md` and #1:
- **Adopt:** Tier A (pure-logic unit tests) and Tier B (in-process server harness).
- **Defer:** Tier C (real-client automation).
- **Reject:** Tier D (protocol fake client).
- The owner chose **SQLite** for persistence tests and **port to v1.4.8 first**.

## Phase 1: harness
Ordered by dependency:
1. **Build plumbing.** Game-path MSBuild properties, `scripts/setup.ps1`, `scripts/build.ps1` (PR #31).
2. **v1.4.8 compile port.**
   - Lib and Harmony first, one PR per root cause (`game-update` issues #32–#43).
   - Then the downstream projects (#44).
3. **Tier A.** Pull engine-free logic into `PersistentEmpires.Core` (netstandard2.0, no TaleWorlds refs), add xunit tests and a GitHub Actions CI (Tier A tests, engine-free builds, format check). Target: full suite < 10 s.
4. **Tier B.** Can start alongside 2, because it doesn't need the mod to compile.
   - **`PE.TestAgent`:** a server SubModule. It runs a localhost TCP control channel (line-delimited JSON) and executes tests on the main thread.
   - **`tools/PE.TestHost`:**
     - REPL commands: `connect <ip:port>`, `disconnect`, `status`, `exec`, `test [filter]`, `quit`. `eval <C#>` only if Roslyn works on the server's .NET 6; otherwise document why not.
     - Agent mode: `PE.TestHost --run "test net.*" --json --timeout 60`. Exit codes: 0 pass, 1 test failure, 2 infra error. JSON only, no ANSI.
   - **Test framework:** attribute-discovered tests, filters, per-test timeouts, setup/teardown, JSON and human reporters.
   - **Logging:** thread-safe, timestamped, levels plus categories (`engine`, `net`, `test`). Goes to the console and a rotating file, controlled by `--log-level`.
   - **Scripts:** `scripts/server.ps1 start|stop|restart`, `scripts/test.ps1 [filter]`.
5. **First suites:**
   - round-trip serialization of every PE network message (in-process, using the native writer and reader);
   - connect/handshake;
   - entity sync (spawn → move → despawn, as seen in the replication state for a second peer);
   - persistence round-trip (SQLite).
6. **Finish.** `CLAUDE.md` documents the full loop. The final PR reports **measured** loop times.

## Definition of done (Phase 1)
From a clean clone plus the local game installs, these all succeed, and their measured runtimes are in the final PR:
- `scripts/setup.ps1`
- `scripts/build.ps1`
- `scripts/server.ps1 start`
- `PE.TestHost --run "test *" --json`

Every known bug is a filed issue.
