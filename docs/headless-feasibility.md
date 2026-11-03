# Headless test harness: feasibility (Phase 0)

Status: **proposal, awaiting approval**. Date: 2026-11-03.
Installs examined: client v1.4.8 (Steam), dedicated server app **1863440** build 24571419 (steamcmd, anonymous login works).
Citations use `Assembly.dll!Type.Member` and come from `ilspycmd` decompiles of the installed DLLs.

## 1. Facts established

| Question | Finding | Evidence |
|---|---|---|
| Engine assembly runtime | All managed TaleWorlds DLLs target **.NET Standard 2.0** | TargetFramework attribute on `TaleWorlds.Core.dll`, `TaleWorlds.MountAndBlade.dll` |
| Client host | `Bannerlord.exe` is .NET Framework **4.7.2** | exe metadata |
| Server host | Native `DedicatedCustomServer.Starter.exe` → `Rgl.dll` embeds **CoreCLR (.NET 6)**, bundled privately in `bin\Win64_Shipping_Server\Microsoft.NETCore.App` | `Rgl.dll!WotsMainNativeCoreCLR`, `TaleWorlds.DotNet.dll!Controller.SetEngineMethodsAsHostedDotNetCore` |
| Packet serialization | **Native.** Every `GameNetworkMessage.Write*ToPacket/Read*FromPacket` calls `MBAPI.IMBNetwork`. Only the compression ranges are managed. UDP transport, reliability and handshake are all native. | `TaleWorlds.MountAndBlade.dll!GameNetworkMessage`, `!MBAPI.IMBNetwork` |
| Client join path | `LobbyClient.RequestJoinCustomGame` → `LobbyGameStateCustomGameClient.StartMultiplayer` → `GameNetwork.StartMultiplayerOnClient` → native `IMBNetwork.InitializeClientSide`. Needs a lobby login (Steam). | `TaleWorlds.MountAndBlade.Diamond.dll`, `TaleWorlds.MountAndBlade.Multiplayer.dll` |
| Headless server | No "headless" flag is needed: the server is already a graphics-less engine build. The client **cannot** reuse this path (`GameNetwork.IsDedicatedServer` is constant `false` on the client, and the client uses a different native lib). | `TaleWorlds.MountAndBlade.dll!GameNetwork.IsDedicatedServer` |
| Lobby dependency | The server logs into the TaleWorlds lobby with a token from `/dedicatedcustomserverauthtoken` or `Tokens\DedicatedCustomServerAuthToken.txt`. | `DedicatedCustomServerIntermissionManagerHandler` ctor, `DedicatedCustomGameServer.Connect` |
| Mod code in server | SubModules load normally. PE already Harmony-patches `GameNetwork.AddNewPlayerOnServer`. A console command system exists. | `DedicatedServerConsoleCommandManager`, `[CommandLineArgumentFunction]` |
| Bot peers | The shipped bot branch of `GameNetwork.AddNewPlayerOnServer` null-derefs. A synthetic peer is possible via `GameNetwork.HandleNewClientConnect(PlayerConnectionInfo, bool)` or a Harmony patch, but a native peer slot is always allocated. AI agents can be spawned and driven: `Mission.SpawnAgent`, `Agent.MovementInputVector`, `Agent.SetScriptedPosition`. | `TaleWorlds.MountAndBlade.dll!GameNetwork`, `!Agent` |
| Roslyn | The server ships Roslyn 2.8.0. A newer `Microsoft.CodeAnalysis.CSharp.Scripting` would have to be bundled in the PE test module (unverified). | `bin\Win64_Shipping_Server` |
| Persistence | MySQL via Dapper/MySqlConnector/FluentMigrator (`PersistentEmpiresSave`). | repo audit |

### Measured (single run, this machine)
```
DedicatedCustomServer.Starter.exe /no_watchdog /dedicatedcustomserver 7210 USER 0 /port 7210 _MODULES_*Native*Multiplayer*_MODULES_
  t≈3s   Native + Multiplayer ModuleData loaded
  t≈4s   POST https://game-even.bannerlord-services-3.net/Data/Login -> NoAuthenticationToken
  t≈24s  process exits ("Shutting down in 20 seconds ... Disconnected from custom battle server manager")
```
The engine boots fast, which makes Tier B's < 60 s target realistic. **Without a token the server lives for 20 s.**

