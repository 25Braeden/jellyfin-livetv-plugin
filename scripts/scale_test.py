#!/usr/bin/env python3
"""Scale test (LOCAL ONLY): 16,000 channels across the two tuners.

Subcommands (run in order; each prints its measurements):
  gen                 write tuners/scale/tuner1.m3u + tuner2.m3u (gitignored), 8,000 channels each
  switch scale|small  point Jellyfin's two M3U tuners + the plugin config + the XMLTV guide at a fixture set
  refresh             run Jellyfin's guide refresh, then wait for the plugin pass it triggers
  resync              re-save the plugin config (nothing changed) and time the pass it triggers
  change N            change N stream URLs (half per tuner), refresh, time Jellyfin + plugin
  restart             restart jellyfin-dev; time Jellyfin startup and the plugin's "starting up" window
  filter N            N PlaybackInfo calls per case; filter check time (debug log) and client latency
  lists               channel list / guide / On Now timings for A and B
Reads JELLYFIN_API_KEY from .env; never prints it.
"""

import datetime as dt
import json
import os
import random
import re
import statistics
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

ROOT_SCRIPTS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ROOT_SCRIPTS)
import nocode_experiment as n  # noqa: E402

PLUGIN_ID = "4d5aa36c91644f4dba7f0fa6c25bfd28"
SCALE_DIR = n.ROOT / "tuners" / "scale"
PER_TUNER = 8000
LOGGING_OVERRIDE = n.ROOT / "local" / "config" / "config" / "logging.json"
PASS_RE = re.compile(r"sync pass: (\d+) channel\(s\) retagged in (\d+) page\(s\), (\d+) user\(s\) updated; "
                     r"took (\d+) ms \(users (\d+) ms, channels (\d+) ms\)")

# The original hand-written channels, kept at the top of each scale playlist so gate3_test.py still works.
ORIGINAL = {
    "tuner1": (n.ROOT / "tuners" / "tuner1.m3u"),
    "tuner2": (n.ROOT / "tuners" / "tuner2.m3u"),
}

GROUPS = {
    "News": ["News", "News Live", "World News", "Business", "Weather"],
    "Sports": ["Sports", "Sports Extra", "Football", "Basketball", "Racing", "Golf", "Tennis", "Fight"],
    "Movies": ["Movies", "Classic Movies", "Action", "Drama", "Comedy Movies", "Thriller", "Family Movies"],
    "Kids": ["Kids", "Junior", "Toons", "Teen"],
    "Entertainment": ["Entertainment", "Reality", "Drama Series", "Comedy", "Lifestyle", "Food", "Travel"],
    "Documentary": ["Docs", "History", "Nature", "Science", "Discovery"],
    "Music": ["Music", "Hits", "Rock", "Country", "Classical", "Latin Music"],
    "Locals": ["ABC", "CBS", "NBC", "FOX", "PBS", "CW"],
    "Religious": ["Faith", "Gospel"],
    "PPV": ["PPV Event", "Pay Per View"],
}
COUNTRIES = ["US", "UK", "CA", "AU", "DE", "FR", "ES", "IT", "MX", "BR", "IN", "IE", "NL"]
CITIES = ["New York", "Los Angeles", "Chicago", "Dallas", "Houston", "Atlanta", "Miami", "Boston", "Denver", "Seattle",
          "Phoenix", "Detroit", "Tampa", "Orlando", "Portland", "Austin", "Nashville", "Raleigh", "Cleveland", "Pittsburgh"]
QUALITY = ["", " HD", " FHD", " 4K", " SD", " HEVC"]


def admin():
    return n.Api(n.load_api_key(), "scale-admin")


def now_iso():
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def docker_logs(since, *args):
    out = subprocess.run(["docker", "logs", "--timestamps", "--since", since, *args, "jellyfin-dev"],
                         capture_output=True, text=True)
    return (out.stdout + out.stderr).splitlines()


def ts(line):
    """Parse the RFC3339 timestamp docker prefixes to each line."""
    stamp = line.split(" ", 1)[0]
    return dt.datetime.fromisoformat(stamp[:26].rstrip("Z") + "+00:00")


class MemSampler:
    """Samples jellyfin-dev container memory every ~1 s until stopped."""

    def __init__(self):
        self.samples = []
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def _run(self):
        while not self._stop.is_set():
            out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", "jellyfin-dev"],
                                 capture_output=True, text=True).stdout.strip()
            if out:
                self.samples.append(parse_mem(out.split("/")[0].strip()))

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        self._thread.join(timeout=10)

    def summary(self):
        if not self.samples:
            return "no samples"
        return f"start {self.samples[0]:.0f} MiB, peak {max(self.samples):.0f} MiB, end {self.samples[-1]:.0f} MiB"


