using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>A prefab-backed item. Definitions under Resources/Items are added to the accessory catalog.</summary>
    [CreateAssetMenu(menuName = "Snowfield/Accessory Item")]
    public sealed class AccessoryItemDefinition : ScriptableObject
    {
        public string itemId;
        public string displayName;
        [Min(1)] public int stackLimit = 6;
        [Tooltip("Visual/placement prefab, without an ItemPickup or SculptureProp component.")]
        public GameObject prefab;
        [Min(0f)] public float sink = 0.04f;
        public Vector3 groundEulerAngles = new Vector3(90f, 0f, 0f);
        [Min(0f)] public float groundLift;

        public AccessoryCatalog.Entry CreateEntry() => new AccessoryCatalog.Entry
        {
            Id = itemId, DisplayName = displayName, StackLimit = stackLimit,
            Sink = sink, GroundRest = Quaternion.Euler(groundEulerAngles), GroundLift = groundLift,
            Prefab = prefab, Build = () => Instantiate(prefab),
        };
    }
}
