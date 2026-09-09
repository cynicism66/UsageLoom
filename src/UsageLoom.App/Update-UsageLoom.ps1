$ErrorActionPreference = 'Stop'
$stage = $PSScriptRoot
$locked = $false
$changed = $false
$installed = $false
$mutex = $null
$exe = $null
$touched = [Collections.Generic.List[string]]::new()
$saved = [Collections.Generic.List[string]]::new()
function Safe-Path([string]$root, [string]$relative) {
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw 'Invalid manifest path' }
    $basePath = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $result = [IO.Path]::GetFullPath((Join-Path $basePath $relative))
    if (!$result.StartsWith($basePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes update root' }
    $cursor = $result
    while ($cursor -and $cursor.Length -ge $basePath.TrimEnd('\').Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not supported' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $result
}
try {
    $job = Get-Content -LiteralPath (Join-Path $stage 'job.json') -Raw | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath($job.Target).TrimEnd('\')
    $dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'UsageLoom\user-data')).TrimEnd('\')
    if ($target.Equals($dataRoot, [StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($dataRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cannot update inside user data' }
    $exe = Safe-Path $target 'UsageLoom.App.exe'
    if (!(Test-Path -LiteralPath $exe) -or $target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\')) { throw 'Invalid application directory' }
    if ((Get-FileHash -LiteralPath $job.Package -Algorithm SHA256).Hash -ne $job.Hash) { throw 'Package hash mismatch' }
    $installed = [bool]$job.Installed
    $process = Get-Process -Id $job.ProcessId -ErrorAction SilentlyContinue
    if ($process -and !$process.WaitForExit(180000)) { throw 'Application did not exit; files were not replaced' }
    $mutex = [Threading.Mutex]::new($false, 'Local\UsageLoom.Desktop')
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'UsageLoom is running again; update cancelled' }
    if ($installed) {
        # Windows Installer performs its own transactional rollback. Keep the install location.
        $msi = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', ('"' + $job.Package + '"'), '/passive', '/norestart', ('INSTALLFOLDER="' + $target + '"'), '/L*v', ('"' + (Join-Path $stage 'msi.log') + '"')) -PassThru -Wait
        if ($msi.ExitCode -notin @(0, 3010)) { throw "MSI upgrade failed: $($msi.ExitCode)" }
    } else {
        $payload = Join-Path $stage 'payload'
        $backup = Join-Path $stage 'backup'
        New-Item -ItemType Directory -Path $payload, $backup | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($job.Package)
        try {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            [long]$size = 0
            foreach ($entry in $zip.Entries) {
                $relative = $entry.FullName.Replace('/', '\')
                $destination = Safe-Path $payload $relative
                if (!$names.Add($relative)) { throw 'Duplicate ZIP entry' }
                $size += $entry.Length
                if ($size -gt 4GB) { throw 'Expanded package too large' }
                if ($entry.FullName.EndsWith('/')) { continue }
                New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination)
            }
        } finally { $zip.Dispose() }
        $old = @(Get-Content -LiteralPath (Safe-Path $target 'update-manifest.txt'))
        $new = @(Get-Content -LiteralPath (Safe-Path $payload 'update-manifest.txt'))
        foreach ($required in @('UsageLoom.App.exe', 'UsageLoom.App.dll', 'App.xbf', 'UsageLoom.App.pri', 'update-manifest.txt')) {
            if ($new -notcontains $required -or $old -notcontains $required) { throw 'Incomplete application manifest' }
        }
        if (((Get-Item -LiteralPath (Safe-Path $payload 'UsageLoom.App.exe')).VersionInfo.ProductVersion -split '\+')[0] -ne $job.Version) { throw 'Unexpected application version' }
        foreach ($name in $old + $new) {
            if ($name -match '(^|[\\/])(user-data|settings.json|usage-v2.sqlite)([\\/]|$)') { throw 'User data is not an application file' }
            $null = Safe-Path $target $name; $null = Safe-Path $payload $name
        }
        foreach ($name in $new) {
            if (!(Test-Path -LiteralPath (Safe-Path $payload $name) -PathType Leaf)) { throw 'Missing payload file' }
            if (($old -notcontains $name) -and (Test-Path -LiteralPath (Safe-Path $target $name))) { throw "Non-application file conflict: $name" }
        }
        # A durable backup remains available even if the helper or Windows is interrupted.
        foreach ($name in $old) {
            $source = Safe-Path $target $name
            if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
            $destination = Safe-Path $backup $name
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
            $saved.Add($name)
        }
        $changed = $true
        foreach ($name in $new) {
            $destination = Safe-Path $target $name
            $touched.Add($name)
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath (Safe-Path $payload $name) -Destination $destination -Force
        }
        foreach ($name in $old) {
            if ($new -notcontains $name) {
                $obsolete = Safe-Path $target $name
                if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete }
            }
        }
    }
    'Update completed' | Out-File -LiteralPath (Join-Path $stage 'result.txt')
} catch {
    $failure = $_.ToString()
    if ($changed -and !$installed) {
        try {
            foreach ($name in $touched) {
                if ($saved -notcontains $name) { Remove-Item -LiteralPath (Safe-Path $target $name) -ErrorAction SilentlyContinue }
            }
            foreach ($name in $saved) { Copy-Item -LiteralPath (Safe-Path $backup $name) -Destination (Safe-Path $target $name) -Force }
            $failure += "`nPrevious version restored."
        } catch { $failure += "`nRollback incomplete. Backup: $backup`n$_" }
    }
    $failure | Out-File -LiteralPath (Join-Path $stage 'result.txt')
    # The restarted app reports this result; no hidden modal dialog blocks restart.
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
if ($locked -and (Test-Path -LiteralPath $exe)) { Start-Process -FilePath $exe -ArgumentList '--show' -WorkingDirectory $target }
