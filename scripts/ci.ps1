<#
Engine-free CI: runs dotnet format --verify-no-changes and dotnet test for every
tools/**/*.Tests.csproj whose project closure has no Bannerlord*/TaleWorlds reference.
Exit 0 = all pass, 1 = any failure. Works on PowerShell 5.1 and pwsh.
#>
param([string]$ResultsDir = 'TestResults')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Test-EngineFree([string]$proj, [hashtable]$seen) {
    $full = (Resolve-Path $proj).Path
    if ($seen.ContainsKey($full)) { return $true }
    $seen[$full] = $true
    $text = [IO.File]::ReadAllText($full) -replace '(?s)<!--.*?-->', ''
    if ($text -match 'Bannerlord|TaleWorlds') { return $false }
    $dir = Split-Path -Parent $full
    foreach ($m in [regex]::Matches($text, '<ProjectReference\s+Include="([^"]+)"')) {
        $ref = Join-Path $dir ($m.Groups[1].Value -replace '\\', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-EngineFree $ref $seen)) { return $false }
    }
    return $true
}

$projects = @(Get-ChildItem -Path (Join-Path $root 'tools') -Recurse -Filter '*.Tests.csproj' |
    Where-Object { Test-EngineFree $_.FullName @{} } | Sort-Object FullName)
if ($projects.Count -eq 0) { Write-Host 'No engine-free test projects found.'; exit 1 }

$results = @()
$resultsAbs = Join-Path $root $ResultsDir
foreach ($p in $projects) {
    $name = $p.BaseName
    Write-Host "=== $name"
    & dotnet format $p.FullName --verify-no-changes
    $fmt = $LASTEXITCODE -eq 0
    & dotnet test $p.FullName -c Release --logger 'trx' --results-directory (Join-Path $resultsAbs $name)
    $tst = $LASTEXITCODE -eq 0
    $results += [pscustomobject]@{ Project = $name; Format = $(if ($fmt) { 'ok' } else { 'FAIL' }); Test = $(if ($tst) { 'ok' } else { 'FAIL' }) }
}

Write-Host ''
Write-Host '=== Summary'
$results | Format-Table -AutoSize | Out-String | Write-Host
if (@($results | Where-Object { $_.Format -ne 'ok' -or $_.Test -ne 'ok' }).Count -gt 0) { exit 1 }
exit 0
