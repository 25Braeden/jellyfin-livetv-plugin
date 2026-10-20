# Gate 5: real-client test plan

A checklist to follow by hand with real clients against the **local** test server, then a short plan for the first
production day. Nothing here changes code. Record each result (pass/fail, device, app version, time) as you go. On any
**fail**, copy the matching log lines (section 0.4) before trying anything else.

Accounts: use only the test accounts **A**, **B** and **C** (no password) plus your admin account. Never use real
users' accounts for these tests.

| Account | Plugin mapping | Native "Allow Live TV access" | Expected Live TV |
|---|---|---|---|
| A | tuner1 | on | tuner 1 channels only: Test News, Test Sports, Shared Channel, Test Live TS (4) |
| B | tuner2 | on | tuner 2 channels only: Test Movies, Test Kids, Shared Channel (3) |
| C | none | off | no Live TV at all |
| admin | not restricted | on | all 7 channels |

The two "Shared Channel" entries are different channels, one per tuner; each user sees only their own.

---

## 0. Setup (local)

### 0.1 Start the stack and confirm it is healthy (Mac)

```sh
cd ~/Projects/jellyfin-livetv-plugin
docker compose up -d
scripts/deploy_local.sh            # current plugin build
curl -s http://localhost:8096/health   # -> Healthy
```

In Dashboard > Plugins > LiveTvAccess, the Status section must show **Healthy** with all checks OK and the tuner counts
above (tuner1: 4 channels, 1 user; tuner2: 3 channels, 1 user). If the guide looks empty, run Dashboard > Scheduled
Tasks > Refresh Guide once (the local guide fixture covers about 48 h from when it was written; Gate 3 regenerates it).

### 0.2 Reach the server from the phone and TV (same Wi-Fi)

1. Find the Mac's LAN address: `ipconfig getifaddr en0` (Wi-Fi). Example: `192.168.1.23`.
2. The server URL for every client is `http://<that address>:8096`. Plain HTTP: **local testing has no Caddy and no
   HTTPS**.
3. `docker-compose.yml` already publishes port 8096 on all interfaces (and the fake stream server on 8081). If macOS asks
   whether Docker may accept incoming connections, allow it. If the firewall blocks it: System Settings > Network >
   Firewall > Options, and allow Docker.
4. Check from the phone's browser: `http://<address>:8096/health` should show `Healthy`.

### 0.3 Afterwards: close it again

- [ ] `docker compose down` when finished (or, to keep it running, change the port mappings in `docker-compose.yml` to
      `127.0.0.1:8096:8096` and `127.0.0.1:8081:80` and run `docker compose up -d`; don't commit that change unless you
      want it permanently).
- [ ] Remove the macOS firewall allowance for Docker if you added one.
- [ ] Sign the test accounts out on the phone and TV (or remove the server from the apps).

### 0.4 Where to look

| What | Where |
|---|---|
| Plugin decisions | `docker logs -f jellyfin-dev 2>&1 \| grep LiveTvAccess` |
| A refusal by the plugin | `LiveTvAccess: denied <Decision> user=<id> <METHOD> <route> <what>=<detail>` (for example `DenyOtherTuner`, `DenyNoTuner`, `DenyNoLiveTvAccess`, `DenyUntrackedStream`) |
| Second stream refused (one-stream rule) | `LiveTvAccess: tuner busy for user=<id> ...` (the client gets HTTP 409) |
| Streams opening and closing | `Opening channel stream from ...`, `Live stream opened after ...ms`, `Closing live stream ...`, `Live stream ... closed successfully` |
| Upstream requests (fake provider) | `docker logs --since 10m fakestreams 2>&1 \| grep -c "ch=news1"` (one line per upstream request; channel keys: `news1`, `sports1`, `shared1`, ...) |
| Who is playing what | Dashboard home page (Active devices / now playing) and Dashboard > Activity |
| Plugin health | Dashboard > Plugins > LiveTvAccess (Status section), or `GET /LiveTvAccess/Status` as admin |
| User IDs (to read log lines) | Dashboard > Users > click the user; the URL has `userId=...` |

Upstream-URL markers that must never appear on a client screen or in a non-admin API response: `fakestreams`,
`/tuners/`, `ch=`, `LiveStreamFiles`.

---

## 1. Clients

Run section 2 on each client, then section 3 and section 4 with the device combinations given there.

| Client | How to add the server |
|---|---|
| Jellyfin web in **Safari** (Mac or iPhone) | open `http://<address>:8096` |
| Official **Jellyfin** app on **iPhone** (Jellyfin Mobile) | Add server > `http://<address>:8096` |
| **Jellyfin** app on **Fire TV / Android TV** | Add server > `<address>:8096` |

For Safari, use a private window per account so sign-ins don't mix. For the apps, use the app's "switch user" or "sign
out", or add the server once per account.

---

## 2. Per client and per account

Do 2.1 to 2.6 for A, B and C on every client. In the result table, mark each cell pass/fail.

