#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SOURCE_DIR="$ROOT/third-party-sources/ffmpeg/8.1"
WORK_DIR="${RUNNER_TEMP:-$ROOT/artifacts/ffmpeg-build}"
PREFIX="$WORK_DIR/prefix"
OUTPUT_DIR="$ROOT/artifacts/ffmpeg-custom/win-x64"
JOBS="${JOBS:-$(nproc)}"

FFMPEG_ARCHIVE="$SOURCE_DIR/ffmpeg-330caae0c1.tar.gz"
X265_ARCHIVE="$SOURCE_DIR/x265-020d7054dbecfd3be8e62efe33ff8a305d41856e.tar.gz"
OPUS_ARCHIVE="$SOURCE_DIR/opus-1.5.2.tar.gz"

printf '%s  %s\n' \
  '67b5876ee973a26f267280b2c1eb851a0ac7a502382d20e41b7a819111886af9' "$FFMPEG_ARCHIVE" \
  'ade2fd082d1a3d7adf89bb38eb54a5cbf6aad6c1c7ed130c89c7bf2cd626484d' "$X265_ARCHIVE" \
  '65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1' "$OPUS_ARCHIVE" | sha256sum --check --strict

rm -rf "$WORK_DIR" "$OUTPUT_DIR"
mkdir -p "$WORK_DIR/src" "$PREFIX" "$OUTPUT_DIR"
tar -xzf "$FFMPEG_ARCHIVE" -C "$WORK_DIR/src"
tar -xzf "$X265_ARCHIVE" -C "$WORK_DIR/src"
tar -xzf "$OPUS_ARCHIVE" -C "$WORK_DIR/src"

FFMPEG_SOURCE="$(find "$WORK_DIR/src" -maxdepth 1 -type d -name 'FFmpeg-*' -print -quit)"
X265_SOURCE="$(find "$WORK_DIR/src" -maxdepth 1 -type d -name 'x265-*' -print -quit)"
OPUS_SOURCE="$(find "$WORK_DIR/src" -maxdepth 1 -type d -name 'opus-*' -print -quit)"
test -n "$FFMPEG_SOURCE" && test -n "$X265_SOURCE" && test -n "$OPUS_SOURCE"

cat > "$WORK_DIR/mingw64-toolchain.cmake" <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
set(CMAKE_C_COMPILER x86_64-w64-mingw32-gcc)
set(CMAKE_CXX_COMPILER x86_64-w64-mingw32-g++)
set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)
set(CMAKE_FIND_ROOT_PATH "$PREFIX" /usr/x86_64-w64-mingw32)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
EOF

cmake -S "$X265_SOURCE/source" -B "$WORK_DIR/x265-build" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$WORK_DIR/mingw64-toolchain.cmake" \
  -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DCMAKE_BUILD_TYPE=Release \
  -DENABLE_SHARED=OFF \
  -DENABLE_CLI=OFF \
  -DENABLE_PIC=ON \
  -DHIGH_BIT_DEPTH=ON \
  -DMAIN12=OFF \
  -DEXPORT_C_API=ON
cmake --build "$WORK_DIR/x265-build" --parallel "$JOBS"
cmake --install "$WORK_DIR/x265-build"

# x265's MinGW CMake probe records -lgcc_s in x265.pc even for a static-only
# build.  That explicit shared-runtime dependency wins over FFmpeg's static
# linker settings and makes the final executable require libgcc_s_seh-1.dll.
# The C++ linker wrapper below supplies the matching static libgcc runtime.
sed -i 's/[[:space:]]-lgcc_s\b//g' "$PREFIX/lib/pkgconfig/x265.pc"

pushd "$OPUS_SOURCE" >/dev/null
./configure \
  --host=x86_64-w64-mingw32 \
  --prefix="$PREFIX" \
  --disable-shared \
  --enable-static \
  --disable-doc \
  --disable-extra-programs
make -j"$JOBS"
make install
popd >/dev/null

export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"

# FFmpeg switches the final link step to the C++ compiler when libx265 is
# enabled.  Some MinGW toolchains append their default shared libgcc choice
# after FFmpeg's extra linker flags, which leaves ffmpeg.exe dependent on
# libgcc_s_seh-1.dll.  Keep the static-runtime options at the very end of every
# C++ compiler/linker invocation so the resulting executable works on a clean
# Windows installation.
cat > "$WORK_DIR/mingw64-g++-static" <<'EOF'
#!/usr/bin/env bash
exec x86_64-w64-mingw32-g++ "$@" -static -static-libgcc -static-libstdc++
EOF
chmod +x "$WORK_DIR/mingw64-g++-static"

pushd "$FFMPEG_SOURCE" >/dev/null
./configure \
  --pkg-config=pkg-config \
  --pkg-config-flags=--static \
  --target-os=mingw32 \
  --arch=x86_64 \
  --cross-prefix=x86_64-w64-mingw32- \
  --cxx="$WORK_DIR/mingw64-g++-static" \
  --prefix="$PREFIX" \
  --enable-cross-compile \
  --enable-gpl \
  --enable-version3 \
  --enable-libx265 \
  --enable-libopus \
  --disable-nonfree \
  --enable-static \
  --disable-shared \
  --disable-network \
  --disable-autodetect \
  --disable-debug \
  --disable-doc \
  --disable-ffplay \
  --disable-ffprobe \
  --disable-devices \
  --enable-indev=lavfi \
  --extra-cflags="-I$PREFIX/include" \
  --extra-ldflags="-L$PREFIX/lib -static -static-libgcc -static-libstdc++" \
  --extra-ldexeflags="-static -static-libgcc -static-libstdc++" \
  --extra-libs="-lstdc++ -lpthread"
make V=1 -j"$JOBS" ffmpeg.exe
popd >/dev/null

cp "$FFMPEG_SOURCE/ffmpeg.exe" "$OUTPUT_DIR/ffmpeg.exe"
sha256sum "$OUTPUT_DIR/ffmpeg.exe" > "$OUTPUT_DIR/SHA256SUMS"
printf '%s\n' \
  'FFmpeg revision: 330caae0c1acccd2222edc52a05940c574561ce5' \
  'x265 revision: 020d7054dbecfd3be8e62efe33ff8a305d41856e' \
  'Opus version: 1.5.2' \
  'Target: Windows x64, static, GPL, Main10 x265' > "$OUTPUT_DIR/BUILD-INFO.txt"

