[CmdletBinding()]
param([switch]$AllPages,[switch]$English,[switch]$Personalization,[switch]$CapacityDetails,[string]$PublishDirectory)
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if(!$PublishDirectory){$PublishDirectory=Join-Path $workspace 'artifacts/win-x64'}
$exe=Join-Path $PublishDirectory 'UsageLoom.App.exe'
# Preview uses a separate mutex and data directory; do not stop the user's app.
$env:USAGELOOM_TEST_DATA=Join-Path $workspace 'artifacts/ui-preview-data'
if($CapacityDetails){$env:USAGELOOM_TEST_DATA=Join-Path $workspace 'artifacts/ui-preview-capacity-data'}
$log=Join-Path $env:USAGELOOM_TEST_DATA 'logs/runtime.log'
$scenarios=@('populated','empty')
if($AllPages){$scenarios+=@('quota','breakdown','sessions','single-day','narrow-overview','wide-overview','settings','about')}
if($CapacityDetails){$scenarios=@('capacity-details')}
foreach($scenario in $scenarios){
    $before=if(Test-Path -LiteralPath $log){@(Get-Content -LiteralPath $log -Encoding UTF8).Count}else{0}
    $arguments=@('--smoke-test','--navigation-check')
    if($English){$arguments+='--preview-english'}
    if($Personalization){$arguments+='--personalization-check'}
    if($scenario -eq 'empty'){$arguments+='--preview-empty'}
    if($scenario -eq 'capacity-details'){$arguments+='--capacity-ui-check'}
    if($scenario -eq 'quota'){$arguments+='--preview-weekly'}
    if($scenario -eq 'sessions'){$arguments+='--preview-session-stress'}
    if($scenario -eq 'single-day'){$arguments+='--preview-single-day'}
    if($scenario -eq 'narrow-overview'){$arguments+='--preview-narrow'}
    if($scenario -eq 'wide-overview'){$arguments+=@('--preview-wide','--preview-single-day')}
    $page='overview'
    if($scenario -notin @('populated','empty','single-day','narrow-overview','wide-overview','capacity-details')){$page=$scenario;$arguments+=@("--preview-page=$page",'--preview-narrow')}
    $testProcess=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if(!$testProcess.WaitForExit(25000)){throw "UI smoke test timed out: $scenario; process was not terminated"}
    $testProcess.Refresh()
    $lines=@(Get-Content -LiteralPath $log -Encoding UTF8 | Select-Object -Skip $before)
    $lines | Write-Output
    if($testProcess.ExitCode -ne 0 -or $lines -match '\[ERROR\]' -or (!$Personalization -and !($lines -match 'Repeated navigation passed')) -or !($lines -match "Initial page ready: $page")){
        throw "UI smoke test failed: $scenario, exit=$($testProcess.ExitCode)"
    }
    if($Personalization -and !($lines -match 'Theme controls and language restart boundary passed')){throw 'Personalization control check did not complete'}
    if($Personalization -and !($lines -match 'Trend header 380/520/900 DIP resize geometry passed')){throw 'Responsive trend header check did not complete'}
    if($CapacityDetails -and !($lines -match 'Capacity detail entry and compact layout passed')){throw 'Capacity detail and compact layout check did not complete'}
    Write-Output "PASS UI startup: $scenario"
}
