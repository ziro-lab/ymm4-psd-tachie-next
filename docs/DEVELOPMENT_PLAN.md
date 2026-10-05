# Development plan

Authority date: 2026-10-03. This plan supersedes pre-implementation assumptions. Public source baseline: `73cc24e7eb70a319f15f8191aa31081638ccc3e9`. Source exists and synthetic CI passes; the product is unfinished. Read [requirements](PRODUCT_REQUIREMENTS.md), [architecture](ARCHITECTURE.md), [acceptance](ACCEPTANCE.md) and [Lab evidence](LAB_EVIDENCE.md) together.

## Sequence and phase gates

Each phase is a reviewable change with focused tests and a Draft PR. A later phase may be designed while an earlier host gate is open, but may not treat that gate as a success. Do not expand palette UI before proving source/output connection.

| Phase | Work and dependencies | Current state | Required exit evidence |
| --- | --- | --- | --- |
| A: original input and asynchronous preparation | Snapshot/hash separate from conversion; content reuse, asset sharing, one compiler, cancellation, stale-result guards, shared-lock I/O removal, prepared leases, original-source persistence/load | Core implemented; synthetic and bounded historical native results; **H-A2 OPEN** | A01-A30 mapped to actual results; paused native refresh, cold normal output, failure/cancel/reference change, original-source save/reopen/material listing |
| B: input/draw compatibility | Required PSD/PSB profiles and effects; preserve structure, metadata, precision/dimensions; explicit unsupported diagnostics | RGB8 compiler and flat/tree renderer implemented; full coverage OPEN | Profile/effect inventory, independent pixel oracles, nested masks/groups/blending/alpha, full-quality host comparisons; no silent flattening/omission |
| C: notation/durable references | One notation index per generation over original names; `*`, `!`, flips, duplicate/hierarchy/escaping rules; durable references/schema and ambiguity repair | Published read-only index at `808a5fb`; local prefix/flip evaluation and synthetic WARP candidate; stable current-selection return and direction-only choice memory adopted and locally verified; explicit radio priority within applicable scopes adopted and locally verified, including reverse-order partial memory; first-Y directional initialization policy unchanged/unadopted; ambiguity repair, durable references and native UI/Undo OPEN | Pure notation/reference matrix, native exclusive-switch Undo, cache deletion/source edit/relink preserve or explicitly repair intent |
| D: sparse expressions/presets/Override | Depends on C; base, immutable character preset, item-local patch; target priority/inheritance; empty control patches | Sparse base/item/ordered-face composition implemented; flat native eye/mouth ownership and Inherit verified in PR4; shared presets and complete Override OPEN | Overlapping eye/mouth/hand examples, target-only higher-timeline-layer conflicts and verified ties; local changes cannot mutate presets; native Undo/save |
| E: Extend | Depends on D and proved timeline input; adopt semantics before implementation | **EX1 PROPOSED, not adopted** | Event/range truth table; forward/direct/reverse seek, adjacent split/real gap, temporary override/reset, cache deletion, save/reopen and Undo agree |
| F: eye/mouth animation | C/D plus public host time/voice; deterministic blink seed, mouth settings, stop/default-return/speaking-only controls | Not implemented | Controlled time/voice; playback/scrub/output agree; no redraw-count RNG or extra recognition; unchanged-state GPU reuse |
| G: detailed/simple/settings palettes | C/D/F; selected target, presets/slots/quick parts, native Undo, working-copy confirm/cancel, window preferences | Detailed palette candidate and flat native View/commands/Undo/reopen verified in PR4; logical hierarchy/folding locally verified, native hierarchy pending; simple/settings/presets/animation/window preferences OPEN | Selection/close/disposal races, one Undo per exclusive action, DPI/foreground/scale/settings persistence, virtualized bounded shared-document thumbnails; real interaction |
| H: persistence/lifecycle hardening | Cross-cutting A-G; schema/copy/split/template, move/relink/cache deletion, save/reopen, device replacement, strict output, finite resources | Minimal persistence and scoped guards implemented; full matrix OPEN | Intent survives cache removal and item operations; malformed/unsupported data explicit; native device/cancel/output; measured repeated-use resource recovery |
| I: distribution/final acceptance | A-H | No general-use package or release | Clean install without SDK/terminal/Git/Python/manual compile; first-use through output; permitted local real-material/long-session tests; licenses and full acceptance review |

T01-T12 and Q01-Q04 remain binding. A does not waive notation, expressions, palettes, animation or draw compatibility. Initial compiler concurrency is one. Larger prefetch budgets require measured benefit and resource evidence; current selected-source decoded retention is 16 MiB/30 seconds, not the proposed 32 MiB.

## Immediate detailed-palette checkpoint (2026-10-05)

PR4 head a0a61e9dfb8b33fa79ac03e97242474044f5984f has the bounded flat
native proof (240 assertions). This does not establish groups, exclusive switches,
force-visible controls or flip counterparts in the actual palette. The current
local increment implements logical origin hierarchy and folding without changing
appearance envelopes/history. Synthetic nested groups with exclusive eyes/mouth,
force-visible parts and X counterparts exercise independent partial ownership.
A second fixture checks matched children and a direction-only child under a
flipped group.