def parse_mem(text):
    m = re.match(r"([\d.]+)\s*([KMG]i?B)", text)
    if not m:
        return 0.0
    value, unit = float(m.group(1)), m.group(2)
    return value * {"KiB": 1 / 1024, "MiB": 1, "GiB": 1024, "KB": 1 / 1000, "MB": 1, "GB": 1000}.get(unit, 1)


def wait_for_pass(since, timeout_s=3600, min_retagged=None):
    """Waits for a plugin 'sync pass' log line after `since`; returns (line, numbers)."""
    end = time.time() + timeout_s
    while time.time() < end:
        for line in docker_logs(since):
            m = PASS_RE.search(line)
            if m and (min_retagged is None or int(m.group(1)) >= min_retagged):
                return line, tuple(int(x) for x in m.groups())
        time.sleep(1)
    return None, None


def fmt_pass(nums):
    retagged, pages, users, total, users_ms, channels_ms = nums
    return (f"{retagged} channels retagged in {pages} pages, {users} users updated; "
            f"{total} ms total (users {users_ms} ms, channels {channels_ms} ms)")


# ------------------------------------------------------------------ gen

def gen():
    rng = random.Random(16000)
    SCALE_DIR.mkdir(parents=True, exist_ok=True)

    def channel_names(count, prefix):
        out = []
        for i in range(count):
            group = rng.choice(list(GROUPS))
            base = rng.choice(GROUPS[group])
            country = rng.choice(COUNTRIES)
            if group == "Locals":
                name = f"{country}: {base} {rng.choice(CITIES)}{rng.choice(QUALITY)}"
            else:
                name = f"{country}: {base} {rng.randint(1, 400)}{rng.choice(QUALITY)}"
            tvg = re.sub(r"[^a-z0-9]+", "", name.lower()) + f".{prefix}{i}"
            out.append((name, tvg, group))
        return out

    t1_generated = channel_names(PER_TUNER, "a")
    # Second account at the same provider: ~40% the same lineup (same names / tvg-ids), the rest different.
    shared = rng.sample(t1_generated, int(PER_TUNER * 0.4))
    t2_generated = shared + channel_names(PER_TUNER - len(shared), "b")
    rng.shuffle(t2_generated)

    for tuner, generated in (("tuner1", t1_generated), ("tuner2", t2_generated)):
        original = ORIGINAL[tuner].read_text().strip().splitlines()
        orig_count = sum(1 for l in original if l.startswith("#EXTINF"))
        lines = list(original)
        for i, (name, tvg, group) in enumerate(generated[: PER_TUNER - orig_count]):
            clip = "clip1.mp4" if i % 2 == 0 else "clip2.mp4"
            lines.append(f'#EXTINF:-1 tvg-id="{tvg}" tvg-name="{name}" group-title="{group}",{name}')
            lines.append(f"http://fakestreams/{clip}?ch={tuner}-{i:05d}")
        (SCALE_DIR / f"{tuner}.m3u").write_text("\n".join(lines) + "\n")
        print(f"  {tuner}: {sum(1 for l in lines if l.startswith('#EXTINF'))} channels "
              f"({orig_count} original + generated) -> {SCALE_DIR / (tuner + '.m3u')}")


# ------------------------------------------------------------------ switch / refresh

def switch(which):
    a = admin()
    target = {"tuner1": f"/tuners/{'scale/' if which == 'scale' else ''}tuner1.m3u",
              "tuner2": f"/tuners/{'scale/' if which == 'scale' else ''}tuner2.m3u"}
    os.environ["LIVETV_FIXTURES"] = "scale" if which == "scale" else ""
    cfg = n.ok(a.get("/System/Configuration/livetv"), "livetv")
    hosts = [h for h in cfg["TunerHosts"] if h["Type"] == "m3u"]
    for host in hosts:
        tuner = "tuner1" if "tuner1" in host["Url"] else "tuner2"
        host["Url"] = target[tuner]
        host["TunerCount"] = 1
        n.ok(a.post("/LiveTv/TunerHosts", host), "save tuner host")
        print(f"  Jellyfin tuner -> {host['Url']}")

    plugin_cfg = {"Tuners": [{"Key": k, "Url": v} for k, v in target.items()],
                  "Users": [{"UserName": "A", "TunerKey": "tuner1"}, {"UserName": "B", "TunerKey": "tuner2"}]}
    n.ok(a.post(f"/Plugins/{PLUGIN_ID}/Configuration", plugin_cfg), "plugin config")
    print(f"  plugin config tuners -> {list(target.values())}")

    # Regenerate the XMLTV guide for this fixture set (gitignored tuners/guide.xml).
    import importlib
    importlib.reload(n)
    n.write_guide()
    print(f"  guide.xml regenerated ({n.GUIDE_FILE.stat().st_size // 1024} KiB)")


