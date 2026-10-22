#!/bin/sh
# Build the plugin and deploy it to the LOCAL Jellyfin 12.2 stack (local12/config/plugins/), then restart it.
# 12.2 counterpart of deploy_local.sh (which targets the 10.11 stack). Never use against production.
set -eu
cd "$(dirname "$0")/.."

VERSION=0.1.0.0
NAME=LiveTvAccess
DEST="local12/config/plugins/${NAME}_${VERSION}"
OUT=src/Jellyfin.Plugin.LiveTvAccess/bin/Release/net10.0

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build src/Jellyfin.Plugin.LiveTvAccess -c Release

rm -rf "$DEST"
mkdir -p "$DEST"
cp "$OUT/Jellyfin.Plugin.LiveTvAccess.dll" "$OUT/LiveTvAccess.Core.dll" "$DEST/"

# Manifest schema: MediaBrowser.Common/Plugins/PluginManifest.cs. "assemblies" whitelists the DLLs to load;
# in 12.2 every whitelisted path must exist inside the plugin folder or the plugin is marked Malfunctioned
# (Emby.Server.Implementations/Plugins/PluginManager.cs:886-935, TryGetPluginDlls).
# targetAbi must be <= the server version (PluginManager.cs:745-755): 12.2.0.0 for Jellyfin 12.2.
cat > "$DEST/meta.json" <<EOF
{
  "category": "Live TV",
  "changelog": "Local development build.",
  "description": "Per-user Live TV tuner access.",
  "guid": "4d5aa36c-9164-4f4d-ba7f-0fa6c25bfd28",
  "name": "${NAME}",
  "overview": "Per-user Live TV tuner access",
  "owner": "local",
  "targetAbi": "12.2.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": ["Jellyfin.Plugin.LiveTvAccess.dll", "LiveTvAccess.Core.dll"]
}
EOF

docker compose -f docker-compose.12.yml restart jellyfin
echo "Deployed to $DEST and restarted jellyfin-dev-12."
