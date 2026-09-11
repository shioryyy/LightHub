param([Parameter(Mandatory=$true)][string]$Directory, [Parameter(Mandatory=$true)][string]$Runtime, [switch]$AllowDevelopmentSource)
$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$metadata = Get-Content -LiteralPath (Join-Path $packageRoot 'BUILD-METADATA.json') -Raw | ConvertFrom-Json
if ($metadata.runtime -ne $Runtime) { throw 'Package runtime does not match its job' }
if (-not $AllowDevelopmentSource -and ($metadata.sourceCommit -notmatch '^[0-9a-f]{40}$' -or $metadata.sourceDirty -ne $false)) { throw 'Release packages require an identified clean source tree' }
$manifest = Join-Path $packageRoot 'SOURCE-MANIFEST.json'
if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $metadata.sourceManifestSha256) { throw 'Source manifest hash mismatch' }
$suffix = if ($Runtime.StartsWith('win-')) { '.exe' } else { '' }
foreach ($name in @("LightHub.Desktop$suffix", "LightHub.Cli$suffix", 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/dependencies.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $name) -PathType Leaf)) { throw "Required package file missing: $name" }
}
if (-not $IsWindows -and -not $Runtime.StartsWith('win-')) {
    foreach ($name in @('LightHub.Desktop', 'LightHub.Cli')) {
        if (([IO.File]::GetUnixFileMode((Join-Path $packageRoot $name)) -band [IO.UnixFileMode]::UserExecute) -eq 0) { throw "Missing executable bit: $name" }
    }
}
$files = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File)
if (@($files | Where-Object { $_.Name -match '\.(lhbackup|lhmacro|lhdraft|lhpreset|raw\.json)$' -or $_.Name -like '*HardwareProbe*' -or $_.Name -like 'LightHub.Tests*' }).Count) { throw 'Private data or developer executables found in the app package' }
$listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Malformed checksum entry' }
    $hash = $Matches[1]; $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '\\|(^|/)\.\.?(/|$)' -or -not $listed.Add($relative)) { throw 'Unsafe or duplicate checksum path' }
    $path = [IO.Path]::GetFullPath($relative, $packageRoot)
    if (-not $path.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Checksum path escaped package root' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash) { throw "Checksum mismatch: $relative" }
}
foreach ($file in $files | Where-Object Name -ne 'SHA256SUMS.txt') {
    if (-not $listed.Contains([IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\','/'))) { throw 'Unlisted package file' }
}
$archive = $packageRoot + $(if ($Runtime.StartsWith('win-')) { '.zip' } else { '.tar.gz' })
if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or (Get-Item -LiteralPath $archive).Length -eq 0) { throw 'Package archive missing' }
Write-Output "Verified $($listed.Count) package files; source $($metadata.sourceCommit), runtime $Runtime"
