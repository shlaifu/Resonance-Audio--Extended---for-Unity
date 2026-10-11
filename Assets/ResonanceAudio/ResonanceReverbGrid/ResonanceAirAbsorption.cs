// Copyright 2026 Christian Schlaeffer
// SPDX-License-Identifier: Apache-2.0

using UnityEngine;

/// <summary>
/// Frequency-dependent distance falloff - air absorbs high frequencies far more than low ones,
/// so distant sounds get duller, not just quieter (what Steam Audio calls air absorption).
/// Put ONE of these anywhere in the scene (the listener is a natural home).
///
/// Direct sound: Resonance has no air absorption of its own, but every source already runs a
/// smoothed one-pole low-pass on its direct path - the occlusion filter (occlusion_node.cc).
/// The patched ResonanceAudioSource folds a distance-derived intensity into that filter, so
/// this needs no native changes: occlusion and air simply stack. Only the direct path is
/// filtered, which is where air absorption matters most; reflections/reverb get theirs below.
///
/// Reverb: ResonanceReverbGrid shortens each octave band's RT60 by the energy air absorbs over
/// the tail's travel time (high bands far more than low ones), so tails turn darker instead of
/// hissing. That applies even without this component, at physical strength (1).
///
/// Model: above ~1 kHz, absorption in dB over distance d is roughly k * f^2 * d
/// (k ~ 1.83e-9 dB/(m*Hz^2), ISO 9613-1 at 20 degC / 50% RH). The cutoff is the frequency that has
/// lost 3 dB, fc = sqrt(3 / (k * strength * d)): ~9 kHz at 20 m, ~4 kHz at 100 m at strength 1.
/// Real air is subtle at game distances - strength 2-4 is a common, deliberate exaggeration.
/// The one-pole slope (6 dB/oct) is gentler than real air above the cutoff, so this
/// under-rather than over-muffles.
/// </summary>
[AddComponentMenu("ResonanceAudio/ResonanceAirAbsorption")]
public class ResonanceAirAbsorption : MonoBehaviour
{
    [Tooltip("1 = physical (20 degC, 50% humidity). Higher exaggerates: 2 halves the distance at which a given dullness is reached. 0 disables.")]
    [Min(0f)] public float strength = 1f;
    [Tooltip("Apply to each source's direct sound (needs a ResonanceAudioSource on it).")]
    public bool applyToDirectSound = true;
    [Tooltip("Apply to the reverb grid's RT60s (high bands decay faster).")]
    public bool applyToReverb = true;
    [Tooltip("Direct sound is never filtered below this, however far the source - keeps far-off sounds intelligible.")]
    [Range(200f, 20000f)] public float minCutoffHz = 1500f;

    [Header("Debug (play mode)")]
    [Tooltip("The listener every C#-side distance is measured from. Should be your player camera's AudioListener - if it's anything else, near and far are being judged from the wrong place.")]
    public Transform debugListener;
    [Tooltip("Drag any source here to watch what air absorption does to it.")]
    public AudioSource debugSource;
    public float debugDistance;
    [Tooltip("Air-absorption cutoff for Debug Source (0 = no filtering at this distance).")]
    public float debugCutoffHz;
    [Tooltip("Total occlusion intensity sent for Debug Source: Resonance's own raycast occlusion combined with air. If this is high on a NEAR source, the raycast occlusion part is muffling it, not air - check the source's occlusion setting and Occlusion Mask on the ResonanceAudioListener (it may be hitting the player's own colliders).")]
    public float debugOcclusionSent;
    [Tooltip("Reverb + reflections send for Debug Source as a level: 0 dB = full room effects, -20 dB = a tenth. Includes the distance falloff (grid wet growth) and the source's Room Effects Send Db trim.")]
    public float debugRoomEffectsSendDb;

    /// <summary>Resonance's own per-octave-band air absorption (energy, per metre) - the table its
    /// shoebox rooms use (platforms/common/room_effects_utils.cc), bands 31.25 Hz ... 8 kHz.</summary>
    public static readonly float[] BandCoefficients =
        { 0.0006f, 0.0006f, 0.0007f, 0.0008f, 0.0010f, 0.0015f, 0.0026f, 0.0060f, 0.0207f };

