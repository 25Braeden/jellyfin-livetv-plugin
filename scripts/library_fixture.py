#!/usr/bin/env python3
"""Library fixture (LOCAL ONLY): a test movie and a test song, in two libraries on the local Jellyfin.

Creates test-media/library/ (gitignored): a copy of clip1.mp4 as a movie and a generated 10 s tone as an MP3,
then adds "Test Movies" and "Test Music" libraries pointing at them (inside the container: /media/library/...)
and waits for the scan. Idempotent. Used by gate3_test.py to prove normal library use still works.
"""

import pathlib
import shutil
import subprocess
import sys
import time
import urllib.parse

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import nocode_experiment as n  # noqa: E402

LIB = n.ROOT / "test-media" / "library"
MOVIE = LIB / "movies" / "Test Movie (2020)" / "Test Movie (2020).mp4"
SONG = LIB / "music" / "Test Artist" / "Test Album" / "01 - Test Tone.mp3"
LIBRARIES = {"Test Movies": ("movies", "/media/library/movies"), "Test Music": ("music", "/media/library/music")}


def files():
    MOVIE.parent.mkdir(parents=True, exist_ok=True)
    if not MOVIE.exists():
        shutil.copyfile(n.ROOT / "test-media" / "clip1.mp4", MOVIE)
    SONG.parent.mkdir(parents=True, exist_ok=True)
    if not SONG.exists():
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                        "sine=frequency=440:duration=10", "-metadata", "title=Test Tone", "-metadata",
                        "artist=Test Artist", "-metadata", "album=Test Album", str(SONG)], check=True)


def ensure_libraries(admin):
    existing = {f["Name"] for f in n.ok(admin.get("/Library/VirtualFolders"), "folders")}
    for name, (ctype, path) in LIBRARIES.items():
        if name in existing:
            continue
        q = urllib.parse.urlencode({"name": name, "collectionType": ctype, "refreshLibrary": "true"})
        n.ok(admin.call("POST", f"/Library/VirtualFolders?{q}", body={"LibraryOptions": {"PathInfos": [{"Path": path}]}}),
             f"add library {name}")
        print(f"  added library {name} -> {path}")


def ensure_access(admin, library_ids):
    """Test users A, B, C get these two libraries (EnabledFolders); EnableAllFolders is left as is."""
    users = n.users_by_name(admin)
    for name in ("A", "B", "C"):
        policy = users[name]["Policy"]
        folders = set(policy.get("EnabledFolders") or [])
        wanted = {i.replace("-", "") for i in library_ids if i}
        if not policy.get("EnableAllFolders") and not wanted <= {f.replace("-", "") for f in folders}:
            policy["EnabledFolders"] = sorted(folders | wanted)
            n.ok(admin.post(f"/Users/{users[name]['Id']}/Policy", policy), f"library access {name}")
            print(f"  gave {name} access to the test libraries")


def find_items(admin):
    aid = n.admin_user_id(admin)
    out = {}
    for kind, name in (("Movie", "Test Movie"), ("Audio", "Test Tone")):
        st, res = admin.get("/Items", userId=aid, recursive="true", includeItemTypes=kind, searchTerm=name)
        if st == 200 and res["Items"]:
            out[kind] = res["Items"][0]["Id"]
    folders = {f["Name"]: f["ItemId"] for f in n.ok(admin.get("/Library/VirtualFolders"), "folders")}
    out["MoviesLibrary"] = folders.get("Test Movies")
    out["MusicLibrary"] = folders.get("Test Music")
    return out


def main():
    files()
    admin = n.Api(n.load_api_key(), "library-fixture")
    ensure_libraries(admin)
    for _ in range(120):
        items = find_items(admin)
        if "Movie" in items and "Audio" in items:
            ensure_access(admin, [items["MoviesLibrary"], items["MusicLibrary"]])
            print(f"  library ready: {items}")
            return items
        time.sleep(2)
    sys.exit("library scan did not finish")


if __name__ == "__main__":
    main()
