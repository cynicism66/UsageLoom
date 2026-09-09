[CmdletBinding()]
param([string]$PublishDirectory)
$ErrorActionPreference='Stop'
if(!$PublishDirectory){$PublishDirectory=Join-Path $PSScriptRoot '../artifacts/win-x64'}
$root=(Resolve-Path -LiteralPath $PublishDirectory).Path
foreach($name in @('UsageLoom.App.exe','UsageLoom.App.dll','UsageLoom.App.pri','App.xbf','Microsoft.UI.Xaml.dll','coreclr.dll','hostfxr.dll','e_sqlite3.dll','Update-UsageLoom.ps1','update-manifest.txt')){
    if(!(Test-Path -LiteralPath (Join-Path $root $name))){throw "Missing required payload: $name"}
}
$files=@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Extension -ne '.pdb')
$unused=@($files | Where-Object Name -Match '^(DirectML|onnxruntime.*|Microsoft\.ML\.OnnxRuntime|Microsoft\.Windows\.(AI|Widgets|Workloads).*)\.(dll|winmd)$')
if($unused.Count){throw ('Unused SDK payload detected; publish into a clean directory: '+($unused.Name -join ', '))}
$actual=@($files | ForEach-Object {[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/')} | Sort-Object -Unique)
$manifest=@(Get-Content -LiteralPath (Join-Path $root 'update-manifest.txt') | Where-Object {$_} | ForEach-Object {$_.Replace('\','/')} | Sort-Object -Unique)
if(Compare-Object $actual $manifest){throw 'Update ownership manifest differs from publish payload'}
Write-Output "PASS modular self-contained payload: $($files.Count) files, $((($files|Measure-Object Length -Sum).Sum)) bytes"
