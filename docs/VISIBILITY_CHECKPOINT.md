# Pure visibility checkpoint and persistence decision gate

Historical internal experiment. The subsequent approved sparse implementation and current
native evidence boundaries are documented in [SPARSE_APPEARANCE.md](SPARSE_APPEARANCE.md).
Statements below describe the earlier experimental stage, not the current parameter connection.

Status: local internal prototype, 2026-10-04. Product save format and native integration are OPEN.

## Existing persistence

`SourceAssetRef` schema 1 stores logical asset identity and original path. Relinking retains
identity. `CompiledItemParameter` serializes only this source reference; its equivalent-refresh
clone copies the reference and refresh revision. Character/face parameters contain no sparse
appearance schema. `LayerRef` is source-hash/generation-local. PSD extra tags are retained as
metadata blocks, but a validated persistent PSD layer-ID index does not yet exist.

Do not connect a raw checkbox array to the saved item: the adopted radio policy also requires
partial direction masks, orientation and logical origins. Evaluated counterpart IDs and render
visibility are not a durable expression representation.

## Implemented experimental seam

`PsdVisibilityCheckpoint` is **internal** and has no saved parameter, palette, native Undo,
preparation or renderer connection. Its experimental version is not a published project format.
It is a complete logical-state checkpoint for testing reconstruction, not the phase-D sparse
base/preset/item-patch schema. The tests write/read JSON using System.Text.Json; this is not
proof of Newtonsoft/native YMM4 serialization.

Capture keeps logical asset identity, source/generation identity, orientation, every canonical
raw alias choice and each origin's radio mask together in immutable records/collections. A flat
reference forest retains original names, kinds, parent links, duplicate occurrence/count, origin
links and applicable directions. Saved keys address this forest and never directly address a
different generation. No display-name, escaped-path, proximity or sibling-order inference is
used across generations. The forest avoids recursive path serialization and quadratic storage
for deep trees.

Restore builds one candidate and returns it only after all references, origins, alias membership,
scopes, force rules and radio consistency checks pass. It does not replay user setters or repeat
None normalization. A repair result has no replacement state and retains the exact checkpoint.
Unsupported experimental versions throw instead of producing defaults. Unsupported/malformed
JSON is not accepted through a native parameter because no such parameter exists yet.

Same-generation recovery verifies every saved reference against target metadata, including
duplicates. Across generations, recovery is disabled by default. The explicit experimental
`allowUniqueHierarchyRecovery` seam admits exact unique original-name/kind hierarchy only;
renamed, moved, missing or duplicated paths, changed origin/alias membership/direction scope,
and new competing radio defaults require repair. This candidate matching rule cannot distinguish
deleting and recreating a unique same-name part; it is not an adopted automatic relink policy.
An absent appearance property still uses the original default factory and empty-first-Y rule.

## Minimal product proposal: decisions required before native wiring

1. **Persistent references.** Derive and validate available PSD layer IDs from preserved metadata,
   including duplicate/invalid IDs and group identity. Decide whether unique original hierarchy
   may be an automatic fallback or only a repair suggestion. Do not use duplicate ordinal or
   generation-local NodeId as evidence across source changes.
2. **Sparse ownership/defaults.** Save an immutable base appearance separately from presets and
   item additions. Candidate operations need explicit logical target, local hide/show and radio
   scope intent; absent operations inherit. Orientation needs absent/inherited versus explicit
   None where expressions require it. Decide how changed source defaults interact with an
   untouched base and sparse inherited parts, and how the protected one-pass/P2 raw aliases are
   reconstructed without turning inherited preview into item-owned settings. The complete
   experimental checkpoint must not be copied wholesale into the final expression schema.
3. **Schema evolution.** Adopt a versioned native DTO and lossless unknown-version/field handling
   before writing project data. Preserve opaque unsupported data; never coerce it to an empty
   patch. Existing source-only projects must keep today's defaults until an actual user edit.
