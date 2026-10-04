# Acceptance and current evidence

Latest source-only review checkpoint (2026-10-04, base808a5fb): [scope, evidence and open gates](PALETTE_CHECKPOINT.md). Local native83 and WPF-component11 are narrowly scoped; historical rows/checklists below are retained and do not become whole-feature PASS.


Historical baseline status date: 2026-10-03. Public baseline: `73cc24e7eb70a319f15f8191aa31081638ccc3e9`. Historical host observations used official YMM4 Lite **4.56.1.0**, SDK **10.0.401**. New tests reconfirm the official latest stable host, not indefinitely inherit this pin.

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

## Phase-C notation, prefix and flip visibility checkpoints

Published source `808a5fb` adds the shared, generation-local notation index. Synthetic runtime cases cover the pinned PSDTool prefix/suffix matrix, unresolved-name diagnostics, hierarchy and duplicate IDs, literal special characters, rejection of negative parent IDs instead of aliasing the root, concurrent/identical-copy sharing, source-generation separation, metadata release after disposal, and deep trees without recursive path construction.

The preceding local uncommitted checkpoint connected immutable prefix visibility to prepared RenderPlans and the original-source/prefetch paths. Five focused runtime cases cover default radio selection within each immediate parent, duplicate names, independent ordinary overlap, forced local visibility, retained hidden-parent choices, required-block admission, clipping/zero opacity, generation mismatch/invalid IDs, and explicit flip/token-edge rejection before compiled-block reads. Three original-source preparation cases initially covered switched pixel blocks without recompilation, consistent prefetch/actual-use defaults, failure cleanup and the retained raw diagnostic seam. The initial prefix checkpoint's local CPU suites passed core 34 cases/196 assertions, runtime 24/241 and preparation 57/306: **115 cases / 743 assertions**. Build warnings/errors were zero. The first runtime build failed on test-only target typing of a single `params` argument; the explicit test type was corrected and runtime was rerun successfully. These are local Windows CPU results, not a new Actions or native-host result.

One tree WARP regression connects the state to actual prepared pixels: hidden-parent switching leaves the effective output/composition count unchanged, showing the parent renders the retained choice, and an exclusive switch changes green to red without leaking transient graph objects. The local host-bound tree suite passes **42 cases / 272 assertions, pixel tolerance 0** against the version/hash-verified official latest stable 4.56.1.0 references. The harness uses the same tracked renderer/cases, the existing host signed-pitch/AlphaMode compatibility source, and only adapts WARP device creation to the public host out-device overload with a null guard. Its first two builds failed on omitted compatibility bindings and nullable handling; both records are retained, and the corrected run passes with zero warnings/errors. Plugin, host-driver and CLI builds also pass with zero warnings/errors. This is a private local harness, not the pinned NuGet 3.8.3 Actions lane; YMM4 was not launched and no host payload was installed. Required new source CI and native editing/save/output gates remain OPEN.

The reviewed diagnostic follow-up separates `UnsupportedPsdNotationException` and `PreparationRecovery.UnsupportedNotation` from generic unsupported pixel/drawing profiles. Runtime cases cover visible and hidden flip/token-only names, combined reasons and notation-specific message/action without RGB8 replacement advice. An original-PSD session case checks that a saved-hidden offending node fails the entire request, publishes no ready appearance, performs no unstable-source retry, releases the shared document/decoded blocks and exposes the correct user-facing diagnostic. The original-source compiler has already converted the initial PSD pixels when this profile rejects it: "before decoding" here means only compiled-block acquisition/decoding. The corrected CPU suites pass core **34/196**, runtime **24/310** and preparation **58/330**: **116 cases / 836 assertions**, zero failures and build warnings/errors. The corrected source also passes host-bound WARP **42/272** and Plugin/HostProof/CLI builds with zero warnings/errors. Updated run identities are recorded separately from the initial checkpoint above; the local harness, required Actions and native-host distinctions remain unchanged.

