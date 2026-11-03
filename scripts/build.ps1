<#
.SYNOPSIS
  Builds the client and server solutions and prints a per-project error/warning summary.
.EXAMPLE
  scripts\build.ps1 -Configuration Release -Project PersistentEmpiresAPI
.NOTES
  Exit 0 = build succeeded, 1 = build failed. Run scripts\setup.ps1 first.
  Full compiler output is saved under %TEMP%\pe-build.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$Project
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $repo 'Directory.Build.props.user'))) {
    Write-Host "Directory.Build.props.user missing - run scripts\setup.ps1 first." -ForegroundColor Red; exit 1
}
$logDir = Join-Path ([IO.Path]::GetTempPath()) 'pe-build'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$solutions = @(
    @{ Name = 'Client'; Path = Join-Path $repo 'PersistentEmpiresClient\PersistentEmpiresClient.sln' },
    @{ Name = 'Server'; Path = Join-Path $repo 'PersistentEmpiresServer\PersistentEmpiresServer.sln' }
)
$rows = @(); $failed = $false; $found = -not $Project
foreach ($s in $solutions) {
    $msargs = @('msbuild', $s.Path, '-restore', '-nologo', '-v:q', '-clp:NoSummary', "-p:Configuration=$Configuration")
    if ($Project) {
        if (-not (Select-String -LiteralPath $s.Path -Pattern ('"' + [regex]::Escape($Project) + '"') -Quiet)) { continue }
        $found = $true; $msargs += "-t:$Project"
    }
    $log = Join-Path $logDir ("msbuild-$($s.Name).log")
    & dotnet @msargs 2>&1 | Out-File -LiteralPath $log -Encoding utf8
    if ($LASTEXITCODE -ne 0) { $failed = $true }
    $diag = Get-Content -LiteralPath $log | Where-Object { $_ -match '(error|warning) [A-Za-z]+\d+:' } | Sort-Object -Unique
    foreach ($d in $diag) {
        $proj = if ($d -match '\[([^\]]+\.csproj)') { [IO.Path]::GetFileNameWithoutExtension($Matches[1]) } else { '(solution)' }
        $kind = if ($d -match ': error ') { 'E' } else { 'W' }
        $code = if ($d -match '(?:error|warning) ([A-Za-z]+\d+):') { $Matches[1] } else { '?' }
        $rows += [pscustomobject]@{ Solution = $s.Name; Project = $proj; Kind = $kind; Code = $code; Line = $d }
    }
}
if (-not $found) { Write-Host "Project '$Project' not found in either solution." -ForegroundColor Red; exit 1 }
Write-Host ""
Write-Host "Build summary ($Configuration)"
$rows | Group-Object Solution, Project | ForEach-Object {
    '{0,-8} {1,-28} errors={2,-5} warnings={3}' -f $_.Group[0].Solution, $_.Group[0].Project,
        @($_.Group | Where-Object Kind -eq 'E').Count, @($_.Group | Where-Object Kind -eq 'W').Count
}
$unres = @($rows | Where-Object { $_.Code -in 'MSB3245', 'MSB3243', 'CS0006' })
Write-Host ("Unresolved-reference diagnostics (MSB3245/MSB3243/CS0006): {0}" -f $unres.Count)
$unres | Select-Object -First 5 | ForEach-Object { Write-Host "  $($_.Line)" }
Write-Host "Logs: $logDir"
if ($failed) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }
Write-Host "BUILD SUCCEEDED" -ForegroundColor Green
exit 0
