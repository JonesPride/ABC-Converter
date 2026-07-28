[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
dotnet test "$PSScriptRoot\..\ABC.Converter.sln" --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
