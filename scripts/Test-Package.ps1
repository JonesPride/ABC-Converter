# Requires PowerShell 7 on Windows x64 (used by GitHub Actions).
#Requires -Version 7.0
[CmdletBinding()]
param([string]$Archive = (Join-Path $PSScriptRoot '..\artifacts\ABC-Converter-win-x64.zip'))

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or ![Environment]::Is64BitProcess) { throw 'Package verification requires Windows x64 PowerShell.' }
$Archive = (Resolve-Path $Archive).Path
$expectedHash = ((Get-Content "$Archive.sha256" -Raw).Trim() -split '\s+')[0]
if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash $Archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Package SHA-256 mismatch.'
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $Archive -DestinationPath $stage
    foreach ($name in @('ABC.Converter.App.exe', 'ABC.Converter.App.dll', 'ABC.Converter.App.deps.json',
        'ABC.Converter.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'PresentationFramework.dll', 'libSkiaSharp.dll', 'e_sqlite3.dll', 'LICENSE', 'README.txt', 'ThirdPartyLicenses')) {
        if (!(Test-Path (Join-Path $stage $name))) { throw "Missing packaged dependency: $name" }
    }
    $config = Get-Content (Join-Path $stage 'ABC.Converter.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks = @($config.runtimeOptions.includedFrameworks.name)
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if ($frameworks -notcontains $name) { throw "Runtime is not self-contained: $name" }
    }
    foreach ($entry in @(
        @{ File = 'libSkiaSharp.dll'; Export = 'sk_version_get_milestone' },
        @{ File = 'e_sqlite3.dll'; Export = 'sqlite3_libversion_number' }
    )) {
        $handle = [Runtime.InteropServices.NativeLibrary]::Load((Join-Path $stage $entry.File))
        try {
            $export = [Runtime.InteropServices.NativeLibrary]::GetExport($handle, $entry.Export)
            if ($export -eq [IntPtr]::Zero) { throw "Missing native export in $($entry.File)" }
            Write-Host "Loaded packaged native library: $($entry.File)"
        }
        finally { [Runtime.InteropServices.NativeLibrary]::Free($handle) }
    }
    Write-Host 'Package verification passed: checksum, bundled runtimes, and native libraries.'
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
