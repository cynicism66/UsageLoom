# Isolated fixtures only. Never launch an app, MSI, or touch the real install/data.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$testRoot = Join-Path $workspace ('artifacts/updater-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Start-Process { param($FilePath, $ArgumentList, $WorkingDirectory, $Verb, [switch]$PassThru, [switch]$Wait)
    $global:updaterTestStarts++
    if ($FilePath -like '*msiexec.exe') {
        if ($Verb -ne 'RunAs' -or !$Wait -or !$PassThru) { throw 'MSI must request UAC and wait for the result' }
        if ($ArgumentList -notcontains ('LOOM_EXPECTED_USER_SID=' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)) { throw 'Missing original user SID guard' }
        if (!($ArgumentList | Where-Object { $_ -like 'INSTALLFOLDER="*"' })) { throw 'Missing original install directory' }
        $global:updaterTestElevations++
        if ($global:updaterTestScenario -eq 'msi-cancel') { throw [ComponentModel.Win32Exception]::new(1223) }
        if ($global:updaterTestScenario -eq 'msi-success') { return [pscustomobject]@{ ExitCode = 0 } }
        if ($global:updaterTestScenario -eq 'msi-reboot') { return [pscustomobject]@{ ExitCode = 3010 } }
    } elseif ($Verb) { throw 'The application must not be restarted elevated' }
    return [pscustomobject]@{ ExitCode = 1603 }
}
function Copy-Item { param($LiteralPath, $Destination, [switch]$Force)
    if ($global:updaterTestFailCopy -and $LiteralPath -like '*\payload\UsageLoom.App.dll') {
        $global:updaterTestFailCopy = $false
        throw 'Synthetic write failure'
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
foreach ($scenario in @('success', 'rollback', 'conflict', 'traversal', 'bad-hash', 'msi-failure', 'msi-cancel', 'msi-success', 'msi-reboot')) {
    $caseRoot = Join-Path $testRoot $scenario
    $installRoot = Join-Path $caseRoot 'app'
    $sourceRoot = Join-Path $caseRoot 'source'
    $jobRoot = Join-Path $caseRoot 'job'
    New-Item -ItemType Directory -Path $installRoot, $sourceRoot, $jobRoot | Out-Null
    $required = @('UsageLoom.App.exe', 'UsageLoom.App.dll', 'App.xbf', 'UsageLoom.App.pri', 'update-manifest.txt')
    foreach ($root in @($installRoot, $sourceRoot)) {
        [IO.File]::Copy((Join-Path $env:SystemRoot 'System32/cmd.exe'), (Join-Path $root 'UsageLoom.App.exe'))
        foreach ($name in @('UsageLoom.App.dll', 'App.xbf', 'UsageLoom.App.pri')) {
            [IO.File]::WriteAllText((Join-Path $root $name), $(if ($root -eq $installRoot) {'old'} else {'new'}))
        }
    }
    $obsolete=@('obsolete.dll','onnxruntime.dll','DirectML.dll','Microsoft.Windows.AI.Text.dll','Microsoft.Windows.Widgets.dll')
    [IO.File]::WriteAllLines((Join-Path $installRoot 'update-manifest.txt'), ($required + $obsolete))
    [IO.File]::WriteAllLines((Join-Path $sourceRoot 'update-manifest.txt'), ($required + 'new.dll' + $(if ($scenario -eq 'traversal') {'../escape.txt'} else {@()})))
    foreach($name in $obsolete){[IO.File]::WriteAllText((Join-Path $installRoot $name), 'old')}
    [IO.File]::WriteAllText((Join-Path $installRoot 'personal.txt'), 'keep')
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'new.dll'), 'new')
    if ($scenario -eq 'conflict') { [IO.File]::WriteAllText((Join-Path $installRoot 'new.dll'), 'personal') }
    $zipPath = Join-Path $jobRoot 'package.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot, $zipPath)
    $helper = [IO.File]::ReadAllText((Join-Path $workspace 'src/UsageLoom.App/Update-UsageLoom.ps1'))
    # Use a unique mutex so fixtures cannot block or interact with the user's app.
    $helper = $helper.Replace('Local\UsageLoom.Desktop', ('Local\UsageLoom.UpdateTest.' + [Guid]::NewGuid().ToString('N')))
    [IO.File]::WriteAllText((Join-Path $jobRoot 'update.ps1'), $helper)
    $job = @{
        Target = $installRoot; Package = $zipPath; ProcessId = 2147483647
        Hash = $(if ($scenario -eq 'bad-hash') {'bad'} else {(Get-FileHash -LiteralPath $zipPath).Hash})
        Version = ((Get-Item -LiteralPath (Join-Path $sourceRoot 'UsageLoom.App.exe')).VersionInfo.ProductVersion -split '\+')[0]
        Installed = $scenario.StartsWith('msi-')
    }
    [IO.File]::WriteAllText((Join-Path $jobRoot 'job.json'), ($job | ConvertTo-Json))
    $global:updaterTestStarts = 0
    $global:updaterTestElevations = 0
    $global:updaterTestScenario = $scenario
    $global:updaterTestFailCopy = $scenario -eq 'rollback'
    & (Join-Path $jobRoot 'update.ps1')
    $result = [IO.File]::ReadAllText((Join-Path $jobRoot 'result.txt'))
    $actual = [IO.File]::ReadAllText((Join-Path $installRoot 'UsageLoom.App.dll'))
    if ($scenario -eq 'success') {
        if ($actual -ne 'new' -or $result -notmatch 'Update completed' -or (Test-Path -LiteralPath (Join-Path $installRoot 'obsolete.dll'))) { throw "Failed: $scenario $result" }
        if ([IO.File]::ReadAllText((Join-Path $jobRoot 'backup/UsageLoom.App.dll')) -ne 'old') { throw 'Backup missing' }
        foreach($name in $obsolete){
            if(Test-Path -LiteralPath (Join-Path $installRoot $name)){throw "Obsolete component retained: $name"}
            if([IO.File]::ReadAllText((Join-Path $jobRoot "backup/$name")) -ne 'old'){throw "Component backup missing: $name"}
        }
    } elseif ($scenario -in @('msi-success', 'msi-reboot')) {
        if ($actual -ne 'old' -or $result -notmatch '^Update completed') { throw "Failed: $scenario $result" }
    } else {
        if ($actual -ne 'old' -or $result -match '^Update completed') { throw "Failed: $scenario $result" }
        if ($scenario -eq 'rollback' -and $result -notmatch 'Previous version restored') { throw "Rollback not exercised: $result" }
        foreach($name in $obsolete){if([IO.File]::ReadAllText((Join-Path $installRoot $name)) -ne 'old'){throw "Old component lost: $name"}}
    }
    if ([IO.File]::ReadAllText((Join-Path $installRoot 'personal.txt')) -ne 'keep') { throw 'User file changed' }
    if ($scenario.StartsWith('msi-') -and ($global:updaterTestElevations -ne 1 -or $global:updaterTestStarts -ne 2)) { throw 'Expected one installer request and one normal application restart' }
    if ($scenario -eq 'msi-cancel' -and $result -notmatch 'authorization cancelled; installation was not started') { throw 'UAC cancellation was not reported' }
    Write-Output "PASS updater: $scenario"
}
Write-Output "Fixtures retained: $testRoot"
