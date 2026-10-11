// Copyright 2026 Christian Schlaeffer
// SPDX-License-Identifier: Apache-2.0

using UnityEngine;

/// <summary>
/// A dense, interpolated field of baked Resonance Audio reverb - the Steam-Audio-style probe grid
/// Resonance itself doesn't have. Resonance's own ResonanceAudioReverbProbe applies ONE baked
/// result to a whole box/sphere region with hard switches at region edges; this instead bakes a
/// result at every grid point (see ResonanceReverbGridBaker) and, at runtime, blends the 8 points
/// surrounding the listener, so reverb changes smoothly from corridor to hall.
///
/// Everything audible still goes through Resonance's own engine - its binaural spatializer, its
/// spectral reverb and its proxy-room early reflections. This component only decides WHICH
/// reverb values to hand it: the blended result is written into a hidden, inactive carrier
/// ResonanceAudioReverbProbe and pushed via ResonanceAudio.UpdateReverbProbe, exactly the call a
/// normal probe makes. The native ReverbNode already ramps RT60 changes over ~1 s, so updating
/// at updateInterval is click-free.
///
/// Setup: place this on a GameObject, size the box (Size) around a playable area, then bake with
/// Resonance's own window (ResonanceAudio > Reverb Baking > Bake) - the patched
/// ResonanceAudioReverbComputer bakes every grid in the scene alongside any selected probes.
/// Don't let ordinary ResonanceAudioRoom/ReverbProbe regions overlap a grid: both drive the same
/// single global room-effects state, and whichever updated last wins.
///
/// The box is axis-aligned in world space and centred on this transform's position; rotation
/// and scale are ignored.
/// </summary>
[AddComponentMenu("ResonanceAudio/ResonanceReverbGrid")]
public class ResonanceReverbGrid : MonoBehaviour
{
    [Header("Volume")]
    [Tooltip("World-space size of the axis-aligned box the grid fills, centred on this transform.")]
    public Vector3 size = new Vector3(20f, 4f, 20f);
    [Tooltip("Metres between grid points. Smaller = smoother transitions but longer bakes; point count grows with the cube of 1/spacing.")]
    [Min(0.5f)] public float spacing = 3f;

    [Header("Bake")]
    [Tooltip("Layers used to reject grid points: a point inside a collider on these layers is skipped, and (if Require Floor Below) a point needs a collider on these layers beneath it. Purely Unity physics - the actual reverb ray tracing uses Resonance's Material Map / Reverb Mask setup.")]
    public LayerMask geometryMask = ~0;
    [Tooltip("Skip points with no floor within Max Floor Distance below them - drops points floating outside the level or in open air.")]
    public bool requireFloorBelow = true;
    [Min(0.1f)] public float maxFloorDistance = 4f;
    [Tooltip("Ray paths traced per grid point. Resonance's own probes use 50000; lower bakes faster but gives noisier RT60s.")]
    [Min(1000)] public int pathsPerPoint = 50000;
    [Tooltip("Maximum reflections per ray path (Resonance's default: 100).")]
    [Min(1)] public int maxDepth = 100;

    [Header("Openness (Unity raycasts - fast; also via the component's context menu > Bake Openness Only)")]
    [Tooltip("Rays per grid point for measuring how enclosed it is. Plain Physics.Raycast against Geometry Mask, so even 512 is quick.")]
    [Min(32)] public int opennessRays = 512;
    [Tooltip("A ray that hits nothing within this distance counts as escaping to open air.")]
    [Min(1f)] public float opennessMaxDistance = 60f;
    [Tooltip("Assumed average absorption of the level's surfaces, for the RT60 upper bound. 0.1 is fairly hard (concrete, plaster); raise it for soft, furnished spaces. Only caps reverb - never lengthens it.")]
    [Range(0.01f, 0.9f)] public float surfaceAbsorption = 0.1f;

    [Header("Data")]
    [Tooltip("Baked result. Created next to the scene on first bake if left empty.")]
    public ResonanceReverbGridData data;

    [Header("Runtime")]
    [Tooltip("Seconds between re-sampling the grid. The native reverb ramps changes over ~1 s anyway, so 0.1 s is plenty.")]
    [Min(0f)] public float updateInterval = 0.1f;
    [Tooltip("Ignore surrounding grid points the listener has no line of sight to - stops reverb from the next room leaking through a wall. Costs up to 8 linecasts per update.")]
    public bool visibilityCheck = true;
    [Tooltip("Layers used by the visibility check AND the live reflection room's raycasts. Static level geometry only - exclude the player, NPCs, ragdolls, pickups and other small/moving objects, or every passing NPC becomes a wall and the acoustics flicker.")]
    public LayerMask visibilityMask = ~0;

