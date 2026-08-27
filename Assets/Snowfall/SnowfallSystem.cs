using UnityEngine;
using UnityEngine.Rendering;

namespace SnowDays
{
    /// <summary>
    /// Retro wrap-box snowfall, N64/GameCube style: a fixed budget of flakes
    /// simulated procedurally in the vertex shader inside a box that follows
    /// the camera. A visible flake holds its world position - moving only by
    /// fall, wind, and flutter - and wraps by a whole box stride when a box
    /// edge passes it, so the storm follows the player without the flakes
    /// ever riding along. Spawns itself on play like GameCubeLook; a
    /// scene-placed instance suppresses the bootstrap and exposes tuning.
    /// </summary>
    public class SnowfallSystem : MonoBehaviour
    {
        [Header("Coverage")]
        [SerializeField, Range(64, 8000)] private int m_FlakeCount = 1400;
        [SerializeField] private Vector3 m_BoxSize = new Vector3(26f, 16f, 26f);
        // Box center sits above the eye so most of the volume is falling sky.
        [SerializeField] private float m_BoxCenterHeight = 2f;

        [Header("Motion")]
        [SerializeField] private Vector2 m_FallSpeedRange = new Vector2(0.9f, 1.9f);
        [SerializeField] private Vector2 m_Wind = new Vector2(0.5f, 0.2f);
        [SerializeField] private float m_SwayAmplitude = 0.3f;
        [SerializeField] private float m_SwaySpeed = 1.2f;

        [Header("Look")]
        [SerializeField] private Vector2 m_FlakeSizeRange = new Vector2(0.04f, 0.1f);
        [SerializeField] private Color m_Tint = new Color(0.95f, 0.97f, 1f, 1f);
        [SerializeField, Range(0f, 1f)] private float m_LightInfluence = 0.6f;
        [SerializeField, Range(0f, 1f)] private float m_EdgeFadeStart = 0.65f;

        private static readonly int FollowPosId = Shader.PropertyToID("_SnowFollowPos");
        private static readonly int BoxId = Shader.PropertyToID("_SnowBox");
        private static readonly int MotionId = Shader.PropertyToID("_SnowMotion");
        private static readonly int FlakeId = Shader.PropertyToID("_SnowFlake");
        private static readonly int TintId = Shader.PropertyToID("_SnowTint");
        private static readonly int MiscId = Shader.PropertyToID("_SnowMisc");

        private Mesh m_Mesh;
        private Material m_Material;
        private MeshRenderer m_Renderer;
        private Transform m_Target;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<SnowfallSystem>() != null) return;
            var go = new GameObject("Snowfall");
            DontDestroyOnLoad(go);
            go.AddComponent<SnowfallSystem>();
        }

        private void OnEnable()
        {
            // Resources so the shader survives build stripping despite having
            // no serialized reference anywhere.
            Shader shader = Resources.Load<Shader>("RetroSnow");
            if (shader == null) shader = Shader.Find("SnowDays/RetroSnow");
            if (shader == null)
            {
                Debug.LogWarning("[Snowfall] RetroSnow shader not found; snow disabled.");
                enabled = false;
                return;
            }

            m_Material = new Material(shader) { name = "Snowfall (runtime)", hideFlags = HideFlags.DontSave };
            BuildMesh();

            MeshFilter filter = GetComponent<MeshFilter>();
            if (filter == null) filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = m_Mesh;

            m_Renderer = GetComponent<MeshRenderer>();
            if (m_Renderer == null) m_Renderer = gameObject.AddComponent<MeshRenderer>();
            m_Renderer.sharedMaterial = m_Material;
            m_Renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_Renderer.receiveShadows = false;
            m_Renderer.lightProbeUsage = LightProbeUsage.Off;
            m_Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            m_Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            m_Renderer.allowOcclusionWhenDynamic = false;

            // The shader emits world-space positions; the transform must stay identity.
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        private void OnDisable()
        {
            if (m_Material != null) Destroy(m_Material);
            if (m_Mesh != null) Destroy(m_Mesh);
            m_Material = null;
            m_Mesh = null;
        }

        private void LateUpdate()
        {
            if (m_Target == null)
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    m_Renderer.enabled = false;
                    return;
                }
                m_Target = cam.transform;
            }
            m_Renderer.enabled = true;

            Vector3 follow = m_Target.position + Vector3.up * m_BoxCenterHeight;
            m_Material.SetVector(FollowPosId, follow);
            m_Material.SetVector(BoxId, new Vector4(
                Mathf.Max(m_BoxSize.x, 1f), Mathf.Max(m_BoxSize.y, 1f), Mathf.Max(m_BoxSize.z, 1f), m_EdgeFadeStart));
            m_Material.SetVector(MotionId, new Vector4(m_Wind.x, m_Wind.y, m_FallSpeedRange.x, m_FallSpeedRange.y));
            m_Material.SetVector(FlakeId, new Vector4(m_FlakeSizeRange.x, m_FlakeSizeRange.y, m_SwayAmplitude, m_SwaySpeed));
            m_Material.SetColor(TintId, m_Tint);
            m_Material.SetVector(MiscId, new Vector4(m_LightInfluence, 0f, 0f, 0f));
        }

        private void OnValidate()
        {
            if (Application.isPlaying && m_Mesh != null && m_Mesh.vertexCount != m_FlakeCount * 4)
                BuildMesh();
        }

        // One quad per flake. Positions are dummies; the vertex shader derives
        // each flake's path from the per-quad seed in TEXCOORD1.
        private void BuildMesh()
        {
            if (m_Mesh == null)
                m_Mesh = new Mesh { name = "Snowfall", indexFormat = IndexFormat.UInt32 };
            m_Mesh.Clear();

            int n = m_FlakeCount;
            var vertices = new Vector3[n * 4];
            var corners = new Vector2[n * 4];
            var seeds = new Vector2[n * 4];
            var indices = new int[n * 6];
            var rng = new System.Random(1234);

            for (int i = 0; i < n; i++)
            {
                var seed = new Vector2((float)rng.NextDouble() * 64f, (float)rng.NextDouble() * 64f);
                int v = i * 4;
                corners[v + 0] = new Vector2(-1f, -1f);
                corners[v + 1] = new Vector2(1f, -1f);
                corners[v + 2] = new Vector2(1f, 1f);
                corners[v + 3] = new Vector2(-1f, 1f);
                seeds[v] = seeds[v + 1] = seeds[v + 2] = seeds[v + 3] = seed;

                int t = i * 6;
                indices[t + 0] = v;
                indices[t + 1] = v + 2;
                indices[t + 2] = v + 1;
                indices[t + 3] = v;
                indices[t + 4] = v + 3;
                indices[t + 5] = v + 2;
            }

            m_Mesh.vertices = vertices;
            m_Mesh.SetUVs(0, corners);
            m_Mesh.SetUVs(1, seeds);
            m_Mesh.SetTriangles(indices, 0, false);
            // Never cull: the shader places vertices anywhere around the camera.
            m_Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
        }
    }
}
