// Copyright 2017 Google Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Modified in 2026 by Christian Schlaeffer (listener re-resolution, volumetric occlusion, UpdateReverbProbe(probe, reflectivity), bake functions loaded from resonanceaudiobake, PushListenerTransform).
// Changes are marked "PATCH" or described in NOTICE.

using UnityEngine;
using UnityEngine.Audio;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;


// Resonance Audio supports Unity 2017.1 or newer.
#if !UNITY_2017_1_OR_NEWER
  #error Resonance Audio SDK requires Unity 2017.1 or newer.
#endif  // !UNITY_2017_1_OR_NEWER

/// This is the main Resonance Audio class that communicates with the native code implementation of
/// the audio system. Native functions of the system can only be called through this class to
/// preserve the internal system functionality. Public function calls are *not* thread-safe.
public static class ResonanceAudio {
  /// Audio listener transform.
  // PATCH (ResonanceReverbGrid): the original cached the first AudioListener it ever found and
  // never looked again. The native side always uses Unity's live listener, so if that first
  // one was a menu/spawn camera, got disabled, or the player camera was created later, every
  // C#-side distance (occlusion, air absorption, reverb grid sampling, live reflection room)
  // was measured from a stale point - e.g. near sounds treated as far and vice versa. Now
  // re-resolves whenever the cached listener is destroyed, disabled or inactive.
  public static Transform ListenerTransform {
    get {
      if (cachedListener == null || !cachedListener.isActiveAndEnabled) {
        cachedListener = null;
        listenerTransform = null;
        foreach (var listener in GameObject.FindObjectsByType<AudioListener>(FindObjectsSortMode.None)) {
          if (listener.isActiveAndEnabled) {
            cachedListener = listener;
            listenerTransform = listener.transform;
            break;
          }
        }
      }
      return listenerTransform;
    }
  }
  private static Transform listenerTransform = null;
  private static AudioListener cachedListener = null;

#if UNITY_EDITOR
  /// Default audio mixer group of the renderer.
  public static AudioMixerGroup MixerGroup {
    get {
      if (mixerGroup == null) {
        AudioMixer mixer = (Resources.Load("ResonanceAudioMixer") as AudioMixer);
        if (mixer != null) {
          mixerGroup = mixer.FindMatchingGroups("Master")[0];
        }
      }
      return mixerGroup;
    }
  }
  private static AudioMixerGroup mixerGroup = null;
#endif  // UNITY_EDITOR

  /// Updates the audio listener.
  /// @note This should only be called from the main Unity thread.
  public static void UpdateAudioListener(ResonanceAudioListener listener) {
    occlusionMaskValue = listener.occlusionMask.value;
    SetListenerGain(ConvertAmplitudeFromDb(listener.globalGainDb));
    SetListenerStereoSpeakerMode(listener.stereoSpeakerModeEnabled);
  }

  /// Disables the room effects.
  /// @note This should only be called from the main Unity thread.
  public static void DisableRoomEffects() {
    // Set the room properties to null, which will effectively disable the room effects.
    SetRoomProperties(IntPtr.Zero, null);
    if (roomPropertiesPtr != IntPtr.Zero) {
      // Free up the unmanaged memory.
      Marshal.FreeHGlobal(roomPropertiesPtr);
      roomPropertiesPtr = IntPtr.Zero;
    }
  }

