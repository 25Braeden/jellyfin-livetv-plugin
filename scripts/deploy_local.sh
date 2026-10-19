#!/bin/sh
# Build the plugin and deploy it to the LOCAL Jellyfin stack (local/config/plugins/), then restart it.
# Never use this against production; deployment there is manual.
set -eu
cd "$(dirname "$0")/.."

VERSION=0.1.0.0
NAME=LiveTvAccess
DEST="local/config/plugins/${NAME}_${VERSION}"
OUT=src/Jellyfin.Plugin.LiveTvAccess/bin/Release/net9.0

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build src/Jellyfin.Plugin.LiveTvAccess -c Release

rm -rf "$DEST"
mkdir -p "$DEST"
cp "$OUT/Jellyfin.Plugin.LiveTvAccess.dll" "$OUT/LiveTvAccess.Core.dll" "$DEST/"

# Manifest schema: MediaBrowser.Common/Plugins/PluginManifest.cs. "assemblies" whitelists the DLLs to load.
cat > "$DEST/meta.json" <<EOF
{
  "category": "Live TV",
  "changelog": "Local development build.",
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

docker compose restart jellyfin
echo "Deployed to $DEST and restarted jellyfin-dev."
