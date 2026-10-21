# LiveTvAccess: per-user Live TV tuners for Jellyfin

A Jellyfin server plugin that gives each user their own Live TV tuner. It works with the official Jellyfin apps and
needs no client changes.

Say you run one Jellyfin server with two M3U tuners (for example, two IPTV provider accounts):

- **User A** sees and plays only tuner 1's channels.
- **User B** sees and plays only tuner 2's channels.
- **User C** has no Live TV at all. This uses Jellyfin's own "Allow Live TV access" setting, so the plugin does
  nothing for it.
- Each user gets one clean channel list. Nobody sees duplicates from the other tuner.
- A user can't run two Live TV streams at once, so nobody can use both tuners.
- Admins are never restricted.

> **Status: v0.1.0, Jellyfin 10.11.11 only.** The plugin is pinned to that exact server version (see
> [Compatibility](#compatibility)). It has a large automated test suite and a live acceptance script, but it has
> not yet been confirmed with real Android TV and iOS clients (the plan is in
> [`docs/gate5-test-plan.md`](docs/gate5-test-plan.md)). Try it on a test server first.

## How it works

Jellyfin has no per-user tuner setting, so the plugin builds one from Jellyfin's own features and then closes the gaps:

1. **Tags channels by tuner.** After every guide refresh, each channel gets a `livetv-<tuner>` tag, based on which
   M3U tuner it came from.
2. **Blocks the other tuners' tags per user.** Each non-admin user's *Block items with tags* list is set so their
   channel list and guide only show their own tuner. The plugin only manages tags that start with `livetv-`.
3. **Default-deny request filter.** A global MVC action filter checks every non-admin request that names a Live TV
   channel, program, live stream or open token, including streaming and legacy HLS routes. Anything the plugin can't
   positively match to the caller's tuner is refused (403), so a hand-built request can't reach another tuner's
   stream. Responses are also scrubbed so non-admins don't see tuner URLs or provider credentials.
4. **One stream per user.** Each user owns one tuner, and you set that tuner's *simultaneous stream limit* to 1 in
   Jellyfin. A second stream gets HTTP 409 with "Your tuner is busy with another live stream". The same channel on
   two of the user's devices shares one stream.
5. **Fails closed.** An unconfigured plugin, or one that detects a different Jellyfin version, denies Live TV to
   non-admins until fixed. Admins and library playback (movies, music) are unaffected.

A startup **self-check** verifies the Jellyfin endpoint shapes the filter relies on, that each configured tuner exists
and has channels, and that no stream URL appears on two tuners. Its result is shown on the settings page and at
`GET /LiveTvAccess/Status` (admin only; it never returns URLs).

The design notes, with Jellyfin source file and line references for every hook the plugin uses, are in
[`docs/research.md`](docs/research.md).

## Compatibility

- **Jellyfin 10.11.11 only**, i.e. the official `jellyfin/jellyfin:10.11.11` image. The plugin relies on Jellyfin
  internals (endpoint argument names, response shapes, the channel ID scheme, the `RefreshGuide` task key). On any
  other version the self-check is expected to fail and Live TV is denied to non-admins. Don't upgrade Jellyfin
  before re-testing the plugin against the new version.
- M3U tuners only. Other tuner types such as HDHomeRun are not supported (the self-check rejects a tuner URL that
  isn't a Jellyfin M3U tuner).
- Built for `net9.0`.

## Install

You build the plugin yourself; there is no plugin repository or prebuilt download yet.

1. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
2. Build and package it:

   ```sh
   scripts/package_release.sh
   ```

   This creates `dist/LiveTvAccess_0.1.0.0/` (two DLLs and a `meta.json`) and `dist/LiveTvAccess_0.1.0.0.tar.gz`, and
   prints SHA-256 sums.
3. Copy the `LiveTvAccess_0.1.0.0` folder into your Jellyfin `plugins/` directory (for the Docker image, that is
   `/config/plugins/`, owned by the user Jellyfin runs as) and restart Jellyfin.
4. In Jellyfin, for each M3U tuner under *Dashboard > Live TV*, set **Simultaneous stream limit** to `1`.
5. Open *Dashboard > Plugins > LiveTvAccess* and configure it (below).

[`docs/deploy.md`](docs/deploy.md) is a full step-by-step runbook (pre-flight checklist, config pre-seeding so
there is no unprotected gap, backup, verification, troubleshooting and rollback). It was written for a Docker
Compose setup behind Caddy, so adjust the paths for your own layout.

## Configure

On the plugin's settings page:

- **Tuners:** give each tuner a key (`a-z`, `0-9`, `-`; `unassigned` is reserved) and its M3U path or URL, which
  must match the one in Jellyfin's Live TV settings exactly.
- **Users:** map each user to one tuner key. Users with no entry get no Live TV.
- **Status:** shows the self-check result, per-tuner channel counts, and a "Run check now" button.

Changes apply without a restart. After changing a tuner URL or playlist, run *Scheduled Tasks > Refresh Guide*.

Before you go live:

- **No stream URL may appear in more than one playlist.** Check with
  `python3 scripts/m3u_overlap.py tuner1.m3u tuner2.m3u` (standalone, standard library only, never prints URLs).
  Shared streams make users of different tuners interfere with each other.
- **Keep playlists short.** Jellyfin's guide refresh gets slow as channel counts grow (quadratic; about 13 hours
  projected at 16,000 channels). Import only the channels you want.
- Tuner URLs can contain provider credentials. The plugin hides them from non-admins, but keep them out of version
  control.

## Known limitations

- Admins see every tuner's channels.
- With a simultaneous stream limit above 0, Jellyfin disables direct play for Live TV, so all Live TV goes through
  the server.
- A crashed client can hold its tuner slot until Jellyfin cleans the stream up.
- DVR recordings use the same tuner slot and are not per-user.
- For a few seconds after a guide refresh, new channels can show in lists before they are tagged. Playback is still
  enforced during that time.
- Accepted risks (denial of service by device ID, raw logo URLs in image info) are documented in
  [`docs/research.md`](docs/research.md) under "Remaining gaps".

## Development

```sh
dotnet build
dotnet test --settings tests/default.runsettings
```

Layout:

| Path | What |
|---|---|
| `src/LiveTvAccess.Core` | Framework-agnostic policy: tuner catalog, user-to-tuner map, access decisions, ID parsing. No Jellyfin or ASP.NET references, so it can be reused (for example behind a reverse proxy). |
| `src/Jellyfin.Plugin.LiveTvAccess` | The plugin: request filter, tag sync, config page, self-check, status API. |
| `tests/` | xUnit unit tests for both projects. |
| `scripts/` | Packaging, local deploy, and live acceptance/scale tests against a local Jellyfin. |
| `docker-compose.yml`, `tuners/`, `fakestreams/` | A local Jellyfin 10.11.11 with two fake M3U tuners. |
| `docs/` | Design research, deployment runbook, real-client test plan, progress log. |
| `review/` | Findings from independent code reviews. |

### Local test stack

```sh
docker compose up -d                       # Jellyfin at http://localhost:8096, fake streams on :8081
cp .env.example .env                       # then add an API key from your LOCAL Jellyfin
scripts/deploy_local.sh                    # build, install into ./local/config/plugins, restart
python3 scripts/gate3_test.py              # live acceptance test (local only)
```

`test-media/` is gitignored, so on a fresh clone put two short H.264 clips there named `clip1.mp4` and `clip2.mp4`
(any small videos will do). Then `scripts/make_ts_fixture.sh` builds `live1.ts` from `clip1.mp4`, and
`scripts/library_fixture.py` builds the library fixture (both need `ffmpeg`). `scripts/gate3_test.py` creates test
users A, B and C and temporarily edits `tuners/tuner2.m3u`. Never point these scripts at a real server.

## Contributing

Issues and pull requests are welcome. Please run the unit tests first, and keep the plugin pinned to the Jellyfin
version it was verified against, with a note on any API that looks version-fragile.

## License

GPL-3.0, the same license as the Jellyfin plugin template. See [LICENSE](LICENSE).
