<#
.SYNOPSIS
  Builds PE.TestHost and runs the PE.TestAgent test suite through it.
.EXAMPLE
  scripts\test.ps1 Economy -Json
.NOTES
  Exit 0 = all passed, 1 = a test failed, 2 = infra/setup error (no agent, timeout, not_implemented, bad args).
  Agent endpoint: 127.0.0.1:$env:PE_TESTAGENT_PORT (default 7900).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Filter = '',
    [switch]$Json,
    [int]$Timeout = 60
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'tools\PE.TestHost\PE.TestHost.csproj'

# Build quietly so stdout stays clean for -Json consumers.
& dotnet build $proj -nologo -v q -c Debug 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine('PE.TestHost build failed'); exit 2 }

$runArgs = @('--project', $proj, '--no-build', '--', '--run', ("test $Filter").Trim(), '--timeout', $Timeout)
if ($Json) { $runArgs += '--json' }
& dotnet run @runArgs
exit $LASTEXITCODE
