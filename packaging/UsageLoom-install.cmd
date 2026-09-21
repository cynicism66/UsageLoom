@echo off
chcp 65001 >nul
setlocal
set "LOOM_INSTALL_LAUNCHER=%~f0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -Command "& ([scriptblock]::Create([IO.File]::ReadAllText($env:LOOM_INSTALL_LAUNCHER).Split(@('# POWERSHELL' + '-START'),[StringSplitOptions]::None)[1]))"
set "LOOM_INSTALL_RESULT=%ERRORLEVEL%"
if not "%LOOM_INSTALL_RESULT%"=="0" pause
exit /b %LOOM_INSTALL_RESULT%
# POWERSHELL-START
$ErrorActionPreference = 'Stop'
try {
    $folder = Split-Path -LiteralPath $env:LOOM_INSTALL_LAUNCHER
    $package = Join-Path $folder 'UsageLoom-@VERSION@-win-x64.msi'
    if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw '请将同版本的 MSI 安装包下载到本安装入口所在文件夹。' }
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '@SHA256@') { throw 'MSI 校验失败，请重新下载原始发行附件。' }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $products = @($installer.RelatedProducts('{9B2E2C4E-0A9E-4F4E-9F18-9B31A9E9E2B1}'))
    if ($products.Count -gt 1) { throw '发现多份安装登记，请先检查重复安装，暂未启动升级。' }
    $arguments = @('/i', ('"' + $package + '"'), '/norestart', ('LOOM_EXPECTED_USER_SID=' + $sid))
    if ($products.Count -eq 1) {
        $location = $installer.ProductInfo($products[0], 'InstallLocation')
        if ([string]::IsNullOrWhiteSpace($location) -or ![IO.Path]::IsPathRooted($location) -or $location.Contains('"')) { throw '无法确认原安装目录，请在同一账号的管理员终端中运行 MSI 并选择原目录。' }
        $arguments += 'INSTALLFOLDER="' + $location.TrimEnd('\') + '"'
    }
    $log = Join-Path ([IO.Path]::GetTempPath()) ('UsageLoom-install-' + [Guid]::NewGuid().ToString('N') + '.log')
    $arguments += @('/L*v', ('"' + $log + '"'))
    Write-Host '请先从托盘退出 UsageLoom；随后使用同一 Windows 账号确认 UAC 授权。'
    Write-Host ('安装日志：' + $log)
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/msiexec.exe') -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) { throw ('安装未完成，错误码：' + $process.ExitCode + '。日志：' + $log) }
    if ($process.ExitCode -eq 3010) { Write-Host '安装成功。Windows 建议重启，请在方便时重启电脑。' }
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
