// Copyright 2026 Christian Schlaeffer
// SPDX-License-Identifier: Apache-2.0

using UnityEngine;

/// <summary>
/// Pushes the active AudioListener's pose to the Resonance plugin once per frame, after all
/// camera/controller scripts have moved it.
///
/// Why: stock Resonance copies the listener pose out of each source's spatializer callback and
/// renders every sound from whichever copy came last. Unity does not update that data for a
/// source that is playing without an AudioClip (by design, see Unity issue "spatializer meta data
/// not sent to native plugin if no audioclip is selected"), so a single clip-less playing source -
/// typically Play On Awake on a source that is only used with PlayOneShot - freezes the listener
/// where it was when that source started. Everything is then heard from that old spot.
///
/// Created automatically when play mode starts; nothing to add to the scene. Does nothing (and
/// says so once) with a plugin DLL that lacks SetListenerTransformFromScript.
/// </summary>
[DefaultExecutionOrder(32000)]
[AddComponentMenu("")]
public sealed class ResonanceListenerSync : MonoBehaviour
{
    readonly float[] matrix = new float[16];
    bool pushing;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        // HideInHierarchy only: DontSave objects would survive leaving Play mode in the Editor
        // and pile up. DontDestroyOnLoad objects are cleaned up when Play mode ends.
        var go = new GameObject("ResonanceListenerSync") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        go.AddComponent<ResonanceListenerSync>();
    }

    void LateUpdate()
    {
        Transform listener = ResonanceAudio.ListenerTransform;
        if (listener == null)
        {
            Release();
            return;
        }

        // Scale is dropped on purpose: only position and rotation matter for the listener.
        Matrix4x4 m = Matrix4x4.TRS(listener.position, listener.rotation, Vector3.one);
        for (int i = 0; i < 16; ++i) matrix[i] = m[i]; // Unity's indexer is column-major

        if (!ResonanceAudio.PushListenerTransform(matrix))
        {
            Debug.LogWarning("ResonanceListenerSync: the loaded Resonance plugin has no " +
                             "SetListenerTransformFromScript - using the plugin's own listener pose.");
            enabled = false;
            return;
        }
        pushing = true;
    }

    void Release()
    {
        if (!pushing) return;
        ResonanceAudio.PushListenerTransform(null);
        pushing = false;
    }

    void OnDisable() => Release();
}
