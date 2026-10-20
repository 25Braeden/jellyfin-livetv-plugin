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

**Verified at Gate 1 / Gate 2 (2026-10-19):**
- `Jellyfin.Controller` / `Jellyfin.Model` 10.11.11 exist on nuget.org; the plugin pins them.
- Plugin loads (Active), `IHostedService` registered from `IPluginServiceRegistrator` runs.
- A global MVC action filter added with `services.Configure<MvcOptions>(o => o.Filters.Add<T>())` from
  `IPluginServiceRegistrator` runs on core controllers (`MediaInfoController`, `LiveTvController`) and
  sees the authenticated user via the `Jellyfin-UserId` claim (`CustomAuthenticationHandler.cs:64`).
  Logged the right user for A, B and C on PlaybackInfo, LiveStreams/Open and LiveTv/Channels.
- Requests rejected by authorization (C on `/LiveTv/*`, 403) never reach action filters.
- API-key requests have an empty user ID and `Jellyfin-IsApiKey=True`: Gate 3 must let them through.

**Version-fragile details the plugin now depends on:** claim names `Jellyfin-UserId` /
`Jellyfin-IsApiKey` (`Jellyfin.Api/Constants/InternalClaimTypes.cs`, not on NuGet); route templates
`Items/{itemId}/PlaybackInfo`, `LiveStreams/Open`, `LiveTv/Channels`; action argument names `itemId`,
`openToken`, `openLiveStreamDto`; `OpenLiveStreamDto` read by reflection (not on NuGet).

**Still unverified:**
- Plugins can register `IStartupFilter` / middleware the same way (not needed so far).
- Sharing against the real production provider (the fake `.ts` provider confirmed one pull).
- Stale stream after a client crash: how long the slot stays held.
- Real clients (web, Android TV, iOS) use the same endpoints as the scripts (Gate 5).

## Gate 3: enforcement (2026-10-19)

Acceptance test: `scripts/gate3_test.py` (42/42 pass). Config is set through Jellyfin's plugin
config API (`POST /Plugins/{id}/Configuration`), the same path a settings page will use:

```json
{"Tuners": [{"Key": "tuner1", "Url": "/tuners/tuner1.m3u"}, {"Key": "tuner2", "Url": "/tuners/tuner2.m3u"}],
 "Users":  [{"UserName": "A", "TunerKey": "tuner1"}, {"UserName": "B", "TunerKey": "tuner2"}]}
```

**Design**
- Core (`src/LiveTvAccess.Core`): `TunerCatalog` (channel ExternalId / tuner URL -> tuner key),
  `UserTunerMap` (user -> tuner, blocked tags), `AccessPolicy` (admins and API keys pass; unmapped
  users get nothing; unknown tuner = deny), `JellyfinIds` (Jellyfin's MD5 ID scheme, open-token
  parsing), `Redact` (short SHA-256 for logs).
- `LiveTvAccessFilter` (global MVC action filter) checks every authenticated non-admin request for
  anything naming Live TV: `itemId` / `channelId` / `programId` arguments, `liveStreamId`, `openToken`,
  and the same fields in request bodies. Denies with a plain-text 403 before the action runs.
  Translates `LiveTvConflictException` into a plain-text 409 "tuner busy".
- `TagSyncService` tags every channel `livetv-{tuner}` (or `livetv-unassigned`) and sets each
  non-admin user's BlockedTags, at startup, after every `RefreshGuide` task, on plugin config
  change and on user update. Tags the plugin doesn't manage (no `livetv-` prefix) are kept.

**Third hole found and closed:** streaming endpoints (`/Videos/{itemId}/stream*`, HLS) look the item
up with no user (`Jellyfin.Api/Helpers/StreamingHelpers.cs:111, 130`). Without a `liveStreamId`, the
transcoder opens the tuner itself (`MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs:662-669`).
Verified: an API-key request (which the plugin lets through) to `/Videos/{channel}/stream.ts?mediaSourceId=...`
opened the tuner and streamed. The filter now blocks this for users without that tuner.

| Gate 3 check | Result |
|---|---|
| Plugin tags channels and sets blocked tags (A, B, C), no script | pass |
| Channel list / On Now: A tuner1 only, B tuner2 only, C 403, API key all | pass |
| PlaybackInfo (POST and GET) on another tuner's channel | 403 "This channel is not available for your account." |
| C PlaybackInfo + AutoOpen (hole 1) | 403 "Live TV is not available for your account." |
| A LiveStreams/Open with a tuner-2 token: real, hand-built, query string (hole 2) | 403 |
| Malformed Live TV open token | 403 (fail closed) |
| `/Videos/{other tuner}/stream.ts`, `master.m3u8` without a live stream (hole 3) | 403, no upstream request |
| A streams or closes B's live stream id | 403 |
| A second channel while A's tuner is busy | 409 "Your tuner is busy with another live stream. Stop it and try again." |
| A same channel from 2 sessions | allowed, one shared stream |
| A plays every tuner-1 channel (HLS master, playlist, first segment) | pass |
| API key: PlaybackInfo / LiveStreams/Open on any tuner | 200 |
| Stream URL change re-creates a channel; plugin re-tags it after guide refresh | pass |
| Plugin logs: no full open tokens, short hash only | pass |

**Not tested live:** an interactive admin login (the local admin password is not available to the
scripts). Admin pass-through is covered by Core unit tests (`AccessPolicy_Decisions`) and by the
API-key path, which takes the same branch.

**Known gaps / risks**
- Between a guide refresh creating channels and the plugin re-tagging them (seconds), new channels
  are briefly visible to all users in channel lists. Playback is still enforced, because the filter
  resolves the tuner from the channel's ExternalId, not from tags.
- Empty or invalid plugin config = fail closed: all non-admin Live TV access is blocked. On first
  install in production, configure the plugin before users notice.
- `GET /LiveTv/LiveStreamFiles/{uniqueId}/stream.ts` (`LiveTvController.cs:1180-1195`) requires no login
  (an anonymous request returns 404, not 401). It serves the shared `.ts` buffer to anyone holding the
  random stream id, which only the opener receives in its media source. The filter skips anonymous
  requests, so it doesn't cover this. Consider blocking it at Caddy for non-local clients.
- The tuner-busy check is still Jellyfin's per-tuner limit (one user per tuner). Per-user limits
  across tuners are Gate 4.
- More version-fragile details: argument names `itemId` / `channelId` / `programId` / `liveStreamId` /
  `openToken`; request body property names `ItemId` / `LiveStreamId` / `OpenToken` (types in `Jellyfin.Api`);
  `RefreshGuide` task key; `Jellyfin.LiveTv.LiveTvMediaSourceProvider` type name (open-token prefix).

## Gate 3.5 phase 2, fix 1: map users by id (2026-10-19)

