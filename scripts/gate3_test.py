#!/usr/bin/env python3
"""Gate 3 acceptance test (LOCAL ONLY): per-user tuner enforcement by the LiveTvAccess plugin.

Checks, for users A (tuner1), B (tuner2), C (no tuner, Live TV disabled) and an API key:
config + tag sync, native visibility, PlaybackInfo / LiveStreams/Open / streaming enforcement
(including both earlier holes and the streaming bypass), cross-user live stream ids, tuner-busy 409,
that A's allowed channels really play (HLS segment), re-tagging after channels are re-created, and
that open tokens never appear in full in the server log.

Temporarily edits tuners/tuner2.m3u (one stream URL) and restores it. Exit code 1 on any failure.
Reads JELLYFIN_API_KEY from .env; never prints it.
"""

import datetime as dt
import json
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import nocode_experiment as n  # noqa: E402
import library_fixture  # noqa: E402

PLUGIN_ID = "4d5aa36c91644f4dba7f0fa6c25bfd28"
CONFIG = {
    "Tuners": [{"Key": k, "Url": url} for k, url in n.TUNERS.items()],
    "Users": [{"UserName": "A", "TunerKey": "tuner1"}, {"UserName": "B", "TunerKey": "tuner2"}],
}
TUNER2_M3U = n.tuner_file("tuner2")
# The plugin's one generic denial message (Core AccessPolicy.DeniedMessage).
DENIED = "This Live TV channel isn't available for your account."
# The original hand-written test channels (present in both the small and the scale fixtures).
TEST_CHANNELS = {"Test News", "Test Sports", "Shared Channel", "Test Live TS", "Test Movies", "Test Kids"}
results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def text(resp):
    return resp[1] if isinstance(resp[1], str) else ""


def raw_get(api, path, timeout=60):
    req = urllib.request.Request(n.BASE + path, headers={"Authorization": api.auth})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def raw_get_limited(api, path, limit=4096, timeout=15):
    """GET reading at most `limit` bytes (for endless live streams)."""
    req = urllib.request.Request(n.BASE + path, headers={"Authorization": api.auth})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read(limit)
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:  # noqa: BLE001 - report timeouts as a status
        return type(e).__name__, b""


def anon_get(path, limit=None, timeout=15):
    """GET with no Authorization header and no api_key."""
    req = urllib.request.Request(n.BASE + path)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read(limit) if limit else r.read(65536)
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:  # noqa: BLE001 - report timeouts as a status
        return type(e).__name__, b""


def channels_by(expected, tuner):
    return {c for c, (t, _) in expected.items() if t == tuner}


def channel(expected, name, tuner):
    return next(c for c, (t, nm) in expected.items() if t == tuner and nm == name)


def visible(api, uid):
    st, res = api.get("/LiveTv/Channels", userId=uid)
    return st, ({c["Id"] for c in res["Items"]} if st == 200 else set())


