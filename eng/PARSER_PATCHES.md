# Pinned parser build patch

## psb-section-length-v1

Upstream: `manju-summoner/PsdParser` at `ff3aee18a95e5fb6e868585a5b0ad15f46decd89`.
File: `PsdParser/LayerAndMaskInformationSection.cs`.

The synthetic RGB8 PSD/PSB regressions cover the final layer/mask section position check, including the PSB eight-byte section length and an empty global-mask field.

The source uses `lengthSize = isPSB ? 8 : 4`, but its early end-of-section branch compares against `position + 4 + Length`. With a PSB and a four-byte empty global-mask field, that branch mistakenly skips the global-mask length field. The final check correctly expects `position + lengthSize + Length` and rejects the stream.

Build-copy correction:

```diff
- if (reader.BaseStream.Position == position + 4 + Length)
+ if (reader.BaseStream.Position == position + lengthSize + Length)
```

Preparation requires exactly one matching source expression and writes only `.deps/patched-src/LayerAndMaskInformationSection.cs`. The pinned Git checkout stays clean. The wrapper excludes the original compilation unit and includes this build copy. Original/patched hashes are emitted to `.deps/patched-src/provenance.json`.

Regression coverage: PSD vs PSB × Raw vs RLE × flat vs group/mask fixture (eight cases). No fixture was weakened or skipped. This is a product dependency fix, NOT a claim about the parser binary bundled by YMM4. No upstream repository was modified.

The compiled format profile remains P0.1: this patch only admits PSB sources that previously failed before publication and does not change decoded PSD pixels. The patch identity is recorded separately in build provenance; no prior PSB generation produced by the unpatched importer can exist.
