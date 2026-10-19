# Research: Jellyfin 10.11.11 Live TV internals

All paths are relative to `../jellyfin` at tag `v10.11.11`. Research only; nothing here has been
built or run unless stated in the "Verification status" section at the end.

## 1. Target framework / SDK

- Every server project targets `net9.0` (e.g. `Jellyfin.Server/Jellyfin.Server.csproj:11`,
  `MediaBrowser.Controller/MediaBrowser.Controller.csproj:39`).
- `global.json`: SDK `9.0.0`, `rollForward: latestMinor` -> any 9.0.x SDK.
- Plugin template (`../jellyfin-plugin-template`) targets `net9.0`, `targetAbi 10.11.0.0`, but
  references `Jellyfin.Controller` / `Jellyfin.Model` **10.11.5** -> bump to 10.11.11.
- **Need: .NET 9 SDK (arm64 on Apple Silicon).**

## 2. Live stream open: call path

Clients either call `POST /Items/{id}/PlaybackInfo` with `AutoOpenLiveStream=true`, or
PlaybackInfo followed by `POST /LiveStreams/Open`.

| Step | Location | User? |
|---|---|---|
| PlaybackInfo endpoint | `Jellyfin.Api/Controllers/MediaInfoController.cs:116-249` | **Yes**: `RequestHelpers.GetUserId(User, userId)` `:151-170` (`Jellyfin.Api/Helpers/RequestHelpers.cs:67-85`) |
| Item visibility check | `MediaInfoController.cs:171` -> `Emby.Server.Implementations/Library/LibraryManager.cs:1401-1406, 3355-3368` (`IsVisibleStandalone(user)`) | Yes |
| Media sources | `Jellyfin.Api/Helpers/MediaInfoHelper.cs:87-160` -> `Emby.Server.Implementations/Library/MediaSourceManager.cs:170` | Yes (`User` param) |
| | `MediaSourceManager.cs:290-319` -> `IMediaSourceProvider.GetMediaSources(item, ct)` | **No, user dropped** |
| | `src/Jellyfin.LiveTv/LiveTvMediaSourceProvider.cs:53-128` (OpenToken `:106-116`, prefixed `MediaSourceManager.cs:321`) -> `DefaultLiveTvService.cs:504-528` -> `TunerHosts/BaseTunerHost.cs:139-167` -> `TunerHosts/M3UTunerHost.cs:144-211` | No |
| LiveStreams/Open endpoint | `MediaInfoController.cs:269-306`, user into `LiveStreamRequest.UserId` `:286-291` | **Yes** |
| | `MediaInfoHelper.cs:405` -> `MediaSourceManager.OpenLiveStreamInternal` `MediaSourceManager.cs:499-563` | Yes (`request.UserId`) |
| | `MediaSourceManager.cs:510`: `provider.OpenMediaSource(keyId, currentLiveStreams, ct)` | **No, UserId not passed** (later only used for audio/sub defaults `:552-559`) |
| | `LiveTvMediaSourceProvider.cs:131-140` -> `GetChannelStream` `:259-307` (`GetItemById(id)` with no user, `:270`) | No |
| | `DefaultLiveTvService.GetChannelStreamWithDirectStreamProvider` `DefaultLiveTvService.cs:462-502` | No |
| | `BaseTunerHost.GetChannelStream` `BaseTunerHost.cs:171-223` -> `M3UTunerHost.GetChannelStream` `M3UTunerHost.cs:78-130` | No |

The user identity exists at the HTTP layer and inside `MediaSourceManager`, but not below the
`IMediaSourceProvider` boundary (`MediaBrowser.Controller/Library/IMediaSourceProvider.cs`).
`IHttpContextAccessor` is registered (`Jellyfin.Server/Startup.cs:63`), so code deeper in the
chain could read the ambient user. Fragile: DVR recordings have no HttpContext.

## 3. Plugin hooks