def wait_for(predicate, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        if predicate():
            return True
        time.sleep(1)
    return False


def channel_tags(admin, aid):
    # One bulk query (works at 16k channels) instead of one request per channel.
    items = n.ok(admin.call("GET", "/LiveTv/Channels", query={"userId": aid, "fields": "Tags"}, timeout=300), "channels")["Items"]
    return {c["Id"]: c.get("Tags", []) for c in items}


def tags_match(admin, aid, expected):
    tags = channel_tags(admin, aid)
    return set(tags) == set(expected) and all(
        f"livetv-{expected[c][0]}" in tags[c] for c in expected)


def stop(api, ms, ps, cid):
    n.report_playing(api, ms, ps, cid, stopped=True)
    api.post("/LiveStreams/Close", liveStreamId=ms["LiveStreamId"])
    api.call("DELETE", "/Videos/ActiveEncodings", query={"deviceId": api.auth.split('DeviceId="')[1].split('"')[0],
                                                          "playSessionId": ps})


def plays(api, uid, cid):
    """Open via PlaybackInfo, fetch HLS master -> media playlist -> first segment. Returns (ok, detail)."""
    st, ms, ps = n.open_channel(api, uid, cid)
    if not ps:
        return False, f"open HTTP {st} {ms}"
    try:
        q = urllib.parse.urlencode({"mediaSourceId": ms["Id"], "liveStreamId": ms["LiveStreamId"], "playSessionId": ps,
                                    "videoCodec": "h264", "audioCodec": "aac", "segmentContainer": "ts"})
        st, master = raw_get(api, f"/Videos/{cid}/master.m3u8?{q}")
        if st != 200:
            return False, f"master HTTP {st}"
        sub = next(l for l in master.decode().splitlines() if l and not l.startswith("#"))
        st, media = raw_get(api, f"/Videos/{cid}/{sub}", timeout=90)
        segs = [l for l in media.decode().splitlines() if l and not l.startswith("#")] if st == 200 else []
        if not segs:
            return False, f"media playlist HTTP {st}"
        st, seg = raw_get(api, f"/Videos/{cid}/{segs[0]}", timeout=90)
        return st == 200 and len(seg) > 0 and seg[:1] == b"G", f"segment HTTP {st}, {len(seg)} bytes"
    finally:
        stop(api, ms, ps, cid)
        time.sleep(2)


def fresh_guide(admin, aid, reason):
    """Self-healing guide fixture. The XMLTV window is relative to when it is written, and Jellyfin reuses its cached
    copy (local/cache/xmltv/{provider id}.xml) for up to 1 h (XmlTvListingsProvider.cs:30, :64-69). So: regenerate
    the fixture (1 h back, 48 h ahead), drop the local cached copy only if it is older than the fixture, refresh the
    guide, and report whether programmes are airing now."""
    n.write_guide(hours_back=1, hours_ahead=48)
    cfg = n.ok(admin.get("/System/Configuration/livetv"), "livetv config")
    for provider in cfg.get("ListingProviders", []):
        if provider.get("Path") == n.GUIDE_PATH_IN_CONTAINER and provider.get("Id"):
            cached = n.ROOT / "local" / "cache" / "xmltv" / (provider["Id"] + ".xml")
            if cached.exists() and cached.stat().st_mtime < n.GUIDE_FILE.stat().st_mtime:
                cached.unlink()
    result = n.run_task(admin, "RefreshGuide")
    st, airing = admin.get("/LiveTv/Programs", userId=aid, isAiring="true", limit=1)
    count = airing.get("TotalRecordCount", 0) if st == 200 else 0
    check(f"guide fixture fresh ({reason}): programmes airing now", result == "Completed" and count > 0, f"refresh {result}, {count} airing")
    return count > 0


def main():
    since = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    admin = n.Api(n.load_api_key(), "gate3-admin")
    aid = n.admin_user_id(admin)
    tokens_used = []

    print("== 0. guide fixture")
    fresh_guide(admin, aid, "setup")

    print("== 1. config + tag sync")
    check("config saved via plugin API", admin.post(f"/Plugins/{PLUGIN_ID}/Configuration", CONFIG)[0] == 204)
    expected = n.expected_channels()
    check("every channel tagged with its tuner (plugin, no script)", wait_for(lambda: tags_match(admin, aid, expected)))
    users = n.users_by_name(admin)
    want = {"A": ["livetv-tuner2", "livetv-unassigned"], "B": ["livetv-tuner1", "livetv-unassigned"],
            "C": ["livetv-tuner1", "livetv-tuner2", "livetv-unassigned"]}
    for name, tags in want.items():
        got = sorted(users[name]["Policy"]["BlockedTags"])
        check(f"{name} blocked tags set by plugin", got == sorted(tags), str(got))

    A, a_uid, _ = n.login("A", "gate3-A")
    B, b_uid, _ = n.login("B", "gate3-B")
    C, c_uid, _ = n.login("C", "gate3-C")
    t1, t2 = channels_by(expected, "tuner1"), channels_by(expected, "tuner2")
    news, movies = channel(expected, "Test News", "tuner1"), channel(expected, "Test Movies", "tuner2")

    print("== 2. visibility (native, from plugin-managed tags)")
    check("A channel list = tuner1 only", visible(A, a_uid) == (200, t1))
    check("B channel list = tuner2 only", visible(B, b_uid) == (200, t2))
    check("C channel list = 403 (Live TV disabled)", visible(C, c_uid)[0] == 403)
    check("API key channel list = all", visible(admin, aid) == (200, t1 | t2))
    st, onnow = A.get("/LiveTv/Programs/Recommended", userId=a_uid, isAiring="true", limit=50)
    check("A On Now only tuner1 channels", st == 200 and {p["ChannelId"] for p in onnow["Items"]} <= t1)

    print("== 3. PlaybackInfo")
    check("A PlaybackInfo own channel = 200", A.post(f"/Items/{news}/PlaybackInfo", {"UserId": a_uid})[0] == 200)
    r = A.post(f"/Items/{movies}/PlaybackInfo", {"UserId": a_uid})
    check("A PlaybackInfo tuner2 channel = 403 + generic message", r[0] == 403 and text(r) == DENIED, text(r))
    r = A.get(f"/Items/{movies}/PlaybackInfo", userId=a_uid)
    check("A GET PlaybackInfo tuner2 channel = 403", r[0] == 403)
    check("B PlaybackInfo own channel = 200", B.post(f"/Items/{movies}/PlaybackInfo", {"UserId": b_uid})[0] == 200)
    check("B PlaybackInfo tuner1 channel = 403", B.post(f"/Items/{news}/PlaybackInfo", {"UserId": b_uid})[0] == 403)
    r = C.post(f"/Items/{news}/PlaybackInfo", {"UserId": c_uid, "AutoOpenLiveStream": True})
    check("C PlaybackInfo+AutoOpen = 403 (hole 1 closed)", r[0] == 403, text(r))
    check("API key PlaybackInfo tuner2 = 200", admin.post(f"/Items/{movies}/PlaybackInfo", {"UserId": aid}, userId=aid)[0] == 200)

    print("== 4. LiveStreams/Open")
    t2_token = n.ok(admin.post(f"/Items/{movies}/PlaybackInfo", {"UserId": aid}, userId=aid), "pi")["MediaSources"][0]["OpenToken"]
    t1_token = n.ok(admin.post(f"/Items/{news}/PlaybackInfo", {"UserId": aid}, userId=aid), "pi")["MediaSources"][0]["OpenToken"]
    crafted = f"{t2_token.split('_')[0]}_LiveTvChannel_{movies}"
    garbage = f"{t2_token.split('_')[0]}_LiveTvChannel_not-a-guid"
    tokens_used += [t1_token, t2_token, crafted]
    r = A.post("/LiveStreams/Open", {"OpenToken": t2_token, "UserId": a_uid, "ItemId": news})
    check("A open tuner2 token (with allowed itemId) = 403 (hole 2 closed)", r[0] == 403, text(r))
    check("A open hand-built tuner2 token = 403", A.post("/LiveStreams/Open", {"OpenToken": crafted, "UserId": a_uid})[0] == 403)
    check("A open token via query string = 403", A.call("POST", "/LiveStreams/Open", query={"openToken": t2_token})[0] == 403)
    check("A open malformed Live TV token = 403 (fail closed)", A.post("/LiveStreams/Open", {"OpenToken": garbage, "UserId": a_uid})[0] == 403)
    check("C open tuner1 token = 403", C.post("/LiveStreams/Open", {"OpenToken": t1_token, "UserId": c_uid})[0] == 403)
    st, res = A.post("/LiveStreams/Open", {"OpenToken": t1_token, "UserId": a_uid, "ItemId": news})
    check("A open own token = 200", st == 200)
    if st == 200:
        A.post("/LiveStreams/Close", liveStreamId=res["MediaSource"]["LiveStreamId"])
    st, res = admin.post("/LiveStreams/Open", {"OpenToken": t2_token, "UserId": aid, "ItemId": movies})
    check("API key open tuner2 token = 200", st == 200)
    if st == 200:
        admin.post("/LiveStreams/Close", liveStreamId=res["MediaSource"]["LiveStreamId"])

    print("== 5. streaming endpoints without a live stream (hole 3)")
    stream_since = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    time.sleep(1)
    # Valid requests (mediaSourceId is required on HLS, else ASP.NET returns 400 before any filter).
    ms_movies = n.ok(admin.post(f"/Items/{movies}/PlaybackInfo", {"UserId": aid}, userId=aid), "pi")["MediaSources"][0]["Id"]
    ms_news = n.ok(admin.post(f"/Items/{news}/PlaybackInfo", {"UserId": aid}, userId=aid), "pi")["MediaSources"][0]["Id"]
    st, _ = raw_get(A, f"/Videos/{movies}/stream.ts?mediaSourceId={ms_movies}&static=false&videoCodec=copy&audioCodec=copy")
    check("A /Videos/{tuner2}/stream.ts = 403", st == 403, f"HTTP {st}")
    st, _ = raw_get(C, f"/Videos/{news}/master.m3u8?mediaSourceId={ms_news}&videoCodec=h264&audioCodec=aac")
    check("C /Videos/{tuner1}/master.m3u8 = 403", st == 403, f"HTTP {st}")
    time.sleep(2)
    check("no upstream request from blocked streaming calls", not n.fakestreams_requests(stream_since))

    print("== 6. someone else's live stream id")
    st, ms_b, ps_b = n.open_channel(B, b_uid, movies)
    if ps_b:
        lid = ms_b["LiveStreamId"]
        st, _ = raw_get(A, f"/Videos/{news}/master.m3u8?liveStreamId={urllib.parse.quote(lid)}&mediaSourceId={ms_b['Id']}")
        check("A streams B's live stream id = 403", st == 403, f"HTTP {st}")
        check("A closes B's live stream = 403", A.post("/LiveStreams/Close", liveStreamId=lid)[0] == 403)
        stop(B, ms_b, ps_b, movies)
    else:
        check("B opens own channel", False, f"HTTP {st} {ms_b}")

    print("== 7. tuner busy")
    live = channel(expected, "Test Live TS", "tuner1")
    s1, _, _ = n.login("A", "gate3-A-s1")
    s2, _, _ = n.login("A", "gate3-A-s2")
    st1, ms1, ps1 = n.open_channel(s1, a_uid, live)
    st2, ms2, ps2 = n.open_channel(s2, a_uid, live)
    check("A same channel from 2 sessions shares one stream", ps1 and ps2 and ms1["LiveStreamId"] == ms2["LiveStreamId"])
    r = s1.post(f"/Items/{news}/PlaybackInfo", {"UserId": a_uid, "AutoOpenLiveStream": True}, userId=a_uid)
    check("A second channel while busy = 409 'tuner busy' (not 500)", r[0] == 409 and "busy" in text(r), f"HTTP {r[0]}: {text(r)}")
    for api, ms, ps in ((s1, ms1, ps1), (s2, ms2, ps2)):
        if ps:
            stop(api, ms, ps, live)
    time.sleep(2)

    print("== 8. A's allowed channels still play (HLS segment)")
    for cid in sorted((c for c in t1 if expected[c][1] in TEST_CHANNELS), key=lambda c: expected[c][1]):
        ok, detail = plays(A, a_uid, cid)
        check(f"A plays {expected[cid][1]}", ok, detail)

    print("== 9. re-tag after channels are re-created")
    original = TUNER2_M3U.read_text()
    try:
        TUNER2_M3U.write_text(original.replace("?ch=kids1", "?ch=kids1-v2"))
        n.run_task(admin, "RefreshGuide")
        new_expected = n.expected_channels()
        new_kids = channel(new_expected, "Test Kids", "tuner2")
        old_kids = channel(expected, "Test Kids", "tuner2")
        check("stream URL change re-created the channel (new id)", new_kids != old_kids and new_kids not in expected)
        check("re-created channel re-tagged by plugin", wait_for(lambda: tags_match(admin, aid, new_expected)))
        check("B sees re-created channel, A does not",
              new_kids in visible(B, b_uid)[1] and new_kids not in visible(A, a_uid)[1])
        check("A PlaybackInfo re-created channel = 403", A.post(f"/Items/{new_kids}/PlaybackInfo", {"UserId": a_uid})[0] == 403)
    finally:
        TUNER2_M3U.write_text(original)
        n.run_task(admin, "RefreshGuide")
    check("fixture restored and channels re-tagged", wait_for(lambda: tags_match(admin, aid, n.expected_channels())))

    print("== 11. sync triggers (user created, admin policy edit)")
    st, dave = admin.post("/Users/New", {"Name": "gate3-temp-user"})
    check("admin creates a user", st == 200, f"HTTP {st}")
    if st == 200:
        try:
            check("new user gets blocked tags (UserCreated event)", wait_for(lambda: sorted(
                n.users_by_name(admin)["gate3-temp-user"]["Policy"]["BlockedTags"]) ==
                ["livetv-tuner1", "livetv-tuner2", "livetv-unassigned"], timeout=20))
        finally:
            admin.call("DELETE", f"/Users/{dave['Id']}")
    pol = n.users_by_name(admin)["A"]["Policy"]
    pol["BlockedTags"] = []
    n.ok(admin.post(f"/Users/{n.users_by_name(admin)['A']['Id']}/Policy", pol), "clear A's tags")
    check("admin policy edit re-synced (A's blocked tags restored)", wait_for(lambda: sorted(
        n.users_by_name(admin)["A"]["Policy"]["BlockedTags"]) == ["livetv-tuner2", "livetv-unassigned"], timeout=20))

    print("== 12. native Live TV permission")
    def set_policy(name, **changes):
        u = n.users_by_name(admin)[name]
        old = {k: u["Policy"][k] for k in changes}
        n.ok(admin.post(f"/Users/{u['Id']}/Policy", dict(u["Policy"], **changes)), "policy")
        return old

    old_c = set_policy("C", EnableLiveTvAccess=True)
    try:
        C2, c2u, _ = n.login("C", "gate3-C-livetv-on")
        r = C2.get("/LiveTv/Channels", userId=c2u)
        check("unmapped user with Live TV ON: channel list = 403 + generic message (no tuner)",
              r[0] == 403 and text(r) == DENIED, f"HTTP {r[0]}")
        r = C2.post(f"/Items/{news}/PlaybackInfo", {"UserId": c2u, "AutoOpenLiveStream": True})
        check("unmapped user with Live TV ON: PlaybackInfo = 403 + generic message", r[0] == 403 and text(r) == DENIED, text(r))
    finally:
        set_policy("C", **old_c)
        time.sleep(2)

    old_a = set_policy("A", EnableLiveTvAccess=False)
    try:
        A2, a2u, _ = n.login("A", "gate3-A-livetv-off")
        r = A2.post(f"/Items/{news}/PlaybackInfo", {"UserId": a2u, "AutoOpenLiveStream": True})
        check("mapped user with native Live TV OFF: own channel PlaybackInfo = 403", r[0] == 403 and text(r) == DENIED, text(r))
        r = A2.post("/LiveStreams/Open", {"OpenToken": t1_token, "UserId": a2u, "ItemId": news})
        check("mapped user with native Live TV OFF: LiveStreams/Open own channel = 403", r[0] == 403, f"HTTP {r[0]}")
    finally:
        set_policy("A", **old_a)
        time.sleep(2)
    check("A plays again once native Live TV is back on", plays(A, a_uid, news)[0])

    # Section 13 needs a current programme on Test News: make sure the guide window still covers now.
    if not fresh_guide(admin, aid, "before section 13"):
        raise SystemExit("guide has no current programmes; cannot run section 13")

    print("== 13. default-deny (fix 5)")
    # Give A Jellyfin's own Live TV management permission, so the timer checks test the plugin, not Jellyfin.
    old_mgmt = set_policy("A", EnableLiveTvManagement=True)
    A3, a3u, a3_session = n.login("A", "gate3-A-fix5")
    own_prog = n.ok(admin.get("/LiveTv/Programs", userId=aid, channelIds=news, limit=1), "prog")["Items"][0]["Id"]
    other_prog = n.ok(admin.get("/LiveTv/Programs", userId=aid, channelIds=movies, limit=1), "prog")["Items"][0]["Id"]
    for label, (method, path, body, query, expect) in {
        "LiveTv/Info (allowlisted)": ("GET", "/LiveTv/Info", None, {}, 200),
        "LiveTv/GuideInfo (allowlisted)": ("GET", "/LiveTv/GuideInfo", None, {}, 200),
        "LiveTv/Channels/{own} (allowlisted)": ("GET", f"/LiveTv/Channels/{news}", None, {"userId": a3u}, 200),
        "LiveTv/Channels/{other tuner}": ("GET", f"/LiveTv/Channels/{movies}", None, {"userId": a3u}, 403),
        "LiveTv/Programs/{own} (allowlisted)": ("GET", f"/LiveTv/Programs/{own_prog}", None, {"userId": a3u}, 200),
        "LiveTv/Programs/{other tuner}": ("GET", f"/LiveTv/Programs/{other_prog}", None, {"userId": a3u}, 403),
        "LiveTv/Programs/Recommended (allowlisted)": ("GET", "/LiveTv/Programs/Recommended", None, {"userId": a3u, "isAiring": "true"}, 200),
        "LiveTv/Timers list": ("GET", "/LiveTv/Timers", None, {}, 403),
        "LiveTv/Timers create on OWN channel": ("POST", "/LiveTv/Timers", {"ChannelId": news, "Name": "g3", "StartDate": "2030-01-01T00:00:00Z", "EndDate": "2030-01-01T00:30:00Z"}, {}, 403),
        "LiveTv/SeriesTimers list": ("GET", "/LiveTv/SeriesTimers", None, {}, 403),
        "LiveTv/Recordings list": ("GET", "/LiveTv/Recordings", None, {"userId": a3u}, 403),
        "LiveTv/TunerHosts/Types": ("GET", "/LiveTv/TunerHosts/Types", None, {}, 403),
        "Items?ids={other tuner}": ("GET", "/Items", None, {"userId": a3u, "ids": movies}, 403),
        "Items?ids={own}": ("GET", "/Items", None, {"userId": a3u, "ids": news}, 200),
        "Sessions/{own}/Playing?itemIds={other tuner}": ("POST", f"/Sessions/{a3_session}/Playing", None, {"playCommand": "PlayNow", "itemIds": movies}, 403),
        "Sessions/Playing body ItemId={other tuner}": ("POST", "/Sessions/Playing", {"ItemId": movies, "PlaySessionId": "x"}, {}, 403),
        "Items/{unknown id}/PlaybackInfo": ("POST", f"/Items/{uuid.uuid4().hex}/PlaybackInfo", {"UserId": a3u}, {}, 403),
        "Videos/{unknown id}/stream.ts": ("GET", f"/Videos/{uuid.uuid4().hex}/stream.ts", None, {"static": "true"}, 403),
        "Items/{placeholder row id}/PlaybackInfo": ("POST", f"/Items/{'0' * 31 + '1'}/PlaybackInfo", {"UserId": a3u}, {}, 403),
        "LiveStreams/Open token of unknown provider": ("POST", "/LiveStreams/Open", {"OpenToken": "0123456789abcdef0123456789abcdef_LiveTvChannel_" + news, "UserId": a3u}, {}, 403),
        "LiveStreams/Open with no token": ("POST", "/LiveStreams/Open", {"UserId": a3u}, {}, 403),
    }.items():
        r = A3.call(method, path, body=body, query=query or None)
        good = r[0] == expect and (expect != 403 or text(r) == DENIED)
        check(f"A {method} {label} = {expect}", good, f"HTTP {r[0]}" + (f": {text(r)[:60]}" if r[0] >= 400 else ""))
    set_policy("A", **old_mgmt)
    timers = n.ok(admin.get("/LiveTv/Timers"), "timers")["Items"]
    check("no timer was created by A", not any(x.get("Name") == "g3" for x in timers))

    print("== 14. library playback and browsing still work (A, and C with no tuner)")
    lib = library_fixture.main()
    lib_items = lib
    for who in ("A", "C"):
        api, uid, _ = n.login(who, f"gate3-lib-{who}")
        st, res = api.get(f"/Users/{uid}/Items", parentId=lib["MoviesLibrary"], recursive="true")
        check(f"{who} browses Test Movies", st == 200 and any(i["Id"] == lib["Movie"] for i in res["Items"]), f"HTTP {st}")
        st, res = api.get("/Items", userId=uid, recursive="true", searchTerm="Test Movie")
        check(f"{who} search finds Test Movie", st == 200 and any(i["Id"] == lib["Movie"] for i in res["Items"]), f"HTTP {st}")
        st, _ = api.get("/UserViews", userId=uid)
        check(f"{who} user views", st == 200, f"HTTP {st}")
        st, info = api.post(f"/Items/{lib['Movie']}/PlaybackInfo", {"UserId": uid})
        check(f"{who} movie PlaybackInfo", st == 200, f"HTTP {st}")
        if st == 200:
            ms = info["MediaSources"][0]["Id"]
            q = urllib.parse.urlencode({"mediaSourceId": ms, "playSessionId": info["PlaySessionId"], "videoCodec": "h264", "audioCodec": "aac", "segmentContainer": "ts"})
            st, master = raw_get(api, f"/Videos/{lib['Movie']}/master.m3u8?{q}")
            sub = next((l for l in master.decode().splitlines() if l and not l.startswith("#")), None) if st == 200 else None
            st2, media = raw_get(api, f"/Videos/{lib['Movie']}/{sub}", timeout=90) if sub else (st, b"")
            segs = [l for l in media.decode().splitlines() if l and not l.startswith("#")] if st2 == 200 else []
            st3, seg = raw_get(api, f"/Videos/{lib['Movie']}/{segs[0]}", timeout=90) if segs else (st2, b"")
            check(f"{who} movie plays (HLS segment)", st3 == 200 and seg[:1] == b"G", f"HTTP {st}/{st2}/{st3}, {len(seg)} bytes")
            api.call("DELETE", "/Videos/ActiveEncodings", query={"deviceId": f"gate3-lib-{who}", "playSessionId": info["PlaySessionId"]})
        st, _ = raw_get(api, f"/Items/{lib['Movie']}/Images/Primary")
        check(f"{who} movie image not blocked by the plugin", st != 403, f"HTTP {st}")
        st, body = raw_get(api, f"/Audio/{lib['Audio']}/stream.mp3?static=true")
        check(f"{who} song streams", st == 200 and len(body) > 0, f"HTTP {st}, {len(body)} bytes")

    print("== 15. anonymous callers, legacy params, play session reuse (fix 5b)")
    anon_since = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    time.sleep(1)
    ms_news_id = n.ok(admin.post(f"/Items/{news}/PlaybackInfo", {"UserId": aid}, userId=aid), "pi")["MediaSources"][0]["Id"]
    for label, path in {
        "stream?static=true, tuner2 channel": f"/Videos/{movies}/stream?static=true",
        "stream.ts (no static), tuner2 channel": f"/Videos/{movies}/stream.ts",
        "stream?static=true, tuner1 channel": f"/Videos/{news}/stream?static=true",
        "Audio stream, tuner2 channel": f"/Audio/{movies}/stream?static=true",
    }.items():
        st, body = anon_get(path)
        check(f"anonymous {label} = 403 + generic message", st == 403 and body.decode(errors="replace") == DENIED, f"HTTP {st}")
    # DynamicHlsController has a class-level [Authorize] (DynamicHlsController.cs:40): Jellyfin rejects first (401).
    st, _ = anon_get(f"/Videos/{news}/master.m3u8?mediaSourceId={ms_news_id}")
    check("anonymous HLS master, tuner1 channel = denied (401 by Jellyfin auth, or 403)", st in (401, 403), f"HTTP {st}")
    time.sleep(2)
    opened = [l for l in subprocess.run(["docker", "logs", "--since", anon_since, "jellyfin-dev"], capture_output=True, text=True).stdout.splitlines()
              if "Opening channel stream" in l]
    check("anonymous attempts opened no tuner and made no upstream request",
          not opened and not n.fakestreams_requests(anon_since))

    lib = library_fixture.main()
    for label, path, ok_status in (
        ("public system info", "/System/Info/Public", 200),
        ("branding", "/Branding/Configuration", 200),
        ("library movie stream?static=true", f"/Videos/{lib['Movie']}/stream?static=true", 200),
    ):
        st, _ = anon_get(path)
        check(f"anonymous {label} still works", st == ok_status, f"HTTP {st}")
    for label, item in (("tuner1 channel logo", news), ("tuner2 channel logo", movies), ("movie image", lib["Movie"])):
        st, _ = anon_get(f"/Items/{item}/Images/Primary")
        check(f"anonymous {label} not blocked (accepted: metadata)", st != 403, f"HTTP {st}")

    # Legacy params field 22: A asks for its own movie with B's open live stream id hidden in "params".
    A5, a5u, _ = n.login("A", "gate3-A-fix5b")
    B5, b5u, _ = n.login("B", "gate3-B-fix5b")
    st_a, ms_a, ps_a = n.open_channel(A5, a5u, news)
    st_b, ms_b, ps_b = n.open_channel(B5, b5u, movies)
    try:
        def legacy(lid):
            fields = [""] * 29
            fields[22] = lid
            return f"/Videos/{lib['Movie']}/stream?static=true&params={urllib.parse.quote(';'.join(fields))}"
        st, _ = raw_get(A5, legacy(ms_b["LiveStreamId"]), timeout=15)
        check("A: own movie + params field 22 = B's stream id -> 403", st == 403, f"HTTP {st}")
        st, body = raw_get(A5, legacy(ms_a["LiveStreamId"]), timeout=15)
        check("A: own movie + params field 22 = A's own stream id -> allowed", st == 200 and len(body) > 0, f"HTTP {st}, {len(body)} bytes")

        # Play session reuse: B starts an HLS transcode of its channel; A tries to attach to that job.
        q = urllib.parse.urlencode({"mediaSourceId": ms_b["Id"], "liveStreamId": ms_b["LiveStreamId"], "playSessionId": ps_b,
                                    "videoCodec": "h264", "audioCodec": "aac", "segmentContainer": "ts"})
        st, master = raw_get(B5, f"/Videos/{movies}/master.m3u8?{q}")
        sub = next((l for l in master.decode().splitlines() if l and not l.startswith("#")), None) if st == 200 else None
        if sub:
            raw_get(B5, f"/Videos/{movies}/{sub}", timeout=90)
        st, _ = raw_get(A5, f"/Videos/{lib['Movie']}/stream.ts?playSessionId={ps_b}&videoCodec=copy&audioCodec=copy", timeout=15)
        check("A: own movie + B's live play session id -> 403", st == 403, f"HTTP {st}")
    finally:
        if ps_a:
            stop(A5, ms_a, ps_a, news)
        if ps_b:
            stop(B5, ms_b, ps_b, movies)
        time.sleep(2)

    # The .ts buffer URL (unauthenticated, read by Jellyfin's own ffmpeg). Since fix 5d non-admins don't see media
    # source paths, so the buffer URL is read through the admin API key. Since fix 6 only the server itself (no
    # forwarded headers) and admins may read it. There is no Caddy locally, so proxy requests are simulated from
    # inside the container (the server's own address) with forwarded headers added.
    st, ms_l, ps_l = n.open_channel(admin, aid, live)
    try:
        buffer_path = (ms_l.get("Path") or "") if ps_l else ""
        check("the .ts channel is served through the LiveStreamFiles buffer (seen via admin)", "/LiveTv/LiveStreamFiles/" in buffer_path, buffer_path[-60:])
        local = buffer_path[buffer_path.index("/LiveTv/"):] if "/LiveTv/" in buffer_path else "/LiveTv/LiveStreamFiles/x/stream.ts"
        st, body = anon_get(local, limit=4096)
        check("buffer URL, anonymous from another host = 403 + generic message (fix 6)", st == 403 and body.decode(errors="replace") == DENIED, f"HTTP {st}")
        st, _ = raw_get_limited(A5, local)
        check("buffer URL, logged-in A from another host = 403 (fix 6)", st == 403, f"HTTP {st}")
        st, body = raw_get_limited(admin, local)
        check("buffer URL, admin API key = allowed", st == 200 and len(body) > 0, f"HTTP {st}")

        def in_container(url, *headers):
            cmd = ["docker", "exec", "jellyfin-dev", "curl", "-s", "-o", "/dev/null", "-m", "3", "-w", "%{http_code} %{size_download}", url]
            for h in headers:
                cmd += ["-H", h]
            out = subprocess.run(cmd, capture_output=True, text=True).stdout.split()
            return (int(out[0]), int(out[1])) if len(out) == 2 else (None, 0)

        code, size = in_container(buffer_path)
        check("buffer URL from the server's own interface address (as ffmpeg) = allowed", code == 200 and size > 0, f"{code}, {size} bytes")
        code, size = in_container("http://127.0.0.1:8096" + local)
        check("buffer URL from loopback (as ffmpeg) = allowed", code == 200 and size > 0, f"{code}, {size} bytes")
        for header in ("X-Forwarded-For: 203.0.113.7", "X-Real-IP: 203.0.113.7", "Forwarded: for=203.0.113.7",
                       "X-Forwarded-Proto: https", "X-Forwarded-Host: tv.example.com", "X-Original-For: 172.18.0.2:41234"):
            code, _ = in_container("http://127.0.0.1:8096" + local, header)
            check(f"buffer URL from the server but via a proxy ({header.split(':')[0]}) = 403", code == 403, str(code))
    finally:
        if ps_l:
            stop(admin, ms_l, ps_l, live)
        time.sleep(2)
    ok, detail = plays(A5, a5u, live)
    check(".ts playback via the buffer still works (HLS segment)", ok, detail)

    print("== 16. legacy HLS file routes bound to a running job (fix 5c)")
    A6, a6u, _ = n.login("A", "gate3-A-fix5c")
    B6, b6u, _ = n.login("B", "gate3-B-fix5c")

    # A starts Live TV HLS on its own channel and keeps it running.
    st, ms6, ps6 = n.open_channel(A6, a6u, news)
    seg_url = None
    try:
        q = urllib.parse.urlencode({"mediaSourceId": ms6["Id"], "liveStreamId": ms6["LiveStreamId"], "playSessionId": ps6,
                                    "videoCodec": "h264", "audioCodec": "aac", "segmentContainer": "ts"})
        st, master = raw_get(A6, f"/Videos/{news}/master.m3u8?{q}")
        sub = next((l for l in master.decode().splitlines() if l and not l.startswith("#")), None) if st == 200 else None
        st, media = raw_get(A6, f"/Videos/{news}/{sub}", timeout=90) if sub else (st, b"")
        segs = [l for l in media.decode().splitlines() if l and not l.startswith("#")] if st == 200 else []
        seg_url = segs[0] if segs else None
        check("Live TV HLS segment URLs use the legacy route (hls/...)", bool(seg_url) and seg_url.startswith("hls/"), seg_url or f"HTTP {st}")
        if seg_url:
            st, body = raw_get(A6, f"/Videos/{news}/{seg_url}", timeout=90)
            check("A fetches its own live segment via the legacy route", st == 200 and body[:1] == b"G", f"HTTP {st}")
            st, body = anon_get(f"/Videos/{news}/{seg_url}", timeout=60)
            check("anonymous fetch of A's running live job segment = allowed (accepted A1)", st == 200 and body[:1] == b"G", f"HTTP {st}")
            st, _ = raw_get(B6, f"/Videos/{news}/{seg_url}", timeout=30)
            check("B (tuner2) fetching A's live job segment = 403", st == 403, f"HTTP {st}")
            playlist, segment = seg_url.split("/")[1], seg_url.split("/")[2].split(".")[0]
            st, _ = anon_get(f"/Videos/00000000000000000000000000000000/hls/{playlist}/{segment}.ts")
            check("legacy segment with empty item id = 403", st == 403, f"HTTP {st}")
            st, _ = anon_get(f"/Videos/{news}/hls/{playlist}/{playlist}.m3u8")
            check("legacy route asking for the job's playlist file (.m3u8) = refused", st in (403, 404), f"HTTP {st}")
            for label, path in (("../ traversal (encoded)", f"/Videos/{news}/hls/..%2F..%2Fdata/x0.ts"),
                                ("segment with ../", f"/Videos/{news}/hls/{playlist}/..%2F{segment}.ts"),
                                ("other job's name", f"/Videos/{news}/hls/{playlist}/{'f' * 32}0.ts")):
                st, _ = anon_get(path)
                check(f"legacy segment, {label} = refused", st in (400, 403, 404) and st != 200, f"HTTP {st}")
    finally:
        if ps6:
            stop(A6, ms6, ps6, news)
        time.sleep(3)
    if seg_url:
        st, _ = anon_get(f"/Videos/{news}/{seg_url}")
        check("legacy segment of a finished job = 403", st == 403, f"HTTP {st}")

    # The confirmed leak: the raw .ts buffer file name, read through the legacy route. (The buffer id is read via the
    # admin API key: since fix 5d A's own response hides it.)
    st, ms7, ps7 = n.open_channel(admin, aid, live)
    try:
        st_a, ms_a, ps_a = n.open_channel(A6, a6u, live)
        check("A's own open response hides the buffer path", bool(ps_a) and not ms_a.get("Path"), f"HTTP {st_a}")
        if ps_a:
            A6.post("/LiveStreams/Close", liveStreamId=ms_a["LiveStreamId"])
        unique = ms7["Path"].split("/LiveStreamFiles/")[1].split("/")[0] if ps7 else "x"
        time.sleep(2)
        st, _ = anon_get(f"/Videos/00000000000000000000000000000000/hls/{unique}/{unique}.ts")
        check("raw .ts buffer file via legacy route (anonymous, empty item id) = 403", st == 403, f"HTTP {st}")
        st, _ = anon_get(f"/Videos/{lib_items['Movie']}/hls/{unique}/{unique}.ts")
        check("raw .ts buffer file via legacy route (anonymous, real item id) = 403", st == 403, f"HTTP {st}")
        st, _ = anon_get(f"/Audio/{lib_items['Audio']}/hls/{unique}/stream.mp3")
        check("legacy audio route for a non-job file = 403", st == 403, f"HTTP {st}")
    finally:
        if ps7:
            stop(admin, ms7, ps7, live)
        time.sleep(2)

    # hls1 + a real ffmpeg transcode (forced by a low bitrate and size) still work for a library movie.
    hls_since = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    st, info = A6.post(f"/Items/{lib_items['Movie']}/PlaybackInfo", {"UserId": a6u})
    if st == 200:
        q = urllib.parse.urlencode({"mediaSourceId": info["MediaSources"][0]["Id"], "playSessionId": info["PlaySessionId"],
                                    "videoCodec": "h264", "audioCodec": "aac", "segmentContainer": "ts",
                                    "videoBitRate": "150000", "maxWidth": "320", "audioBitRate": "64000"})
        st, master = raw_get(A6, f"/Videos/{lib_items['Movie']}/master.m3u8?{q}")
        sub = next((l for l in master.decode().splitlines() if l and not l.startswith("#")), None) if st == 200 else None
        st, media = raw_get(A6, f"/Videos/{lib_items['Movie']}/{sub}", timeout=90) if sub else (st, b"")
        segs = [l for l in media.decode().splitlines() if l and not l.startswith("#")] if st == 200 else []
        st, seg = raw_get(A6, f"/Videos/{lib_items['Movie']}/{segs[0]}", timeout=90) if segs else (st, b"")
        check("library HLS uses hls1 and plays (A)", bool(segs) and segs[0].startswith("hls1/") and st == 200 and seg[:1] == b"G",
              f"{segs[0][:30] if segs else '-'} HTTP {st}")
        A6.call("DELETE", "/Videos/ActiveEncodings", query={"deviceId": "gate3-A-fix5c", "playSessionId": info["PlaySessionId"]})
        time.sleep(1)
        log = subprocess.run(["docker", "logs", "--since", hls_since, "jellyfin-dev"], capture_output=True, text=True)
        ffmpeg = [l for l in (log.stdout + log.stderr).splitlines() if "/ffmpeg" in l and "libx264" in l]
        check("Jellyfin's own ffmpeg transcoded the movie (libx264)", bool(ffmpeg))

    print("== 17. upstream URLs hidden, open-token source, cross-tuner sharing (fix 5d)")
    MARKERS = ("fakestreams", "/tuners/", "LiveStreamFiles")
    A7, a7u, _ = n.login("A", "gate3-A-fix5d")
    B7, b7u, _ = n.login("B", "gate3-B-fix5d")

    def leaks(body):
        text = body if isinstance(body, str) else json.dumps(body)
        return [m for m in MARKERS if m in text]

    WEB_PROFILE = {
        "MaxStreamingBitrate": 120000000,
        "DirectPlayProfiles": [{"Container": "webm", "Type": "Video"}],
        "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "AudioCodec": "aac", "VideoCodec": "h264",
                                 "Context": "Streaming", "Protocol": "hls", "MaxAudioChannels": "2", "MinSegments": 1,
                                 "BreakOnNonKeyFrames": True}],
    }
    # Web-client path: PlaybackInfo with a device profile + auto-open, then its TranscodingUrl.
    st, info = A7.post(f"/Items/{news}/PlaybackInfo", {"UserId": a7u, "AutoOpenLiveStream": True, "DeviceProfile": WEB_PROFILE}, userId=a7u)
    ms7 = info["MediaSources"][0] if st == 200 else {}
    try:
        check("A PlaybackInfo (web profile, auto-open): no upstream URL in the response", st == 200 and not leaks(info), f"HTTP {st} {leaks(info) if st == 200 else ''}")
        check("A PlaybackInfo: Path and RequiredHttpHeaders empty", st == 200 and not ms7.get("Path") and not ms7.get("RequiredHttpHeaders"), str(ms7.get("Path")))
        turl = ms7.get("TranscodingUrl")
        st2, master = raw_get(A7, turl) if turl else (None, b"")
        sub = next((l for l in master.decode().splitlines() if l and not l.startswith("#")), None) if st2 == 200 else None
        base = turl.split("?")[0].rsplit("/", 1)[0] if turl else ""
        st3, media = raw_get(A7, f"{base}/{sub}", timeout=90) if sub else (st2, b"")
        segs = [l for l in media.decode().splitlines() if l and not l.startswith("#")] if st3 == 200 else []
        st4, seg = raw_get(A7, f"{base}/{segs[0]}", timeout=90) if segs else (st3, b"")
        check("web-client path plays (TranscodingUrl -> HLS segment)", st4 == 200 and seg[:1] == b"G", f"HTTP {st2}/{st3}/{st4}")
        st, sess = A7.get("/Sessions")
        check("A GET /Sessions while playing: no upstream URL", st == 200 and not leaks(sess), f"HTTP {st} {leaks(sess) if st == 200 else ''}")
    finally:
        if ms7.get("LiveStreamId"):
            stop(A7, ms7, info.get("PlaySessionId"), news)
        time.sleep(2)

    survey = {
        "LiveStreams/Open (own)": lambda: A7.post("/LiveStreams/Open", {"OpenToken": t1_token, "UserId": a7u, "ItemId": news}),
        "GET Items/{own}/PlaybackInfo": lambda: A7.get(f"/Items/{news}/PlaybackInfo", userId=a7u),
        "LiveTv/Channels (all fields)": lambda: A7.get("/LiveTv/Channels", userId=a7u, fields="Path,MediaSources,MediaStreams,ChannelInfo", addCurrentProgram="true"),
        "LiveTv/Channels/{own}": lambda: A7.get(f"/LiveTv/Channels/{news}", userId=a7u),
        "Items?ids={own}&fields=Path,MediaSources": lambda: A7.get("/Items", userId=a7u, ids=news, fields="Path,MediaSources,MediaStreams"),
        "Users/{id}/Items/{own}": lambda: A7.get(f"/Users/{a7u}/Items/{news}"),
        "LiveTv/Programs (fields)": lambda: A7.get("/LiveTv/Programs", userId=a7u, fields="Path,MediaSources,ChannelInfo", limit=50),
        "LiveTv/Programs/Recommended": lambda: A7.get("/LiveTv/Programs/Recommended", userId=a7u, isAiring="true", fields="Path,MediaSources"),
        "LiveTv/Info": lambda: A7.get("/LiveTv/Info"),
        "LiveTv/GuideInfo": lambda: A7.get("/LiveTv/GuideInfo"),
        "Search/Hints": lambda: A7.get("/Search/Hints", userId=a7u, searchTerm="Test"),
        "System/Configuration/livetv": lambda: A7.get("/System/Configuration/livetv"),
        "System/Configuration": lambda: A7.get("/System/Configuration"),
        "System/Info": lambda: A7.get("/System/Info"),
        "System/Info/Public": lambda: A7.get("/System/Info/Public"),
    }
    for label, call in survey.items():
        st, body = call()
        found = leaks(body) if st == 200 else []
        check(f"A {label}: no upstream URL", st != 200 or not found, f"HTTP {st} {found}")
        if label == "LiveStreams/Open (own)" and st == 200:
            A7.post("/LiveStreams/Close", liveStreamId=body["MediaSource"]["LiveStreamId"])
    st, cfg = admin.get("/System/Configuration/livetv")
    check("admin still sees the real tuner URLs", st == 200 and any("/tuners/" in (h.get("Url") or "") for h in cfg["TunerHosts"]))

    # Crafted open token: A's own channel + B's media source id.
    st_b, ms_b, ps_b = n.open_channel(B7, b7u, movies)
    try:
        crafted = f"{t1_token.split('_')[0]}_LiveTvChannel_{news}_{ms_b['Id']}"
        st, res = A7.post("/LiveStreams/Open", {"OpenToken": crafted, "UserId": a7u, "ItemId": news})
        check("A crafted token with B's media source id = 403 (B's stream not handed out)",
              st == 403 and text((st, res)) == DENIED and ms_b["LiveStreamId"] not in json.dumps(res), f"HTTP {st}")
    finally:
        if ps_b:
            stop(B7, ms_b, ps_b, movies)
        time.sleep(2)

    # Same stream URL on both tuners: B's tuner-2 twin is open -> A opening its own channel is refused.
    original = TUNER2_M3U.read_text()
    news_url = next(l for l in (n.tuner_file("tuner1")).read_text().splitlines() if "ch=news1" in l)
    try:
        TUNER2_M3U.write_text(original.replace(next(l for l in original.splitlines() if "ch=kids1" in l), news_url))
        n.run_task(admin, "RefreshGuide")
        twin = channel(n.expected_channels(), "Test Kids", "tuner2")
        wait_for(lambda: tags_match(admin, aid, n.expected_channels()))
        # Fix 7: the self-check reports the duplicate as a warning only, redacted to scheme://host and a hash.
        st, s7 = admin.get("/LiveTvAccess/Status", refresh="true")
        dup = (s7.get("Duplicates") or [None])[0] if st == 200 else None
        parts = urllib.parse.urlsplit(news_url.strip())
        check("self-check: same URL on two tuners = warning, state stays Healthy",
              st == 200 and s7["State"] == "Healthy" and len(s7["Duplicates"]) == 1, f"HTTP {st} {s7.get('State') if st == 200 else ''}")
        check("self-check: duplicate shown as host + hash only",
              dup is not None and dup["Host"] == f"{parts.scheme}://{parts.netloc}" and dup["Hash"].startswith("sha256:")
              and dup["Tuners"] == ["tuner1", "tuner2"], str(dup))
        check("self-check: no stream path, query or tuner file in the status",
              st == 200 and parts.path not in json.dumps(s7) and "ch=" not in json.dumps(s7) and "/tuners/" not in json.dumps(s7))
        st_b, ms_b, ps_b = n.open_channel(B7, b7u, twin)
        try:
            st, _ = A7.post(f"/Items/{news}/PlaybackInfo", {"UserId": a7u, "AutoOpenLiveStream": True}, userId=a7u)
            check("same URL on both tuners, B's twin open: A opening its own channel = 403", st == 403, f"HTTP {st}")
        finally:
            if ps_b:
                stop(B7, ms_b, ps_b, twin)
            time.sleep(3)
        ok7, detail7 = plays(A7, a7u, news)
        check("same URL on both tuners, nothing open: A plays its own channel", ok7, detail7)
    finally:
        TUNER2_M3U.write_text(original)
        n.run_task(admin, "RefreshGuide")
        wait_for(lambda: tags_match(admin, aid, n.expected_channels()))

    print("== 18. no untracked static streams of Live TV channels (fix 5e)")
    A8, a8u, _ = n.login("A", "gate3-A-fix5e")
    B8, b8u, _ = n.login("B", "gate3-B-fix5e")
    sports = channel(expected, "Test Sports", "tuner1")
    lib8 = library_fixture.main()

    def first_bytes(api, path, timeout=20):
        """Status and the first 64 KiB of a (possibly endless) stream; api None = anonymous."""
        headers = {"Authorization": api.auth} if api else {}
        try:
            with urllib.request.urlopen(urllib.request.Request(n.BASE + path, headers=headers), timeout=timeout) as r:
                return r.status, r.read(65536)
        except urllib.error.HTTPError as e:
            return e.code, e.read()
        except Exception as e:  # noqa: BLE001 - report timeouts as a status
            return type(e).__name__, b""

    def upstream_hits(since, marker):
        logs = subprocess.run(["docker", "logs", "--since", since, "fakestreams"], capture_output=True, text=True)
        return (logs.stdout + logs.stderr).count(marker)

    params3 = ";".join("true" if i == 3 else "" for i in range(29))
    st, ms8, ps8 = n.open_channel(A8, a8u, news)
    try:
        check("A opens Test News (A's tuner now busy)", bool(ps8), f"HTTP {st}")
        since8 = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        time.sleep(1)
        for label, path in (
            ("Videos stream?static=true", f"/Videos/{sports}/stream?static=true"),
            ("Videos stream.ts?static=true", f"/Videos/{sports}/stream.ts?static=true"),
            ("Videos stream, legacy params field 3 = true", f"/Videos/{sports}/stream?params={urllib.parse.quote(params3)}"),
            ("Audio stream?static=true", f"/Audio/{sports}/stream?static=true"),
            ("UniversalAudio", f"/Audio/{sports}/universal?userId={a8u}"),
        ):
            st, body = first_bytes(A8, path)
            check(f"A, tuner busy, {label} of Test Sports = 403 + generic message", st == 403 and body.decode(errors="replace") == DENIED, f"HTTP {st}")
        st, _ = first_bytes(A8, f"/Videos/{news}/stream?static=true")
        check("A static stream of the open channel itself, without its live stream id = 403", st == 403, f"HTTP {st}")
        time.sleep(1)
        check("denied static requests made no upstream request", upstream_hits(since8, "ch=sports1") == 0)
        q = urllib.parse.urlencode({"static": "true", "mediaSourceId": ms8["Id"], "liveStreamId": ms8["LiveStreamId"]})
        st, body = first_bytes(A8, f"/Videos/{news}/stream?{q}")
        check("A static stream WITH A's own open live stream id = allowed (tracked)", st == 200 and len(body) > 0, f"HTTP {st}, {len(body)} bytes")
    finally:
        if ps8:
            stop(A8, ms8, ps8, news)
        time.sleep(2)

    for who, api, uid, cid in (("A", A8, a8u, news), ("B", B8, b8u, movies)):
        ok8, detail8 = plays(api, uid, cid)
        check(f"{who} tracked path still plays (open -> HLS)", ok8, detail8)
        time.sleep(2)
    C8, c8u, _ = n.login("C", "gate3-C-fix5e")
    for who, api in (("A", A8), ("C", C8), ("anonymous", None)):
        st, body = first_bytes(api, f"/Videos/{lib8['Movie']}/stream?static=true")
        check(f"{who} library movie stream?static=true still works", st == 200 and len(body) > 0, f"HTTP {st}")
        st, body = first_bytes(api, f"/Audio/{lib8['Audio']}/stream.mp3?static=true")
        check(f"{who} library song stream?static=true still works", st == 200 and len(body) > 0, f"HTTP {st}")
        st, body = first_bytes(api, f"/Audio/{lib8['Audio']}/stream?params={urllib.parse.quote(params3)}")
        check(f"{who} library song via legacy params static still works", st == 200 and len(body) > 0, f"HTTP {st}")
    st, body = first_bytes(A8, f"/Audio/{lib8['Audio']}/universal?userId={a8u}&container=mp3")
    check("A UniversalAudio for a library song still works", st == 200 and len(body) > 0, f"HTTP {st}")

    print("== 19. self-check, failed state, status API (fix 7)")
    since9 = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    A9, a9u, _ = n.login("A", "gate3-A-fix7")
    B9, b9u, _ = n.login("B", "gate3-B-fix7")

    def status(refresh=False):
        st, s = admin.get("/LiveTvAccess/Status", refresh="true" if refresh else "false")
        return s if st == 200 else {"State": f"HTTP {st}"}

    s = status(refresh=True)
    check("admin GET /LiveTvAccess/Status = Healthy", s["State"] == "Healthy", s["State"])
    check("status: every check passed (filter, endpoint shapes, claims, tuners, duplicates)",
          all(c["Ok"] for c in s.get("Checks", [])) and len(s.get("Checks", [])) == 5,
          "; ".join(f"{c['Name']}={c['Ok']}" for c in s.get("Checks", [])))
    want_counts = {}
    for tuner, _ in n.expected_channels().values():
        want_counts[tuner] = want_counts.get(tuner, 0) + 1
    check("status: per-tuner channel counts match the playlists",
          {x["Key"]: x["ChannelCount"] for x in s.get("Tuners", [])} == want_counts, str(want_counts))
    check("status: no tuner URL, stream URL or credential in the response",
          not any(m in json.dumps(s) for m in ("/tuners/", "fakestreams", "ch=", ".m3u")))
    check("non-admin A GET /LiveTvAccess/Status = 403", A9.get("/LiveTvAccess/Status")[0] == 403)
    check("anonymous GET /LiveTvAccess/Status = 401", anon_get("/LiveTvAccess/Status")[0] == 401)

    # Drift: plugin tuner2 no longer matches a Jellyfin tuner -> failed state, then fix it -> recovers by itself.
    bad = {**CONFIG, "Tuners": [dict(x, Url="/tuners/missing.m3u") if x["Key"] == "tuner2" else x for x in CONFIG["Tuners"]]}
    try:
        check("bad config saved through the normal plugin config API", admin.post(f"/Plugins/{PLUGIN_ID}/Configuration", bad)[0] == 204)
        check("self-check enters the failed state on its own after the save", wait_for(lambda: status()["State"] == "Failed", 20))
        s = status()
        check("failed state names the tuner, without its URL",
              any("tuner2" in p for p in s.get("Problems", [])) and "missing.m3u" not in json.dumps(s), str(s.get("Problems")))
        st, ms9, ps9 = n.open_channel(A9, a9u, news)
        check("failed state: A opening A's own channel = 403", st == 403, f"HTTP {st}")
        if ps9:
            stop(A9, ms9, ps9, news)
        check("failed state: A lists Live TV channels = 403", A9.get("/LiveTv/Channels", userId=a9u)[0] == 403)
        st, _ = A9.post(f"/Items/{lib8['Movie']}/PlaybackInfo", {"UserId": a9u}, userId=a9u)
        check("failed state: A's library movie still works", st == 200, f"HTTP {st}")
        st, body = first_bytes(A9, f"/Audio/{lib8['Audio']}/stream.mp3?static=true")
        check("failed state: A's library song still streams", st == 200 and len(body) > 0, f"HTTP {st}")
        st, msa, psa = n.open_channel(admin, aid, news)
        check("failed state: admin still opens Live TV", st == 200 and bool(psa), f"HTTP {st}")
        if psa:
            stop(admin, msa, psa, news)
        time.sleep(2)
    finally:
        check("config restored", admin.post(f"/Plugins/{PLUGIN_ID}/Configuration", CONFIG)[0] == 204)
    check("self-check recovers on its own after the fix", wait_for(lambda: status()["State"] == "Healthy", 20))
    check("tags back in sync after the fix", wait_for(lambda: tags_match(admin, aid, n.expected_channels())))
    ok9, detail9 = plays(A9, a9u, news)
    check("after recovery A plays A's own channel again", ok9, detail9)
    log9 = subprocess.run(["docker", "logs", "--since", since9, "jellyfin-dev"], capture_output=True, text=True)
    log9 = log9.stdout + log9.stderr
    check("server log: SELF-CHECK FAILED error, then 'restored'",
          "SELF-CHECK FAILED" in log9 and "Live TV access restored" in log9)
    check("server log: self-check lines carry no tuner path", not any("missing.m3u" in l for l in log9.splitlines() if "SelfCheck" in l))

    print("== 20. settings page (step 3)")
    st, pages = admin.get("/web/ConfigurationPages")
    mine = [x for x in pages if x.get("Name") == "LiveTvAccess"] if st == 200 else []
    check("dashboard lists the LiveTvAccess settings page", len(mine) == 1 and mine[0].get("PluginId", "").replace("-", "") == PLUGIN_ID, str(mine))
    check("non-admin A cannot list configuration pages", A9.get("/web/ConfigurationPages")[0] == 403)
    st, html = raw_get(admin, "/web/ConfigurationPage?name=LiveTvAccess")
    source = (n.ROOT / "src" / "Jellyfin.Plugin.LiveTvAccess" / "Configuration" / "configPage.html").read_bytes()
    check("server serves the embedded page byte-for-byte", st == 200 and html == source, f"HTTP {st}, {len(html)} bytes")
    script = html.decode().split('<script type="text/javascript">', 1)[-1].split("</script>", 1)[0]
    js = pathlib.Path(n.ROOT / "local" / "page-script-check.js")
    js.write_text(script)
    try:
        node = subprocess.run(["node", "--check", str(js)], capture_output=True, text=True)
        check("page script parses (node --check)", node.returncode == 0, node.stderr[:200])
    finally:
        js.unlink()

    # Save exactly as the page does: read the config, replace Tuners and Users (user picked by id from GET /Users,
    # display name possibly stale), write it back through the plugin config API.
    before = n.ok(admin.get(f"/Plugins/{PLUGIN_ID}/Configuration"), "cfg")
    users_now = n.users_by_name(admin)
    page_cfg = dict(before)
    page_cfg["Tuners"] = [{"Key": k, "Url": url} for k, url in n.TUNERS.items()]
    page_cfg["Users"] = [{"UserId": users_now["A"]["Id"], "UserName": "stale-name", "TunerKey": "tuner1"},
                         {"UserId": users_now["B"]["Id"], "UserName": "B", "TunerKey": "tuner2"}]
    page_cfg["OwnedTagHistory"] = []
    check("page-style save accepted (204)", admin.post(f"/Plugins/{PLUGIN_ID}/Configuration", page_cfg)[0] == 204)
    after = n.ok(admin.get(f"/Plugins/{PLUGIN_ID}/Configuration"), "cfg")
    names = {u["UserId"].replace("-", ""): u["UserName"] for u in after["Users"]}
    check("saved user ids kept, stale display name refreshed by the server",
          names.get(users_now["A"]["Id"].replace("-", "")) == "A" and names.get(users_now["B"]["Id"].replace("-", "")) == "B", str(names))
    check("owned-tag history kept although the page sent none",
          set(before.get("OwnedTagHistory") or []) <= set(after.get("OwnedTagHistory") or []), str(after.get("OwnedTagHistory")))
    check("after the page-style save: tags in sync", wait_for(lambda: tags_match(admin, aid, n.expected_channels())))
    check("after the page-style save: status Healthy", wait_for(lambda: status(refresh=True)["State"] == "Healthy", 20))
    admin.post(f"/Plugins/{PLUGIN_ID}/Configuration", CONFIG)

    print("== 10. logging")
    time.sleep(1)
    log = subprocess.run(["docker", "logs", "--since", since, "jellyfin-dev"], capture_output=True, text=True)
    log = log.stdout + log.stderr
    plugin_lines = [l for l in log.splitlines() if "LiveTvAccess" in l]
    check("plugin never logs a full open token", not any(t in l for t in tokens_used for l in plugin_lines))
    check("denials log a short token hash", any("=sha256:" in l and "denied" in l for l in plugin_lines))

    failed = [r for r in results if not r[1]]
    print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
