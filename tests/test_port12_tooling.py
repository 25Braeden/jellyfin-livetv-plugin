#!/usr/bin/env python3
"""Local packaging regressions; stdlib only, no server or credentials needed."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent.parent


class ReleasePackageTests(unittest.TestCase):
    def test_package_uses_current_framework_and_12_2_manifest(self):
        framework = ET.parse(ROOT / "Directory.Build.props").findtext(".//TargetFramework")
        with tempfile.TemporaryDirectory(prefix="package-test-", dir=ROOT / "local12") as temp:
            root = Path(temp)
            (root / "scripts").mkdir()
            script = root / "scripts/package_release.sh"
            shutil.copyfile(ROOT / "scripts/package_release.sh", script)
            dlls = ("Jellyfin.Plugin.LiveTvAccess.dll", "LiveTvAccess.Core.dll")
            # Leave stale 10.11 build output present: packaging must never select it.
            for target in ("net9.0", framework):
                output = root / "src/Jellyfin.Plugin.LiveTvAccess/bin/Release" / target
                output.mkdir(parents=True, exist_ok=True)
                for dll in dlls:
                    (output / dll).write_bytes(target.encode())
            tools = root / "tools"
            tools.mkdir()
            dotnet = tools / "dotnet"
            dotnet.write_text("#!/bin/sh\nexit 0\n")
            dotnet.chmod(0o755)
            env = dict(os.environ, DOTNET_ROOT=str(tools), GIT_DIR=str(ROOT / ".git"))
            subprocess.run(["sh", str(script)], cwd=root, env=env, check=True,
                           capture_output=True, text=True)
            package = root / "dist/LiveTvAccess_0.1.0.0"
            manifest = json.loads((package / "meta.json").read_text())
            with self.subTest("targetAbi"):
                self.assertEqual("12.2.0.0", manifest["targetAbi"])
            self.assertEqual(set(dlls), set(manifest["assemblies"]))
            with tarfile.open(str(package) + ".tar.gz") as archive:
                for dll in manifest["assemblies"]:
                    with self.subTest(dll=dll):
                        self.assertEqual(framework.encode(), (package / dll).read_bytes())
                        packed = archive.extractfile(f"{package.name}/{dll}")
                        self.assertIsNotNone(packed)
                        self.assertEqual(framework.encode(), packed.read())


if __name__ == "__main__":
    unittest.main()