The next protected local candidate adds pure None/X/Y/XY evaluation and immutable original-name flip bindings against PSDTool commit `5f40b67da531db5e689831aba71bd438d5ee0ae0`. Six focused runtime cases cover independent flip states, saved-visible prefix normalization, duplicate child IDs, exact raw child names, hidden-parent retention, recursive child-before-parent flip order, missing candidates/children and blocking ambiguities. Missing bases and unmatched children retain reference-defined choices with diagnostics; ambiguous base names, overlapping variants, kind differences and unequal duplicate counts stop preparation without guessed correspondence. Two original-source preparation cases cover switched counterpart blocks without recompilation and diagnostic-preserving prefetch of an unmatched hidden variant. The full-profile token-only rejection retains notation-specific guidance; the legacy prefix-only seam continues to reject flips explicitly.

The preceding flip checkpoint's local CPU suites passed core **34/196**, runtime **30/410** and preparation **60/339**: **124 cases / 945 assertions**, zero failures and build warnings/errors. Three additional synthetic WARP cases verify exact whole-canvas None/X/Y/XY pixels, shared X/Y suffix versus separate XY selection, flip identity/context restoration, and duplicate-radio child transfer under a hidden parent. The host-bound tree suite passed **45 cases / 315 assertions, tolerance 0**; Plugin/HostProof/CLI builds passed with zero warnings/errors against verified official stable 4.56.1.0 references. The first new WARP build failed on test-only object-initializer syntax; that record is retained and the corrected fresh run passes. This is the same private host-reference harness distinction described above, not the pinned NuGet 3.8.3 Actions lane. YMM4 was not launched and no payload was installed.

Independent review found a missing-base/nested-flip initialization counterexample absent from the preceding successful suites. Before correction the new runtime fixture failed: the second None evaluation lost a visible child. Executing the pinned reference's flip registration/transfer/setter methods with ordinary-node stubs confirms active IDs `[0,1,2]` after one initialization pass, while the old candidate produced `[0,1]`. The correction normalizes saved variants once and directly consumes normalized None choices. Added runtime and exact WARP cases cover this initial result, direct X/Y/XY evaluation and pure None restoration. Fresh initialization plus one reference setter yields X `[3,4]`, Y/XY `[0,1]`; X-to-None yields `[0,1,2]`, Y/XY-to-None yields `[0,1]`. The pure checkpoint restores current canonical None `[0,1,2]` when no edit has occurred. Repeated reference setters are non-idempotent for this accepted missing-binding input. On 2026-10-04 the user adopted stable current-selection return as an intentional compatibility difference; the later adopted direction-only model and remaining mixed-scope guard are described below.

Corrected local CPU suites pass core **34/196**, runtime **31/427**, preparation **60/339**: **125 cases / 962 assertions**. Corrected host-reference WARP passes **46 cases / 331 assertions, tolerance 0**. Plugin/HostProof/CLI builds also pass with zero compiler warnings/errors. The failing pre-fix counterexample and previous successful suites remain retained; only these corrected run identities include the new counterexample. The private host-reference versus Actions/native-host distinctions remain unchanged.

Four new runtime cases cover edits in every orientation, latest-choice return, independent ordinary overlap, duplicate radios, same-generation idempotence, shared X/Y and separate XY, hidden-parent retention, immutable owner snapshot undo/redo and foreign-generation rejection. A characterization case confirms the retained-but-None-hidden unmatched ordinary choice, the unmatched radio overwritten by matched-child transfer, and the P2 case's independently retained bound counterpart after editing its normal node off. These are explicit limitations of a raw diagnostic seam, not accepted end-user behavior. Runtime result counts are recorded separately after this added characterization. Core **34/196** and preparation **60/339** retain their preceding results because their behavior is unchanged; the new production edit is API documentation only. The evidence inventory combines these retained results with the current runtime result, not a fresh full-CPU run. New host-reference WARP coverage verifies edited and restored snapshot pixels for X/Y/XY and latest normal-side return: **47 cases / 372 assertions, tolerance 0**; Plugin/HostProof/CLI builds pass with zero warnings/errors.

The latest local candidate implements the subsequently adopted direction-only selection policy. Seven focused runtime cases cover missing-base roots and unmatched children; None/X/Y/XY scope masks; shared X/Y versus separate XY; force/radio rules; common transfer with retained direction overrides; common edits and alias cleanup; hidden parents; nested internal bindings; immutable snapshot consistency; a 512-radio index; and structured rejection of ambiguous ownership or impossible scopes. Prior unmatched-radio overwrite and P2 stale-alias characterization are now regressions for the corrected behavior. An original-source preparation case checks required-block selection, force visibility and flip round trips without recompilation or input changes. Two exact WARP cases verify direction masks, hidden-parent restoration, radio/common edits and snapshot pixels with expected composition/read counts and zero transient graph leaks.

