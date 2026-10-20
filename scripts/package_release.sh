#!/bin/sh
# Build the plugin and package it for a MANUAL production deploy (see docs/deploy.md).
# Output: dist/LiveTvAccess_0.1.0.0/ and dist/LiveTvAccess_0.1.0.0.tar.gz. Touches nothing outside this project.
set -eu
cd "$(dirname "$0")/.."

VERSION=0.1.0.0
NAME=LiveTvAccess
DIR="${NAME}_${VERSION}"
OUT=src/Jellyfin.Plugin.LiveTvAccess/bin/Release/net9.0

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build src/Jellyfin.Plugin.LiveTvAccess -c Release

rm -rf "dist/$DIR" "dist/$DIR.tar.gz"
mkdir -p "dist/$DIR"
cp "$OUT/Jellyfin.Plugin.LiveTvAccess.dll" "$OUT/LiveTvAccess.Core.dll" "dist/$DIR/"

# Manifest schema: MediaBrowser.Common/Plugins/PluginManifest.cs. "assemblies" whitelists the DLLs to load.
cat > "dist/$DIR/meta.json" <<EOF
{
  "category": "Live TV",
  "changelog": "Release build $(git rev-parse --short HEAD).",
  "description": "Per-user Live TV tuner access.",
  "guid": "4d5aa36c-9164-4f4d-ba7f-0fa6c25bfd28",
  "name": "${NAME}",
  "overview": "Per-user Live TV tuner access",
  "owner": "local",
  "targetAbi": "10.11.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": ["Jellyfin.Plugin.LiveTvAccess.dll", "LiveTvAccess.Core.dll"]
}
EOF

# No macOS metadata / xattrs in the archive (avoids warnings and ._ files when unpacked with GNU tar).
COPYFILE_DISABLE=1 tar --no-mac-metadata --no-xattrs -C dist -czf "dist/$DIR.tar.gz" "$DIR"
( cd dist && shasum -a 256 "$DIR.tar.gz" "$DIR"/* )
echo "Packaged dist/$DIR.tar.gz"
