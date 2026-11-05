#!/usr/bin/env python3
"""Package an existing Release build without modifying any server or release."""

import datetime
import hashlib
import json
import pathlib
import re
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def main():
    props = ET.parse(ROOT / "Directory.Build.props").getroot()
    version = props.findtext(".//Version")
    framework = props.findtext(".//TargetFramework")
    assert version and re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version)
    assert framework in ("net9.0", "net10.0")
    family, abi = ("10.11", "10.11.0.0") if framework == "net9.0" else ("12.2", "12.2.0.0")
    metadata = json.loads((ROOT / "catalog/plugin.json").read_text())
    source = (ROOT / "src/Jellyfin.Plugin.LiveTvAccess/Plugin.cs").read_text()
    description = re.search(r"public override string Description =>(.*?);", source, re.S)
    assert description, "Compiled plugin description missing"
    fragments = re.findall(r'"((?:\\.|[^"\\])*)"', description.group(1))
    assert "".join(json.loads('"' + text + '"') for text in fragments) == metadata["description"], \
        "Compiled and catalog descriptions differ"

    name = metadata["name"]
    folder = f"{name}_{version}"
    output = ROOT / "dist" / folder
    output.mkdir(parents=True, exist_ok=True)
    assemblies = ["Jellyfin.Plugin.LiveTvAccess.dll", "LiveTvAccess.Core.dll"]
    build = ROOT / "src/Jellyfin.Plugin.LiveTvAccess/bin/Release" / framework
    contents = {assembly: (build / assembly).read_bytes() for assembly in assemblies}
    manifest = {key: value for key, value in metadata.items() if key != "imageUrl"}
    manifest.update({
        "changelog": "Expanded plugin description and catalog branding; tuner/access code unchanged.",
        "targetAbi": abi,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "version": version,
        "status": "Active",
        "autoUpdate": False,
        "assemblies": assemblies,
    })
    contents["meta.json"] = (json.dumps(manifest, indent=2) + "\n").encode()
    for filename, data in contents.items():
        (output / filename).write_bytes(data)
    archive = ROOT / "dist" / f"{folder}.tar.gz"
    with tarfile.open(archive, "w:gz", format=tarfile.USTAR_FORMAT) as tar:
        for filename in sorted(contents):
            tar.add(output / filename, arcname=f"{folder}/{filename}", recursive=False)
    catalog_zip = ROOT / "dist" / f"{folder}_jellyfin-{family}.zip"
    with zipfile.ZipFile(catalog_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for filename in sorted(contents):
            z.writestr(filename, contents[filename])
    for path in (archive, catalog_zip):
        print(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
