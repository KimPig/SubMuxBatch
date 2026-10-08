# SubMux FFmpeg 8.1 Windows x64 binary

`submux-ffmpeg-8.1-win-x64.zip` was built by
`.github/workflows/build-ffmpeg.yml` from the source archives documented in
`third-party-sources/ffmpeg/8.1/README.md`.

- GitHub Actions run: `37769054980`
- Archive SHA-256: `222CE5F119932B1004E62D696DD7CB494895A1DF7EED015B4FA02F7FE07C0522`
- `ffmpeg.exe` SHA-256: `E65635765731D860FE2D250D51F6846AE4C751E0DC6E0691DA362554851B147E`
- FFmpeg version: `8.1.3` (`330caae0c1acccd2222edc52a05940c574561ce5`)
- x265 revision: `020d7054dbecfd3be8e62efe33ff8a305d41856e`
- Opus version: `1.5.2`
- Target: Windows x64, static MinGW runtime, GPL, x265 Main10, Opus

The Windows verification job runs the executable with only Windows system
directories on `PATH`, checks its GPL/nonfree configuration and required media
capabilities, and performs real HEVC Main10 and Opus smoke encodes.
