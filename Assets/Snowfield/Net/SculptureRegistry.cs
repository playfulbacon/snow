using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// Logical identity for sculptures. The factory destroys and replaces GameObjects constantly
    /// (Promote/Regrow/Fuse), so network events reference these ids, not objects. Ids are minted lock-free by
    /// prefixing the creating peer's client id: (clientId &lt;&lt; 32) | counter.
    /// Tracks the churn through the <see cref="SculptureNet"/> lifecycle hooks.
    /// </summary>
    public sealed class SculptureRegistry
    {
        readonly Dictionary<ulong, SnowSculpture> _byId = new Dictionary<ulong, SnowSculpture>();
        readonly Dictionary<SnowSculpture, ulong> _ids = new Dictionary<SnowSculpture, ulong>();
        uint _next = 1;

        /// <summary>High half of every locally minted id; set to the NGO client id on session start.</summary>
        public uint LocalPrefix;

        /// <summary>When set, the next factory creation takes this id instead of minting one (remote replay).</summary>
        public ulong? PendingId;

        public int Count => _byId.Count;

        public void Attach()
        {
            SculptureNet.Created += OnCreated;
            SculptureNet.Replaced += OnReplaced;
            SculptureNet.Removed += OnRemoved;
        }

        public void Detach()
        {
            SculptureNet.Created -= OnCreated;
            SculptureNet.Replaced -= OnReplaced;
            SculptureNet.Removed -= OnRemoved;
        }

        ulong MintId() => ((ulong)LocalPrefix << 32) | _next++;

        void OnCreated(SnowSculpture s)
        {
            ulong id = PendingId ?? MintId();
            PendingId = null;
            Map(id, s);
        }

        void OnReplaced(SnowSculpture old, SnowSculpture replacement)
        {
            // The replacement was just Created inside the op and holds a fresh id; the old object's id is the
            // one every peer knows, so it migrates onto the replacement.
            if (!_ids.TryGetValue(old, out ulong id)) return;
            Unmap(old);
            Unmap(replacement);
            Map(id, replacement);
        }

        void OnRemoved(SnowSculpture s) => Unmap(s);

        void Map(ulong id, SnowSculpture s)
        {
            if (_byId.TryGetValue(id, out var existing) && existing != s && existing != null)
                Debug.LogWarning($"[SnowNet] Sculpture id {id:x} reassigned while still mapped");
            _byId[id] = s;
            _ids[s] = id;
        }

        void Unmap(SnowSculpture s)
        {
            if (!_ids.TryGetValue(s, out ulong id)) return;
            _ids.Remove(s);
            if (_byId.TryGetValue(id, out var mapped) && mapped == s) _byId.Remove(id);
        }

        public bool TryGet(ulong id, out SnowSculpture s)
        {
            if (_byId.TryGetValue(id, out s) && s != null) return true;
            if (s == null && _byId.ContainsKey(id)) { _byId.Remove(id); }
            s = null;
            return false;
        }

        public bool TryGetId(SnowSculpture s, out ulong id)
        {
            if (s != null && _ids.TryGetValue(s, out id)) return true;
            id = 0;
            return false;
        }

        /// <summary>Assign local ids to every live sculpture (host bootstrap: its field becomes the shared one).</summary>
        public void RegisterExisting()
        {
            foreach (var s in Object.FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
                if (s.Grid != null && s.Grid.IsCreated && !_ids.ContainsKey(s))
                    Map(MintId(), s);
        }

        /// <summary>Drop entries whose objects were destroyed outside the hooks (scene wipes, empty scoops).</summary>
        public void Sweep()
        {
            List<ulong> dead = null;
            foreach (var kv in _byId)
                if (kv.Value == null) (dead ??= new List<ulong>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var id in dead) _byId.Remove(id);
            List<SnowSculpture> deadKeys = null;
            foreach (var kv in _ids)
                if (kv.Key == null) (deadKeys ??= new List<SnowSculpture>()).Add(kv.Key);
            if (deadKeys != null) foreach (var k in deadKeys) _ids.Remove(k);
        }

        public void Clear()
        {
            _byId.Clear();
            _ids.Clear();
        }

        /// <summary>Live (id, sculpture) pairs; skips destroyed entries.</summary>
        public IEnumerable<KeyValuePair<ulong, SnowSculpture>> All()
        {
            foreach (var kv in _byId)
                if (kv.Value != null)
                    yield return kv;
        }
    }
}
