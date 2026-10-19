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

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import nocode_experiment as n  # noqa: E402

PLUGIN_ID = "4d5aa36c91644f4dba7f0fa6c25bfd28"
CONFIG = {
    "Tuners": [{"Key": "tuner1", "Url": "/tuners/tuner1.m3u"}, {"Key": "tuner2", "Url": "/tuners/tuner2.m3u"}],
    "Users": [{"UserName": "A", "TunerKey": "tuner1"}, {"UserName": "B", "TunerKey": "tuner2"}],
}
TUNER2_M3U = n.ROOT / "tuners" / "tuner2.m3u"
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
    out = {}
    for c in n.ok(admin.get("/LiveTv/Channels", userId=aid), "channels")["Items"]:
        out[c["Id"]] = n.ok(admin.get(f"/Items/{c['Id']}", userId=aid), "item").get("Tags", [])
    return out


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
    check("A PlaybackInfo tuner2 channel = 403 + message", r[0] == 403 and "not available" in text(r), text(r))
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
    for cid in sorted(t1, key=lambda c: expected[c][1]):
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

    print("== 10. logging")
    time.sleep(1)
    log = subprocess.run(["docker", "logs", "--since", since, "jellyfin-dev"], capture_output=True, text=True)
    log = log.stdout + log.stderr
    plugin_lines = [l for l in log.splitlines() if "LiveTvAccess" in l]
    check("plugin never logs a full open token", not any(t in l for t in tokens_used for l in plugin_lines))
    check("denials log a short token hash", any("OpenToken=sha256:" in l for l in plugin_lines))

    failed = [r for r in results if not r[1]]
    print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
