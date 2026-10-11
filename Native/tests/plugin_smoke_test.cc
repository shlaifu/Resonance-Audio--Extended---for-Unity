// Loads the Resonance Unity plugin the way Unity does (dlopen + UnityGetAudioEffectDefinitions)
// and drives renderer + spatializer callbacks with audio.
// Scenario: listener at the origin facing +z (Unity), source A playing noise 2 m to the RIGHT.
// Source B plays silence but carries a STALE listener matrix (listener 4 m further right), as
// Unity does for a source playing without a clip. B is processed after A, so stock Resonance
// takes B's stale pose: A is then rendered from a head at x=4, i.e. on the LEFT.
#include <dlfcn.h>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>
#include "NativeCode/AudioPluginInterface.h"
#include "platforms/common/room_properties.h"

typedef int (*GetDefs)(UnityAudioEffectDefinition***);
typedef void (*SetPose)(const float*);
typedef void (*SetRoom)(vraudio::RoomProperties*, float*);

static void Translation(float x, float y, float z, float* m) {  // column-major TRS, no rotation
  std::memset(m, 0, 16 * sizeof(float));
  m[0] = m[5] = m[10] = m[15] = 1.0f;
  m[12] = x; m[13] = y; m[14] = z;
}

struct Instance {
  UnityAudioEffectState state{};
  UnityAudioSpatializerData sdata{};
  int dummy = 0;
};

static void Init(Instance* in, bool spatializer) {
  in->state.structsize = sizeof(UnityAudioEffectState);
  in->state.samplerate = 48000;
  in->state.dspbuffersize = 1024;
  in->state.hostapiversion = UNITY_AUDIO_PLUGIN_API_VERSION;
  in->state.internal = &in->dummy;
  if (spatializer) {
    in->state.spatializerdata = &in->sdata;
    in->sdata.spatialblend = 1.0f;
  }
}

// Returns right/left energy ratio (dB) of the renderer output over the last blocks.
static double total_energy = 0;
static double Run(void* lib, bool push_pose, bool room = false) {
  UnityAudioEffectDefinition** defs = nullptr;
  int n = ((GetDefs)dlsym(lib, "UnityGetAudioEffectDefinitions"))(&defs);
  if (n != 2) { printf("expected 2 effects, got %d\n", n); return NAN; }
  UnityAudioEffectDefinition* renderer = defs[0];
  UnityAudioEffectDefinition* spat = defs[1];
  SetPose set_pose = (SetPose)dlsym(lib, "SetListenerTransformFromScript");

  Instance r, a, b;
  Init(&r, false); Init(&a, true); Init(&b, true);
  renderer->create(&r.state);
  spat->create(&a.state);
  spat->create(&b.state);

  float listener_world[16], world_to_listener[16], stale[16];
  Translation(0, 1.7f, 0, listener_world);
  Translation(0, -1.7f, 0, world_to_listener);   // inverse of the listener TRS
  Translation(-4, -1.7f, 0, stale);              // listener believed at x = 4
  std::memcpy(a.sdata.listenermatrix, world_to_listener, sizeof stale);
  Translation(2, 1.7f, 0, a.sdata.sourcematrix);  // 2 m to the right
  std::memcpy(b.sdata.listenermatrix, stale, sizeof stale);
  Translation(4, 1.7f, 0, b.sdata.sourcematrix);
  if (set_pose) set_pose(push_pose ? listener_world : nullptr);
  SetRoom set_room = (SetRoom)dlsym(lib, "SetRoomProperties");
  vraudio::RoomProperties props;
  if (room) {
    props.position[1] = 2.0f;  // Resonance space: right-handed, but y/x unchanged
    props.dimensions[0] = 10.0f; props.dimensions[1] = 4.0f; props.dimensions[2] = 12.0f;
    for (int i = 0; i < 6; ++i) props.material_names[i] = vraudio::kConcreteBlockCoarse;
    props.reflection_scalar = 1.0f; props.reverb_gain = 1.0f; props.reverb_time = 1.0f;
  }
  set_room(room ? &props : nullptr, nullptr);

  const unsigned frames = 1024;
  std::vector<float> tone(2 * frames), silence(2 * frames, 0.0f), out(2 * frames), mix(2 * frames);
  double left = 0, right = 0; bool finite = true; size_t phase = 0;
  for (int block = 0; block < 60; ++block) {
    for (unsigned i = 0; i < frames; ++i, ++phase)
      { static unsigned seed = 12345u; seed = seed * 1664525u + 1013904223u;
        tone[2 * i] = tone[2 * i + 1] = 0.3f * ((seed >> 8) / 8388608.0f - 1.0f); }
    spat->process(&a.state, tone.data(), out.data(), frames, 2, 2);
    spat->process(&b.state, silence.data(), out.data(), frames, 2, 2);
    std::fill(silence.begin(), silence.end(), 0.0f);
    renderer->process(&r.state, silence.data(), mix.data(), frames, 2, 2);
    if (set_pose && push_pose) set_pose(listener_world);  // C# does this every frame
    for (float s : mix) finite = finite && std::isfinite(s);
    if (block >= 20) for (unsigned i = 0; i < frames; ++i) {
      left += mix[2 * i] * mix[2 * i]; right += mix[2 * i + 1] * mix[2 * i + 1];
    }
  }
  spat->release(&a.state); spat->release(&b.state); renderer->release(&r.state);
  if (!finite) { printf("non-finite output\n"); return NAN; }
  if (left + right < 1e-6) { printf("silent output\n"); return NAN; }
  total_energy = left + right;
  return 10.0 * std::log10(right / left);
}

int main(int argc, char** argv) {
  void* lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
  if (!lib) { printf("dlopen failed: %s\n", dlerror()); return 1; }
  double stock = Run(lib, false), fixed = Run(lib, true);
  const double dry = total_energy;
  double roomed = Run(lib, true, true);
  const double wet = total_energy;
  printf("right-minus-left level, noise source 2 m to the right:\n");
  printf("  pose from sources (stale clip-less source last): %+.1f dB\n", stock);
  printf("  pose pushed from script:                         %+.1f dB\n", fixed);
  printf("  pose pushed, inside a concrete room:             %+.1f dB, energy %+.1f dB vs dry\n",
         roomed, 10.0 * std::log10(wet / dry));
  bool ok = std::isfinite(stock) && std::isfinite(fixed) && fixed > 3.0 && stock < 0.0 &&
            std::isfinite(roomed) && roomed > 0.0 && wet > dry;
  printf(ok ? "PASS\n" : "FAIL\n");
  return ok ? 0 : 1;
}
