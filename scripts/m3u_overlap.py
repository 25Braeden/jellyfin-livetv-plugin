#!/usr/bin/env python3
"""Pre-flight check: do two (or more) M3U playlists list the same stream URL?

Usage:  python3 m3u_overlap.py tuner1.m3u tuner2.m3u [more.m3u ...]

Standalone (Python 3 standard library only), so it can run anywhere the playlist files are, for example on the server
against the files Jellyfin's tuners point at. It reads local files only and makes no network requests.

It never prints a URL: each shared stream is shown as scheme://host[:port] plus the same short hash
GET /LiveTvAccess/Status uses (sha256 of Jellyfin's media source id, the md5 of the URL), and the channel names.
URLs are compared exactly as Jellyfin does (trimmed line, case-sensitive).

Exit code: 0 = no overlap, 1 = overlap found, 2 = usage or read error.
"""

import hashlib
import sys
import urllib.parse


def dotnet_md5_guid(text):
    """Jellyfin's string.GetMD5().ToString("N"): MD5 of UTF-16LE, bytes laid out as a .NET Guid."""
    b = hashlib.md5(text.encode("utf-16-le")).digest()
    return (b[3::-1] + b[5:3:-1] + b[7:5:-1] + b[8:]).hex()


def short_hash(value):
    return "sha256:" + hashlib.sha256(value.encode("utf-8")).hexdigest()[:8]


def location(url):
    """scheme://host[:port] only: never user info, path or query (where providers put credentials)."""
    parts = urllib.parse.urlsplit(url)
    if not parts.scheme or not parts.hostname:
        return "file" if url.startswith(("/", "\\")) else "(unrecognized)"
    default = {"http": 80, "https": 443}.get(parts.scheme.lower())
    port = f":{parts.port}" if parts.port and parts.port != default else ""
    return f"{parts.scheme.lower()}://{parts.hostname.lower()}{port}"


def read_playlist(path):
    """Returns {url: [channel names]} for one playlist."""
    streams = {}
    name = None
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        for raw in f:
            line = raw.strip()
            if not line:
                continue
            if line.startswith("#EXTINF"):
                name = line.rsplit(",", 1)[-1].strip() or "(no name)"
            elif not line.startswith("#"):
                streams.setdefault(line, []).append(name or "(no name)")
                name = None
    return streams


def main(paths):
    if len(paths) < 2:
        print(__doc__.strip().splitlines()[2], file=sys.stderr)
        return 2
    try:
        playlists = {path: read_playlist(path) for path in paths}
    except OSError as e:
        print(f"cannot read playlist: {e.strerror} ({e.filename})", file=sys.stderr)
        return 2

    for i, (path, streams) in enumerate(playlists.items(), 1):
        print(f"playlist {i}: {sum(len(v) for v in streams.values())} channels, {len(streams)} distinct stream URLs")

    owners = {}
    for i, streams in enumerate(playlists.values(), 1):
        for url, names in streams.items():
            owners.setdefault(url, []).append((i, names))
    shared = {url: o for url, o in owners.items() if len(o) > 1}
    if not shared:
        print("OK: no stream URL is listed by more than one playlist.")
        return 0

    print(f"OVERLAP: {len(shared)} stream URL(s) are listed by more than one playlist.")
    print("Users of different tuners would block each other on these channels; remove them from all but one playlist.")
    for url, o in sorted(shared.items(), key=lambda kv: location(kv[0])):
        where = "; ".join(f"playlist {i}: {', '.join(names)}" for i, names in o)
        print(f"  {location(url)}  {short_hash(dotnet_md5_guid(url))}  {where}")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
