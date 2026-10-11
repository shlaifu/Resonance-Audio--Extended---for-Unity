// Copyright 2026 Christian Schlaeffer
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Bakes every ResonanceReverbGrid in the open scene(s). Not called directly: the patched
/// ResonanceAudioReverbComputer.ComputeReverbForSceneGeometry calls BakeAll() right after it has
/// baked the selected reverb probes - at that point Resonance's native reverb computer has
/// already been initialised with the scene geometry and Material Map from the Reverb Baking
/// window, so each grid point is just one more ComputeRt60sAndProxyRoom call against it.
///
/// That call takes a ResonanceAudioReverbProbe (reads its position, writes RT60s and the proxy
/// room into it), so a single hidden, inactive probe is moved to each grid point in turn and
/// its results copied into the grid's data asset.
/// </summary>
public static class ResonanceReverbGridBaker
{
    // Same values Resonance's own probe bake uses (see ResonanceAudioReverbComputer).
    const int PathsPerBatch = 1000;
    const float EnergyThreshold = 1e-12f;
    const float ListenerSphereRadius = 0.1f;

    // Radius of the "is this point inside geometry" test.
    const float SolidCheckRadius = 0.25f;

    /// <summary>Whether the scene has any grid - the patched Reverb Baking window enables Bake for this even with no probes selected.</summary>
    public static bool HasGridsToBake() =>
        Object.FindObjectsByType<ResonanceReverbGrid>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length > 0;

    /// <summary>Bakes all active grids. Returns false if a bake failed or was cancelled.</summary>
    public static bool BakeAll()
    {
        var grids = Object.FindObjectsByType<ResonanceReverbGrid>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (grids.Length == 0) return true;

        var probeObject = new GameObject("ReverbGridBakeProbe") { hideFlags = HideFlags.HideAndDontSave };
        probeObject.SetActive(false); // never registers with ResonanceAudioRoomManager
        var probe = probeObject.AddComponent<ResonanceAudioReverbProbe>();

        try
        {
            foreach (var grid in grids)
            {
                if (!Bake(grid, probe)) return false;
            }
            return true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Object.DestroyImmediate(probeObject);
            AssetDatabase.SaveAssets();
        }
    }

    static bool Bake(ResonanceReverbGrid grid, ResonanceAudioReverbProbe probe)
    {
        float spacing = Mathf.Max(0.5f, grid.spacing);
        Bounds box = grid.WorldBounds;
        var counts = new Vector3Int(
            Mathf.FloorToInt(box.size.x / spacing) + 1,
            Mathf.FloorToInt(box.size.y / spacing) + 1,
            Mathf.FloorToInt(box.size.z / spacing) + 1);

        // No Undo.RecordObject here on purpose: a dense grid is tens of thousands of array
        // entries, and snapshotting them into the undo stack for a bake nobody undoes is waste.
        ResonanceReverbGridData data = grid.data != null ? grid.data : CreateDataAsset(grid);
        data.Allocate(box.min, spacing, counts);

        int totalPaths = Mathf.Max(PathsPerBatch, grid.pathsPerPoint / PathsPerBatch * PathsPerBatch);
        int pointCount = data.PointCount, validCount = 0, done = 0;

        for (int z = 0; z < counts.z; z++)
        for (int y = 0; y < counts.y; y++)
        for (int x = 0; x < counts.x; x++, done++)
        {
            if (EditorUtility.DisplayCancelableProgressBar(
                    "Baking reverb grid " + grid.name,
                    $"Point {done + 1} / {pointCount}  ({validCount} baked)",
                    (float)done / pointCount))
            {
                Debug.LogWarning($"Reverb grid '{grid.name}' bake cancelled - points not reached yet are left invalid.", grid);
                EditorUtility.SetDirty(data);
                return false;
            }

            Vector3 point = data.PointPosition(x, y, z);
            if (!IsUsablePoint(grid, point)) continue;

            probe.transform.position = point;
            if (!ResonanceAudio.ComputeRt60sAndProxyRoom(probe, totalPaths, PathsPerBatch, grid.maxDepth,
                                                         EnergyThreshold, ListenerSphereRadius))
            {
                Debug.LogError($"Reverb grid '{grid.name}': Resonance failed to compute point {point}.", grid);
                EditorUtility.SetDirty(data);
                return false;
            }

            int index = data.Index(x, y, z);
            Store(data, index, probe);
            ComputeOpenness(grid, data, index, point);
            validCount++;
        }

        EditorUtility.SetDirty(data);
        Debug.Log($"Reverb grid '{grid.name}': baked {validCount} of {pointCount} points.", grid);
        return true;
    }