## 2. Repo state against v1.4.8
- `SubModule.xml` depends on Native **e1.8.1**. The mod is v1.3.1.2.
- About 115 compile errors each in Lib and Client, and 123 in Server. The main causes are API drift in `UsableMissionObject`/`UsableMachine`/`ITargetable` (`WeakGameEntity`), `AgentStatCalculateModel` (new abstract members), and removed `ActionIndexValueCache`, `Agent.GetWieldedItemIndex` and `Mission.Missiles`.
- References are relative HintPaths into empty `ClientReferences/` and `ServerReferences/` folders.
- The engine-free logic is entangled with TaleWorlds types inside `PersistentEmpiresLib`.

## 3. Decision: tiers, ranked by iteration speed

### Tier A, pure-logic tests: **ADOPT**
- Extract economy, crafting, faction and persistence rules into `PersistentEmpires.Core` (netstandard2.0, **no TaleWorlds reference**), behind interfaces such as `IClock`, `IInventory` and `IRepository`.
- Tested with xunit on the installed .NET 10 SDK. It runs in CI with no game binaries.
- Target: full suite < 10 s.
- Cost: refactoring is incremental. It starts with whatever we touch while fixing compile errors.

### Tier B, in-process server harness: **ADOPT (primary)**
- `PE.TestAgent`: a server-only SubModule loaded next to PE. It opens a localhost TCP control channel (line-delimited JSON) and runs `[PeTest]` tests **inside the server process** on the main thread (queued to `OnApplicationTick`).
- `tools/PE.TestHost`: an ordinary .NET console app. It starts and stops the server, connects to the control channel (`connect <ip:port>` targets this channel, not the game port), streams results and maps them to exit codes. Its runtime is independent of the engine.
- Peers: synthetic `NetworkCommunicator`s via `HandleNewClientConnect` or a Harmony patch, and AI agents via `Mission.SpawnAgent`, which can be moved and despawned.
  - The "second peer sees entity" test asserts on server-side replication state (what would be sent to each peer), not on a real client.
- Serialization round-trip: real packet bytes are native, so the round-trip runs in-process with the server's own `GameNetworkMessage` writer and reader (write → loopback → read). It cannot run in Tier A.
- Persistence round-trip needs MySQL locally. `setup.ps1` will check for or install it (open question below).
- **Blocker, the lobby token. Two options to spike, in this order:**
  1. **Token (yours):** you generate a dedicated-server auth token in the game client and save it to `Tokens\DedicatedCustomServerAuthToken.txt`. Simple and supported, but token lifetime is unverified and it can't be used in CI.
  2. **Offline mode:** `PE.TestAgent` Harmony-patches the lobby connect/shutdown path so the server never contacts TaleWorlds. It only changes the local test process, never shipped PE code. Feasibility is unverified: our module must load before the login, and the log shows module data loading before the login POST, which is promising.

### Tier C, real-client automation: **DEFER**
- Needs Steam login, the lobby join and a full renderer.
- Only worth it for client-only code (UI, client handlers). Revisit once B is green.

### Tier D, protocol fake client: **REJECT**
- Bit packing, the UDP reliability layer and the handshake live in `TaleWorlds.Native.dll`/`Rgl.dll`. There is no managed encoder to reuse.
- Reproducing them means native reverse engineering, and we'd still need the lobby auth for the real flow.

### `eval <C#>`
Optional and gated on a spike: can Roslyn 4.x scripting be loaded in the server's CoreCLR 6? If not, it gets dropped and documented.

## 4. Open questions for approval
1. Approve the tiers **A + B now, C deferred, D rejected**?
2. Token path: will you generate a dedicated-server token? And may I spike the offline Harmony bypass for local tests only?
3. MySQL: OK to require a local MySQL 8 (installed via winget, or Docker if you have it), or would you rather abstract persistence and use SQLite for tests?
4. Get the mod compiling against v1.4.8 first (prerequisite for B), as tracked `game-update` issues and small PRs?
