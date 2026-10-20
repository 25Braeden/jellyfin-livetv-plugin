# Deploying LiveTvAccess to production

This is a runbook for you to follow by hand. Nothing in this project connects to the VPS. Commands marked **(VPS)**
run on the server in a shell you open yourself; commands marked **(local)** run in this project folder.

- Plugin: `LiveTvAccess` 0.1.0.0, GUID `4d5aa36c-9164-4f4d-ba7f-0fa6c25bfd28`.
- Server: Jellyfin **10.11.11 only** (official image `jellyfin/jellyfin:10.11.11`). The plugin checks Jellyfin's
  endpoint shapes at startup; on any other version expect the self-check to fail (see Troubleshooting).
- Production layout: the stack is in `/opt/media-server`. Host `/opt/media-server/jellyfin/config` is `/config` in
  the container, which runs as uid/gid `1000:1000`. Caddy (`jellyfin-caddy`) is the HTTPS reverse proxy.

Paths used below (host side):

| What | Path |
|---|---|
| Plugin folder | `/opt/media-server/jellyfin/config/plugins/LiveTvAccess_0.1.0.0/` |
| Plugin config file | `/opt/media-server/jellyfin/config/plugins/configurations/Jellyfin.Plugin.LiveTvAccess.xml` |
| Jellyfin Live TV config (tuner URLs) | `/opt/media-server/jellyfin/config/config/livetv.xml` |
| Jellyfin database | `/opt/media-server/jellyfin/config/data/` |

The examples assume the Jellyfin compose service is called `jellyfin`. Check with `docker compose ps` in
`/opt/media-server` and substitute the real name.

---

## 1. Pre-flight checklist (before the maintenance window)

Work through these on the live server (dashboard) and on your own machine. Nothing here changes production except
where it says so.

- [ ] **Jellyfin version is exactly 10.11.11.** Dashboard > About, or `GET /System/Info/Public` (`Version`). The
      compose file should pin `jellyfin/jellyfin:10.11.11`, not `latest`.
- [ ] **Real provider connection limit.** Find out how many concurrent streams each provider account allows, and
      whether both tuners use the same account.
  - In Dashboard > Live TV, set each M3U tuner's **simultaneous stream limit to 1**. That limit is what holds each
    user to one stream: a user only ever gets their own tuner, and a second stream returns "tuner busy" (409).
  - A non-zero limit also turns off direct play for Live TV (`M3UTunerHost.cs:148`), so every client plays through
    the server, where the plugin enforces access.
  - If both tuners share one account, that account must allow at least 2 concurrent streams for A and B to watch at
    the same time.
- [ ] **Do the M3U URLs or stream URLs embed credentials?** (For example `username=...&password=...` or a token in
      the playlist URL or in the stream lines.)
  - Once deployed, non-admins can no longer read them through the API.
  - **Before** the plugin, any logged-in user could read every tuner URL at `GET /System/Configuration/livetv`
    (`ConfigurationController.cs:78`). If credentials are embedded and non-admin users have had accounts, consider
    rotating the provider credentials after the deploy.
- [ ] **Shorten the playlists.** Jellyfin's Refresh Guide is quadratic in the number of channels. Measured locally:
  - 16,007 channels: the guide refresh projected about 13 h with a 13 MB guide, and still ran over 17 min with no
    guide at all.
  - A few hundred channels per tuner: seconds.

  Import only the channels you want (filter the M3U before Jellyfin sees it) and note the final channel count per
  tuner.
- [ ] **Measure Refresh Guide on the final playlists.** In Dashboard > Scheduled Tasks > Refresh Guide, run it once
      and note how long it takes. The plugin tags channels after each refresh, and the self-check reports "no
      channels" for a tuner until its channels exist. If the refresh takes long, schedule it off-peak.
