[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sdks = dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.\d+\.\d+' })) {
    throw 'ABC Converter requires an installed .NET 10 SDK.'
}
dotnet restore "$PSScriptRoot\..\ABC.Converter.sln"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