**DI order:** plugin `IPluginServiceRegistrator`
(`Emby.Server.Implementations/Plugins/PluginManager.cs:206-226`) runs inside `appHost.Init(services)`
(`Emby.Server.Implementations/ApplicationHost.cs:460-462`, `Jellyfin.Server/Program.cs:170`), which
runs *before* `Startup.ConfigureServices` adds Live TV (`Startup.cs:131` ->
`src/Jellyfin.LiveTv/Extensions/LiveTvServiceCollectionExtensions.cs:32-42`). So a plugin can
**add** `ITunerHost` / `ILiveTvService` (they are enumerables) but **cannot replace**
`ILiveTvManager` / `ITunerHostManager` (core registers last, last registration wins).

**(a) Per-user channel / guide filtering**
- Native: channels are library items queried with the user (`src/Jellyfin.LiveTv/LiveTvManager.cs:113-153`).
  User `BlockedTags` filter them in SQL (`MediaBrowser.Controller/Entities/InternalItemsQuery.cs:366-388`
  -> `Jellyfin.Server.Implementations/Item/BaseItemRepository.cs:2518-2524`) and also make them
  invisible for playback (`MediaBrowser.Controller/Entities/BaseItem.cs:1678-1684` via `IsVisibleStandalone`).
- Channel tags survive guide refresh: the M3U parser never sets `ChannelInfo.Tags`, and
  `src/Jellyfin.LiveTv/Guide/GuideManager.cs:399-407` only overwrites tags when they are non-null.
- **Correction (verified 2026-10-19):** programs on hidden channels are *not* returned. A program's
  parent is its channel (`GuideManager.cs:504-506`), visibility includes parent tags
  (`BaseItem.cs:1660-1676`), and `DtoService.GetBaseItemDtos` drops invisible items
  (`Emby.Server.Implementations/Dto/DtoService.cs:158`). That filter runs *after* the SQL query and
  paging, so pages come back short (`limit=5` returned 3) and `TotalRecordCount` still counts hidden
  programs (36 vs 18). Program endpoints: `LiveTvController.cs:566-649`, `LiveTvManager.cs:193-317`.
- Plugin fix: an ASP.NET Core MVC action filter (via `services.Configure<MvcOptions>`) sees
  `HttpContext.User` and can filter responses. Standard ASP.NET Core, not a Jellyfin API.

**(b) Intercept / veto stream open**
- No Jellyfin interface. Best option: the same action filter on `PlaybackInfo` and `LiveStreams/Open`.
- Closes a real hole: `LiveStreams/Open` never checks item visibility
  (`MediaInfoController.cs:269-306` -> `LiveTvMediaSourceProvider.cs:270`).
- Reactive backup: `ISessionManager.PlaybackStart` (`Emby.Server.Implementations/Session/SessionManager.cs:127`) has the user, but fires after playback starts.

**(c) Custom tuner host**
- Possible: `services.AddSingleton<ITunerHost, MyHost>()`; `TunerHostManager` takes `IEnumerable<ITunerHost>`
  (`src/Jellyfin.LiveTv/TunerHosts/TunerHostManager.cs:37-47`).
- But `ITunerHost.GetChannelStream` has no user (`BaseTunerHost.cs:171`).

**User identity:** action filter / middleware = yes (reliable); session events = yes (after the
fact); tuner host / media source provider = no (unless the `IHttpContextAccessor` workaround).

## 4. Concurrent streams and tuner assignment today

- Open streams: `MediaSourceManager._openStreams` (`MediaSourceManager.cs:59`). The limit check
  and the add happen under one global lock (`:60, :504-523`), so no race.
- Per-tuner limit: `M3UTunerHost.cs:80-91` counts open streams with the same `TunerHostId`
  against `TunerCount`. Stream records its tuner at `src/Jellyfin.LiveTv/TunerHosts/LiveStream.cs:39-42`.
- Sharing: same channel already open -> reuse and `ConsumerCount++` (`DefaultLiveTvService.cs:466-476`).
  Closed when the count hits 0 (`MediaSourceManager.cs:861-883`).
- Close triggers: `LiveStreams/Close` (`MediaInfoController.cs:314-320`), playback stop / session end
  (`SessionManager.cs:315-317, 325-358, 1090-1092`), transcode end (`MediaBrowser.Controller/Streaming/StreamState.cs:175`).