- [ ] **Overlap check: no stream URL on two tuners.** Run on the final playlist files, wherever they are (on the VPS,
      or on copies you downloaded yourself):

      python3 scripts/m3u_overlap.py tuner1.m3u tuner2.m3u

  - Exit 0 prints `OK: no stream URL is listed by more than one playlist.`
  - Exit 1 lists each shared stream as `scheme://host`, a hash and the channel names. It never prints URLs. Remove
    each shared stream from all but one playlist.
  - The script is standalone Python 3 with no dependencies (copy `scripts/m3u_overlap.py` to the server if needed).
  - The hash matches the one `GET /LiveTvAccess/Status` shows under `Duplicates`.
  - The plugin only warns about overlaps; it doesn't fail. But users of different tuners block each other on a
    shared stream, and a non-sharable shared stream can be closed by the other user
    (`docs/research.md`, fix 5e, gap 2).
- [ ] **Logo URLs (`tvg-logo`) carry no credentials.** Non-admins can see the raw logo URL of their own channels
      (accepted risk, gap 4).
- [ ] **User settings** (Dashboard > Users):
  - Users who get a tuner (A, B): "Allow Live TV access" **on**.
  - User C (no Live TV): "Allow Live TV access" **off**. That's native Jellyfin; no plugin entry is needed.
  - Nobody except admins has "Allow remote control of other users" (accepted risk, gap 3).
  - Note which accounts are admins. Admins are never restricted by the plugin.
- [ ] **Collect the values for the plugin config:**
  - The exact tuner URLs or paths as Jellyfin has them: Dashboard > Live TV > each tuner (or `livetv.xml`,
    `<Url>` under each M3U `TunerHostInfo`). The plugin's URL must match **exactly**.
  - The user IDs, if you'll pre-seed the config file (option A below): Dashboard > Users > click the user; the URL
    contains `userId=...`.
- [ ] **An admin API key** for verification (Dashboard > API Keys). Keep it in your password manager. The commands
      below read it with `read -rs` so it never lands in shell history.
- [ ] **Caddy:** no change needed. Requests through Caddy reach Jellyfin from Caddy's container with forwarded
      headers, so the plugin refuses them on the internal buffer URLs (fix 6).
- [ ] **Build the package (local)** from the commit you want to deploy:

      scripts/package_release.sh

      This produces `dist/LiveTvAccess_0.1.0.0.tar.gz` and prints SHA-256 sums. Keep the sums to compare on the
      server.
- [ ] **Have a way to tell users.** Plan a short window: Live TV is denied for non-admins until the plugin is
      configured and its first sync and self-check pass. That takes seconds, unless a guide refresh is needed.

## 2. Prepare the plugin configuration

An installed but unconfigured plugin **denies Live TV to every non-admin**: it fails closed, and its status shows
"No tuners are configured". So configure it right away. Choose one option.

**Option A (recommended): pre-seed the config file, so there's no gap at all.** Write
`Jellyfin.Plugin.LiveTvAccess.xml` on your machine from this template, using the exact tuner URLs and the user IDs.
User entries need the **ID**: a pre-seeded file isn't processed by the config API, so names alone are not resolved,
and entries without an ID are skipped (and dropped at the next save if no user has that name).

```xml
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Tuners>
    <TunerConfig>
      <Key>tuner1</Key>
      <Url>EXACT URL OR PATH OF JELLYFIN M3U TUNER 1</Url>
    </TunerConfig>
    <TunerConfig>
      <Key>tuner2</Key>
      <Url>EXACT URL OR PATH OF JELLYFIN M3U TUNER 2</Url>
    </TunerConfig>
  </Tuners>
  <Users>
    <UserTunerConfig>
      <UserId>USER-A-ID</UserId>
      <UserName>A</UserName>
      <TunerKey>tuner1</TunerKey>
    </UserTunerConfig>
    <UserTunerConfig>
      <UserId>USER-B-ID</UserId>
      <UserName>B</UserName>
      <TunerKey>tuner2</TunerKey>
    </UserTunerConfig>
  </Users>
  <OwnedTagHistory />
</PluginConfiguration>
```

