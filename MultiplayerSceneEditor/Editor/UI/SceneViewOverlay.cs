using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Draws all collaborative visuals in every SceneView:
    ///   • Per-user coloured outline + username chip on every selected object
    ///   • Animated pulsing highlight when two users select the same object
    ///   • Remote cursor crosshairs with name chips
    ///   • Lock badges on objects being edited
    ///   • User avatar strip in the top-right corner of the Scene View
    /// </summary>
    [InitializeOnLoad]
    public static class SceneViewOverlay
    {
        private const double CURSOR_FADE  = 4.0;
        private const float  CHIP_HEIGHT  = 18f;
        private const float  CHIP_PADDING = 6f;
        private const float  AVATAR_SIZE  = 22f;
        private const float  AVATAR_GAP   = 4f;

        private static GUIStyle _chipStyle;
        private static GUIStyle _lockStyle;
        private static bool     _stylesReady;

        static SceneViewOverlay()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView sv)
        {
            if (SceneSyncManager.Mode == SessionMode.None) return;
            EnsureStyles();

            TrackLocalCursor();

            var selMap = BuildSelectionMap();

            foreach (var kv in selMap)
                DrawObjectOverlay(kv.Key, kv.Value, sv);

            DrawLockBadges();

            foreach (var kv in SceneSyncManager.RemoteUsers)
                DrawRemoteCursor(kv.Value, sv);

            DrawAvatarStrip(sv);
        }

        // ── Selection map ─────────────────────────────────────────────────────

        private static Dictionary<string, List<(string name, Color col)>> BuildSelectionMap()
        {
            var map = new Dictionary<string, List<(string, Color)>>();

            void Add(string guid, string name, Color col)
            {
                if (!map.TryGetValue(guid, out var list))
                {
                    list = new List<(string, Color)>();
                    map[guid] = list;
                }
                list.Add((name, col));
            }

            foreach (var kv in SceneSyncManager.RemoteUsers)
            {
                var ru = kv.Value;
                if (ru.SelectedGuids == null) continue;
                foreach (var guid in ru.SelectedGuids)
                    Add(guid, ru.Info.displayName, ru.UserColor);
            }
            return map;
        }

        // ── Per-object overlay ────────────────────────────────────────────────

        private static void DrawObjectOverlay(
            string guid, List<(string name, Color col)> users, SceneView sv)
        {
            var go = StableGuid.Find(guid);
            if (go == null) return;

            Bounds bounds = GetObjectBounds(go);
            double time   = EditorApplication.timeSinceStartup;

            // One coloured outline per user (stacked slightly outward)
            for (int i = 0; i < users.Count; i++)
            {
                var (_, col) = users[i];
                float pulse  = 0.6f + 0.4f * Mathf.Abs(Mathf.Sin((float)(time * Mathf.PI)));
                Color outline = new Color(col.r, col.g, col.b, pulse);
                Color fill    = new Color(col.r, col.g, col.b, 0.05f + 0.03f * i);

                Bounds expanded = bounds;
                expanded.Expand(0.04f * (i + 1));

                Handles.color = fill;
                DrawSolidBounds(expanded);

                Handles.color = outline;
                DrawWireBounds(expanded);
            }

            // Name chips above the bounding box
            Vector3 topWorld = bounds.center + Vector3.up * (bounds.extents.y + 0.05f);

            Handles.BeginGUI();

            Vector2 topScreen = HandleUtility.WorldToGUIPoint(topWorld);
            float totalWidth  = 0f;
            foreach (var (name, _) in users)
                totalWidth += MeasureChip(name) + AVATAR_GAP;
            totalWidth -= AVATAR_GAP;

            float startX = topScreen.x - totalWidth * 0.5f;
            // BUG FIX: stack chips upward when multiple users share the same object.
            // Previously the formula ended with "* 0" so all chips rendered at the same y.
            float baseY  = topScreen.y - CHIP_HEIGHT - 4f;

            for (int i = 0; i < users.Count; i++)
            {
                var (name, col) = users[i];
                float w = MeasureChip(name);
                // Each additional chip stacks (CHIP_HEIGHT + 2) px above the previous
                float y = baseY - i * (CHIP_HEIGHT + 2f);
                DrawNameChip(new Rect(startX, y, w, CHIP_HEIGHT), name, col);
                startX += w + AVATAR_GAP;
            }

            Handles.EndGUI();
        }

        private static void DrawNameChip(Rect rect, string name, Color col)
        {
            Color bg = new Color(col.r * 0.35f, col.g * 0.35f, col.b * 0.35f, 0.92f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                            bg, Vector4.zero, new Vector4(rect.height * 0.5f, rect.height * 0.5f,
                                                          rect.height * 0.5f, rect.height * 0.5f));

            GUI.DrawTexture(new Rect(rect.x, rect.y, 4, rect.height),
                            Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                            col, Vector4.zero, new Vector4(2, 0, 0, 2));

            GUI.Label(new Rect(rect.x + 6, rect.y, rect.width - 6, rect.height),
                      name, _chipStyle);
        }

        private static float MeasureChip(string name)
        {
            float textW = _chipStyle.CalcSize(new GUIContent(name)).x;
            return textW + CHIP_PADDING * 2 + 6;
        }

        // ── Lock badges ───────────────────────────────────────────────────────

        private static void DrawLockBadges()
        {
            foreach (var kv in SceneSyncManager.Locks.AllLocks)
            {
                string userId = kv.Value;
                if (userId == SceneSyncManager.LocalUser?.userId) continue;

                var go = StableGuid.Find(kv.Key);
                if (go == null) continue;

                Color col = Color.white;
                if (SceneSyncManager.RemoteUsers.TryGetValue(userId, out var ru))
                    col = ru.UserColor;

                Bounds b = GetObjectBounds(go);

                Handles.color = col;
                Handles.DrawDottedLines(GetBoundsEdgePairs(b), 3.5f);

                Vector3 chipPos = b.center + new Vector3(b.extents.x, b.extents.y, -b.extents.z);
                string label = ru != null ? $"🔒 {ru.Info.displayName}" : "🔒";

                Handles.BeginGUI();
                Vector2 sp = HandleUtility.WorldToGUIPoint(chipPos);
                float w = _lockStyle.CalcSize(new GUIContent(label)).x + 12;
                var r = new Rect(sp.x, sp.y - CHIP_HEIGHT - 2, w, CHIP_HEIGHT);
                GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill,
                                true, 0, new Color(0.15f, 0.05f, 0.05f, 0.88f),
                                Vector4.zero, Vector4.one * 4);
                GUI.Label(new Rect(r.x + 6, r.y, r.width, r.height), label, _lockStyle);
                Handles.EndGUI();
            }
        }

        // ── Remote cursor ─────────────────────────────────────────────────────

        private static void DrawRemoteCursor(RemoteUser ru, SceneView sv)
        {
            double age = EditorApplication.timeSinceStartup - ru.LastCursorTime;
            if (age > CURSOR_FADE) return;

            float alpha = Mathf.Clamp01(1f - (float)(age / CURSOR_FADE));
            Color col   = ru.UserColor;
            Vector3 pos = ru.CursorWorld;
            float size  = HandleUtility.GetHandleSize(pos) * 0.18f;

            Handles.color = new Color(col.r, col.g, col.b, alpha * 0.4f);
            Handles.DrawWireDisc(pos, Vector3.up, size * 1.5f);

            Handles.color = new Color(col.r, col.g, col.b, alpha);
            Handles.DrawLine(pos - Vector3.right * size,   pos + Vector3.right * size);
            Handles.DrawLine(pos - Vector3.forward * size, pos + Vector3.forward * size);
            Handles.DrawWireDisc(pos, Vector3.up, size * 0.35f);

            DrawNameChipAt(pos + Vector3.up * size * 2f, ru.Info.displayName, col, alpha);
        }

        private static void DrawNameChipAt(Vector3 worldPos, string name, Color col, float alpha)
        {
            Handles.BeginGUI();
            Vector2 sp = HandleUtility.WorldToGUIPoint(worldPos);
            float w = _chipStyle.CalcSize(new GUIContent(name)).x + CHIP_PADDING * 2 + 6;
            var r = new Rect(sp.x - w * 0.5f, sp.y - CHIP_HEIGHT * 0.5f, w, CHIP_HEIGHT);

            Color bg = new Color(col.r * 0.35f, col.g * 0.35f, col.b * 0.35f, 0.92f * alpha);
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, bg,
                            Vector4.zero, Vector4.one * (CHIP_HEIGHT * 0.5f));
            GUI.DrawTexture(new Rect(r.x, r.y, 4, r.height), Texture2D.whiteTexture,
                            ScaleMode.StretchToFill, true, 0,
                            new Color(col.r, col.g, col.b, alpha),
                            Vector4.zero, new Vector4(2, 0, 0, 2));

            var s = new GUIStyle(_chipStyle);
            s.normal.textColor = new Color(1, 1, 1, alpha);
            GUI.Label(new Rect(r.x + 6, r.y, r.width - 6, r.height), name, s);
            Handles.EndGUI();
        }

        // ── Avatar strip ──────────────────────────────────────────────────────

        private static void DrawAvatarStrip(SceneView sv)
        {
            Handles.BeginGUI();

            var allUsers = new List<(string name, Color col, bool isLocal)>();

            var lu = SceneSyncManager.LocalUser;
            if (lu != null)
                allUsers.Add((lu.displayName, Color.HSVToRGB(lu.colorH, 0.85f, 0.95f), true));

            foreach (var kv in SceneSyncManager.RemoteUsers)
                allUsers.Add((kv.Value.Info.displayName, kv.Value.UserColor, false));

            string modeTxt = SceneSyncManager.Mode == SessionMode.Hosting ? "HOST" : "CLIENT";
            Color  modeBg  = SceneSyncManager.Mode == SessionMode.Hosting
                             ? new Color(0.2f, 0.5f, 0.2f, 0.9f)
                             : new Color(0.15f, 0.3f, 0.55f, 0.9f);

            float panelRight = sv.position.width - 8;
            float panelY     = 8f;
            float pillW      = 70f;
            float pillH      = 20f;

            var pillRect = new Rect(panelRight - pillW, panelY, pillW, pillH);
            GUI.DrawTexture(pillRect, Texture2D.whiteTexture, ScaleMode.StretchToFill,
                            true, 0, modeBg, Vector4.zero, Vector4.one * 6);
            GUI.Label(pillRect, $"● {modeTxt}",
                      new GUIStyle(EditorStyles.miniLabel)
                      {
                          alignment = TextAnchor.MiddleCenter,
                          fontStyle = FontStyle.Bold,
                          normal    = { textColor = Color.white }
                      });

            float ax = panelRight - pillW - AVATAR_GAP;
            float ay = panelY;

            foreach (var (name, col, isLocal) in allUsers)
            {
                ax -= AVATAR_SIZE;
                var aRect = new Rect(ax, ay, AVATAR_SIZE, AVATAR_SIZE);

                GUI.DrawTexture(aRect, Texture2D.whiteTexture, ScaleMode.StretchToFill,
                                true, 0,
                                isLocal ? new Color(col.r, col.g, col.b, 0.95f)
                                        : new Color(col.r * 0.7f, col.g * 0.7f, col.b * 0.7f, 0.9f),
                                Vector4.zero, Vector4.one * (AVATAR_SIZE * 0.5f));

                string init = name.Length > 0 ? name[..1].ToUpper() : "?";
                GUI.Label(aRect, init,
                          new GUIStyle(EditorStyles.boldLabel)
                          {
                              alignment = TextAnchor.MiddleCenter,
                              fontSize  = 11,
                              normal    = { textColor = Color.white }
                          });

                if (aRect.Contains(Event.current.mousePosition))
                {
                    float tw = EditorStyles.boldLabel.CalcSize(new GUIContent(name)).x + 16;
                    var   tr = new Rect(ax - tw + AVATAR_SIZE, ay + AVATAR_SIZE + 2, tw, 18);
                    GUI.DrawTexture(tr, Texture2D.whiteTexture, ScaleMode.StretchToFill,
                                    true, 0, new Color(0.1f, 0.1f, 0.1f, 0.9f),
                                    Vector4.zero, Vector4.one * 4);
                    GUI.Label(tr, name,
                              new GUIStyle(EditorStyles.miniLabel)
                              {
                                  alignment = TextAnchor.MiddleCenter,
                                  normal = { textColor = col }
                              });
                }

                ax -= AVATAR_GAP;
            }

            Handles.EndGUI();
        }

        // ── Local cursor tracking ─────────────────────────────────────────────

        private static void TrackLocalCursor()
        {
            var e = Event.current;
            if (e.type != EventType.MouseMove && e.type != EventType.MouseDrag) return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            // FIX: Physics.Raycast is unavailable in edit mode (PhysX is off).
            // Use HandleUtility.RaySnap first (snaps to geometry via editor picking),
            // then fall back to a horizontal plane at y = 0.
            Vector3 world;
            object snapHit = HandleUtility.RaySnap(ray);
            if (snapHit is RaycastHit hit)
            {
                world = hit.point;
            }
            else
            {
                var plane = new Plane(Vector3.up, Vector3.zero);
                world = plane.Raycast(ray, out float d) ? ray.GetPoint(d) : ray.GetPoint(10f);
            }

            SceneSyncManager.Tracker.TrySendCursor(world);
        }

        // ── Geometry helpers ──────────────────────────────────────────────────

        private static Bounds GetObjectBounds(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0)
                return new Bounds(go.transform.position, Vector3.one * 0.5f);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        private static void DrawWireBounds(Bounds b)
        {
            var c = b.center; var ex = b.extents;
            var p = GetBoundsCorners8(c, ex);
            int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            for (int i = 0; i < edges.Length; i += 2)
                Handles.DrawLine(p[edges[i]], p[edges[i+1]]);
        }

        private static void DrawSolidBounds(Bounds b)
        {
            var c = b.center; var ex = b.extents;
            var p = GetBoundsCorners8(c, ex);
            int[][] faces = {
                new[]{0,1,2,3}, new[]{4,5,6,7},
                new[]{0,1,5,4}, new[]{2,3,7,6},
                new[]{0,3,7,4}, new[]{1,2,6,5},
            };
            foreach (var f in faces)
                Handles.DrawSolidRectangleWithOutline(
                    new[] { p[f[0]], p[f[1]], p[f[2]], p[f[3]] },
                    Handles.color, Color.clear);
        }

        private static Vector3[] GetBoundsCorners8(Vector3 c, Vector3 e) => new[]
        {
            c+new Vector3(-e.x,-e.y,-e.z), c+new Vector3(e.x,-e.y,-e.z),
            c+new Vector3(e.x,-e.y,e.z),   c+new Vector3(-e.x,-e.y,e.z),
            c+new Vector3(-e.x,e.y,-e.z),  c+new Vector3(e.x,e.y,-e.z),
            c+new Vector3(e.x,e.y,e.z),    c+new Vector3(-e.x,e.y,e.z),
        };

        private static Vector3[] GetBoundsEdgePairs(Bounds b)
        {
            var p = GetBoundsCorners8(b.center, b.extents);
            return new[]
            {
                p[0],p[1], p[1],p[2], p[2],p[3], p[3],p[0],
                p[4],p[5], p[5],p[6], p[6],p[7], p[7],p[4],
                p[0],p[4], p[1],p[5], p[2],p[6], p[3],p[7],
            };
        }

        private static void EnsureStyles()
        {
            if (_stylesReady && _chipStyle != null) return;
            _chipStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 10,
                alignment = TextAnchor.MiddleLeft,
                padding   = new RectOffset(0, 0, 0, 0),
                normal    = { textColor = Color.white },
            };
            _lockStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 10,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = new Color(1f, 0.75f, 0.75f) },
            };
            _stylesReady = true;
        }
    }
}
