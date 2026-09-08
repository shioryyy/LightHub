$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$stage = Join-Path $root ('artifacts/source-' + [Guid]::NewGuid().ToString('N') + '/LightHub')
New-Item -ItemType Directory -Force $stage | Out-Null
$directories = @('src','tests','tools','docs','packaging','third-party','.github')
foreach ($directory in $directories) {
    Get-ChildItem (Join-Path $root $directory) -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length+1)
        $target = Join-Path $stage $relative
        New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
        Copy-Item $_.FullName $target
    }
}
Get-ChildItem $root -File -Force | Where-Object { $_.Name -notmatch '\.(zip|lhbackup|log)$' } | ForEach-Object { Copy-Item $_.FullName $stage }
$zip = Join-Path $root "dist/LightHub-$version-source.zip"
New-Item -ItemType Directory -Force (Split-Path $zip -Parent) | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($zip, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File -Force | ForEach-Object {
        $entry = 'LightHub/' + $_.FullName.Substring($stage.Length+1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }
Get-FileHash $zip
