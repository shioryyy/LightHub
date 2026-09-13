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

# The distributed artifact is the archive itself, so its contents must match the
# already-verified directory exactly: same relative paths, same bytes, nothing extra.
$prefix = (Split-Path -Leaf $packageRoot) + '/'
$expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($file in $files) { $expected[[IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
function Assert-ArchiveEntry([string]$RawName, [string]$Hash) {
    $FullName = $RawName.Replace('\', '/')
    if (-not $FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw "Archive entry outside the package folder: $RawName" }
    $relative = $FullName.Substring($prefix.Length)
    if ($relative -match '(^|/)\.\.?(/|$)' -or -not $expected.ContainsKey($relative)) { throw "Archive entry is unsafe or not part of the verified directory: $relative" }
    if (-not $seen.Add($relative)) { throw "Duplicate archive entry: $relative" }
    if ($Hash -ne $expected[$relative]) { throw "Archive entry differs from the verified directory: $relative" }
}
if ($Runtime.StartsWith('win-')) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) { continue }
            $sha = [Security.Cryptography.SHA256]::Create(); $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)) } finally { $stream.Dispose(); $sha.Dispose() }
            Assert-ArchiveEntry $entry.FullName $hash
        }
    } finally { $zip.Dispose() }
} else {
    # The archive must be verified member by member: tar extraction silently lets a
    # later member overwrite an earlier one, so a wrong-content member followed by a
    # correct duplicate would otherwise escape the extracted-tree comparison. List
    # every member first (duplicates, unexpected names, traversal), then compare the
    # extracted content.
    $rootName = Split-Path -Leaf $packageRoot
    $tarListArgs = @('-tzf', $archive)
    if ((tar --version) -match 'GNU tar') { $tarListArgs += '--force-local' }
    $members = & tar @tarListArgs
    if ($LASTEXITCODE -ne 0) { throw 'Archive listing failed' }
    foreach ($raw in $members) {
        $name = $raw.Replace('\', '/').TrimEnd('/')
        if ([string]::IsNullOrEmpty($name) -or $name -eq $rootName) { continue }
        if (-not $name.StartsWith("$rootName/", [StringComparison]::Ordinal)) { throw "Archive entry outside the package folder: $raw" }
        $relative = $name.Substring($rootName.Length + 1)
        if ($relative -match '(^|/)\.\.?(/|$)') { throw "Unsafe archive entry: $relative" }
        if ($raw.EndsWith('/')) { continue }
        if (-not $expected.ContainsKey($relative)) { throw "Archive entry is unsafe or not part of the verified directory: $relative" }
        if (-not $seen.Add($relative)) { throw "Duplicate archive entry: $relative" }
    }
    # .NET's TarReader mishandles PAX entry names, so extract with the platform tar
    # tool and compare the extracted tree against the verified directory as well;
    # this catches a single wrong-content member that no later member overwrites.
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("lhverify-" + [Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $temp | Out-Null
        $tarArgs = @('-xzf', $archive, '-C', $temp)
        if ((tar --version) -match 'GNU tar') { $tarArgs += '--force-local' }
        & tar @tarArgs
        if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed' }
        $roots = @(Get-ChildItem -LiteralPath $temp)
        if ($roots.Count -ne 1 -or -not $roots[0].PSIsContainer -or $roots[0].Name -ne $rootName) { throw 'Archive root layout does not match the package folder' }
        $root = $roots[0].FullName
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
            if (-not $expected.ContainsKey($relative)) { throw "Archive entry is unsafe or not part of the verified directory: $relative" }
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected[$relative]) { throw "Archive entry differs from the verified directory: $relative" }
        }
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
}
$missing = @($expected.Keys | Where-Object { -not $seen.Contains($_) })
if ($missing.Count) { throw "Archive is missing verified package files: $($missing -join ', ')" }
Write-Output "Verified $($listed.Count) package files and all archive entries; source $($metadata.sourceCommit), runtime $Runtime"
