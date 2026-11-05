#!/bin/sh
# Build a versioned manual-deploy tarball and a Jellyfin catalog ZIP.
set -eu
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build src/Jellyfin.Plugin.LiveTvAccess -c Release
python3 scripts/package_artifact.py