    [Header("Modifiers (same as on a Resonance reverb probe)")]
    public float reverbGainDb = 0f;
    [Range(ResonanceAudio.minReverbBrightness, ResonanceAudio.maxReverbBrightness)]
    public float reverbBrightness = 0f;
    [Min(0f)] public float reverbTime = 1f;

    [Header("Openness (runtime)")]
    [Tooltip("Reverb gain follows enclosure^this (enclosure = how much sky is blocked, 0-1). 1 = linear amplitude (half the sky open = -6 dB); higher fades reverb out faster as the sky opens. 0 disables openness-based gain.")]
    [Min(0f)] public float enclosureGainExponent = 1f;
    [Tooltip("Below this enclosure, the proxy room's ceiling is set to Transparent - no early reflection from a ceiling that is actually sky.")]
    [Range(0f, 1f)] public float openCeilingThreshold = 0.5f;
    [Tooltip("Early-reflection strength follows enclosure^this. In open space Resonance's proxy room is a big virtual box whose walls send back discrete, late echoes - the 'ghostly' layer. 0 keeps reflections at full strength everywhere.")]
    [Min(0f)] public float reflectionEnclosureExponent = 1.5f;

    [Header("Reverb level from room volume (needs Live Reflection Room)")]
    [Tooltip("Resonance plays reverb at one fixed level whatever the space. Physically, the reverberant level a sound builds up is proportional to RT60 / volume: a huge space with the same decay as a small room sounds far quieter. This scales the reverb by that, using the live reflection box's volume (open sides count as Max Reflection Distance), and only ever attenuates.")]
    public bool scaleLevelWithRoomVolume = true;
    [Tooltip("Volume (m^3) at which a 1 s RT60 plays at Resonance's normal level. ~150 m^3 is a mid-sized room; lower it if indoor reverb ends up too quiet, raise it if small rooms are still too loud.")]
    [Min(1f)] public float referenceRoomVolume = 150f;
    [Tooltip("Never attenuate by more than this from room volume alone.")]
    [Range(-40f, 0f)] public float minVolumeLevelDb = -24f;

    [Header("Early reflections")]
    [Tooltip("Build the early-reflection box live from raycasts around the listener (6 directions x 5 rays per update) instead of blending Resonance's baked proxy rooms. The baked boxes are statistical fits that differ a lot between neighbouring points, and Resonance swaps reflections to a new box within one audio buffer (~20 ms) - the main source of position-to-position jumps. Live boxes follow the actual walls continuously.")]
    public bool liveReflectionRoom = true;
    [Tooltip("Walls further than this (or directions where most rays hit nothing) count as open and get a Transparent surface. Far walls only produce late, discrete echoes, which read as 'ghostly' rather than as room sound.")]
    [Min(1f)] public float maxReflectionDistance = 25f;
    [Tooltip("Live walls are never treated as closer than this. With the per-source reflections plugin every source gets its own reflections, so this only tames very early, strong reflections from a wall right next to the listener. With the stock plugin (listener-centric reflections applied to every source) a wall 0.5 m to one side pulls everything - your own footsteps included - toward that side; 2 m keeps that below the point where it shifts where sounds seem to come from.")]
    [Min(0.5f)] public float minWallDistance = 2f;
    [Tooltip("Surface used for a live wall the baked data calls Transparent (open) but the live raycasts actually hit.")]
    public ResonanceAudioRoomManager.SurfaceMaterial fallbackWallMaterial =
        ResonanceAudioRoomManager.SurfaceMaterial.ConcreteBlockCoarse;

    [Header("Smoothing")]
    [Tooltip("Weight kept by a surrounding grid point the listener can't see (visibility check), instead of dropping it entirely. 0 = old hard cut-off, which flips points in and out as you step past crates and railings. Small values still keep the next room from leaking through a wall.")]
    [Range(0f, 1f)] public float hiddenPointWeight = 0.15f;
    [Tooltip("Seconds over which enclosure (reverb gain, reflection strength, send falloff) and the live reflection box follow the listener. RT60s don't need this - Resonance already ramps them over ~1 s.")]
    [Min(0f)] public float smoothingTime = 0.4f;

