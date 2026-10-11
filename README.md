# Resonance Audio for Unity – Extended

Extensions and fixes for Google's [Resonance Audio](https://github.com/resonance-audio/resonance-audio) in Unity. The project keeps Resonance's binaural spatializer and adds:

- a dense, baked reverb field
- early reflections computed per source
- air absorption
- softer, volumetric occlusion
- a fix for a listener-position bug that can make every sound in the scene seem to come from the wrong place

It is installed as an overlay on top of the official [Resonance Audio SDK for Unity](https://github.com/resonance-audio/resonance-audio-unity-sdk/releases). It needs no new middleware and no new mixer setup.

> Not affiliated with or endorsed by Google. Resonance Audio is an open-source project by Google, licensed under Apache-2.0.

---

## Features

### Native plugin (rebuilt `audiopluginresonanceaudio`)

- **Per-source early reflections.** Stock Resonance computes one set of six early reflections from the *listener's* position and applies it to every sound. Footsteps at your feet and a gunshot 40 m away get identical reflections. Standing near a wall pulls everything toward that wall.
  - Here every source gets its own six image-source reflections, computed from its own position in the room box: delay, level and direction follow the real source → wall → listener geometry.
  - Ambisonic/soundfield sources keep the original listener-centric behaviour.
- **Listener pose from script.** Stock Resonance takes the listener position from whichever source it processed last. Unity does not update that data for a source that plays without an AudioClip ([Unity issue, closed "By Design"](https://issuetracker-mig.prd.it.unity3d.com/issues/audio-spatializer-sdk-meta-data-not-sent-to-native-plugin-if-no-audioclip-is-selected-in-audiosource)).
  - One such source, for example a weapon AudioSource with *Play On Awake* and no clip that is only used with `PlayOneShot`, freezes the listener where it was when that source started. Everything is then heard from that old spot.
  - The plugin now accepts the pose from C# every frame, so no source can override it.

### Unity scripts

- **`ResonanceReverbGrid`: dense baked reverb.** Resonance's own reverb probes apply one baked result to a whole region and switch hard at region borders.
  - The grid bakes Resonance's own reverb at every point of a 3D lattice and blends the 8 points around the listener (trilinear), so reverb changes smoothly from corridor to hall.
  - Grid points the listener can't see are down-weighted, so the next room's reverb doesn't leak through walls.
  - Baking uses Resonance's own ray tracer and material map, from the usual *Reverb Baking* window.
- **Openness-aware reverb.** An optional fast bake measures how enclosed each grid point is. At runtime this:
  - caps RT60 with an Eyring estimate, so open fields don't get cathedral tails
  - fades reverb gain as the sky opens
  - makes the ceiling of the reflection room transparent under open sky
  - fades early reflections outdoors
- **Live reflection room.** The early-reflection box is built continuously from raycasts around the listener rather than switched between baked boxes. Moving through a level no longer makes reflections jump.
- **Reverb level from room volume.** Resonance plays reverb at one fixed level. The diffuse level is scaled with RT60 / room volume, so a huge space with the same decay sounds quieter than a small room.
- **Distance-dependent reverb send.** In stock Resonance a source's reverb send doesn't fall off with distance, which produces a "distant rain" wash of reverb from far sources.
  - Here the send follows the source's own volume rolloff, softened by a *wet growth* setting, so far sources get proportionally wetter without flooding the mix.
  - Each source also has a **Room Effects Send (dB)** trim, for example to keep first-person weapons drier.
- **`ResonanceAirAbsorption`.** Frequency-dependent distance falloff, based on ISO 9613-1 at 20 °C and 50 % relative humidity.
  - Direct sound gets duller with distance through Resonance's existing per-source low-pass. No native change is needed for this.
  - Reverb tails decay faster in high bands.
- **`ResonanceOcclusion`: softer, volumetric occlusion.**
  - Several rays go to a disc around each source instead of a single ray, so edges and pillars occlude partially.
  - Resonance's very steep occlusion filter is softened: one wall gives about 2.3 kHz instead of about 500 Hz.
  - A modest, smoothed level drop per occluder is added.
  - Trigger colliders are ignored, and sample points that end up inside floors or walls are pulled back into free space.
- **Fixes in the stock scripts.**
  - The listener is re-resolved when the cached AudioListener is destroyed or disabled; it was cached once and never refreshed.
  - The `ResonanceAudioSource` inspector no longer draws its directivity preview into Unity's shared `Texture2D.blackTexture`, which corrupted every other use of that texture in the Editor.
- **Debug readouts in play mode.**
  - The grid shows enclosure, reverb gain, mean RT60, room volume and reflection strength.
  - `ResonanceAirAbsorption` shows, for any source, its distance, air cutoff, total occlusion sent and actual reverb send in dB.

---

## Platform support

| Platform | Native plugin | Status |
|---|---|---|
| **Windows x64** | Rebuilt (`Plugins/x86_64/audiopluginresonanceaudio.dll`) | Used and tested in Unity 6.3, Editor and player. |
| **Linux x86_64** | Rebuilt (`Plugins/x86_64/libaudiopluginresonanceaudio.so`, glibc ≥ 2.17) | **Untested in Unity.** Built from the same source as the Windows plugin. It was smoke-tested outside Unity by loading it through Unity's native audio plugin interface and rendering audio: spatialization, room effects and the listener fix all work. It has never been run inside the Unity Editor or a Linux player. |
| macOS, Android, iOS | Stock Resonance plugin (not rebuilt) | Untested. All C# features (grid, openness, air absorption, occlusion, reverb send) should work. Per-source reflections and the listener fix are **not** available; `ResonanceListenerSync` logs one warning and steps aside. |

The scripts are plain C# and contain no platform-specific code beyond the plugin imports.

---

## Requirements

- Unity 6 (developed with **Unity 6.3**). Older versions are untested.
- The official **Resonance Audio SDK for Unity**, imported and working:
  - *Project Settings → Audio → Spatializer Plugin* = **Resonance Audio**
  - the `ResonanceAudioMixer` asset in use
  - sources routed to it

---

## Installation

> **Close Unity first.** Unity locks native plugins while it is running.

1. **Back up** your project, at least `Assets/ResonanceAudio/`.

2. **Keep the stock plugin for reverb baking.** The rebuilt plugin does not contain Resonance's reverb baker, which needs Intel Embree. Baking keeps using the stock plugin under a new name.
   - **Windows:** copy the *stock* `Assets/ResonanceAudio/Plugins/x86_64/audiopluginresonanceaudio.dll` to `Assets/ResonanceAudio/Editor/Plugins/resonanceaudiobake.dll`.
   - **Linux (untested):** copy the stock `Assets/ResonanceAudio/Plugins/x86_64/libaudiopluginresonanceaudio.so` to `Assets/ResonanceAudio/Editor/Plugins/libresonanceaudiobake.so`.

   The copy's name must **not** start with `audioplugin`. Unity loads every such library as an audio plugin, and the effects would be registered twice.

3. **Copy this repository's `Assets` folder** over your project's `Assets` folder and overwrite. This replaces:
   - the native plugin(s) in `Plugins/x86_64/`
   - five stock scripts (`ResonanceAudio.cs`, `ResonanceAudioSource.cs`, `ResonanceAudioSourceEditor.cs`, `ResonanceAudioReverbComputer.cs`, `ResonanceAudioReverbBakingWindow.cs`)

   It also adds the `ResonanceReverbGrid/` folder. Your existing `.meta` files, and with them the plugin import settings, are kept.

4. **Start Unity and check the plugin import settings.** Select each plugin and check its platform settings in the Inspector:
   - `audiopluginresonanceaudio.dll`: Editor + Standalone, Windows x86_64.
   - `libaudiopluginresonanceaudio.so`: Editor + Standalone, Linux x86_64.
   - `resonanceaudiobake.*`: **Editor only**, on the matching OS.

5. **Verify.**
   - The Console shows no errors, in particular no *"Effect Resonance Audio Renderer could not be found"*.
   - Spatialization works in Play mode.
   - *ResonanceAudio → Reverb Baking → Bake* still works.

**Rollback:** restore the backed-up `Assets/ResonanceAudio/` and delete `Editor/Plugins/resonanceaudiobake.*`.

---

## Setting up a scene

### Listener and sources (stock Resonance, with a few rules)

- On the camera's AudioListener, set the `ResonanceAudioListener` **Occlusion Mask** to **static level geometry only**. Exclude the player, NPCs, weapons and pickups.
- Every spatialized source needs:
  - **AudioSource** with *Spatialize* on, and **Output** set to the `ResonanceAudioMixer` group. Otherwise Unity also plays the raw, unspatialized signal through whatever mixer group it is routed to, including that group's effects.
  - **ResonanceAudioSource.** It is required for the reverb send, air absorption and occlusion features.
- Avoid *Play On Awake* on AudioSources with no clip. With the rebuilt plugin this is harmless, but on other platforms it still triggers the frozen-listener bug.

### Reverb grid

1. Set up Resonance's **Material Map** and **Reverb Mask** in *ResonanceAudio → Reverb Baking* as usual. The grid's ray tracing uses them.
2. Create an empty GameObject and add **ResonanceAudio → ResonanceReverbGrid**.
   - Set **Size** to cover the playable area. It is an axis-aligned box centred on the object; rotation and scale are ignored.
   - **Spacing** of 2–4 m is typical. The point count, and with it bake time, grows with the cube of 1 / spacing.
   - Set **Geometry Mask** and **Visibility Mask** to static level geometry.
3. Bake with **ResonanceAudio → Reverb Baking → Bake**. Every grid in the scene is baked alongside any selected probes. The data asset is created next to the scene.
4. Optional extras from the component's context menu (right-click its header):
   - **Bake Openness Only** is fast and needed for the RT60 cap and open-sky behaviour. Rerun it after level changes.
   - **Smooth Reverb Times** evens out noisy neighbouring points.
5. Don't let stock `ResonanceAudioRoom` or `ResonanceAudioReverbProbe` regions overlap a grid. Both drive the same global room state, and whichever updated last wins.

Several grids can share a scene, for example one per area.

### Optional global components (one each, on any GameObject)

- **ResonanceAudio → ResonanceAirAbsorption**: *Strength* 1 is physical; 2–4 is a common game exaggeration.
- **ResonanceAudio → ResonanceOcclusion**: tunes ray count, disc radius, filter strength and level drop. Occlusion itself is still enabled per source with *Occlusion Enabled* on `ResonanceAudioSource`.

`ResonanceListenerSync` needs no setup. It creates itself, hidden, when Play mode starts.

---

## Tuning cheat sheet

| Symptom | Setting |
|---|---|
| Your own weapons drown in reverb | `ResonanceAudioSource` → **Room Effects Send (dB)** about −6 to −12 |
| Distant sources sound too dry | Grid → **Enclosed / Open Wet Growth** up |
| Far-away sources wash the mix in reverb | Grid → **Wet Growth** down |
| Big tails on an open field | Run **Bake Openness Only**; raise **Enclosure Gain Exponent** |
| Large halls too loud, small rooms too quiet | Grid → **Reference Room Volume** |
| Reverb jumps between positions | Smaller **Spacing**, higher **Smoothing Time**, keep **Live Reflection Room** on |
| Reverb from the next room leaks through walls | Grid → **Visibility Check** on, **Hidden Point Weight** down |
| Occluded sounds too muffled | `ResonanceOcclusion` → **Filter Strength** down |
| Occluded sounds not quiet enough | `ResonanceOcclusion` → **Attenuation Per Occluder Db** up |
| Distant sounds not dull enough | `ResonanceAirAbsorption` → **Strength** up |

Use `ResonanceAirAbsorption`'s **Debug Source** field during Play to see what a source actually receives.

---

## Known limitations

- **Platforms:** the rebuilt plugin exists only for Windows x64 and Linux x86_64. Linux is **untested in Unity**. The other platforms run the stock plugin; see [Platform support](#platform-support).
- **Reverb baking still uses the stock plugin.** The rebuilt plugin has no baker, so the renamed stock copy is needed in the Editor. On macOS the bake import would need a renamed copy of the stock bundle (`resonanceaudiobake.bundle`); this is untested.
- **Reverb is global.** As in stock Resonance there is one reverb for the listener's position. A source in another room is heard with the listener's room reverb.
- **Early reflections are first-order and from a box.** There are six reflections per source, off a shoebox approximation of the surroundings, which is rebuilt live from raycasts. There are no higher-order reflections and no reflections off actual level geometry.
- **Occlusion is raycast-based.**
  - There is no per-material transmission and no path-finding around obstacles; the volumetric rays only approximate diffraction.
  - A raycast reports at most one hit per collider, so walls that share one combined MeshCollider count as a single occluder.
- **Air absorption on direct sound is a one-pole low-pass.** It is shared with occlusion, with a 6 dB/octave slope, so it is gentler than real air above the cutoff.
- **The grid is an axis-aligned box.** Bake time grows quickly with smaller spacing, since Resonance traces 50,000 paths per point by default.
- **The listener pose updates once per rendered frame** (`LateUpdate`), not once per audio block.
- **CPU cost grows with the number of sources.** Per-source reflections cost a small delay line and six taps per playing source. That's fine for dozens of sources but not free. Reflection delays are capped at 0.5 s.
- **This is a hobby project.** It was developed for one game. Expect rough edges and test in your own project before relying on it.

---

## Building the native plugin

The plugin is built from upstream Resonance Audio plus [`Native/per_source_reflections.patch`](Native/per_source_reflections.patch), cross-compiled with [zig](https://ziglang.org/). See [`Native/README.md`](Native/README.md) for exact steps, dependency versions and tests.

---

## Credits and licenses

This project is licensed under the **Apache License 2.0** (see [`LICENSE`](LICENSE)).

- The five modified Resonance Audio scripts remain © Google Inc. under Apache-2.0. Each one carries a modification notice, and changes are marked `PATCH`.
- New files are © 2026 Christian Schlaeffer.

The native plugins bundle third-party code. Their licenses are in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) and [`licenses/`](licenses/).

| Component | Used for | License |
|---|---|---|
| [Resonance Audio](https://github.com/resonance-audio/resonance-audio) (Google) | Engine, plugin source | Apache-2.0 |
| [Resonance Audio SDK for Unity](https://github.com/resonance-audio/resonance-audio-unity-sdk) (Google) | Base for the modified C# scripts | Apache-2.0 |
| [SADIE binaural database](https://www.york.ac.uk/sadie-project/) (University of York), via Resonance | HRTFs compiled into the plugin | Apache-2.0 |
| [Eigen](https://eigen.tuxfamily.org) ([GitHub mirror](https://github.com/eigenteam/eigen-git-mirror)) | Linear algebra (header-only) | MPL-2.0 |
| [PFFFT](https://github.com/marton78/pffft) (J. Pommier, H. Ayguen, D. Mambro; FFTPACK by UCAR) | FFT | BSD-style (FFTPACK) |
| [libogg](https://github.com/xiph/ogg) 1.3.5, [libvorbis](https://github.com/xiph/vorbis) 1.3.7 (Xiph.Org) | Soundfield recorder | BSD-3-Clause |
| [Unity NativeAudioPlugins](https://github.com/Unity-Technologies/NativeAudioPlugins) | Native audio plugin SDK header | MIT |
| LLVM libc++ / libc++abi / libunwind (via zig) | C++ runtime, statically linked | Apache-2.0 WITH LLVM-exception |
| mingw-w64 runtime (via zig, Windows only) | C runtime startup code | ZPL-2.1 and others |
| [googletest](https://github.com/google/googletest) | Native unit tests only (not distributed) | BSD-3-Clause |
| [zig](https://ziglang.org/) | Cross-compiler toolchain (not distributed) | MIT |
