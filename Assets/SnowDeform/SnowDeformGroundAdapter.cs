using Snowfield.Player;
using UnityEngine;

namespace SnowDays
{
    /// <summary>
    /// Presents SnowDeformSystem to the Snowfield sculpting tools as their ground-snow surface
    /// (Snowfield.Player.SnowGround). Scooping a handful off the ground, snowball roll trenches and any other
    /// StampDepression call become trample stamps on the deform window; height/freshness queries read the
    /// system's CPU mirrors. Lives in Assembly-CSharp because asmdefs cannot reference it, so the dependency
    /// is inverted: this side registers itself into the Snowfield-side static.
    /// Bootstraps itself on play like SnowDeformSystem; a scene-placed instance suppresses the bootstrap.
    /// </summary>
    public class SnowDeformGroundAdapter : MonoBehaviour, ISnowGroundBackend
    {
        [Tooltip("Grain added to stamp edges so scoops and trenches read as dug snow, not clean punches.")]
        [SerializeField, Range(0f, 1f)] private float m_StampNoise = 0.2f;
        [Tooltip("Trample above this fraction reads as 'not fresh' (blocks snowball growth). The trample channel saturates at footprint strength, so this is a feel knob, not metres — keep it generous: the sculpting feel is the product.")]
        [SerializeField, Range(0f, 1f)] private float m_FreshTrampleLimit = 0.9f;

        private static readonly RaycastHit[] s_FloorHits = new RaycastHit[8];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<SnowDeformGroundAdapter>() != null) return;
            var go = new GameObject("SnowGroundAdapter");
            DontDestroyOnLoad(go);
            go.AddComponent<SnowDeformGroundAdapter>();
        }

        private void OnEnable()
        {
            if (SnowGround.Instance == null) SnowGround.Instance = this;
        }

        private void OnDisable()
        {
            if (ReferenceEquals(SnowGround.Instance, this)) SnowGround.Instance = null;
        }

        public bool IsCreated
        {
            get
            {
                var sys = SnowDeformSystem.Instance;
                return sys != null && sys.WindowReady;
            }
        }

        public float SampleHeight(Vector3 world)
        {
            var sys = SnowDeformSystem.Instance;
            float surface = float.NegativeInfinity;
            if (sys != null && !sys.TrySampleSnowSurface(world.x, world.z, out surface))
                surface = float.NegativeInfinity;
            // The snow shell has no collider and the shell height is terrain-based, but town geometry
            // (porches, the frozen-lake plane) can stand above it: ride whichever is higher. Sculpture snow
            // is skipped — the old heightmap deliberately ignored it ("a raycast can land on another
            // sculpture's snow and inflate the ground height").
            int n = Physics.RaycastNonAlloc(new Ray(world + Vector3.up * 3f, Vector3.down), s_FloorHits, 8f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float floor = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
                if (s_FloorHits[i].collider.GetComponentInParent<Snowfield.Sculpture.SnowSculpture>() == null
                    && s_FloorHits[i].point.y > floor)
                    floor = s_FloorHits[i].point.y;
            float best = Mathf.Max(surface, floor);
            return float.IsNegativeInfinity(best) ? world.y : best;
        }

        public bool IsFreshAt(Vector3 world, float tolerance)
        {
            var sys = SnowDeformSystem.Instance;
            if (sys == null || !sys.WindowReady) return true; // no data: treat as fresh, like the old terrain did
            // The old heightmap compared metres below fresh, where a footprint was only 0.02 m deep. SnowDays
            // footprints trample at full strength, so a pure metre comparison would halt snowball growth on
            // every walked path; gate on the trample fraction instead, taking the more generous of the two.
            float removed = sys.SnowDepth * sys.Compression * sys.SampleTrample01(world.x, world.z);
            return removed <= Mathf.Max(tolerance, sys.SnowDepth * sys.Compression * m_FreshTrampleLimit);
        }

        public void StampDepression(Vector3 worldCenter, float radiusMetres, float depthMetres, float shoulder)
        {
            var sys = SnowDeformSystem.Instance;
            if (sys == null) return;
            // The trample channel is 0..1 with a hard floor at the compressed depth; map the requested depth
            // onto that range. Snowfield's falloff has a flat core inside `shoulder`, the stamp shader's core
            // ends where its smoothstep starts: softness = 1 - shoulder.
            float maxDepth = Mathf.Max(sys.SnowDepth * sys.Compression, 1e-4f);
            float strength = Mathf.Clamp01(depthMetres / maxDepth);
            if (strength <= 0f || radiusMetres <= 0f) return;
            sys.Stamp(worldCenter, Vector2.right, radiusMetres * 2f, radiusMetres * 2f,
                strength, Mathf.Clamp01(1f - shoulder), m_StampNoise);
        }
    }
}
