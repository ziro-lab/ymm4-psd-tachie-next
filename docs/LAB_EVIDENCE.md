# Lab evidence and feedback queue

Inventory checked 2026-10-03: canonical [Lab main](https://github.com/ziro-lab/chat-native-work-lab-001/tree/f966709501aa19300ecbb3bd1a7615311c39cf20), plus PR and branch collections. Main alone did not contain all relevant Tachie/PSD work. Read fixed refs/results below before reuse; pending branches are not merged authority.

## Relevant public findings

| Question | Fixed public reference | Observed scope and limits |
| --- | --- | --- |
| Same-frame repaint after asynchronous readiness | [PR 155](https://github.com/ziro-lab/chat-native-work-lab-001/pull/155), [source 4d6a92a](https://github.com/ziro-lab/chat-native-work-lab-001/tree/4d6a92a1ad3c7e9b9845317057d2b3a064f2217f/experiments/ymm4/tachie-paused-completion), [native run 36942563425](https://github.com/ziro-lab/chat-native-work-lab-001/actions/runs/36942563425) | Synthetic real paused preview red to green at frame 0. Equivalent ready parameter replacement retained live item/Source ID 2 and source-creation count 2. Measured Timeline serialization, selected-item array, input bytes, parameter JSON and Undo state unchanged. Dedicated live dirty flag, audio, focus/autosave races, old-parameter consumers and arbitrary PSD graphs NOT PROVEN |
| Alternative repaint routes | Same PR/run | Whole-item replacement repainted but reconstructed Source (2 to 3). Temporary add/remove repainted but created/disposed an extra Source. These observations do not prove transient timeline mutation is universally harmless |
| Public refresh/change surface | [source 6d5c05d](https://github.com/ziro-lab/chat-native-work-lab-001/tree/6d5c05d25aebc1c50746f043ff4f4f1ba3346179/experiments/ymm4/tachie-refresh-surface) | Static public/change-notification inventory on 4.56.1.0; candidate locations, not paused repaint or native Undo proof |
| PSD notation/compiled runtime candidates | [source 6ad9ee5](https://github.com/ziro-lab/chat-native-work-lab-001/tree/6ad9ee525b6f82189e4c5a58a5ac429cfba84f9e/experiments/ymm4/psd-notation-surface) | Static notation/string/surface inventory on 4.56.1.0. A literal/IL/member discovery is not runtime notation semantics; no directly found flip route does not prove absence. Related compiled-runtime/editing-footprint experiments need their own result review |
| Official FFmpeg resource location | [main experiment](https://github.com/ziro-lab/chat-native-work-lab-001/tree/f966709501aa19300ecbb3bd1a7615311c39cf20/experiments/ymm4/ffmpeg-bundle-surface) | 4.56.1.0 public locator resolves official bundle in-host; standalone console resolves its own app directory. Verified FFmpeg/ffprobe bundle existence is not normal writer, codec, output success or Cancel proof |
| Earlier repaint candidates | [PR 152](https://github.com/ziro-lab/chat-native-work-lab-001/pull/152), [PR 153](https://github.com/ziro-lab/chat-native-work-lab-001/pull/153), [PR 154](https://github.com/ziro-lab/chat-native-work-lab-001/pull/154) | Search inventory of same-frame/two-stage routes, not used here as behavioral evidence without their exact result conditions |

PR 155 evidence identity: source `4d6a92a1ad3c7e9b9845317057d2b3a064f2217f`, run `36942563425`, artifact `11200349347`, digest `sha256:cb3cb9ae9b33d3a62d9d08babe06204f3f27a533198e90e7499e7187ba9577b2`. Three repaint phases were observed; “lightest observed route” is a matrix conclusion, not proof of a dedicated public transient-refresh API. Runtime-version confirmation must accompany any new reproduction.

Official host identity for the recorded 4.56.1.0 Lite evidence: ZIP SHA256 `49c0ed689f545737b7ce939971bfc625962e00791c57883dc8e6f058aa336c5a`; product validation executable SHA256 `3a2beb9890f0c3c88a4b0d17c2d8a6a5f1f974ce1d411a58e2769f9c8b473e9a`. Confirm latest stable anew rather than treating these hashes as a permanent compatibility target.

## Missing reusable knowledge and proposed feedback

No reviewed public evidence above closes H-A2. Unpublished downstream attempts are described as historical local observations in [Acceptance](ACCEPTANCE.md); they are not public Lab PASS. This documentation task proposes the following feedback without modifying or publishing Lab:

1. **Normal writer/Source cancellation contract inventory.** Contrast Exporting usage with lack of Source job ID/token and public progress cancellation members. Prove or explicitly fail to prove correlation, host disposal while waiting and bounded termination. Do not import a resource-download token or inject a global token into the Source.
2. **One synthetic normal-output boundary probe.** Stabilize native Save/path/range first. Four controlled cases: cold wait with all-frame decode oracle, preparation failure with native error/job terminal state, native Cancel during actual wait, and source-reference invalidation mid-output. Record Source callbacks/disposal/waiters, compiler counts, host token and final job/file status on every exit. Cleanup release is a separate event, never cancellation evidence. Earlier assertion failure and pre-Source Save timeout remain FAIL/BLOCKED evidence until diagnosed.
3. **Feedback on PR 155's product application.** Record equivalent-parameter ownership lifetime, two-owner paused/save-reopen observations and any future native dirty/Undo/autosave tests. Product-specific prefetch priority/expiry and GPU/cache behavior stay in product tests; only reusable host claims return to Lab. Do not broaden the original synthetic PASS to all product settings or duplicate the refresh matrix unnecessarily.

A future Lab Draft PR should state the narrow question, exact latest-stable version/hash, fixed source/checkout/run/artifact identity, synthetic conditions, assertions, PASS/FAIL/BLOCKED and NOT PROVEN. Product docs then cite the fixed result and retain independent downstream regression. See [workflow](DEVELOPMENT_WORKFLOW.md) for the complete return record.

## Open interpretation boundaries

Static notation findings do not establish required `*`/`!`/flip/hierarchy semantics. Public callbacks do not yet prove sufficient ended-contribution input for deterministic Extend. EX1 remains unadopted. A progress cancellation member is not an exporting Source token bridge. A paused repaint observation is not an audio/dirty/focus guarantee. Keep these boundaries open until a narrow native experiment or a deliberate product semantics decision resolves them.