- Config entries now carry `UserId`. `Plugin.UpdateConfiguration` (the path Jellyfin's
  `POST /Plugins/{id}/Configuration` uses) resolves `UserName` to an id once, at save time, and stores it.
  Access is decided by id only; `GetUserByName` is no longer called at decision time.
- `UserTunerMap.FromEntries` (Core) skips, with a logged warning, any entry that is null, has no id,
  names an invalid/unknown tuner key, or points at a deleted user. Duplicates for the same tuner are
  merged with a warning; conflicting entries for one user deny only that user. Other users are unaffected.
- **Upgrade note:** a config saved before this change has names but no ids, so those users get no tuner
  until the config is saved again (fail closed, logged as "has no user id"). No automatic name lookup
  at load time, by design. After deploying, re-save the plugin config once.

## Gate 3.5 phase 2, fix 2: tag-sync worker lifecycle (2026-10-19)

- **Startup:** `StartAsync` only subscribes and starts one worker on the thread pool; the first sync
  no longer runs on Jellyfin's host-start thread (verified in the log: "waiting" on the host thread,
  the pass on a worker thread, both before "Startup complete"). Until the first full pass (users and
  channels) succeeds, `IsReady` is false and the filter denies protected operations for non-admins
  (`AccessDecision.DenyNotReady`, "Live TV is starting up"). Failed passes retry with backoff
  (2 s doubling to 1 min) and stay closed.
- **One worker, coalesced flags:** triggers only set "users dirty" / "channels dirty" and wake the
  worker; any burst becomes at most one more pass. The service doesn't subscribe to `OnUserUpdated`, and
  `UpdateUserAsync` publishes nothing, so its own user writes never queue a pass.
