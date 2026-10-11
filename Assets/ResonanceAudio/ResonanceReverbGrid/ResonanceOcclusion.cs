// Copyright 2026 Christian Schlaeffer
// SPDX-License-Identifier: Apache-2.0

using UnityEngine;

/// <summary>
/// Tunes Resonance occlusion so walls sound like walls without swallowing everything behind
/// them. Put ONE of these anywhere in the scene (same singleton pattern as
/// ResonanceAirAbsorption). Without it, the patched Resonance scripts behave like the original
/// (single ray, native filter at full strength, no level drop) apart from ignoring triggers.
///
/// Three parts, all fed from the same occlusion value (the source's Occlusion Intensity times
/// the average number of occluders - see ResonanceAudio.ComputeOcclusion):
///
///  - Volumetric detection: RayCount rays to a disc of SourceRadius around the source instead
///    of one ray to its exact centre. A pillar or wall edge that hides part of the source gives
///    a fractional value instead of full occlusion - a cheap stand-in for sound bending around
///    edges (diffraction), the same idea as Steam Audio's volumetric occlusion.
///
///  - Filter strength: Resonance's native occlusion low-pass is very steep - its pass factor is
///    1/(I+1)^4, so one occluder at full intensity is a ~500 Hz cutoff and two are ~95 Hz,
///    i.e. silence for footsteps. This scales the intensity handed to that filter: at 0.4 one
///    wall is ~2.3 kHz (clearly muffled, still readable) and two are ~750 Hz.
///
///  - Level drop: Resonance's occlusion never lowers the level, so bass passes at full volume.
///    This adds a modest per-occluder attenuation through the source's gain - which natively
///    scales direct sound, reflections and reverb send together - smoothed, since detection
///    only refreshes every 0.2 s.
///
/// A raycast reports at most ONE hit per collider: if several walls share one combined
/// MeshCollider, they count as a single occluder.
/// </summary>
[AddComponentMenu("ResonanceAudio/ResonanceOcclusion")]
public class ResonanceOcclusion : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Rays per source per occlusion update (every 0.2 s): one to the centre, the rest spread around a disc facing the listener. More = smoother partial occlusion around edges.")]
    [Range(1, 16)] public int rayCount = 7;
    [Tooltip("Radius of that disc in metres - roughly how far around an edge sound still reaches you. 0 = old single-ray behaviour.")]
    [Min(0f)] public float sourceRadius = 0.75f;
    [Tooltip("Hits closer than this to the listener or the source are ignored. Leave at 0 when the ResonanceAudioListener's Occlusion Mask contains only level geometry. Only raise it (~0.5) if the mask must include character or weapon colliders.")]
    [Min(0f)] public float ignoreRadius = 0f;

    [Header("Response")]
    [Tooltip("Multiplier on the intensity handed to Resonance's native occlusion low-pass. 1 = Resonance's own (one wall ~500 Hz, two ~95 Hz - very dull). 0.4 = one wall ~2.3 kHz, two ~750 Hz.")]
    [Range(0f, 1f)] public float filterStrength = 0.4f;
    [Tooltip("Level drop per occluder (direct, reflections and reverb send together). Real walls cost more, but games usually keep occluded sounds audible as gameplay information.")]
    [Min(0f)] public float attenuationPerOccluderDb = 4f;
    [Tooltip("Never attenuate more than this in total, however many occluders.")]
    [Min(0f)] public float maxAttenuationDb = 12f;
    [Tooltip("Seconds over which the level drop follows changes - detection only refreshes every 0.2 s, so without this it steps.")]
    [Min(0f)] public float smoothingTime = 0.15f;

    static ResonanceOcclusion instance;

    void OnEnable() => instance = this;

    void OnDisable()
    {
        if (instance == this) instance = null;
    }

    /// <summary>Rays per occlusion check (1 without this component - the original behaviour).</summary>
    public static int RayCount => instance != null ? instance.rayCount : 1;

    /// <summary>Disc radius around the source (0 without this component).</summary>
    public static float SourceRadius => instance != null ? instance.sourceRadius : 0f;

    /// <summary>Ignore radius for ResonanceAudio.ComputeOcclusion (0 without this component).</summary>
    public static float IgnoreRadius => instance != null ? instance.ignoreRadius : 0f;

    /// <summary>Intensity to hand Resonance's native low-pass (unchanged without this component).</summary>
    public static float FilterIntensity(float occlusion) =>
        instance != null ? occlusion * instance.filterStrength : occlusion;

    /// <summary>
    /// Moves |currentDb| toward the level drop for |occlusion| and returns it. Called every frame
    /// per source by the patched ResonanceAudioSource, which keeps the smoothed value.
    /// </summary>
    public static float SmoothGainDb(float currentDb, float occlusion, float deltaTime)
    {
        if (instance == null) return 0f;
        float target = -Mathf.Min(instance.maxAttenuationDb, occlusion * instance.attenuationPerOccluderDb);
        if (instance.smoothingTime <= 0f) return target;
        return Mathf.Lerp(currentDb, target, 1f - Mathf.Exp(-deltaTime / instance.smoothingTime));
    }
}