    /// <summary>
    /// Adds/refreshes openness on an already-baked grid without re-running Resonance's slow
    /// reverb bake - plain Unity raycasts only, so seconds even for large grids. Right-click the
    /// ResonanceReverbGrid component header (or its "⋮" menu) > Bake Openness Only.
    /// </summary>
    [MenuItem("CONTEXT/ResonanceReverbGrid/Bake Openness Only")]
    static void BakeOpennessOnly(MenuCommand command)
    {
        var grid = (ResonanceReverbGrid)command.context;
        var data = grid.data;
        if (data == null || !data.IsBaked)
        {
            Debug.LogWarning($"Reverb grid '{grid.name}' has no baked data yet - run a full bake first.", grid);
            return;
        }

        data.AllocateOpenness();
        try
        {
            int n = data.PointCount, done = 0;
            for (int z = 0; z < data.counts.z; z++)
            for (int y = 0; y < data.counts.y; y++)
            for (int x = 0; x < data.counts.x; x++, done++)
            {
                if ((done & 63) == 0 &&
                    EditorUtility.DisplayCancelableProgressBar("Baking openness " + grid.name,
                        $"Point {done + 1} / {n}", (float)done / n))
                {
                    // Partial openness would be inconsistent - drop it rather than keep half.
                    data.enclosure = null;
                    data.rt60Cap = null;
                    Debug.LogWarning($"Reverb grid '{grid.name}': openness bake cancelled and discarded.", grid);
                    return;
                }

                int index = data.Index(x, y, z);
                if (data.valid[index]) ComputeOpenness(grid, data, index, data.PointPosition(x, y, z));
            }
            Debug.Log($"Reverb grid '{grid.name}': openness baked for {n} points.", grid);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }
    }

    /// <summary>
    /// Median filter over the baked RT60s: each point/band becomes the median of itself and its
    /// valid neighbours (3x3x3) that it has line of sight to (Visibility Mask), so rooms on either
    /// side of a wall don't mix. Resonance only collects energy where rays cross a 10 cm sphere at
    /// the bake point, so low Paths Per Point bakes give noisy, sometimes far too long, decay
    /// fits at individual points; a median removes those outliers without blurring real room
    /// boundaries much. Destructive (overwrites the baked values) - rebake to undo. Repeatable.
    /// </summary>
    [MenuItem("CONTEXT/ResonanceReverbGrid/Smooth Reverb Times")]
    static void SmoothReverbTimes(MenuCommand command)
    {
        var grid = (ResonanceReverbGrid)command.context;
        var data = grid.data;
        if (data == null || !data.IsBaked)
        {
            Debug.LogWarning($"Reverb grid '{grid.name}' has no baked data yet.", grid);
            return;
        }

        const int Bands = ResonanceReverbGridData.BandCount;
        var smoothed = (float[])data.rt60s.Clone();
        var samples = new System.Collections.Generic.List<float>(27);
        var neighbours = new System.Collections.Generic.List<int>(27);
        Vector3Int c = data.counts;

        for (int z = 0; z < c.z; z++)
        for (int y = 0; y < c.y; y++)
        for (int x = 0; x < c.x; x++)
        {
            int index = data.Index(x, y, z);
            if (!data.valid[index]) continue;
            Vector3 p = data.PointPosition(x, y, z);

            neighbours.Clear();
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= c.x || ny >= c.y || nz >= c.z) continue;
                int n = data.Index(nx, ny, nz);
                if (!data.valid[n]) continue;
                if (n != index && Physics.Linecast(p, data.PointPosition(nx, ny, nz), grid.visibilityMask,
                                                   QueryTriggerInteraction.Ignore))
                    continue;
                neighbours.Add(n);
            }

