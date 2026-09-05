[CmdletBinding()]
param([string]$OutputDirectory)

$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$OutputDirectory = if ($OutputDirectory) {
  $OutputDirectory
} else {
  Join-Path $pluginRoot "dist"
}
$manifestPath = Join-Path $pluginRoot ".codex-plugin\plugin.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
  throw "Missing plugin manifest: $manifestPath"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$pluginName = [string]$manifest.name
$pluginVersion = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($pluginName) -or [string]::IsNullOrWhiteSpace($pluginVersion)) {
  throw "Manifest must define name and version"
}

$outputRoot = $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $outputRoot).Path
$archivePath = Join-Path $outputRoot "$pluginName-$pluginVersion.zip"
if (Test-Path -LiteralPath $archivePath) {
  Remove-Item -LiteralPath $archivePath -Force
}

Push-Location $pluginRoot
try {
  # Keep the release archive intentionally small and auditable. In particular,
  # never package .git, tests, caches, local fixtures, or generated archives.
  $packagePaths = @(
    ".codex-plugin\\plugin.json",
    "assets\\usage-dashboard.html",
    "docs\\ARCHITECTURE.md",
    "docs\\PRIVACY.md",
    "docs\\PROJECT_ORIGINS.md",
    "docs\\RELEASING.md",
    "server\\index.mjs",
    "skills\\usage-monitor\\SKILL.md",
    "src\\providers\\codex\\pricing.mjs",
    "src\\providers\\codex\\quota.mjs",
    "src\\providers\\codex\\usage.mjs",
    "scripts\\package-plugin.ps1",
    "scripts\\smoke-test.mjs",
    ".mcp.json",
    "package.json",
    "LICENSE",
    "LICENSE.zh-CN.md",
    "README.md",
    "THIRD_PARTY_NOTICES.md",
    "SECURITY.md",
    "CONTRIBUTING.md",
    "CHANGELOG.md"
  )
  $relativeFiles = foreach ($packagePath in $packagePaths) {
    $candidate = Join-Path $pluginRoot $packagePath
    if (-not (Test-Path -LiteralPath $candidate)) {
      throw "Missing package path: $packagePath"
    }
    $item = Get-Item -LiteralPath $candidate
    if ($item.PSIsContainer) { throw "Package allowlist must contain files only: $packagePath" }
    (Resolve-Path -LiteralPath $candidate -Relative) -replace '^\.\\', ''
  }

  if (-not $relativeFiles) {
    throw "No package files found"
  }
  & tar.exe -a -c -f $archivePath -- $relativeFiles
  if ($LASTEXITCODE -ne 0) {
    throw "tar.exe failed with exit code $LASTEXITCODE"
  }
} finally {
  Pop-Location
}

Write-Output "Created $archivePath"
Write-Output "Included $($relativeFiles.Count) files"