Exit for this increment: managed hierarchy/parameter and non-visible row-template
checks; then the existing real-host Actions route must verify actual fold bindings,
unchanged contributor JSON/history on folding, exclusive eye/mouth edits, standard
Undo/Redo, parent hide/restore, X counterparts and native save/reopen. Keep the
successful flat evidence and local/native hierarchy evidence separate.
The hierarchy driver is locally built; a new public push/run is awaiting scope
approval. No human-input or D3D preview-pixel claim follows from these checks.

After this bounded connection, continue the unimplemented basic settings/quick-part,
preset and animation work under T05/T07/T09. Extend still requires its own adopted
semantics. Keep H-A2 active and do not expand the provisional UI design or imply
full phase C/D/G completion from this increment.

## Immediate A checkpoint: H-A2

H-A1 is no longer entirely unknown: [Lab PR 155](https://github.com/ziro-lab/chat-native-work-lab-001/pull/155) observed parameter-only same-frame repaint, and historical downstream observations cover two owners and save/reopen. These are bounded observations, not a universal refresh/dirty-state contract. Preserve their limits in [Acceptance](ACCEPTANCE.md).

H-A2 remains the active gate. `ITachieSource2` supplies Exporting usage but no job ID or job cancellation token. A public progress token exists; job correlation and disposal propagation to a waiting Source are not proved. Source-owned strict waits and reference invalidation are implemented, not proof of normal writer behavior.

1. Search Lab main, PRs and branches for normal writer/cancellation and official bundled writer/resource evidence. Separate the missing host question from product regressions.
2. Stabilize a small synthetic normal project, supported OutputVideo/native writer route and Save-dialog output range/path. Record writer selection, accepted path, terminal result and native errors on **every exit**, including harness failure.
3. Cold-cache case: gate preparation; prove no ready frames/compilation progress and appropriate UI liveness. Release, decode actual output with official bundled FFmpeg and compare every expected frame/range against an independent pixel oracle. Five frames are an initial gate, not performance evidence.
4. Failure case: controlled preparation error must reach the real job boundary, prevent false success and release Source/leases. A detached factory exception is not normal writer evidence.
5. Cancel case: invoke native Cancel while the Source is actually waiting; observe host token, Source disposal/wait cancellation and bounded termination. Test cleanup releasing a gate is not Cancel PASS. Do not inject an uncorrelated global token.
6. Reference-change case: prove explicit abort or a documented immutable job snapshot, without mixed generations/stale appearance/transparent success. Verify the actual caller receives the export-episode guard result.
7. Return reusable facts and failed/blocked conditions to one narrow Lab experiment; link fixed evidence here. Keep H-A2 OPEN until all native writer/product cases pass on the test-time latest stable host.

Two previous native attempts do not close this gate. The first observed cold wait/UI heartbeat, then failed its expected returned-frame assertion with incomplete failure diagnostics. The second selected the normal writer but timed out in Save automation before any Source requests. Neither proves successful output, native Cancel propagation or a completed file. Fix observability/automation before diagnosing semantics.

The [2026-10-03 local matrix](NORMAL_EXPORT_EVIDENCE.md) now observes cold five-frame output, visible preparation failure and reference-clear abort through the official normal writer. It diagnoses the old filename automation/range assumptions and retains the failed records. **H-A2 stays OPEN:** native Cancel sets the real progress token/flag but does not dispose or cancel the gated Source within 3 seconds; termination after the separately logged test release is not cancellation proof. Resolve that correctly correlated public job-lifetime boundary next, then broaden the remaining matrix.

Then complete A's normal plugin/parser loading, material listing/relink, native Undo during completion, actual player device changes and Windows publication/pin/cleanup/watcher cases. Keep synthetic results distinct from OS/host obligations.

## Extend decision checkpoint

Extend remains required; EX1 is a proposal. Decide and record:

- Distinct carry-after-end, inherit-previous/reset and stack-lower/Override controls. Store sparse logical contributions, never bitmaps or sampled animation state.
- Half-open `[Start, End)` intervals and scene/character/logical-asset/continuous-standing-picture scope. Adjacent split may be continuous; real absence resets. Speaking-only suppressed rendering needs a separate presence decision.
- Whether ended contributions retain start-time order, where reset cuts history, and which host input supplies ended contributions for arbitrary seek. A callback containing only active faces is not assumed sufficient.
- Temporary active Override ending, intervening reset, future carry, same-start/same-layer ties and standing-picture return. Legally verify required user-visible compatibility rather than infer legacy behavior from flag names.

Before E code, expand the truth table for carry alone, temporary override/return, reset during carry, reset after override ends, adjacent split/gap, scope separation, direct/reverse seek, empty control patches and conflict priority. Record adopted rules, rejected alternatives and unverified host input. No Extend state machine is part of current A.

## Completion review

The next C/H persistence step has an internal [pure checkpoint prototype](VISIBILITY_CHECKPOINT.md)
with local reconstruction tests. It does not close durable-reference, sparse-settings or native
save/Undo gates. Before native wiring, decide validated PSD-ID/fallback references, sparse
base/default/alias ownership and unsupported-schema retention; then re-test parameter cloning,
atomic native edits and preparation/reopen lifetimes. Do not persist the diagnostic full logical
checkpoint as an item expression snapshot.

Phase exits need the evidence above and updated docs. Final acceptance requires normal timeline/player, save/reopen, actual output and physical-device/resource observations wherever claimed. CI build/WARP cannot replace them. Distribution requires separate explicit approval after feature, quality, lifecycle and workflow acceptance.
