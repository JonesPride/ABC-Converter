[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sdks = dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^8\.\d+\.\d+' })) {
    throw 'ABC Converter requires an installed .NET 8 SDK.'
}
dotnet restore "$PSScriptRoot\..\ABC.Converter.sln"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
