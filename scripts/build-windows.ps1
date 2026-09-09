[CmdletBinding()]
param([switch]$Publish, [switch]$Test, [switch]$NoRestore)
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
[string[]]$restoreArguments=@()
if($NoRestore){$restoreArguments+= '--no-restore'}
try {
    if ($Test) {
        & $dotnetPath run --project tests/UsageLoom.Core.Tests -c Release @restoreArguments
    } elseif ($Publish) {
        # A fresh output prevents removed NuGet components leaking into new packages.
        $staging=Join-Path $workspace ('artifacts/publish-staging-'+[guid]::NewGuid().ToString('N'))
        & $dotnetPath publish src/UsageLoom.App -c Release -o $staging @restoreArguments
        if($LASTEXITCODE -ne 0){throw "发布失败：$LASTEXITCODE"}
        & "$PSScriptRoot/test-publish-layout.ps1" -PublishDirectory $staging
        $destination=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/win-x64'))
        $backup=[IO.Path]::GetFullPath((Join-Path $workspace ('artifacts/publish-backup-'+[guid]::NewGuid().ToString('N'))))
        $boundary=[IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))+[IO.Path]::DirectorySeparatorChar
        foreach($path in @($staging,$destination,$backup)){if(![IO.Path]::GetFullPath($path).StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw 'Publish path outside artifacts'}}
        $running=Get-Process -Name UsageLoom.App -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($destination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}
        if($running){throw "发布目录中的程序仍在运行。新包保留在 $staging"}
        if(Test-Path -LiteralPath $destination){Move-Item -LiteralPath $destination -Destination $backup}
        try{Move-Item -LiteralPath $staging -Destination $destination}
        catch{if(!(Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)){Move-Item -LiteralPath $backup -Destination $destination};throw}
        Write-Output "Previous publish preserved at $backup"
    } else {
        & $dotnetPath build src/UsageLoom.App -c Debug @restoreArguments
    }
    if ($LASTEXITCODE -ne 0) { throw "构建或测试失败：$LASTEXITCODE" }
} finally {
    Pop-Location
    Stop-Transcript | Out-Null
    Write-Output "构建/测试日志：$transcript"
}
