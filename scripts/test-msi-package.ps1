[CmdletBinding()]
param([string]$Version)
. "$PSScriptRoot/common/Get-ReleaseVersion.ps1"
$Version=Get-ReleaseVersion $Version
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$installer.OpenDatabase((Join-Path $workspace "artifacts/installer/UsageLoom-$Version-win-x64.msi"),0)
function Read-MsiTable([string]$Query,[int]$Columns) {
    $view=$database.OpenView($Query);$null=$view.Execute()
    try { while($record=$view.Fetch()) { $values=@();for($i=1;$i -le $Columns;$i++){$values+=$record.StringData($i)}; ,$values } }
    finally { $null=$view.Close() }
}
$properties=@{}
Read-MsiTable 'SELECT Property, Value FROM Property' 2 | ForEach-Object {$properties[$_[0]]=$_[1]}
if($properties.ProductVersion -ne $Version -or $properties.ProductName -ne 'UsageLoom'){throw 'Invalid product identity'}
if($properties.ARPSYSTEMCOMPONENT -eq '1' -or $properties.ARPNOREMOVE -eq '1'){throw 'Uninstall entry is hidden'}
if($properties.ProductLanguage -ne '2052'){throw 'Installer language is not Simplified Chinese'}
if($properties.ALLUSERS -or $properties.MSIINSTALLPERUSER -or $properties.DISABLEROLLBACK){throw 'Installation scope or rollback safety changed'}
$conditions=@(Read-MsiTable 'SELECT Condition, Description FROM LaunchCondition' 2 | ForEach-Object { $_[0] })
foreach($condition in @('MsiRunningElevated = 1','NOT ALLUSERS','NOT LOOM_EXPECTED_USER_SID OR LOOM_EXPECTED_USER_SID = UserSID')){
    if($conditions -notcontains $condition){throw "Missing installer preflight: $condition"}
}
foreach($table in @('InstallUISequence','InstallExecuteSequence')){
    $sequence=@{};Read-MsiTable "SELECT Action, Sequence FROM $table" 2 | ForEach-Object {$sequence[$_[0]]=[int]$_[1]}
    if(!$sequence.LaunchConditions -or ($sequence.InstallInitialize -and $sequence.LaunchConditions -ge $sequence.InstallInitialize) -or ($sequence.RemoveExistingProducts -and $sequence.LaunchConditions -ge $sequence.RemoveExistingProducts)){throw 'Preflight runs after modification starts'}
    if($table -eq 'InstallExecuteSequence' -and $sequence.RemoveExistingProducts -le $sequence.InstallInitialize){throw 'Old product removal must remain transactional'}
}
$launcher=Get-Content (Join-Path $workspace 'artifacts/installer/UsageLoom-install.cmd') -Raw
$hash=(Get-FileHash (Join-Path $workspace "artifacts/installer/UsageLoom-$Version-win-x64.msi") -Algorithm SHA256).Hash
if(!$launcher.Contains($hash) -or !$launcher.Contains("UsageLoom-$Version-win-x64.msi") -or $launcher.Contains('@VERSION@') -or $launcher.Contains('@SHA256@')){throw 'Manual launcher does not match this MSI'}
$script=$launcher.Split(@('# POWERSHELL' + '-START'),[StringSplitOptions]::None)[1]
$parseTokens=$null;$parseErrors=$null
$null=[Management.Automation.Language.Parser]::ParseInput($script,[ref]$parseTokens,[ref]$parseErrors)
if($parseErrors.Count){throw "Invalid launcher PowerShell: $parseErrors"}
if($launcher -match '(?<!\r)\n'){throw 'CMD launcher requires CRLF line endings'}
$launcherFixture=Join-Path $workspace ('artifacts/launcher-check-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $launcherFixture
Copy-Item -LiteralPath (Join-Path $workspace 'artifacts/installer/UsageLoom-install.cmd') -Destination $launcherFixture
# A fresh directory contains no MSI, so the actual CMD wrapper must stop before any installation.
& "$env:SystemRoot/System32/cmd.exe" /d /c ('"'+(Join-Path $launcherFixture 'UsageLoom-install.cmd')+'" <NUL')
if($LASTEXITCODE -ne 1){throw 'CMD wrapper did not reject the missing MSI'}
$global:LASTEXITCODE=0
Write-Output 'PASS installer preflight: actual elevation, same user, per-user scope, transactional rollback, matching launcher.'
$dialogs=Read-MsiTable 'SELECT Dialog FROM Dialog' 1
if($properties.LOOM_DESKTOP -ne '1' -or $properties.LOOM_STARTMENU -ne '1'){throw 'Invalid shortcut defaults'}
if(!($dialogs | Where-Object {$_[0] -eq 'LoomShortcutsDlg'})){throw 'Missing shortcut selection dialog'}
$components=Read-MsiTable 'SELECT Component, Condition FROM Component' 2
$layout=@{}
Read-MsiTable 'SELECT Dialog_, Control, X, Y, Width, Height, Text FROM Control' 7 | Where-Object {$_[0] -eq 'LoomShortcutsDlg'} | ForEach-Object {$layout[$_[1]]=$_}
$dialog=Read-MsiTable 'SELECT Dialog, Width, Height FROM Dialog' 3 | Where-Object {$_[0] -eq 'LoomShortcutsDlg'}
foreach($row in $layout.Values){
    if([int]$row[2] -lt 0 -or [int]$row[3] -lt 0 -or ([int]$row[2]+[int]$row[4]) -gt [int]$dialog[1] -or ([int]$row[3]+[int]$row[5]) -gt [int]$dialog[2]){throw "Out-of-bounds installer control: $($row[1])"}
}
$previous=$null
foreach($id in @('Back','Next','Cancel')){
    $row=$layout[$id]
    if(!$row -or $row[3] -ne '225' -or $row[4] -ne '56' -or $row[5] -ne '18'){throw "Inconsistent installer button: $id"}
    if($previous -and ([int]$row[2]-[int]$previous[2]-[int]$previous[4]) -ne 8){throw 'Unequal installer button gaps'}
    $previous=$row
}
$previous=$null
foreach($id in @('Title','Description','Desktop','StartMenu','TaskbarTitle','Note','UninstallNote')){
    $row=$layout[$id]
    if(!$row -or $row[2] -ne '24'){throw "Misaligned installer text: $id"}
    if($previous -and [int]$row[3] -lt ([int]$previous[3]+[int]$previous[5])){throw "Overlapping installer text: $id"}
    $previous=$row
}
foreach($id in @('Description','Desktop','StartMenu','Note','UninstallNote','Back','Next','Cancel')){
    if(!$layout[$id][6].StartsWith('{\LoomBody}')){throw "Missing body font: $id"}
}
$bodyStyle=Read-MsiTable 'SELECT TextStyle, FaceName, Size FROM TextStyle' 3 | Where-Object {$_[0] -eq 'LoomBody'}
if(!$bodyStyle -or $bodyStyle[1] -ne 'Microsoft YaHei UI' -or $bodyStyle[2] -ne '9'){throw 'Incorrect installer body style'}
$binary=Read-MsiTable 'SELECT Name FROM Binary' 1
if($layout['BrandIcon'][6] -ne 'LoomBrandIcon' -or !($binary | Where-Object {$_[0] -eq 'LoomBrandIcon'})){throw 'Missing installer brand icon'}
Write-Output 'PASS installer layout: boundaries, text alignment, fonts, button spacing, and embedded icon.'
foreach($pair in @(@('DesktopEntry','LOOM_DESKTOP'),@('StartMenuEntry','LOOM_STARTMENU'))){
    $row=$components | Where-Object {$_[0] -eq $pair[0]}
    if(!$row -or $row[1] -ne ($pair[1]+' = "1"')){throw 'Shortcut component is not bound to its option'}
}
if(!($dialogs | Where-Object {$_[0] -eq 'InstallDirDlg'})){throw 'Missing install-directory dialog'}
$shortcuts=Read-MsiTable 'SELECT Shortcut, Directory_, Target, Arguments, WkDir FROM Shortcut' 5
foreach($id in @('StartMenuShortcut','DesktopShortcut','UninstallShortcut')){
    $entry=$shortcuts | Where-Object {$_[0] -eq $id}
    if(!$entry){throw "Missing shortcut $id"}
    if($id -ne 'UninstallShortcut' -and ($entry[2] -ne '[INSTALLFOLDER]UsageLoom.App.exe' -or $entry[3] -ne '--show' -or $entry[4] -ne 'INSTALLFOLDER')){throw "Shortcut does not target the selected install directory: $id"}
    Write-Output "PASS shortcut: $id"
}
$files=Read-MsiTable 'SELECT FileName FROM File' 1
foreach($name in @('UsageLoom.App.exe','UsageLoom.App.dll','UsageLoom.App.pri','App.xbf','coreclr.dll','Update-UsageLoom.ps1','update-manifest.txt')){
    if(!($files | Where-Object {($_[0] -split '\|')[-1] -eq $name})){throw "MSI is missing $name"}
}
Write-Output "PASS MSI $Version : language, install directory, launch/uninstall shortcuts, and required resources. Installation was not executed."