- **No user is stored on `ILiveStream`.** The only stream-to-user link is through session play
  state (`SessionManager.cs:839`).
- **Each channel maps to exactly one tuner.** M3U channel ID = `"m3u_" + MD5(tunerUrl) + MD5(streamUrl)`
  (`M3UTunerHost.cs:64-67`, `src/Jellyfin.LiveTv/TunerHosts/M3uParser.cs:104`; the tvg-id-based ID
  built at `:150-165` is overwritten). So "Shared Channel" from tuner 1 and from tuner 2 are two
  separate channel items, and there is no cross-tuner failover for M3U (`BaseTunerHost.cs:182-222`).

## 5. Recommendation: plugin, mostly native features

| Requirement | Mechanism |
|---|---|
| User C has no Live TV | Native `EnableLiveTvAccess` (`Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:80`, `LiveTvManager.cs:1216-1219`) |
| A sees only tuner 1, B only tuner 2, no duplicates | Tag channels per tuner (`livetv-tuner1` / `livetv-tuner2`); A blocks `livetv-tuner2`, B blocks `livetv-tuner1` |
| Streams routed to the user's tuner | Automatic: each channel item belongs to exactly one tuner |
| One stream per user | `TunerCount = 1` on each tuner (one user per tuner, so per-tuner = per-user) |

**Decision (user, 2026-10-19):** the same channel on two devices counts as one stream; keep
Jellyfin's native sharing.

Plugin adds: (1) auto-tag channels by tuner after each guide refresh (channels are re-created
when an M3U URL changes, losing tags); (2) user-to-tuner config page; (3) action filter that
strips hidden channels' programs from guide/program responses and vetoes `PlaybackInfo` /
`LiveStreams/Open` for channels outside the user's tuner; (4) optional "newest stream wins".

**Risks**
- Stale streams after a client crash hold the slot -> "M3U simultaneous stream limit has been
  reached" (`M3UTunerHost.cs:89`).
- DVR recordings consume the same tuner slot and are not user-scoped.
- `TunerCount > 0` disables direct play (`M3UTunerHost.cs:148`): all streams go through the server.
- Version-fragile: channel-ID format, tag-survival behaviour, response shapes. `Jellyfin.LiveTv`
  internals are not in the NuGet packages.
- `BlockedTags` applies library-wide, so tag names must be unique. Admins see both tuners.
- Local gotcha: with `TunerCount = 0` direct play is allowed and the browser tries to fetch
  `http://fakestreams/...` itself, which only resolves inside Docker.

## Gate 1 spec: hello-world plugin

- `src/Jellyfin.Plugin.LiveTvAccess/` from the template: `net9.0`, `Jellyfin.Controller` /
  `Jellyfin.Model` 10.11.11 (`ExcludeAssets=runtime`), new GUID, `targetAbi 10.11.0.0`.
- `src/LiveTvAccess.Core/`: empty `net9.0` library with no Jellyfin references.
- `Plugin : BasePlugin<PluginConfiguration>` logs on construction; an `IPluginServiceRegistrator`
  registers an `IHostedService` that logs `"LiveTvAccess started on Jellyfin {version}"`.
- Deploy: `dotnet build -c Release`, copy DLL + `meta.json` into
  `local/config/plugins/LiveTvAccess_0.1.0.0/` (versioned folders: `PluginManager.cs:705-733`),
  `docker compose restart jellyfin`.
- Pass: zero-warning build, Dashboard -> Plugins shows Active, log line in `docker logs jellyfin-dev`
  and `local/config/log/`. Then commit.

## Install on the Mac

- .NET 9 SDK, macOS arm64, latest 9.0.x (Microsoft installer or `dotnet-install.sh --channel 9.0`).
  Avoid `brew install --cask dotnet-sdk`, which may install .NET 10.
- Nothing else; Docker is already present. No need to build `../jellyfin`.

## Verification status (no-code experiment, 2026-10-19)

Script: `scripts/nocode_experiment.py` (local only). Setup: both tuners `TunerCount=1`, generated
XMLTV guide (`tuners/guide.xml`), channels tagged `livetv-tuner1` / `livetv-tuner2`, A blocks
`livetv-tuner2`, B blocks `livetv-tuner1`, C has Live TV disabled. Fixture URLs given `?ch=<tvg-id>`
suffixes because duplicate stream URLs inside one M3U collapse into one channel (`M3uParser.cs:104`).