def refresh(expect_retagged=None):
    a = admin()
    since = now_iso()
    with MemSampler() as mem:
        t0 = time.time()
        status = n.run_task(a, "RefreshGuide", timeout_s=3600)
        jf_s = time.time() - t0
        line, nums = wait_for_pass(since, min_retagged=expect_retagged)
        total_s = time.time() - t0
    print(f"  Jellyfin guide refresh: {status} in {jf_s:.1f} s")
    print(f"  plugin pass after refresh: {fmt_pass(nums) if nums else 'NOT SEEN'}")
    print(f"  refresh start -> plugin pass done: {total_s:.1f} s")
    print(f"  container memory: {mem.summary()}")
    return nums


def guide_small():
    """Guide for the original test channels only. Jellyfin re-reads the whole XMLTV file once per channel
    (GuideManager.cs:220-236 -> XmlTvListingsProvider.cs:138-151), so a full 13 MB guide x 16k channels
    takes ~13 h; a tiny guide keeps refreshes practical for the rest of the scale run."""
    ids = []
    for path in ORIGINAL.values():
        ids += [l.split('tvg-id="', 1)[1].split('"', 1)[0] for l in path.read_text().splitlines() if 'tvg-id="' in l]
    start = dt.datetime.now(dt.timezone.utc).replace(minute=0, second=0, microsecond=0) - dt.timedelta(hours=1)
    fmt = lambda x: x.strftime("%Y%m%d%H%M%S +0000")
    parts = ['<?xml version="1.0" encoding="UTF-8"?>', "<tv>"]
    parts += [f'  <channel id="{c}"><display-name>{c}</display-name></channel>' for c in dict.fromkeys(ids)]
    for c in dict.fromkeys(ids):
        for h in range(6):
            s = start + dt.timedelta(hours=h)
            parts.append(f'  <programme start="{fmt(s)}" stop="{fmt(s + dt.timedelta(hours=1))}" channel="{c}">'
                         f"<title>{c} show {h}</title></programme>")
    parts.append("</tv>")
    n.GUIDE_FILE.write_text("\n".join(parts) + "\n")
    print(f"  guide.xml: {len(dict.fromkeys(ids))} channels, {n.GUIDE_FILE.stat().st_size} bytes")


def task_info(a, key="RefreshGuide"):
    return next(t for t in n.ok(a.get("/ScheduledTasks"), "tasks") if t.get("Key") == key)


def load(which):
    """switch + time the guide refresh Jellyfin queues on its own + the plugin pass after it."""
    a = admin()
    since = now_iso()
    before = task_info(a).get("LastExecutionResult", {}).get("EndTimeUtc")
    with MemSampler() as mem:
        t0 = time.time()
        switch(which)
        # Wait for the refresh queued by the tuner change to finish (start one if none was queued).
        started = False
        while True:
            info = task_info(a)
            last = info.get("LastExecutionResult", {})
            if info["State"] == "Idle" and last.get("EndTimeUtc") != before:
                break
            if info["State"] == "Idle" and time.time() - t0 > 10 and not started:
                n.ok(a.post(f"/ScheduledTasks/Running/{info['Id']}"), "start refresh")
                started = True
            time.sleep(1)
        line, nums = wait_for_pass(since, min_retagged=1000 if which == "scale" else 1)
        total_s = time.time() - t0
    start = dt.datetime.fromisoformat(last["StartTimeUtc"].replace("Z", "+00:00")[:26] + "+00:00")
    end = dt.datetime.fromisoformat(last["EndTimeUtc"].replace("Z", "+00:00")[:26] + "+00:00")
    print(f"  Jellyfin guide refresh: {last.get('Status')} in {(end - start).total_seconds():.1f} s")
    print(f"  plugin pass after refresh: {fmt_pass(nums) if nums else 'NOT SEEN'}")
    print(f"  switch -> plugin pass done: {total_s:.1f} s")
    print(f"  container memory: {mem.summary()}")


