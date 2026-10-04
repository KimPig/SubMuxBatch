# Third-party corresponding source archives

This directory stores the exact or corresponding upstream source archives for GPL software redistributed inside the SubMux Batch single-file Windows package. The release ZIP still contains only `SubMuxBatch.exe`; these archives are source-compliance material and are not embedded in the application.

| Component | Bundled version | License | Source details |
| --- | --- | --- | --- |
| MKVToolNix | 102.0 | GPL-2.0-only | [`mkvtoolnix/102.0/README.md`](mkvtoolnix/102.0/README.md) |
| LAPSE | 2.2.4 | GPL-3.0-or-later | [`lapse/2.2.4/README.md`](lapse/2.2.4/README.md) |

LAPSE statically links FFmpeg, FFTW, libfvad, and zlib. The corresponding source archives used by its official Windows build are stored below its version directory. ONNX Runtime and the Silero VAD model are separately distributed permissive components; their license texts and ONNX Runtime third-party notices are stored under [`../licenses/lapse/2.2.4`](../licenses/lapse/2.2.4).
