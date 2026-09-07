[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$toolRoot = Join-Path $workspace '.tools'
$sdkRoot = Join-Path $toolRoot 'dotnet'
$dotnetPath = Join-Path $sdkRoot 'dotnet.exe'
if (Test-Path -LiteralPath $dotnetPath) {
    & $dotnetPath --version
    exit $LASTEXITCODE
}
$sdkVersion = '10.0.400'
$uri = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip"
$expectedHash = '9b8b88590e4da131bfd0da7aa089d0fc04d5418d5f8607ec13d55dc5a17b4399afd54d496c12657fa05c6c6546dc5eab930f26ac6c50f2d3a7712c0fb378c366'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
$archive = Join-Path $toolRoot "dotnet-sdk-$sdkVersion-win-x64.zip"
if (-not (Test-Path -LiteralPath $archive)) {
    Write-Output "下载 .NET SDK $sdkVersion（仅项目本地工具目录）"
    Invoke-WebRequest -Uri $uri -OutFile $archive
}
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash
if ($actualHash -ne $expectedHash) { throw 'SDK SHA-512 不匹配，停止安装；请核对本地下载文件。' }
if (Test-Path -LiteralPath $sdkRoot) { throw 'SDK 目录已存在但缺少可执行文件，不覆盖未知内容。' }
Expand-Archive -LiteralPath $archive -DestinationPath $sdkRoot
Write-Output 'SDK 校验与解压完成；未修改全局 PATH。'
& $dotnetPath --info
exit $LASTEXITCODE
