#!/usr/bin/env python3
"""Throwaway experiment: prove (or disprove) the "no-code" per-user Live TV model.

LOCAL ONLY. Talks to http://localhost:8096 and reads `docker logs fakestreams`.
Reads JELLYFIN_API_KEY from ./.env. The key is only ever sent in a request header;
it is never printed, logged, or written anywhere.

Usage:
    python3 scripts/nocode_experiment.py setup     # tuner limits, guide, tags, blocked tags
    python3 scripts/nocode_experiment.py report    # what users A, B, C see
    python3 scripts/nocode_experiment.py refresh   # run a guide refresh, re-check tags
    python3 scripts/nocode_experiment.py share ["Test Live TS"]  # sharing + second-channel test as A
    python3 scripts/nocode_experiment.py all
"""

import datetime as dt
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

BASE = "http://localhost:8096"
ROOT = pathlib.Path(__file__).resolve().parent.parent
# LIVETV_FIXTURES=scale switches to the generated (gitignored) playlists in tuners/scale/.
FIXTURES = os.environ.get("LIVETV_FIXTURES", "").strip("/")
_SUB = f"{FIXTURES}/" if FIXTURES else ""
TUNERS = {"tuner1": f"/tuners/{_SUB}tuner1.m3u", "tuner2": f"/tuners/{_SUB}tuner2.m3u"}


def tuner_file(tuner):
    """Local path of a tuner's playlist (the container sees it at TUNERS[tuner])."""
    return ROOT / "tuners" / _SUB / f"{tuner}.m3u"

TAGS = {"tuner1": "livetv-tuner1", "tuner2": "livetv-tuner2"}
BLOCKED = {"A": ["livetv-tuner2"], "B": ["livetv-tuner1"]}
GUIDE_FILE = ROOT / "tuners" / "guide.xml"  # gitignored (tuners/*)
GUIDE_PATH_IN_CONTAINER = "/tuners/guide.xml"
CLIENT = 'MediaBrowser Client="nocode-experiment", Device="script", DeviceId="{dev}", Version="0.1"'


# ---------------------------------------------------------------- helpers

def load_api_key():
    env = ROOT / ".env"
    if not env.exists():
        sys.exit("ERROR: .env not found (needs JELLYFIN_API_KEY=...)")
    for line in env.read_text().splitlines():
        line = line.strip()
        if line.startswith("JELLYFIN_API_KEY="):
            value = line.split("=", 1)[1].strip().strip('"').strip("'")
            if value:
                return value
    sys.exit("ERROR: JELLYFIN_API_KEY missing or empty in .env")


