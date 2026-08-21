using Unity.Mathematics;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>Stamps the starter mound (a ground-clipped hemisphere) into a sculpture at startup.</summary>
    [RequireComponent(typeof(SnowSculpture))]
    public class SculptureSpawner : MonoBehaviour
    {
        [Tooltip("Mound radius in metres.")]
        public float moundRadius = 0.9f;
        [Tooltip("How far below the grid floor the sphere centre sits (0 = hemisphere, >0 = flatter mound).")]
        public float sink = 0.25f;
        [Range(0f, 1f)] public float shoulder = 0.7f;

        void Start() => SpawnNow();

        public void SpawnNow()
        {
            var s = GetComponent<SnowSculpture>();
            float extent = s.Info.WorldExtent;
            // Grid origin is the sculpture's min corner; the mound sits on the grid floor, centred in XZ.
            float3 centre = (float3)transform.position + new float3(extent * 0.5f, -sink, extent * 0.5f);
            s.StampSphere(centre, moundRadius, shoulder, clipBelowWorldY: transform.position.y);
            s.Remesh();
            s.RebuildColliders();
        }
    }
}
