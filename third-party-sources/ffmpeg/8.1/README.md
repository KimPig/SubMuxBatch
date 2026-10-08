# SubMux FFmpeg 8.1 GPL build sources

SubMux Batch's reproducible Windows x64 FFmpeg build uses only FFmpeg, x265,
and Opus as source inputs. The build and Windows smoke-test recipes are stored
in `build/ffmpeg`, and `.github/workflows/build-ffmpeg.yml` performs the Linux
MinGW cross-build followed by validation on a Windows runner.

| Custom-build archive | Upstream revision | SHA-256 |
| --- | --- | --- |
| `ffmpeg-330caae0c1.tar.gz` | FFmpeg `330caae0c1acccd2222edc52a05940c574561ce5` | `67B5876EE973A26F267280B2C1EB851A0AC7A502382D20E41B7A819111886AF9` |
| `x265-020d7054dbecfd3be8e62efe33ff8a305d41856e.tar.gz` | x265 `020d7054dbecfd3be8e62efe33ff8a305d41856e` | `ADE2FD082D1A3D7ADF89BB38EB54A5CBF6AAD6C1C7ED130C89C7BF2CD626484D` |
| `opus-1.5.2.tar.gz` | Opus 1.5.2 | `65C1D2F78B9F2FB20082C38CBE47C951AD5839345876E46941612EE87F9A7CE1` |

The resulting executable is static, GPL-enabled, nonfree-disabled, and contains
x265 Main10 and Opus encoders. `build/ffmpeg/verify-build.ps1` checks the license
configuration, required codecs and formats, and performs Main10 and Opus smoke
encodes before the binary can be used by a SubMux release build.

## Transitional BtbN binary

SubMux Batch embeds the static Windows x64 GPL build published by
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds). The binary is
executed as a separate command-line program and contains `libx265` and
`libopus`.

- Binary release: `autobuild-2026-10-07-13-07`
- Binary asset: `ffmpeg-n8.1.3-14-g330caae0c1-win64-gpl-8.1.zip`
- Binary archive SHA-256: `17FC492B32962EB241AABC2A9AB9F2439FDCC7FD8179B32F33129436A12A030F`
- FFmpeg version reported by the binary: `n8.1.3-14-g330caae0c1-20261007`
- x265 version reported by the binary: `4.3+49-020d7054`

| Archive | Upstream revision | SHA-256 |
| --- | --- | --- |
| `btbn-ffmpeg-builds-9acad4a9ef1583096af7836cc1e9c8cbcb4d3950.tar.gz` | BtbN build scripts `9acad4a9ef1583096af7836cc1e9c8cbcb4d3950` | `F9BD160A794173DB7E838AD5E8841EBBA845CE6D012E830951946AA85B2967AF` |
| `ffmpeg-330caae0c1.tar.gz` | FFmpeg `330caae0c1acccd2222edc52a05940c574561ce5` | `67B5876EE973A26F267280B2C1EB851A0AC7A502382D20E41B7A819111886AF9` |
| `x265-020d7054dbecfd3be8e62efe33ff8a305d41856e.tar.gz` | x265 `020d7054dbecfd3be8e62efe33ff8a305d41856e` | `ADE2FD082D1A3D7ADF89BB38EB54A5CBF6AAD6C1C7ED130C89C7BF2CD626484D` |

The BtbN archive contains the build recipe and the complete dependency list.
The FFmpeg and x265 archives are the exact revisions identified by that recipe
and the bundled executable. Other linked dependency sources can be reproduced
from the pinned revisions in the BtbN scripts.

The normal publish script temporarily falls back to this BtbN build until the
first custom artifact completes Windows validation. A validated custom binary
can already be supplied with `build/Publish.ps1 -BundledFfmpegPath ...`. The
BtbN fallback and build-script archive will be removed after the custom binary
hash is pinned in the release pipeline.
