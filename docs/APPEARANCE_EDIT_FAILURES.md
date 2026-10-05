# Appearance edit failures and native ownership

This unpublished follow-up preserves the earlier schema, native candidate and driver
activation delta. It changes no saved appearance format or PSD selection semantics.

## Edit/record failure contract

`AppearanceEditBridge.Commit` is an edit/record boundary, not an atomic host transaction.
It returns false for an unchanged, invalid or stale edit before replacement is attempted.
It returns true only if parameter replacement and the public `Record` call both return.
True alone does not prove native history ownership or a one-action Undo boundary; the
caller must use the real manager for the owning live timeline and verify integration.

Normal return also does not certify that the final parameter still holds this edit.
A notification callback can replace it with another parameter and return without an
exception, during either replacement or Record. Two managed cases preserve this behavior:
true is returned, the caller's re-read observes the other unedited parameter, and an old-plan
retry is rejected without another setter or Record. Returning false instead would falsely
classify an already attempted edit/history operation as a pre-mutation rejection.

Before claiming retained intent or issuing a subsequent edit, callers must independently
re-read the live target, verify current ownership and compare the complete intended
appearance. The only current external caller is the native validation driver; it follows
Commit with Ready checks against the actual current parameter/output and later exact JSON
and history assertions. Its bool assertion alone is only an API-return checkpoint; its
output checks alone are not a general sparse-intent or final-ownership guarantee. There is
no product UI caller yet. Future UI integration must apply the independent checks rather
than display bool=true as proof of retained intent. No result API change or repair is added.

Once replacement is attempted, a setter or Record exception is wrapped in
`AppearanceEditCommitException`. It carries the original cause, failure stage and a
best-effort observation of the current parameter (Original, Replacement, Other or
Unavailable), plus its immutable appearance when readable. `HistoryMayHaveChanged`
is always true in this case. Original is not proof that pending history is clean.

The bridge performs no automatic rollback, repeated Record, Undo, Clear or other history
repair. Callers must retain and show the uncertain result, stop automatic retries and
re-read the actual target before further work. No public transaction receipt or atomic
abort contract has been established. Complete atomic recovery remains OPEN.

The native test driver already stops when this exception escapes. It does not continue
the edit sequence and silently treat the operation as an unapplied edit.

## Exact-host managed observation

On official YMM4 4.56.1.0 assemblies, a new isolated public `UndoRedoManager` and synthetic
`UndoRedoActionCommand` were exercised in a managed console with no YMM4 process or
Timeline. Exceptions were deliberately thrown by subscribers of Recorded,
HistoryChanged and the IsUndoable PropertyChanged notification.

In all three observed cases Record threw after history had become undoable. Removing
only the injected test subscriber and calling public UndoAsync/RedoAsync invoked each
synthetic callback exactly once and restored the expected values. Later notifications
were skipped where the earlier handler threw. Thus an exception cannot be used as a
receipt that history was never committed or that all subscribers have synchronized.

Sixteen managed regression cases (65 assertions) cover successful whole-value editing,
rejection without side effects, early/partial Record failures, setter failure before/after
mutation, owner displacement, diagnostic-read failure and all three actual public manager
notification failures. The earlier managed parameter contract retains 25 assertions.
These are not live Timeline subscription, standard main-window commands, native save,
graphics or physical GUI acceptance tests.

## Ownership investigation

The earlier manual startup runs obtained real TimelineToolInfo and opened a synthetic
project, but `info.Timeline.Items.Contains(liveItem)` was false, including after 30 seconds.
No edit/Record operation followed that failed ownership guard. Those logs did not record
the Info reception count, provided Timeline ID or applied ProjectFilePath, so they cannot
identify a stale Info versus another timeline or an item identity mapping problem.

Relevant bounded references:

- [Lab PR 133](https://github.com/ziro-lab/chat-native-work-lab-001/pull/133), commit
  `92115963ff869de09bf936b4b99f5bb7ebb8ee97`: public initial Tool/Info/manager delivery;
  it does not prove rebinding after this product fixture's OpenProject operation.
- [Lab PR 138](https://github.com/ziro-lab/chat-native-work-lab-001/pull/138), commit
  `180cc800b4a92ebbaae5e71aee71cca0dd53e6d8`: public supplied Timeline mutation and
  Recorded/Undoed/Redoed lifecycle signals; separate from project-switch ownership.
- [Lab PR 87](https://github.com/ziro-lab/chat-native-work-lab-001/pull/87), commit
  `0600506b55b379880e4f30c5ef3c958bf6869491`: public ProjectFilePath switch signal and
  stable Timeline ID through its tested reopen. It concerns ToolState restoration;
  it does not promise a second LoadState or this TimelineToolInfo rebinding.
- [Lab PR 130](https://github.com/ziro-lab/chat-native-work-lab-001/pull/130), commit
  `94a3f4ed7866cf0b2acf5e93a07941fe0942c4cc`: successful native Record and standard
  Undo/Redo route; Record exception atomicity is outside its PASS.

Public metadata on 4.56.1.0 shows that TimelineToolInfo's fields are init-only record
properties and it has no update events. ITimelineToolViewModel exposes
SetTimelineToolInfo. The public MainViewModel.OpenProject(string) returns void and has
no AsyncStateMachineAttribute; neither fact supplies a completion receipt for every
downstream notification. The public TimelineViewModel exposes wrapper Items whose
Item property is IItem; it exposes no public Timeline property in the inspected build.
No private Timeline field or MainModel was read to fill that gap.

The prepared test-only diagnostic driver requires the expected project path and unique
synthetic marker/source before selecting a fixture. It records Info receipts, provided
Timeline IDs and item counts, relevant public MainViewModel notifications, and reference
membership at fixed pre/post OpenProject and Tool-activation stages. It avoids arbitrary
menu getters and ViewModel materialization. These additions are compiled only, not native
evidence. The saved earlier six-line driver delta remains a separate checkpoint.

Next native work needs a coordinated human startup close and one bounded diagnostic run;
do not repeatedly launch the GUI, synthesize a TimelineToolInfo or relax ownership to
name/ID equivalence. Native one-action Undo, actual save/reopen, copy/split and H-A2 remain
OPEN. Any reusable host result requires separate Lab evidence before wider adoption.
