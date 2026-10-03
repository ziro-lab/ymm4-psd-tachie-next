# Acceptance and current evidence

Status date: 2026-10-03. Public baseline: `73cc24e7eb70a319f15f8191aa31081638ccc3e9`. Historical host observations used official YMM4 Lite **4.56.1.0**, SDK **10.0.401**. New tests reconfirm the official latest stable host, not indefinitely inherit this pin.

## Evidence layers

| Evidence | Result | Proves | Does not prove |
| --- | --- | --- | --- |
| [Public Windows CI 37030148213](https://github.com/ziro-lab/ymm4-psd-tachie-next/actions/runs/37030148213) at baseline | Completed/success | Synthetic core/runtime/preparation/WARP, CLI and exact-host integration-source build | Host is not executed; normal timeline/player/save/writer/Cancel/audio/physical GPU outside this lane |
| Local synthetic baseline | Core 34 cases/196 assertions; runtime 11/50; preparation 53/254; flat 11/46; tree 41/261; **150/807 PASS** | Listed synthetic fixtures and Windows WARP | Counts are local results, not newly downloaded CI artifact counts; no full compatibility/user workflow claim |
| [Lab PR 155](https://github.com/ziro-lab/chat-native-work-lab-001/pull/155); fixed source/run in [Lab evidence](LAB_EVIDENCE.md) | Observed paused native repaint matrix | Equivalent ready parameter clone repaints frame 0, retains Source; measured serialized state/selection/Undo unchanged | Live dirty flag/audio/autosave/focus/old-parameter consumers/arbitrary product graphs |
| Historical local downstream paused/save/reopen | 42 assertions, two owners; retained native Source pointers/frame 0; 988 changed preview pixels per side | Bounded product bridge and saved/reopened synthetic project | Not public CI/downloadable artifact; not complete Undo/dirty/device/selection matrix |
| Historical local selected-source prefetch | 166 assertions, 128 switches; CPU preparation promoted on placement; no GPU before placement; expiry about 30.45 seconds | Selected default-item path, priority, 16 MiB/30-second retention; 8/16 MiB accepted, over-limit rejected | Typical PSD/process peaks, physical GPU speed, whole-process limit or proposed 32 MiB setting |
| Historical normal-writer attempt 1 | FAIL after eight assertions | Cold gated preparation/no ready frames/UI heartbeat before release | Returned-frame assertion failed; failure record incomplete; terminal state/output/exact cause unknown |
| Historical normal-writer attempt 2 | Save automation timeout after writer selection; zero Source requests | Normal writer selected and path automation attempted | No Source/output/cancellation boundary evidence; disabled-element timeout is not a writer-contract failure |
| [Local normal writer matrix, 2026-10-03](NORMAL_EXPORT_EVIDENCE.md), source `5272254` | Four cases complete; 50 control assertions; **PARTIAL** | Cold five-frame decode/one compile; preparation failure and reference-clear reach visible native error and stop output | Native Cancel did not interrupt the cold wait in 3 seconds; later termination needs test gate release. H-A2 remains OPEN; no public native artifact |

Historical local observations are labeled as such; do not invent public artifact URLs for unpublished evidence. Reusable host facts should enter Lab with version/conditions/limits before broader adoption.

## A01-A30 retained checklist

“Synthetic coverage” means fixtures/model tests passed at baseline. It does **not** mark an entire row PASS when host/OS obligations remain. Future checkpoints name exact fixtures/result artifacts.

| ID | Required scenario | Evidence and remaining obligation |
| --- | --- | --- |
| A01 | Cold PSD: one compile, original unchanged, parser disposed | Synthetic compiler/preparation; normal input workflow separate |
| A02 | Same-content reopen: reuse, zero parser | Synthetic cache reuse; native cache-delete/reopen matrix remains |
| A03 | Different contents, same size/mtime | Content hash/revision guard fixtures; OS edit boundary remains |
| A04 | Multiple Sources request same asset | Synthetic shared compilation, separate leases |
| A05 | Different paths, equal contents | Content sharing fixtures; logical identities remain distinct |
| A06 | Cancel A, surviving B succeeds | Synthetic separate-waiter cancellation |
| A07 | Cancel last waiter | Cooperative worker/staging cleanup; actual Windows file faults remain |
| A08 | A-B-A, reversed completion | Synthetic request/settings/revision guards; broader native races remain |
| A09 | Original changes during compile | Snapshot/current checks; full Windows watcher recovery OPEN |
| A10 | Complete after Undo | Guard/equivalent clone implemented; native Undo/Redo matrix OPEN |
| A11 | Complete after Clear/Dispose | Synthetic cancellation/leases; native output-wait disposal OPEN |
| A12 | Corrupt manifest/pixels | Synthetic validation/rebuild/error bounds; not all disk faults |
| A13 | Delete cache then reopen | Source-authoritative recovery; full logical settings and cold output OPEN |
| A14 | Capacity/access denied | Explicit failures preserve original; actual disk-full/ACL/locked-file recovery OPEN |
| A15 | Concurrent publication | Repository and Windows competing-process publication fixtures passed; broader crash/stress matrix remains |
| A16 | Scavenge in-use generation | Windows other-process read-pin exclusion and owned-crash-work cleanup fixtures passed; full process-kill recovery/peak-disk matrix remains |
| A17 | Other asset's cold I/O cannot block ready acquisition/release | Shared lock/I/O separation and focused fixtures |
| A18 | Trim after prefetch | Prepared lease survives render; synthetic and historical expiry observation |
| A19 | Missing required render block | Prepared-only boundary, explicit error/no disk-decode fallback; synthetic/WARP |
| A20 | Recompute with old Source holding all blocks | Bounded admission/replacement model/no infinite wait; process/native stress OPEN |
| A21 | Watcher duplicate/overflow/rename | Coalescing/overflow/resume fixtures and Windows real-rename fixture passed; exhaustive event-loss/rearm stress OPEN |
| A22 | Repeated unchanged appearance | Reuse and synthetic counters; long native timeline/resource measurement OPEN |
| A23 | Candidate GPU failure | Atomic guards/WARP; physical device failure/recovery OPEN |
| A24 | Odd/even dimensions, source replacement | Flat/tree pixels/placement fixtures; broader profile/final-host comparisons remain |
| A25 | Async ready while paused | H-A1 bounded native + Lab PR 155; dirty/focus/audio/autosave NOT PROVEN |
| A26 | Cold normal video output | **H-A2 OPEN**; local five-frame cold success and visible preparation failure observed; native Cancel wait interruption OPEN |
| A27 | Original changes during output | Local reference-clear triggers guard/native error after one frame; actual file edit/relink/settings/snapshot matrix OPEN |
| A28 | Normal plugin load with host parser present | Unique `PsdTachieNext.Parser`, exact-host build/historical load; clean release-package matrix future |
| A29 | Original reference save/reopen/move/relink/material listing | Minimal source/`IFileItem`, bounded native reopen; full material/relink/later settings matrix OPEN |
| A30 | Device epoch changes before late candidate | Synthetic stale-stamp rejection; actual player replacement/CPU reuse OPEN |

Representative named fixtures in [preparation tests](../tests/PsdTachieNext.PreparationTests/Program.cs) make the mapping reviewable:

| Obligation | Baseline fixture names |
| --- | --- |
| A01-A07 | `A01-A02-snapshot-cache-restart-no-parser-on-hit`, `A03-same-size-mtime-different-content`, `A04-A06-shared-job-independent-leases-waiter-cancel`, `A05-different-paths-same-content-compile-once`, `A07-cooperative-cancel-after-parser-layer-write-cleans-all-work` |
| A08-A11/A30 | `A08-A10-A11-A30-session-stamps-dispose-late-candidates`, `A09-one-settled-retry-updates-stamp-and-publishes-current-content`, `A09-continuously-changing-source-stops-after-one-retry`, `A11-clear-during-retry-settle-prevents-second-attempt` |
| A12-A16/A20 | `A12-corrupt-pixels-regenerate-once-without-overwrite`, `A13-cache-deleted-reconstruct-from-original`, `A14-write-admission-retains-old-pin-and-removes-failed-new-work`, `A15-Windows-competing-processes-publish-one-generation`, `A16-Windows-other-process-read-pin-excludes-cleanup`, `A14-A20-explicit-document-capacity-keeps-old-lease` |
| A17-A19/A22 | `A17-cold-block-load-does-not-hold-cache-gate`, `A17-slow-document-open-does-not-block-ready-document`, `A16-A18-generation-and-prepared-blocks-pin-through-trim`, `A19-missing-block-has-no-disk-fallback`, `A22-warm-appearance-plan-does-not-reload` |
| A21/minimal A29 | `A21-watcher-hints-coalesce-and-overflow-marks-registered-paths`, `A21-resume-checks-only-active-registrations-once-per-path`, `A21-Windows-real-rename-dirties-old-and-new-registered-paths`, `source-reference-schema-and-relink-keep-logical-identity` |

Fixture names describe scoped assertions, not completion of every corresponding native gate. Renderer suites supply A23/A24 pixel/candidate cases; native A25-A29 need their separate evidence above.

## Host gates

| Gate | Status | Next evidence |
| --- | --- | --- |
| H-A1: async paused repaint | Bounded observation, broader limitations OPEN | Source/frame/saved semantics, dirty/Undo/focus/autosave/disposed-owner cases where claimed |
| H-A2: exact output wait/error/Cancel | **OPEN**; scoped cold/error/reference-clear observed | Native Cancel requests cancellation but no disposal/wait cancellation in the measured 3 seconds; correlate job lifetime and prove interruption without cleanup release |
| H-A3: player context/device/lifecycle | Partial native observations/synthetic stamps | Actual player replacement, Clear/Dispose and playback/output lifetime; WARP callback insufficient |
| H-A4: source persistence/material API | Minimal connection/bounded reopen | Actual resource listing/move/relink, missing/ambiguous handling; no cache path as authority |

Do not close H-A2 with a custom writer, injected token, test cleanup counted as Cancel, or transparent/stale strict-error frames. Public progress cancellation is a candidate until correlated with the actual exporting Source/job.

## New evidence and final acceptance

Record product/source and checkout SHAs, official host version/hash, SDK, commands, synthetic input identity, writer/range, assertions and artifact identity. Preserve diagnostics on success/failure/timeout/cancel. Separate implementation, pure tests, native integration, physical-device measurements and human visual/audio acceptance.

CI uploads named synthetic JSON/provenance only, retained three days; expired artifacts cannot independently prove results. Do not upload user material, derived cache/manifest/pixels/project/screenshots or host binaries. Authorized local material can support local visual acceptance with a redacted public summary.

Final PASS requires [T01-T12/Q01-Q04](PRODUCT_REQUIREMENTS.md), phase exits, normal editing/player/save/output and distribution first-use. Required unsupported profiles remain failures/pending work, not narrowed requirements. Intent must survive deleting every disposable cache.
