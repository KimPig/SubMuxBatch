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
