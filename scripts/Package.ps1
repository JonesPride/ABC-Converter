[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts ([guid]::NewGuid().ToString('N'))
$zip = Join-Path $artifacts 'ABC-Converter-win-x64.zip'
$project = Join-Path $root 'src\ABC.Converter.App\ABC.Converter.App.csproj'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $stage -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

    Copy-Item (Join-Path $root 'LICENSE') $stage
    Copy-Item (Join-Path $root 'docs\portable-readme.txt') (Join-Path $stage 'README.txt')

    # Retain package license metadata and bundled notices, including runtime packs.
    $assets = Get-Content (Join-Path $root 'src\ABC.Converter.App\obj\project.assets.json') -Raw | ConvertFrom-Json
    $packages = @{}
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'package') { $packages[$library.Name.ToLowerInvariant()] = $true }
    }
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($dependency in $framework.Value.downloadDependencies) {
            $bounds = $dependency.version.Trim('[', ']', ' ').Split(',')
            $version = $bounds[0].Trim()
            if ($bounds.Count -gt 1 -and $version -ne $bounds[1].Trim()) {
                throw "Expected an exact runtime-pack version: $($dependency.version)"
            }
            $packages[("$($dependency.name)/$version").ToLowerInvariant()] = $true
        }
    }
    $notices = Join-Path $stage 'ThirdPartyLicenses'
    foreach ($package in $packages.Keys) {
        $found = $false
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $source = Join-Path $folder $package
            if (!(Test-Path $source)) { continue }
            $found = $true
            foreach ($file in Get-ChildItem $source -Recurse -File) {
                if ($file.Name -match '(?i)(license|licence|notice|copyright)|\.nuspec$') {
                    $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
                    $destination = Join-Path (Join-Path $notices $package) $relative
                    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
                    Copy-Item $file.FullName $destination
                }
            }
            break
        }
        if (!$found) { throw "Cannot locate restored package $package for notices." }
    }
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
    Write-Host "Packaged $zip (SHA-256 $hash)"
}
finally {
    Remove-Item $stage -Recurse -Force
}
