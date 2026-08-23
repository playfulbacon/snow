using Snowfield.Config;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Creates sculptures at runtime (a snowball stacked on another, or a snowball brushed in Sculpt mode).
    /// Scene singleton on the "Sculptures" root; new sculptures are parented under it.
    /// </summary>
    public class SculptureFactory : MonoBehaviour
    {
        public static SculptureFactory Instance { get; private set; }

        public SculptFeelConfig config;
        public Material snowMaterial;
        [Tooltip("Parent for created sculptures. Defaults to this transform.")]
        public Transform container;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Grid extent in metres (one axis).</summary>
        public float Extent => config.gridSize * config.voxelSize;

        /// <summary>New empty sculpture whose grid is centred in XZ on <paramref name="groundCentre"/> with its floor at that height.</summary>
        public SnowSculpture CreateAt(Vector3 groundCentre)
        {
            float extent = Extent;
            var go = new GameObject("Sculpture");
            go.SetActive(false); // assign refs before Awake runs
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.position = groundCentre - new Vector3(extent * 0.5f, 0f, extent * 0.5f);
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            go.SetActive(true);
            return s;
        }
    }
}
