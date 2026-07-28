[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sdk = dotnet --version
if (-not $sdk.StartsWith('8.')) {
    throw "ABC Converter requires the .NET 8 SDK. Found: $sdk"
}
dotnet restore "$PSScriptRoot\..\ABC.Converter.sln"
