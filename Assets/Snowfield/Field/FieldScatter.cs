using System.Collections.Generic;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Field
{
    /// <summary>Scatters loose accessories across the field at startup for the player to find.</summary>
    public class FieldScatter : MonoBehaviour
    {
        [System.Serializable]
        public struct Stack { public string id; public int count; }

        public List<Stack> items = new List<Stack>
        {
            new Stack { id = "twig", count = 10 },
            new Stack { id = "carrot", count = 3 },
            new Stack { id = "button", count = 8 },
            new Stack { id = "pebble", count = 8 },
        };
        [Tooltip("Items land within this radius of this object (m).")]
        public float radius = 12f;
        [Tooltip("Keep this clear around the centre so the starter mound is not littered.")]
        public float innerRadius = 2.5f;
        public int seed = 0;
        public LayerMask groundMask = ~0;

        void Start() => Scatter();

        [ContextMenu("Scatter Now")]
        public void Scatter()
        {
            var rng = seed == 0 ? new System.Random() : new System.Random(seed);
            foreach (var stack in items)
            {
                var entry = AccessoryCatalog.Find(stack.id);
                if (entry == null) { Debug.LogWarning($"[FieldScatter] unknown accessory '{stack.id}'"); continue; }
                for (int i = 0; i < stack.count; i++)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float r = Mathf.Lerp(innerRadius, radius, Mathf.Sqrt((float)rng.NextDouble()));
                    Vector3 p = transform.position + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (Physics.Raycast(p + Vector3.up * 10f, Vector3.down, out var hit, 20f, groundMask, QueryTriggerInteraction.Ignore))
                        p = hit.point;
                    var item = WorldItem.Spawn(entry, p, (float)rng.NextDouble() * 360f);
                    item.transform.SetParent(transform, true);
                }
            }
        }
    }
}
