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


def main():
    since = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    admin = n.Api(n.load_api_key(), "gate3-admin")
    aid = n.admin_user_id(admin)
    tokens_used = []

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

    # The .ts buffer URL (unauthenticated, read by Jellyfin's own ffmpeg) is unchanged until fix 6.
    st, ms_l, ps_l = n.open_channel(A5, a5u, live)
    try:
        buffer_path = (ms_l.get("Path") or "") if ps_l else ""
        check("A's .ts channel is served through the LiveStreamFiles buffer", "/LiveTv/LiveStreamFiles/" in buffer_path, buffer_path[-60:])
        local = buffer_path[buffer_path.index("/LiveTv/"):] if "/LiveTv/" in buffer_path else "/LiveTv/LiveStreamFiles/x/stream.ts"
        st, body = anon_get(local, limit=4096)
        check("LiveStreamFiles buffer URL still readable without auth (fix 6 will restrict it)", st == 200 and len(body) > 0, f"HTTP {st}")
    finally:
        if ps_l:
            stop(A5, ms_l, ps_l, live)
        time.sleep(2)
    ok, detail = plays(A5, a5u, live)
    check(".ts playback via the buffer still works (HLS segment)", ok, detail)

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