def resync():
    a = admin()
    cfg = n.ok(a.get(f"/Plugins/{PLUGIN_ID}/Configuration"), "cfg")
    since = now_iso()
    time.sleep(1)
    with MemSampler() as mem:
        t0 = time.time()
        n.ok(a.post(f"/Plugins/{PLUGIN_ID}/Configuration", cfg), "re-save")
        line, nums = wait_for_pass(since)
        wall = time.time() - t0
    print(f"  no-change pass: {fmt_pass(nums) if nums else 'NOT SEEN'} (wall {wall:.1f} s)")
    print(f"  container memory: {mem.summary()}")


def change(count):
    rng = random.Random(1000)
    per = count // 2
    for tuner in ("tuner1", "tuner2"):
        path = SCALE_DIR / f"{tuner}.m3u"
        lines = path.read_text().splitlines()
        url_idx = [i for i, l in enumerate(lines) if l.startswith("http://") and f"?ch={tuner}-" in l]
        for i in rng.sample(url_idx, per):
            lines[i] = lines[i] + "-v2" if not lines[i].endswith("-v2") else lines[i][:-3]
        path.write_text("\n".join(lines) + "\n")
    print(f"  changed {per} stream URLs on each tuner ({2 * per} total)")
    refresh(expect_retagged=1)


# ------------------------------------------------------------------ restart

def restart():
    os.environ["LIVETV_FIXTURES"] = "scale" if (SCALE_DIR / "tuner1.m3u").exists() and \
        "scale" in json.dumps(n.ok(admin().get("/System/Configuration/livetv"), "cfg")) else ""
    import importlib
    importlib.reload(n)
    exp = n.expected_channels()
    news = next(c for c, (t, nm) in exp.items() if nm == "Test News")
    movies = next(c for c, (t, nm) in exp.items() if nm == "Test Movies")

    since = now_iso()
    t0 = time.time()
    subprocess.run(["docker", "restart", "jellyfin-dev"], capture_output=True)
    healthy = opened = None
    a_ok = b_ok = None
    first_denied = None
    while time.time() - t0 < 900:
        el = time.time() - t0
        try:
            with urllib.request.urlopen(n.BASE + "/health", timeout=3) as r:
                if r.read() == b"Healthy" and healthy is None:
                    healthy = el
        except Exception:
            pass
        if healthy is not None:
            try:
                if a_ok is None:
                    A, au, _ = n.login("A", "scale-restart-A")
                    st = A.post(f"/Items/{news}/PlaybackInfo", {"UserId": au})[0]
                    if st == 200:
                        a_ok = el
                    elif first_denied is None:
                        first_denied = (el, st)
                if b_ok is None:
                    B, bu, _ = n.login("B", "scale-restart-B")
                    if B.post(f"/Items/{movies}/PlaybackInfo", {"UserId": bu})[0] == 200:
                        b_ok = el
            except Exception:
                pass
        if a_ok is not None and b_ok is not None:
            break
        time.sleep(0.25)

    lines = docker_logs(since)
    def when(pattern):
        for l in lines:
            if pattern in l:
                return (ts(l) - dt.datetime.fromisoformat(since.rstrip("Z") + "+00:00")).total_seconds()
        return None
    startup = next((l.split("Startup complete ")[1].strip() for l in lines if "Startup complete" in l), "?")
    print(f"  Jellyfin 'Startup complete' reported: {startup}")
    print(f"  /health Healthy at {healthy:.1f} s after restart" if healthy else "  never healthy")
    print(f"  plugin 'waiting for initial sync' at {when('waiting for the initial sync') or float('nan'):.1f} s; "
          f"'initial sync complete' at {when('initial sync complete') or float('nan'):.1f} s")
    print(f"  first A PlaybackInfo that wasn't 200: {first_denied}")
    print(f"  A PlaybackInfo 200 at {a_ok:.1f} s, B at {b_ok:.1f} s" if a_ok and b_ok else f"  A {a_ok} B {b_ok}")
    nums = next((tuple(int(x) for x in PASS_RE.search(l).groups()) for l in lines if PASS_RE.search(l)), None)
    print(f"  startup pass: {fmt_pass(nums) if nums else 'NOT SEEN'}")


# ------------------------------------------------------------------ filter cost

