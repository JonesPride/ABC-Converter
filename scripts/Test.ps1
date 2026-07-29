[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$results = Join-Path $PSScriptRoot '..\TestResults'
New-Item -ItemType Directory -Force -Path $results | Out-Null
dotnet test "$PSScriptRoot\..\ABC.Converter.sln" `
    --configuration $Configuration `
    --no-build `
    --logger "trx;LogFileName=abc-converter.trx" `
    --results-directory $results
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
