[CmdletBinding()]
param([string]$Version='0.7.0')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
$workspace=Split-Path $PSScriptRoot
$assets=@()
foreach($pair in @(@('installer','msi'),@('portable','zip'))){
    $name="UsageLoom-$Version-win-x64.$($pair[1])"
    $file=Get-Item -LiteralPath (Join-Path $workspace "artifacts/$($pair[0])/$name")
    $assets+=@{name=$name;size=$file.Length;digest='sha256:'+(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();browser_download_url="https://github.com/cynicism66/UsageLoom/releases/download/v$Version/$name"}
}
$directory=Join-Path $workspace "artifacts/release-$Version"
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$feed=@{tag_name="v$Version";draft=$false;prerelease=$false;body=[IO.File]::ReadAllText((Join-Path $workspace "docs/RELEASE-$Version.md"));assets=$assets}
[IO.File]::WriteAllText((Join-Path $directory 'update.json'),($feed|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
Write-Output "Update feed: $directory/update.json (upload alongside MSI and ZIP)"
