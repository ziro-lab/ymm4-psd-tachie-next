# Development workflow

Authority date: 2026-10-03. Read [AGENTS](../AGENTS.md) and the requirements/architecture/plan/acceptance before work. This workflow governs development, not a permission to publish a release.

## Change progression

1. Check current remote main, branches/PRs and local changes. Agree on ownership and preserve concurrent work. Start a focused branch from current main; do not overwrite an existing checkout's changes.
2. State the concrete behavior, affected requirements, unresolved host assumptions and exit evidence. Investigate unknown host behavior through Lab before building a large product workaround.
3. Implement the smallest complete change and run focused pure tests frequently. Update architecture/plan/acceptance when behavior or a hypothesis changes. Do not claim implementation as verification.
4. Open a Draft PR with problem/result, exact source/evidence links, tests and NOT PROVEN. Use early review to resolve uncertain semantics. Do not bypass rejected approval or force-push to hide a failed checkpoint.
5. At a meaningful checkpoint, run the applicable Windows/native matrix on the test-time latest stable host. Record success, failure or blocked status with diagnostics. A retry fixes an identified harness/infrastructure issue and retains prior results; it does not turn an unexplained failure into PASS.
6. Mark ready only when the scoped acceptance is complete and remaining limitations explicit. Merge/release/package/tag/topic changes need their own authorization. Development CI does not install a usable distribution or establish final user acceptance.

Use three separate feedback levels: cheap focused developer regressions, phase acceptance checkpoints, and real-host/human workflow acceptance. A documentation-only update gets document/link/publication checks and does not launch YMM4. Runtime/test/build/workflow changes receive the corresponding CI layer. The development Windows workflow is synthetic/WARP plus exact-host **build**; it does not execute the host timeline or writer. Keep CI's claims consistent with [Acceptance](ACCEPTANCE.md).

The current workflow reacts to main pushes and PRs, including documentation changes. Do not invent a skipped-docs policy without changing/validating its configuration. Manual `workflow_dispatch` is available, but is not permission to spend private Actions or launch an unrelated workflow. This project is public; use focused checks and avoid repeated large host downloads without a reason.

## Lab lookup and return loop

Canonical shared host evidence is [chat-native-work-lab-001](https://github.com/ziro-lab/chat-native-work-lab-001), not a product's private notes or a remembered API assumption.

Before researching:

- Read Lab AGENTS and YMM4 observation/reference/surface guides. Search main experiments, open **and closed** PRs, relevant issues and branch/commit files; paginate collections. Record search date and fixed refs. Main alone can miss current findings; a missing file/ref or failed lookup is not proof of absence.
- Read the native conditions, PASS/NOT PROVEN, exact version, source/checkout SHA, run and artifact before adopting a result. A PR's existence or a member-name/IL inventory is not behavioral proof. A moving branch name needs a fixed commit reference.
- Prefer public Plugin API, then public host/WPF surface. A bounded adapter/reflection approach needs a demonstrated gap and lifetime/error tests; Harmony is not a default. Samples/docs are references, not observed native behavior or code-copy permission.

When evidence is missing, propose one narrow synthetic Lab experiment for the reusable host contract. Keep product preparation/cache/state machinery out of the shared probe unless needed to reproduce that contract. Search again before publishing to avoid duplicating another experiment.

Return record:

| Field | Required contents |
| --- | --- |
| Question | One uncertain public/native behavior and why the product depends on it |
| Identity | Exact official host version, archive/executable hash, SDK, source SHA, checkout SHA if different, run ID/attempt, artifact ID/digest |
| Conditions | Synthetic inputs, real host entry point, paused/playing/exporting state, ownership/lifetime, controlled gates and bounded timeout |
| Result | Assertions and observed pixels/model/source/token/disposal/terminal states; PASS, FAIL, BLOCKED or NOT PROVEN as appropriate |
| Failure | Original exception/native error/harness stage, cleanup separately from actual host cancellation; diagnostics retained on every exit |
| Limits | What was not measured; static inventory vs runtime, synthetic vs normal workflow, physical-device/audio/dirty/focus boundaries |
| Feedback | Fixed Lab PR/commit/run link; product downstream regression and any stricter/rejected interpretation |

Newly verified reusable facts belong back in Lab with their limits, including negative/blocked results. Product-specific behavior stays here with a Lab reference. Downstream PASS does not automatically widen Lab PASS. If evidence conflicts, record both versions/conditions and reopen the boundary; do not silently select the favorable result.

## Alignment with other plugin development

The comparison uses public/currently relevant documentation rather than copying another plugin's host pin or internal operating details.

| Reference | Adopted discipline here | Product-specific distinction |
| --- | --- | --- |
| [Template Placer](https://github.com/ziro-lab/ymm4-template-placer) | Requirements/design/plan/current-state authority, main-based Draft PR, cheap feedback then checkpoint/ready/release progression, native Undo/fidelity and explicit real-host acceptance | PSD preparation, GPU, notation and strict output need their own acceptance; another host version or release workflow is not inherited |
| [Voice Quality Assist PR 16](https://github.com/ziro-lab/ymm4-voice-quality-assist/pull/16), source `f57a5358d11cd9778f9a6e4703f3e5b9645bce96` | Lab-first gates, PROVEN/CANDIDATE/BLOCKED distinctions, references separate from native evidence, fixed Lab links and scoped public-surface adapters | The exporting Source cancellation gap and paused equivalent-parameter route require PSD-specific regression |
| [Public Lab](https://github.com/ziro-lab/chat-native-work-lab-001) | Canonical reusable experiments, exact host/source/run provenance, synthetic inputs and NOT PROVEN | Product owns full functional/persistence/resource/user-workflow completion; Lab's bounded host PASS cannot substitute |

## Environment and publication

Confirm official latest **stable** at the time of each new test. Record exact version/hash and repin/revalidate source/build/probes when it changes. An older stable used by another plugin is historical evidence, not this project's policy. The current CI pin checks the official latest release and fails for review if it changes.

Use independent validation hosts and input copies. Preserve normal host settings/plugins/projects and original source materials. Stop for genuinely new installation/security/authentication requirements outside the approved task; repeated use of an already authorized validation host should not generate extra discretionary permission requests.

Public changes contain reviewed source, general technical documentation, legitimate notices and synthetic evidence only. Audit staged diff, links and tracked-file allowlist before publishing. Exclude private URLs/paths/credentials, personal materials and derived data, host binaries and old-plugin/decompiled code. Add individual docs to `.gitignore`; do not expose all local notes. Publishing a Lab proposal is a separate task, not implied by writing a feedback queue here.