- Keys: `a-z`, `0-9` and `-`. `unassigned` is reserved.
- Escape `&` in URLs as `&amp;`.
- This file can contain provider credentials (the tuner URLs). Don't commit it, and delete your local copy after the
  deploy.

**Option B: configure in the dashboard right after the start** (Dashboard > Plugins > LiveTvAccess). Add the tuners
(the M3U path field suggests Jellyfin's tuner URLs) and the users, then click Save. Saving resolves user names to
IDs. Non-admins are denied Live TV from the restart until you save.

## 3. Deploy (VPS)

Copy `dist/LiveTvAccess_0.1.0.0.tar.gz` (and, for option A, `Jellyfin.Plugin.LiveTvAccess.xml`) to your home
directory on the VPS yourself, for example with `scp`. Then:

```sh
cd /opt/media-server
docker compose ps                      # confirm the Jellyfin service name (assumed: jellyfin)

# 1. Stop Jellyfin and back up its config + database while it is stopped.
docker compose stop jellyfin
sudo tar -C /opt/media-server/jellyfin -czf /opt/media-server/backup-jellyfin-config-$(date +%F-%H%M).tgz config
ls -lh /opt/media-server/backup-jellyfin-config-*.tgz

# 2. Install the plugin (check the sums against the ones package_release.sh printed).
sha256sum ~/LiveTvAccess_0.1.0.0.tar.gz
sudo tar --no-same-owner -xzf ~/LiveTvAccess_0.1.0.0.tar.gz -C /opt/media-server/jellyfin/config/plugins/
sudo chown -R 1000:1000 /opt/media-server/jellyfin/config/plugins/LiveTvAccess_0.1.0.0
sha256sum /opt/media-server/jellyfin/config/plugins/LiveTvAccess_0.1.0.0/*

# 3. Option A only: put the config in place before the start.
sudo install -o 1000 -g 1000 -m 0600 ~/Jellyfin.Plugin.LiveTvAccess.xml \
  /opt/media-server/jellyfin/config/plugins/configurations/Jellyfin.Plugin.LiveTvAccess.xml
rm ~/Jellyfin.Plugin.LiveTvAccess.xml

# 4. Start and watch the plugin come up.
docker compose start jellyfin
docker compose logs -f --since 2m jellyfin 2>&1 | grep -E "LiveTvAccess|Startup complete"
```

Expected log lines, in roughly this order:

```
Loaded plugin: LiveTvAccess 0.1.0.0
LiveTvAccess plugin 0.1.0.0 loaded
LiveTvAccess: config loaded (version 0), tuners [tuner1, tuner2], 2 user assignment(s)
LiveTvAccess: initial sync complete; Live TV access enforcement is active
LiveTvAccess: self-check passed: tuner1 (N channels, 1 users), tuner2 (M channels, 1 users)
```

- For option B, the config line and the self-check pass appear after you save in the dashboard. Do that now.
- If you see `SELF-CHECK FAILED`, go to Troubleshooting. Admins keep working while you fix it.

## 4. Verify

**Self-check status (VPS)**, from inside the container (curl is in the image):

```sh
read -rs KEY    # paste the admin API key, press Enter (not echoed, not in history)
docker compose exec -T -e KEY="$KEY" jellyfin sh -c \
  'curl -s -H "Authorization: MediaBrowser Token=\"$KEY\"" "http://127.0.0.1:8096/LiveTvAccess/Status?refresh=true"' \
  | python3 -m json.tool
unset KEY
```

Expect:
- `"State": "Healthy"` and every entry in `Checks` with `"Ok": true`;
- `Tuners` with the channel counts you noted and `"InJellyfin": true`;
- `Duplicates` empty.

The response never contains URLs; tuners show as `scheme://host` or `file`.

**Behaviour.** Use the web client first, then Android TV and iOS (Gate 5).
- [ ] A sees only tuner 1's channels in Live TV and the guide, and a channel plays.
- [ ] A starts a second Live TV stream on another device: it fails with "Your tuner is busy with another live
      stream" (HTTP 409).
- [ ] B sees only tuner 2's channels, and a channel plays.
- [ ] C sees no Live TV.
- [ ] An admin sees everything.
- [ ] Movies and music still work for A, B and C.
- [ ] Internal buffer URLs are closed through Caddy (from anywhere). Expect `403`:

      curl -s -o /dev/null -w '%{http_code}\n' https://YOUR-DOMAIN/LiveTv/LiveStreamFiles/0123456789abcdef0123456789abcdef/stream.ts

- [ ] Tuner URLs are hidden from non-admins. Signed in as A in a browser, open
      `https://YOUR-DOMAIN/System/Configuration/livetv`. The `Url` of every tuner is empty, and the `Password` and
      `Username` of every guide provider are empty.
- [ ] `docker compose logs jellyfin | grep -c "LiveTvAccess: denied"` stays low during normal use. Each line names
      the reason and never a full token or URL.

## 5. Troubleshooting

`GET /LiveTvAccess/Status` (or the Status section of the settings page) names each problem. While it is `Failed`,
non-admins get "This Live TV channel isn't available for your account." for Live TV, and admins and library use are
unaffected. It recovers by itself within a minute of the fix, or immediately with `?refresh=true` or "Run check now".

| Problem | Fix |
|---|---|
| `No tuners are configured` | Configure the plugin (section 2) |
| `Tuner 'x': its URL is not a Jellyfin M3U tuner` | Make the plugin URL exactly equal to the Jellyfin tuner's URL or path |
| `Tuner 'x': no channels in the library` | Run Refresh Guide and wait for it, or check the playlist |
| `Jellyfin endpoint drift: ...` | Jellyfin isn't 10.11.11 (or was upgraded). Roll back the image tag, or roll back the plugin |
| `Claim contract broken` | Same cause. Admins can't be recognized either, so roll back |
| `The LiveTvAccess request filter is not registered` | Restart Jellyfin. If it persists, roll back |
| Warning: stream URL listed by more than one tuner | Remove it from one playlist (`m3u_overlap.py`), then Refresh Guide |

## 6. Rollback (VPS)

**Quick rollback (keeps all data):**

```sh
cd /opt/media-server
docker compose stop jellyfin
sudo mv /opt/media-server/jellyfin/config/plugins/LiveTvAccess_0.1.0.0 /opt/media-server/LiveTvAccess_0.1.0.0.disabled
docker compose start jellyfin
```

Leftovers:
- The plugin added `livetv-...` entries to each user's **Block items with tags** (Dashboard > Users > user > Parental
  control) and `livetv-...` tags to channels. After a rollback, users still don't see the other tuner's channels in
  lists, but nothing stops playback any more.