    [Header("Source reverb send (needs ResonanceAudioSource on each source)")]
    [Tooltip("How much wetter a source gets with distance, indoors (enclosure 1). The reverb send follows the source's OWN volume rolloff raised to (1 - this): 0 = reverb fades exactly like the direct sound (wet/dry ratio constant), 1 = reverb never fades (Resonance's built-in behaviour - only right deep inside one reverberant room, and the cause of the 'distant rain' wash).")]
    [Range(0f, 1f)] public float enclosedWetGrowth = 0.5f;
    [Tooltip("Same, in the open (enclosure 0). Outdoors a far source's reverb fades almost as fast as its direct sound.")]
    [Range(0f, 1f)] public float openWetGrowth = 0.1f;

    [Header("Debug (written at runtime - read-only)")]
    [Tooltip("Blended enclosure at the listener: 1 indoors, 0 open field.")]
    public float debugEnclosure;
    [Tooltip("Reverb gain actually sent to Resonance (modifier + openness).")]
    public float debugGainDb;
    [Tooltip("Early-reflection reflectivity actually sent.")]
    public float debugReflectivity;
    [Tooltip("Mean RT60 across bands actually sent, after cap and air absorption.")]
    public float debugMeanRt60;
    [Tooltip("Volume of the live reflection box around the listener (m^3).")]
    public float debugRoomVolume;
    [Tooltip("Reverb level change from room volume (dB, included in Debug Gain Db).")]
    public float debugVolumeLevelDb;
    [Tooltip("False means this grid's data has no openness: no RT60 cap and no open-sky gain anywhere. Right-click the component > Bake Openness Only.")]
    public bool debugHasOpenness;

    [Header("Gizmos")]
    public bool drawPoints = true;

    // The grid currently feeding Resonance, if any. With overlapping grids, the one that claimed
    // the listener first keeps it until the listener leaves its box.
    static ResonanceReverbGrid activeGrid;

    ResonanceAudioReverbProbe carrier;
    float nextUpdateTime;
    readonly float[] lastSentRt60s = new float[ResonanceReverbGridData.BandCount];
    bool hasSent;

    // Blended enclosure from the last Sample() (1 when the data predates openness), and its
    // time-smoothed version, which is what everything audible actually uses.
    float sampledEnclosure = 1f;
    float smoothedEnclosure = 1f;
    float lastSentReflectivity = -1f;
    float lastSampleTime;

    // Live reflection box: smoothed distance to the nearest wall in each direction, in Resonance's
    // wall order - left (-x), right (+x), floor (-y), ceiling (+y), back (-z), front (+z).
    static readonly Vector3[] WallAxes =
        { Vector3.left, Vector3.right, Vector3.down, Vector3.up, Vector3.back, Vector3.forward };
    readonly float[] wallDistances = new float[6];
    readonly bool[] wallOpen = new bool[6];
    readonly float[] rayDistanceBuffer = new float[5];
    Vector3 lastSentRoomPosition, lastSentRoomSize;
    int lastSentOpenMask = -1;

    /// <summary>
    /// Reverb send multiplier for a source - called by the patched ResonanceAudioSource every
    /// frame. Resonance's reverb send ignores distance entirely while the direct sound follows
    /// the AudioSource's rolloff, so far sources turn almost purely wet. This makes the send
    /// follow that same rolloff, softened by the wet-growth exponent:
    ///   send = rolloff(d) ^ (1 - wetGrowth)
    /// so the wet/dry ratio still rises with distance (sounds do get more distant-sounding), just
    /// by a controlled amount rather than without limit. 1 when no grid is active.
    /// </summary>
    public static float SourceReverbSend(AudioSource source, Vector3 sourcePosition)
    {
        ResonanceReverbGrid grid = activeGrid;
        if (grid == null || !grid.isActiveAndEnabled || source == null) return 1f;
        Transform listener = ResonanceAudio.ListenerTransform;
        if (listener == null) return 1f;

        float attenuation = DirectAttenuation(source, Vector3.Distance(listener.position, sourcePosition));
        float wetGrowth = Mathf.Lerp(grid.openWetGrowth, grid.enclosedWetGrowth, Mathf.Clamp01(grid.smoothedEnclosure));
        return Mathf.Pow(attenuation, 1f - wetGrowth);
    }

