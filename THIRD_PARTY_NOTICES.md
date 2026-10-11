# Third-party notices

The native plugins in `Assets/ResonanceAudio/Plugins/x86_64/` (`audiopluginresonanceaudio.dll`, `libaudiopluginresonanceaudio.so`) are compiled from the following components. Full license texts are in [`licenses/`](licenses/).

| Component | Copyright | License | License text |
|---|---|---|---|
| Resonance Audio: engine and Unity plugin | © 2017–2018 Google Inc. | Apache-2.0 | [`LICENSE`](LICENSE) |
| Resonance Audio SDK for Unity: C# scripts (modified, see `NOTICE`) | © 2017 Google Inc. | Apache-2.0 | [`LICENSE`](LICENSE) |
| SADIE binaural measurement database: HRTF data, as distributed with Resonance Audio | University of York | Apache-2.0 | [`licenses/SADIE-HRTF-Apache-2.0.txt`](licenses/SADIE-HRTF-Apache-2.0.txt) |
| Eigen | Eigen contributors | MPL-2.0 | [`licenses/Eigen-MPL-2.0.txt`](licenses/Eigen-MPL-2.0.txt) |
| PFFFT, including FFTPACK | © 2020 Dario Mambro, © 2019 Hayati Ayguen, © 2013 Julien Pommier, © 2004 UCAR | BSD-style (FFTPACK license) | [`licenses/PFFFT.txt`](licenses/PFFFT.txt) |
| libogg 1.3.5 | © 2002 Xiph.org Foundation | BSD-3-Clause | [`licenses/libogg-BSD-3-Clause.txt`](licenses/libogg-BSD-3-Clause.txt) |
| libvorbis 1.3.7 | © 2002–2020 Xiph.org Foundation | BSD-3-Clause | [`licenses/libvorbis-BSD-3-Clause.txt`](licenses/libvorbis-BSD-3-Clause.txt) |
| Unity Native Audio Plugin SDK (`AudioPluginInterface.h`) | © 2014 Unity Technologies | MIT | [`licenses/Unity-NativeAudioPlugins-MIT.txt`](licenses/Unity-NativeAudioPlugins-MIT.txt) |
| LLVM libc++, libc++abi, libunwind: statically linked C++ runtime, via zig | LLVM Project contributors | Apache-2.0 WITH LLVM-exception | [`licenses/LLVM-libcxx-Apache-2.0-with-LLVM-exception.txt`](licenses/LLVM-libcxx-Apache-2.0-with-LLVM-exception.txt) |
| mingw-w64 runtime startup code: Windows DLL only, via zig | © 2009–2013 the mingw-w64 project | ZPL-2.1 (parts public domain / BSD) | [`licenses/mingw-w64-runtime.txt`](licenses/mingw-w64-runtime.txt) |

## Source code availability (MPL-2.0)

Eigen is used unmodified, as a header-only library. Its source is available at <https://gitlab.com/libeigen/eigen> (the build used the GitHub mirror <https://github.com/eigenteam/eigen-git-mirror> at commit `36b9596`). It was compiled with `EIGEN_MPL2_ONLY`, so no LGPL-licensed Eigen code is included.

## Not distributed

These tools were used to build or test the plugin but are not part of this repository's files or binaries:
- googletest (BSD-3-Clause), for unit tests only.
- zig (MIT), the compiler toolchain.
- Intel Embree (Apache-2.0), used by the *stock* Resonance plugin for reverb baking. That plugin is not included here; you copy it from your own Resonance SDK install.
