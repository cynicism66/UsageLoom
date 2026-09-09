[CmdletBinding()]
param([string]$Version='0.6.7')
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish=Join-Path $workspace 'artifacts/win-x64'
$exe=Join-Path $publish 'UsageLoom.App.exe'
if(!(Test-Path -LiteralPath $exe)){throw '请先运行 build-windows.ps1 -Publish'}
if(((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -split '\+')[0] -ne $Version){throw '发布目录版本不一致'}
foreach($name in @('App.xbf','UsageLoom.App.pri','coreclr.dll','hostfxr.dll','Microsoft.UI.Xaml.dll')){
    if(!(Test-Path -LiteralPath (Join-Path $publish $name))){throw "发布目录缺少 $name"}
}
$out=Join-Path $workspace 'artifacts/portable';New-Item -ItemType Directory -Force $out | Out-Null
$zip=Join-Path $out "UsageLoom-$Version-win-x64.zip"
if(Test-Path -LiteralPath $zip){throw '目标 ZIP 已存在，拒绝静默覆盖'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($file in Get-ChildItem -LiteralPath $publish -Recurse -File){
        # Only omit symbols. Keep every runtime, PRI/XBF and language resource.
        if($file.Extension -eq '.pdb'){continue}
        $relative=[IO.Path]::GetRelativePath($publish,$file.FullName).Replace('\','/')
        $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
    }
    foreach($doc in @('docs/PORTABLE.md')){
        $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $workspace $doc),[IO.Path]::GetFileName($doc))
    }
}finally{$archive.Dispose()}
$check=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    foreach($file in Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object Extension -ne '.pdb'){
        $relative=[IO.Path]::GetRelativePath($publish,$file.FullName).Replace('\','/')
        $entry=$check.GetEntry($relative)
        if(!$entry -or $entry.Length -ne $file.Length){throw "ZIP 文件清单不一致：$relative"}
    }
    Write-Output "ZIP 文件清单检查通过：$($check.Entries.Count) 项"
}finally{$check.Dispose()}
Get-FileHash -LiteralPath $zip -Algorithm SHA256
