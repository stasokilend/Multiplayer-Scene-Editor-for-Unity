using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Dockable editor window — open via  Window → Multiplayer Scene Editor
    /// </summary>
    public class MultiplayerEditorWindow : EditorWindow
    {
        private static MultiplayerEditorWindow _instance;

        [MenuItem("Window/Multiplayer Scene Editor", priority = 1500)]
        public static void Open()
        {
            _instance = GetWindow<MultiplayerEditorWindow>("MSE");
            _instance.minSize = new Vector2(300, 460);
            _instance.Show();
        }

        // ── GUI state ─────────────────────────────────────────────────────────

        private enum Tab { Session, Users, Chat }
        private Tab    _tab = Tab.Session;

        // Setup form
        private string _displayName  = "Editor_" + System.Environment.UserName.Replace(" ", "");
        private string _hostAddress  = "127.0.0.1";
        private int    _port         = 7700;
        private int    _selectedIPIdx = 0;          // index into local IP list
        private List<string> _localIPs = new List<string>();
        private bool   _ipsFetched   = false;

        // Chat
        private string  _chatInput = "";
        private Vector2 _chatScroll;
        private Vector2 _userScroll;

        // Log
        private bool          _showLog = false;
        private List<string>  _logLines = new List<string>();

        // Denial dialog
        private string _denialMessage = null;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            SceneSyncManager.OnUsersChanged       += Repaint;
            SceneSyncManager.OnPendingJoinsChanged += Repaint;
            SceneSyncManager.OnChatReceived        += OnChat;
            SceneSyncManager.OnSessionEnded        += OnSessionEnded;
            SceneSyncManager.OnJoinDenied          += OnDenied;
            SceneSyncManager.OnApprovalPending     += Repaint;
            Application.logMessageReceived         += OnLog;
        }

        private void OnDisable()
        {
            SceneSyncManager.OnUsersChanged       -= Repaint;
            SceneSyncManager.OnPendingJoinsChanged -= Repaint;
            SceneSyncManager.OnChatReceived        -= OnChat;
            SceneSyncManager.OnSessionEnded        -= OnSessionEnded;
            SceneSyncManager.OnJoinDenied          -= OnDenied;
            SceneSyncManager.OnApprovalPending     -= Repaint;
            Application.logMessageReceived         -= OnLog;
        }

        private void OnChat(string name, string msg)
        {
            Repaint();
            _chatScroll.y = float.MaxValue;
        }

        private void OnSessionEnded()
        {
            _tab = Tab.Session;
            Repaint();
        }

        private void OnDenied(string reason)
        {
            _denialMessage = reason;
            Repaint();
        }

        private void OnLog(string msg, string stack, LogType type)
        {
            if (!msg.StartsWith("[MSE")) return;
            _logLines.Add($"{DateTime.Now:HH:mm:ss}  {msg}");
            if (_logLines.Count > 150) _logLines.RemoveAt(0);
            Repaint();
        }

        // ── OnGUI ─────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();

            // Denial overlay
            if (_denialMessage != null)
            {
                DrawDenialBanner(_denialMessage);
                return;
            }

            if (SceneSyncManager.Mode == SessionMode.None)
                DrawSetupPanel();
            else
                DrawSessionPanel();
        }

        // ── Header ────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            var rect = GUILayoutUtility.GetRect(0, 46, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.10f, 0.10f, 0.13f));

            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                normal   = { textColor = new Color(0.92f, 0.92f, 0.92f) }
            };
            GUI.Label(new Rect(rect.x + 10, rect.y + 6, 260, 20), "● Multiplayer Scene Editor", titleStyle);

            if (SceneSyncManager.Mode != SessionMode.None)
            {
                var cs = SceneSyncManager.ClientConnectionState;
                string sub;
                Color  subCol;
                if (SceneSyncManager.Mode == SessionMode.Hosting)
                {
                    sub    = $"HOST  :{_port}  —  {SceneSyncManager.RemoteUsers.Count + 1} users";
                    subCol = new Color(0.4f, 1f, 0.4f);
                }
                else if (cs == ClientState.WaitingApproval)
                {
                    sub    = "⏳  Waiting for host approval…";
                    subCol = new Color(1f, 0.8f, 0.2f);
                }
                else
                {
                    sub    = $"CLIENT  {_hostAddress}:{_port}  —  {SceneSyncManager.RemoteUsers.Count + 1} users";
                    subCol = new Color(0.4f, 0.7f, 1f);
                }
                GUI.Label(new Rect(rect.x + 10, rect.y + 26, 340, 16), sub,
                    new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = subCol } });
            }
            GUILayout.Space(2);
        }

        // ── Denial banner ─────────────────────────────────────────────────────

        private void DrawDenialBanner(string reason)
        {
            GUILayout.Space(20);
            var style = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 13,
                normal    = { textColor = new Color(1f, 0.4f, 0.4f) }
            };
            GUILayout.Label("🚫  Connection Denied", style);
            GUILayout.Space(10);
            EditorGUILayout.HelpBox(reason, MessageType.Error);
            GUILayout.Space(16);
            if (GUILayout.Button("OK", GUILayout.Height(28)))
            {
                _denialMessage = null;
                Repaint();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  SETUP PANEL  (no active session)
        // ═══════════════════════════════════════════════════════════════════════

        private void DrawSetupPanel()
        {
            GUILayout.Space(8);

            // ── Identity ──────────────────────────────────────────────────────
            GUILayout.Label("Identity", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope(1))
                _displayName = EditorGUILayout.TextField("Your Name", _displayName);

            GUILayout.Space(10);
            DrawSeparator();
            GUILayout.Space(8);

            // ── HOST SECTION ──────────────────────────────────────────────────
            GUILayout.Label("Host a Session", EditorStyles.boldLabel);

            using (new EditorGUI.IndentLevelScope(1))
            {
                _port = EditorGUILayout.IntField("Port", _port);

                // Fetch IPs lazily
                if (!_ipsFetched)
                {
                    _localIPs   = SceneSyncManager.GetLocalIPAddresses();
                    _ipsFetched = true;
                }

                // IP address selector
                if (_localIPs.Count > 0)
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel("Share this IP");
                    _selectedIPIdx = Mathf.Clamp(_selectedIPIdx, 0, _localIPs.Count - 1);

                    // Popup to pick among multiple interfaces
                    if (_localIPs.Count > 1)
                        _selectedIPIdx = EditorGUILayout.Popup(_selectedIPIdx, _localIPs.ToArray());

                    string displayIP = _localIPs[_selectedIPIdx];

                    // Read-only styled text field
                    var ipStyle = new GUIStyle(EditorStyles.textField)
                    { normal = { textColor = new Color(0.3f, 0.9f, 0.3f) } };
                    EditorGUILayout.SelectableLabel(displayIP, ipStyle,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));

                    if (GUILayout.Button("Copy", GUILayout.Width(42)))
                        GUIUtility.systemCopyBuffer = displayIP;

                    GUILayout.EndHorizontal();
                }
            }

            GUI.backgroundColor = new Color(0.25f, 0.65f, 0.25f);
            if (GUILayout.Button("▶  Start Hosting", GUILayout.Height(32)))
            {
                if (ValidateName())
                {
                    try { SceneSyncManager.StartHosting(_port, _displayName); }
                    catch (Exception ex)
                    {
                        EditorUtility.DisplayDialog("Host Error",
                            $"Could not start server on port {_port}.\n\n{ex.Message}", "OK");
                    }
                }
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(14);
            DrawSeparator();
            GUILayout.Space(8);

            // ── JOIN SECTION ──────────────────────────────────────────────────
            GUILayout.Label("Join a Session", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope(1))
            {
                // IP address field
                GUILayout.BeginHorizontal();
                _hostAddress = EditorGUILayout.TextField("Host IP", _hostAddress);
                if (GUILayout.Button("Paste", GUILayout.Width(44)))
                {
                    string clip = GUIUtility.systemCopyBuffer.Trim();
                    if (!string.IsNullOrEmpty(clip)) _hostAddress = clip;
                }
                GUILayout.EndHorizontal();

                _port = EditorGUILayout.IntField("Port", _port);
            }

            GUI.backgroundColor = new Color(0.25f, 0.45f, 0.85f);
            if (GUILayout.Button("⟶  Join Session", GUILayout.Height(32)))
            {
                if (ValidateName() && ValidateIP())
                {
                    try { SceneSyncManager.JoinSession(_hostAddress.Trim(), _port, _displayName); }
                    catch (Exception ex)
                    {
                        EditorUtility.DisplayDialog("Connection Error", ex.Message, "OK");
                    }
                }
            }
            GUI.backgroundColor = Color.white;

            // ── Firewall hint ─────────────────────────────────────────────────
            GUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "💡  Make sure port " + _port + " is open in your firewall.\n" +
                "Windows: Windows Defender Firewall → Allow an app → add Unity Editor\n" +
                "Both TCP inbound and outbound must be permitted.",
                MessageType.Info);

            GUILayout.Space(6);
            _showLog = EditorGUILayout.Foldout(_showLog, "Log");
            if (_showLog) DrawLog();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  SESSION PANEL  (connected / hosting)
        // ═══════════════════════════════════════════════════════════════════════

        private void DrawSessionPanel()
        {
            // ── Pending join notifications (HOST only) ────────────────────────
            if (SceneSyncManager.Mode == SessionMode.Hosting &&
                SceneSyncManager.PendingJoins.Count > 0)
            {
                DrawPendingJoins();
                DrawSeparator();
            }

            // ── Waiting-for-approval banner (CLIENT) ──────────────────────────
            if (SceneSyncManager.Mode == SessionMode.Connected &&
                SceneSyncManager.ClientConnectionState == ClientState.WaitingApproval)
            {
                DrawWaitingBanner();
                return;
            }

            _tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { "Session", "Users", "Chat" });
            GUILayout.Space(4);

            switch (_tab)
            {
                case Tab.Session: DrawSessionTab();  break;
                case Tab.Users:   DrawUsersTab();    break;
                case Tab.Chat:    DrawChatTab();     break;
            }
        }

        // ── Pending join requests ─────────────────────────────────────────────

        private void DrawPendingJoins()
        {
            var notifBg = new Color(0.6f, 0.4f, 0.0f, 0.25f);
            var pending = new List<PendingJoin>(SceneSyncManager.PendingJoins);

            foreach (var pj in pending)
            {
                var rect = EditorGUILayout.BeginVertical();
                EditorGUI.DrawRect(new Rect(0, rect.y, position.width, 52), notifBg);
                GUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8);
                    var nameStyle = new GUIStyle(EditorStyles.boldLabel)
                    { normal = { textColor = new Color(1f, 0.85f, 0.3f) } };
                    GUILayout.Label($"🔔  \"{pj.DisplayName}\" wants to join", nameStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(pj.RemoteEndPoint,
                        new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } });
                    GUILayout.Space(6);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8);
                    GUI.backgroundColor = new Color(0.25f, 0.65f, 0.25f);
                    if (GUILayout.Button("✔  Accept", GUILayout.Height(22)))
                        SceneSyncManager.ApproveJoin(pj);

                    GUI.backgroundColor = new Color(0.7f, 0.2f, 0.2f);
                    if (GUILayout.Button("✕  Deny", GUILayout.Height(22)))
                        SceneSyncManager.DenyJoin(pj);

                    GUI.backgroundColor = Color.white;
                    GUILayout.Space(6);
                }

                GUILayout.Space(4);
                EditorGUILayout.EndVertical();
            }
        }

        // ── Waiting for approval (client side) ───────────────────────────────

        private void DrawWaitingBanner()
        {
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();

            var style = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 13,
                normal    = { textColor = new Color(1f, 0.85f, 0.3f) }
            };
            GUILayout.Label("⏳", new GUIStyle(style) { fontSize = 32 });
            GUILayout.Label("Waiting for host approval…", style);
            GUILayout.Space(6);
            GUILayout.Label($"Connected to  {_hostAddress}:{_port}",
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(20);

            GUI.backgroundColor = new Color(0.6f, 0.2f, 0.2f);
            if (GUILayout.Button("Cancel", GUILayout.Height(26)))
                SceneSyncManager.LeaveSession();
            GUI.backgroundColor = Color.white;

            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }

        // ── Session tab ───────────────────────────────────────────────────────

        private void DrawSessionTab()
        {
            var lu = SceneSyncManager.LocalUser;
            if (lu == null) return;

            GUILayout.Space(6);
            DrawInfoRow("Mode",  SceneSyncManager.Mode == SessionMode.Hosting ? "Host" : "Client");
            DrawInfoRow("Name",  lu.displayName);
            DrawInfoRow("Port",  _port.ToString());
            if (SceneSyncManager.Mode == SessionMode.Hosting)
            {
                // Show all IPs for easy sharing
                var ips = SceneSyncManager.GetLocalIPAddresses();
                foreach (var ip in ips)
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(ips.Count > 1 ? "Share IP" : "Your IP");
                    var ipStyle = new GUIStyle(EditorStyles.textField)
                    { normal = { textColor = new Color(0.3f, 0.9f, 0.3f) } };
                    EditorGUILayout.SelectableLabel(ip, ipStyle,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (GUILayout.Button("Copy", GUILayout.Width(42)))
                        GUIUtility.systemCopyBuffer = ip;
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                DrawInfoRow("Host",  _hostAddress);
            }
            DrawInfoRow("Users", (SceneSyncManager.RemoteUsers.Count + 1).ToString());

            GUILayout.Space(12);
            DrawSeparator();
            GUILayout.Space(6);

            GUILayout.Label("Active Locks", EditorStyles.boldLabel);
            if (SceneSyncManager.Locks.AllLocks.Count == 0)
                GUILayout.Label("  none", EditorStyles.miniLabel);
            else
                foreach (var kv in SceneSyncManager.Locks.AllLocks)
                {
                    var go   = StableGuid.Find(kv.Key);
                    string n = go != null ? go.name : $"<{kv.Key[..6]}…>";
                    string owner = kv.Value == lu.userId ? "(you)" : kv.Value;
                    if (SceneSyncManager.RemoteUsers.TryGetValue(kv.Value, out var ru))
                        owner = ru.Info.displayName;
                    EditorGUILayout.LabelField($"  🔒 {n}", owner, EditorStyles.miniLabel);
                }

            GUILayout.FlexibleSpace();
            DrawSeparator();
            GUILayout.Space(4);

            GUI.backgroundColor = new Color(0.75f, 0.2f, 0.2f);
            if (GUILayout.Button("✕  Leave Session", GUILayout.Height(28)))
                if (EditorUtility.DisplayDialog("Leave", "Leave the multiplayer session?", "Leave", "Cancel"))
                    SceneSyncManager.LeaveSession();
            GUI.backgroundColor = Color.white;

            _showLog = EditorGUILayout.Foldout(_showLog, "Log");
            if (_showLog) DrawLog();
        }

        // ── Users tab ─────────────────────────────────────────────────────────

        private void DrawUsersTab()
        {
            _userScroll = EditorGUILayout.BeginScrollView(_userScroll);

            var lu = SceneSyncManager.LocalUser;
            if (lu != null)
                DrawUserRow(lu.displayName, Color.HSVToRGB(lu.colorH, 0.85f, 0.95f),
                            SceneSyncManager.Mode == SessionMode.Hosting ? "Host (you)" : "You");

            foreach (var kv in SceneSyncManager.RemoteUsers)
            {
                var ru  = kv.Value;
                int sel = ru.SelectedGuids?.Count ?? 0;
                DrawUserRow(ru.Info.displayName, ru.UserColor,
                            sel > 0 ? $"{sel} obj{(sel != 1 ? "s" : "")} selected" : "idle");
            }

            // Pending joins
            if (SceneSyncManager.PendingJoins.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("Awaiting approval:", EditorStyles.miniLabel);
                foreach (var pj in SceneSyncManager.PendingJoins)
                    DrawUserRow(pj.DisplayName, Color.yellow, $"⏳ {pj.RemoteEndPoint}");
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawUserRow(string name, Color col, string status)
        {
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                var swatchR = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12));
                EditorGUI.DrawRect(new Rect(swatchR.x, swatchR.y + 2, 10, 10), col);
                GUILayout.Space(6);
                GUILayout.Label(name, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                GUILayout.Label(status, EditorStyles.miniLabel);
            }
        }

        // ── Chat tab ──────────────────────────────────────────────────────────

        private void DrawChatTab()
        {
            _chatScroll = EditorGUILayout.BeginScrollView(_chatScroll, GUILayout.ExpandHeight(true));
            foreach (var (name, msg, _) in SceneSyncManager.ChatLog)
            {
                Color col = Color.gray;
                foreach (var kv in SceneSyncManager.RemoteUsers)
                    if (kv.Value.Info.displayName == name) { col = kv.Value.UserColor; break; }
                if (SceneSyncManager.LocalUser?.displayName == name)
                    col = Color.HSVToRGB(SceneSyncManager.LocalUser.colorH, 0.85f, 0.95f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(name + ":",
                        new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = col }, wordWrap = false },
                        GUILayout.Width(90));
                    GUILayout.Label(msg,
                        new GUIStyle(EditorStyles.wordWrappedLabel)
                        { normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } });
                }
            }
            EditorGUILayout.EndScrollView();

            GUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.SetNextControlName("ChatInput");
                _chatInput = EditorGUILayout.TextField(_chatInput);
                bool send = GUILayout.Button("Send", GUILayout.Width(50));
                bool enter = Event.current.type == EventType.KeyDown
                          && Event.current.keyCode == KeyCode.Return
                          && GUI.GetNameOfFocusedControl() == "ChatInput";
                if ((send || enter) && !string.IsNullOrWhiteSpace(_chatInput))
                {
                    SceneSyncManager.SendChat(_chatInput);
                    _chatInput = "";
                    GUI.FocusControl("ChatInput");
                    if (enter) Event.current.Use();
                }
            }
        }

        // ── Log panel ─────────────────────────────────────────────────────────

        private void DrawLog()
        {
            using (new EditorGUILayout.ScrollViewScope(Vector2.zero, GUILayout.Height(100)))
                foreach (var line in _logLines)
                    EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private bool ValidateName()
        {
            if (!string.IsNullOrWhiteSpace(_displayName)) return true;
            EditorUtility.DisplayDialog("Error", "Please enter a display name.", "OK");
            return false;
        }

        private bool ValidateIP()
        {
            string ip = _hostAddress.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                EditorUtility.DisplayDialog("Error", "Please enter the host IP address.", "OK");
                return false;
            }
            return true;
        }

        private static void DrawInfoRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(80));
                EditorGUILayout.LabelField(value);
            }
        }

        private static void DrawSeparator()
        {
            var r = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.28f, 0.28f, 0.28f));
        }
    }
}
