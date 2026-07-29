[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot\..\src\ABC.Converter.App\ABC.Converter.App.csproj"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
