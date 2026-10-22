# Progress log

| Date | Milestone | Commit | Notes |
|---|---|---|---|
| 2026-10-19 | Research + no-code experiment | 4896b41 | `docs/research.md` |
| 2026-10-19 | Gate 1: hello-world plugin | 7947c24 | Loads Active on 10.11.11 |
| 2026-10-19 | Gate 2: action filter sees user identity | 4ad7b5f | Runs on core endpoints |
| 2026-10-19 | Gate 3: enforcement | 009822e | 42/42 acceptance checks |
| 2026-10-19 | Gate 3.5 phase 1: review reproductions | 36a259f | 15 KnownIssue tests |
| 2026-10-19 | Gate 3.5 fix 1: map users by id | ae8c854 | KnownIssue 3/15 pass |
| 2026-10-19 | Gate 3.5 fix 2: sync worker lifecycle | 8c6e9c8 | KnownIssue 5/15 pass; 45/45 acceptance |
| 2026-10-19 | Scale test (16,000 channels) | cc13c53 | See below |
| 2026-10-19 | Gate 3.5 fix 3: snapshots, reserved key, tag ownership, API-key rule | 3f1547a | KnownIssue 9/15 pass; 45/45 acceptance |
| 2026-10-19 | Gate 3.5 fix 4: native permission, unrecognized callers, one message | 775e7f2 | KnownIssue 12/15 pass; 50/50 acceptance |
| 2026-10-19 | Gate 3.5 fix 5: default-deny filter | 51e3769 | KnownIssue 15/15 fixed; 86/86 acceptance |
| 2026-10-19 | Gate 3.5 fix 5b: anonymous callers, legacy params, play sessions | c2db566 | 104/104 acceptance |
| 2026-10-19 | Gate 3.5 fix 5c: legacy HLS routes bound to running jobs | 63a4254 | 119/119 acceptance |
| 2026-10-19 | Gate 3.5 fix 5d: open-token source, cross-tuner sharing, hidden upstream URLs | 79f655e | 143/143 acceptance |
| 2026-10-20 | Gate 3.5 fix 5e: no untracked static streams of Live TV channels; gaps 2-4 documented | 389984d | 164/164 acceptance |
| 2026-10-20 | Gate 3.5 fix 6: live buffer URLs only from the server itself, no forwarded headers | cebec2e | 174/174 acceptance |
| 2026-10-20 | Gate 3.5 fix 7: startup self-check, failed state, admin status API | 9478d14 | 197/197 acceptance |
| 2026-10-20 | Step 3: admin settings page (tuners, user mapping, self-check status) | 1e7d5d5 | 206/206 acceptance |
| 2026-10-20 | Step 4: deploy runbook, overlap check, release packaging, self-healing guide fixture, review files | eca64bc | 208/208 acceptance |
| 2026-10-20 | Final review R1: static exemption only from liveStreamId / params 22 (Sol #1, Argon #6) | 5b82786 | 212/212 acceptance |
| 2026-10-20 | Final review R2: Live TV listings bound to the caller (POST LiveTv/Programs, Q1) | 67f2bc9 | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R3: self-check accepts only scrubber-handled response shapes; unexpected shapes refused (Sol #3) | 9a28d86 | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R4: duplicate-tuner warning logs host + hash, not the URL (Sol #4) | 5452670 | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R5: failed mid-pass user sync re-queued (Sol #2) | b2cf198 | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R6: failed owned-tag history save no longer breaks the snapshot (Argon #4) | 80d1d96 | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R7: history tags stay blocked while channels are re-tagged after a key rename (Q3) | ed98e8e | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R8: unresolvable name-only user entries dropped at save (Q5) | 26b90c4 | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R9: a play session id doesn't satisfy LiveStreams/Open's target rule (Q6) | 1acf78b | Gate 3 219/219 (run on R10) |
| 2026-10-20 | Final review R10: status API refreshes don't reset the claim-evidence window (Q8) | b93e9e0 | Gate 3 219/219 (run on R10) |

## Scale test (16,000 channels), 2026-10-19

| Measurement | Result |
|---|---|
| Plugin first sync, 16,000 untagged channels | 52.9 s, +15 MiB |
| Plugin re-sync, no changes | ~0.58 s |
| Plugin re-sync, 1,000 URLs changed | 5.6 s |
| Restart to ready (A and B can use Live TV) | 7.2–7.6 s (plugin ready at ~3 s, before Jellyfin is healthy) |
| Jellyfin startup delayed by the plugin | No (6.97 s vs 6.56 s with 7 channels; sync is off the startup thread) |
| Filter check per PlaybackInfo | median ~2.2 ms, worst 5.6 ms |
| Channel list for A / B (8,500 each) | first 100 in 55 ms; all in ~394 ms |
| Gate 3 acceptance at this size | Not measured |
| **Jellyfin guide refresh** | **Impractical at this size: quadratic in channel count (Jellyfin, not the plugin). See research.md.** |

Recommendation: import only the channels you want; see research.md for options.
| 2026-10-20 | Final review: full findings table (docs) | ba997b9 | Gate 3 219/219 |
| 2026-10-20 | Final review R11: cap of 1000 distinct identifiers per request (Q2) | e1fc98b | Gate 3 224/224 |
| 2026-10-20 | Final review R12: opens of the same channel / stream serialized (Q4); overlap check is a go-live hard stop | 5957e2f | Gate 3 224/224 |

## Jellyfin 12.2 port (local fake-data stack, 2026-10-22)

| Milestone | Commit | Verification |
|---|---|---|
| net10.0, Jellyfin.Controller/Model 12.2.0, session DTO traversal port | 4f57d6f | Core 175/175; plugin 225/225 |
| 12.2 compose/deploy scripts and environment-targetable Gate 3 | e88701a | Local port 8097; separate container/config names |
| API-only fixture setup | d91f2c0 | Wizard, admin, A/B/C, two limit-1 M3U tuners, XMLTV; no manual tagging |
| Restore missing fake media (gitignored; no commit) | — | clip1.mp4 472128 bytes; clip2.mp4 300844; live1.ts 13163760 |
| Release package uses current DLLs and ABI 12.2.0.0 | 3a44c53 | Regression failed first for stale net9.0/ABI; passed after fix; actual archive verified |
| Section 5 log boundary retains microseconds | 88cd4c2 | Regression failed first; same safety assertion, no check skipped or relaxed |

- Original incomplete attempt: **65 PASS / 7 FAIL**, abort at section 14 due to missing clip1.mp4.
  Every original failure and its classification is recorded in the 12.2 section of `docs/research.md`.
- Full run 1, with copied media and unchanged Gate 3 assertions: **224 PASS / 0 FAIL of 224**.
- Full run 2, after the packaging fix: **224 PASS / 0 FAIL of 224**.
- Final run 3, after the timestamp precision fix: **224 PASS / 0 FAIL of 224**.
- All completed runs execute every section 0–21 (section 10 logging is deliberately last),
  under `caffeinate -dis`, unbuffered Python, and a pipefail-protected tee pipeline.
  Logs: `/tmp/gate3_12_run1.log`, `/tmp/gate3_12_run2.log`, `/tmp/gate3_12_run3.log`.
- Build: **0 warnings / 0 errors**. Core **175/175**, plugin **225/225**, no skipped tests;
  additional Python tooling regressions **2/2**.
- Plugin Active and Healthy on the local 12.2 server. Release/deploy manifests both use
  `targetAbi: 12.2.0.0`, with both whitelisted DLLs present.
- No category (c) port failure found in completed runs; no plugin enforcement code changed.
  Source-sweep watch items and the 10.11 NowViewingItem applicability are in `docs/research.md`.
- Not verified: real clients/providers/tuner hardware, real second tuner, HLS/DASH provider
  sources, 12.2 large-guide scale, or end-to-end websocket safety. No production access,
  branch changes, merge, push, software installs, or modifications to the original project.
