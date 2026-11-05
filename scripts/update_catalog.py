#!/usr/bin/env python3
"""Write family-specific Jellyfin manifests from verified release ZIPs."""

import argparse
import hashlib
import json
import pathlib
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def package(path, abi, tag, metadata):
    data = path.read_bytes()
    with zipfile.ZipFile(path) as archive:
        assert sorted(archive.namelist()) == [
            "Jellyfin.Plugin.LiveTvAccess.dll", "LiveTvAccess.Core.dll", "meta.json"
        ], "Catalog ZIP must have DLLs and meta.json directly at its root"
        manifest = json.loads(archive.read("meta.json"))
    assert manifest["targetAbi"] == abi, "Wrong build for catalog"
    for key in ("guid", "name", "description", "overview", "owner"):
        assert manifest[key] == metadata[key], f"Release/catalog mismatch: {key}"
    return {
        "version": manifest["version"],
        "changelog": manifest["changelog"],
        "targetAbi": abi,
        "sourceUrl": f"https://github.com/25Braeden/jellyfin-livetv-plugin/releases/download/{tag}/{path.name}",
        # Jellyfin's catalog checksum field uses MD5, not the release's SHA-256 digest.
        "checksum": hashlib.md5(data).hexdigest(),
        "timestamp": manifest["timestamp"],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jellyfin-10-11", type=pathlib.Path, required=True)
    parser.add_argument("--jellyfin-12", type=pathlib.Path, required=True)
    parser.add_argument("--tag", required=True)
    args = parser.parse_args()
    metadata = json.loads((ROOT / "catalog/plugin.json").read_text())
    for filename, path, abi in (
        ("manifest.json", args.jellyfin_12, "12.2.0.0"),
        ("manifest-10.11.json", args.jellyfin_10_11, "10.11.0.0"),
    ):
        version = package(path, abi, args.tag, metadata)
        (ROOT / filename).write_text(json.dumps([{**metadata, "versions": [version]}], indent=2) + "\n")
        print(f"Verified and wrote {filename}: {version['version']} / ABI {abi}")


if __name__ == "__main__":
    main()
