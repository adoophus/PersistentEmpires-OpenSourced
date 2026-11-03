# PE.TestAgent wire protocol v1

Transport: TCP on `127.0.0.1`, port from env `PE_TESTAGENT_PORT` (default 7900). UTF-8, one JSON object per line (`\n`).
Sequential clients are supported; concurrent clients also work.

On connect the agent sends:

    {"event":"hello","data":{"agent":"PE.TestAgent","agentVersion":"0.1.0","protocol":1}}

Request:  `{"id":<int>,"cmd":"<name>","args":{...}}`
Success:  `{"id":<int>,"ok":true,"result":{...}}`
Failure:  `{"id":<int|null>,"ok":false,"error":{"code":"<snake_case>","message":"..."}}`
Events:   `{"event":"<name>","data":{...}}` (unsolicited; only `hello` exists in v1)

| cmd | args | result / errors |
|---|---|---|
| `ping` | none | `{"pong":true}` |
| `status` | none | `{"uptimeSec":n,"protocol":1,"missionActive":bool,"missionScene":string\|null,"peerCount":n,"modules":[ids]}` |
| `exec` | `{"command":"..."}` | `{"output":"..."}`. Commands registered via `CommandLineFunctionality` return their real output. Other dedicated-console commands (multiplayer options, `list`, ...) are dispatched via `DedicatedServerConsoleCommandManager.HandleConsoleCommand`, which only writes to the rgl log, so `output` is a fixed note. Missing `command` gives `bad_request`. |
| `test` | any | error `not_implemented` |
| `shutdown` | none | `{}` then the server quits |

Error codes: `bad_request` (malformed JSON or bad shape; `id` is null), `unknown_command`, `not_implemented`, `internal_error`.
All game-state access runs on the game main thread (queue drained in `OnApplicationTick`); a request that the main thread cannot service within 10 s yields `internal_error`.
