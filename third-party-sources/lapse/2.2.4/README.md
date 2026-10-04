# LAPSE 2.2.4 and its native dependencies

SubMux Batch embeds the unmodified files from the official `lapse-windows-x64.zip` release archive and executes `lapse.exe` as a separate command-line program. The official binary release SHA-256 is `63D7E924E4DA50A1A067B756EBB7C1517996632F7742F45D28D8CE77A742A2A6`.

## LAPSE source

- Upstream tag: <https://github.com/Schwponaco-org/lapse/releases/tag/v2.2.4>
- Upstream source archive: <https://github.com/Schwponaco-org/lapse/archive/refs/tags/v2.2.4.tar.gz>
- Stored file: `lapse-2.2.4.tar.gz`
- SHA-256: `D1AB0BDAAC81F1C038AC64FDF2578FA3DEACA1CE31980F0C9D8C0024800C8BE1`
- License: GPL-3.0-or-later

The `GPL-3.0-or-later` identifier follows the copyright headers in LAPSE's source and build files, which permit GPL version 3 or any later version.

## Statically linked dependencies

The official v2.2.4 Windows release workflow builds and statically links the following components:

| Component | Version/source | License | Stored archive | SHA-256 |
| --- | --- | --- | --- | --- |
| FFmpeg | 7.1 | LGPL-2.1-or-later; the LAPSE build does not enable FFmpeg GPL components | `dependencies/ffmpeg-7.1.tar.xz` | `40973D44970DBC83EF302B0609F2E74982BE2D85916DD2EE7472D30678A7ABE6` |
| FFTW | 3.3.10 | GPL-2.0-or-later | `dependencies/fftw-3.3.10.tar.gz` | `56C932549852CDDCFAFDAB3820B0200C7742675BE92179E59E6215B340E26467` |
| libfvad | commit `532ab666c20d3cfda38bca63abbb0f152706c369` | BSD-3-Clause | `dependencies/libfvad-532ab666.tar.gz` | `BF639E4A5BC37B1C90E047C42524E80A1D0CECADEB6E0A61A33580FCA22A00AB` |
| zlib | 1.3.2 (MSYS2 package revision 2 in the release runner) | zlib License | `dependencies/zlib-1.3.2.tar.gz` | `BB329A0A2CD0274D05519D61C667C062E06990D72E125EE2DFA8DE64F0119D16` |

Upstream archives:

- <https://ffmpeg.org/releases/ffmpeg-7.1.tar.xz>
- <https://fftw.org/fftw-3.3.10.tar.gz>
- <https://github.com/dpirch/libfvad/archive/532ab666c20d3cfda38bca63abbb0f152706c369.tar.gz>
- <https://zlib.net/zlib-1.3.2.tar.gz>

The exact build configuration is documented by LAPSE's tag in `.github/workflows/release.yml` and `.github/scripts/`. In particular, the Windows workflow uses ONNX Runtime 1.28.0, and the fetch script pins the Silero VAD 6.2.1 model by SHA-256.

## Dynamically loaded/runtime data

- ONNX Runtime 1.28.0 (`onnxruntime.dll`) — MIT
- Silero VAD 6.2.1 (`silero_vad.onnx`) — MIT

Their license texts and ONNX Runtime's complete `ThirdPartyNotices.txt` are kept in [`../../../licenses/lapse/2.2.4`](../../../licenses/lapse/2.2.4) and embedded in the SubMux Batch executable for display in **Settings → Other → Open-source licenses**.
