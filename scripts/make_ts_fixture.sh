#!/bin/sh
# Generate the .ts live-stream fixture (test-media/ is gitignored, so re-run this on a fresh clone).
# Loops clip1.mp4 into ~10 minutes of MPEG-TS without re-encoding. The fakestreams nginx
# throttles *.ts (see fakestreams/default.conf) so it behaves like a live feed.
set -eu
cd "$(dirname "$0")/.."
ffmpeg -hide_banner -loglevel error -y \
  -stream_loop 19 -i test-media/clip1.mp4 \
  -c copy -bsf:v h264_mp4toannexb -f mpegts \
  test-media/live1.ts
ls -l test-media/live1.ts
