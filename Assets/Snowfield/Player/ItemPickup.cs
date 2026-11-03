using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>A finite world stack. Scene authors can add this component and set itemId/quantity.</summary>
    [DisallowMultipleComponent]
    public sealed class ItemPickup : MonoBehaviour
    {
        [Tooltip("AccessoryCatalog id, including prefab item definitions in Resources/Items.")]
        public string itemId = "twig";
        [Min(1)] public int quantity = 1;
        [Tooltip("Optional model already authored in this pickup prefab. Otherwise built from the catalog at startup.")]
        public GameObject worldModel;

        public AccessoryCatalog.Entry Item => AccessoryCatalog.Find(itemId);
        public bool CanCollect(PlayerInventory inventory) => isActiveAndEnabled && quantity > 0
            && inventory != null && inventory.SpaceFor(itemId) > 0;

        void Start()
        {
            var entry = Item;
            if (entry == null || quantity <= 0)
            {
                Debug.LogWarning($"[Snowfield] Invalid pickup '{itemId}' on {name}", this);
                gameObject.SetActive(false);
                return;
            }
            var model = worldModel;
            if (model == null)
            {
                worldModel = model = entry.Build();
                model.transform.SetParent(transform, false);
                model.transform.localPosition = Vector3.up * entry.GroundLift;
                model.transform.localRotation = entry.GroundRest;
            }
            AccessoryCatalog.SetColliders(model, false);
            // A generous trigger makes small buttons/pebbles targetable without blocking movement or snowballs.
            var bounds = new Bounds(transform.position, Vector3.zero);
            foreach (var renderer in model.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(renderer.bounds);
            var target = GetComponent<BoxCollider>();
            if (target == null) target = gameObject.AddComponent<BoxCollider>();
            target.center = transform.InverseTransformPoint(bounds.center);
            Vector3 size = transform.InverseTransformVector(bounds.size);
            target.size = new Vector3(Mathf.Max(0.22f, Mathf.Abs(size.x)),
                Mathf.Max(0.18f, Mathf.Abs(size.y)), Mathf.Max(0.22f, Mathf.Abs(size.z)));
            target.isTrigger = true;
        }

        public int Collect(PlayerInventory inventory)
        {
            if (!CanCollect(inventory)) return 0;
            int accepted = inventory.Add(itemId, quantity);
            quantity -= accepted;
            if (quantity == 0)
            {
                // Disable immediately: another click in this frame cannot collect the same object twice.
                gameObject.SetActive(false);
                Destroy(gameObject);
            }
            return accepted;
        }

        public static ItemPickup Create(string id, int count, Vector3 position, Transform parent = null)
        {
            if (AccessoryCatalog.Find(id) == null || count <= 0) return null;
            var go = new GameObject("Pickup_" + id);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var pickup = go.AddComponent<ItemPickup>();
            pickup.itemId = id;
            pickup.quantity = count;
            return pickup;
        }
    }
}