def filter_cost(count):
    os.environ["LIVETV_FIXTURES"] = "scale"
    import importlib
    importlib.reload(n)
    exp = n.expected_channels()
    t1 = [c for c, (t, _) in exp.items() if t == "tuner1"]
    t2 = [c for c, (t, _) in exp.items() if t == "tuner2"]
    rng = random.Random(5)

    LOGGING_OVERRIDE.write_text(json.dumps(
        {"Serilog": {"MinimumLevel": {"Override": {"Jellyfin.Plugin.LiveTvAccess": "Debug"}}}}))
    time.sleep(3)  # logging.json is reloaded on change (Jellyfin.Server/Program.cs:345)
    try:
        A, au, _ = n.login("A", "scale-filter-A")
        B, bu, _ = n.login("B", "scale-filter-B")
        cases = {"A own channel (allowed)": (A, au, t1), "A tuner2 channel (denied)": (A, au, t2),
                 "B own channel (allowed)": (B, bu, t2)}
        for label, (api, uid, pool) in cases.items():
            since = now_iso()
            time.sleep(0.5)
            client = []
            statuses = {}
            for cid in rng.sample(pool, count):
                t0 = time.perf_counter()
                st = api.post(f"/Items/{cid}/PlaybackInfo", {"UserId": uid})[0]
                client.append((time.perf_counter() - t0) * 1000)
                statuses[st] = statuses.get(st, 0) + 1
            time.sleep(1.5)
            checks = [int(m.group(1)) for l in docker_logs(since)
                      if (m := re.search(r"check took (\d+) us", l))]
            print(f"  {label}: {count} requests, status {statuses}")
            if checks:
                checks.sort()
                print(f"    filter check: median {statistics.median(checks):.0f} us, "
                      f"p95 {checks[int(len(checks) * 0.95) - 1]} us, worst {checks[-1]} us (n={len(checks)})")
            client.sort()
            print(f"    end-to-end PlaybackInfo: median {statistics.median(client):.1f} ms, "
                  f"p95 {client[int(len(client) * 0.95) - 1]:.1f} ms, worst {client[-1]:.1f} ms")
    finally:
        LOGGING_OVERRIDE.unlink(missing_ok=True)


# ------------------------------------------------------------------ lists

def lists():
    a = admin()
    aid = n.admin_user_id(a)
    for name in ("A", "B"):
        api, uid, _ = n.login(name, f"scale-lists-{name}")

        def timed(fn, runs=3):
            times, result = [], None
            for _ in range(runs):
                t0 = time.perf_counter()
                result = fn()
                times.append((time.perf_counter() - t0) * 1000)
            return statistics.median(times), result

        full_ms, (st, full) = timed(lambda: api.call("GET", "/LiveTv/Channels", query={"userId": uid}, timeout=300))
        page_ms, (st2, page) = timed(lambda: api.get("/LiveTv/Channels", userId=uid, startIndex=0, limit=100,
                                                     fields="PrimaryImageAspectRatio,ChannelInfo", enableUserData="true"))
        first100 = [c["Id"] for c in page["Items"]] if st2 == 200 else []
        start = dt.datetime.now(dt.timezone.utc)
        guide_ms, (st3, guide) = timed(lambda: api.post("/LiveTv/Programs", {
            "UserId": uid, "ChannelIds": first100,
            "MinEndDate": start.isoformat(), "MaxStartDate": (start + dt.timedelta(hours=6)).isoformat()}))
        onnow_ms, (st4, onnow) = timed(lambda: api.get("/LiveTv/Programs/Recommended", userId=uid,
                                                       isAiring="true", limit=24))
        print(f"  {name}: full channel list {full_ms:.0f} ms ({len(full['Items']) if st == 200 else st} channels); "
              f"first page of 100 {page_ms:.0f} ms (total {page.get('TotalRecordCount') if st2 == 200 else st2})")
        print(f"     guide grid (100 channels x 6 h) {guide_ms:.0f} ms ({len(guide['Items']) if st3 == 200 else st3} programs); "
              f"On Now (limit 24) {onnow_ms:.0f} ms ({len(onnow['Items']) if st4 == 200 else st4} items)")


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    arg = sys.argv[2] if len(sys.argv) > 2 else None
    {"gen": gen, "switch": lambda: switch(arg), "load": lambda: load(arg), "guide-small": guide_small, "refresh": refresh, "resync": resync,
     "change": lambda: change(int(arg)), "restart": restart, "filter": lambda: filter_cost(int(arg)),
     "lists": lists}[cmd]()


if __name__ == "__main__":
    main()