- Remove the `livetv-tuner1`, `livetv-tuner2` and `livetv-unassigned` entries from each user's blocked tags. The
  channel tags are harmless.
- The plugin config file can stay; it's ignored without the plugin.

**Full rollback (back to the exact pre-deploy state):** this restores the config and database from the backup. Any
changes since the backup (watch history, settings) are lost.

```sh
cd /opt/media-server
docker compose stop jellyfin
sudo mv /opt/media-server/jellyfin/config /opt/media-server/jellyfin/config.after-plugin
sudo mkdir /opt/media-server/jellyfin/config
sudo tar -C /opt/media-server/jellyfin -xzf /opt/media-server/backup-jellyfin-config-YYYY-MM-DD-HHMM.tgz
sudo chown -R 1000:1000 /opt/media-server/jellyfin/config
docker compose start jellyfin
```

Delete `config.after-plugin` once you're satisfied. It contains the plugin config, which may hold provider URLs.

## 7. Later

- **Upgrading Jellyfin:** don't, until the plugin has been re-tested against the new version locally (the full test
  run and Gate 3). The pinned image tag protects you; on an unplanned upgrade, the self-check's failed state limits
  the damage to "Live TV unavailable for non-admins".
- **Changing tuners or users:** use the settings page. Changes apply without a restart. After changing a tuner URL or
  a playlist, run Refresh Guide.