4. **Native connection after these decisions.** Assign the complete immutable appearance value
   through one supported parameter setter per action; keep refresh clones semantically identical,
   include appearance changes in revision/publication guards, and feed the resolved value into
   preparation/render identity. Prove clone/copy/split, one-action Undo/Redo, late preparation,
   cache-free save/reopen and unsupported-data retention using exact-host native evidence.

The first three points are a design gate, not permission to invent product semantics. Continue
pure reference/schema work once these policies are adopted; investigate any uncertain native
setter/save/clone contract through the canonical Lab before native implementation. H-A2 stays
OPEN, including the unresolved public job-to-Source cancellation boundary; this work adds no
token bridge, hook or output workaround.

## Validation boundary

Eight synthetic runtime cases cover JSON disk roundtrips, partial X=A/Y=B masks, retained None
and XY, immutable clones/old checkpoints, hidden-parent intent, protected P2 aliases and one-pass
None, reordered unique reference recovery, explicit recovery opt-in, missing/moved/renamed/
duplicate references, counterpart membership changes, conflicting new radio defaults,
unsupported/malformed checkpoint values, 4096-deep forests and 512-radio documents. Block-read
counts remain zero during this checkpoint work.

The first run retained one failure: a newly added counterpart with unchanged aggregate scopes
was admitted. Recovery now also requires identical alias membership for each saved origin.
The failing evidence is preserved separately from corrected runs.

Final local CPU results: core **34/196**, runtime **53/814**, preparation **61/352**:
**148 cases / 1362 assertions**, zero failures and compiler warnings/errors. SDK **10.0.401**;
ordinary sandbox restore uses one explicit offline NuGet configuration and task-local cache/temp
paths. The final run's source/evidence hashes and commands are recorded in the private checkpoint
audit. Official latest stable was checked at test time as **4.56.1.0** via the
[official release history](https://manjubox.net/ymm4/release/); these CPU tests do not load a host.

No new WARP, Windows Actions, native YMM4, palette, save/reopen, native clone/Undo/Redo or output
result is claimed. The earlier radio-priority candidate's 140/1244 CPU and 51/509 WARP records
remain historical protected evidence for that source, not new validation for this prototype.

## Physical radio input-validation review follow-up

Review found a second P2 input-validation counterexample. Starting with the protected initial
raw choices `[0,2,4]`, changing only saved node 1 to locally visible was accepted: None raw
became `[0,1,2,4]` and active nodes `[0,1,2]`. Radio siblings 1 and 2 have the same logical
origin, and sibling 2 has zero structural scope, so the earlier logical-origin/scope checks
did not reject their simultaneous physical selection. Hiding the parent masked the display
but retained the invalid physical choices. The pre-fix runtime run records all four saved
orientations with both shown and hidden parents, plus an escaping unsupported-notation
exception: **53/55 cases, 829 assertions, two failures**. An earlier test-only single-params
target-typing build error is retained separately; explicit fixture typing corrected it.

Before returning a candidate, recovery now evaluates each None/X/Y/XY orientation and counts
locally enabled physical radio nodes per immediate parent, before ancestor/opacity hiding.
Multiple siblings are rejected even for one origin or a zero-scope alias. The repair result
retains the exact checkpoint and contains no replacement state. No setter replay or second
None normalization repairs corrupted input. Unsupported target notation and blocking flip
bindings are caught narrowly and classified as preserved repair results.

Two additional runtime cases cover the corruption matrix, valid shown/hidden P2 snapshots,
and saved-hidden unsupported token names/blocking ambiguous bindings without block reads.
The final fresh CPU run passes core **34/196**, runtime **55/835**, preparation **61/352**:
**150 cases / 1383 assertions**, compiler warnings/errors and failures zero. SDK **10.0.401**.
The original 148/1362 checkpoint, radio-priority source and all failing runs remain protected.
This reviewed candidate is still internal; no sparse product schema or native connection is
adopted. WARP, Actions, native Undo/clone/save/reopen and H-A2 remain OPEN.
