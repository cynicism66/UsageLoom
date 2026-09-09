function Get-ReleaseVersion {
    param([string]$RequestedVersion)
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $project = [xml](Get-Content -LiteralPath (Join-Path $root 'src/UsageLoom.App/UsageLoom.App.csproj') -Raw)
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project version' }
    if ($RequestedVersion -and $RequestedVersion -ne $version) { throw 'Requested version differs from project version' }
    return $version
}
