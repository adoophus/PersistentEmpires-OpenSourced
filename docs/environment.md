# Environment

## Installs (owner's machine)
| What | Path | Notes |
|---|---|---|
| Game client v1.4.8 | `D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord` | DLLs in `bin\Win64_Shipping_Client`, module DLLs in `Modules\<Mod>\bin\Win64_Shipping_Client` |
| Dedicated server (Steam app **1863440**, build 24571419) | `D:\bannerlord-headless\server` | DLLs in `bin\Win64_Shipping_Server` |
| steamcmd | `D:\bannerlord-headless\steamcmd\steamcmd.exe` | Anonymous login works for 1863440. Exit code 7 after a self-update is harmless; check `StateFlags 4` in the appmanifest |
| ilspycmd | global dotnet tool, v11 | Example: `ilspycmd -t TaleWorlds.MountAndBlade.GameNetwork <dll>` |
| .NET SDK | 10.0.102 | The game ships its own runtimes (see below) |

No Python on this machine. Use PowerShell or Node for scripting.

## Runtimes (confirmed from the assemblies)
- TaleWorlds managed DLLs target **.NET Standard 2.0**.
- The client host `Bannerlord.exe` is **net472**.
- The server: native `DedicatedCustomServer.Starter.exe` → `Rgl.dll`, which embeds **CoreCLR .NET 6** from its private `Microsoft.NETCore.App` folder. No global .NET 6 is needed.
- Repo projects: Lib, Client and Save target net472. Server, Harmony and API target net6.0.

## Server auth token
- The dedicated server must log in to the TaleWorlds lobby. Without a token it shuts down 20 s after boot ("Disconnected from custom battle server manager").
- The token lives at `%USERPROFILE%\Documents\Mount and Blade II Bannerlord\Tokens\DedicatedCustomServerAuthToken.txt`. The owner supplies it; agents only ever check that it **exists**.
- If boots start failing at `Data/Login` again, the token has probably expired. Ask the owner for a new one. Never try to obtain one yourself.
- Because of the token, the server (Tier B) runs locally only. CI can't run it.

## Booting the server manually
```powershell
cd D:\bannerlord-headless\server\bin\Win64_Shipping_Server
.\DedicatedCustomServer.Starter.exe /no_watchdog /dedicatedcustomserver 7210 USER 0 /port 7210 /DisableErrorReporting _MODULES_*Native*Multiplayer*_MODULES_
```
- stdout is empty. Logs go to `C:\ProgramData\Mount and Blade II Bannerlord\logs\rgl_log_<pid>.txt` (errors in `rgl_log_errors_<pid>.txt`).
- Measured: module data loads by about 3 s, and the lobby login happens at about 4 s.
- When grepping logs, redact long base64 strings.
