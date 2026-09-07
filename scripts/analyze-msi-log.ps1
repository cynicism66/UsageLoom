[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference='Stop'
$starts=@{};$results=@()
foreach($line in Get-Content -LiteralPath $Path){
    if($line -match '(?:Action start|操作开始)\s+(\d{1,2}:\d{2}:\d{2}):\s*([A-Za-z][A-Za-z0-9_]+)'){
        $name=$Matches[2];if(!$starts.ContainsKey($name)){$starts[$name]=[System.Collections.Generic.Stack[TimeSpan]]::new()}
        $starts[$name].Push([TimeSpan]::Parse($Matches[1]))
    } elseif($line -match '(?:Action ended|操作结束)\s+(\d{1,2}:\d{2}:\d{2}):\s*([A-Za-z][A-Za-z0-9_]+)'){
        $name=$Matches[2];$end=[TimeSpan]::Parse($Matches[1])
        if($starts.ContainsKey($name) -and $starts[$name].Count){
            $start=$starts[$name].Pop();$seconds=($end-$start).TotalSeconds;if($seconds -lt 0){$seconds+=86400}
            $results+=[pscustomobject]@{Action=$name;Seconds=$seconds}
        }
    }
}
if(!$results.Count){throw '未识别到 MSI 标准动作时间，请提供完整详细日志'}
$results | Sort-Object Seconds -Descending | Select-Object -First 20
Write-Output '单位为秒，精度约一秒；嵌套动作不能相加为总时间。只输出动作名和耗时，不输出路径或账号。'
