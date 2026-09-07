[CmdletBinding()]
param([switch]$AllPages)
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe=Join-Path $workspace 'artifacts/win-x64/UsageLoom.App.exe'
# Preview uses a separate mutex and data directory; do not stop the user's app.
$env:USAGELOOM_TEST_DATA=Join-Path $workspace 'artifacts/ui-preview-data'
$log=Join-Path $env:USAGELOOM_TEST_DATA 'logs/runtime.log'
$scenarios=@('populated','empty')
if($AllPages){$scenarios+=@('quota','breakdown','sessions','single-day','narrow-overview','wide-overview','settings','about')}
foreach($scenario in $scenarios){
    $before=if(Test-Path -LiteralPath $log){@(Get-Content -LiteralPath $log -Encoding UTF8).Count}else{0}
    $arguments=@('--smoke-test','--navigation-check')
    if($scenario -eq 'empty'){$arguments+='--preview-empty'}
    if($scenario -eq 'quota'){$arguments+='--preview-weekly'}
    if($scenario -eq 'sessions'){$arguments+='--preview-session-stress'}
    if($scenario -eq 'single-day'){$arguments+='--preview-single-day'}
    if($scenario -eq 'narrow-overview'){$arguments+='--preview-narrow'}
    if($scenario -eq 'wide-overview'){$arguments+=@('--preview-wide','--preview-single-day')}
    $page='overview'
    if($scenario -notin @('populated','empty','single-day','narrow-overview','wide-overview')){$page=$scenario;$arguments+=@("--preview-page=$page",'--preview-narrow')}
    $testProcess=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if(!$testProcess.WaitForExit(25000)){throw "UI smoke test timed out: $scenario; process was not terminated"}
    $testProcess.Refresh()
    $lines=@(Get-Content -LiteralPath $log -Encoding UTF8 | Select-Object -Skip $before)
    $lines | Write-Output
    if($testProcess.ExitCode -ne 0 -or $lines -match '\[ERROR\]' -or !($lines -match 'Repeated navigation passed') -or !($lines -match "Initial page ready: $page")){
        throw "UI smoke test failed: $scenario, exit=$($testProcess.ExitCode)"
    }
    Write-Output "PASS UI startup: $scenario"
}