  /// Updates the room effects of the environment with the given |room|.
  /// @note This should only be called from the main Unity thread.
  public static void UpdateRoom(ResonanceAudioRoom room) {
    if (roomPropertiesPtr == IntPtr.Zero) {
      // Allocate the unmanaged memory only once.
      roomPropertiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf(roomProperties));
    }
    UpdateRoomProperties(room);
    Marshal.StructureToPtr(roomProperties, roomPropertiesPtr, false);
    SetRoomProperties(roomPropertiesPtr, null);
    Marshal.DestroyStructure(roomPropertiesPtr, typeof(RoomProperties));
  }

  /// Updates the room effects of the environment with given |reverbProbe|.
  /// @note This should only be called from the main Unity thread.
  public static void UpdateReverbProbe(ResonanceAudioReverbProbe reverbPobe) {
    UpdateReverbProbe(reverbPobe, 1.0f);
  }

  // PATCH (ResonanceReverbGrid): same as above, but with an early-reflection reflectivity
  // multiplier (1 = unchanged). Rooms always had this; probes were hardwired to 1. The reverb
  // grid fades reflections out as the sky opens up - see ResonanceReverbGrid.
  public static void UpdateReverbProbe(ResonanceAudioReverbProbe reverbPobe, float reflectivity) {
    if (roomPropertiesPtr == IntPtr.Zero) {
      // Allocate the unmanaged memory only once.
      roomPropertiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf(roomProperties));
    }
    UpdateRoomProperties(reverbPobe, reflectivity);
    Marshal.StructureToPtr(roomProperties, roomPropertiesPtr, false);
    SetRoomProperties(roomPropertiesPtr, reverbPobe.rt60s);
    Marshal.DestroyStructure(roomPropertiesPtr, typeof(RoomProperties));
  }

  /// Starts soundfield recording.
  /// @note This should only be called from the main Unity thread.
  public static bool StartRecording() {
#if UNITY_EDITOR
    return StartSoundfieldRecorder();
#else
    return false;
#endif  // UNITY_EDITOR
  }

  /// Stops soundfield recording and saves it into target file path.
  /// @note This should only be called from the main Unity thread.
  public static bool StopRecordingAndSaveToFile(string filePath, bool seamless) {
#if UNITY_EDITOR
    return StopSoundfieldRecorderAndWriteToFile(filePath, seamless);
#else
    return false;
#endif  // UNITY_EDITOR
  }

  /// Initializes the reverb computer.
  /// @note This should only be called from the main Unity thread.
  public static void InitializeReverbComputer(float[] vertices, int[] triangles,
                                              int[] materialIndices, float scatteringCoefficient) {
#if UNITY_EDITOR
    InitializeReverbComputer(vertices.Length / 3, triangles.Length / 3, vertices, triangles,
                             materialIndices, scatteringCoefficient);
#endif  // UNITY_EDITOR
  }

  /// Computes the RT60s and proxy room properties.
  /// @note This should only be called from the main Unity thread.
  public static bool ComputeRt60sAndProxyRoom(ResonanceAudioReverbProbe reverbProbe,
                                              int totalNumPaths, int numPathsPerBatch, int maxDepth,
                                              float energyThreshold, float listenerSphereRadius) {
#if UNITY_EDITOR
    Vector3 reverbProbePosition = reverbProbe.transform.position;
    roomPosition[0] = reverbProbePosition.x;
    roomPosition[1] = reverbProbePosition.y;
    roomPosition[2] = reverbProbePosition.z;
    IntPtr proxyRoomPropertiesPtr =
      Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RoomProperties)));
    float sampleRate = (float) AudioSettings.GetConfiguration().sampleRate;
    int impulseResponseNumSamples = (int) (maxReverbTime * sampleRate);
    if (!ComputeRt60sAndProxyRoom(totalNumPaths, numPathsPerBatch, maxDepth, energyThreshold,
                                  roomPosition, listenerSphereRadius, sampleRate,
                                  impulseResponseNumSamples, reverbProbe.rt60s,
                                  proxyRoomPropertiesPtr)) {
      return false;
    }

    // Validate the computed RT60s for all frequency bands to the reverb probe.
    for (int band = 0; band < reverbProbe.rt60s.Length; ++band) {
        reverbProbe.rt60s[band] = Mathf.Clamp(reverbProbe.rt60s[band], 0.0f,
                                              ResonanceAudio.maxReverbTime);
    }
    // Copy the estimated proxy room properties to the reverb probe.
    RoomProperties proxyRoomProperties =
        (RoomProperties) Marshal.PtrToStructure(proxyRoomPropertiesPtr, typeof(RoomProperties));
    SetProxyRoomProperties(reverbProbe, proxyRoomProperties);
    Marshal.FreeHGlobal(proxyRoomPropertiesPtr);

    return true;
#else
    return false;
