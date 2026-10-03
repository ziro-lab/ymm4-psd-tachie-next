# Product requirements

Authority date: 2026-10-03. These are completion requirements, not a list of shipped features. Current implementation and verification are recorded separately in [Acceptance](ACCEPTANCE.md).

## Goal and completion boundary

Deliver the useful functional scope of an extended PSD standing-picture workflow in normal YMM4 editing: select original PSD/PSB, automatic preparation, base appearance, PSDTool notation, presets and sparse expression stacking, Override, Extend, eye blink/mouth animation, palettes, native Undo and save/reopen, source changes and normal video output. Preserve original dimensions, precision and visible quality. The priority is low editing burden and correct tracking over time; beating another plugin's load speed is not a requirement.

Original PSD/PSB and logical project settings remain the source of truth. No destructive preprocessing, lossy image simplification, silent layer omission, external service, terminal or manual compiled conversion is required of the finished user's workflow. Cache deletion must not destroy expression settings. Template Placer is not a runtime dependency.

Functional parity is a requirements/acceptance inventory, not permission to copy an old plugin or a promise of unchanged old project serialization. Automatic migration of old-plugin project data, PFV interchange and AviUtl integration are not adopted scopes; decide them explicitly if evidence and need justify them. Unknown old behavior is not an oracle. Verify required user-visible results using legitimate references and permitted material.

## Requirements and acceptance examples

| ID | Required behavior | Completion evidence | Phase |
| --- | --- | --- | --- |
| T01 | Open a detailed PSD palette from the selected standing-picture/expression item, with an unambiguous editing target | Click → selected target → preview; changing selection cannot write the previous target | G |
| T02 | Sparse partial expressions coexist by target; unspecified parts inherit, explicit hide and out-of-scope are distinct | Eye-only plus mouth-only changes preserve clothes/body; normal overlapping layers may coexist | C/D |
| T03 | Override filters the relevant lower-priority expression contributions, preserving the standing-picture base | Higher timeline-layer conflict wins only its target; a control-only empty patch still acts; host order/ties are verified | D |
| T04 | Extend preserves logical contributions after an item ends without relying on prior playback | Forward/direct/reverse seek, cache deletion, Undo and reopen at the same time give identical results; exact reset/temporary-override semantics are decided first | E/H |
| T05 | Character preset definition and item-local extra patch are independent | Applying or removing a local hand patch does not mutate the shared face preset; rename/delete/missing/ambiguous preset references are explicit | D/G |
| T06 | Save logical base, expression, preset, eye/mouth and per-item settings losslessly | Reopen without cache reconstructs the same state; unsupported schema is not rewritten as empty | A/C/D/F/G/H |
| T07 | Eye blink, mouth animation and stop/default-return/speaking-only settings follow host time and voice input | Controlled voice/time inputs agree during playback, scrub and output; stopped mouth is distinguished from forced closed mouth | F/H |
| T08 | PSDTool-style `*`, `!`, `:flipx/:flipy/:flipxy`, hierarchy, duplicate names and required escaping | Exclusive and ordinary siblings are distinct; force-visible/hidden ancestor/flip pairs agree with the stated reference | C/B |
| T09 | Settings and simple palettes configure eye/mouth slots, preset definitions and selected quick parts | Register/edit/order/delete operations restore on reopen; preset/filter application changes only its defined targets | G |
| T10 | Native Undo/Redo covers user-visible operations with stable boundaries | One exclusive switch is one Undo; preset working copy can confirm/cancel; preparation completion creates no user edit history | A/D/G/H |
| T11 | Palette ownership, separate windows, foreground mode, DPI, tree scale and appearance preferences work safely | Selection/window/DataContext changes reject late work; closing the palette stops its rendering/watch/borrowed resources | G/H |
| T12 | Missing, moved, edited or replaced PSD is recoverable without changing saved intent silently | Explicit relink preserves logical asset identity; ambiguous layers require repair; stale candidates cannot publish | A/C/H |
| Q01 | Required PSD/PSB input and draw compatibility, original dimensions and quality | Independent source/pixel/render/final-preview comparisons; RGB8 success does not waive other required profiles or effects | B/I |
| Q02 | Finite CPU/GPU/disk resources and inexpensive unchanged-state updates | No recurring PSD parse/compose on the same effective appearance; measured repeated edit/close/reopen/device replacement returns resources | A/F/G/H/I |
| Q03 | Exact normal video output | Cold cache waits or visibly aborts; every required frame is correct; failure/cancel/source change cannot yield false success or mixed generations | A/H/I |
| Q04 | Ordinary installation and final workflow | Verified distribution DLL/package in a clean isolated host, first PSD → edit → Undo → save/reopen → output; permitted local real-material acceptance | I |

The PSDTool target describes source notation, not YMM4's generic sibling-switch command. PSDTool preset filters and timeline expression precedence are separate concepts. A larger timeline layer number is the planned conflict priority for expressions; same-layer/same-start/voice ties and host-resolved ordering must be established before completion.

## Appearance and animation rules

- Store sparse logical operations, not a full visible-state screenshot. Inherited preview parts must not become item-owned selections on save.
- Preserve original layer names separately from display labels. Do not resolve renamed or duplicate layers by a nearby name or display order.
- Keep base settings, immutable preset definition and item additions separate; evaluate restrictions and animation after logical contributions.
- Eye blink is derived from time/seed/settings, not redraw count or advancing a cache-dependent random generator. Mouth state consumes the host's supported voice input; no extra speech recognizer is required.
- Explicit stop/flip values and unspecified/inherited values need distinct internal meaning where required; a universal three-state UI is not prescribed.
- Extend stores no last rendered bitmap, mouth sample or GPU object. An active temporary override, inheritance reset and carry-after-end must not be collapsed into one flag.

## Palette and resource requirements

Detailed/simple/settings palettes share the compiled document, never reparse a second PSD for thumbnails. Virtualize long lists and generate thumbnails only for visible/near-visible rows. Lower thumbnail resolution is a UI optimization and must never replace full-quality product pixels. Actual rendering takes priority over speculation/thumbnails. Do not pre-render every preset or retain all layers on GPU permanently.

Logical budgets are not measured total process RAM/VRAM. Record parser/snapshot/encoded/decode/copy/GPU and process peaks separately where applicable. The current 16 MiB/30-second selected-source prefetch limit is provisional, not a bound on the entire process or proof of adequacy for typical PSDs.

## Unfinished semantics

Extend EX1 is unadopted. It proposes distinct carry-after-end, inherit-previous reset and stack-lower/Override controls with deterministic half-open frame intervals and scene/character/logical-asset/standing-picture-continuity scope. Temporary override ending, reset persistence, start-vs-end priority and hidden-vs-absent standing pictures still require an explicit compatibility decision and host input evidence. Keep this feature in the completion plan; do not implement a playback-history workaround or announce these proposal rules as observed legacy behavior.