    /// <summary>
    /// The AudioSource's own volume rolloff at distance d, weighted by spatial blend the same way
    /// Resonance weights it natively. Custom rolloff falls back to the logarithmic curve:
    /// AudioSource.GetCustomCurve returns a fresh copy (an allocation) on every call, which is
    /// too costly per source per frame.
    /// </summary>
    static float DirectAttenuation(AudioSource source, float d)
    {
        float min = Mathf.Max(0.01f, source.minDistance);
        float max = Mathf.Max(min + 0.01f, source.maxDistance);
        float attenuation = source.rolloffMode == AudioRolloffMode.Linear
            ? Mathf.Clamp01(1f - (d - min) / (max - min))
            : (d <= min ? 1f : min / d); // Unity's logarithmic rolloff
        float blend = source.spatialBlend;
        return Mathf.Clamp01(1f - blend + blend * attenuation);
    }

    public Bounds WorldBounds => new Bounds(transform.position, size);

    void Update()
    {
        if (Time.unscaledTime < nextUpdateTime) return;
        nextUpdateTime = Time.unscaledTime + updateInterval;

        Transform listener = ResonanceAudio.ListenerTransform;
        if (listener == null || data == null || !data.IsBaked) return;

        Vector3 position = listener.position;
        if (!WorldBounds.Contains(position))
        {
            Release();
            return;
        }
        if (activeGrid != null && activeGrid != this && activeGrid.isActiveAndEnabled &&
            activeGrid.WorldBounds.Contains(position))
            return;

        EnsureCarrier();
        if (!Sample(position)) return; // no usable point around the listener - keep what's playing

        if (activeGrid != this) hasSent = false; // just took over: always push once
        activeGrid = this;

        // First update after taking over snaps; afterwards everything glides.
        float now = Time.unscaledTime;
        float follow = !hasSent || smoothingTime <= 0f
            ? 1f
            : 1f - Mathf.Exp(-(now - lastSampleTime) / smoothingTime);
        lastSampleTime = now;

        smoothedEnclosure = Mathf.Lerp(smoothedEnclosure, sampledEnclosure, follow);
        bool roomChanged = liveReflectionRoom && UpdateLiveReflectionRoom(position, follow);

        float volumeLevelDb = liveReflectionRoom && scaleLevelWithRoomVolume ? VolumeLevelDb() : 0f;
        float gainDb = reverbGainDb + OpennessGainDb(smoothedEnclosure) + volumeLevelDb;
        float reflectivity = reflectionEnclosureExponent <= 0f
            ? 1f
            : Mathf.Pow(Mathf.Clamp01(smoothedEnclosure), reflectionEnclosureExponent);
        if (hasSent && !roomChanged && !HasChanged(gainDb) &&
            Mathf.Abs(reflectivity - lastSentReflectivity) < 0.02f) return;

        carrier.reverbGainDb = gainDb;
        carrier.reverbBrightness = reverbBrightness;
        carrier.reverbTime = reverbTime;
        ResonanceAudio.UpdateReverbProbe(carrier, reflectivity);
        lastSentReflectivity = reflectivity;
        lastSentRoomPosition = carrier.proxyRoomPosition;
        lastSentRoomSize = carrier.proxyRoomSize;

        debugEnclosure = smoothedEnclosure;
        debugVolumeLevelDb = volumeLevelDb;
        debugHasOpenness = data.HasOpenness;
        debugGainDb = gainDb;
        debugReflectivity = reflectivity;
        float rtSum = 0f;
        for (int b = 0; b < carrier.rt60s.Length; b++) rtSum += carrier.rt60s[b];
        debugMeanRt60 = rtSum / carrier.rt60s.Length;

        System.Array.Copy(carrier.rt60s, lastSentRt60s, lastSentRt60s.Length);
        hasSent = true;
    }

    void OnDisable() => Release();

    void OnDestroy()
    {
        if (carrier != null) Destroy(carrier.gameObject);
    }

    void Release()
    {
        if (activeGrid != this) return;
        activeGrid = null;
        hasSent = false;
        ResonanceAudio.DisableRoomEffects();
    }

    // An inactive GameObject never runs the probe's own OnEnable/Update, so it never registers
    // itself with ResonanceAudioRoomManager - it's purely a value container for UpdateReverbProbe.
    void EnsureCarrier()
    {
        if (carrier != null) return;
        var go = new GameObject("ReverbGridCarrier") { hideFlags = HideFlags.HideAndDontSave };
        go.SetActive(false);
        carrier = go.AddComponent<ResonanceAudioReverbProbe>();
    }