- **Shutdown:** `StopAsync` cancels the worker and awaits it (bounded by the host's shutdown token);
  `Dispose` is idempotent and safe after stop.
- **Paging:** channel ids are fetched once (`ILibraryManager.GetItemIds`), then loaded and retagged
  500 at a time. A user change that arrives mid-pass is applied after the current page, not after the
  whole channel pass.
- **Re-sync triggers chosen:**
  - user created / deleted: `IEventConsumer<UserCreatedEventArgs>` / `<UserDeletedEventArgs>`, published at
    `UserManager.cs:322` / `:373` and resolved from DI (`EventManager.cs:46-52`). Verified live.
  - admin policy edits: **no Jellyfin event exists** (`UserManager.UpdatePolicyAsync`, `UserManager.cs:803`,
    publishes nothing). The filter marks users dirty after any successful admin/API-key write to
    `/Users/*` (excluding authentication). Verified live: clearing A's blocked tags via
    `POST /Users/{id}/Policy` is undone within seconds.
  - backstop: a periodic users-only pass every 15 minutes, for edits that bypass the HTTP API.
- **Bug found while testing:** the channel pass set `Tags` on Jellyfin's cached item before saving. If the
  save failed, the cached item looked tagged, later passes skipped it, and the tag was never persisted.
  Fixed by restoring the old tags on failure; regression test fails without the fix.

## Scale test: 16,000 channels (2026-10-19)

Setup: `scripts/scale_test.py gen` writes `tuners/scale/tuner{1,2}.m3u` (gitignored), 8,000 channels per
tuner, all stream URLs unique, ~40% of the lineup shared between the two tuners, groups spread evenly.
Docker Desktop on an arm64 Mac (14 CPUs, 7.7 GiB for the VM). Plugin build with timing logs.

| Measurement | Result |
|---|---|
| Plugin: first sync of 16,000 untagged channels | **52.9 s**, 33 pages of 500; container memory 438 → 453 MiB peak |
| Plugin: re-sync, nothing changed | **0.57–0.59 s** (users ~13 ms, channels ~570 ms) |
| Plugin: re-sync after 1,000 stream URLs changed | **5.6 s**, 1,000 channels retagged (35 pages); memory peak 580 MiB |
| Restart to ready (~17,000 channels) | Plugin's first sync completes 2.8–3.1 s after restart, before Jellyfin is healthy; A and B get PlaybackInfo 200 at 7.2–7.6 s (the moment Jellyfin is healthy); no request was ever denied as "starting up" |
| Jellyfin's own startup | "Startup complete" 6.96–6.98 s vs 6.56 s with 7 channels; the plugin's sync runs on a worker thread, so it doesn't delay startup |
| Filter check on PlaybackInfo (own cost) | median **~2.2 ms**, p95 2.7–3.2 ms, worst 5.6 ms (allowed and denied alike; n=200 per case) |
| PlaybackInfo end-to-end | allowed: median 10.4–11.0 ms; denied: median 7.6 ms; two early outliers (154 ms, 327 ms) after a restart, consistent with warm-up |
| Channel list, A and B (8,500 channels each) | first page of 100: **55 ms**; full list: **~394 ms**; only the user's own tuner |
| Guide grid / On Now | Not meaningful: no guide data was loaded at this size (see below) |
| Gate 3 acceptance script at this size | **Not measured** (needs two full guide refreshes; impractical at this size) |

**Jellyfin's guide refresh is quadratic in the number of channels; this is Jellyfin, not the plugin.**
Channel creation is fast (16,007 channels in ~36 s). The per-channel guide phase is the problem: for each
channel, `GuideManager.cs:220-236` calls `DefaultLiveTvService.GetProgramsAsync`, which rebuilds the full
channel list from both tuners and re-applies guide matching to every channel
(`DefaultLiveTvService.cs:448-455`, `ListingsManager.cs:171-184`). The XMLTV provider also re-reads the whole
guide file per channel (`XmlTvListingsProvider.cs:138-151`). Measured:
- full 13 MB guide (12,801 guide channels, 76,806 programmes): 17.9% after 2 h 24 min (~13 h projected); cancelled
- 5 KB guide (6 channels): 18.5% after ~30 min; cancelled after 80 min
- no guide provider at all: 25% after ~17 min, still running; it ignored cancellation for 10+ minutes and was
  ended by a restart

The plugin re-tags channels only after a guide refresh completes (that's when Jellyfin creates or re-creates
channels), so at this size new channels could wait hours for their tags. Playback is still enforced in the
meantime, because the filter resolves the tuner from the channel itself, not from tags.

Restoring the 7-channel fixtures afterwards: the refresh that deleted ~17,000 channel items took ~4 min 49 s;
the next refresh took 4 s. Gate 3 acceptance then passed 45/45.

**Options (no design change made):**
1. **Import only wanted channels** (filter the M3U before Jellyfin sees it). The quadratic cost makes this the
   main lever; a few hundred channels per tuner is fast.
2. Keep the guide provider limited to the channels in use, and run the guide refresh off-peak.
3. Consider the reverse-proxy layer or an M3U-filtering proxy in front of the provider for curation.

## Gate 3.5 phase 2, fix 3: snapshots, reserved key, tag ownership, API-key rule (2026-10-19)

- **One snapshot per decision and per pass:** `AccessService.Snapshot` is an immutable `AccessSnapshot`
  (catalog, map, owned tags, newly claimed tags, version), rebuilt only after `Invalidate()`. The filter takes
  it once per request; a sync pass takes it once and, if the version changes mid-pass, stops between pages and
  redoes the work with the new snapshot (no backoff, not counted towards "ready"). The CG5 race test (12,976 of
  20,000 wrong allows before) now has 0.
- **Reserved key:** `unassigned` (any case) is rejected by `TunerTags.NormalizeKey`. Tuner entries are now
  built with `TunerCatalog.FromEntries`: a null, keyless, reserved, or URL-less entry is skipped with a warning,
  and keys or URLs used twice skip every entry involved. Other tuners are unaffected (before, any bad tuner entry
  blocked everyone).
- **Tag ownership:** the plugin owns exactly `livetv-{configured key}`, `livetv-unassigned`, and every tag it
  applied before (`OwnedTagHistory`, kept in the plugin config, merged back in on every config save, saved
  without raising ConfigurationChanged). `TagOwnership.Merge` replaces owned tags and keeps everything else.
  Other `livetv-*` tags are left untouched and reported (warning when the set changes). A tag the plugin claims
  for the first time that was already present is reported once; after upgrading from fix 2 this fires once for
  the plugin's own earlier tags (seen locally, not repeated after restart).
- **One API-key rule:** `AccessPolicy.IsExempt` (admins, and API keys without a user) is used by the filter, by
  `Decide`, and by the admin-write re-sync hook. An API-key principal that carries a user id is treated as that
  user.

## Gate 3.5 phase 2, fix 4: native permission, unrecognized callers, one message (2026-10-19)

- **Native Live TV permission:** `Caller.HasLiveTvAccess` comes from the user's `EnableLiveTvAccess`; Core
  `Decide` denies a non-exempt caller without it (`DenyNoLiveTvAccess`), whatever the tuner map says. Jellyfin
  itself only enforces this permission on `/LiveTv/*`, not on PlaybackInfo, LiveStreams/Open or streaming.
  Admins and user-less API keys stay exempt.
- **Unrecognized callers:** `GetCaller` now distinguishes anonymous requests (no authenticated identity; still
  `null`, Jellyfin decides) from authenticated principals with no usable user claim, or whose user no longer
  exists. Those become a caller with no Live TV access, so they are denied rather than treated as anonymous; a
  warning (at most every 5 minutes) flags possible claim-format drift.
- **One message:** every denial returns `AccessPolicy.DeniedMessage` ("This Live TV channel isn't available for
  your account."), including "starting up"; the reason is logged server-side only (closes the GM9 oracle).
- Acceptance (`gate3_test.py` section 12, 50/50): an unmapped user with native Live TV ON sees 0 channels and gets
  403 on PlaybackInfo; a mapped user with native Live TV OFF gets 403 on PlaybackInfo and LiveStreams/Open for
  their own channel, and plays again when it is turned back on.
- Still open (Fix 5, default-deny): GM2/CG3 timer bodies, CG2 unrecognized token providers, GM5 id arrays.

## Gate 3.5 phase 2, fix 5: default-deny filter (2026-10-19)

For authenticated callers that aren't admins or user-less API keys, `LiveTvAccessFilter` now treats everything that
can reach Live TV content as protected:

1. **LiveTvController** (`Jellyfin.Api/Controllers/LiveTvController.cs`): only the allowlist below, and only for a
   user with a tuner, native Live TV permission, after the first sync (`AccessPolicy.DecideEndpoint`).
2. **Every action anywhere** (Videos, Audio, HLS, Items, Users/{id}/Items, Sessions, Playstate, SyncPlay, images, …):
   `TargetCollector` walks *all* action arguments, whatever their names (route, query, header, body), and every
   nested readable property of body objects of any non-framework type (depth ≤ 8, ≤ 50,000 values; larger is denied).
   It collects GUIDs, GUID-shaped strings (any format Jellyfin's `new Guid(string)` accepts), and provider-prefixed
   strings (`{32 hex}_…`, open tokens and live stream ids). `AccessService.Classify` resolves each one, and
   `AccessPolicy.DecideTarget` decides it.
3. **LiveStreams/Open and LiveStreams/Close** must name a resolvable target (`DenyMissingTarget` otherwise).

**Allowlist (non-admin, mapped user), `LiveTvEndpoints.Allowlist`:**

| Action | Route |
|---|---|
| GetLiveTvInfo | GET LiveTv/Info |
| GetLiveTvChannels | GET LiveTv/Channels |
| GetChannel | GET LiveTv/Channels/{channelId} (own tuner only) |
| GetLiveTvPrograms | GET LiveTv/Programs |
| GetPrograms | POST LiveTv/Programs (ChannelIds must be own tuner) |
| GetRecommendedPrograms | GET LiveTv/Programs/Recommended ("On Now") |
| GetProgram | GET LiveTv/Programs/{programId} (own tuner only) |
| GetGuideInfo | GET LiveTv/GuideInfo |

Playback info and stream open/close for own-tuner channels live on MediaInfoController and are allowed by the target
rules. Everything else on LiveTvController is denied for non-admins: timers, series timers, recordings
(including `LiveRecordings/{id}/stream`), tuner and listing-provider management, channel mappings, tuner discovery,
`LiveStreamFiles` for authenticated callers, and any action added in a future version.

**Decisions (confirmed by the user, 2026-10-19):**
- **Unresolvable values.** The route's content identifier (`itemId`, `routeItemId`, `channelId`, `programId`,
  `recordingId`, `seriesId`, `videoId`, `collectionId`, `streamId`, `timerId`) must resolve, or the request is denied.
  Anything shaped like a Live TV token or stream id (any provider prefix) must resolve. Every other value is still
  inspected, but if it doesn't resolve it's ignored: play-session ids, media-source ids, device ids, and HLS
  `{playlistId}` are GUID-shaped values that aren't items, and can't reach a channel on their own. A lookup that
  throws is treated as unresolvable and denied, whatever argument it came from.
- **Users with no tuner** (e.g. C with native Live TV on) are denied the Live TV listings too (403), not given an
  empty list.
- **Recordings** are denied for non-admins: active ones (`Video.SourceType == LiveTV`, `Video.cs:168-177`) and finished
  ones (files under the recording folders, `RecordingsManager.cs:106-170`). DVR isn't in the requirements. **DVR support
  would need recordings mapped to tuners** (e.g. via the timer's channel) before non-admins could be allowed.

All denials, including unresolvable route ids, return the same 403 text, so "doesn't exist" can't be told apart from
"not yours".

**Bugs found live and fixed (tests fail without the fix):** `LibraryManager.GetItemById(Guid.Empty)` throws, and a
malformed Live TV token made the classifier look it up (500). Jellyfin's placeholder row
`00000000000000000000000000000001` throws "Cannot deserialize unknown type" on load (500). Both now deny with 403.

**Acceptance:** `gate3_test.py` 86/86. New section 13 covers the allowlist and default-deny live: A with Jellyfin's own
LiveTvManagement permission still can't create a timer; Items?ids, Sessions/{id}/Playing?itemIds, Playstate bodies,
unknown and placeholder route ids, unknown-provider tokens and a missing open token are denied. Section 14
(`scripts/library_fixture.py`, local test movie and song) proves library browse, search, views, PlaybackInfo, HLS
playback, images and audio streaming still work for A and for C (no tuner).

All 15 review findings reproduced in phase 1 are now fixed; no tests carry `Category=KnownIssue`.

## Gate 3.5 phase 2, fix 5b: anonymous callers, legacy params, play sessions (2026-10-19)

The independent check after fix 5 found that anonymous requests bypassed the filter entirely. Reproduced live:
`GET /Videos/{tuner-2 channel}/stream?static=true` with no auth returned 200 and video.

- **Anonymous callers (HIGH, fixed).** Jellyfin doesn't require auth on the progressive stream endpoints
  (`VideosController.cs:38/312`, `AudioController.cs:19/89` have no `[Authorize]`, and no fallback policy is set),
  so the filter's old comment "streaming endpoints require auth" was wrong. Anonymous callers now get default-deny
  as a caller with no user and no Live TV access: any route, query, header or body identifier that resolves to a Live
  TV channel, program, stream, recording or play session, or looks like a Live TV token or stream id, gets the same
  generic 403. Anonymous requests that don't name Live TV are unaffected (public info, branding, login, library
  streams). Two exceptions pass anonymously unchecked:
  - **ImageController**, accepted (user decision): channel logos and programme art stay readable without auth
    because clients load images without auth headers. Metadata only.
  - **`LiveTv/LiveStreamFiles` and `LiveTv/LiveRecordings`**: Jellyfin's own ffmpeg reads these buffer URLs without
    auth. They are unchanged; restricting them is fix 6. Their ids are random.
  - `DynamicHlsController` has a class-level `[Authorize]` (`DynamicHlsController.cs:40`), so Jellyfin rejects anonymous
    HLS itself (401).
- **Legacy `params` (HIGH, fixed).** The streaming / HLS endpoints accept a `;`-separated `params` string that sets
  MediaSourceId (field 2), PlaySessionId (field 20) and LiveStreamId (field 22) (`StreamingHelpers.cs:389-560`). The
  collector now splits it exactly like Jellyfin and inspects every field; field 22 is resolved like any stream id,
  field 20 as a play session. Jellyfin's split can't fail, so there is no "unparseable" input; any unexpected error
  while splitting is denied. Live: A asking for its own movie with B's stream id in field 22 gets 403; with A's own
  stream id it plays.
- **Play session reuse (LOW, fixed: cheap and safe).** A play session id makes streaming reuse that session's
  transcoding job and its media source (`StreamingHelpers.cs:117-125`). Any play-session value (query `playSessionId`,
  `params` field 20, body `PlaySessionId`; any string, since Jellyfin accepts client-chosen ids) is looked up with
  `ITranscodeManager.GetTranscodingJob`. If the job is on a live stream, that stream's tuner is checked. This enforces
  the tuner, not the exact session owner. Attaching to another user's job on your own tuner is moot, because only you
  use that tuner. Live: A with B's live HLS play session id gets 403.
- **Anonymous channel logos / programme art (LOW, accepted).** Readable without auth, as above. Metadata only;
  no stream.

Acceptance: `gate3_test.py` 104/104 (section 15 adds anonymous stream attempts on tuner 1 and tuner 2 with and without
`static=true`, anonymous regressions, params field 22, play session reuse, and the LiveStreamFiles buffer plus `.ts`
playback through it).

## Gate 3.5 phase 2, fix 5c: legacy HLS file routes bound to a running job (2026-10-19)

The second independent check found, and I confirmed live, that `GET /Videos/{any id}/hls/{x}/{name}.ts` (no auth)
served any file in the transcode folder by name. That includes the live `.ts` buffer `{UniqueId}.ts`
(`LiveStream.cs:79`) and other users' HLS segments. Denying the route, as first proposed, would have broken Live TV:
for live (event) playlists Jellyfin has ffmpeg write segment URLs as `hls/{playlist}/`
(`DynamicHlsController.cs:1656-1662`, `HlsHelpers.cs:125-130`), which clients fetch through this legacy route. It is
also deliberately unauthenticated because some browsers fetch segments without auth (`HlsSegmentController.cs:120-121`).

**Fix (option A1, user decision).** For every non-exempt caller, logged in or anonymous, the three legacy actions
(`GetHlsVideoSegmentLegacy`, `GetHlsAudioSegmentLegacy`, `GetHlsPlaylistLegacy`) only serve a running job's own files:
- Route names must be plain stems (`HlsNames.IsSafeName`: 1-128 of `A-Za-z0-9-_`). ASP.NET has already URL-decoded
  them, so path separators, `.`/`..`, encoded variants and whitespace are rejected.
- The job is found by its **exact** playlist path `{transcode folder}/{name}.m3u8`
  (`ITranscodeManager.GetTranscodingJob(path, Hls)`, exact match, `TranscodeManager.cs:109-115`). The segment must be
  exactly `{name}{1-9 digits}` or `{name}-1` (fMP4 init), and the extension one of ts/mp4/m4s/aac/mp3. The audio route
  has no playlist id, so each candidate name made by stripping 1-9 trailing digits is looked up as an exact path.
  Nothing matches by prefix or substring.
- The route item id must be a non-empty, resolvable item, and recordings are denied (closes the "empty id" finding).
- Logged-in non-admins also get the tuner check on the item and on the job's live stream. No running job (finished
  or never existed) means 403. All denials use the generic message.

**Accepted risk (A1): anonymous requests for a running Live TV job's segments are allowed.** The random job name (an
MD5) is the only secret. This is the same class of risk as the `LiveStreamFiles` buffer URL, and Chrome-style segment
requests without auth need it. A segment can only be fetched while the job runs and only by someone who knows its
name; no API was found that discloses another user's job name. **To tighten it later:** rewrite live playlists in the
plugin (a result filter on `main.m3u8` / `live.m3u8`) to append a short-lived signed token (HMAC over job name + user +
expiry) to each segment URL, and require that token on the legacy segment route; or require auth on segment requests
once all supported clients send it (re-test the Chrome case); or enforce either at Caddy.

