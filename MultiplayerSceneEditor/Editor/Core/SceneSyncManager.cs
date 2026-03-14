using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace MultiplayerSceneEditor
{
    public enum SessionMode { None, Hosting, Connected }

    /// <summary>
    /// Central coordinator for the multiplayer editing session.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneSyncManager
    {
        // ── Session state ─────────────────────────────────────────────────────
        public static SessionMode Mode      { get; private set; } = SessionMode.None;
        public static UserInfo    LocalUser { get; private set; }

        public static Dictionary<string, RemoteUser> RemoteUsers { get; }
            = new Dictionary<string, RemoteUser>();

        public static LockManager   Locks   { get; } = new LockManager();
        public static ObjectTracker Tracker { get; } = new ObjectTracker();

        // ── Pending join requests (host side) ─────────────────────────────────
        public static List<PendingJoin> PendingJoins { get; } = new List<PendingJoin>();

        // ── Client state ──────────────────────────────────────────────────────
        public static ClientState ClientConnectionState =>
            _client?.State ?? ClientState.Idle;

        private static NetworkServer _server;
        private static NetworkClient _client;
        private static float         _nextHue = 0f;

        // Chat log — uses DateTime so timestamps survive domain reload
        public static List<(string senderId, string name, string msg, DateTime time)> ChatLog { get; }
            = new List<(string, string, string, DateTime)>();

        // Events (main thread)
        public static event Action OnUsersChanged;
        public static event Action OnPendingJoinsChanged;              // new pending request arrived
        public static event Action<string, string> OnChatReceived;     // (displayName, message)
        public static event Action OnSessionEnded;
        public static event Action<string> OnJoinDenied;               // client: fired when host denies us
        public static event Action<string> OnRemoteSelectionChanged;   // (userId) remote user changed selection
        public static event Action OnApprovalPending;                  // client: fired when waiting for host

        // ── Echo-loop prevention ──────────────────────────────────────────────
        /// <summary>
        /// Set to true while applying incoming remote changes so ObjectTracker
        /// ignores the Unity events they generate and doesn't echo them back.
        /// </summary>
        internal static bool SuppressTrackerEvents;

        // ── Heartbeat (detect dead TCP connections) ───────────────────────────
        private static double _lastPingTime;
        private const  double PING_INTERVAL    = 5.0;   // send Ping every 5 s
        private const  double PONG_TIMEOUT     = 20.0;  // kick if no Pong in 20 s

        // ── Static constructor (registers update loop) ────────────────────────

        static SceneSyncManager()
        {
            EditorApplication.update += Tick;
        }

        // ── Host / Join / Leave ───────────────────────────────────────────────

        public static void StartHosting(int port, string displayName)
        {
            if (Mode != SessionMode.None) return;

            LocalUser = new UserInfo
            {
                userId      = Guid.NewGuid().ToString("N")[..8],
                displayName = displayName,
                colorH      = AllocHue(),
            };

            _server = new NetworkServer();
            _server.Start(port);

            Mode = SessionMode.Hosting;
            Tracker.Activate(LocalUser.userId);

            _lastPingTime = EditorApplication.timeSinceStartup;
            Debug.Log($"[MSE] Hosting session on port {port} as {displayName}");
        }

        public static void JoinSession(string host, int port, string displayName)
        {
            if (Mode != SessionMode.None) return;

            LocalUser = new UserInfo
            {
                userId      = Guid.NewGuid().ToString("N")[..8],
                displayName = displayName,
                colorH      = 0f,
            };

            _client = new NetworkClient();
            _client.Connect(host, port, LocalUser);   // throws on immediate failure

            // Mode = Connected but Tracker NOT activated yet.
            // Tracker.Activate is called in HandleHandshakeAck after the host approves us,
            // to prevent accumulated local changes from echoing as a burst of outgoing packets.
            Mode = SessionMode.Connected;

            Debug.Log($"[MSE] Join request sent to {host}:{port} as \"{displayName}\". Waiting for host approval…");
        }

        public static void LeaveSession()
        {
            if (Mode == SessionMode.None) return;

            Tracker.Deactivate();
            _server?.Stop();
            _server?.Dispose();
            _server = null;
            _client?.Disconnect();
            _client?.Dispose();
            _client = null;

            Locks.ReleaseAll(LocalUser?.userId ?? "");
            RemoteUsers.Clear();
            ChatLog.Clear();

            Mode      = SessionMode.None;
            _nextHue  = 0f;   // reset colour wheel for next session

            OnSessionEnded?.Invoke();
            OnUsersChanged?.Invoke();
            SceneView.RepaintAll();
            Debug.Log("[MSE] Session ended.");
        }

        // ── Send helpers (outgoing from local user) ───────────────────────────

        public static void SendChat(string message)
        {
            if (Mode == SessionMode.None) return;
            Broadcast(Envelope.Create(MsgType.ChatMessage, LocalUser.userId,
                Protocol.Ser(new ChatPayload
                {
                    message     = message,
                    displayName = LocalUser.displayName,
                    senderId    = LocalUser.userId,
                })));
        }

        // ── Approval API (host only) ──────────────────────────────────────────

        public static void ApproveJoin(PendingJoin pj)
        {
            if (Mode != SessionMode.Hosting || _server == null) return;

            PendingJoins.Remove(pj);

            var peer = _server.ApprovePeer(pj.UserId);
            if (peer == null) return;   // already gone

            float hue = AllocHue();

            var approvedUser = new UserInfo
            {
                userId      = pj.UserId,
                displayName = pj.DisplayName,
                colorH      = hue,
            };

            RemoteUsers[pj.UserId] = new RemoteUser { Info = approvedUser };

            var snapshots    = BuildSceneSnapshot();
            var presentUsers = new List<UserInfo> { LocalUser };
            foreach (var kv in RemoteUsers)
                if (kv.Key != pj.UserId) presentUsers.Add(kv.Value.Info);

            var ack = Envelope.Create(MsgType.HandshakeAck, LocalUser.userId,
                          Protocol.Ser(new HandshakeAckPayload
                          {
                              localUser     = approvedUser,
                              presentUsers  = presentUsers.ToArray(),
                              sceneSnapshot = snapshots,
                          }));
            _server.SendTo(pj.UserId, ack);

            var joined = Envelope.Create(MsgType.UserJoined, pj.UserId,
                             Protocol.Ser(approvedUser));
            _server.Broadcast(joined, exceptUserId: pj.UserId);

            OnUsersChanged?.Invoke();
            OnPendingJoinsChanged?.Invoke();
            Debug.Log($"[MSE] Approved join for \"{pj.DisplayName}\"");
        }

        public static void DenyJoin(PendingJoin pj, string reason = "The host declined your request.")
        {
            if (Mode != SessionMode.Hosting || _server == null) return;

            PendingJoins.Remove(pj);
            _server.DenyPeer(pj.UserId, reason);

            OnPendingJoinsChanged?.Invoke();
        }

        // ── Local IP helper ───────────────────────────────────────────────────

        public static List<string> GetLocalIPAddresses()
        {
            var result = new List<string>();
            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus != OperationalStatus.Up) continue;
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in iface.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            result.Add(ua.Address.ToString());
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[MSE] GetLocalIPs: {ex.Message}"); }

            if (result.Count == 0) result.Add("127.0.0.1");
            return result;
        }

        // ── Tick — drain queues, flush outgoing ───────────────────────────────

        private static void Tick()
        {
            if (Mode == SessionMode.None) return;

            // ── Server: drain pending joins ───────────────────────────────────
            if (Mode == SessionMode.Hosting && _server != null)
            {
                bool changed = false;
                while (_server.PendingQueue.TryDequeue(out var pj))
                {
                    pj.ReceivedAt = EditorApplication.timeSinceStartup;   // set here on main thread
                    PendingJoins.Add(pj);
                    changed = true;
                }
                if (changed)
                {
                    OnPendingJoinsChanged?.Invoke();
                    SceneView.RepaintAll();
                }

                // Drain approved inbound
                while (_server.InboundQueue.TryDequeue(out var tuple))
                    HandleIncoming(tuple.fromUserId, tuple.env, isServer: true);

                // ── Heartbeat (Ping → all clients) ────────────────────────────
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastPingTime > PING_INTERVAL)
                {
                    _lastPingTime = now;
                    _server.Broadcast(Envelope.Create(MsgType.Ping, LocalUser.userId, ""));

                    // Kick peers who haven't responded in PONG_TIMEOUT seconds
                    var dead = new List<string>();
                    foreach (var kv in RemoteUsers)
                    {
                        var ru = kv.Value;
                        // Only check after the first pong has been received
                        if (ru.LastPongTime > 0 && now - ru.LastPongTime > PONG_TIMEOUT)
                            dead.Add(kv.Key);
                    }
                    foreach (var uid in dead)
                    {
                        string name = RemoteUsers[uid].Info.displayName;
                        Debug.LogWarning($"[MSE] {name} timed out — kicking.");
                        _server.KickPeer(uid);
                        RemoteUsers.Remove(uid);
                        Locks.ReleaseAll(uid);
                        OnUsersChanged?.Invoke();
                    }
                }
            }

            // ── Client: drain inbound ─────────────────────────────────────────
            if (Mode == SessionMode.Connected && _client != null)
            {
                while (_client.InboundQueue.TryDequeue(out var env))
                {
                    var type = (MsgType)env.type;

                    if (type == MsgType.JoinPending)
                    {
                        OnApprovalPending?.Invoke();
                        OnUsersChanged?.Invoke();
                        continue;
                    }

                    if (type == MsgType.JoinDenied)
                    {
                        var reason = Protocol.Des<JoinDeniedPayload>(env.payload)?.reason
                                     ?? "No reason given.";
                        Tracker.Deactivate();
                        _client.Dispose();
                        _client = null;
                        Mode    = SessionMode.None;
                        OnJoinDenied?.Invoke(reason);
                        OnSessionEnded?.Invoke();
                        SceneView.RepaintAll();
                        return;
                    }

                    HandleIncoming(env.userId, env, isServer: false);
                }

                // Detect silent disconnect
                if (_client.State == ClientState.Disconnected)
                {
                    Debug.LogWarning("[MSE] Lost connection to host.");
                    LeaveSession();
                    return;
                }
            }

            // ── Flush local tracker outbox ────────────────────────────────────
            bool fullyConnected = Mode == SessionMode.Hosting ||
                                  (_client != null && _client.State == ClientState.Connected);
            if (fullyConnected)
                while (Tracker.TryDequeue(out var outEnv))
                    Broadcast(outEnv);

            // ── Interpolation ─────────────────────────────────────────────────
            TickInterpolation();
        }

        // ── Message handling ──────────────────────────────────────────────────

        private static void HandleIncoming(string fromUserId, Envelope env, bool isServer)
        {
            var type = (MsgType)env.type;

            switch (type)
            {
                case MsgType.Handshake:
                    break;

                case MsgType.HandshakeAck when !isServer:
                    HandleHandshakeAck(env);
                    break;

                case MsgType.UserJoined:
                    HandleUserJoined(env);
                    break;

                case MsgType.UserLeft:
                    HandleUserLeft(env);
                    break;

                case MsgType.TransformUpdate:
                    if (env.userId != LocalUser.userId)
                    {
                        SuppressTrackerEvents = true;
                        try { ApplyTransformUpdate(env); }
                        finally { SuppressTrackerEvents = false; }
                    }
                    if (isServer) _server.Broadcast(env, exceptUserId: env.userId);
                    break;

                case MsgType.HierarchyChange:
                    if (env.userId != LocalUser.userId)
                    {
                        SuppressTrackerEvents = true;
                        try { ApplyHierarchyChange(env); }
                        finally { SuppressTrackerEvents = false; }
                    }
                    if (isServer) _server.Broadcast(env, exceptUserId: env.userId);
                    break;

                case MsgType.SelectionUpdate:
                    ApplySelectionUpdate(env);
                    if (isServer) _server.Broadcast(env, exceptUserId: env.userId);
                    break;

                case MsgType.LockRequest when isServer:
                    HandleLockRequest(fromUserId, env);
                    break;

                case MsgType.LockGrant:
                case MsgType.LockDeny:
                    ApplyLockResponse(env);
                    if (isServer) _server.Broadcast(env, exceptUserId: null);
                    break;

                case MsgType.LockRelease:
                    ApplyLockRelease(env);
                    if (isServer) _server.Broadcast(env, exceptUserId: env.userId);
                    break;

                case MsgType.CursorUpdate:
                    ApplyCursorUpdate(env);
                    if (isServer) _server.Broadcast(env, exceptUserId: env.userId);
                    break;

                case MsgType.ChatMessage:
                    ApplyChatMessage(env);
                    if (isServer) _server.Broadcast(env, exceptUserId: env.userId);
                    break;

                case MsgType.Ping:
                    var pong = Envelope.Create(MsgType.Pong, LocalUser.userId, "");
                    if (isServer) _server.SendTo(fromUserId, pong);
                    else _client?.Send(pong);
                    break;

                case MsgType.Pong:
                    // Update last-seen time for heartbeat timeout detection
                    if (RemoteUsers.TryGetValue(env.userId, out var pongUser))
                        pongUser.LastPongTime = EditorApplication.timeSinceStartup;
                    break;
            }

            SceneView.RepaintAll();
        }

        // ── Specific handlers ─────────────────────────────────────────────────

        private static void HandleHandshakeAck(Envelope env)
        {
            var ack = Protocol.Des<HandshakeAckPayload>(env.payload);

            // Update our assigned colour
            LocalUser.colorH = ack.localUser.colorH;

            // Register all present users
            foreach (var u in ack.presentUsers)
                if (u.userId != LocalUser.userId)
                    RemoteUsers[u.userId] = new RemoteUser { Info = u };

            // Apply scene snapshot with suppression so we don't echo it back
            SuppressTrackerEvents = true;
            try { ApplySceneSnapshot(ack.sceneSnapshot); }
            finally { SuppressTrackerEvents = false; }

            // NOW activate tracker — only after we're fully set up
            Tracker.Activate(LocalUser.userId);

            OnUsersChanged?.Invoke();
        }

        private static void HandleUserJoined(Envelope env)
        {
            var user = Protocol.Des<UserInfo>(env.payload);
            if (user.userId == LocalUser.userId) return;
            RemoteUsers[user.userId] = new RemoteUser { Info = user };
            OnUsersChanged?.Invoke();
            Debug.Log($"[MSE] {user.displayName} joined the session.");
        }

        private static void HandleUserLeft(Envelope env)
        {
            var user = Protocol.Des<UserInfo>(env.payload);
            if (RemoteUsers.TryGetValue(user.userId, out var ru))
            {
                Debug.Log($"[MSE] {ru.Info.displayName} left the session.");
                RemoteUsers.Remove(user.userId);
            }
            Locks.ReleaseAll(user.userId);
            OnUsersChanged?.Invoke();
        }

        private static void ApplyTransformUpdate(Envelope env)
        {
            var p = Protocol.Des<TransformPayload>(env.payload);

            if (RemoteUsers.TryGetValue(env.userId, out var ru))
            {
                if (!ru.TransformTargets.TryGetValue(p.guid, out var tgt))
                {
                    tgt = new TransformTarget();
                    ru.TransformTargets[p.guid] = tgt;
                }
                tgt.Position   = new Vector3(p.px, p.py, p.pz);
                tgt.Rotation   = new Quaternion(p.rx, p.ry, p.rz, p.rw);
                tgt.Scale      = new Vector3(p.sx, p.sy, p.sz);
                tgt.ReceivedAt = EditorApplication.timeSinceStartup;

                if (!tgt.Initialised)
                {
                    tgt.CurPosition = tgt.Position;
                    tgt.CurRotation = tgt.Rotation;
                    tgt.CurScale    = tgt.Scale;
                    tgt.Initialised = true;
                }
            }

            var go = StableGuid.Find(p.guid);
            if (go == null) return;
            Protocol.ApplyPayload(p, go.transform);
        }

        // ── Interpolation tick ────────────────────────────────────────────────

        private static void TickInterpolation()
        {
            const float LERP_SPEED = 25f;
            float dt = (float)(EditorApplication.timeSinceStartup - _lastTickTime);
            _lastTickTime = EditorApplication.timeSinceStartup;
            float t = Mathf.Clamp01(LERP_SPEED * dt);

            bool anyMoved = false;
            foreach (var kv in RemoteUsers)
            {
                var ru = kv.Value;
                foreach (var tk in ru.TransformTargets)
                {
                    var tgt = tk.Value;
                    if (!tgt.Initialised) continue;

                    tgt.CurPosition = Vector3.Lerp(tgt.CurPosition,    tgt.Position, t);
                    tgt.CurRotation = Quaternion.Slerp(tgt.CurRotation, tgt.Rotation, t);
                    tgt.CurScale    = Vector3.Lerp(tgt.CurScale,        tgt.Scale,    t);

                    var go = StableGuid.Find(tk.Key);
                    if (go == null) continue;

                    go.transform.position   = tgt.CurPosition;
                    go.transform.rotation   = tgt.CurRotation;
                    go.transform.localScale = tgt.CurScale;
                    anyMoved = true;
                }
            }
            if (anyMoved) SceneView.RepaintAll();
        }

        private static double _lastTickTime;

        private static void ApplyHierarchyChange(Envelope env)
        {
            var p = Protocol.Des<HierarchyPayload>(env.payload);

            switch (p.changeType)
            {
                case "create":
                {
                    if (StableGuid.Find(p.guid) != null) return;

                    var parent = string.IsNullOrEmpty(p.parentGuid) ? null : StableGuid.Find(p.parentGuid);
                    var go     = new GameObject(p.name);
                    if (parent != null) go.transform.SetParent(parent.transform, false);
                    go.transform.position   = new Vector3(p.px, p.py, p.pz);
                    go.transform.rotation   = new Quaternion(p.rx, p.ry, p.rz, p.rw);
                    go.transform.localScale = new Vector3(p.sx, p.sy, p.sz);
                    go.SetActive(p.active);

                    var sg = go.AddComponent<StableGuid>();
                    var fi = typeof(StableGuid).GetField("_guid",
                                 System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    fi?.SetValue(sg, p.guid);

                    Undo.RegisterCreatedObjectUndo(go, "Remote Create");
                    EditorSceneManager.MarkSceneDirty(go.scene);
                    break;
                }

                case "delete":
                {
                    var go = StableGuid.Find(p.guid);
                    if (go == null) return;
                    Undo.DestroyObjectImmediate(go);
                    break;
                }

                case "reparent":
                {
                    var go     = StableGuid.Find(p.guid);
                    if (go == null) return;
                    var parent = string.IsNullOrEmpty(p.parentGuid) ? null : StableGuid.Find(p.parentGuid);
                    Undo.SetTransformParent(go.transform, parent?.transform, "Remote Reparent");
                    break;
                }

                case "rename":
                {
                    var go = StableGuid.Find(p.guid);
                    if (go == null) return;
                    Undo.RecordObject(go, "Remote Rename");
                    go.name = p.name;
                    break;
                }

                case "active":
                {
                    var go = StableGuid.Find(p.guid);
                    if (go == null) return;
                    Undo.RecordObject(go, "Remote Active");
                    go.SetActive(p.active);
                    break;
                }
            }
        }

        private static void ApplySelectionUpdate(Envelope env)
        {
            if (!RemoteUsers.TryGetValue(env.userId, out var ru)) return;
            var p    = Protocol.Des<SelectionPayload>(env.payload);
            var prev = new HashSet<string>(ru.SelectedGuids);

            foreach (var g in prev)
                if (!Array.Exists(p.guids, x => x == g))
                    Locks.Release(g, env.userId);

            foreach (var g in p.guids)
                if (!prev.Contains(g))
                    Locks.TryAcquire(g, env.userId);

            ru.SelectedGuids = new HashSet<string>(p.guids);
            OnRemoteSelectionChanged?.Invoke(env.userId);
            SceneView.RepaintAll();
        }

        private static void HandleLockRequest(string userId, Envelope env)
        {
            var p       = Protocol.Des<LockPayload>(env.payload);
            bool granted = Locks.TryAcquire(p.guid, userId);
            var resp    = Envelope.Create(
                              granted ? MsgType.LockGrant : MsgType.LockDeny,
                              userId,
                              Protocol.Ser(new LockPayload { guid = p.guid, ownerUserId = userId }));
            _server.SendTo(userId, resp);
            if (granted) _server.Broadcast(resp, exceptUserId: userId);
        }

        private static void ApplyLockResponse(Envelope env)
        {
            var p = Protocol.Des<LockPayload>(env.payload);
            if ((MsgType)env.type == MsgType.LockGrant)
                Locks.TryAcquire(p.guid, p.ownerUserId);
        }

        private static void ApplyLockRelease(Envelope env)
        {
            var p = Protocol.Des<LockPayload>(env.payload);
            Locks.Release(p.guid, p.ownerUserId);
        }

        private static void ApplyCursorUpdate(Envelope env)
        {
            if (!RemoteUsers.TryGetValue(env.userId, out var ru)) return;
            var p = Protocol.Des<CursorPayload>(env.payload);
            ru.CursorWorld    = new Vector3(p.x, p.y, p.z);
            ru.LastCursorTime = EditorApplication.timeSinceStartup;
        }

        private static void ApplyChatMessage(Envelope env)
        {
            var p = Protocol.Des<ChatPayload>(env.payload);
            ChatLog.Add((p.senderId, p.displayName, p.message, DateTime.Now));
            OnChatReceived?.Invoke(p.displayName, p.message);
        }

        // ── Scene snapshot helpers ────────────────────────────────────────────

        private static ObjSnapshot[] BuildSceneSnapshot()
        {
            var list = new List<ObjSnapshot>();
#if UNITY_2023_1_OR_NEWER
            var all = UnityEngine.Object.FindObjectsByType<StableGuid>(FindObjectsSortMode.None);
#else
            var all = UnityEngine.Object.FindObjectsOfType<StableGuid>();
#endif
            foreach (var sg in all)
                list.Add(Protocol.ToSnapshot(sg.Guid, sg.gameObject));
            return list.ToArray();
        }

        private static void ApplySceneSnapshot(ObjSnapshot[] snapshots)
        {
            if (snapshots == null) return;
            foreach (var s in snapshots)
            {
                var go = StableGuid.Find(s.guid);
                if (go == null)
                {
                    var env = Envelope.Create(MsgType.HierarchyChange, "snapshot",
                                  Protocol.Ser(new HierarchyPayload
                                  {
                                      changeType = "create",
                                      guid       = s.guid,
                                      parentGuid = s.parentGuid,
                                      name       = s.name,
                                      active     = s.active,
                                      px = s.px, py = s.py, pz = s.pz,
                                      rx = s.rx, ry = s.ry, rz = s.rz, rw = s.rw,
                                      sx = s.sx, sy = s.sy, sz = s.sz,
                                  }));
                    ApplyHierarchyChange(env);
                }
                else
                {
                    go.transform.position   = new Vector3(s.px, s.py, s.pz);
                    go.transform.rotation   = new Quaternion(s.rx, s.ry, s.rz, s.rw);
                    go.transform.localScale = new Vector3(s.sx, s.sy, s.sz);
                    go.SetActive(s.active);
                }
            }
        }

        // ── Broadcast ─────────────────────────────────────────────────────────

        private static void Broadcast(Envelope env)
        {
            if (Mode == SessionMode.Hosting)
                _server?.Broadcast(env, exceptUserId: env.userId);
            else if (Mode == SessionMode.Connected)
                _client?.Send(env);
        }

        // ── Colour allocation ─────────────────────────────────────────────────

        private static float AllocHue()
        {
            float h  = _nextHue;
            _nextHue = (_nextHue + 0.17f) % 1f;
            return h;
        }
    }

    // ── Remote user runtime state ─────────────────────────────────────────────

    public class RemoteUser
    {
        public UserInfo         Info;
        public HashSet<string>  SelectedGuids  = new HashSet<string>();
        public Vector3          CursorWorld;
        public double           LastCursorTime;
        public double           LastPongTime;   // used by heartbeat to detect dead connections

        public Dictionary<string, TransformTarget> TransformTargets
            = new Dictionary<string, TransformTarget>();

        public Color UserColor => Color.HSVToRGB(Info.colorH, 0.85f, 0.95f);
    }

    public class TransformTarget
    {
        public Vector3    Position;
        public Quaternion Rotation;
        public Vector3    Scale;
        public double     ReceivedAt;

        public Vector3    CurPosition;
        public Quaternion CurRotation;
        public Vector3    CurScale;
        public bool       Initialised;
    }
}
