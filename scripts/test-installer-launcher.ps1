# Execute the launcher's PowerShell with all installation/registry boundaries mocked.
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility
Import-Module Microsoft.PowerShell.Management
$workspace=Split-Path $PSScriptRoot
$template=Get-Content (Join-Path $workspace 'packaging/UsageLoom-install.cmd') -Raw -Encoding UTF8
$script=$template.Split(@('# POWERSHELL' + '-START'),[StringSplitOptions]::None)[1].Replace('exit 0','return').Replace('exit 1',"throw 'Launcher failed'")
function New-Object { param($ComObject)
    if($ComObject -ne 'WindowsInstaller.Installer'){throw 'Unexpected COM object'}
    $mock=[pscustomobject]@{}
    $mock | Add-Member ScriptMethod RelatedProducts {
        param($code)
        if($code -ne '{9B2E2C4E-0A9E-4F4E-9F18-9B31A9E9E2B1}'){throw 'Wrong upgrade identity'}
        if($script:scenario -eq 'new'){return}
        if($script:scenario -eq 'duplicates'){return @('old1','old2')}
        return 'old1'
    }
    $mock | Add-Member ScriptMethod ProductInfo { param($code,$property)
        if($code -ne 'old1' -or $property -ne 'InstallLocation'){throw 'Unexpected product query'}
        return 'D:\Fixture path with spaces\'
    }
    return $mock
}
function Get-FileHash { param($LiteralPath,$Algorithm)
    return [pscustomobject]@{Hash=$(if($script:scenario -eq 'bad-hash'){'bad'}else{'@SHA256@'})}
}
function Test-Path { param($LiteralPath,$PathType) return $script:scenario -ne 'missing' }
function Start-Process { param($FilePath,$Verb,$ArgumentList,[switch]$Wait,[switch]$PassThru)
    $script:starts++
    if($Verb -ne 'RunAs' -or !$Wait -or !$PassThru -or $FilePath -ne (Join-Path $env:SystemRoot 'System32/msiexec.exe')){throw 'Invalid elevated installer request'}
    if($ArgumentList -notcontains ('LOOM_EXPECTED_USER_SID='+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)){throw 'Missing account guard'}
    if($script:scenario -eq 'upgrade' -and $ArgumentList -notcontains 'INSTALLFOLDER="D:\Fixture path with spaces"'){throw 'Existing directory not preserved'}
    if($script:scenario -eq 'new' -and ($ArgumentList | Where-Object {$_ -like 'INSTALLFOLDER=*'})){throw 'New installation has an unexpected directory override'}
    if($script:scenario -eq 'cancel'){throw [ComponentModel.Win32Exception]::new(1223)}
    return [pscustomobject]@{ExitCode=$(if($script:scenario -eq 'failure'){1603}else{0})}
}
$original=$env:LOOM_INSTALL_LAUNCHER
try {
    $env:LOOM_INSTALL_LAUNCHER=Join-Path $workspace 'packaging/UsageLoom-install.cmd'
    foreach($script:scenario in @('new','upgrade','missing','bad-hash','duplicates','cancel','failure')){
        $script:starts=0;$failed=$false
        try { & ([scriptblock]::Create($script)) } catch { $failed=$true }
        $shouldFail=$script:scenario -notin @('new','upgrade')
        if($failed -ne $shouldFail){throw "Unexpected launcher result: $script:scenario"}
        $expectedStarts=if($script:scenario -in @('missing','bad-hash','duplicates')){0}else{1}
        if($script:starts -ne $expectedStarts){throw "Unexpected installer starts: $script:scenario"}
        Write-Output "PASS manual installer launcher: $script:scenario"
    }
} finally { $env:LOOM_INSTALL_LAUNCHER=$original }
