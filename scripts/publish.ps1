param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet run --project tests/Glance.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    dotnet publish src/Glance.Desktop -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "dist/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Get-FileHash "dist/$Runtime/Glance.exe" -Algorithm SHA256 | Format-List
} finally { Pop-Location }
