[CmdletBinding()]
param([string]$Version='0.6.9')
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$localRuntime=Join-Path $workspace '.tools/dotnet'
if(Test-Path (Join-Path $localRuntime 'dotnet.exe')){$env:DOTNET_ROOT=$localRuntime}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_HOME=Join-Path $workspace '.tools/cli-home'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw '版本号必须为 major.minor.patch'}
$wix=Join-Path $workspace '.tools/wix/wix.exe'
if(!(Test-Path $wix)){throw 'WiX 未安装，请先运行 dotnet tool install wix --tool-path .tools/wix --version 6.0.2'}
$publish=Join-Path $workspace 'artifacts/win-x64'
if(!(Test-Path (Join-Path $publish 'UsageLoom.App.exe'))){throw '请先运行 build-windows.ps1 -Publish'}
foreach($required in @('UsageLoom.App.pri','App.xbf','Microsoft.UI.Xaml.dll','coreclr.dll','hostfxr.dll')){
    if(!(Test-Path -LiteralPath (Join-Path $publish $required))){throw "自包含发布目录缺少 $required，拒绝生成不完整 MSI"}
}
$appProject=[xml](Get-Content (Join-Path $workspace 'src/UsageLoom.App/UsageLoom.App.csproj') -Raw)
$expectedFileVersion=[string]$appProject.Project.PropertyGroup.FileVersion
if([string]$appProject.Project.PropertyGroup.Version -ne $Version){throw 'MSI 与项目产品版本不匹配'}
foreach($binary in @('UsageLoom.App.exe','UsageLoom.App.dll')){
    $actualVersion=(Get-Item (Join-Path $publish $binary)).VersionInfo.FileVersion
    if($actualVersion -ne $expectedFileVersion -or [version]$actualVersion -le [version]'1.0.0.0'){throw "$binary 文件版本 $actualVersion 不符合递增规则，请重新发布"}
}
$out=Join-Path $workspace 'artifacts/installer';New-Item -ItemType Directory -Force $out|Out-Null
$obj=Join-Path $out 'obj';New-Item -ItemType Directory -Force $obj|Out-Null
$msi=Join-Path $out "UsageLoom-$Version-win-x64.msi"
Push-Location $workspace
try {
    & $wix build (Join-Path $workspace 'packaging/UsageLoom.wxs') -ext WixToolset.UI.wixext/6.0.2 -culture zh-CN -d PublishDir=$publish -d Version=$Version -d "LicenseRtf=$(Join-Path $workspace 'packaging/License.rtf')" -o $msi -arch x64
} finally { Pop-Location }
if($LASTEXITCODE -ne 0){throw "WiX 构建失败：$LASTEXITCODE"}
Write-Output 'MSI 已收获自包含发布目录中的全部文件。尚未执行安装，不代表干净系统验收通过。'
Get-FileHash $msi -Algorithm SHA256 | Format-List
Write-Output "MSI：$msi"
