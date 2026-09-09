# Isolated fixtures only. Never launch an app, MSI, or touch the real install/data.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$testRoot = Join-Path $workspace ('artifacts/updater-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Start-Process { param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$PassThru, [switch]$Wait)
    $global:updaterTestStarts++
    return [pscustomobject]@{ ExitCode = 1603 }
}
function Copy-Item { param($LiteralPath, $Destination, [switch]$Force)
    if ($global:updaterTestFailCopy -and $LiteralPath -like '*\payload\UsageLoom.App.dll') {
        $global:updaterTestFailCopy = $false
        throw 'Synthetic write failure'
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
foreach ($scenario in @('success', 'rollback', 'conflict', 'traversal', 'bad-hash', 'msi-failure')) {
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
    [IO.File]::WriteAllLines((Join-Path $installRoot 'update-manifest.txt'), ($required + 'obsolete.dll'))
    [IO.File]::WriteAllLines((Join-Path $sourceRoot 'update-manifest.txt'), ($required + 'new.dll' + $(if ($scenario -eq 'traversal') {'../escape.txt'} else {@()})))
    [IO.File]::WriteAllText((Join-Path $installRoot 'obsolete.dll'), 'old')
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
        Installed = $scenario -eq 'msi-failure'
    }
    [IO.File]::WriteAllText((Join-Path $jobRoot 'job.json'), ($job | ConvertTo-Json))
    $global:updaterTestStarts = 0
    $global:updaterTestFailCopy = $scenario -eq 'rollback'
    & (Join-Path $jobRoot 'update.ps1')
    $result = [IO.File]::ReadAllText((Join-Path $jobRoot 'result.txt'))
    $actual = [IO.File]::ReadAllText((Join-Path $installRoot 'UsageLoom.App.dll'))
    if ($scenario -eq 'success') {
        if ($actual -ne 'new' -or $result -notmatch 'Update completed' -or (Test-Path -LiteralPath (Join-Path $installRoot 'obsolete.dll'))) { throw "Failed: $scenario $result" }
        if ([IO.File]::ReadAllText((Join-Path $jobRoot 'backup/UsageLoom.App.dll')) -ne 'old') { throw 'Backup missing' }
    } else {
        if ($actual -ne 'old' -or $result -match '^Update completed') { throw "Failed: $scenario $result" }
        if ($scenario -eq 'rollback' -and $result -notmatch 'Previous version restored') { throw "Rollback not exercised: $result" }
    }
    if ([IO.File]::ReadAllText((Join-Path $installRoot 'personal.txt')) -ne 'keep') { throw 'User file changed' }
    Write-Output "PASS updater: $scenario"
}
Write-Output "Fixtures retained: $testRoot"
