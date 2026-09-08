param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$assets = Get-Content (Join-Path $root 'src/LightHub.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$inventory = @()
foreach ($property in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    if ($property.Value.type -ne 'package') { continue }
    $parts = $property.Name.Split('/')
    $folder = Join-Path $packageRoot ($parts[0].ToLowerInvariant() + '/' + $parts[1])
    $spec = Get-ChildItem $folder -Filter '*.nuspec' | Select-Object -First 1
    if (-not $spec) { throw "Missing license metadata for $($property.Name)" }
    [xml]$xml = Get-Content $spec.FullName -Raw
    $metadata = $xml.package.metadata
    $itemDir = Join-Path $OutputDirectory ($parts[0] + '-' + $parts[1])
    New-Item -ItemType Directory -Force $itemDir | Out-Null
    Copy-Item $spec.FullName $itemDir -Force
    $licenseFiles = Get-ChildItem $folder -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING|THIRD.PARTY)' }
    $index = 0
    foreach ($file in $licenseFiles) { Copy-Item $file.FullName (Join-Path $itemDir ("$index-" + $file.Name)) -Force; $index++ }
    $license = if ($metadata.license) { $metadata.license.InnerText } else { [string]$metadata.licenseUrl }
    $inventory += [PSCustomObject]@{ name=$parts[0]; version=$parts[1]; license=$license; projectUrl=[string]$metadata.projectUrl; sha512=$property.Value.sha512; licenseFiles=$licenseFiles.Count }
}
$text = ConvertTo-Json -InputObject $inventory -Depth 5
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'dependencies.json'),$text,[Text.UTF8Encoding]::new($false))
