#!/usr/bin/env bash
# Cross-compiles audiopluginresonanceaudio.dll (Windows x64) with zig, WITHOUT the
# Embree-based reverb baking (InitializeReverbComputer / ComputeRt60sAndProxyRoom).
# Baking keeps using the original DLL, renamed - see the C# patch.
# Usage: build_windows_dll.sh <resonance-audio repo root> <deps folder>
#   deps folder: nativeaudioplugins (Unity SDK header), ogg, vorbis checkouts.
set -euo pipefail
RA="$1"          # resonance-audio repo root (with per_source_reflections.patch applied)
WB="$2"          # deps folder
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$WB/out"; OBJ="$WB/obj"
mkdir -p "$OUT" "$OBJ"
ZIG="python3 -m ziglang"
TARGET="-target x86_64-windows-gnu"
COMMON="$TARGET -O2 -DNDEBUG -msse -msse2 -msse3 -ffp-contract=off -DEIGEN_MPL2_ONLY -D_USE_MATH_DEFINES -DNOMINMAX -DPFFFT_STATIC_DEFINE"
INC="-I$RA -I$RA/resonance_audio -I$RA/third_party/eigen -I$RA/third_party/pffft \
     -I$RA/third_party/SADIE_hrtf_database/generated -I$WB/nativeaudioplugins \
     -I$WB/ogg/include -I$WB/vorbis/include -I$WB/vorbis/lib"

compile() {  # $1 = source, $2 = language flags
  local src="$1"; local o="$OBJ/$(echo "$src" | tr '/.' '__').o"
  $ZIG ${CC_KIND} $COMMON $2 $INC -c "$src" -o "$o"
  echo "$o"
}
export -f compile; export ZIG COMMON INC OBJ CC_KIND

cd "$RA"
SOURCES_CXX="$(cat "$HERE/core_sources.txt") \
  third_party/SADIE_hrtf_database/generated/hrtf_assets.cc \
  resonance_audio/utils/ogg_vorbis_recorder.cc \
  resonance_audio/utils/vorbis_stream_encoder.cc \
  platforms/unity/unity.cc \
  platforms/unity/unity_nativeaudioplugins.cc"
SOURCES_C="third_party/pffft/pffft.c third_party/pffft/pffft_common.c third_party/pffft/fftpack.c \
  $WB/ogg/src/bitwise.c $WB/ogg/src/framing.c \
  $(ls $WB/vorbis/lib/*.c | grep -v -E 'psytune|barkmel|tone|vorbisfile')"

CC_KIND="c++" printf '%s\n' $SOURCES_CXX | CC_KIND="c++" xargs -P 8 -I{} bash -c 'compile {} "-std=c++14"' > "$OBJ/cxx.list"
CC_KIND="cc"  printf '%s\n' $SOURCES_C   | CC_KIND="cc"  xargs -P 8 -I{} bash -c 'compile {} ""' > "$OBJ/c.list"

$ZIG c++ $TARGET -shared -O2 \
  $(cat "$OBJ/cxx.list" "$OBJ/c.list") "$HERE/exports.def" \
  -o "$OUT/audiopluginresonanceaudio.dll"
ls -l "$OUT/audiopluginresonanceaudio.dll"
