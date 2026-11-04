[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $sdkVersion = dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.0\.\d+$') {
        throw 'ABC Converter requires a stable .NET 10 SDK compatible with global.json.'
    }

    dotnet restore "$repoRoot\ABC.Converter.sln"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
