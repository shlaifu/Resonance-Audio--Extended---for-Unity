// Copyright 2026 Christian Schlaeffer
// SPDX-License-Identifier: Apache-2.0

using UnityEngine;

/// <summary>
/// Baked output of a ResonanceReverbGrid: one Resonance reverb sample (9 octave-band RT60s plus
/// the estimated proxy room used for early reflections) per point of an axis-aligned world-space
/// grid. Filled by ResonanceReverbGridBaker in the editor, read at runtime by
/// ResonanceReverbGrid, which interpolates between the 8 points surrounding the listener.
///
/// Stored as flat arrays (Unity can't serialize multidimensional ones). Point (x, y, z) lives at
/// index x + countX * (y + countY * z); RT60s and proxy materials are strided per point.
/// </summary>
public class ResonanceReverbGridData : ScriptableObject
{
    /// Resonance's number of reverb octave bands (kNumReverbOctaveBands in the native engine).
    public const int BandCount = 9;

    /// Proxy room walls per point, in Resonance's order: left, right, floor, ceiling, front, back.
    public const int WallCount = 6;

    [Tooltip("World position of grid point (0, 0, 0) - the box's minimum corner at bake time.")]
    public Vector3 origin;
    [Tooltip("Distance between neighbouring grid points, in metres, on every axis.")]
    public float spacing = 3f;
    [Tooltip("Number of grid points along X, Y and Z.")]
    public Vector3Int counts;

    [Tooltip("False for points the baker skipped (inside geometry, or no floor below). Ignored when interpolating.")]
    public bool[] valid;
    public float[] rt60s;
    public Vector3[] proxyPositions;
    public Quaternion[] proxyRotations;
    public Vector3[] proxySizes;
    public byte[] proxyMaterials;

    [Tooltip("Per point, 0-1: how much of the sky (upper hemisphere) is blocked by geometry. 1 = indoors, 0 = open field. Scales reverb gain at runtime - Resonance's own bake only measures how fast energy decays, never how much comes back, so without this an open space gets full-level reverb.")]
    public float[] enclosure;
    [Tooltip("Per point: physically plausible upper bound on RT60 in seconds (Eyring, from mean free path and the fraction of rays escaping to open air). Baked RT60s are clamped to it at runtime - sparse late echoes in open space otherwise fit absurdly long decays.")]
    public float[] rt60Cap;

    public int PointCount => counts.x * counts.y * counts.z;
    public bool IsBaked => valid != null && valid.Length == PointCount && PointCount > 0;

    /// False for assets baked before openness existed - run "Bake Openness Only" from the grid's
    /// context menu to add it without redoing the slow reverb bake.
    public bool HasOpenness => enclosure != null && enclosure.Length == PointCount &&
                               rt60Cap != null && rt60Cap.Length == PointCount;

    public int Index(int x, int y, int z) => x + counts.x * (y + counts.y * z);
    public Vector3 PointPosition(int x, int y, int z) => origin + new Vector3(x, y, z) * spacing;

    /// <summary>(Re)allocates every array for a new bake. All points start out invalid.</summary>
    public void Allocate(Vector3 newOrigin, float newSpacing, Vector3Int newCounts)
    {
        origin = newOrigin;
        spacing = newSpacing;
        counts = newCounts;

        int n = PointCount;
        valid = new bool[n];
        rt60s = new float[n * BandCount];
        proxyPositions = new Vector3[n];
        proxyRotations = new Quaternion[n];
        proxySizes = new Vector3[n];
        proxyMaterials = new byte[n * WallCount];
        AllocateOpenness();
        for (int i = 0; i < n; i++) proxyRotations[i] = Quaternion.identity;
    }

    /// <summary>(Re)allocates only the openness arrays - used by the openness-only bake.</summary>
    public void AllocateOpenness()
    {
        int n = PointCount;
        enclosure = new float[n];
        rt60Cap = new float[n];
        for (int i = 0; i < n; i++) { enclosure[i] = 1f; rt60Cap[i] = float.MaxValue; }
    }

    /// <summary>Mean RT60 across all bands at a point - only used for gizmo colouring.</summary>
    public float MeanRt60(int index)
    {
        float sum = 0f;
        int offset = index * BandCount;
        float cap = HasOpenness ? rt60Cap[index] : float.MaxValue;
        for (int b = 0; b < BandCount; b++) sum += Mathf.Min(rt60s[offset + b], cap);
        return sum / BandCount;
    }
}
