# Third-party notices

First-party code is licensed under MIT; see [LICENSE](LICENSE). This source-only repository does not distribute third-party binaries, host binaries or user assets.

| Dependency | Pinned version/source commit | License |
| --- | --- | --- |
| [PsdParser](https://github.com/manju-summoner/PsdParser) | `ff3aee18a95e5fb6e868585a5b0ad15f46decd89` | [MIT, copyright 2022 manju summoner](licenses/PsdParser.LICENSE) |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) Direct2D1, Direct3D11, DirectX, DXGI | 3.8.3; `9e609cb9439c9872aa1b339f177e40ec96f77239` | [MIT, Amer Koleci and Contributors](licenses/Vortice.Windows.LICENSE) |
| [Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics) | 2.1.0; `fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6` | [MIT, Amer Koleci and contributors](licenses/Vortice.Mathematics.LICENSE) |
| [SharpGenTools](https://github.com/SharpGenTools/SharpGenTools) Runtime and Runtime.COM | 2.4.2-beta; `6990bcafe124a4c22515ad19cee5a081da8db67b` | [MIT; upstream notices retained in full](licenses/SharpGenTools.LICENSE) |

`eng/prepare_parser.py` fetches the public MIT parser source. Its original checkout stays unchanged. One separately generated compilation unit changes the PSB layer/mask end check from a hard-coded 4-byte length to `lengthSize`; see [the patch record](eng/PARSER_PATCHES.md). The assembly name is `PsdTachieNext.Parser`, avoiding the host parser's identity. Include the full upstream license and copyright with any distributed parser build and retain this patch notice.

The test-only Direct2D project restores its pinned NuGet dependencies. The YMM4 adapter instead references the chosen official host assemblies with `Private=false`; it does not ship another host Vortice copy. YMM4, Newtonsoft.Json and other selected host references remain external. CI downloads the official host temporarily and uploads only explicitly listed synthetic test summaries and provenance, never the host or its bundled FFmpeg/codec files.

The implementation and synthetic PSD/PSB fixture writer are independently authored. No old extended-PSD plugin code, decompiled implementation, PSDTool implementation, unknown official-sample copy or user PSD is included. References to supported public host APIs are not copies of host implementation code.
