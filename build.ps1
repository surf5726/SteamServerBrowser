param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $Dotnet run --project ServerBrowser.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    & $Dotnet publish ServerBrowser.Desktop -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/linux-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    & $Dotnet run --project packaging/Packager.csproj -c Release -- artifacts/linux-x64 artifacts/packages .
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed' }
} finally { Pop-Location }