class Api:
    def __init__(self, token, device):
        self.auth = CLIENT.format(dev=device) + f', Token="{token}"'

    def call(self, method, path, body=None, query=None, raw=False, timeout=60):
        url = BASE + path
        if query:
            url += "?" + urllib.parse.urlencode(query, doseq=True)
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method, headers={
            "Authorization": self.auth, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                payload = r.read()
                if raw:
                    return r.status, payload
                return r.status, (json.loads(payload) if payload else None)
        except urllib.error.HTTPError as e:
            # Never echo request headers; body text only.
            return e.code, e.read().decode(errors="replace")[:300]

    def get(self, path, **q):
        return self.call("GET", path, query=q or None)

    def post(self, path, body=None, **q):
        return self.call("POST", path, body=body, query=q or None)


def ok(resp, what):
    status, payload = resp
    if status >= 300:
        sys.exit(f"ERROR: {what} -> HTTP {status}: {payload}")
    return payload


def login(username, device):
    req = urllib.request.Request(
        BASE + "/Users/AuthenticateByName",
        data=json.dumps({"Username": username, "Pw": ""}).encode(), method="POST",
        headers={"Content-Type": "application/json", "Authorization": CLIENT.format(dev=device)})
    with urllib.request.urlopen(req, timeout=30) as r:
        res = json.load(r)
    return Api(res["AccessToken"], device), res["User"]["Id"], res["SessionInfo"]["Id"]


def dotnet_md5_guid(s):
    """Jellyfin's string.GetMD5(): MD5 over UTF-16LE, as a .NET Guid, formatted "N"."""
    return uuid.UUID(bytes_le=hashlib.md5(s.encode("utf-16-le")).digest()).hex


def expected_channels():
    """channel item id -> (tuner, name). Mirrors M3UTunerHost.cs:64-67, M3uParser.cs:104,
    LiveTvDtoService.cs:403-408, LibraryManager.cs:636-657."""
    out = {}
    for tuner, url in TUNERS.items():
        lines = tuner_file(tuner).read_text().splitlines()
        for i, line in enumerate(lines):
            if line.startswith("#EXTINF") and i + 1 < len(lines):
                ext = "m3u_" + dotnet_md5_guid(url) + dotnet_md5_guid(lines[i + 1].strip())
                item_id = dotnet_md5_guid(
                    "MediaBrowser.Controller.LiveTv.LiveTvChannel" + ("Emby" + ext + "4").lower())
                out[item_id] = (tuner, line.rsplit(",", 1)[-1])
    return out


def admin_user_id(admin):
    users = ok(admin.get("/Users"), "list users")
    return next(u["Id"] for u in users if u["Policy"]["IsAdministrator"])


def users_by_name(admin):
    return {u["Name"]: u for u in ok(admin.get("/Users"), "list users")}


def run_task(admin, key, timeout_s=1800):
    tasks = ok(admin.get("/ScheduledTasks"), "list tasks")
    task = next(t for t in tasks if t.get("Key") == key)
    ok(admin.post(f"/ScheduledTasks/Running/{task['Id']}"), f"start {key}")
    time.sleep(2)
    for _ in range(timeout_s):
        t = ok(admin.get(f"/ScheduledTasks/{task['Id']}"), "task state")
        if t["State"] == "Idle":
            return t.get("LastExecutionResult", {}).get("Status")
        time.sleep(1)
    return "timeout"


def write_guide(hours_back=1, hours_ahead=48):
    """Hourly programs from `hours_back` before the current hour to `hours_ahead` after now, for every tvg-id in the
    fixtures. The window is relative to when it is written, so callers regenerate it before use (gate3 does)."""
    ids = []
    for tuner in TUNERS:
        for line in tuner_file(tuner).read_text().splitlines():
            if 'tvg-id="' in line:
                tid = line.split('tvg-id="', 1)[1].split('"', 1)[0]
                if tid not in ids:
                    ids.append(tid)
    start = dt.datetime.now(dt.timezone.utc).replace(minute=0, second=0, microsecond=0) - dt.timedelta(hours=hours_back)
    fmt = lambda t: t.strftime("%Y%m%d%H%M%S +0000")
    parts = ['<?xml version="1.0" encoding="UTF-8"?>', "<tv>"]
    parts += [f'  <channel id="{c}"><display-name>{c}</display-name></channel>' for c in ids]
    for c in ids:
        for h in range(hours_back + hours_ahead + 1):
            s = start + dt.timedelta(hours=h)
            parts.append(f'  <programme start="{fmt(s)}" stop="{fmt(s + dt.timedelta(hours=1))}" channel="{c}">'
                         f'<title>{c} show {h}</title></programme>')
    parts.append("</tv>")
    GUIDE_FILE.write_text("\n".join(parts) + "\n")


# ---------------------------------------------------------------- steps

def setup(admin):
    print("== setup")
    # 1. Stream limit 1 on both M3U tuners.
    cfg = ok(admin.get("/System/Configuration/livetv"), "livetv config")
    for host in cfg["TunerHosts"]:
        if host["Type"] == "m3u":
            host["TunerCount"] = 1
            ok(admin.post("/LiveTv/TunerHosts", host), "save tuner host")
            print(f"  tuner {host['Url']}: TunerCount=1")

    # 2. Local XMLTV guide so guide / On Now have data.
    write_guide()
    cfg = ok(admin.get("/System/Configuration/livetv"), "livetv config")
    if not any(p.get("Path") == GUIDE_PATH_IN_CONTAINER for p in cfg.get("ListingProviders", [])):
        ok(admin.post("/LiveTv/ListingProviders",
                      {"Type": "xmltv", "Path": GUIDE_PATH_IN_CONTAINER, "EnableAllTuners": True}),
           "add xmltv provider")
        print("  added XMLTV listings provider")
    print(f"  guide refresh: {run_task(admin, 'RefreshGuide')}")

    # 3. Tag channels by tuner (item IDs computed from tuner URL + stream URL).
    tag_channels(admin)

    # 4. Blocked tags for A and B.
    users = users_by_name(admin)
    for name, blocked in BLOCKED.items():
        policy = users[name]["Policy"]
        policy["BlockedTags"] = blocked
        ok(admin.post(f"/Users/{users[name]['Id']}/Policy", policy), f"policy {name}")
        print(f"  user {name}: BlockedTags={blocked}")
    print(f"  user C: EnableLiveTvAccess={users['C']['Policy']['EnableLiveTvAccess']}")


def tag_channels(admin):
    aid = admin_user_id(admin)
    expected = expected_channels()
    chans = ok(admin.get("/LiveTv/Channels", userId=aid), "admin channels")["Items"]
    for c in chans:
        tuner = expected.get(c["Id"], (None,))[0]
        if not tuner:
            print(f"  WARNING: {c['Name']} {c['Id']} matches no fixture entry; not tagged")
            continue
        item = ok(admin.get(f"/Items/{c['Id']}", userId=aid), "get item")
        tags = [t for t in item.get("Tags", []) if t not in TAGS.values()] + [TAGS[tuner]]
        item["Tags"] = tags
        ok(admin.post(f"/Items/{c['Id']}", item), "update item")
        print(f"  tagged {c['Name']} ({tuner}) -> {tags}")


def channel_tags(admin):
    aid = admin_user_id(admin)
    out = {}
    for c in ok(admin.get("/LiveTv/Channels", userId=aid), "admin channels")["Items"]:
        item = ok(admin.get(f"/Items/{c['Id']}", userId=aid), "get item")
        out[c["Id"]] = (c["Name"], item.get("Tags", []))
    return out


def report(admin):
    print("== report")
    expected = expected_channels()
    label = lambda cid: "{1} [{0}]".format(*expected.get(cid, ("?", cid)))
    hidden_probe = {"A": "tuner2", "B": "tuner1", "C": "tuner1"}
    results = {}
    for name in ("A", "B", "C"):
        api, uid, _ = login(name, f"report-{name}")
        r = {}
        st, chans = api.get("/LiveTv/Channels", userId=uid)
        r["channels"] = sorted(label(c["Id"]) for c in chans["Items"]) if st == 200 else f"HTTP {st}"
        visible_ids = [c["Id"] for c in chans["Items"]] if st == 200 else []

        # Guide grid as the web client does it: programs for the visible channel IDs.
        st, progs = api.post("/LiveTv/Programs", {"UserId": uid, "ChannelIds": visible_ids, "Limit": 500})
        r["guide (visible channel ids)"] = (sorted({label(p["ChannelId"]) for p in progs["Items"]})
                                            if st == 200 else f"HTTP {st}")
        # Programs with no channel filter (Programs / Movies / Shows tabs).
        st, progs = api.get("/LiveTv/Programs", userId=uid, limit=500)
        r["programs (no channel filter)"] = (sorted({label(p["ChannelId"]) for p in progs["Items"]})
                                             if st == 200 else f"HTTP {st}")
        # "On Now".
        st, progs = api.get("/LiveTv/Programs/Recommended", userId=uid, isAiring="true", limit=50)
        r["on now"] = (sorted({label(p["ChannelId"]) for p in progs["Items"]})
                       if st == 200 else f"HTTP {st}")
        # Playback of a channel that should be hidden.
        probe = next(cid for cid, (t, _) in expected.items() if t == hidden_probe[name])
        st, _ = api.post(f"/Items/{probe}/PlaybackInfo", {"UserId": uid}, userId=uid)
        r[f"PlaybackInfo on hidden {label(probe)}"] = f"HTTP {st}"
        results[name] = r
    print(json.dumps(results, indent=2))
    return results


def refresh(admin):
    print("== refresh")
    before = channel_tags(admin)
    print(f"  guide refresh: {run_task(admin, 'RefreshGuide')}")
    after = channel_tags(admin)
    for cid, (name, tags) in before.items():
        print(f"  {name} {cid[:8]}: before={tags} after={after.get(cid, (None, 'MISSING'))[1]}")
    return before == after


def fakestreams_requests(since):
    out = subprocess.run(["docker", "logs", "--since", since, "fakestreams"],
                         capture_output=True, text=True).stdout + \
        subprocess.run(["docker", "logs", "--since", since, "fakestreams"],
                       capture_output=True, text=True).stderr
    return [l for l in out.splitlines() if '"GET /' in l or '"HEAD /' in l]


def open_channel(api, uid, channel_id):
    st, info = api.post(f"/Items/{channel_id}/PlaybackInfo",
                        {"UserId": uid, "AutoOpenLiveStream": True}, userId=uid)
    if st != 200:
        return st, info, None
    if info.get("ErrorCode"):
        return st, info.get("ErrorCode"), None
    return st, info["MediaSources"][0], info["PlaySessionId"]


def pull_stream(api, channel_id, ms, play_session, seconds, out):
    q = urllib.parse.urlencode({
        "mediaSourceId": ms["Id"], "liveStreamId": ms["LiveStreamId"], "playSessionId": play_session,
        "static": "false", "videoCodec": "copy", "audioCodec": "copy"})
    req = urllib.request.Request(f"{BASE}/Videos/{channel_id}/stream.ts?{q}", headers={"Authorization": api.auth})
    total, end = 0, time.time() + seconds
    try:
        with urllib.request.urlopen(req, timeout=seconds + 30) as r:
            while time.time() < end:
                chunk = r.read(65536)
                if not chunk:
                    break
                total += len(chunk)
    except Exception as e:  # report, don't crash
        out["error"] = type(e).__name__ + ": " + str(e)[:200]
    out["bytes"] = total


def report_playing(api, ms, play_session, channel_id, stopped=False):
    body = {"ItemId": channel_id, "MediaSourceId": ms["Id"], "LiveStreamId": ms["LiveStreamId"],
            "PlaySessionId": play_session, "PlayMethod": "Transcode"}
    return api.post("/Sessions/Playing/Stopped" if stopped else "/Sessions/Playing", body)[0]


def share(admin, channel_name=None):
    """Same channel from two sessions of user A; optional channel name (e.g. "Test Live TS")."""
    print("== share")
    expected = expected_channels()
    t1 = [cid for cid, (t, _) in expected.items() if t == "tuner1"]
    a_chans = {c["Id"] for c in ok(login("A", "share-probe")[0].get("/LiveTv/Channels"), "chans")["Items"]}
    t1 = [c for c in t1 if c in a_chans]
    if channel_name:
        t1.sort(key=lambda c: expected[c][1] != channel_name)
    first, other = t1[:2]
    print(f"  channel X = {expected[first][1]}, channel Y = {expected[other][1]} (both tuner1)")

    s1, uid, _ = login("A", "share-session-1")
    s2, _, _ = login("A", "share-session-2")
    since = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    time.sleep(1)

    st1, ms1, ps1 = open_channel(s1, uid, first)
    st2, ms2, ps2 = open_channel(s2, uid, first)
    print(f"  session1 open X: HTTP {st1} liveStreamId={ms1.get('LiveStreamId') if ps1 else ms1}")
    print(f"  session2 open X: HTTP {st2} liveStreamId={ms2.get('LiveStreamId') if ps2 else ms2}")
    if not (ps1 and ps2):
        return
    report_playing(s1, ms1, ps1, first)
    report_playing(s2, ms2, ps2, first)

    o1, o2 = {}, {}
    th = [threading.Thread(target=pull_stream, args=(s1, first, ms1, ps1, 10, o1)),
          threading.Thread(target=pull_stream, args=(s2, first, ms2, ps2, 10, o2))]
    for t in th:
        t.start()
    time.sleep(3)
    # While both are playing, session 1 tries a different tuner1 channel.
    st3, ms3, ps3 = open_channel(s1, uid, other)
    print(f"  session1 open Y while X playing: HTTP {st3} -> {ms3 if not ps3 else 'OPENED ' + ms3['LiveStreamId']}")
    for t in th:
        t.join()
    print(f"  pulled bytes: session1={o1} session2={o2}")

    # Clean up so the tuner slot is released.
    for api, ms, ps, cid in ((s1, ms1, ps1, first), (s2, ms2, ps2, first)) + (
            ((s1, ms3, ps3, other),) if ps3 else ()):
        report_playing(api, ms, ps, cid, stopped=True)
        api.post("/LiveStreams/Close", liveStreamId=ms["LiveStreamId"])
    print("  cleaned up (playback stopped + LiveStreams/Close)")

    # nginx logs a request only when it completes; a live (throttled .ts) pull completes when
    # Jellyfin closes the stream, so read the log after cleanup.
    time.sleep(3)
    reqs = fakestreams_requests(since)
    print(f"  fakestreams saw {len(reqs)} request(s) since {since}:")
    for l in reqs:
        print("    " + l)


def bypass(admin):
    """Can C (no Live TV) open a stream? Can A open a hidden channel via a raw openToken?"""
    print("== bypass")
    expected = expected_channels()
    t1 = next(c for c, (t, n) in expected.items() if t == "tuner1" and n == "Test News")
    t2 = next(c for c, (t, n) in expected.items() if t == "tuner2" and n == "Test Movies")

    c_api, c_uid, _ = login("C", "bypass-C")
    st, ms, ps = open_channel(c_api, c_uid, t1)
    print(f"  C PlaybackInfo+AutoOpen on Test News: HTTP {st} -> "
          f"{'OPENED ' + ms['LiveStreamId'] if ps and ms.get('LiveStreamId') else ms}")
    if ps and ms.get("LiveStreamId"):
        c_api.post("/LiveStreams/Close", liveStreamId=ms["LiveStreamId"])

    aid = admin_user_id(admin)
    info = ok(admin.post(f"/Items/{t2}/PlaybackInfo", {"UserId": aid}, userId=aid), "admin playbackinfo")
    token = info["MediaSources"][0]["OpenToken"]
    a_api, a_uid, _ = login("A", "bypass-A")
    st, res = a_api.post("/LiveStreams/Open", {"OpenToken": token, "UserId": a_uid, "ItemId": t2})
    opened = st == 200 and isinstance(res, dict) and res.get("MediaSource", {}).get("LiveStreamId")
    print(f"  A LiveStreams/Open with hidden Test Movies token: HTTP {st} -> "
          f"{'OPENED ' + opened if opened else res}")
    if opened:
        a_api.post("/LiveStreams/Close", liveStreamId=opened)


def main():
    step = sys.argv[1] if len(sys.argv) > 1 else "all"
    admin = Api(load_api_key(), "nocode-admin")
    if step in ("setup", "all"):
        setup(admin)
    if step in ("report", "all"):
        report(admin)
    if step in ("refresh", "all"):
        print(f"  tags survived refresh: {refresh(admin)}")
        if step == "all":
            report(admin)
    if step in ("bypass", "all"):
        bypass(admin)
    if step in ("share", "all"):
        share(admin, sys.argv[2] if len(sys.argv) > 2 else None)


if __name__ == "__main__":
    main()