#endif  // UNITY_EDITOR
  }

  /// Computes the occlusion intensity of a given |source| using point source detection.
  public static float ComputeOcclusion(Transform sourceTransform) {
    // PATCH (ResonanceReverbGrid): volumetric occlusion. The original cast ONE ray to the
    // source's exact position, so a pillar or a wall corner clipping that single line fully
    // occluded the source, although most of its sound bends around such an edge. Now casts
    // ResonanceOcclusion.RayCount rays to points spread over a disc of ResonanceOcclusion
    // SourceRadius around the source (facing the listener) and returns the AVERAGE occluder
    // count - fractional when only part of the source is hidden. Without a ResonanceOcclusion
    // component this is exactly the old single-ray behaviour, minus trigger colliders, which
    // are never acoustic walls.
    if (ListenerTransform == null) {
      return 0.0f;
    }
    Vector3 listenerPosition = listenerTransform.position;
    Vector3 sourcePosition = sourceTransform.position;
    Vector3 toSource = sourcePosition - listenerPosition;
    if (toSource.sqrMagnitude < 1e-6f) {
      return 0.0f;
    }

    int rayCount = Mathf.Max(1, ResonanceOcclusion.RayCount);
    float radius = ResonanceOcclusion.SourceRadius;
    Vector3 forward = toSource.normalized;
    Vector3 u = Vector3.Cross(forward, Mathf.Abs(forward.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
    Vector3 v = Vector3.Cross(forward, u);

    float total = 0.0f;
    for (int r = 0; r < rayCount; ++r) {
      Vector3 target = sourcePosition;
      if (r > 0 && radius > 0.0f) {
        float angle = 2.0f * Mathf.PI * (r - 1) / (rayCount - 1);
        Vector3 offsetDirection = Mathf.Cos(angle) * u + Mathf.Sin(angle) * v;
        // Keep sample points in the open air around the source: a footstep source sits on the
        // floor, so half the disc would otherwise lie underground and every ray to it would
        // "hit" that floor - making any ground-level source partly occluded even in plain view.
        float reach = radius;
        if (Physics.Raycast(sourcePosition, offsetDirection, out RaycastHit blocker, radius,
                            occlusionMaskValue, QueryTriggerInteraction.Ignore)) {
          reach = Mathf.Max(0.0f, blocker.distance - 0.05f);
        }
        target += reach * offsetDirection;
      }
      total += CountOccluders(listenerPosition, target, sourceTransform);
    }
    return total / rayCount;
  }

  // Hits this close to the far end of an occlusion ray are the surface the source is resting
  // on or touching (a floor under a footstep, a wall behind a radio), not something between.
  private const float occlusionEndTolerance = 0.15f;

  // Occluders on one listener->target line. Skips the listener/source themselves, triggers,
  // and hits within ResonanceOcclusion.IgnoreRadius of either end (0 by default).
  private static float CountOccluders(Vector3 from, Vector3 to, Transform sourceTransform) {
    Vector3 direction = to - from;
    float distance = direction.magnitude;
    float ignoreRadius = ResonanceOcclusion.IgnoreRadius;
    int numHits = Physics.RaycastNonAlloc(from, direction, occlusionHits, distance,
                                          occlusionMaskValue, QueryTriggerInteraction.Ignore);
    float count = 0.0f;
    for (int i = 0; i < numHits; ++i) {
      if (occlusionHits[i].transform == listenerTransform ||
          occlusionHits[i].transform == sourceTransform) {
        continue;
      }
      float hitDistance = occlusionHits[i].distance;
      if (hitDistance < ignoreRadius ||
          hitDistance > distance - Mathf.Max(ignoreRadius, occlusionEndTolerance)) {
        continue;
      }
      count += 1.0f;
    }
    return count;
  }

  /// Converts given |db| value to its amplitude equivalent where 'dB = 20 * log10(amplitude)'.
  public static float ConvertAmplitudeFromDb(float db) {
    return Mathf.Pow(10.0f, 0.05f * db);
  }

  /// Generates a set of points to draw a 2D polar pattern.
  public static Vector2[] Generate2dPolarPattern(float alpha, float order, int resolution) {
    Vector2[] points = new Vector2[resolution];
    float interval = 2.0f * Mathf.PI / resolution;
    for (int i = 0; i < resolution; ++i) {
      float theta = i * interval;
      // Magnitude |r| for |theta| in radians.
      float r = Mathf.Pow(Mathf.Abs((1 - alpha) + alpha * Mathf.Cos(theta)), order);
      points[i] = new Vector2(r * Mathf.Sin(theta), r * Mathf.Cos(theta));
    }
    return points;
  }

  /// Listener directivity GUI color.
  public static readonly Color listenerDirectivityColor = 0.65f * Color.magenta;

  /// Source directivity GUI color.
  public static readonly Color sourceDirectivityColor = 0.65f * Color.blue;

#if UNITY_EDITOR && UNITY_2017_2_OR_NEWER
  /// Spatializer plugin name.
  public const string spatializerPluginName = "Resonance Audio";
#endif  // UNITY_EDITOR && UNITY_2017_2_OR_NEWER

  /// Minimum distance threshold between |minDistance| and |maxDistance|.
  public const float distanceEpsilon = 0.01f;

  /// Max distance limit that can be set for volume rolloff.
  public const float maxDistanceLimit = 1000000.0f;

  /// Min distance limit that can be set for volume rolloff.
  public const float minDistanceLimit = 990099.0f;

  /// Maximum allowed gain value in decibels.
  public const float maxGainDb = 24.0f;

  /// Minimum allowed gain value in decibels.
  public const float minGainDb = -24.0f;

  /// Maximum allowed reverb brightness modifier value.
  public const float maxReverbBrightness = 1.0f;

  /// Minimum allowed reverb brightness modifier value.
  public const float minReverbBrightness = -1.0f;

  /// Maximum allowed reverb time modifier value.
  public const float maxReverbTime = 10.0f;

  /// Maximum allowed reflectivity multiplier of a room surface material.
  public const float maxReflectivity = 2.0f;

  /// Maximum allowed number of raycast hits for occlusion computation per source.
  public const int maxNumOcclusionHits = 12;

  /// Source occlusion detection rate in seconds.
  public const float occlusionDetectionInterval = 0.2f;

  [StructLayout(LayoutKind.Sequential)]
  private class RoomProperties {
    // Center position of the room in world space.
    public float positionX;
    public float positionY;
    public float positionZ;

    // Rotation (quaternion) of the room in world space.
    public float rotationX;
    public float rotationY;
    public float rotationZ;
    public float rotationW;

    // Size of the shoebox room in world space.
    public float dimensionsX;
    public float dimensionsY;
    public float dimensionsZ;

    // Material name of each surface of the shoebox room.
    public ResonanceAudioRoomManager.SurfaceMaterial materialLeft;
    public ResonanceAudioRoomManager.SurfaceMaterial materialRight;
    public ResonanceAudioRoomManager.SurfaceMaterial materialBottom;
    public ResonanceAudioRoomManager.SurfaceMaterial materialTop;
    public ResonanceAudioRoomManager.SurfaceMaterial materialFront;
    public ResonanceAudioRoomManager.SurfaceMaterial materialBack;

    // User defined uniform scaling factor for reflectivity. This parameter has no effect when set
    // to 1.0f.
    public float reflectionScalar;

    // User defined reverb tail gain multiplier. This parameter has no effect when set to 0.0f.
    public float reverbGain;

    // Adjusts the reverberation time across all frequency bands. RT60 values are multiplied by this
    // factor. Has no effect when set to 1.0f.
    public float reverbTime;

    // Controls the slope of a line from the lowest to the highest RT60 values (increases high
    // frequency RT60s when positive, decreases when negative). Has no effect when set to 0.0f.
    public float reverbBrightness;
  };

  // Converts given |position| and |rotation| from Unity space to audio space.
  private static void ConvertAudioTransformFromUnity(ref Vector3 position,
                                                     ref Quaternion rotation) {
    transformMatrix = flipZ * Matrix4x4.TRS(position, rotation, Vector3.one) * flipZ;
    position = transformMatrix.GetColumn(3);
    rotation = Quaternion.LookRotation(transformMatrix.GetColumn(2), transformMatrix.GetColumn(1));
  }

  /// Sets computed |proxyRoomProperties| for the given |reverbProbe|. Proxy rooms are estimated by
  /// the ray-tracing engine and passed back to be used in real-time early reflections.
  private static void SetProxyRoomProperties(ResonanceAudioReverbProbe reverbProbe,
                                             RoomProperties proxyRoomProperties) {
    reverbProbe.proxyRoomPosition.x = proxyRoomProperties.positionX;
    reverbProbe.proxyRoomPosition.y = proxyRoomProperties.positionY;
    reverbProbe.proxyRoomPosition.z = proxyRoomProperties.positionZ;
    reverbProbe.proxyRoomRotation.x = proxyRoomProperties.rotationX;
    reverbProbe.proxyRoomRotation.y = proxyRoomProperties.rotationY;
    reverbProbe.proxyRoomRotation.z = proxyRoomProperties.rotationZ;
    reverbProbe.proxyRoomRotation.w = proxyRoomProperties.rotationW;
    reverbProbe.proxyRoomSize.x = proxyRoomProperties.dimensionsX;
    reverbProbe.proxyRoomSize.y = proxyRoomProperties.dimensionsY;
    reverbProbe.proxyRoomSize.z = proxyRoomProperties.dimensionsZ;
    reverbProbe.proxyRoomLeftWall = proxyRoomProperties.materialLeft;
    reverbProbe.proxyRoomRightWall = proxyRoomProperties.materialRight;
    reverbProbe.proxyRoomFloor = proxyRoomProperties.materialBottom;
    reverbProbe.proxyRoomCeiling = proxyRoomProperties.materialTop;
    reverbProbe.proxyRoomBackWall = proxyRoomProperties.materialBack;
    reverbProbe.proxyRoomFrontWall = proxyRoomProperties.materialFront;
  }

  // Updates room properties with the given |room|.
  private static void UpdateRoomProperties(ResonanceAudioRoom room) {
    FillGeometryOfRoomProperties(room.transform.position, room.transform.rotation,
                                 Vector3.Scale(room.transform.lossyScale, room.size));
    FillWallMaterialsOfRoomProperties(room.leftWall, room.rightWall, room.floor, room.ceiling,
                                      room.frontWall, room.backWall);
    FillModifiersOfRoomProperties(room.reverbGainDb, room.reverbTime, room.reverbBrightness,
                                  room.reflectivity);
  }

  // Updates room properties with the given |reverbProbe|.
  private static void UpdateRoomProperties(ResonanceAudioReverbProbe reverbProbe,
                                           float reflectivity) {
    FillGeometryOfRoomProperties(reverbProbe.proxyRoomPosition, reverbProbe.proxyRoomRotation,
                                 reverbProbe.proxyRoomSize);
    FillWallMaterialsOfRoomProperties(reverbProbe.proxyRoomLeftWall, reverbProbe.proxyRoomRightWall,
                                      reverbProbe.proxyRoomFloor, reverbProbe.proxyRoomCeiling,
                                      reverbProbe.proxyRoomFrontWall,
                                      reverbProbe.proxyRoomBackWall);
    // PATCH (ResonanceReverbGrid): reflectivity now comes from the caller (1 for plain probes).
    FillModifiersOfRoomProperties(reverbProbe.reverbGainDb, reverbProbe.reverbTime,
                                  reverbProbe.reverbBrightness, reflectivity);
  }

  // Fills the geometry part (position, rotation, and dimensions) of the room properties.
  private static void FillGeometryOfRoomProperties(Vector3 position, Quaternion rotation,
                                                   Vector3 scale) {
    ConvertAudioTransformFromUnity(ref position, ref rotation);
    roomProperties.positionX = position.x;
    roomProperties.positionY = position.y;
    roomProperties.positionZ = position.z;
    roomProperties.rotationX = rotation.x;
    roomProperties.rotationY = rotation.y;
    roomProperties.rotationZ = rotation.z;
    roomProperties.rotationW = rotation.w;
    roomProperties.dimensionsX = scale.x;
    roomProperties.dimensionsY = scale.y;
    roomProperties.dimensionsZ = scale.z;
  }

  // Fills the wall materials part of the room properties.
  private static void FillWallMaterialsOfRoomProperties(
      ResonanceAudioRoomManager.SurfaceMaterial leftWall,
      ResonanceAudioRoomManager.SurfaceMaterial rightWall,
      ResonanceAudioRoomManager.SurfaceMaterial floor,
      ResonanceAudioRoomManager.SurfaceMaterial ceiling,
      ResonanceAudioRoomManager.SurfaceMaterial frontWall,
      ResonanceAudioRoomManager.SurfaceMaterial backWall) {
    roomProperties.materialLeft = leftWall;
    roomProperties.materialRight = rightWall;
    roomProperties.materialBottom = floor;
    roomProperties.materialTop = ceiling;
    roomProperties.materialFront = frontWall;
    roomProperties.materialBack = backWall;
  }

  // Fills the modifiers part (reverb gain, reverb time, reverb brightness, and reflection scalar)
  // of the room properties.
  private static void FillModifiersOfRoomProperties(float reverbGainDb, float reverbTime,
                                                    float reverbBrightness, float reflectivity) {
    roomProperties.reverbGain = ConvertAmplitudeFromDb(reverbGainDb);
    roomProperties.reverbTime = reverbTime;
    roomProperties.reverbBrightness = reverbBrightness;
    roomProperties.reflectionScalar = reflectivity;
  }

  // Right-handed to left-handed matrix converter (and vice versa).
  private static readonly Matrix4x4 flipZ = Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f));

  // Pre-allocated raycast hit list for occlusion computation.
  private static RaycastHit[] occlusionHits = new RaycastHit[maxNumOcclusionHits];

  // Occlusion layer mask.
  private static int occlusionMaskValue = -1;