Fresh full CPU validation passes core **34 cases/196 assertions**, runtime **42/613** and preparation **61/352**: **137 cases / 1161 assertions**, zero failures. Fresh host-reference WARP passes **49 cases / 431 assertions, pixel tolerance 0**; Plugin/HostProof/CLI builds pass with zero compiler warnings/errors. These counts come from new full executions, not retained-suite inventory. A new mixed-scope guard initially rejected a safe Y-only common-radio edit; that one-failure run remains retained. The guard was narrowed to multi-scope conflicts and all CPU/WARP suites were rerun in distinct successful runs. Prior candidates and evidence remain protected.

Official latest stable was checked anew before validation: **4.56.1.0**, executable SHA256 `3a2beb9890f0c3c88a4b0d17c2d8a6a5f1f974ce1d411a58e2769f9c8b473e9a`, SDK **10.0.401**. The same tracked tests/renderer and existing host compatibility shim run against the private host references; only public WARP out-device creation is adapted. This is not the pinned NuGet 3.8.3 Actions lane or actual YMM4 execution. No host launch, payload deployment, native Undo, save/reopen, normal writer, physical-GPU performance or source-edit persistence is proved by these results.

At the preceding direction checkpoint, partially overlapping radio edits were guarded pending an explicit policy decision. That guard is superseded by the adopted priority follow-up below. Durable references/source-edit repair, sparse saved settings/PFV, palette/native exclusive-switch Undo and full T08/C compatibility remain OPEN. Source-level ambiguous ownership and unresolved token-only/empty flip bases remain checkpoint restrictions, including hidden nodes; missing-binding diagnostics are metadata without warning UI. No persisted parameter or end-user selection control is added. Required Actions/native editing/save/output evidence and H-A2 remain OPEN.

## Direction radio initial fallback review follow-up

Independent review found a further radio P2 counterexample absent from the preceding 137/1161 CPU and 49/431 WARP suites: hidden `body` with hidden `*part` and visible `*part:flipx`, plus visible `body:flipx` with visible `*part:flipx`. One reference None pass selects `[0,2,4]`, but the direction candidate's common baseline fallback ignored selected common alias 2 and added origin 1. The failing execution records checked `[0,1,2,4]`, effective `[0,1,2]`, and **43/44 runtime cases, 625 assertions, one failure**. The original direction candidate and failure evidence are retained.

The minimal correction counts selected common aliases in the existing sibling group before adding a common baseline. It does not rerun None normalization or add direction-only fallback. Initial checked IDs are now `[0,2,4]`, effective IDs `[0,2]`. Two new runtime cases cover exact initial exclusivity, None/X/Y/XY round trips, immutable snapshot preservation and common edits, plus the unchanged empty first-Y result for a group containing only saved-selected X-only A and unselected Y-only B. One WARP case verifies separate-position pixels through every return to None, an edited common choice and restored snapshot, with nine compositions, three block reads and zero transient graph objects.

Fresh full CPU passes core **34/196**, runtime **44/659** and preparation **61/352**: **139 cases / 1207 assertions**, zero failures. Fresh private host-reference WARP passes **50 cases / 459 assertions, tolerance 0**; Plugin/HostProof/CLI and test builds pass with zero compiler warnings/errors. Official latest stable was checked again before testing: **4.56.1.0**, with the same verified executable hash and SDK **10.0.401**. This is local unpublished source validation, not the required NuGet 3.8.3 Actions lane, native host launch, Undo, save/reopen or output proof.

At the radio P2 checkpoint, mixed X-only A versus X/Y-shared B priority remained OPEN and conflicting edits were guarded. The subsequently adopted B-priority rule is implemented and verified below. The separate first-Y empty-selection initialization policy remains unadopted and unchanged. Native/persistence/ambiguity-repair and full phase-C/T08 gates remain OPEN.

## Adopted radio selection priority follow-up

