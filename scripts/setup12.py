#!/usr/bin/env python3
"""Prepare ONLY the local 12.2 API fixture; never configure/tag via the plugin.

Secrets are read from (or generated into) the gitignored root .env, never printed.
Uses the same Api, tuner/provider calls and guide generator as nocode_experiment,
but deliberately does NOT call its setup() or manual tagging experiment.

12.2 API references in ../jellyfin-12/Jellyfin.Api/Controllers/:
StartupController.cs:40-46,74-84,93-101,109-167; UserController.cs:412-456,517-532;
ApiKeyController.cs:36-59; LiveTvController.cs:950-954,992-1006.
"""

import os
import secrets
import subprocess
import sys

import nocode_experiment as n


# Do not inherit an environment that could target the other stack or real tuners.
n.BASE = "http://localhost:8097"
n.TUNERS = {"tuner1": "/tuners/tuner1.m3u", "tuner2": "/tuners/tuner2.m3u"}
n._SUB = ""
ENV = n.ROOT / ".env"
APP = "local-12.2-gate3"


def require(response, label):
    status, payload = response
    if not 200 <= status < 300:
        # Authentication responses and error bodies may contain credentials.
        raise RuntimeError(f"{label}: HTTP {status} (response body withheld)")
    return payload


def read_env():
    values = {}
    if ENV.exists():
        for line in ENV.read_text().splitlines():
            key, sep, value = line.strip().partition("=")
            if sep and not key.startswith("#"):
                values[key] = value.strip().strip('"').strip("'")
    return values


def save_secret(key, value):
    lines = ENV.read_text().splitlines() if ENV.exists() else []
    lines = [line for line in lines if not line.strip().startswith(key + "=")]
    lines.append(f"{key}={value}")
    fd = os.open(ENV, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as stream:
        stream.write("\n".join(lines) + "\n")
    ENV.chmod(0o600)


def main():
    ignored = subprocess.run(
        ["git", "check-ignore", "--quiet", ".env"], cwd=n.ROOT, check=False)
    if ignored.returncode:
        raise RuntimeError(".env must be gitignored before storing credentials")
    print(".env is gitignored; secrets will not be printed.")
    env = read_env()
    anon = n.Api("", "setup12-startup")
    info = require(anon.get("/System/Info/Public"), "server info")
    if info.get("Version") != "12.2.0":
        raise RuntimeError("Expected local Jellyfin 12.2.0")

    password = env.get("JELLYFIN_ADMIN_PASSWORD_12")
    if not info["StartupWizardCompleted"]:
        if not password:
            password = secrets.token_urlsafe(32)
            save_secret("JELLYFIN_ADMIN_PASSWORD_12", password)
        cfg = require(anon.get("/Startup/Configuration"), "startup configuration")
        cfg.update({"UICulture": "en-US", "MetadataCountryCode": "US",
                    "PreferredMetadataLanguage": "en"})
        require(anon.post("/Startup/Configuration", cfg), "save startup configuration")
        require(anon.get("/Startup/User"), "initialize first user")
        require(anon.post("/Startup/User", {"Name": "admin", "Password": password}),
                "set startup admin")
        require(anon.post("/Startup/RemoteAccess", {"EnableRemoteAccess": False}),
                "local-only remote access setting")
        require(anon.post("/Startup/Complete"), "complete startup wizard")
    if not password:
        raise RuntimeError("JELLYFIN_ADMIN_PASSWORD_12 missing from .env")
    print("Startup wizard complete; admin configured; no libraries added.")

    auth = require(anon.post("/Users/AuthenticateByName", {"Username": "admin", "Pw": password}),
                   "authenticate admin")
    session = n.Api(auth["AccessToken"], "setup12-startup")
    if not auth["User"]["Policy"]["IsAdministrator"]:
        raise RuntimeError("admin is not an administrator")
    key = env.get("JELLYFIN_API_KEY_12")
    if not key:
        keys = require(session.get("/Auth/Keys"), "list API keys")["Items"]
        matches = [item for item in keys if item.get("AppName") == APP]
        if not matches:
            require(session.post("/Auth/Keys", app=APP), "create API key")
            keys = require(session.get("/Auth/Keys"), "retrieve new API key")["Items"]
            matches = [item for item in keys if item.get("AppName") == APP]
        if len(matches) != 1:
            raise RuntimeError("Expected exactly one setup API key")
        key = matches[0]["AccessToken"]
        save_secret("JELLYFIN_API_KEY_12", key)
    admin = n.Api(key, "setup12-admin")
    users = {u["Name"]: u for u in require(admin.get("/Users"), "list users")}
    for name in ("A", "B", "C"):
        user = users.get(name)
        if user is None:
            user = require(admin.post("/Users/New", {"Name": name, "Password": ""}),
                           f"create user {name}")
        policy = user["Policy"]
        policy["EnableLiveTvAccess"] = name != "C"
        require(admin.post(f"/Users/{user['Id']}/Policy", policy), f"save policy {name}")
        require(anon.post("/Users/AuthenticateByName", {"Username": name, "Pw": ""}),
                f"verify empty-password login {name}")
        print(f"User {name}: empty-password login verified; Live TV access {name != 'C'}.")

    cfg = require(admin.get("/System/Configuration/livetv"), "Live TV configuration")
    for name, path in n.TUNERS.items():
        host = next((h.copy() for h in cfg.get("TunerHosts", []) if h.get("Url") == path),
                    {"Type": "m3u", "Url": path, "FriendlyName": name})
        host["TunerCount"] = 1
        require(admin.post("/LiveTv/TunerHosts", host), f"save {name}")
        print(f"Tuner {name}: {path}; stream limit 1.")
    n.write_guide()
    cfg = require(admin.get("/System/Configuration/livetv"), "Live TV configuration")
    provider = next((p.copy() for p in cfg.get("ListingProviders", [])
                     if p.get("Path") == n.GUIDE_PATH_IN_CONTAINER),
                    {"Type": "xmltv", "Path": n.GUIDE_PATH_IN_CONTAINER})
    provider["EnableAllTuners"] = True
    require(admin.post("/LiveTv/ListingProviders", provider), "save XMLTV provider")
    print("XMLTV: /tuners/guide.xml; enabled for all tuners.")

    cfg = require(admin.get("/System/Configuration/livetv"), "verify Live TV configuration")
    assert all(any(h.get("Url") == path and h.get("TunerCount") == 1
                   for h in cfg["TunerHosts"]) for path in n.TUNERS.values())
    assert any(p.get("Path") == n.GUIDE_PATH_IN_CONTAINER and p.get("EnableAllTuners")
               for p in cfg["ListingProviders"])
    libraries = require(admin.get("/Library/VirtualFolders"), "verify no libraries")
    print(f"Setup verified; library count {len(libraries)}; no manual tagging performed.")


if __name__ == "__main__":
    try:
        main()
    except RuntimeError as exc:
        sys.exit(f"ERROR: {exc}")
    except Exception as exc:
        # Avoid traceback/local values or network error strings exposing secrets.
        sys.exit(f"ERROR: {type(exc).__name__} (details withheld to protect credentials)")