    const float K = 1.83e-9f;          // dB / (m * Hz^2)
    const float SpeedOfSound = 343f;   // m/s
    const float Ln1e6 = 13.8155f;      // ln(10^6): a 60 dB energy drop

    static ResonanceAirAbsorption instance;
    float sampleRate = 48000f;

    void OnEnable()
    {
        instance = this;
        sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000f;
    }

    void OnDisable()
    {
        if (instance == this) instance = null;
    }

    void Update()
    {
        debugListener = ResonanceAudio.ListenerTransform;
        if (debugSource == null || debugListener == null) return;

        debugDistance = Vector3.Distance(debugListener.position, debugSource.transform.position);
        debugCutoffHz = CutoffForDistance(debugDistance);
        if (debugSource.GetSpatializerFloat(8, out float occlusion)) // 8 = Resonance's Occlusion parameter
            debugOcclusionSent = occlusion;
        if (debugSource.GetSpatializerFloat(2, out float roomEffects)) // 2 = Resonance's RoomEffectsGain
            debugRoomEffectsSendDb = 20f * Mathf.Log10(Mathf.Max(roomEffects, 1e-4f));
    }

    /// <summary>Strength applied to reverb RT60s: this component's if present (0 if it opts out),
    /// otherwise 1 - real air always absorbs, so the physical default stays on.</summary>
    public static float ReverbStrength =>
        instance == null ? 1f : (instance.applyToReverb ? instance.strength : 0f);

    /// <summary>
    /// Applies air absorption to one octave band's RT60: air removes energy at a constant rate
    /// m*c per second, which adds to the room's own decay rate:
    /// 1/RT_total = 1/RT_room + m*c*strength / ln(10^6). Leaves 0 (no reverb) at 0.
    /// </summary>
    public static float ApplyToRt60(float rt60, int band, float strength)
    {
        if (rt60 <= 0f || strength <= 0f) return rt60;
        float airDecayRate = BandCoefficients[band] * SpeedOfSound * strength / Ln1e6;
        return 1f / (1f / rt60 + airDecayRate);
    }

    /// <summary>
    /// Called by the patched ResonanceAudioSource with its own raycast occlusion value; returns
    /// that combined with air absorption for this source's distance. Resonance maps intensity I
    /// to a pass factor 1/(I+1)^4, so two stacked effects multiply as (I1+1)(I2+1) - 1.
    /// </summary>
    public static float CombineWithOcclusion(float occlusion, Vector3 sourcePosition)
    {
        if (instance == null || !instance.applyToDirectSound || instance.strength <= 0f) return occlusion;
        Transform listener = ResonanceAudio.ListenerTransform;
        if (listener == null) return occlusion;

        float air = instance.IntensityForDistance(Vector3.Distance(listener.position, sourcePosition));
        return (occlusion + 1f) * (air + 1f) - 1f;
    }

    /// <summary>Air-absorption cutoff at this distance, or 0 when no filtering applies.</summary>
    float CutoffForDistance(float distance)
    {
        if (distance < 1f || strength <= 0f) return 0f;
        float cutoff = Mathf.Max(minCutoffHz, Mathf.Sqrt(3f / (K * strength * distance)));
        return cutoff >= 0.45f * sampleRate ? 0f : cutoff; // above the audible/representable range
    }

    float IntensityForDistance(float distance)
    {
        float cutoff = CutoffForDistance(distance);
        if (cutoff <= 0f) return 0f;

        // Resonance's filter is y[n] = a*y[n-1] + (1-a)*x[n]; a = exp(-2*pi*fc/fs) puts its
        // -3 dB point at fc. Inverting its intensity mapping a = 1 - 1/(I+1)^4 (full
        // directivity) gives I = (1/(1-a))^(1/4) - 1.
        float a = Mathf.Exp(-2f * Mathf.PI * cutoff / sampleRate);
        return Mathf.Pow(1f / (1f - a), 0.25f) - 1f;
    }
}