| Check | A | B | C |
|---|---|---|---|
| Channel list | tuner1 only (3) | tuner2 only (3) | 403 |
| Guide (programs for visible channels) | tuner1 only | tuner2 only | 403 |
| Programs, no channel filter | tuner1 only (but short pages, total count leaks) | tuner2 only (same) | 403 |
| On Now | tuner1 only | tuner2 only | 403 |
| `PlaybackInfo` on a hidden channel | 404 | 404 | **200, and AutoOpen opens a stream** |
| `LiveStreams/Open` with hidden channel's openToken | **200, stream opened** | not tested | n/a |
| Tags after guide refresh | survive | survive | n/a |
| Same channel, 2 sessions, `.mp4` source | same LiveStreamId, consumer count 2; **provider saw 2 connections** (one ffmpeg each) | | |
| Same channel, 2 sessions, `.ts` source (production format) | same LiveStreamId, consumer count 2; **provider saw 1 connection** (3 of 3 runs) | | |
| Different tuner1 channel while one is playing | blocked: HTTP 500, `LiveTvConflictException` | | |

**Confirmed claims:** net9.0; channel item ID derivation (`M3UTunerHost.cs:64-67`,
`M3uParser.cs:104`, `LiveTvDtoService.cs:403-408`, `LibraryManager.cs:636-657`; computed IDs match
the live server); BlockedTags hide channels and 404 their PlaybackInfo; channel tags survive guide
refresh; TunerCount=1 blocks a second channel; Jellyfin shares the same channel's LiveStream.

**Corrected claims:**
- Program/guide leak: does not happen (see 3(a) correction); the remaining issues are short pages
  and leaked total counts.
- "Same channel = one upstream connection": true only for `.ts` / MP2T sources, **verified**.
  `SharedHttpStream` (`M3UTunerHost.cs:97-129`, `SharedHttpStream.cs:55-127`) makes one upstream GET
  (user-agent `Jellyfin-Server/10.11.11`), buffers it to `/cache/transcodes/<id>.ts`, and every
  session's ffmpeg reads Jellyfin's local `/LiveTv/LiveStreamFiles/<id>/stream.ts`. For other sources
  (`.mp4` verified; `.m3u8` expected) each session runs its own ffmpeg against the provider URL.
  Production streams are `.ts`, so the native-sharing decision holds.
- Sharing stops when the upstream download ends (`SharedHttpStream.cs:121-124`), so the `.ts`
  fixture (`scripts/make_ts_fixture.sh`) is served throttled to about real time by
  `fakestreams/default.conf`.

**New holes (plugin must close them):**
- Live TV access is only enforced on `/LiveTv/*` (`LiveTvController.cs:147`), not on
  `/Items/{id}/PlaybackInfo` or `/LiveStreams/Open`. A user with Live TV disabled who knows a
  channel ID can open a stream.
- `/LiveStreams/Open` never checks item visibility (`MediaInfoController.cs:269-306` ->
  `LiveTvMediaSourceProvider.cs:270`). A user who has a hidden channel's openToken can open it. A
  hand-built token needs the hidden channel's item ID, which the endpoints tested did not expose to
  A or C.
- The stream-limit error is a generic HTTP 500 (`BaseTunerHost.cs:222` -> `ExceptionMiddleware`).

**Still unverified (Gate 1 / later must confirm):**
- A plugin can add an MVC action filter via `services.Configure<MvcOptions>` from
  `IPluginServiceRegistrator`, and it runs for core controllers.
- `Jellyfin.Controller` / `Jellyfin.Model` 10.11.11 exist on NuGet (not checked: no remote access).
- Plugins can register `IStartupFilter` / middleware the same way.
- Sharing against the real production provider (the fake `.ts` provider confirmed one pull).
- Stale stream after a client crash: how long the slot stays held.
- Real clients (web, Android TV, iOS) use the same endpoints as the script (Gate 5).