#if UNITY_EDITOR
  // Pre-allocated position array for proxy room computation.
  private static float[] roomPosition = new float[3];
#endif  // UNITY_EDITOR

  // Pre-allocated room properties instance for room effects computation.
  private static RoomProperties roomProperties = new RoomProperties();

  // Unmanaged pointer to a room properties struct.
  private static IntPtr roomPropertiesPtr = IntPtr.Zero;

  // 4x4 transformation matrix to be used in transform space conversion.
  private static Matrix4x4 transformMatrix = Matrix4x4.identity;

#if !UNITY_EDITOR && UNITY_IOS
  private const string pluginName = "__Internal";
#else
  private const string pluginName = "audiopluginresonanceaudio";
#endif  // !UNITY_EDITOR && UNITY_IOS

  // Listener handlers.
  [DllImport(pluginName)]
  private static extern void SetListenerGain(float gain);

  [DllImport(pluginName)]
  private static extern void SetListenerStereoSpeakerMode(bool enableStereoSpeakerMode);

  // PATCH (listener pose): Unity stops updating spatializer data for a source that plays without
  // an AudioClip, and stock Resonance takes the listener pose from whichever source it processed
  // last - one clip-less source (e.g. Play On Awake with no clip) freezes the listener for every
  // sound. The rebuilt DLL accepts the pose from script instead; ResonanceListenerSync calls this
  // every frame. Null hands the pose back to the plugin.
