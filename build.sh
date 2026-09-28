#!/bin/sh
set -eu
cd "$(dirname "$0")"
command -v dotnet >/dev/null 2>&1 || { echo 'Install the .NET 10 SDK first.' >&2; exit 1; }
dotnet run --project ServerBrowser.Tests -c Release
dotnet publish ServerBrowser.Desktop -c Release -r linux-x64 --self-contained true \
  -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/linux-x64
dotnet run --project packaging/Packager.csproj -c Release -- artifacts/linux-x64 artifacts/packages .
