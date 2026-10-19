# Jellyfin Live TV per-user access plugin

## Goal
One Jellyfin instance (10.11.11). Two M3U tuners. Per-user behavior, seamless in the official apps:
- User A only gets tuner 1, User B only gets tuner 2.
- User C has Live TV hidden entirely (native Jellyfin setting, no code needed).
- Channels are deduplicated: each user sees one clean list, no duplicates.
- A user can never run two concurrent live streams (no using both tuners).

## Approach
Primary: a Jellyfin server plugin (C#) running inside the server, which has real user context.
Fallback: a reverse-proxy layer in front of Jellyfin (production uses Caddy). Core logic
(merge/dedupe channels, user-to-tuner map, one-stream-per-user) should be kept in a separate,
framework-agnostic module so it can be reused if we fall back.

## Decision gates (stop and report at each one)
1. Hello-world plugin builds, loads in local Jellyfin, logs appear.
2. IDENTIFY THE USER: confirm which hook gives us the user identity when a Live TV
   stream is opened (and when channel/guide lists are requested). If no reliable hook
   exists, STOP and recommend the proxy fallback.
3. Per-user channel filtering with two test tuners.
4. Tuner routing per user + one-stream-per-user enforcement.
5. Test with real clients (web, Android TV, iOS).

## Environment
- Target: Jellyfin 10.11.11 (official Docker image jellyfin/jellyfin:10.11.11).
- Jellyfin source is cloned next to this project at ../jellyfin, checked out at tag v10.11.11.
  Plugin template is at ../jellyfin-plugin-template.
- Confirm the required .NET SDK version from the source's target framework BEFORE
  installing anything. (Believed to be .NET 9; verify.)
- Local test stack: `docker compose up -d` (see docker-compose.yml). Local Jellyfin is at
  http://localhost:8096. Fake tuners live in ./tuners, fake streams are served by the
  `fakestreams` container from ./test-media.
- Plugins are loaded from ./local/config/plugins/<PluginName>_<version>/ in the local stack.
  Restart the jellyfin container after changing the plugin.

## Hard rules
- NEVER connect to, SSH into, or modify the production VPS. All work is local.
  Deployment is done manually by the user.
- Do not store secrets, API keys, or tokens in the repo. Use a gitignored .env file.
- Commit to git after each working milestone.
- Prefer reading Jellyfin source over guessing. Cite file paths and line numbers for any
  hook or API you rely on.
- Pin everything to 10.11.11. Note any API that looks version-fragile.

## Production facts (for deployment notes only, do not act on them)
- Jellyfin runs in Docker Compose (stack at /opt/media-server on the VPS), user 1000:1000.
- Host config dir /opt/media-server/jellyfin/config maps to /config in the container, so
  plugins go in /opt/media-server/jellyfin/config/plugins/ and must be owned by uid 1000.
- Caddy (container jellyfin-caddy) is the HTTPS reverse proxy in front of Jellyfin.