**Other file-serving controllers, checked one by one:** Image (item images by `itemId`; anonymous reads accepted,
logged-in requests check the route item), Trickplay (`[Authorize]`, tiles from the item's own folder; `itemId` and
`mediaSourceId` checked), Subtitle (item subtitle file or extraction, anonymous; `routeItemId`/`itemId` checked; Live TV
channels aren't `Video`), VideoAttachments (item attachments, anonymous; `videoId` checked), Lyrics (`[Authorize]`,
item-based), Library `File`/`Download` (logged-in, the item's own path; route item checked), Videos static (item media
source; route item and all arguments checked). None serves transcode-folder files by name. The only routes that do are
the three `HlsSegment` actions above. `LiveStreamFiles`/`LiveRecordings` look streams up by id, not by file name (fix 6).

Live acceptance (`gate3_test.py` section 16, 119/119): Live TV HLS uses the legacy route and plays; anonymous fetch of a
running live job segment works (A1); B gets 403 on A's job; empty item id, `.m3u8` request, encoded traversal, a
foreign job name, a finished job and the raw `.ts` buffer name (video and audio routes) all get 403; library HLS uses
`hls1` and plays with a real `libx264` transcode by Jellyfin's ffmpeg.

## Gate 3.5 phase 2, fix 5d: open-token media source, cross-tuner sharing, hidden upstream URLs (2026-10-19)

