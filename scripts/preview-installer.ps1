[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$MsiPath,[string]$Dialog='LoomShortcutsDlg',[int]$Seconds=60)
$ErrorActionPreference='Stop'
# Windows Installer authoring preview only: no install session or execute sequence.
Add-Type -AssemblyName System.Windows.Forms
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$installer.OpenDatabase((Resolve-Path -LiteralPath $MsiPath).Path,0)
$preview=$database.EnableUIPreview()
try {
    $preview.ViewDialog($Dialog)
    $deadline=[DateTime]::UtcNow.AddSeconds([Math]::Clamp($Seconds,1,300))
    while([DateTime]::UtcNow -lt $deadline){[System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50}
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($preview)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
