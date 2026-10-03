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

Canonical shared host evidence is [chat-native-work-lab-001](https://github.com/ziro-lab/chat-native-work-lab-001). Use its maintained policies rather than duplicate them here. The short entry loop is:

1. **Search.** Read [reference sources/search order](https://github.com/ziro-lab/chat-native-work-lab-001/blob/main/docs/YMM4_REFERENCE_SOURCES.md), then existing main experiments, open/closed PRs, issues and branch/commit files; paginate and record fixed refs/date. Main-only or failed lookup cannot establish absence. Official samples/API indexes are discovery references, not runtime evidence.
2. **Isolate the unresolved point.** State one version-sensitive host behavior and the product decision it blocks. Prefer public Plugin API, then public host/WPF; bounded reflection/Harmony needs a demonstrated gap. Propose one small synthetic experiment rather than duplicate product machinery or rediscover documented symbols.
3. **Record evidence.** Follow [observation policy](https://github.com/ziro-lab/chat-native-work-lab-001/blob/main/docs/YMM4_OBSERVATION_POLICY.md): exact host/version/hash, conditions, evidence type, source versus actual checkout, run/artifact identity, assertions, outcome and NOT PROVEN. Preserve failure-stage diagnostics; cleanup is separate from native cancellation. Static inventory and a PR's existence are not native PASS.
4. **Confirm product integration.** Link fixed Lab evidence, implement the smallest justified behavior and independently re-test product lifetime/Undo/save/output where relevant. Record functional, UI/UX, package and human acceptance separately. A bounded host PASS does not imply full product PASS.
5. **Return downstream feedback.** Use [downstream feedback](https://github.com/ziro-lab/chat-native-work-lab-001/blob/main/docs/DOWNSTREAM_FEEDBACK.md): adopted/rejected/superseded, product decision, integration result and any contradiction. Keep original Lab PASS limits intact. A newly proved reusable fact extends a narrow Lab experiment; product-specific observations remain here with their reference.

These canonical documents govern the detailed record fields and evidence distinctions. If results conflict, retain both versions/conditions and reopen the boundary. [The feedback queue](LAB_EVIDENCE.md) identifies this project's current missing questions; publishing those proposals is a separate authorized task.

## Alignment with other plugin development

The comparison uses public/currently relevant documentation rather than copying another plugin's host pin or internal operating details.

| Reference | Adopted discipline here | Product-specific distinction |
| --- | --- | --- |
| [Template Placer research registry](https://github.com/ziro-lab/ymm4-template-placer/blob/main/docs/RESEARCH.md) and development docs | Reference/Lab/product evidence separation, requirements/design/plan/current-state authority, main-based Draft PR, cheap feedback then checkpoint/ready/release, native Undo/fidelity and real-host acceptance | PSD preparation, GPU, notation and strict output need their own acceptance; another host pin or release workflow is not inherited |
| [Voice Quality Assist AGENTS](https://github.com/ziro-lab/ymm4-voice-quality-assist/blob/f57a5358d11cd9778f9a6e4703f3e5b9645bce96/AGENTS.md), [PR 16](https://github.com/ziro-lab/ymm4-voice-quality-assist/pull/16) | Lab-first gates, PROVEN/CANDIDATE/BLOCKED, reference/native separation, fixed Lab links and scoped public-surface adapters | Exporting Source cancellation and paused equivalent-parameter route require PSD-specific regression |
| [Public Lab](https://github.com/ziro-lab/chat-native-work-lab-001) | Canonical reusable experiments, exact host/source/run provenance, synthetic inputs and NOT PROVEN | Product owns full functional/persistence/resource/user-workflow completion; Lab's bounded host PASS cannot substitute |

## Environment and publication

Confirm official latest **stable** at the time of each new test. Record exact version/hash and repin/revalidate source/build/probes when it changes. An older stable used by another plugin is historical evidence, not this project's policy. The current CI pin checks the official latest release and fails for review if it changes.

Use independent validation hosts and input copies. Preserve normal host settings/plugins/projects and original source materials. Stop for genuinely new installation/security/authentication requirements outside the approved task; repeated use of an already authorized validation host should not generate extra discretionary permission requests.

Public changes contain reviewed source, general technical documentation, legitimate notices and synthetic evidence only. Audit staged diff, links and tracked-file allowlist before publishing. Exclude private URLs/paths/credentials, personal materials and derived data, host binaries and old-plugin/decompiled code. Add individual docs to `.gitignore`; do not expose all local notes. Publishing a Lab proposal is a separate task, not implied by writing a feedback queue here.
