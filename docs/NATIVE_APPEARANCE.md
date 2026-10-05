# Native appearance transaction validation

This is a separate, unpublished working copy of the [sparse schema](SPARSE_APPEARANCE.md).
The approved schema candidate is preserved. The native controller below is experimental;
its compilation is not evidence that one action produces one native Undo unit.

## Implementation

`AppearanceEditBridge.Commit` runs on the UI dispatcher, verifies the current live item
parameter identity and revision, and accepts only a successful immutable codec edit.
It replaces the item parameter with a revised equivalent clone and calls the actual
`UndoRedoManager.Record`. It does not replay raw checkboxes or keep a custom history stack.
The manager must come from the host's public `TimelineToolInfo` for the owning timeline.
The caller is responsible for selecting that timeline and the appropriate edit target.

The dedicated HostProof tool implements `ITimelineToolViewModel` alone and receives
`SetTimelineToolInfo` from the host. A bounded test adapter reads the public tool menu
and opens only this test tool through a public command or the exact observed public
`ToolAreaViewModel.IsVisible` setter. It never acquires the private main model or history.
This adapter is test-only, not a product acquisition API.

The synthetic driver checks ordinary edits, standard Undo/Redo commands, edits while
flipped, common B priority followed by X=A/Y=B memory, live native save/reopen, changed
source defaults, and atomic repair on missing stable references. It records real Source
sessions, request stamps, generations, compositions, output pointers, saved JSON and
lifecycle events. None of these assertions has completed in the current native run.

## Evidence

- CPU: 158 cases / 1473 assertions passed in `sparse-native-cpu-01`.
- Exact-host managed parameter contract: 1 case / 25 assertions passed.
- WARP: 51 cases / 509 assertions passed in `sparse-native-warp-01`, using the same
  documented initializer-only test adaptation and exact host-bound graphics references.
  This is graphics test evidence, not native timeline or physical GUI acceptance.
- Product and HostProof build: compiler warnings/errors zero; SDK 10.0.401.
- Official stable host checked: YMM4 Lite 4.56.1.0, executable SHA256
  `3a2beb9890f0c3c88a4b0d17c2d8a6a5f1f974ce1d411a58e2769f9c8b473e9a`.
- Native runs: failed or blocked before transaction assertions, zero standard history
  commands. A fresh isolated host opened one synthetic live item and rendered its
  baseline, but did not deliver the test tool's `TimelineToolInfo` before timeout.
  The About window was observed. Reused dedicated hosts also blocked inside OpenProject.
  Their exact blank-dialog cause is not proven. Earlier result fields deriving
  `actualLiveProject` from completed snapshots must not override the live-item stage log.
- Run 06 is invalid staging, not host evidence: a wildcard selected dependency DLLs
  instead of the exact product/proof assemblies. Fixed staging requires five unique
  named assemblies and matching SHA256. Run 05 retained the preceding proof assembly
  and did not execute its newly built diagnostic code. Those failures are preserved.

## Lab basis and blocker

Lab [PR 133](https://github.com/ziro-lab/chat-native-work-lab-001/pull/133), commit
`92115963ff869de09bf936b4b99f5bb7ebb8ee97`, proves the public timeline-tool manager route.
Its native launcher closes About/update windows before validating the tool. The current
isolated run did not do so. Startup completion is therefore a likely prerequisite,
not an established causal diagnosis for every local failure.

The selected computer-use skill requires its designated runtime for physical input;
that runtime is not exposed in this session. No alternative Win32/UIA/input automation
or automatic modal acceptance was used. Resume with that supported input runtime,
finish the dedicated host's startup, then execute the prepared synthetic driver.
The final driver waits read-only for the observed About window to disappear and for
its own tool to be registered before opening the synthetic project. This gate is
compiled but has not been executed; it does not dismiss or accept any window.
No new permission rejection or manual approval is pending.

Lab [PR 155](https://github.com/ziro-lab/chat-native-work-lab-001/pull/155), commit
`4d6a92a1ad3c7e9b9845317057d2b3a064f2217f`, supports paused equivalent parameter
replacement. Its unchanged observed history state does not prove that pending refresh
changes cannot affect the next native Undo unit. The one-action tests remain required.
Lab [PR 130](https://github.com/ziro-lab/chat-native-work-lab-001/pull/130), commit
`94a3f4ed7866cf0b2acf5e93a07941fe0942c4cc`, supports standard configured Undo/Redo
commands on the actual main Window. It does not establish this product transaction.

Native save/reopen, one-action Undo/Redo, copy/split, edits during blocked preparation,
repair UI, physical preview/output acceptance and H-A2 remain OPEN. The first empty
Y selection for direction-only parts remains undecided. No normal host, original PSD,
protected candidate, repository commit, publication or permanent settings were changed.
