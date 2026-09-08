param(
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime = 'win-x64',
    [string]$Dotnet = 'dotnet',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
Push-Location $root
try {
    & $Dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    if (-not $SkipTests) {
        & $Dotnet test --no-restore -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    $stage = Join-Path $root "dist/LightHub-$version-$Runtime"
    if (Test-Path -LiteralPath $stage) { throw "Output already exists: $stage. Archive it explicitly before rebuilding." }
    & $Dotnet publish src/LightHub.Desktop -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
    & $Dotnet publish src/LightHub.Cli -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
    foreach ($file in @('LICENSE','README.md','README.zh-CN.md','THIRD-PARTY-NOTICES.md','CHANGELOG.md')) { Copy-Item (Join-Path $root $file) $stage -Force }
    Copy-Item (Join-Path $root 'docs') $stage -Recurse -Force
    Copy-Item (Join-Path $root 'packaging') $stage -Recurse -Force
    & (Join-Path $PSScriptRoot 'dependency-inventory.ps1') -OutputDirectory (Join-Path $stage 'licenses')
    Copy-Item (Join-Path $root 'third-party/*') (Join-Path $stage 'licenses') -Recurse -Force
    $sourceEntries = Get-ChildItem (Join-Path $root 'src'),(Join-Path $root 'tests'),(Join-Path $root 'tools'),(Join-Path $root 'docs'),(Join-Path $root 'packaging'),(Join-Path $root 'third-party'),(Join-Path $root '.github') -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    $sourceEntries += Get-ChildItem $root -File -Force
    $sourceManifest = $sourceEntries | Sort-Object FullName | ForEach-Object { [ordered]@{ path=$_.FullName.Substring($root.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } }
    [IO.File]::WriteAllText((Join-Path $stage 'SOURCE-MANIFEST.json'),(ConvertTo-Json -InputObject @($sourceManifest) -Depth 4),[Text.UTF8Encoding]::new($false))
    $metadata = [ordered]@{ version=$version; runtime=$Runtime; quality='engineering-preview'; signed=$false; builtUtc=[DateTimeOffset]::UtcNow.ToString('O'); sourceManifestSha256=(Get-FileHash (Join-Path $stage 'SOURCE-MANIFEST.json')).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllText((Join-Path $stage 'BUILD-METADATA.json'),($metadata | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $entries = Get-ChildItem $stage -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName).Hash.ToLowerInvariant(),$_.FullName.Substring($stage.Length+1).Replace('\','/')
    }
    [IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'),[string[]]$entries,[Text.UTF8Encoding]::new($false))
    if ($Runtime.StartsWith('win-')) { Compress-Archive -Path $stage -DestinationPath "$stage.zip" -Force; Get-FileHash "$stage.zip" }
    else {
        # GitHub's Linux runner has a native tar with predictable Unix mode handling;
        # avoid relying on PowerShell/.NET Tar API availability across pwsh versions.
        & chmod +x (Join-Path $stage 'LightHub.Desktop') (Join-Path $stage 'LightHub.Cli')
        if ($LASTEXITCODE -ne 0) { throw 'Failed to set Unix executable modes' }
        & tar -czf "$stage.tar.gz" -C (Split-Path $stage -Parent) (Split-Path $stage -Leaf)
        if ($LASTEXITCODE -ne 0) { throw 'Archive failed' }
        Get-FileHash "$stage.tar.gz"
    }
    Write-Output "Package directory: $stage"
} finally { Pop-Location }
