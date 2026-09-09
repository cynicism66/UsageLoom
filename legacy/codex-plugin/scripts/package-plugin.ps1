[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$pluginRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $pluginRoot '../..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $workspace 'artifacts/legacy'}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$manifest=Get-Content -LiteralPath (Join-Path $pluginRoot '.codex-plugin/plugin.json') -Raw | ConvertFrom-Json
$archive=Join-Path $OutputDirectory "$($manifest.name)-$($manifest.version).zip"
if(Test-Path -LiteralPath $archive){throw 'Archive exists; choose another output directory'}
$stage=Join-Path $workspace ('artifacts/legacy-staging/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
# Explicit source allowlist; no user data, logs, fixtures or build caches.
$localFiles=@('.codex-plugin/plugin.json','.mcp.json','package.json','README.md',
    'assets/usage-dashboard.html','server/index.mjs','skills/usage-monitor/SKILL.md',
    'src/providers/codex/pricing.mjs','src/providers/codex/quota.mjs','src/providers/codex/usage.mjs',
    'scripts/smoke-test.mjs')
$sharedFiles=@('LICENSE','LICENSE.zh-CN.md','THIRD_PARTY_NOTICES.md','SECURITY.md',
    'docs/ARCHITECTURE.md','docs/PRIVACY.md','docs/PROJECT_ORIGINS.md','docs/RELEASING.md')
foreach($file in $localFiles+$sharedFiles){
    $source=Join-Path $(if($file -in $localFiles){$pluginRoot}else{$workspace}) $file
    $target=Join-Path $stage $file
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
& tar.exe -a -c -f ([IO.Path]::GetFullPath($archive)) -C $stage -- ($localFiles+$sharedFiles)
if($LASTEXITCODE -ne 0){throw "tar failed: $LASTEXITCODE"}
Write-Output "Created $archive"
Write-Output "Staging preserved at $stage"
