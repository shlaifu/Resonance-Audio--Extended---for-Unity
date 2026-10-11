# Native plugin: source, build and tests

The plugins in `Assets/ResonanceAudio/Plugins/x86_64/` are upstream [Resonance Audio](https://github.com/resonance-audio/resonance-audio) with [`per_source_reflections.patch`](per_source_reflections.patch) applied. They are cross-compiled with [zig](https://ziglang.org/) 0.17.0 (installed with `pip install ziglang`). The build runs on Linux, and both targets are built from there.

## What the patch changes

**`resonance_audio/dsp/image_source_reflections.{h,cc}`** (new)
- Six first-order image-source reflections of a source inside a shoebox room.
- Each reflection's delay is the extra path length over the direct path, divided by the speed of sound.
- Gain is the wall coefficient times the ratio of direct to reflected path length.
- Direction is that of the image source as seen from the listener.
- Reflections whose extra path is under 1 m fade out, since they would only comb-filter the direct sound.

**`resonance_audio/graph/per_source_reflections_node.{h,cc}`** (new)
- Replaces `ReflectionsNode`.
- Each source runs a low-pass, a delay line (max 0.5 s) and six gain taps, encoded into one shared first-order ambisonic bus.
- When reflections move, the old and new taps are crossfaded.
- After a source stops, its reflection tails ring out.
- Soundfield sources stay listener-centric.

**`resonance_audio/graph/graph_manager.{h,cc}`**
- Sources feed the per-source node directly; the shared reflections gain mixer is removed.

**`platforms/unity/unity_nativeaudioplugins.{h,cc}`**
- New export `SetListenerTransformFromScript(const float* listenerToWorld)`.
- While C# calls it, the renderer stops taking the listener pose from spatializer callbacks. Pass `nullptr` to hand the pose back.

**Tests**
- `image_source_reflections_test.cc`
- `per_source_reflections_node_test.cc`
- `per_source_reflections_integration_test.cc`

Room geometry, wall materials and the reflection cutoff still come from the usual `SetRoomProperties` call, so the C# interface is unchanged apart from the new export.

## Dependency versions used

| Path | Source | Version |
|---|---|---|
| Resonance Audio | https://github.com/resonance-audio/resonance-audio | `4556a46` (base of the patch) |
| `third_party/eigen` | https://github.com/eigenteam/eigen-git-mirror | `36b9596` |
| `third_party/pffft` | https://github.com/marton78/pffft | `aa16fd3` |
| `third_party/googletest` (tests only) | https://github.com/google/googletest | `release-1.12.1` |
| `<deps>/ogg` | https://github.com/xiph/ogg | `v1.3.5` |
| `<deps>/vorbis` | https://github.com/xiph/vorbis | `v1.3.7` |
| `<deps>/nativeaudioplugins` | https://github.com/Unity-Technologies/NativeAudioPlugins | `bc7893e` |

Upstream's `third_party/clone_core_deps.sh` points at Bitbucket and GitLab locations that have moved or disappeared. The GitHub sources above are drop-in replacements. pffft is the maintained fork of the original by Julien Pommier.

## Build

```bash
# Resonance + patch
git clone https://github.com/resonance-audio/resonance-audio
cd resonance-audio
git checkout 4556a46
git apply /path/to/Native/per_source_reflections.patch
git clone https://github.com/eigenteam/eigen-git-mirror third_party/eigen && git -C third_party/eigen checkout 36b9596
git clone https://github.com/marton78/pffft third_party/pffft && git -C third_party/pffft checkout aa16fd3
cd ..

# Plugin-only dependencies
mkdir deps && cd deps
git clone -b v1.3.5 https://github.com/xiph/ogg
git clone -b v1.3.7 https://github.com/xiph/vorbis
git clone https://github.com/Unity-Technologies/NativeAudioPlugins nativeaudioplugins
cd ..

pip install ziglang==0.17.0

# Windows x64  -> deps/out/audiopluginresonanceaudio.dll
bash Native/build_windows_dll.sh "$PWD/resonance-audio" "$PWD/deps"
# Linux x86_64 -> deps/out-linux/libaudiopluginresonanceaudio.so (glibc >= 2.17)
bash Native/build_linux_so.sh    "$PWD/resonance-audio" "$PWD/deps"
```

Notes:
- **No reverb baker.** The plugins are built without Resonance's reverb computer (`InitializeReverbComputer`, `ComputeRt60sAndProxyRoom`), which needs Intel Embree. The C# side loads those from the renamed stock plugin (`resonanceaudiobake`).
- **Self-contained Windows DLL.** It imports only `KERNEL32` and the Universal CRT (`api-ms-win-crt-*`), which ship with Windows 10 and later.
- **Self-contained Linux library.** It needs only glibc. The C++ runtime is linked statically, and only the seven plugin entry points are exported (`exports.map`), so the bundled ogg/vorbis can't clash with symbols in the Unity process.

## Tests

**Engine unit tests** (Linux; 446 upstream tests and 12 new ones, all passing):

```bash
cd resonance-audio
git clone -b release-1.12.1 https://github.com/google/googletest third_party/googletest
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_RESONANCE_AUDIO_TESTS=ON
cmake --build build
./build/resonance_audio/ResonanceAudioUnitTests
```

The same suite also passes under AddressSanitizer and UBSan. To run it that way, add `-DCMAKE_CXX_FLAGS="-fsanitize=address,undefined" -DCMAKE_C_FLAGS="-fsanitize=address,undefined"`.

**Plugin smoke test** (Linux; loads the built `.so` the way Unity does):

```bash
g++ -std=c++14 -O1 -I deps/nativeaudioplugins -I resonance-audio \
    Native/tests/plugin_smoke_test.cc -ldl -o plugin_smoke_test
./plugin_smoke_test deps/out-linux/libaudiopluginresonanceaudio.so
```

The test calls `UnityGetAudioEffectDefinitions`, creates the renderer and two spatializer instances, and renders audio through Unity's callback interface. One instance carries a stale listener matrix, as Unity sends for a source playing without a clip. The test checks three things:
1. With the per-source pose, a noise source 2 m to the right comes out on the **left** (−10 dB). This reproduces the bug.
2. With `SetListenerTransformFromScript`, the source comes out on the right (+10 dB).
3. With a room set, the output stays finite and gains energy.

The Windows DLL is built from identical sources. It can't be loaded on Linux, so it was tested in Unity on Windows instead.