            for (int b = 0; b < Bands; b++)
            {
                samples.Clear();
                for (int i = 0; i < neighbours.Count; i++) samples.Add(data.rt60s[neighbours[i] * Bands + b]);
                samples.Sort();
                smoothed[index * Bands + b] = samples[samples.Count / 2];
            }
        }

        Undo.RecordObject(data, "Smooth Reverb Times");
        data.rt60s = smoothed;
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
        Debug.Log($"Reverb grid '{grid.name}': reverb times smoothed.", grid);
    }

    /// <summary>
    /// Casts opennessRays evenly spread directions from point and derives:
    ///  - enclosure: fraction of UPPER-hemisphere rays that hit something. Only the upper half,
    ///    because every point has a floor - counting it would make an open field look 50% enclosed.
    ///  - rt60Cap: Eyring reverb time for a space with this mean free path, where escaping rays
    ///    (all directions) act as perfect absorption on top of surfaceAbsorption:
    ///    RT60 = 0.161 V / (-S ln(1 - a)), and with mean free path l = 4V/S that is
    ///    0.04025 l / -ln(1 - a). Generous for closed rooms (never shortens a believable indoor
    ///    tail), but collapses to fractions of a second in open air.
    /// </summary>
    static void ComputeOpenness(ResonanceReverbGrid grid, ResonanceReverbGridData data, int index, Vector3 point)
    {
        Vector3[] directions = GetDirections(grid.opennessRays);
        int upperRays = 0, upperHits = 0, hits = 0;
        float hitDistanceSum = 0f;

        for (int i = 0; i < directions.Length; i++)
        {
            Vector3 d = directions[i];
            bool upper = d.y > 0f;
            if (upper) upperRays++;

            if (!Physics.Raycast(point, d, out RaycastHit hit, grid.opennessMaxDistance,
                                 grid.geometryMask, QueryTriggerInteraction.Ignore))
                continue;

            hits++;
            hitDistanceSum += hit.distance;
            if (upper) upperHits++;
        }

        data.enclosure[index] = upperRays > 0 ? (float)upperHits / upperRays : 1f;

        if (hits == 0)
        {
            data.rt60Cap[index] = 0f; // nothing anywhere - no reverb at all
            return;
        }

        float escapeFraction = 1f - (float)hits / directions.Length;
        float meanFreePath = hitDistanceSum / hits;
        float absorption = Mathf.Clamp(escapeFraction + (1f - escapeFraction) * grid.surfaceAbsorption, 0.01f, 0.99f);
        data.rt60Cap[index] = 0.04025f * meanFreePath / -Mathf.Log(1f - absorption);
    }

    // Fibonacci-sphere directions, cached per count - an even spread with no clumping at poles.
    static Vector3[] cachedDirections;
    static Vector3[] GetDirections(int count)
    {
        count = Mathf.Max(32, count);
        if (cachedDirections != null && cachedDirections.Length == count) return cachedDirections;

        cachedDirections = new Vector3[count];
        float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int i = 0; i < count; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / count;
            float r = Mathf.Sqrt(1f - y * y);
            float phi = goldenAngle * i;
            cachedDirections[i] = new Vector3(Mathf.Cos(phi) * r, y, Mathf.Sin(phi) * r);
        }
        return cachedDirections;
    }

    static bool IsUsablePoint(ResonanceReverbGrid grid, Vector3 point)
    {
        if (Physics.CheckSphere(point, SolidCheckRadius, grid.geometryMask, QueryTriggerInteraction.Ignore))
            return false; // inside a wall/prop

        if (grid.requireFloorBelow &&
            !Physics.Raycast(point, Vector3.down, grid.maxFloorDistance, grid.geometryMask, QueryTriggerInteraction.Ignore))
            return false; // floating outside the level

        return true;
    }

    static void Store(ResonanceReverbGridData data, int index, ResonanceAudioReverbProbe probe)
    {
        data.valid[index] = true;
        System.Array.Copy(probe.rt60s, 0, data.rt60s, index * ResonanceReverbGridData.BandCount,
                          ResonanceReverbGridData.BandCount);
        data.proxyPositions[index] = probe.proxyRoomPosition;
        data.proxyRotations[index] = probe.proxyRoomRotation;
        data.proxySizes[index] = probe.proxyRoomSize;

        int m = index * ResonanceReverbGridData.WallCount;
        data.proxyMaterials[m + 0] = (byte)probe.proxyRoomLeftWall;
        data.proxyMaterials[m + 1] = (byte)probe.proxyRoomRightWall;
        data.proxyMaterials[m + 2] = (byte)probe.proxyRoomFloor;
        data.proxyMaterials[m + 3] = (byte)probe.proxyRoomCeiling;
        data.proxyMaterials[m + 4] = (byte)probe.proxyRoomFrontWall;
        data.proxyMaterials[m + 5] = (byte)probe.proxyRoomBackWall;
    }

    static ResonanceReverbGridData CreateDataAsset(ResonanceReverbGrid grid)
    {
        string scenePath = grid.gameObject.scene.path;
        string folder = string.IsNullOrEmpty(scenePath) ? "Assets" : Path.GetDirectoryName(scenePath).Replace('\\', '/');
        string sceneName = string.IsNullOrEmpty(scenePath) ? "Untitled" : Path.GetFileNameWithoutExtension(scenePath);
        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{sceneName}_{grid.name}_ReverbGrid.asset");

        var data = ScriptableObject.CreateInstance<ResonanceReverbGridData>();
        AssetDatabase.CreateAsset(data, path);

        Undo.RecordObject(grid, "Assign Reverb Grid Data");
        grid.data = data;
        EditorUtility.SetDirty(grid);
        EditorSceneManager.MarkSceneDirty(grid.gameObject.scene);
        return data;
    }
}
