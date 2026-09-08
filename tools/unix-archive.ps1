param([Parameter(Mandatory=$true)][string]$Source, [Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Formats.Tar
$sourceDirectory = Get-Item -LiteralPath $Source
$output = [IO.File]::Open($Destination, [IO.FileMode]::Create)
$gzip = [IO.Compression.GZipStream]::new($output, [IO.Compression.CompressionLevel]::Optimal, $false)
$writer = [System.Formats.Tar.TarWriter]::new($gzip, $true)
try {
    $directories = @($sourceDirectory) + @(Get-ChildItem -LiteralPath $sourceDirectory.FullName -Directory -Recurse)
    foreach ($directory in $directories) {
        $relative = [IO.Path]::GetRelativePath($sourceDirectory.FullName, $directory.FullName).Replace('\','/')
        $name = if ($relative -eq '.') { $sourceDirectory.Name } else { $sourceDirectory.Name + '/' + $relative }
        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, $name + '/')
        $entry.Mode = [IO.UnixFileMode]0x1ED
        $writer.WriteEntry($entry)
    }
    foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory.FullName -File -Recurse | Sort-Object FullName) {
        if ($file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Archive does not follow linked files' }
        $name = $sourceDirectory.Name + '/' + [IO.Path]::GetRelativePath($sourceDirectory.FullName, $file.FullName).Replace('\','/')
        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $name)
        $entry.Mode = [IO.UnixFileMode]$(if ($file.Name -in @('LightHub.Desktop','LightHub.Cli')) { 0x1ED } else { 0x1A4 })
        $entry.ModificationTime = [DateTimeOffset]$file.LastWriteTimeUtc
        $inputStream = [IO.File]::OpenRead($file.FullName)
        try { $entry.DataStream = $inputStream; $writer.WriteEntry($entry) } finally { $inputStream.Dispose() }
    }
} finally { $writer.Dispose(); $gzip.Dispose(); $output.Dispose() }
