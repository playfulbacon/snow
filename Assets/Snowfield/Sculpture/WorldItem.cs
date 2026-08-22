using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>An accessory lying loose in the field, waiting to be found. Picking it up adds it to the inventory.</summary>
    public class WorldItem : MonoBehaviour
    {
        public string prefabId;

        /// <summary>Spawn an accessory resting on the ground at <paramref name="groundPoint"/>.</summary>
        public static WorldItem Spawn(AccessoryCatalog.Entry entry, Vector3 groundPoint, float yaw)
        {
            var go = entry.Build();
            go.name = "Item_" + entry.Id;
            AccessoryCatalog.SetColliders(go, true);
            go.transform.SetPositionAndRotation(groundPoint + Vector3.up * entry.GroundLift, Quaternion.Euler(0f, yaw, 0f) * entry.GroundRest);
            var item = go.AddComponent<WorldItem>();
            item.prefabId = entry.Id;
            return item;
        }
    }
}