**Found by a narrow trace of the stream-open path, confirmed live.** A Live TV open token is
`{provider}_LiveTvChannel_{channelId}_{mediaSourceId}`. Jellyfin shares any already-open stream whose id equals the
third part, without checking channel or tuner (`DefaultLiveTvService.cs:466-476`). A, opening A's own tuner-1 channel
with B's media source id, was handed **B's open tuner-2 stream**, including its raw upstream URL in `Path`.

1. **Open-token media source (fixed).** The third part must be empty, the token's own channel id, or that channel's
   own media source id: md5(stream URL), which is the last 32 characters of the M3U channel's ExternalId
   (`M3uParser.cs:104`, `M3UTunerHost.cs:195`; `JellyfinIds.M3uMediaSourceId`). Anything else gets the generic 403.
2. **Accidental cross-tuner sharing (fixed by detection).** If both playlists list the same stream URL, the media
   source ids are equal, and Jellyfin would hand A the tuner-2 stream B already has open. For actions that can open a
   stream (MediaInfo, Videos, Audio, DynamicHls, UniversalAudio), the plugin computes the live stream id Jellyfin would
   reuse (`{provider}_{service}_{sourceId}`, `JellyfinIds.LiveTvStreamId`). If that stream is open, sharable, and on
   another tuner, the tuner checked is the one Jellyfin would actually use. So A is refused (403) while B's twin is
   open, and can open normally once it closes. **Config rule:** tuners should not list the same stream URL;
   otherwise users of different tuners block each other on that channel.
3. **Upstream URLs hidden from non-admins (fixed).** `ResponseScrubber`, run after the action for every non-exempt
   caller, blanks `Path`, `EncoderPath` and `RequiredHttpHeaders` on Live TV media sources in `PlaybackInfoResponse`,
   `LiveStreamResponse`, `BaseItemDto` (and lists / `QueryResult`), and `SessionInfoDto` now-playing items. It also
   blanks tuner `Url`/`Source` and guide-provider `Path`/`Username`/`Password`/`ListingsId` in the Live TV
   configuration. **`GET /System/Configuration/livetv` was readable by any logged-in user**
   (`ConfigurationController.cs:78`, class-level `[Authorize]` only), so every user could read every tuner's M3U URL.
   Responses are deep-copied with Jellyfin's own JSON options before scrubbing (some DTOs are shared server state); if
   a copy fails, the response is denied. Admins and API keys get the original object. Clients don't need these
   values: with a tuner stream limit direct play is off (`M3UTunerHost.cs:148`), and playback goes through
   `TranscodingUrl` / server stream URLs. Verified live: the web-client path (PlaybackInfo with a device profile ->
   `TranscodingUrl` -> HLS) and manual HLS still play.

**URL-leak survey (non-admin), live unless noted:** PlaybackInfo POST/GET, LiveStreams/Open, LiveTv/Channels (all
fields), LiveTv/Channels/{id}, Items?ids&fields=Path,MediaSources, Users/{id}/Items/{id}, LiveTv/Programs (fields),
Programs/Recommended, LiveTv/Info, GuideInfo, Search/Hints, System/Configuration and /livetv, System/Info and
/Info/Public, and /Sessions while playing. None contains an upstream marker. By code: plugin configuration is admin-only
(`PluginsController.cs:25`); server logs (`SystemController.cs:146-147, 206-207`) and the activity log
(`ActivityLogController.cs:18`) are admin-only; WebSocket session updates go to admins only
(`SessionInfoWebSocketListener.cs:69`). The plugin's own logs never contain URLs or full tokens.

**Documented, not fixed (affects Gate 4, one stream per user):**
- **Stream id collisions.** Two separate streams with the same stream URL get the same live stream id. The newer one
  overwrites the older in `MediaSourceManager._openStreams` (`MediaSourceManager.cs:522`), and the older keeps
  running untracked: it no longer counts towards the M3U stream limit (`M3UTunerHost.cs:80-90`) and is never closed.
  This happens without sharing, e.g. a token with no media source part. Impact on Gate 4: a per-user limit can't rely
  on `_openStreams` alone. The plugin should track the streams it admits per user, and the rule "no two tuners list
  the same URL" avoids the cross-tuner variant.
- **Shared stream closed by any consumer.** Sharers of one stream share its live stream id, and
  `CloseLiveStream` just decrements a consumer count with no per-user tracking (`MediaSourceManager.cs:861-883`). Any
  consumer can close it repeatedly and end it for the others. Within one tuner (one user per tuner) this only affects
  that user's own devices. Impact on Gate 4: per-user accounting must count devices or sessions, not trust consumer
  counts, and any "newest stream wins" policy must close streams through the plugin's own bookkeeping.

## Gate 3.5 phase 2, fix 5e: no untracked static streams of Live TV channels (2026-10-20)

**Found by the fix 5d independent check, confirmed live.** `GET /Videos/{channel}/stream?static=true` with no live
stream id doesn't open a live stream. `StreamingHelpers.cs:117-137` takes the channel's media source as is, and
`VideosController.cs:456-460` (Audio: `AudioHelper.cs:116-119`) proxies the provider URL through
`FileStreamResponseHelpers.cs:29-96`. No stream is opened, so the M3U stream limit (`M3UTunerHost.cs:80-90`) never
applies. Live: with A's tuner busy, a normal open of a second channel got 409, but `static=true` on it returned
video. The provider's raw response (for an HLS source, a playlist that may carry credentialed URLs) goes straight to
the client.

