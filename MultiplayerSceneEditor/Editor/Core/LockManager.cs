using System.Collections.Generic;

namespace MultiplayerSceneEditor
{
    /// <summary>
    /// Tracks which user currently "owns" (has locked) each GameObject GUID.
    /// A lock is implicitly acquired when a user selects an object and released
    /// when they deselect it or disconnect.
    /// </summary>
    public class LockManager
    {
        // guid → userId
        private readonly Dictionary<string, string> _locks = new Dictionary<string, string>();

        public bool TryAcquire(string guid, string userId)
        {
            if (_locks.TryGetValue(guid, out string owner))
                return owner == userId;   // already owned by same user → ok
            _locks[guid] = userId;
            return true;
        }

        public void Release(string guid, string userId)
        {
            if (_locks.TryGetValue(guid, out string owner) && owner == userId)
                _locks.Remove(guid);
        }

        public void ReleaseAll(string userId)
        {
            var toRemove = new List<string>();
            foreach (var kv in _locks)
                if (kv.Value == userId) toRemove.Add(kv.Key);
            foreach (var k in toRemove) _locks.Remove(k);
        }

        public bool IsLocked(string guid) => _locks.ContainsKey(guid);

        public bool IsLockedByOther(string guid, string localUserId)
        {
            if (_locks.TryGetValue(guid, out string owner))
                return owner != localUserId;
            return false;
        }

        public string GetOwner(string guid)
        {
            _locks.TryGetValue(guid, out string owner);
            return owner;
        }

        public IReadOnlyDictionary<string, string> AllLocks => _locks;
    }
}
