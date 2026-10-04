# PSD palette checkpoint: source-only Draft PR candidate

Status: 2026-10-04. Base: `808a5fb5e22c218079ca3c0ef5c08525d51b682f`.
The runtime implementation is the fixed 123-file implementation snapshot. Publication preparation
adds documentation and standalone first-party proof sources; it changes no runtime behavior.
This candidate is for review. It is not a packaged plugin release or a full PSD compatibility claim.

## Changes and supporting evidence

| Change | Supporting check | Boundary |
| --- | --- | --- |
| Lossless layer references, prefix/radio/direction choices, sparse envelopes and appearance stack | CPU 162 cases / 1493 assertions; parameter 25; face 20; current-direction projection 10 | Synthetic RGB8 fixtures; full RGB/PSD/PSB requirements remain retained |
| Renderer selection, reusable output and preparation/publication guards | WARP 51 cases / 509 assertions, tolerance 0; CPU runtime/preparation cases | Software WARP; no physical GPU speed or screenshot correspondence claim |
| Standing/face palette, independent ownership, Inherit, native edit boundaries | Actual native 83 assertions: original hair Undo branch, eye/mouth editing, native SaveProject/OpenProject, re-edit and one Undo per contributor | Same fixed host and synthetic scene; not full frame-varying expression output |
| CPU-ready refresh without equivalent live parameter replacement | SDK controls and native pending-edit condition | Empty SDK notification preserves the tested public notification count/flags; no arbitrary history-depth or dirty-state guarantee |
| Failure contracts and explicit reconfirmation latch | Managed 16 cases / 65 assertions; independent code review | Native Record-throw and post-commit Capture-failure latch probes remain open |
| View subscription lifetime | Local non-visible WPF component: 11 assertions, actual Loaded/Unloaded and DataContext A/B swaps, Dispose | No live TimelineToolInfo or acquired document/asset/watch lease; realized host View/lease lifetime remains open |
| Face owner/epoch and late-publication guard | Code review and current-owner native edit/reopen observations | Explicit stale old-face-clone publication rejection remains open on a live host |

The [sanitized synthetic summary](PALETTE_SYNTHETIC_EVIDENCE.json) records the exact host,
implementation audit and loaded assembly identities. These are local observations; no new public
Actions run or downloadable native artifact is invented. Earlier 49/44-assert native results belong
to their older implementation and are not added to the latest 83 count.

Native reopen replaces the host-created product ViewModel. The previous driver cached the retired
instance. Reading the current public ToolArea ViewModel resolved the test failure without product
changes, fabricated TimelineToolInfo or force-Resume. The earlier OpenProject timeout is preserved;
its exact cause was not established. Two new runs completed the requested reopen/edit/Undo sequence.

## Pending edit observation

With an unrelated live edit still unrecorded, the product's empty preparation notice left public
`IsSaved=true`, `IsUndoable=false`, `IsRedoable=true`, and `HistoryChanged` notification count
unchanged. The pending edit survived; recording that user's edit once and issuing one standard Undo
restored its exact value. SDK with/without-notice controls also restored the same edit with Undo/Redo.
`IsSaved` was already true while that edit was pending. This is the tested public-property observation,
not a guarantee about every private dirty flag, autosave race or history depth.

## Standalone first-party proof sources

- [Native product/reopen/pending proof](proofs/PaletteNativeProof.cs): the executed driver source;
  compile in a diagnostic-only `PsdTachieNext.HostProof` assembly with the product and synthetic
  `PsdFixture`, and wire its `Schedule()` at the diagnostic startup. Native output is controlled by
  `PSD_NEXT_PALETTE_NATIVE_OUTPUT`. It does not automatically run from the product/CI project.
- [SDK control](proofs/SdkPendingProof.cs) and [WPF component check](proofs/WpfLifetimeProof.cs):
  standalone .NET 10 Windows/WPF executables referencing the product and external host assemblies.
  Use assembly name `PsdTachieNext.HostProof` for the component's internal proof accessor. Set
  `YMM4_COMPONENT_HOST` to the authorized validation host directory and pass a result JSON path.
  Only the local assembly locator was generalized for publication; assertions are unchanged.

The standalone proof files are intentionally outside normal project compilation. Build artifacts,
host DLLs, dependency trees and executed native project/material files are not included.

## Later hands-on check

Use the shared validation host and the existing synthetic eye/mouth scene after the candidate's
temporary test placement is authorized. Keep ordinary YMM4 and personal PSD files separate.

1. Open the PSD palette from the real tool menu. Select the standing item, then the eye and mouth
   items. Confirm the heading, readable labels and each item's ownership indicators.
2. Toggle eye and mouth separately. Confirm the preview changes only that part and Undo restores it;
   clothes/hair and the other contributor stay unchanged. Check Inherit and orientation labels.
3. Save, reopen, select eye and mouth again, repeat one toggle and one Undo. Confirm the palette
   follows the new items instead of a retired context.
4. Select two items and confirm editing is disabled. Close/reopen the pane and check the current
   target returns. Record visual issues locally; do not upload personal PSD/project/screenshot data.

## Remaining decisions

The user decides whether this source-only scope should update the existing Draft PR or become a
separate Draft PR, and when to perform the hands-on check. No canonical apply/commit/push is included.
Visual acceptance, realized View/leased-resource lifetime, native failure-latch probes and explicit
stale-clone rejection remain open. H-A2, native Cancel, full frame-varying output and full compatibility
remain outside this checkpoint's acceptance; their requirements are not removed or silently reduced.
