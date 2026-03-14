using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Attaches a stable GUID to every GameObject so we can track it across sessions.
    /// Hidden from inspector and Add Component menus — managed automatically.
    ///
    /// Maintains a static cache (guid → component) so Find() is O(1) instead of O(n).
    /// The cache is populated in Awake (fires after domain reload too) and cleared in OnDestroy.
    /// </summary>
    [ExecuteInEditMode]
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public class StableGuid : MonoBehaviour
    {
        [SerializeField, HideInInspector]
        private string _guid = "";

        // O(1) lookup cache — populated by Awake, cleared by OnDestroy
        private static readonly Dictionary<string, StableGuid> _cache
            = new Dictionary<string, StableGuid>();

        public string Guid
        {
            get
            {
                if (string.IsNullOrEmpty(_guid))
                {
                    _guid = System.Guid.NewGuid().ToString("N");
                    _cache[_guid] = this;
                }
                return _guid;
            }
        }

        private void Awake()
        {
            if (string.IsNullOrEmpty(_guid))
                _guid = System.Guid.NewGuid().ToString("N");
            _cache[_guid] = this;
        }

        private void OnDestroy()
        {
            if (!string.IsNullOrEmpty(_guid) &&
                _cache.TryGetValue(_guid, out var cached) && cached == this)
                _cache.Remove(_guid);
        }

#if UNITY_EDITOR
        /// <summary>Gets existing GUID or adds the component and creates one.</summary>
        public static string GetOrCreate(GameObject go)
        {
            var sg = go.GetComponent<StableGuid>();
            if (sg == null)
            {
                sg = Undo.AddComponent<StableGuid>(go);
                EditorUtility.SetDirty(go);
            }
            return sg.Guid;
        }

        /// <summary>Returns the GUID string, or null if the component doesn't exist.</summary>
        public static string Get(GameObject go)
        {
            var sg = go.GetComponent<StableGuid>();
            return sg != null ? sg.Guid : null;
        }

        /// <summary>
        /// Finds a GameObject by its stable GUID. O(1) via cache; falls back to linear
        /// scan only after domain reload (before the first Awake fires on cached objects).
        /// </summary>
        public static GameObject Find(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;

            // Fast path
            if (_cache.TryGetValue(guid, out var sg) && sg != null && sg.gameObject != null)
                return sg.gameObject;

            // Slow path (post-domain-reload warm-up) — also repairs the cache
#if UNITY_2023_1_OR_NEWER
            foreach (var s in UnityEngine.Object.FindObjectsByType<StableGuid>(FindObjectsSortMode.None))
#else
            foreach (var s in UnityEngine.Object.FindObjectsOfType<StableGuid>())
#endif
            {
                _cache[s.Guid] = s;   // repair all, not just the one we need
                if (s.Guid == guid) return s.gameObject;
            }
            return null;
        }
#endif
    }
}
