using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>Finite item stacks in an inventory that grows on demand. Empty slots are reused.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerInventory : MonoBehaviour
    {
        struct Stack { public string itemId; public int count; }
        readonly List<Stack> _slots = new List<Stack>();
        public int SlotCount => _slots.Count;

        public int CountAt(int slot)
        {
            return slot >= 0 && slot < SlotCount ? _slots[slot].count : 0;
        }

        public string ItemIdAt(int slot)
        {
            return CountAt(slot) > 0 ? _slots[slot].itemId : null;
        }

        public AccessoryCatalog.Entry ItemAt(int slot) => AccessoryCatalog.Find(ItemIdAt(slot));

        public long Count(string itemId)
        {
            long total = 0;
            foreach (var stack in _slots)
                if (stack.itemId == itemId) total += stack.count;
            return total;
        }

        public int SpaceFor(string itemId)
        {
            return AccessoryCatalog.Find(itemId) != null ? int.MaxValue : 0;
        }

        /// <summary>Accepts the entire valid quantity, adding as many finite stacks as needed.</summary>
        public int Add(string itemId, int quantity)
        {
            var item = AccessoryCatalog.Find(itemId);
            if (item == null || quantity <= 0) return 0;
            int remaining = quantity;
            // Top up matching stacks before occupying empty slots, even if an empty slot comes first.
            for (int pass = 0; pass < 2 && remaining > 0; pass++)
            for (int i = 0; i < SlotCount && remaining > 0; i++)
            {
                var stack = _slots[i];
                bool eligible = pass == 0 ? stack.count > 0 && stack.itemId == itemId : stack.count == 0;
                if (!eligible) continue;
                int accepted = Mathf.Min(remaining, Mathf.Max(0, item.StackLimit - stack.count));
                if (accepted == 0) continue;
                stack.itemId = itemId;
                stack.count += accepted;
                _slots[i] = stack;
                remaining -= accepted;
            }
            while (remaining > 0)
            {
                int accepted = Mathf.Min(remaining, item.StackLimit);
                _slots.Add(new Stack { itemId = itemId, count = accepted });
                remaining -= accepted;
            }
            return quantity;
        }

        public bool TryConsume(string itemId)
        {
            return TryConsumeAt(FindFirstSlot(itemId));
        }

        public bool TryConsumeAt(int slot)
        {
            if (CountAt(slot) <= 0) return false;
            var stack = _slots[slot];
            stack.count--;
            if (stack.count == 0) stack.itemId = null;
            _slots[slot] = stack;
            return true;
        }

        public int FindFirstSlot(string itemId)
        {
            for (int i = 0; i < SlotCount; i++)
                if (_slots[i].count > 0 && _slots[i].itemId == itemId) return i;
            return -1;
        }
    }
}
