[CmdletBinding()]
param([string]$PublishDirectory,[switch]$Portable)
$ErrorActionPreference='Stop'
if(!$PublishDirectory){$PublishDirectory=Join-Path $PSScriptRoot '../artifacts/win-x64'}
$root=(Resolve-Path -LiteralPath $PublishDirectory).Path
foreach($name in @('UsageLoom.App.exe','UsageLoom.App.dll','UsageLoom.App.pri','App.xbf','SolidDropDownResources.xbf','Microsoft.UI.Xaml.dll','coreclr.dll','hostfxr.dll','e_sqlite3.dll','ZstdSharp.dll','THIRD-PARTY-LICENSES/ZstdSharp-LICENSE.txt','THIRD-PARTY-LICENSES/Zstandard-LICENSE.txt','Update-UsageLoom.ps1','update-manifest.txt')){
    if(!(Test-Path -LiteralPath (Join-Path $root $name))){throw "Missing required payload: $name"}
}
$files=@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Extension -ne '.pdb')
$unused=@($files | Where-Object Name -Match '^(DirectML|onnxruntime.*|Microsoft\.ML\.OnnxRuntime|Microsoft\.Windows\.(AI|Widgets|Workloads).*)\.(dll|winmd)$')
if($unused.Count){throw ('Unused SDK payload detected; publish into a clean directory: '+($unused.Name -join ', '))}
$actual=@($files | ForEach-Object {[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/')} | Where-Object {!$Portable -or $_ -ne 'PORTABLE.md'} | Sort-Object -Unique)
if($Portable -and !(Test-Path -LiteralPath (Join-Path $root 'PORTABLE.md'))){throw 'Missing portable instructions'}
$manifest=@(Get-Content -LiteralPath (Join-Path $root 'update-manifest.txt') | Where-Object {$_} | ForEach-Object {$_.Replace('\','/')} | Sort-Object -Unique)
if(Compare-Object $actual $manifest){throw 'Update ownership manifest differs from publish payload'}
Write-Output "PASS modular self-contained payload: $($files.Count) files, $((($files|Measure-Object Length -Sum).Sum)) bytes"
