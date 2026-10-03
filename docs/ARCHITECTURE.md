# Current architecture and decisions

Baseline: public source [73cc24e](https://github.com/ziro-lab/ymm4-psd-tachie-next/commit/73cc24e7eb70a319f15f8191aa31081638ccc3e9). This document distinguishes existing mechanisms from later proposals.

## Dependency direction

Original PSD/PSB → owned immutable snapshot and fingerprint → versioned compiled manifest/independent lossless blocks → shared immutable document/block leases → logical appearance/RenderPlan → prepared required blocks → host-owned GPU renderer/output. Palette, item, character and expression state are consumers, not owners of a second asset database.

| Responsibility | Current code | Boundary |
| --- | --- | --- |
| Source and snapshot | `SourceAssetRef`, `SourceSnapshot`, `PsdCompiler.CreateSnapshot` | Logical asset identity is independent of content hash/path; snapshot/hash reads bytes even when conversion is reused |
| Compiled generations | `CompiledAssetRepository`, `PsdCompiler.CompileSnapshot`, `CompiledStore` | Content/schema/compiler/pixel profile identify a generation; incomplete publication is not a valid cache hit |
| Preparation sharing | `SourcePreparationService`, `SourceSession` | Same material/content work is shared; process-wide compiler concurrency starts at one; cancel a waiter without canceling other consumers |
| CPU ownership | `SharedDocumentPool`, `BlockCache`, `PreparedAppearanceLease`, `RenderPlan` | Pin needed blocks through rendering; publish shared entries outside slow I/O/decode; no render-time disk fallback |
| Source/render owner | `CompiledTachieSource`, flat/tree renderers | Build a candidate on the host owner and keep prior completed output until success; device/lifetime checks reject late candidates |
| Paused refresh | `HostPreparationBridge`, `CompiledParameterRefreshBridge`, `HostSourceRecheck` | Completion signals Dispatcher/live-owner equivalent parameter replacement; it does not draw GPU output or invent Undo inside a CPU callback |
| Selected-source speculation | `SelectedCharacterPrefetchBinding`, `SelectedSourcePrefetch` | One latest selection; 16 MiB required decoded blocks for 30 seconds, actual-use priority, no blanket preparation of all characters |
| Strict export episode | `ExportRequestScope` | Source-local fixed request/reference/revisions; not a host job ID; normal native Cancel linkage remains unproved |
| Minimal persistence/materials | `CompiledItemParameter.Source/File`, `GetFiles/ReplaceFile/GetResources` | Original source reference is saved/enumerated; full character/expression/preset persistence is unfinished |

The adapter uses public Plugin/graphics interfaces plus a narrow public host/WPF bridge. Public view models are version-sensitive surfaces, not a timeless Plugin API guarantee. [Lab evidence](LAB_EVIDENCE.md) bounds the parameter-refresh route.

## Decisions retained

1. **Lossless source representation.** Current storage is straight BGRA8 and independent Raw/Brotli blocks, with separate mask planes. Do not revert to an older premultiplied/Deflate design solely for document consistency. Preserve raw image-resource/extra-tag information when stored; retaining it is neither proof of correct interpretation nor anonymization.
2. **RGB8 is a checkpoint.** Current compiler/renderer profile deliberately rejects unsupported input/draw cases. Necessary color management, translucent/masked pass-through, complex masks/clipping and broader blend/precision cases remain phase B. An explicit rejection is safe failure, not compatibility completion.
3. **Separate identities.** AssetIdentity is saved intent; fingerprint is byte content; generation is compiler/schema/profile result; NodeId is generation-local; request/settings/source revision and device epoch are publication validity. None is interchangeable with a native export job ID or a display name.
4. **Snapshot before content reuse.** Same-content cache reuse avoids parsing/conversion, not all source I/O. Size/mtime and watcher notifications are hints; same-size/mtime replacement must still be detected. Watcher overflow/resume/retry must reconcile current bytes safely.
5. **Two ownership levels.** Shared documents/blocks are immutable. Each Source owns its selected state and GPU resources. Pool budgets do not authorize deleting pinned disk generations or evicting borrowed blocks. CPU timers/workers never operate GPU objects.
6. **Prepared publication.** A RenderPlan names required blocks; a PreparedAppearanceLease pins them. Candidate render success plus current stamp checks precede visible replacement. Failures retain prior valid preview where appropriate; strict playback/output must fail rather than silently accepting it as the requested frame.
7. **Finite speculation.** Selected decoded-ready retention is currently 16 MiB/30 seconds. A 32 MiB candidate was discussed but is not implemented/adopted. It requires targeted retention/expiry/pressure measurements before a policy change.
8. **Native project behavior.** Use native source setters/Undo/save/material integration. Preparation/refresh must not alter persisted parameter meaning. Current source-reference roundtrip does not prove future preset/window/schema migrations.

## Planned appearance model

Phase C adds a durable PersistentLayerRef using original hierarchy, node kind, duplicate disambiguation and available PSD IDs/structural evidence. Current `LayerRef(SourceSha256, NodeId, OriginalName)` is generation-local and correctly rejects changed content; it is not yet safe persistence across arbitrary source edits.

First derive a document-generation notation index from preserved original names rather than changing compiled format unnecessarily. Phase D resolves base + immutable preset + item-local sparse patch + ordered expression contributions per target. Resolve Override before PSDTool constraints and current eye/mouth evaluation. Include all result-affecting inputs in the appearance/render identity; unchanged identity must not force recomposition.

Phase E requires timeline contribution snapshots/indexes for past-ended expressions and direct/reverse seek. EX1 is a proposal, not a shipped rule set. Phase F uses host time/voice inputs and small repeated eye/mouth block sets. Phase G's palettes share source/document preparation with separate finite thumbnail ownership. Native Undo and project settings remain the only user-facing history/authority.

## H-A2 fixed-request and cancellation boundary

Current export continuation rejects changed owner/settings, cleared/relinked source, changed revision and changed request stamp before clearing or publishing another frame. Source invalidation marks an export episode without synchronously joining its strict owner lock. A retired episode cannot invalidate a newer independent episode.

`ITachieSource2.Update(TachieSourceDescription)` exposes Exporting usage but no job ID or cancellation token. `ProgressViewModel` has a native Cancel command/token on the inspected public WPF surface; this is not an established product cancellation bridge. `Source.Dispose` cancels its waiter before joining its GPU-owner lock, but normal native Cancel delivering that disposal **during** a strict preparation wait is not proven. Do not use a global token, arbitrary timeout, resource-download token, custom writer or private job method to fill this gap silently.

The device-epoch/request model has pure regressions; automatic real-player device replacement and all cross-owner resource peaks still need phase A/H host proof. [Acceptance](ACCEPTANCE.md) records these incomplete boundaries.