### 2.1 Channel list
- [ ] **A:** Live TV > Channels shows exactly the 4 tuner-1 channels.
- [ ] **B:** exactly the 3 tuner-2 channels.
- [ ] **C:** no Live TV section, tab or tile at all.
- **Pass:** exactly those channels; the counts match.
- **Fail:** any other tuner's channel is listed, or a list is empty for A/B (empty: check the plugin Status first; it may
  still be syncing or failed).
- **Look at:** plugin Status page; `LiveTvAccess: denied ... GET LiveTv/Channels ...` if the list fails to load.

### 2.2 Guide
- [ ] Open the guide (TV guide / Programs). A and B see programmes only for their own channels, with names and times.
- [ ] Search (if the client has it) for "Test": A/B find only their own channels; C finds no channel results.
- **Pass:** the guide rows match the channel list from 2.1.
- **Fail:** a row for another tuner's channel; or an error loading the guide. Copy the `LiveTvAccess: denied` line;
  `GetPrograms` problems show as `POST LiveTv/Programs`.

### 2.3 Start playback
- [ ] A plays Test News; B plays Test Movies.
- **Pass:** video starts within about 10 s, with picture and sound.
- **Fail:** an error, or a spinner that never ends. Look for:
  - `LiveTvAccess: denied ...` for that user. That's a plugin false positive: **stop and report** the full line, the
    client and its version.
  - `tuner busy` means another stream of this user is still open (see 3.1 and 4.1).
- Also check the Dashboard home page: the session shows the channel playing.

### 2.4 Switch channels
- [ ] While playing, switch to another of the user's channels (A: Test Sports; B: Test Kids), then back.
- **Pass:** each switch plays within about 10 s. The old stream closes: `Closing live stream ...` appears, possibly with
  a delay of up to about a minute.
- **Fail:** the switch shows "tuner busy" or an error, *and* the log shows `tuner busy` for this user, *and* it still
  fails after waiting 90 s. That means the previous stream wasn't released. Note how long it took to recover.

### 2.5 Stop and restart
- [ ] Stop playback (back out to the channel list), wait 5 s, start the same channel again.
- [ ] Then stop, wait 90 s, and start a *different* own channel.
- **Pass:** both restarts play.
- **Fail:** "tuner busy" that doesn't clear within 90 s; note the time and the last `Closing live stream` line.

### 2.6 No upstream URL anywhere
- [ ] On every client, open any "Media info", "Playback info", "Playback data" or statistics screen the player offers
      while a channel plays, and look at the channel's details page.
- [ ] Safari only: Develop > Show Web Inspector > Network. Reload the Live TV page, start a channel, then use the
      search box in the Network tab for `fakestreams`, `/tuners/` and `ch=`.
- **Pass:** none of the markers appear anywhere. Paths show as empty, or as server URLs (`/videos/...`, `/hls/...`).
- **Fail:** any marker visible to A, B or C. **Stop and report.** This is a leak, even though the local fake URLs carry
  no real secret.

---

## 3. One-stream rule

Use two devices (for example iPhone and Safari, or TV and iPhone).

### 3.1 A, two devices, two different channels: the second must be refused
- [ ] Device 1 (A): play Test News. Device 2 (A): play Test Sports.
- **Pass:** device 2 gets an error (the app may say "playback failed" or similar; the wording varies by app), and the
  log shows `LiveTvAccess: tuner busy for user=<A's id>` (HTTP 409). Device 1 keeps playing.
- **Fail:**
  - Both play: one-stream rule broken. **Stop and report**, with `docker logs --since 5m fakestreams | grep -E "ch=(news1|sports1)"`
    (two different upstream channels at once).
  - Or device 1 stops when device 2 is refused.

### 3.2 A, two devices, the same channel: should share
- [ ] Device 1 (A): play Test News. Device 2 (A): play Test News too.
- **Pass:** both play. Expected: a single upstream connection for it, so `docker logs --since 5m fakestreams | grep -c ch=news1`
  stays at about 1 after device 2 starts, and only one `Opening channel stream` line appears.
- **Fail:** device 2 gets "tuner busy". Note it; that's sharing not happening for this client's request shape. It is not
  a security failure, but it should be reported.

### 3.3 A and B at the same time: both must work
- [ ] A (device 1): Test News. B (device 2): Test Movies, at the same time.
- **Pass:** both play continuously for 2 minutes.
- **Fail:** either one is refused or stops. Copy the `denied` or `tuner busy` lines.

---

## 4. Failure cases

### 4.1 Force-quit mid-stream, then reopen
- [ ] A plays Test News on the iPhone (or TV). Force-quit the app (iPhone: swipe it away; Fire TV/Android TV: Settings >
      Applications > Jellyfin > Force stop).
- [ ] Reopen at once and play **Test News** again.
- [ ] Force-quit again. Reopen and play **Test Sports** (a different channel) at once, and again after 2 minutes.
- **Pass:**
  - The same channel plays at once (the open stream is reused).
  - A different channel is refused with "tuner busy" for at most about 2 minutes, then plays. Jellyfin kills an
    abandoned HLS transcode after 60 s without requests, then closes the stream; look for `Closing live stream ...`.
