[CmdletBinding()]
param([string]$PublishDirectory)
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot
if(!$PublishDirectory){$PublishDirectory=Join-Path $workspace 'artifacts/win-x64'}
$original=$env:USAGELOOM_TEST_DATA
$env:USAGELOOM_TEST_DATA=Join-Path $workspace ('artifacts/claude-settings-ui-'+[Guid]::NewGuid().ToString('N'))
$log=Join-Path $env:USAGELOOM_TEST_DATA 'logs/runtime.log'
try {
    foreach($phase in @('save','restart')){
        $before=if(Test-Path -LiteralPath $log){@(Get-Content -LiteralPath $log).Count}else{0}
        $arguments=@('--smoke-test','--claude-settings-check','--preview-page=settings')
        if($phase -eq 'restart'){$arguments+='--claude-settings-restart-check'}
        $process=Start-Process -FilePath (Join-Path $PublishDirectory 'UsageLoom.App.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if(!$process.WaitForExit(25000)){throw 'Isolated Claude settings UI check timed out; no process was killed'}
        $lines=@(Get-Content -LiteralPath $log -Encoding UTF8 | Select-Object -Skip $before)
        $expected=if($phase -eq 'restart'){'Claude settings process restart passed'}else{'Claude global/section save, navigation, source draft, validation and write rollback passed'}
        if($process.ExitCode -ne 0 -or $lines -match '\[ERROR\]' -or !($lines -match [regex]::Escape($expected))){$lines | Write-Output;throw "Claude settings UI failed: $phase"}
        if(!($lines -match 'Claude settings contained in Data sources without nested expander passed')){throw "Claude data source grouping check missing: $phase"}
        Write-Output "PASS $expected"
    }
} finally { $env:USAGELOOM_TEST_DATA=$original }