    bool HasChanged(float gainDb)
    {
        for (int b = 0; b < lastSentRt60s.Length; b++)
            if (Mathf.Abs(carrier.rt60s[b] - lastSentRt60s[b]) > 0.005f) return true;
        // carrier still holds the last values sent; modifiers may be tweaked live.
        return Mathf.Abs(carrier.reverbGainDb - gainDb) > 0.1f ||
               carrier.reverbBrightness != reverbBrightness || carrier.reverbTime != reverbTime;
    }

    /// <summary>
    /// Replaces the carrier's blended proxy room with a world-aligned box measured around the
    /// listener: per direction, 5 rays (the axis plus 4 tilted 20 deg off it) give the median
    /// distance to the nearest surface; fewer than 3 hits within maxReflectionDistance means the
    /// side is open (Transparent). Distances are smoothed with |follow| so walls glide rather
    /// than jump as the listener moves past props. Materials come from the baked dominant point
    /// (Sample already set them) unless the side is open. Returns true if the box differs enough
    /// from what Resonance last received to be worth resending.
    /// </summary>
    bool UpdateLiveReflectionRoom(Vector3 position, float follow)
    {
        int openMask = 0;
        for (int i = 0; i < 6; i++)
        {
            float target = MeasureWall(position, WallAxes[i], out bool open);
            wallDistances[i] = follow >= 1f ? target : Mathf.Lerp(wallDistances[i], target, follow);
            wallOpen[i] = open;
            if (open) openMask |= 1 << i;
        }

        Vector3 min = position - new Vector3(wallDistances[0], wallDistances[2], wallDistances[4]);
        Vector3 max = position + new Vector3(wallDistances[1], wallDistances[3], wallDistances[5]);
        carrier.proxyRoomPosition = 0.5f * (min + max);
        carrier.proxyRoomSize = max - min;
        carrier.proxyRoomRotation = Quaternion.identity;

        carrier.proxyRoomLeftWall = LiveMaterial(carrier.proxyRoomLeftWall, wallOpen[0]);
        carrier.proxyRoomRightWall = LiveMaterial(carrier.proxyRoomRightWall, wallOpen[1]);
        carrier.proxyRoomFloor = LiveMaterial(carrier.proxyRoomFloor, wallOpen[2]);
        carrier.proxyRoomCeiling = LiveMaterial(carrier.proxyRoomCeiling, wallOpen[3]);
        carrier.proxyRoomBackWall = LiveMaterial(carrier.proxyRoomBackWall, wallOpen[4]);
        carrier.proxyRoomFrontWall = LiveMaterial(carrier.proxyRoomFrontWall, wallOpen[5]);

        bool changed = openMask != lastSentOpenMask ||
                       (carrier.proxyRoomPosition - lastSentRoomPosition).sqrMagnitude > 0.04f ||
                       (carrier.proxyRoomSize - lastSentRoomSize).sqrMagnitude > 0.04f;
        if (changed) lastSentOpenMask = openMask;
        return changed;
    }

    /// <summary>
    /// Diffuse-field level relative to the reference room: the steady reverberant energy a
    /// source builds up is proportional to RT60 / V (Sabine: 4W / A with A = 0.161 V / RT60), so
    /// level = 10 log10((RT60 / V) / (1 s / referenceRoomVolume)). Uses the carrier's RT60s
    /// (already capped and air-absorbed) and the live box. Clamped to [minVolumeLevelDb, 0].
    /// </summary>
    float VolumeLevelDb()
    {
        Vector3 s = carrier.proxyRoomSize;
        float volume = Mathf.Max(1f, s.x * s.y * s.z);
        debugRoomVolume = volume;

        float rtSum = 0f;
        for (int b = 0; b < carrier.rt60s.Length; b++) rtSum += carrier.rt60s[b];
        float meanRt60 = rtSum / carrier.rt60s.Length;
        if (meanRt60 <= 0f) return minVolumeLevelDb;

        float db = 10f * Mathf.Log10(meanRt60 * referenceRoomVolume / volume);
        return Mathf.Clamp(db, minVolumeLevelDb, 0f);
    }

