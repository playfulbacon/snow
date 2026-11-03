using System.Collections;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>Small, finite supply scatter so existing scenes immediately support collecting accessories.</summary>
    [DisallowMultipleComponent]
    public sealed class ItemPickupSpawner : MonoBehaviour
    {
        [Tooltip("Disable when the level has hand-placed ItemPickup components instead.")]
        public bool spawnNearbySupplies = true;
        [Min(1f)] public float radius = 3f;

        IEnumerator Start()
        {
            if (!spawnNearbySupplies) yield break;
            // Let the player and ground adapter finish startup before sampling the visible snow.
            yield return null;
            float deadline = Time.time + 5f;
            while ((SnowGround.Instance == null || !SnowGround.Instance.IsCreated) && Time.time < deadline)
                yield return null;
            var tool = GetComponent<SculptTool>();
            var body = tool != null && tool.reachOrigin != null ? tool.reachOrigin : transform;
            var origin = body.position;
            var root = new GameObject("AccessorySupplies");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, gameObject.scene);
            for (int i = 0; i < AccessoryCatalog.Entries.Count; i++)
            {
                // A fan in front of spawn; one stack per type, with no timer or automatic refill.
                float angle = Mathf.Lerp(-65f, 65f, i / (float)Mathf.Max(1, AccessoryCatalog.Entries.Count - 1));
                Vector3 direction = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
                if (direction.sqrMagnitude < 0.01f) direction = Vector3.forward;
                direction = Quaternion.AngleAxis(angle, Vector3.up) * direction;
                Vector3 point = origin + direction * radius;
                var ground = SnowGround.Instance;
                if (ground != null && ground.IsCreated) point.y = ground.SampleHeight(point);
                else if (Physics.Raycast(point + Vector3.up * 3f, Vector3.down, out var hit, 10f,
                             Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) point.y = hit.point.y;
                else continue; // never leave supplies floating when this scene has no ground
                var entry = AccessoryCatalog.Entries[i];
                ItemPickup.Create(entry.Id, Mathf.Max(1, entry.StackLimit / 3), point + Vector3.up * 0.02f, root.transform);
            }
        }
    }
}
