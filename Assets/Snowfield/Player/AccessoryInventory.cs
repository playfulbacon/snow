using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>What the player is carrying, by accessory id. Found items go in; placed items come out.</summary>
    public class AccessoryInventory : MonoBehaviour
    {
        [System.Serializable]
        public struct StartingStack { public string id; public int count; }

        [Tooltip("Items the player begins with (debug convenience; the real game starts empty and forages).")]
        public List<StartingStack> starting = new List<StartingStack>();

        readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        void Awake()
        {
            foreach (var e in AccessoryCatalog.Entries) _counts[e.Id] = 0;
            foreach (var s in starting) if (_counts.ContainsKey(s.id)) _counts[s.id] += Mathf.Max(0, s.count);
        }

        public int Count(string id) => _counts.TryGetValue(id, out var n) ? n : 0;
        public bool Has(string id) => Count(id) > 0;

        public void Add(string id, int n = 1)
        {
            if (!_counts.ContainsKey(id)) return;
            _counts[id] += n;
        }

        public bool TryTake(string id)
        {
            if (!Has(id)) return false;
            _counts[id]--;
            return true;
        }
    }
}
