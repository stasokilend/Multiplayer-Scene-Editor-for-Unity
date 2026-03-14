using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Detects local scene changes and feeds outgoing Envelopes to SceneSyncManager.
    ///
    /// Real-time drag sync strategy:
    ///   EditorApplication.update polls every ~16 ms for transform changes on
    ///   ANY selected object — this gives smooth, frame-by-frame updates while
    ///   the user is dragging in Scene View, before Undo records anything.
    ///
    ///   ObjectChangeEvents / Undo.postprocessModifications catch everything else
    ///   (rename, reparent, activate, component edits, …).
    ///
    /// Echo-loop prevention:
    ///   When SceneSyncManager is applying an incoming remote change it sets
    ///   SuppressTrackerEvents = true. Every event handler bails out immediately
    ///   so that remote-created / remote-moved objects don't echo back as local changes.
    /// </summary>
    public class ObjectTracker
    {
        // Last-known transform per guid
        private readonly Dictionary<string, TransformPayload> _lastTransforms
            = new Dictionary<string, TransformPayload>();

        // Pending selection that hasn't been sent yet
        private HashSet<string> _lastSelectionGuids = new HashSet<string>();

        private readonly Queue<Envelope> _outbox = new Queue<Envelope>();
        private string _userId;
        private bool   _active;

        // Throttle
        private double _lastCursorSend;
        private double _lastTransformPoll;
        private const double CURSOR_INTERVAL    = 0.05;   // 50 ms
        private const double TRANSFORM_INTERVAL = 0.033;  // ~30 fps real-time drag

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Activate(string userId)
        {
            _userId = userId;
            _active = true;

            EditorApplication.update                 += OnUpdate;
            Undo.postprocessModifications            += OnUndoMods;
            EditorApplication.hierarchyChanged       += OnHierarchyChanged;
            Selection.selectionChanged               += OnSelectionChanged;
            ObjectChangeEvents.changesPublished      += OnObjectChanges;

            RebuildSnapshot();
        }

        public void Deactivate()
        {
            _active = false;
            EditorApplication.update                 -= OnUpdate;
            Undo.postprocessModifications            -= OnUndoMods;
            EditorApplication.hierarchyChanged       -= OnHierarchyChanged;
            Selection.selectionChanged               -= OnSelectionChanged;
            ObjectChangeEvents.changesPublished      -= OnObjectChanges;
            _lastTransforms.Clear();
            _outbox.Clear();
        }

        // ── Outbox ────────────────────────────────────────────────────────────

        public bool TryDequeue(out Envelope env)
        {
            if (_outbox.Count > 0) { env = _outbox.Dequeue(); return true; }
            env = null;
            return false;
        }

        // ── Cursor ────────────────────────────────────────────────────────────

        public void TrySendCursor(Vector3 worldPos)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastCursorSend < CURSOR_INTERVAL) return;
            _lastCursorSend = now;
            _outbox.Enqueue(Envelope.Create(MsgType.CursorUpdate, _userId,
                Protocol.Ser(new CursorPayload { x = worldPos.x, y = worldPos.y, z = worldPos.z })));
        }

        // ── High-frequency update (main thread, every frame) ──────────────────

        private void OnUpdate()
        {
            // Echo-loop guard: don't track changes we're applying from remote
            if (!_active || SceneSyncManager.SuppressTrackerEvents) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastTransformPoll < TRANSFORM_INTERVAL) return;
            _lastTransformPoll = now;

            // Poll transforms of everything currently selected
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                string guid = StableGuid.Get(go);
                if (guid == null) continue;
                QueueTransformIfDirty(guid, go.transform);
            }
        }

        // ── Undo modifications (fires after user releases mouse / confirms) ────

        private UndoPropertyModification[] OnUndoMods(UndoPropertyModification[] mods)
        {
            if (!_active || SceneSyncManager.SuppressTrackerEvents) return mods;

            var dirty = new HashSet<string>();
            foreach (var mod in mods)
            {
                if (mod.currentValue.target is Transform t)
                { var g = StableGuid.Get(t.gameObject); if (g != null) dirty.Add(g); }
                else if (mod.currentValue.target is GameObject go2)
                { var g = StableGuid.Get(go2); if (g != null) dirty.Add(g); }
            }
            foreach (var guid in dirty)
            {
                var go = StableGuid.Find(guid);
                if (go != null) QueueTransformIfDirty(guid, go.transform);
            }
            return mods;
        }

        // ── ObjectChangeEvents (hierarchy restructuring, creates, deletes) ─────

        private void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            if (!_active || SceneSyncManager.SuppressTrackerEvents) return;

            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    {
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var ev);
                        var go = EditorUtility.InstanceIDToObject(ev.instanceId) as GameObject;
                        if (go == null) break;
                        var g = StableGuid.Get(go);
                        if (g != null) QueueTransformIfDirty(g, go.transform);
                        break;
                    }
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                    {
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var ev);
                        var go = EditorUtility.InstanceIDToObject(ev.instanceId) as GameObject;
                        if (go != null) QueueReparent(go);
                        break;
                    }
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                    {
                        stream.GetCreateGameObjectHierarchyEvent(i, out var ev);
                        var go = EditorUtility.InstanceIDToObject(ev.instanceId) as GameObject;
                        if (go != null) QueueCreate(go);
                        break;
                    }
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                    {
                        // Scan for GUIDs whose object no longer exists
                        var snapshot = new Dictionary<string, TransformPayload>(_lastTransforms);
                        foreach (var kv in snapshot)
                        {
                            if (StableGuid.Find(kv.Key) == null)
                            {
                                _outbox.Enqueue(Envelope.Create(MsgType.HierarchyChange, _userId,
                                    Protocol.Ser(new HierarchyPayload { changeType = "delete", guid = kv.Key })));
                                _lastTransforms.Remove(kv.Key);
                            }
                        }
                        break;
                    }
                }
            }
        }

        private void OnHierarchyChanged()
        {
            if (!_active || SceneSyncManager.SuppressTrackerEvents) return;
            RebuildSnapshot();
        }

        private void OnSelectionChanged()
        {
            if (!_active || SceneSyncManager.SuppressTrackerEvents) return;

            var guids = new List<string>();
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                guids.Add(StableGuid.GetOrCreate(go));
            }
            _lastSelectionGuids = new HashSet<string>(guids);
            _outbox.Enqueue(Envelope.Create(MsgType.SelectionUpdate, _userId,
                Protocol.Ser(new SelectionPayload { guids = guids.ToArray() })));
        }

        // ── Queue helpers ─────────────────────────────────────────────────────

        private void QueueTransformIfDirty(string guid, Transform t)
        {
            var p = Protocol.ToPayload(guid, t);
            if (_lastTransforms.TryGetValue(guid, out var last) && ApproxEqual(last, p)) return;
            _lastTransforms[guid] = p;
            _outbox.Enqueue(Envelope.Create(MsgType.TransformUpdate, _userId, Protocol.Ser(p)));
        }

        private void QueueReparent(GameObject go)
        {
            string guid = StableGuid.GetOrCreate(go);
            string parentGuid = go.transform.parent != null
                ? StableGuid.Get(go.transform.parent.gameObject) ?? "" : "";
            _outbox.Enqueue(Envelope.Create(MsgType.HierarchyChange, _userId,
                Protocol.Ser(new HierarchyPayload
                { changeType = "reparent", guid = guid, parentGuid = parentGuid })));
        }

        private void QueueCreate(GameObject go)
        {
            string guid = StableGuid.GetOrCreate(go);
            string parentGuid = go.transform.parent != null
                ? StableGuid.Get(go.transform.parent.gameObject) ?? "" : "";
            var t = go.transform;
            _outbox.Enqueue(Envelope.Create(MsgType.HierarchyChange, _userId,
                Protocol.Ser(new HierarchyPayload
                {
                    changeType = "create", guid = guid, parentGuid = parentGuid,
                    name = go.name, active = go.activeSelf,
                    px = t.position.x, py = t.position.y, pz = t.position.z,
                    rx = t.rotation.x, ry = t.rotation.y, rz = t.rotation.z, rw = t.rotation.w,
                    sx = t.localScale.x, sy = t.localScale.y, sz = t.localScale.z,
                })));
            _lastTransforms[guid] = Protocol.ToPayload(guid, t);
        }

        private void RebuildSnapshot()
        {
#if UNITY_2023_1_OR_NEWER
            foreach (var sg in Object.FindObjectsByType<StableGuid>(FindObjectsSortMode.None))
#else
            foreach (var sg in Object.FindObjectsOfType<StableGuid>())
#endif
                _lastTransforms[sg.Guid] = Protocol.ToPayload(sg.Guid, sg.transform);
        }

        // ── Approximate equality ──────────────────────────────────────────────

        private static bool ApproxEqual(TransformPayload a, TransformPayload b)
        {
            const float eps = 0.0001f;
            return Mathf.Abs(a.px-b.px)<eps && Mathf.Abs(a.py-b.py)<eps && Mathf.Abs(a.pz-b.pz)<eps
                && Mathf.Abs(a.rx-b.rx)<eps && Mathf.Abs(a.ry-b.ry)<eps
                && Mathf.Abs(a.rz-b.rz)<eps && Mathf.Abs(a.rw-b.rw)<eps
                && Mathf.Abs(a.sx-b.sx)<eps && Mathf.Abs(a.sy-b.sy)<eps && Mathf.Abs(a.sz-b.sz)<eps;
        }
    }
}
