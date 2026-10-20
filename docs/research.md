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
