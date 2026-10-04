# Sparse appearance persistence checkpoint

Local unpublished implementation, 2026-10-04, baseline `808a5fb` plus the protected radio/P2
candidate. The two behavior decisions were approved: untouched parts follow the new PSD defaults;
names/hierarchy alone give repair candidates after a source change, never automatic binding.
The first-Y empty direction-only default remains unchanged. Preset/face/item stacking UI and
native Undo/copy/split/save remain subsequent gates.

## Stored intent

`PsdAppearanceSettings` is an immutable, bounded JSON envelope with `version:1`, `defaults:1`,
logical `asset`, nullable `flip` (None=0, X=1, Y=2, XY=3) and sparse `overrides`.
`flip:null` inherits the current None baseline; explicit None remains distinct in saved intent.
Ordinary overrides store nullable `visible`: false is explicit off, null/absent is unowned.
Radio overrides store positive `radio` direction bits and a logical target; None/X/Y/XY are
independent bits in `PsdDirectionScope`. Reselecting a current default still records ownership.
Selecting a common expression B owns its supported scopes; subsequently selecting X-only A
retains B's Y mask. No render visibility or generation-local NodeId is saved.

An override includes `target` and `proof` alias references, physical scopes and radio parent
references. `canonical` records actual canonical-origin edit provenance so baseline P2 alias
choices survive untouched. Reconstruction starts from the current source baseline once and
applies final assignments; it neither replays setters nor normalizes None again. A partial
authored radio operation with insufficient provenance to retain baseline aliases is rejected
instead of silently owning unrelated directions. All physical radio siblings are checked across
all four flips, including hidden ancestors and zero-scope aliases.

`SetVisible`, `WithFlip` and `Inherit` return an atomic resolution. Failed edits retain the old
settings and provide no replacement state. Unknown root/operation/reference fields survive edits;
unknown versions/default rules remain opaque and cannot be evaluated or edited. Duplicate JSON
property names and syntax-invalid JSON fail parsing; they are not converted to defaults.

`CompiledItemParameter.Appearance` uses the existing base `Set` once, increments publication
revision once, and omits null on JSON save. Source-only old projects remain source-only/default.
Native Newtonsoft storage is the envelope's **JSON text string**, rather than a mutable object:
this preserves even unknown dates and arbitrary numeric precision exactly, independently of
the host reader's date/float parsing. The pure codec's standalone file remains a JSON object.
Unexpected native storage shapes fail rather than being coerced into defaults. This new storage
shape has never been published and is not a migration of a prior released appearance schema.
Equivalent-refresh clone shares the immutable value and copies revision without setters; edits
replace the value, leaving the original intact. Native application clone is a separate contract.

## Reference recovery

Worker-side `PsdLayerReferenceIndex.Read` reads verified four-byte `lyid` extra metadata blocks
from the compiled document, not source files or raster data. Adobe documents this PSD/PSB tag as
[Layer ID, four-byte integer](https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/).
Exact one valid tag and document-unique ID are required. A stored reference records source SHA,
optional unsigned layer ID, original name/kind path, duplicate occurrence and sibling count.

For identical source bytes, the original path/count/occurrence is safe across cache rebuilds;
an available ID must also agree. Across changed bytes, only a unique ID of the same kind may
resolve, inside the same logical asset identity. Alias membership, physical direction scopes and
radio parent domains must still match. Missing/duplicate IDs and name-only matches retain the
entire settings and report candidates. A unique ID is not proof of unrelated-file lineage: asset
identity remains the boundary; no global ID inference is made.

## Preparation and evidence limits

`PrepareSavedAppearance` resolves the entire envelope before compiled pixel block acquisition.
Failure raises `PsdAppearanceRepairException` containing the retained resolution, and gives a
repair diagnostic rather than RGB8 conversion advice. Successful preparation receives the
resolved visibility/flip state. Source requests capture immutable settings, update publication
revision on edits, reject changes during output and disable default-ready prefetch promotion
for authored appearances. Existing guarded retry carries the same settings snapshot.

Pure CPU tests cover B priority, X=A/Y=B, hidden choices, all flips, P2 raw alias preservation,
same-default ownership, changed defaults, unknown data, atomic partial reference failures,
synthetic PSD/PSB ID decoding, entirely empty-cache reconstruction and pre-pixel repair failures.
The managed exact-host parameter test covers JSON, notifications, ABA revision, refresh owner
guards and clone independence. Build uses isolated YMM4 4.56.1.0 references with task-local
offline restore configuration. These prove no native Undo/Redo, timeline copy/split, project
reopen, GUI output or graphics rendering behavior. H-A2 public job-to-Source cancellation stays
OPEN, with no invasive hooks or global token guesses.

The earlier [visibility checkpoint](VISIBILITY_CHECKPOINT.md) remains an internal complete-state
experiment; its opt-in hierarchy recovery is not used by this sparse product schema.
