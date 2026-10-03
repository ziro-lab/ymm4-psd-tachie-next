# Development authority

This is an unfinished, source-only YMM4 PSD/PSB standing-picture project. No general-use distribution has started.

Read in order:

1. [Product requirements](docs/PRODUCT_REQUIREMENTS.md)
2. [Current architecture and decisions](docs/ARCHITECTURE.md)
3. [Development plan](docs/DEVELOPMENT_PLAN.md)
4. [Acceptance and current evidence](docs/ACCEPTANCE.md)
5. [Development workflow](docs/DEVELOPMENT_WORKFLOW.md)
6. [Lab evidence and feedback queue](docs/LAB_EVIDENCE.md)

Use the current code and versioned evidence to update status. A historical plan is rationale, not proof that a feature is implemented. Keep IMPLEMENTED, VERIFIED, PROPOSED, OPEN and BLOCKED distinct. Do not silently shrink the full requirements to the current RGB8 fixtures.

## Host research and Lab feedback

Before investigating an uncertain YMM4 behavior, search the canonical [public Lab](https://github.com/ziro-lab/chat-native-work-lab-001), its observation/reference/surface guides, existing experiments, **open and closed PRs and their branches/commits**, plus relevant issues. Paginate results; main alone is insufficient. Read PASS/NOT PROVEN, host version, source/checkout SHA and run evidence before using a result. A lookup failure is not proof that a capability is absent.

Prefer public Plugin API, then public host/WPF surface, then a bounded adapter justified by evidence. Reflection or Harmony requires a concrete gap; do not broaden discovery merely because a member name looks useful. External documentation and samples are references, not native evidence or permission to copy code.

If a reusable host fact is missing, propose one narrow synthetic Lab experiment. Return findings there with question, exact official host version/hash, conditions, source/checkout/run/artifact identity, assertions, success/failure/blocked outcome and NOT PROVEN. Product changes cite that fixed Lab evidence and add product regression coverage. Product PASS does not automatically widen Lab PASS. See [the workflow](docs/DEVELOPMENT_WORKFLOW.md) for the feedback record.

## Implementation boundaries

- Original PSD/PSB and saved logical project settings are authoritative. Cache is disposable and lossless, not an anonymized asset.
- Share immutable documents/blocks; keep character, expression, item, Undo and owner state separate.
- Keep preparation off the render callback; compiler concurrency is initially one. Separate waiter cancellation from shared-worker cancellation.
- Publish only current request/settings/source revision/device epoch after GPU success. Strict output cannot silently use a stale or transparent frame.
- No disk/decode fallback during rendering; no I/O under a shared publication lock. Keep finite budgets and release leases at owner disposal.
- Prefer native Undo and save/material APIs; no private expression database or custom Undo stack.
- Preserve full PSDTool, expression, preset, palette, eye/mouth and persistence requirements. Extend EX1 remains a proposal until explicitly adopted and host input is proved.

## Change and validation discipline

Work from current main through a small branch and Draft PR. Check remote/local work before editing; preserve others' changes. No force-push, merge, tags, topics, release or package publication merely because CI is green. Report rejected operations and the last successful checkpoint.

Use the official latest **stable version confirmed at test time**, record exact version/hash and repin/revalidate when it changes. Never apply another plugin's older host pin without checking. Use only isolated hosts and synthetic copies; preserve normal YMM4 settings and original materials.

Documentation-only edits receive document/link/publication checks, not a native host run. Source/test/workflow changes use the required Windows CI layer; do not claim the current build/WARP lane executes the actual YMM4 timeline or normal writer. Keep development feedback, checkpoint acceptance, real-host evidence and human acceptance separate. Current H-A2 is OPEN.

Publish only reviewed source, general technical docs and synthetic test evidence. Exclude private links/paths, credentials, user PSD/PSB and derived cache/manifest/pixels/project/screenshot/log data, host binaries and old-plugin/decompiled code. Check licenses before adopting external code. The allowlist in `.gitignore` is deliberate; add individual documentation files rather than permitting all local notes.