**Fix.** For non-exempt callers, a request that may be a static stream and names a Live TV channel or program is
denied (generic 403, `AccessDecision.DenyUntrackedStream`, `AccessPolicy.DecideStatic`). This applies unless the
request also carries a tracked stream: an open live stream id, or the play session of a running job on one
(`AccessService.IsTrackedLiveStream`). Those are already counted by Jellyfin and tuner-checked by the plugin.
Static with the caller's own open live stream is the tracked direct-stream path and stays allowed (owner decision).
A play session id that isn't a running job doesn't count as tracked, even if it happens to be an item id.

**Ways to skip the tracked path, checked:**

| Path | Result |
|---|---|
| `Videos/{id}/stream`, `stream.{container}` (and their HEAD routes, same actions) with `static=true` | Closed |
| The same with legacy `params` field 3 = `true` (`StreamingHelpers.cs:405-421`) | Closed |
| `Audio/{id}/stream`, `stream.{container}` with `static` or params field 3 | Closed |
| `Audio/{id}/universal`: may stream statically without a live stream, or redirect to `mediaSource.Path` (`UniversalAudioController.cs:176-182`) | Closed: denied for Live TV items (M3U channels are video; nothing legitimate uses it) |
| Non-static progressive streams, incl. `videoCodec=copy&audioCodec=copy` (container copy) | Tracked: ffmpeg path, `TranscodeManager.cs:662-669` opens the live stream. Unchanged |
| DynamicHls (master / main / variant, incl. copy codecs) | Tracked, same reason. Unchanged |
| Stream request with no open token or live stream id, non-static | Tracked: opened by the transcode manager. Unchanged |
| `Items/{id}/File`, `Items/{id}/Download` | No proxy: they serve `item.Path` as a local file (`LibraryController.cs:105-113, 669-715`), and channel items have no Path |
| `LiveTv/LiveStreamFiles`, `LiveTv/LiveRecordings` | Serve an already-open buffer (tracked); access restriction is fix 6 |
| Legacy HLS file routes | Bound to a running job (fix 5c) |
| Admins and API keys | Exempt, unchanged |

Live (Gate 3, section 18), with A's tuner busy on Test News: all five static variants for Test Sports return 403,
make no upstream request, and use the generic message. Static on the open channel without its live stream id is 403;
with A's own live stream id it is 200. The tracked path (open -> HLS) still plays for A and B. Library static
streams (movie, song, legacy params) still work for A, C and anonymous callers, and UniversalAudio still serves a
library song.

### Remaining gaps from the fix 5d check

- **Gap 2: same stream URL on two tuners (config rule).** Two tuners must not list the same stream URL. Live stream ids
  depend only on the URL (`LiveTvMediaSourceProvider.cs:301-303`, `M3UTunerHost.cs:195`). For a sharable stream,
  fix 5d refuses the cross-tuner share. For a non-sharable one (HLS, or sharing off), B's new stream replaces A's in
  `MediaSourceManager._openStreams` (`MediaSourceManager.cs:522`). When A's playback ends, B's stream is closed
  (`TranscodeManager.cs:243-245`, `SessionManager.cs:325-351`), and A's original stream is orphaned, still holding a
  tuner slot. This is interference, not access to another user's content. **Fix 7's self-check will warn about, or
  refuse, a configuration where two tuners list the same stream URL.**
- **Gap 3: killing another user's transcodes by device id (accepted risk).** A stream request with
  `deviceId=<victim>` and no play session kills all of the victim's transcode jobs (`DynamicHlsController.cs:1528`,
  `TranscodeManager.cs:202-204`). Accepted because:
  - It's denial of service on in-progress playback, not access to a stream; the victim's client restarts playback.
  - It needs the victim's device id. Non-admins only see other users' sessions with "Allow remote control of other
    users" (`SessionManager.cs:1975-2010`), which is off by default. Keep it off for Live TV users.
  - It's Jellyfin core behavior that affects library playback just as much.
  - Binding `deviceId` to the caller would mean second-guessing Jellyfin's device registry and could break clients.
- **Gap 4: raw logo URL in image info (accepted risk).** `GET /Items/{channel}/Images` returns `ImageInfo.Path`,
  which is the playlist's `tvg-logo` URL until Jellyfin caches the image (`ImageController.cs:1800-1802`,
  `GuideManager.cs:447-457`). Accepted because:
  - Only the user's own tuner's channels are reachable.
  - A logo URL isn't a stream URL; at most it reveals the logo host.
  - Once cached, it is a local path.
  - Scrubbing it would add another response type to keep in step with Jellyfin for little gain.
  - Config rule: logo URLs must not carry credentials.
  - Tuner `UserAgent`/`DeviceId`/`FriendlyName` in the Live TV config are left visible for the same reason (not
    secrets), unless a provider treats the user agent as one.

## Gate 3.5 phase 2, fix 6: live buffer URLs only for the server itself (2026-10-20)

`LiveTv/LiveStreamFiles/{id}/stream.{container}` and `LiveTv/LiveRecordings/{id}/stream`
(`LiveTvController.cs:1162-1203`) have no auth. They exist for Jellyfin's own ffmpeg, which gets them through
`GetApiUrlForLocalAccess()` (`SharedHttpStream.cs:66`, `MediaSourceManager.cs:839`). That URL is built from the
server's first non-loopback interface address (`ApplicationHost.cs:919-926`, `NetworkManager.cs:866-870`), so ffmpeg
connects from the server to itself. Before this fix, the stream's random id was the only secret.