The user answered B for X-only A selected in X followed by X/Y-shared B selected in Y. Explicit radio edits now win in the selected part's valid structural directions; changing orientation alone preserves memory. A later X-only A selection updates X and retains B in Y. XY choices and unavailable directions stay intact outside the overlap. Immutable owner radio masks represent partially retained scope, and actual alias-slot intersections limit updates; no display-name inference or extra normalization pass is introduced. The old mixed-scope exception guard is removed in this local unpublished candidate. Selecting an all-direction common part intentionally updates every direction it supports.

Fresh full CPU passes core **34/196**, runtime **45/696** and preparation **61/352**: **140 cases / 1244 assertions**, zero failures. Fresh private host-reference WARP passes **51 cases / 509 assertions, tolerance 0**. Plugin/HostProof/CLI and test builds pass with zero compiler warnings/errors. Official latest stable was checked anew before final validation: **4.56.1.0**, the same verified executable hash, SDK **10.0.401**. This remains private host-reference validation rather than NuGet 3.8.3 Actions or actual YMM4 execution.

Updated and new runtime coverage checks B priority, reverse selection preserving B's Y scope, explicit reselection of an already checked partial part, all None/X/Y/XY results, pure map reuse during flips, common parts with no X counterpart, and preservation of the radio P2 alias in None during a Y-only edit. A new exact WARP case checks both orders, hidden-parent edit without recomposition, restoration on show, None/XY scope isolation and old snapshots: **21 compositions / five block reads / zero transient graph objects**. Existing full-common pixel oracles now reflect the adopted all-applicable-direction update. The earlier radio baseline P2 and one-pass None regressions remain passing; the first-Y empty initialization policy is asserted unchanged. No test/build failure occurred in this priority stage; earlier failing P2/guard evidence is retained.

The adopted radio conflict decision is complete at the pure state/render seam. Save/reopen, sparse settings, durable/source-edit references, palette/native Undo/output and required new Windows Actions remain OPEN; full T08/C compatibility and H-A2 remain OPEN. Immutable snapshot restoration is verified and does not claim native Undo integration. Prior candidates remain protected, with no original application, publication or permanent configuration change.

## Host gates

The subsequent [sparse appearance checkpoint](SPARSE_APPEARANCE.md) implements the approved
changed-default and repair-candidate policies, stable PSD layer-ID references, sparse ownership,
orientation/masks, atomic recovery and opaque unknown-data retention. Fresh CPU results are
**34/196 + 61/894 + 63/383 = 158 cases / 1473 assertions PASS**. Synthetic PSD/PSB tests include
reconstruction in an entirely empty cache and preserved intent on unresolved references before
compiled pixel acquisition. Managed exact-host parameter JSON/refresh-clone tests and builds
are separate evidence, not native project save/Undo/copy/split or GUI output. The earlier
complete-state checkpoint results below remain historical, internal experiment evidence.

The subsequent checkpoint input-validation review reproduces the same-origin/zero-scope P2
double-radio counterexample, including hidden parents. All four evaluated physical orientations
are now checked before candidate return, and unsupported target notation/bindings produce
non-destructive repair results. Fresh CPU **34/196 + 55/835 + 61/352 = 150/1383 PASS**, zero
compiler warnings/errors. See the [review record](VISIBILITY_CHECKPOINT.md) for retained failures
and the unchanged internal-only/native evidence boundaries.

The local [pure visibility checkpoint](VISIBILITY_CHECKPOINT.md) passes core **34/196**,
runtime **53/814** and preparation **61/352**: **148 cases / 1362 assertions** in a fresh
ordinary-sandbox CPU run, with zero failures and compiler warnings/errors. Eight new runtime
cases prove only internal JSON/state reconstruction and conservative reference recovery.
The earlier counterpart-membership recovery failure is preserved. The types are internal;
product sparse serialization, native parameter clone, native Undo/Redo and actual save/reopen
remain OPEN. No new WARP/Actions or native-host evidence is included.

An additional independent A09/A14 Windows regression exercises a real exclusive lock on a synthetic original source through the initial attempt and its single automatic retry. It checks the unstable-source diagnostic, retention of the previously prepared pixels, rejection of the stale publication stamp, snapshot cleanup, and recovery through an explicit new request after lock release. This is CPU preparation/OS-sharing coverage; it does not prove native Undo, visible preview retention or the normal writer's Cancel behavior. H-A2 remains OPEN.

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