#if !UNITY_IOS || UNITY_EDITOR
  [DllImport(pluginName)]
  private static extern void SetListenerTransformFromScript(float[] listenerToWorld);
#endif

  /// Sends the listener pose (Unity listener-to-world matrix, 16 floats column-major) or null.
  /// Returns false if the loaded plugin doesn't have the function (stock or not-rebuilt platform
  /// plugin). On iOS the plugin is linked statically and wasn't rebuilt, so the call is compiled
  /// out there - referencing a missing symbol would fail the Xcode link.
  public static bool PushListenerTransform(float[] listenerToWorld) {
#if UNITY_IOS && !UNITY_EDITOR
    return false;
#else
    try {
      SetListenerTransformFromScript(listenerToWorld);
      return true;
    } catch (EntryPointNotFoundException) {
      return false;
    } catch (DllNotFoundException) {
      return false;
    }
#endif
  }

  // Room handlers.
  [DllImport(pluginName)]
  private static extern void SetRoomProperties(IntPtr roomProperties, float[] rt60s);

#if UNITY_EDITOR
  // Soundfield recorder handlers.
  [DllImport(pluginName)]
  private static extern bool StartSoundfieldRecorder();

  [DllImport(pluginName)]
  private static extern bool StopSoundfieldRecorderAndWriteToFile(string filePath, bool seamless);

  // PATCH (per-source reflections): the rebuilt audiopluginresonanceaudio.dll has
  // no reverb computer (it needs Intel Embree, which isn't cross-compiled). Baking
  // uses the ORIGINAL Resonance DLL instead, copied to Editor-only
  // "resonanceaudiobake.dll". The name must NOT start with "audioplugin": Unity
  // loads every such DLL as a native audio plugin, and two copies would register
  // the same effects twice.
  private const string bakePluginName = "resonanceaudiobake";

  // Reverb computer handlers.
  [DllImport(bakePluginName)]
  private static extern void InitializeReverbComputer(int numVertices, int numTriangles,
                                                      float[] vertices, int[] triangles,
                                                      int[] materialIndices,
                                                      float scatteringCoefficient);

  [DllImport(bakePluginName)]
  private static extern bool ComputeRt60sAndProxyRoom(int totalNumPaths, int numPathsPerBatch,
                                                      int maxDepth, float energyThreshold,
                                                      float[] samplePosition,
                                                      float listenerSphereRadius,
                                                      float samplingRate,
                                                      int impulseResponseNumSamples,
                                                      float[] outputRt60s,
                                                      IntPtr outputProxyRoom);
#endif  // UNITY_EDITOR
}
