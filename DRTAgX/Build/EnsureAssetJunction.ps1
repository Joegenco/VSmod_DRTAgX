param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$junctionProject = [IO.Directory]::GetParent($PSScriptRoot).FullName
$junctionSource = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
$junctionOutput = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$junctionExpectedSource = Join-Path $junctionProject 'assets'
$junctionOutputRoot = (Join-Path $junctionProject 'bin') + '\'

# Build outputs must remain inside this project. Never remove an existing link
# or directory: following a junction while deleting could erase source assets.
if (!$junctionSource.Equals($junctionExpectedSource, [StringComparison]::OrdinalIgnoreCase) -or
    !$junctionOutput.StartsWith($junctionOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Asset junction paths must use the project assets and bin directories.'
}
if (!(Test-Path -LiteralPath $junctionSource -PathType Container)) {
    throw "Source assets are unavailable: $junctionSource"
}

$junctionExisting = Get-Item -LiteralPath $junctionOutput -Force -ErrorAction SilentlyContinue
if ($null -ne $junctionExisting) {
    $junctionTargets = @($junctionExisting.Target)
    if ($junctionExisting.PSIsContainer -and
        ($junctionExisting.LinkType -eq 'Junction' -or $junctionExisting.LinkType -eq 'SymbolicLink') -and
        $junctionTargets.Count -eq 1) {
        $junctionTarget = [IO.Path]::GetFullPath([IO.Path]::Combine($junctionExisting.Parent.FullName, [string]$junctionTargets[0])).TrimEnd('\')
        if ($junctionTarget.Equals($junctionSource, [StringComparison]::OrdinalIgnoreCase)) { return }
    }
    throw "Asset output already exists and is not the expected link; it was preserved: $junctionOutput"
}

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($junctionOutput)) | Out-Null
New-Item -ItemType Junction -Path $junctionOutput -Target $junctionSource | Out-Null
