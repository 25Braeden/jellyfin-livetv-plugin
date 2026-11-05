# Publishing the LiveTvAccess catalog

Two manifests share the plugin GUID but intentionally contain different builds:

- `manifest.json`: .NET 10 build, minimum ABI 12.2.0.0; verified on 12.2 and 12.4.0.
- `manifest-10.11.json`: .NET 9 build for Jellyfin 10.11.11, ABI 10.11.0.0.

Users should register only the manifest matching their server family. Compatibility
health checks remain necessary; a minimum ABI is not a promise about future servers.

Edit `catalog/plugin.json` for the description, overview, developer and image URL.
Keep `Plugin.Description` in `src/Jellyfin.Plugin.LiveTvAccess/Plugin.cs` identical:
the installed plugin's compiled description takes precedence over the catalog.
`scripts/package_artifact.py` verifies these descriptions agree. The artwork is
`assets/livetvaccess.png`; its generation prompt is in `assets/README.md`.

For a new release:

1. Update all three version fields in `Directory.Build.props` on both source branches.
2. Run the relevant tests, then `scripts/package_release.sh` on each branch. It emits
   a tarball for manual deploys and a flat ZIP for catalog installation. Do not put
   both builds in one ZIP. Do not modify published ZIPs in place.
3. Collect the two ZIPs in `dist/` in a clean checkout, and generate manifests:

   ```sh
   python3 scripts/update_catalog.py \
     --jellyfin-10-11 dist/LiveTvAccess_0.1.0.1_jellyfin-10.11.zip \
     --jellyfin-12 dist/LiveTvAccess_0.1.0.1_jellyfin-12.2.zip \
     --tag v0.1.0.1
   ```

   Substitute the new version and tag. The script checks ZIP layout, metadata and
   target ABI, then calculates Jellyfin's required MD5 catalog checksum directly
   from each ZIP. Also publish SHA-256 sums for independent artifact verification.
4. Commit the metadata, source description, version, docs, logo and manifests.
   Publish the matching ZIPs in the specified GitHub release before consumers use
   the new catalog. Verify the public asset hashes and manifest/image URLs.

The release ZIP's `meta.json` keeps automatic updates disabled. Existing tuner and
user configuration is stored separately; never include provider credentials in a
release, manifest, screenshot or commit.
