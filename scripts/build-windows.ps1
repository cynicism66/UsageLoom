[CmdletBinding()]
param([switch]$Publish, [switch]$Test)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetPath = Join-Path $workspace '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path $dotnetPath
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$logDirectory = Join-Path $workspace 'artifacts/logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$operation = if ($Test) { 'test' } elseif ($Publish) { 'publish' } else { 'build' }
$transcript = Join-Path $logDirectory "$operation-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff').log"
Start-Transcript -LiteralPath $transcript -Force | Out-Null
Push-Location $workspace
try {
    if ($Test) {
        & $dotnetPath run --project tests/UsageLoom.Core.Tests -c Release
    } elseif ($Publish) {
        & $dotnetPath publish src/UsageLoom.App -c Release -o artifacts/win-x64
    } else {
        & $dotnetPath build src/UsageLoom.App -c Debug
    }
    if ($LASTEXITCODE -ne 0) { throw "构建或测试失败：$LASTEXITCODE" }
} finally {
    Pop-Location
    Stop-Transcript | Out-Null
    Write-Output "构建/测试日志：$transcript"
}