    ResonanceAudioRoomManager.SurfaceMaterial LiveMaterial(ResonanceAudioRoomManager.SurfaceMaterial baked, bool open)
    {
        if (open) return ResonanceAudioRoomManager.SurfaceMaterial.Transparent;
        return baked == ResonanceAudioRoomManager.SurfaceMaterial.Transparent ? fallbackWallMaterial : baked;
    }

    float MeasureWall(Vector3 position, Vector3 axis, out bool open)
    {
        // Any vector not parallel to the axis gives a perpendicular basis for the tilted rays.
        Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
        Vector3 v = Vector3.Cross(axis, u);
        const float TiltCos = 0.9397f, TiltSin = 0.3420f; // 20 degrees

        int hits = 0;
        for (int r = 0; r < 5; r++)
        {
            Vector3 dir = r == 0 ? axis
                : TiltCos * axis + TiltSin * (r == 1 ? u : r == 2 ? -u : r == 3 ? v : -v);
            if (!Physics.Raycast(position, dir, out RaycastHit hit, maxReflectionDistance,
                                 visibilityMask, QueryTriggerInteraction.Ignore))
                continue;
            // Distance along the axis, not along the tilted ray - that's the box extent.
            rayDistanceBuffer[hits++] = Mathf.Max(0.5f, hit.distance * Vector3.Dot(dir, axis));
        }

        // Only a side where NOTHING was hit is open (Transparent). Partial hits - a lamp post, the
        // edge of a structure - push the wall outward in proportion to the rays that missed, so it
        // fades away gradually (Resonance's reflection level falls with 1/(distance + 1 m))
        // instead of the side flipping between "concrete" and "open" as the listener moves.
        open = hits == 0;
        if (open) return maxReflectionDistance;

        System.Array.Sort(rayDistanceBuffer, 0, hits); // non-allocating for a float[] range
        float median = rayDistanceBuffer[hits / 2];
        float missFraction = (5 - hits) / 5f;
        float distance = Mathf.Lerp(median, maxReflectionDistance, missFraction);
        return Mathf.Clamp(distance, minWallDistance, maxReflectionDistance);
    }

    // Resonance plays reverb at a fixed default level whatever the bake found (see
    // ComputeReverbPropertiesFromRT60s: gain = kDefaultReverbGain * modifier), so how much of the
    // sky is open has to come in through the gain modifier. Floors at -60 dB, where the native
    // spectral reverb bypasses itself entirely.
    float OpennessGainDb(float enclosure)
    {
        if (enclosureGainExponent <= 0f) return 0f;
        float amplitude = Mathf.Pow(Mathf.Clamp01(enclosure), enclosureGainExponent);
        return Mathf.Max(-60f, 20f * Mathf.Log10(Mathf.Max(amplitude, 1e-3f)));
    }

