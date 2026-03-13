using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Attaches a stable GUID to every GameObject so we can track it across sessions.
    /// Hidden from inspector and Add Component menus — managed automatically.
    /// </summary>
    [ExecuteInEditMode]
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public class StableGuid : MonoBehaviour
    {
        [SerializeField, HideInInspector]
        private string _guid = "";

        public string Guid
        {
            get
            {
                if (string.IsNullOrEmpty(_guid))
                    _guid = System.Guid.NewGuid().ToString("N");
                return _guid;
            }
        }

        private void Awake()
        {
            if (string.IsNullOrEmpty(_guid))
                _guid = System.Guid.NewGuid().ToString("N");
        }

        // ──────────────────────────────────────────────
        //  Static helpers (Editor-only)
        // ──────────────────────────────────────────────

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

        /// <summary>Finds a GameObject by its stable GUID in the loaded scenes.</summary>
        public static GameObject Find(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
#if UNITY_2023_1_OR_NEWER
            foreach (var sg in FindObjectsByType<StableGuid>(FindObjectsSortMode.None))
#else
            foreach (var sg in FindObjectsOfType<StableGuid>())
#endif
                if (sg.Guid == guid) return sg.gameObject;
            return null;
        }
#endif
    }
}