- **Fail:** "tuner busy" still after 3 minutes (stuck tuner). Note the time of the last `Live stream opened` and whether
  `Closing live stream` ever appears. Recovery: Dashboard home page > the stale session > stop. If that doesn't free it,
  restart the container.

### 4.2 User renamed in the dashboard
- [ ] As admin: Dashboard > Users > A > rename to `A-renamed` > Save. Then sign in as `A-renamed` on one client.
- **Pass:** exactly the same as 2.1 to 2.3 for A. The plugin maps users by ID, so a rename changes nothing. The plugin
  settings page shows the new name after its next save.
- **Fail:** A loses Live TV, or gains another tuner. Copy any `LiveTvAccess` lines.
- [ ] Rename back to `A`.

### 4.3 Tuner assignment removed while watching
- [ ] A plays Test News on one client.
- [ ] As admin in another browser: Dashboard > Plugins > LiveTvAccess, remove A's user row, then Save.
- **Pass:**
  - Playback stops within about 30 s. The next HLS segment request is refused, with
    `LiveTvAccess: denied DenyNoTuner user=<A's id> GET ...` in the log, and the client shows an error.
  - A's channel list is then empty, or the client shows "This Live TV channel isn't available for your account."
- **Fail:** A keeps playing beyond 60 s, or can start another channel. Copy the log.
- [ ] Add A back (tuner1), then Save. A can play again within about 30 s.

---

## 5. Result table

| Check | Safari | iPhone app | Fire TV / Android TV app |
|---|---|---|---|
| 2.1 A / B / C channel lists | | | |
| 2.2 guide | | | |
| 2.3 start playback | | | |
| 2.4 switch channels | | | |
| 2.5 stop / restart | | | |
| 2.6 no upstream URL | | | |
| 3.1 A two channels: second refused | (pair) | | |
| 3.2 A same channel twice: shares | (pair) | | |
| 3.3 A + B together | (pair) | | |
| 4.1 force-quit and reopen | n/a | | |
| 4.2 rename | | | |
| 4.3 assignment removed while watching | | | |

---

## 6. Production day (first deploy)

Follow `docs/deploy.md`. This section adds the order of testing and a stop criterion for each step. **Rollback** means
`docs/deploy.md` section 6: quick rollback, or the full restore if data changed in a way you don't want.

Two things only production can show:
- **Caddy's forwarded headers.** Local testing has no Caddy; the fix 6 rule was verified locally only by simulating the
  headers, so it must be checked once in production (step 4).
- **The real provider:** its connection limit, stream format and the real playlists.

| Step | Do | Stop and roll back if |
|---|---|---|
| 0. Pre-flight | Everything in `docs/deploy.md` section 1, including the **hard stop**: `m3u_overlap.py` reports zero overlaps. Note the time, and tell users Live TV is down for a short window. | Any pre-flight item fails. Don't deploy (no rollback needed). |
| 1. Deploy | `docs/deploy.md` section 3: stop Jellyfin, back up, install the plugin owned by uid 1000, pre-seed the config (option A), start. Watch the log for `LiveTvAccess plugin 0.1.0.0 loaded`, `initial sync complete`, `self-check passed`. | Jellyfin doesn't start, the plugin doesn't load, or `SELF-CHECK FAILED` doesn't clear within 10 minutes after fixing the config. |
| 2. Config immediately | Option A: already in place. Option B: Dashboard > Plugins > LiveTvAccess within a minute of the start; add tuners and users, then Save. Check `GET /LiveTvAccess/Status`: Healthy, the expected channel counts, no duplicates. | Status isn't Healthy, the channel counts are wrong, or `Duplicates` isn't empty. |
| 3. Your own account first (admin) | Play one channel from each tuner, and a movie. Check Dashboard > Plugins > LiveTvAccess. | Admin can't play Live TV or library items. |
| 4. Buffer URL through Caddy (production only) | From anywhere outside: `curl -s -o /dev/null -w '%{http_code}\n' https://YOUR-DOMAIN/LiveTv/LiveStreamFiles/0123456789abcdef0123456789abcdef/stream.ts` gives `403`. Then, as admin, play a `.ts` channel for a minute. | The curl gives `200` (the buffer rule isn't effective behind Caddy); or `.ts` channels fail to play while the log shows `denied DenyBufferNotLocal ... buffer=not from the server` for Jellyfin's own reads (the server's address detection doesn't match production networking). |
| 5. Each user, one at a time | A on one device: section 2 (own channels only, guide, play, switch), then 3.1 with a second device. Then B the same way, then 3.3 (A and B together). Then C: no Live TV. Then 2.6 on one client per user. | **Immediately** if any user sees or plays another tuner's channel, or any upstream URL is visible to a user. If a user can't play their own channel: copy the `denied` line, then decide between rolling back and leaving Live TV down for that user while you report it. |
| 6. First day | Every few hours: `docker compose logs --since 3h jellyfin \| grep -c "LiveTvAccess: denied"` (should be low), and Status Healthy. | Status Failed that you can't fix quickly, or users reporting playback refused on their own channels. |

After a successful first day: keep the backup for at least a week, delete any local copy of the pre-seeded config file,
and record the date and the plugin package SHA-256 in `docs/progress.md`.
