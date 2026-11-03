<#
.SYNOPSIS
  Build/deploy PE.TestAgent and start|stop|restart|status the headless dedicated server.
.DESCRIPTION
  Exit 0 = ok, 2 = error. Only tests that the auth token file EXISTS; never reads it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('start', 'stop', 'restart', 'status')][string]$Action,
    [int]$Port = 7900,
    [int]$TimeoutSec = 60
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$devDir = Join-Path $env:LOCALAPPDATA 'PE-dev'
$pidFile = Join-Path $devDir 'server.pid'
$gamePort = 7210

function Fail([string]$m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 2 }

function Get-ServerPath {
    $f = Join-Path $repo 'Directory.Build.props.user'
    if (-not (Test-Path -LiteralPath $f)) { Fail 'Directory.Build.props.user missing; run scripts\setup.ps1' }
    $m = [regex]::Match((Get-Content -LiteralPath $f -Raw), '<BannerlordServerPath>([^<]*)</BannerlordServerPath>')
    if (-not $m.Success) { Fail 'BannerlordServerPath not set in Directory.Build.props.user' }
    [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
}

function Send-Agent([string]$json, [int]$timeoutMs = 3000) {
    # Returns the response line after the hello event, or $null on any failure.
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($timeoutMs) -or -not $c.Connected) { return $null }
        $c.EndConnect($iar)
        $s = $c.GetStream(); $s.ReadTimeout = $timeoutMs
        $enc = New-Object System.Text.UTF8Encoding($false)
        $r = New-Object System.IO.StreamReader($s, $enc)
        $w = New-Object System.IO.StreamWriter($s, $enc); $w.NewLine = "`n"; $w.AutoFlush = $true
        $null = $r.ReadLine()   # hello
        $w.WriteLine($json)
        return $r.ReadLine()
    } catch { return $null } finally { $c.Close() }
}
function Test-Ping { $r = Send-Agent '{"id":1,"cmd":"ping"}'; [bool]($r -and $r -match '"pong":true') }

function Get-ServerProc {
    if (-not (Test-Path -LiteralPath $pidFile)) { return $null }
    $p = [int](Get-Content -LiteralPath $pidFile -Raw).Trim()
    Get-Process -Id $p -ErrorAction SilentlyContinue
}

function Show-LogTail([int]$pidv) {
    $dir = Join-Path $env:ProgramData 'Mount and Blade II Bannerlord\logs'
    $f = Join-Path $dir "rgl_log_$pidv.txt"
    if (-not (Test-Path -LiteralPath $f)) { $f = (Get-ChildItem $dir -Filter 'rgl_log_*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
    if (-not $f) { Write-Host '(no rgl log found)'; return }
    Write-Host "--- last 20 relevant lines of $f"
    Get-Content -LiteralPath $f | Where-Object { $_ -match 'PETestAgent|PE\.TestAgent|error|exception|fail|module' } | Select-Object -Last 20 |
        ForEach-Object { $_ -replace '[A-Za-z0-9+/=_-]{60,}', '<redacted>' }
}

function Do-Stop {
    $p = Get-ServerProc
    if (-not $p) { Write-Host 'server not running'; Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue; return }
    $null = Send-Agent '{"id":1,"cmd":"shutdown"}'
    if (-not $p.WaitForExit(15000)) { Write-Host 'graceful shutdown timed out; killing'; Stop-Process -Id $p.Id -Force; $null = $p.WaitForExit(5000) }
    Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
    Write-Host "stopped pid $($p.Id)"
}

function Do-Start {
    $server = Get-ServerPath
    $token = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Tokens\DedicatedCustomServerAuthToken.txt'
    if (-not (Test-Path -LiteralPath $token)) { Fail "auth token file missing: $token" }
    if (Get-ServerProc) { Fail 'server already running (use restart)' }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet build (Join-Path $repo 'tools\PE.TestAgent\PE.TestAgent.csproj') -c Release -nologo -v q -p:BannerlordServerPath="$server" | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'PE.TestAgent build failed' }
    $mod = Join-Path $server 'Modules\PETestAgent'
    $dst = Join-Path $mod 'bin\Win64_Shipping_Server'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $repo 'tools\PE.TestAgent\Module\SubModule.xml') $mod -Force
    Copy-Item (Join-Path $repo 'publish\PE.TestAgent\bin\PE.TestAgent.dll') $dst -Force

    $bin = Join-Path $server 'bin\Win64_Shipping_Server'
    $env:PE_TESTAGENT_PORT = "$Port"
    $args = "/no_watchdog /dedicatedcustomserver $gamePort USER 0 /port $gamePort /DisableErrorReporting _MODULES_*Native*Multiplayer*PETestAgent*_MODULES_"
    $sw.Restart()
    $proc = Start-Process -FilePath (Join-Path $bin 'DedicatedCustomServer.Starter.exe') -ArgumentList $args -WorkingDirectory $bin -WindowStyle Hidden -PassThru
    New-Item -ItemType Directory -Force -Path $devDir | Out-Null
    Set-Content -LiteralPath $pidFile -Value $proc.Id

    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if ($proc.HasExited) { Write-Host "server exited early (code $($proc.ExitCode))"; break }
        if (Test-Ping) { Write-Host ("ping ok after {0:N1}s (pid {1}, port {2})" -f $sw.Elapsed.TotalSeconds, $proc.Id, $Port); return }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "ERROR: no ping within ${TimeoutSec}s" -ForegroundColor Red
    Show-LogTail $proc.Id
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
    exit 2
}

switch ($Action) {
    'start'   { Do-Start }
    'stop'    { Do-Stop }
    'restart' { Do-Stop; Do-Start }
    'status'  {
        $p = Get-ServerProc
        if ($p) { Write-Host "running pid=$($p.Id) ping=$(if (Test-Ping) { 'ok' } else { 'no' })" } else { Write-Host 'not running' }
    }
}
exit 0