    /// <summary>
    /// Trilinear blend of the (up to) 8 grid points around position into the carrier. RT60s,
    /// proxy-room position and size are weighted averages; proxy rotation and wall materials
    /// (enums - nothing to average) come from the highest-weight point. Invalid points and, with
    /// visibilityCheck, points hidden behind geometry drop out and the rest are renormalised.
    /// Returns false if no point contributed.
    /// </summary>
    bool Sample(Vector3 position)
    {
        Vector3Int c = data.counts;
        Vector3 local = (position - data.origin) / data.spacing;
        local.x = Mathf.Clamp(local.x, 0f, c.x - 1);
        local.y = Mathf.Clamp(local.y, 0f, c.y - 1);
        local.z = Mathf.Clamp(local.z, 0f, c.z - 1);

        int x0 = Mathf.Min((int)local.x, c.x - 1), x1 = Mathf.Min(x0 + 1, c.x - 1);
        int y0 = Mathf.Min((int)local.y, c.y - 1), y1 = Mathf.Min(y0 + 1, c.y - 1);
        int z0 = Mathf.Min((int)local.z, c.z - 1), z1 = Mathf.Min(z0 + 1, c.z - 1);
        float fx = local.x - x0, fy = local.y - y0, fz = local.z - z0;

        float[] rt60s = carrier.rt60s;
        System.Array.Clear(rt60s, 0, rt60s.Length);
        Vector3 proxyPosition = Vector3.zero, proxySize = Vector3.zero;
        float weightSum = 0f, bestWeight = -1f, enclosureSum = 0f;
        int bestIndex = -1;
        bool hasOpenness = data.HasOpenness;

        for (int corner = 0; corner < 8; corner++)
        {
            bool hx = (corner & 1) != 0, hy = (corner & 2) != 0, hz = (corner & 4) != 0;
            float w = (hx ? fx : 1f - fx) * (hy ? fy : 1f - fy) * (hz ? fz : 1f - fz);
            if (w <= 0f) continue;

            int x = hx ? x1 : x0, y = hy ? y1 : y0, z = hz ? z1 : z0;
            int index = data.Index(x, y, z);
            if (!data.valid[index]) continue;

            if (visibilityCheck &&
                Physics.Linecast(position, data.PointPosition(x, y, z), visibilityMask, QueryTriggerInteraction.Ignore))
            {
                // Down-weight rather than drop: a hard cut flips points in and out of the blend
                // as the listener steps past props, which is what made small moves sound huge.
                w *= hiddenPointWeight;
                if (w <= 0f) continue;
            }

            // Clamp per point BEFORE blending, so one open-air point's absurd tail can't drag
            // its indoor neighbours' blend up with it.
            float cap = hasOpenness ? data.rt60Cap[index] : float.MaxValue;
            int offset = index * ResonanceReverbGridData.BandCount;
            for (int b = 0; b < ResonanceReverbGridData.BandCount; b++)
                rt60s[b] += w * Mathf.Min(data.rt60s[offset + b], cap);
            enclosureSum += w * (hasOpenness ? data.enclosure[index] : 1f);
            proxyPosition += w * data.proxyPositions[index];
            proxySize += w * data.proxySizes[index];
            weightSum += w;

            if (w > bestWeight) { bestWeight = w; bestIndex = index; }
        }

        if (weightSum < 1e-5f) return false;

        float inv = 1f / weightSum;
        // Resonance's ray-traced bake has no air absorption, so high bands ring as long as low
        // ones in big spaces - that bright, flat tail is what reads as hiss/rain on transients.
        float airStrength = ResonanceAirAbsorption.ReverbStrength;
        for (int b = 0; b < rt60s.Length; b++)
            rt60s[b] = ResonanceAirAbsorption.ApplyToRt60(rt60s[b] * inv, b, airStrength);
        carrier.proxyRoomPosition = proxyPosition * inv;
        carrier.proxyRoomSize = proxySize * inv;
        carrier.proxyRoomRotation = data.proxyRotations[bestIndex];

        int m = bestIndex * ResonanceReverbGridData.WallCount;
        carrier.proxyRoomLeftWall = (ResonanceAudioRoomManager.SurfaceMaterial)data.proxyMaterials[m + 0];
        carrier.proxyRoomRightWall = (ResonanceAudioRoomManager.SurfaceMaterial)data.proxyMaterials[m + 1];
        carrier.proxyRoomFloor = (ResonanceAudioRoomManager.SurfaceMaterial)data.proxyMaterials[m + 2];
        carrier.proxyRoomCeiling = (ResonanceAudioRoomManager.SurfaceMaterial)data.proxyMaterials[m + 3];
        carrier.proxyRoomFrontWall = (ResonanceAudioRoomManager.SurfaceMaterial)data.proxyMaterials[m + 4];
        carrier.proxyRoomBackWall = (ResonanceAudioRoomManager.SurfaceMaterial)data.proxyMaterials[m + 5];

        sampledEnclosure = enclosureSum * inv;
        // Open sky above: no early reflection off a ceiling that isn't there.
        if (sampledEnclosure < openCeilingThreshold)
            carrier.proxyRoomCeiling = ResonanceAudioRoomManager.SurfaceMaterial.Transparent;
        return true;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireCube(transform.position, size);

        if (!drawPoints || data == null || !data.IsBaked || data.PointCount > 20000) return;

        // Blue = dry/short reverb, red = long reverb (2 s+). Invalid points aren't drawn.
        Vector3 cube = Vector3.one * Mathf.Min(0.2f, data.spacing * 0.1f);
        for (int z = 0; z < data.counts.z; z++)
        for (int y = 0; y < data.counts.y; y++)
        for (int x = 0; x < data.counts.x; x++)
        {
            int i = data.Index(x, y, z);
            if (!data.valid[i]) continue;
            Gizmos.color = Color.Lerp(Color.blue, Color.red, Mathf.Clamp01(data.MeanRt60(i) / 2f));
            Gizmos.DrawCube(data.PointPosition(x, y, z), cube);
        }
    }
#endif
}
