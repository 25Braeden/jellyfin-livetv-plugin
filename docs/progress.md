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
| 2026-10-19 | Gate 3.5 fix 3: snapshots, reserved key, tag ownership, API-key rule | 9032190 | KnownIssue 9/15 pass; 45/45 acceptance |
| 2026-10-19 | Gate 3.5 fix 4: native permission, unrecognized callers, one message | 3eac58b | KnownIssue 12/15 pass; 50/50 acceptance |
| 2026-10-19 | Gate 3.5 fix 5: default-deny filter | 8bd42f7 | KnownIssue 15/15 fixed; 86/86 acceptance |
| 2026-10-19 | Gate 3.5 fix 5b: anonymous callers, legacy params, play sessions | 82bb969 | 104/104 acceptance |
| 2026-10-19 | Gate 3.5 fix 5c: legacy HLS routes bound to running jobs | 29124c3 | 119/119 acceptance |
| 2026-10-19 | Gate 3.5 fix 5d: open-token source, cross-tuner sharing, hidden upstream URLs | 93eb2b8 | 143/143 acceptance |

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