**Rule** (`BufferAccess`, Core; the filter's `AdmitBuffer`): admins and API keys keep access. Everyone else, including
anonymous callers, is allowed only if both of these hold:
- The connection comes from the server itself: loopback, the same address it connected to (a self-connection to the
  container's own IP), or one of the server's interface addresses (`ServerAddresses`, cached 1 minute; empty on
  error, so it fails closed).
- The request carries no forwarded headers: `X-Forwarded-*`, `X-Real-IP`, `Forwarded`, `X-Client-IP`,
  `True-Client-IP`, `CF-Connecting-IP`, and `X-Original-For/-Host/-Proto/-Prefix`. ASP.NET's forwarded headers
  middleware leaves the `X-Original-*` headers behind when it consumes `X-Forwarded-*` from a known proxy
  (`ApiServiceCollectionExtensions.cs:169-190`). It also rewrites the remote address to the client's, which is then
  not local either.

Requests through Caddy are refused either way: they come from Caddy's container and carry forwarded headers.
Everyone else gets the generic 403.

**Verified live** (Gate 3, section 15; there is no Caddy locally, so proxied requests are simulated from inside the
container, i.e. from the server's own address with a forwarded header added):

| Request | Result |
|---|---|
| Anonymous from the host | 403 |
| Logged-in A from the host | 403 |
| Admin API key | 200 |
| From the container's own interface address (the exact URL ffmpeg uses) | 200 |
| From loopback | 200 |
| From the server, with each of `X-Forwarded-For`, `X-Real-IP`, `Forwarded`, `X-Forwarded-Proto`, `X-Forwarded-Host`, `X-Original-For` | 403 |

`.ts` playback through the buffer (HLS segment) still works. The server log shows exactly the 8 denials the test
made, so ffmpeg was never refused.

**Version-fragile:** this depends on ffmpeg reading the buffer over HTTP from the server's own address. If a future
Jellyfin reads it from another address, `.ts` channels stop playing (visible as buffer denials in the log), and
nothing is exposed.

**Accepted:** a process on the Jellyfin host or inside its container can still read a buffer if it knows the random
id. That is already inside the trust boundary.

## Gate 3.5 phase 2, fix 7: startup self-check, failed state, status API (2026-10-20)

`SelfCheckService` (`Health/`) runs about 2 s after startup. It runs again when the plugin config or the Jellyfin
`livetv` config is saved, after Refresh Guide, and periodically: every 10 minutes while healthy, every minute while
failed. It checks:

| Check | Source | Failed when |
|---|---|---|
| Filter registered | `IOptions<MvcOptions>.Filters` has a `TypeFilterAttribute` for `LiveTvAccessFilter` | Absent |
| Endpoint shapes | `IActionDescriptorCollectionProvider` against `EndpointContract.Requirements`. 30 actions, covering parameter names the filter reads (`static`, `params`, `playSessionId`, `liveStreamId`, route ids), body properties (`OpenToken`, `LiveStreamId`, `MediaSourceId`), return types the scrubber recognizes, and the `Users/` route that triggers a re-sync. Every route of an action must match (GET and HEAD) | Anything missing |
| Model members | Reflection: `MediaSourceInfo.Path`/`EncoderPath`/`RequiredHttpHeaders`/`OpenToken`/`LiveStreamId`, `LiveTvOptions` tuner and listing fields, `SessionInfoDto` now-playing, `ChannelInfo.Id`/`Path`, and others | Anything missing |
| Claim contract | `Jellyfin.Api.Constants.InternalClaimTypes.UserId`/`IsApiKey`, read by reflection (`InternalClaimTypes.cs:11, 41`). Also runtime evidence from `AccessService.CallerCounts` | Constants missing or changed, or since the last check at least 3 authenticated requests carried no recognizable claim and none did |
| Tuners | `TunerHealth.Evaluate` (Core): the plugin's tuners against Jellyfin's M3U tuner URLs and the library's channel ExternalIds | No tuners configured; a tuner URL that isn't a Jellyfin M3U tuner; a tuner with no channels |
| Duplicate stream URLs | The same media source id (md5 of the URL) on more than one tuner | **Never.** Warning only, per the owner's decision |

Warnings (not failures) also cover: ignored invalid tuner entries, a tuner with no users, and Jellyfin M3U tuners
missing from the plugin config.

**Failed state.** The problems are logged once per change as `LiveTvAccess: SELF-CHECK FAILED ...`, and the filter
treats the server as not ready (`ready = sync.IsReady && selfCheck.IsHealthy`). That is the same proven path as before
the initial sync: protected Live TV operations get the generic 403 for non-admins, while admins, API keys and
non-Live-TV use are unaffected. The buffer rule (fix 6) is independent of this, so Jellyfin's ffmpeg keeps working.
It never throws or unloads: a check that itself fails is a failed state. It recovers on the next run after a fix,
and logs "Live TV access restored". Before the first run the state is `Pending`, which counts as not healthy (fail
closed, about 2 s after startup).

**Limits:**
- If the filter isn't registered, nothing can be enforced. The self-check can only report it, and the tag-based
  channel hiding still applies.
- If the claim contract is broken, admins can't be recognized either, so admins lose Live TV too until it is fixed.

**Status API.** `GET /LiveTvAccess/Status[?refresh=true]` requires the `RequiresElevation` policy (admin); A gets 403
and anonymous callers 401. It returns:
- `State` (`Pending`, `Healthy` or `Failed`), `CheckedAtUtc`, `Trigger`, `InitialSyncComplete`;
- `Checks` (name, ok, detail), `Problems`, `Warnings`;
- `Tuners`: key, `Location` (`scheme://host` or `file`), `UrlHash`, `InJellyfin`, `ChannelCount`, `UserCount`;
- `Duplicates`: `Host` as `scheme://host[:port]`, `Hash`, `Tuners`, `ChannelCount`;
- `RequestsChecked`.

It never includes a URL path, query, user info, tuner file path or credential (`Redact.Location`, tested).
Duplicate hosts come from Jellyfin's cached M3U channel list (`ITunerHost.GetChannels(enableCache: true)`). This
lookup only happens when duplicates exist; on a cold cache, Jellyfin reads the playlists once, as a guide refresh
does.

**Verified live** (Gate 3, sections 17 and 19, 197/197):
- Healthy against the real 10.11.11 server: all 30 endpoint shapes and the claim constants match. Per-tuner channel
  counts match the playlists, and the response has no URLs.
- Saving a bad tuner URL through the normal config API: the server reaches the failed state by itself. The problem
  names the tuner but not its URL. A gets 403 on A's own channel and on the channel list. A's library movie and song
  still work, and the admin still opens Live TV.
- Restoring the config: it recovers by itself, tags re-sync, and A plays again. The log has the "SELF-CHECK FAILED"
  and "restored" lines and no tuner path.
- Same URL on both tuners: the state stays Healthy, with one duplicate shown as `http://fakestreams` plus a hash.

**Version-fragile:** the whole contract is pinned to 10.11.11. That's the point: on an upgrade, drift shows up as a
failed state with the exact missing piece, instead of a silent bypass.

## Step 3: admin settings page (2026-10-20)

`Configuration/configPage.html` is an embedded resource registered through `IHasWebPages.GetPages()`, the standard
plugin page mechanism (as in `../jellyfin-plugin-template`). Jellyfin lists it under Dashboard > Plugins >
LiveTvAccess. `GET /web/ConfigurationPages` is admin-only (`DashboardController.cs:48-49`); the page's data calls are
admin-only too: plugin config (`PluginsController.cs:25`) and `GET /LiveTvAccess/Status`.

- **Tuners:** key and M3U path rows. The path field suggests Jellyfin's own M3U tuner URLs (a `datalist` from
  `livetv` config), because the path must match exactly. Validation runs before saving and follows the server's
  rules: key `a-z0-9-`, not `unassigned`, unique; path required and unique.
- **Users:** pick a user from a dropdown (`ApiClient.getUsers()`; admins are marked "not restricted", and missing
  users are kept and marked) and a tuner from the configured keys. One row per user.
- **Status (read-only):** the self-check state, problems, warnings, per-tuner channel and user counts, duplicates
  (host and hash) and the individual checks, with a "Run check now" button (`?refresh=true`). After a save, the status
  refreshes by itself.
- **Save:** the normal path. `getPluginConfiguration`, replace `Tuners` and `Users`, then
  `updatePluginConfiguration`. So `Plugin.UpdateConfiguration` still resolves names and ids, the owned-tag history is
  kept, and the save fires `ConfigurationChanged`, which drives the tag sync and the self-check.
- **No external resources:** inline CSS and JS only, no `src` or `href`, no fetches. Nothing from the server is
  inserted as HTML: every value goes in through `textContent` or `value`, and there is no `innerHTML`. Unit tests
  check both.

**Verified:**
- Unit tests check that the page is embedded, the plugin id, the save path, that nothing external is loaded and that
  nothing is injected as HTML. A save payload shaped like the page's keeps the id, refreshes a stale name and keeps
  the history.
- Live (Gate 3, section 20):
  - The dashboard lists the page; A can't list pages.
  - The server serves the embedded page byte-for-byte, and its script parses (`node --check`).
  - A save made exactly as the page makes it is accepted. The server keeps the ids, fixes the stale name and keeps the
    history; tags stay in sync, and the self-check is Healthy.

**Not verified visually:** I didn't open the page in a browser. Not tested: how the rows, `emby-select` controls and
status badge render and lay out in the 10.11 dashboard theme; the user and tuner dropdowns, the datalist
suggestions, the Add and Remove buttons, and the inline validation messages; and the `Dashboard.*` loading and
saved-toast helpers. Worth a quick manual look in the dashboard before deploying.

## Step 4: deployment runbook, packaging, cleanup (2026-10-20)

- `docs/deploy.md` is the manual production runbook. It covers:
  - a pre-flight checklist: provider connection limit and the tuner stream limit of 1, embedded credentials,
    shortening the playlists, Refresh Guide duration, the overlap check, logo URLs, user settings, collecting tuner
    URLs and user ids;
  - configuring before users connect: a pre-seeded config XML with user ids (option A) or the settings page (option
    B), since an unconfigured plugin denies non-admins;
  - backup with Jellyfin stopped, install owned by uid 1000, the expected log lines, verification (status API, client
    behaviour, buffer URL 403 through Caddy, hidden tuner URLs), troubleshooting by self-check problem, and quick and
    full rollback (including removing the `livetv-*` blocked tags).
- `scripts/m3u_overlap.py`: a standalone, stdlib-only pre-flight check for stream URLs listed by more than one
  playlist. It never prints URLs: just `scheme://host`, the same hash as `GET /LiveTvAccess/Status` (checked:
  `sha256:cd0a068d` for the test duplicate in both), and the channel names. Exit 0 means no overlap, 1 means overlap.
- `scripts/package_release.sh`: builds `dist/LiveTvAccess_0.1.0.0.tar.gz` (the two DLLs and `meta.json`, with no
  macOS metadata) and prints SHA-256 sums. `dist/` is gitignored.
- `review/`: the two external review files are committed; generated `review/bundle-*.txt` files are ignored.
  `review-bundle.txt` never existed, so there was nothing to delete.

**Stale guide fixture (found during the final regression).** The XMLTV fixture holds programmes relative to when it is
written (it used to cover 1 h back to 5 h ahead). After it ran out, Gate 3 section 13 found no programme.
Regenerating the fixture alone didn't help: Jellyfin reuses its cached copy (`cache/xmltv/{provider id}.xml`) for up
to 1 hour (`XmlTvListingsProvider.cs:30, :64-69`). The cached copy, written at 04:54Z, held programmes ending 04:00Z,
so the 05:07Z refresh read stale data and returned 0 programmes.

Fix (local test tooling only):
- `write_guide()` now covers 1 h back to 48 h ahead.
- Gate 3 runs `fresh_guide()` at setup and again before section 13. It regenerates the fixture, deletes the local
  cached copy only if it is older than the fixture, refreshes the guide, and checks that programmes are airing
  (a check, not a crash).

This doesn't affect production, where the guide comes from the real provider.

## Final review (2026-10-20): independent subagent, Argon (Gemini), Sol (ChatGPT)

Each external claim was checked against the plugin and the Jellyfin 10.11.11 source, and reproduced live where possible,
before any change. The full table of findings and their statuses is at the end of this section.

### R1. Static exemption only from the effective stream parameters (Sol #1, Argon #6)

**Sol #1: confirmed live.**
- **Request:** A opens Test News (live stream id `S`, A's tuner busy), then sends
  `GET /Videos/{Test Sports}/stream?static=true&deviceId=S`. It returned **200 with video**, and the provider got a
  Test Sports request: an untracked second stream.
- **Cause:** the filter set `tracked` when any argument named an open live stream on the caller's tuner. Jellyfin only
  uses the effective `LiveStreamId` (`StreamingHelpers.cs:116, 141-143`); `deviceId` was irrelevant to it.

**Argon #6: not reproducible as a bypass.**
- Jellyfin's `params` field 3 is `Static = string.Equals("true", val, OrdinalIgnoreCase)` (`StreamingHelpers.cs:417-418`):
  no trim, no `bool.TryParse`.
- So `params=;;; true ` is not static. The request takes the tracked transcode path, and it returned 409 (tuner busy)
  live.
- Fixed together with Sol #1 anyway (failing closed), as decided.

**Fix (`StaticStreamRequest`, Core; `LiveTvEndpoints.IsStaticStream` / `EffectiveLiveStreamId`):**
- Static: a non-blank `params` field 3 overrides the bound `static` argument, as in Jellyfin's ParseParams
  (`StreamingHelpers.cs:62, 389-418`). Any non-blank value other than `false` counts as static: a superset of
  Jellyfin's "true".
- Tracked: only the effective live stream (`params` field 22 if non-blank, else `liveStreamId`) can make a static
  request tracked, and it must be an open stream. Its tuner is checked with every other identifier.
- **A play session alone no longer counts.** With no `LiveStreamId`, Jellyfin streams the job's media source
  statically (`StreamingHelpers.cs:116-125`) without opening a counted stream.
- UniversalAudio takes no live stream id, so it is always denied for Live TV items.

**Tests:** Core 20 and plugin 11 failed first, including the old "play session alone is allowed" expectation, now 403.
Gate 3 section 18 has the exact requests from both reviews, plus `tag=S` and play-session-only. All return 403 with no
upstream request; static with A's own `liveStreamId` still returns 200.
