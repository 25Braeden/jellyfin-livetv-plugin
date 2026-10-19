#!/usr/bin/env python3
"""Gate 2 probe (LOCAL ONLY): call the watched endpoints as users A, B, C and print the plugin's
"LiveTvAccess probe" log lines next to what each request actually was.

Uses helpers from nocode_experiment.py. Opened streams are closed immediately.
"""

import datetime as dt
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, __file__.rsplit("/", 1)[0])
import nocode_experiment as n  # noqa: E402


def wait_healthy():
    for _ in range(90):
        try:
            with urllib.request.urlopen(n.BASE + "/health", timeout=5) as r:
                if r.read() == b"Healthy":
                    return
        except Exception:
            pass
        time.sleep(2)
    sys.exit("jellyfin-dev not healthy")


def main():
    wait_healthy()
    expected = n.expected_channels()
    own = {"A": "tuner1", "B": "tuner2", "C": "tuner1"}  # C has no tuner; probe a tuner1 channel anyway
    since = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    time.sleep(1)
    sent = []
    for name in ("A", "B", "C"):
        api, uid, _ = n.login(name, f"gate2-{name}")
        st, _ = api.get("/LiveTv/Channels", userId=uid)
        sent.append(f"{name} ({uid}) GET /LiveTv/Channels -> {st}")

        cid = next(c for c, (t, nm) in expected.items() if t == own[name] and nm == "Test News"
                   or t == own[name] and nm == "Test Movies")
        st, info = api.post(f"/Items/{cid}/PlaybackInfo", {"UserId": uid}, userId=uid)
        sent.append(f"{name} POST /Items/{cid}/PlaybackInfo -> {st}")

        if st == 200 and info.get("MediaSources"):
            token = info["MediaSources"][0]["OpenToken"]
            st, res = api.post("/LiveStreams/Open", {"OpenToken": token, "UserId": uid, "ItemId": cid})
            sent.append(f"{name} POST /LiveStreams/Open item={cid} -> {st}")
            if st == 200:
                api.post("/LiveStreams/Close", liveStreamId=res["MediaSource"]["LiveStreamId"])

    admin = n.Api(n.load_api_key(), "gate2-admin")
    st, _ = admin.get("/LiveTv/Channels", userId=n.admin_user_id(admin))
    sent.append(f"API key GET /LiveTv/Channels -> {st}")

    print("== requests sent")
    for s in sent:
        print("  " + s)
    time.sleep(1)
    out = subprocess.run(["docker", "logs", "--since", since, "jellyfin-dev"], capture_output=True, text=True)
    print("== plugin log lines")
    for line in (out.stdout + out.stderr).splitlines():
        if "LiveTvAccess probe" in line:
            print("  " + line.split("] ", 3)[-1])


if __name__ == "__main__":
    main()
