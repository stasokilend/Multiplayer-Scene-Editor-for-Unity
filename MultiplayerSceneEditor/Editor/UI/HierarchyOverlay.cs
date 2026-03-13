using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Colours the Unity Hierarchy window rows that remote users have selected —
    /// just like Google Docs shows coloured cursors in the document margin.
    ///
    /// Each row gets:
    ///   • A coloured left-edge bar (2px wide, full-row height)
    ///   • A tinted row background
    ///   • Small initials badges on the far right  e.g.  [V] [A]
    ///   • If the row is locked by a remote user: a 🔒 icon
    /// </summary>
    [InitializeOnLoad]
    public static class HierarchyOverlay
    {
        // guid → list of (displayName, color)
        private static Dictionary<string, List<(string name, Color col)>> _selMap
            = new Dictionary<string, List<(string, Color)>>();

        private static GUIStyle _badgeStyle;
        private static bool     _stylesReady;

        static HierarchyOverlay()
        {
            EditorApplication.hierarchyWindowItemOnGUI  += OnHierarchyItem;
            SceneSyncManager.OnUsersChanged             += RebuildMap;
        }

        // ── Rebuild the selection map whenever users change ───────────────────

        private static void RebuildMap()
        {
            _selMap.Clear();
            foreach (var kv in SceneSyncManager.RemoteUsers)
            {
                var ru = kv.Value;
                if (ru.SelectedGuids == null) continue;
                foreach (var guid in ru.SelectedGuids)
                {
                    if (!_selMap.TryGetValue(guid, out var list))
                    {
                        list = new List<(string, Color)>();
                        _selMap[guid] = list;
                    }
                    list.Add((ru.Info.displayName, ru.UserColor));
                }
            }
        }

        // ── Per-row callback ──────────────────────────────────────────────────

        private static void OnHierarchyItem(int instanceID, Rect rowRect)
        {
            if (SceneSyncManager.Mode == SessionMode.None) return;
            EnsureStyles();

            var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (go == null) return;

            string guid = StableGuid.Get(go);
            if (guid == null) return;

            bool hasUsers  = _selMap.TryGetValue(guid, out var users) && users.Count > 0;
            bool isLocked  = SceneSyncManager.Locks.IsLockedByOther(guid, SceneSyncManager.LocalUser?.userId ?? "");

            if (!hasUsers && !isLocked) return;

            // ── Row tint ──────────────────────────────────────────────────────
            if (hasUsers)
            {
                Color primaryCol = users[0].col;
                Color tint = new Color(primaryCol.r, primaryCol.g, primaryCol.b, 0.12f);
                EditorGUI.DrawRect(rowRect, tint);

                // Left colour bar
                var barRect = new Rect(rowRect.x, rowRect.y, 3f, rowRect.height);
                if (users.Count == 1)
                {
                    EditorGUI.DrawRect(barRect, primaryCol);
                }
                else
                {
                    // Split bar for multiple users
                    float segH = rowRect.height / users.Count;
                    for (int i = 0; i < users.Count; i++)
                        EditorGUI.DrawRect(
                            new Rect(barRect.x, barRect.y + segH * i, barRect.width, segH),
                            users[i].col);
                }
            }

            // ── Badges on the right ───────────────────────────────────────────
            float badgeX = rowRect.xMax - 4f;
            float badgeY = rowRect.y + (rowRect.height - 14f) * 0.5f;

            // Lock icon (leftmost / last)
            if (isLocked)
            {
                string lockOwner = SceneSyncManager.Locks.GetOwner(guid);
                string ownerName = "?";
                if (SceneSyncManager.RemoteUsers.TryGetValue(lockOwner, out var locker))
                    ownerName = locker.Info.displayName;

                var lockRect = new Rect(badgeX - 16, badgeY, 16, 14);
                GUI.Label(lockRect, "🔒",
                    new GUIStyle(EditorStyles.miniLabel) { fontSize = 10, alignment = TextAnchor.MiddleCenter });
                badgeX -= 18f;
            }

            // User badges (right-to-left)
            if (hasUsers)
            {
                for (int i = users.Count - 1; i >= 0; i--)
                {
                    var (name, col) = users[i];
                    string init     = name.Length > 0 ? name[..1].ToUpper() : "?";
                    float  bw       = 16f;
                    var    bRect    = new Rect(badgeX - bw, badgeY, bw, 14f);

                    // Pill background
                    GUI.DrawTexture(bRect, Texture2D.whiteTexture,
                                    ScaleMode.StretchToFill, true, 0,
                                    new Color(col.r * 0.55f, col.g * 0.55f, col.b * 0.55f, 0.92f),
                                    Vector4.zero, Vector4.one * 4);

                    // Initial
                    GUI.Label(bRect, init,
                              new GUIStyle(_badgeStyle) { normal = { textColor = Color.white } });

                    // Tooltip on hover — full name
                    if (bRect.Contains(Event.current.mousePosition))
                    {
                        float tw = 8f * name.Length + 16f;
                        var   tr = new Rect(bRect.xMax - tw, bRect.yMax + 2, tw, 16f);
                        GUI.DrawTexture(tr, Texture2D.whiteTexture,
                                        ScaleMode.StretchToFill, true, 0,
                                        new Color(0.1f, 0.1f, 0.1f, 0.9f),
                                        Vector4.zero, Vector4.one * 3);
                        GUI.Label(tr, name,
                                  new GUIStyle(EditorStyles.miniLabel)
                                  { alignment = TextAnchor.MiddleCenter, normal = { textColor = col } });
                    }

                    badgeX -= bw + 2f;
                }
            }

            // Force repaint so animations (pulsing) run
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void EnsureStyles()
        {
            if (_stylesReady && _badgeStyle != null) return;
            _badgeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 9,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(0, 0, 0, 0),
            };
            _stylesReady = true;
        }
    }
}
