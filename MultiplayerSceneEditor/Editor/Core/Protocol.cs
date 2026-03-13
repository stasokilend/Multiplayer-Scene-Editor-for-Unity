using System;
using System.Text;
using UnityEngine;

namespace MultiplayerSceneEditor
{
    // ──────────────────────────────────────────────────────────────────────────
    //  Message type enum
    // ──────────────────────────────────────────────────────────────────────────

    public enum MsgType
    {
        Handshake       = 1,   // Client → Server  (first packet / join request)
        HandshakeAck    = 2,   // Server → Client  (welcome + scene snapshot)
        UserJoined      = 3,   // Server → All     (new user connected)
        UserLeft        = 4,   // Server → All     (user disconnected)
        TransformUpdate = 5,   // Any → All        (position/rotation/scale)
        HierarchyChange = 6,   // Any → All        (create/delete/reparent/rename)
        SelectionUpdate = 7,   // Any → All        (what a user has selected)
        LockRequest     = 8,   // Client → Server  (claim exclusive edit)
        LockGrant       = 9,   // Server → Client  (lock granted)
        LockDeny        = 10,  // Server → Client  (lock denied – already taken)
        LockRelease     = 11,  // Any → All        (lock released)
        CursorUpdate    = 12,  // Any → All        (3-D mouse position in scene)
        ComponentUpdate = 13,  // Any → All        (non-transform property change)
        ChatMessage     = 14,  // Any → All        (text chat)
        Ping            = 15,
        Pong            = 16,
        JoinDenied      = 17,  // Server → Client  (host rejected the join request)
        JoinPending     = 18,  // Server → Client  (waiting for host approval)
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Wire envelope
    // ──────────────────────────────────────────────────────────────────────────

    [Serializable]
    public class Envelope
    {
        public int    type;      // MsgType cast to int
        public string userId;    // sender
        public string payload;   // inner JSON (type-specific struct)
        public long   timestamp; // ms since epoch

        public static Envelope Create(MsgType t, string userId, string payload) => new Envelope
        {
            type      = (int)t,
            userId    = userId,
            payload   = payload,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Payload structs  (serialised with JsonUtility)
    // ──────────────────────────────────────────────────────────────────────────

    [Serializable]
    public class UserInfo
    {
        public string userId;
        public string displayName;
        public float  colorH;   // HSV hue [0-1] → unique user colour
    }

    [Serializable]
    public class HandshakePayload
    {
        public UserInfo user;
    }

    [Serializable]
    public class HandshakeAckPayload
    {
        public UserInfo       localUser;
        public UserInfo[]     presentUsers;
        public ObjSnapshot[]  sceneSnapshot;
    }

    [Serializable]
    public class TransformPayload
    {
        public string guid;
        public float px, py, pz;
        public float rx, ry, rz, rw;
        public float sx, sy, sz;
    }

    [Serializable]
    public class HierarchyPayload
    {
        // changeType: "create" | "delete" | "reparent" | "rename" | "active"
        public string changeType;
        public string guid;
        public string parentGuid;   // "" means scene root
        public string name;
        public bool   active;
        // Initial transform for "create"
        public float px, py, pz;
        public float rx, ry, rz, rw;
        public float sx, sy, sz;
    }

    [Serializable]
    public class SelectionPayload
    {
        public string[] guids;
    }

    [Serializable]
    public class LockPayload
    {
        public string guid;
        public string ownerUserId;
    }

    [Serializable]
    public class CursorPayload
    {
        public float x, y, z;  // world-space position
    }

    [Serializable]
    public class ComponentPayload
    {
        public string guid;
        public string componentTypeName;
        public string propertyPath;
        public string valueJson;
    }

    [Serializable]
    public class ChatPayload
    {
        public string message;
        public string displayName;
    }

    [Serializable]
    public class JoinDeniedPayload
    {
        public string reason;  // human-readable denial reason
    }

    [Serializable]
    public class ObjSnapshot
    {
        public string guid;
        public string name;
        public string parentGuid;
        public bool   active;
        public float px, py, pz;
        public float rx, ry, rz, rw;
        public float sx, sy, sz;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Serialisation helpers
    // ──────────────────────────────────────────────────────────────────────────

    public static class Protocol
    {
        private const int LENGTH_PREFIX = 4;

        // ── Encode ──────────────────────────────────────────────────────────
        public static byte[] Encode(Envelope env)
        {
            string  json  = JsonUtility.ToJson(env);
            byte[]  body  = Encoding.UTF8.GetBytes(json);
            byte[]  frame = new byte[LENGTH_PREFIX + body.Length];
            // Big-endian length prefix
            frame[0] = (byte)(body.Length >> 24);
            frame[1] = (byte)(body.Length >> 16);
            frame[2] = (byte)(body.Length >>  8);
            frame[3] = (byte)(body.Length);
            Buffer.BlockCopy(body, 0, frame, LENGTH_PREFIX, body.Length);
            return frame;
        }

        // ── Decode ──────────────────────────────────────────────────────────
        /// <summary>
        /// Reads one message from a stream buffer. Returns the Envelope and
        /// advances <paramref name="consumed"/> by the number of bytes used.
        /// Returns null when the buffer doesn't contain a full message.
        /// </summary>
        public static Envelope TryDecode(byte[] buf, int available, out int consumed)
        {
            consumed = 0;
            if (available < LENGTH_PREFIX) return null;

            int len = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
            if (available < LENGTH_PREFIX + len) return null;

            string json = Encoding.UTF8.GetString(buf, LENGTH_PREFIX, len);
            consumed = LENGTH_PREFIX + len;
            return JsonUtility.FromJson<Envelope>(json);
        }

        // ── Payload convenience ──────────────────────────────────────────────
        public static string Ser<T>(T obj) => JsonUtility.ToJson(obj);
        public static T     Des<T>(string json) => JsonUtility.FromJson<T>(json);

        // ── Transform helpers ────────────────────────────────────────────────
        public static TransformPayload ToPayload(string guid, Transform t) => new TransformPayload
        {
            guid = guid,
            px = t.position.x, py = t.position.y, pz = t.position.z,
            rx = t.rotation.x, ry = t.rotation.y, rz = t.rotation.z, rw = t.rotation.w,
            sx = t.localScale.x, sy = t.localScale.y, sz = t.localScale.z,
        };

        public static void ApplyPayload(TransformPayload p, Transform t)
        {
            t.position   = new Vector3(p.px, p.py, p.pz);
            t.rotation   = new Quaternion(p.rx, p.ry, p.rz, p.rw);
            t.localScale = new Vector3(p.sx, p.sy, p.sz);
        }

        public static ObjSnapshot ToSnapshot(string guid, GameObject go)
        {
            var parent     = go.transform.parent;
            string parentG = parent != null ? StableGuid.Get(parent.gameObject) ?? "" : "";
            var t          = go.transform;
            return new ObjSnapshot
            {
                guid       = guid,
                name       = go.name,
                parentGuid = parentG,
                active     = go.activeSelf,
                px = t.position.x, py = t.position.y, pz = t.position.z,
                rx = t.rotation.x, ry = t.rotation.y, rz = t.rotation.z, rw = t.rotation.w,
                sx = t.localScale.x, sy = t.localScale.y, sz = t.localScale.z,
            };
        }
    }
}
