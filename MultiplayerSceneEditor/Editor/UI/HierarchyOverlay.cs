using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Colours the Unity Hierarchy window rows that remote users have selected.
    ///
    /// Three visual tiers:
    ///
    ///   1. DIRECT selection  — full tint + 3 px left bar + initials badge (+ pulse)
    ///   2. CHILD  of selected — lighter tint + 2 px left bar (same colour, 60 % opacity)
    ///   3. ANCESTOR of selected — very subtle tint + small coloured diamond on the right
    ///      (useful when the parent is collapsed and children aren't visible)
    ///
    ///   Lock icon is drawn on top of whichever tier applies.
    /// </summary>
    [InitializeOnLoad]
    public static class HierarchyOverlay
    {
        // ── Maps: guid → list of (displayName, color) ─────────────────────────

        /// Direct selections
        private static Dictionary<string, List<(string name, Color col)>> _selMap
            = new Dictionary<string, List<(string, Color)>>();

        /// Descendants of selected objects (not directly selected themselves)
        private static Dictionary<string, List<(string name, Color col)>> _childMap
            = new Dictionary<string, List<(string, Color)>>();

        /// Ancestors of selected objects (parent / grandparent chain)
        private static Dictionary<string, List<(string name, Color col)>> _ancestorMap
            = new Dictionary<string, List<(string, Color)>>();

        // guid → time when it first appeared in _selMap (drives pulse)
        private static Dictionary<string, double> _highlightTimes
            = new Dictionary<string, double>();

        private static GUIStyle _badgeStyle;
        private static bool     _stylesReady;

        private const double PULSE_DURATION = 1.2;
        private static bool  _animating;

        static HierarchyOverlay()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItem;
            EditorApplication.update                   += OnUpdate;
            SceneSyncManager.OnUsersChanged            += RebuildMap;
            SceneSyncManager.OnRemoteSelectionChanged  += _ => RebuildMap();
        }

        // ── Rebuild all three maps ────────────────────────────────────────────

        private static void RebuildMap()
        {
            var prevDirect = new HashSet<string>(_selMap.Keys);

            _selMap.Clear();
            _childMap.Clear();
            _ancestorMap.Clear();

            foreach (var kv in SceneSyncManager.RemoteUsers)
            {
                var ru = kv.Value;
                if (ru.SelectedGuids == null) continue;

                foreach (var guid in ru.SelectedGuids)
                {
                    // ── Direct selection ──────────────────────────────────────
                    AddToMap(_selMap, guid, ru.Info.displayName, ru.UserColor);

                    // Try to resolve the GO for hierarchy traversal.
                    // StableGuid.Find is O(1) after the cache is warm.
                    var go = StableGuid.Find(guid);
                    if (go == null) continue;

                    // ── Children (all descendants) ────────────────────────────
                    // GetComponentsInChildren includes the root transform, so we
                    // skip index 0 (that's the selected object itself).
                    var children = go.GetComponentsInChildren<Transform>(includeInactive: true);
                    for (int i = 1; i < children.Length; i++)
                    {
                        string cGuid = StableGuid.Get(children[i].gameObject);
                        if (cGuid == null) continue;
                        // Only add to childMap if not already directly selected
                        if (!_selMap.ContainsKey(cGuid))
                            AddToMap(_childMap, cGuid, ru.Info.displayName, ru.UserColor);
                    }

                    // ── Ancestors (parent chain up to scene root) ─────────────
                    Transform ancestor = go.transform.parent;
                    while (ancestor != null)
                    {
                        string aGuid = StableGuid.Get(ancestor.gameObject);
                        if (aGuid != null && !_selMap.ContainsKey(aGuid))
                            AddToMap(_ancestorMap, aGuid, ru.Info.displayName, ru.UserColor);
                        ancestor = ancestor.parent;
                    }
                }
            }

            // Remove objects from childMap/ancestorMap that ended up in selMap
            // (can happen when two users select parent & child separately)
            foreach (var guid in _selMap.Keys)
            {
                _childMap.Remove(guid);
                _ancestorMap.Remove(guid);
            }

            // Pulse: record time for newly-appeared direct selections
            double now = EditorApplication.timeSinceStartup;
            foreach (var guid in _selMap.Keys)
                if (!prevDirect.Contains(guid))
                    _highlightTimes[guid] = now;

            // Drop stale pulse entries
            var stale = new List<string>();
            foreach (var guid in _highlightTimes.Keys)
                if (!_selMap.ContainsKey(guid)) stale.Add(guid);
            foreach (var guid in stale)
                _highlightTimes.Remove(guid);

            _animating = true;
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void AddToMap(
            Dictionary<string, List<(string, Color)>> map,
            string guid, string name, Color col)
        {
            if (!map.TryGetValue(guid, out var list))
            {
                list = new List<(string, Color)>();
                map[guid] = list;
            }
            // Avoid duplicate entries for the same user (e.g. multi-selected children)
            foreach (var (n, _) in list)
                if (n == name) return;
            list.Add((name, col));
        }

        // ── Update loop ───────────────────────────────────────────────────────

        private static void OnUpdate()
        {
            if (!_animating) return;
            if (SceneSyncManager.Mode == SessionMode.None) { _animating = false; return; }

            double now          = EditorApplication.timeSinceStartup;
            bool   stillPulsing = false;
            foreach (var kv in _highlightTimes)
                if (now - kv.Value < PULSE_DURATION) { stillPulsing = true; break; }

            if (stillPulsing)
                EditorApplication.RepaintHierarchyWindow();
            else
                _animating = false;
        }

        // ── Per-row GUI callback ──────────────────────────────────────────────

        private static void OnHierarchyItem(int instanceID, Rect rowRect)
        {
            if (SceneSyncManager.Mode == SessionMode.None) return;
            EnsureStyles();

            var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (go == null) return;

            string guid = StableGuid.Get(go);
            if (guid == null) return;

            bool isDirect   = _selMap.TryGetValue(guid, out var directUsers)   && directUsers.Count   > 0;
            bool isChild     = _childMap.TryGetValue(guid, out var childUsers)   && childUsers.Count    > 0;
            bool isAncestor  = _ancestorMap.TryGetValue(guid, out var ancUsers)  && ancUsers.Count      > 0;
            bool isLocked    = SceneSyncManager.Locks.IsLockedByOther(
                                   guid, SceneSyncManager.LocalUser?.userId ?? "");

            if (!isDirect && !isChild && !isAncestor && !isLocked) return;

            // ── Pulse (direct only) ───────────────────────────────────────────
            float pulse = 0f;
            if (isDirect && _highlightTimes.TryGetValue(guid, out double t0))
            {
                double age = EditorApplication.timeSinceStartup - t0;
                if (age < PULSE_DURATION)
                {
                    float p = (float)(age / PULSE_DURATION);
                    pulse   = (1f - p) * Mathf.Abs(Mathf.Sin(p * Mathf.PI * 3f));
                }
            }

            // ══════════════════════════════════════════════════════════════════
            //  TIER 1 — DIRECT SELECTION
            // ══════════════════════════════════════════════════════════════════
            if (isDirect)
            {
                // Row tint
                Color c = directUsers[0].col;
                EditorGUI.DrawRect(rowRect, new Color(c.r, c.g, c.b, 0.09f + pulse * 0.22f));

                // Left bar (3 px, split per user when multiple)
                DrawLeftBar(rowRect, directUsers, 3f, 0.9f + pulse * 0.1f);

                // Right-side badges & lock
                float badgeX = rowRect.xMax - 4f;
                float badgeY = rowRect.y + (rowRect.height - 14f) * 0.5f;

                if (isLocked) badgeX = DrawLockIcon(badgeX, badgeY);

                for (int i = directUsers.Count - 1; i >= 0; i--)
                {
                    var (name, col) = directUsers[i];
                    string init     = name.Length > 0 ? name[..1].ToUpper() : "?";
                    float  bw       = 16f;
                    var    bRect    = new Rect(badgeX - bw, badgeY, bw, 14f);

                    float bgV   = Mathf.Lerp(0.50f, 0.80f, pulse);
                    Color bgCol = new Color(col.r * bgV, col.g * bgV, col.b * bgV, 0.95f);
                    GUI.DrawTexture(bRect, Texture2D.whiteTexture,
                                    ScaleMode.StretchToFill, true, 0,
                                    bgCol, Vector4.zero, Vector4.one * 4f);
                    GUI.Label(bRect, init,
                              new GUIStyle(_badgeStyle) { normal = { textColor = Color.white } });

                    if (bRect.Contains(Event.current.mousePosition))
                        DrawTooltip(bRect, name, col);

                    badgeX -= bw + 2f;
                }
                return;   // don't fall through to lower tiers
            }

            // ══════════════════════════════════════════════════════════════════
            //  TIER 2 — CHILD OF SELECTED
            // ══════════════════════════════════════════════════════════════════
            if (isChild)
            {
                // Lighter tint — same colour family as the parent selection
                Color c = childUsers[0].col;
                EditorGUI.DrawRect(rowRect, new Color(c.r, c.g, c.b, 0.05f));

                // Thinner left bar (2 px, 60 % opacity)
                DrawLeftBar(rowRect, childUsers, 2f, 0.60f);

                if (isLocked)
                {
                    float badgeX = rowRect.xMax - 4f;
                    float badgeY = rowRect.y + (rowRect.height - 14f) * 0.5f;
                    DrawLockIcon(badgeX, badgeY);
                }
                return;
            }

            // ══════════════════════════════════════════════════════════════════
            //  TIER 3 — ANCESTOR (parent is collapsed or just partially visible)
            // ══════════════════════════════════════════════════════════════════
            if (isAncestor)
            {
                // Very faint tint
                Color c = ancUsers[0].col;
                EditorGUI.DrawRect(rowRect, new Color(c.r, c.g, c.b, 0.035f));

                // Small diamond dots on the right (one per user) instead of initials
                float dotX = rowRect.xMax - 6f;
                float dotY = rowRect.y + rowRect.height * 0.5f;

                if (isLocked) dotX = DrawLockIcon(dotX, rowRect.y + (rowRect.height - 14f) * 0.5f) - 2f;

                for (int i = ancUsers.Count - 1; i >= 0; i--)
                {
                    var (name, col) = ancUsers[i];
                    const float D = 5f;   // diamond half-size
                    // Draw a tiny rotated square (diamond) using four triangles via DrawRect quad
                    var dRect = new Rect(dotX - D, dotY - D, D * 2f, D * 2f);
                    GUI.DrawTexture(dRect, Texture2D.whiteTexture,
                                    ScaleMode.StretchToFill, true, 0,
                                    new Color(col.r, col.g, col.b, 0.80f),
                                    Vector4.zero, Vector4.one * D);   // fully rounded → circle

                    if (dRect.Contains(Event.current.mousePosition))
                        DrawTooltip(dRect, $"↳ {name}", col);

                    dotX -= D * 2f + 3f;
                }
                return;
            }

            // ── Lock-only row (not selected by anyone, just locked) ───────────
            if (isLocked)
            {
                float badgeX = rowRect.xMax - 4f;
                float badgeY = rowRect.y + (rowRect.height - 14f) * 0.5f;
                DrawLockIcon(badgeX, badgeY);
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        /// <summary>Draws the left-edge colour bar, splitting evenly when multiple users.</summary>
        private static void DrawLeftBar(
            Rect rowRect, List<(string name, Color col)> users, float width, float alpha)
        {
            var barRect = new Rect(rowRect.x, rowRect.y, width, rowRect.height);

            if (users.Count == 1)
            {
                Color c = users[0].col;
                EditorGUI.DrawRect(barRect, new Color(c.r, c.g, c.b, alpha));
            }
            else
            {
                float segH = rowRect.height / users.Count;
                for (int i = 0; i < users.Count; i++)
                {
                    Color c = users[i].col;
                    EditorGUI.DrawRect(
                        new Rect(barRect.x, barRect.y + segH * i, barRect.width, segH),
                        new Color(c.r, c.g, c.b, alpha));
                }
            }
        }

        /// <summary>Draws the lock icon and returns the new badgeX (left edge of the icon).</summary>
        private static float DrawLockIcon(float badgeX, float badgeY)
        {
            var lockRect = new Rect(badgeX - 16f, badgeY, 16f, 14f);
            GUI.Label(lockRect, "🔒",
                new GUIStyle(EditorStyles.miniLabel)
                { fontSize = 10, alignment = TextAnchor.MiddleCenter });
            return badgeX - 18f;
        }

        // ── Tooltip ───────────────────────────────────────────────────────────

        private static void DrawTooltip(Rect anchor, string text, Color nameCol)
        {
            GUIStyle style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = nameCol },
                padding   = new RectOffset(6, 6, 2, 2),
            };

            float tw    = Mathf.Max(style.CalcSize(new GUIContent(text)).x + 12f, 40f);
            var   tipRect = new Rect(anchor.xMax - tw, anchor.yMax + 2f, tw, 16f);

            GUI.DrawTexture(tipRect, Texture2D.whiteTexture,
                            ScaleMode.StretchToFill, true, 0,
                            new Color(0.08f, 0.08f, 0.08f, 0.92f),
                            Vector4.zero, Vector4.one * 3f);
            GUI.Label(tipRect, text, style);
        }

        // ── Style init ────────────────────────────────────────────────────────

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
