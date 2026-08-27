using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SnowDays
{
    /// <summary>
    /// Deformable snow cover for the whole terrain. A world-anchored square
    /// window follows the player; inside it live two fields: a CPU-sampled
    /// terrain-height texture the snow mesh drapes over, and a GPU trample
    /// map that footsteps stamp depressions into. When the player nears the
    /// window edge the window scrolls (the trample RT is shifted GPU-side,
    /// the height texture refilled in strips), so deformation works anywhere
    /// on the terrain without a terrain-sized texture. A player-following
    /// grid mesh - dense near the player, coarse further out, with skirts
    /// hiding the LOD seams - is displaced in the SnowSurface vertex shader.
    /// Bootstraps itself on play like SnowfallSystem; a scene-placed
    /// instance suppresses the bootstrap and exposes tuning.
    /// </summary>
    [DefaultExecutionOrder(1000)] // drain stamps after everyone has enqueued
    public class SnowDeformSystem : MonoBehaviour
    {
        [Header("Coverage")]
        // Side length of the sliding deformation window, in meters.
        [SerializeField] private float m_WindowSize = 80f;
        [SerializeField] private int m_TrampleResolution = 2048;
        [SerializeField] private int m_HeightResolution = 512;
        // Recenter the window when the player drifts this far from its center.
        [SerializeField] private float m_ScrollMargin = 2f;

        [Header("Snow")]
        // 0.127 m = 5 inches.
        [SerializeField] private float m_SnowDepth = 0.127f;
        // Fraction of the depth a full trample removes; the rest stays as a
        // compressed floor so prints never z-fight the terrain underneath.
        [SerializeField, Range(0.5f, 1f)] private float m_Compression = 0.85f;
        [SerializeField] private float m_RimHeight = 0.035f;
        // Clearance keeps even fully-faded snow off the terrain surface.
        [SerializeField] private float m_MinClearance = 0.02f;
        // Seconds for snowfall to fully refill a print. 0 disables refill.
        [SerializeField] private float m_RefillSeconds = 600f;

        [Header("Mesh")]
        [SerializeField] private float m_InnerHalfExtent = 16f;
        [SerializeField] private float m_InnerSpacing = 0.1f;
        [SerializeField] private float m_OuterHalfExtent = 36f;
        [SerializeField] private float m_OuterSpacing = 0.4f;
        [SerializeField] private float m_SkirtDepth = 0.6f;
        [SerializeField] private float m_FadeStart = 33f;
        [SerializeField] private float m_FadeEnd = 35.5f;

        [Header("Look")]
        // Tiling diffuse for the shell. Left empty, the terrain's own snow
        // layer (name containing "snow") is used so both surfaces match.
        [SerializeField] private Texture2D m_SnowTexture;
        [SerializeField] private float m_SnowTextureTile = 32f;
        [SerializeField] private Color m_Albedo = new Color(0.93f, 0.95f, 0.99f);
        [SerializeField] private Color m_TrenchAlbedo = new Color(0.72f, 0.78f, 0.90f);
        [SerializeField, Range(0f, 1f)] private float m_TrenchDarkening = 0.45f;
        // Diffuse light steps (retro toon bands); every band multiplies the
        // live light color, so time-of-day drives the palette.
        [SerializeField, Range(2, 6)] private int m_LightBands = 3;
        // Multiplies the ambient probe so shadowed snow reads cold.
        [SerializeField] private Color m_ShadowTint = new Color(0.72f, 0.82f, 1.0f);
        [SerializeField] private bool m_CastShadows = true;

        private static readonly int WindowId = Shader.PropertyToID("_SnowWindow");
        private static readonly int ShapeId = Shader.PropertyToID("_SnowShape");
        private static readonly int FadeId = Shader.PropertyToID("_SnowFade");
        private static readonly int TexelsId = Shader.PropertyToID("_SnowTexels");
        private static readonly int TrampleTexId = Shader.PropertyToID("_SnowTrampleTex");
        private static readonly int HeightTexId = Shader.PropertyToID("_SnowHeightTex");
        private static readonly int PrevTrampleId = Shader.PropertyToID("_SnowPrevTrample");
        private static readonly int MaintId = Shader.PropertyToID("_SnowMaint");
        private static readonly int StampParamsId = Shader.PropertyToID("_StampParams");
        private static readonly int WriteInvId = Shader.PropertyToID("_SnowWriteInv");
        private static readonly int SnowAlbedoId = Shader.PropertyToID("_SnowAlbedo");
        private static readonly int TrenchAlbedoId = Shader.PropertyToID("_SnowTrenchAlbedo");
        private static readonly int TrenchAOId = Shader.PropertyToID("_SnowTrenchAO");
        private static readonly int ShadowTintId = Shader.PropertyToID("_SnowShadowTint");
        private static readonly int LightBandsId = Shader.PropertyToID("_SnowLightBands");
        private static readonly int BaseMapId = Shader.PropertyToID("_SnowBaseMap");
        private static readonly int TexTilingId = Shader.PropertyToID("_SnowTexTiling");

        private struct StampRequest
        {
            public Vector2 position;   // world XZ center
            public Vector2 direction;  // world XZ, normalized
            public float length;
            public float width;
            public float strength;
            public float softness;
            public float noise;
        }

        private struct TerrainTile
        {
            public Terrain terrain;
            public Rect rect;   // world XZ footprint
            public float baseY;
        }

        public static SnowDeformSystem Instance { get; private set; }

        /// <summary>Undisturbed snow shell thickness in metres.</summary>
        public float SnowDepth => m_SnowDepth;
        /// <summary>Fraction of the depth a full trample removes (the rest is the compressed floor).</summary>
        public float Compression => m_Compression;
        /// <summary>True once the deformation window around the target is filled and sampleable.</summary>
        public bool WindowReady => m_WindowValid;

        private Transform m_Target;
        private float m_NextTargetSearch;

        private RenderTexture m_TrampleA;
        private RenderTexture m_TrampleB;
        private RenderTexture m_TrampleCurrent;
        private Texture2D m_HeightTex;
        private float[] m_Heights;
        private float[] m_HeightsScratch;
        // CPU mirror of the trample map at height-field resolution, kept in
        // step with the RT (stamps, scroll shifts, decay). Coarse but enough
        // for gameplay queries: snow-surface height and "is this fresh?".
        private float[] m_Trample01;
        private float[] m_TrampleScratch;
        private Vector2 m_Origin;           // world XZ of the window min corner
        private bool m_WindowValid;
        private int m_FillRow = -1;         // time-sliced initial fill cursor; -1 = not filling
        private float m_PendingDecay;

        // Rows of the height field sampled per frame during the initial
        // fill; 512 rows / 64 = 8 frames instead of one 30-80ms hitch.
        private const int FillRowsPerFrame = 64;

        private Material m_SurfaceMat;
        private Material m_StampMat;
        private Material m_MaintMat;
        private Mesh m_SnowMesh;
        private Mesh m_UnitQuad;
        private Transform m_MeshTransform;
        private MeshRenderer m_MeshRenderer;
        private CommandBuffer m_Cmd;
        private MaterialPropertyBlock m_StampProps;
        private readonly List<StampRequest> m_Stamps = new List<StampRequest>(32);

        private TerrainTile[] m_Tiles;
        private int m_LastTile;

        private float HeightTexel => m_WindowSize / m_HeightResolution;
        private float TrampleTexel => m_WindowSize / m_TrampleResolution;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<SnowDeformSystem>() != null) return;
            var go = new GameObject("SnowDeform");
            DontDestroyOnLoad(go);
            go.AddComponent<SnowDeformSystem>();
        }

        /// <summary>
        /// Stamp a depression into the snow. Position is world space; dir is
        /// the print's long axis in world XZ. Strength 1 tramples to the
        /// compressed floor; softness/noise shape the edge.
        /// </summary>
        public void Stamp(Vector3 position, Vector2 direction, float length, float width,
            float strength = 1f, float softness = 0.35f, float noise = 0.3f)
        {
            if (m_Stamps.Count >= 256) return;
            if (direction.sqrMagnitude < 1e-6f) direction = Vector2.up;
            m_Stamps.Add(new StampRequest
            {
                position = new Vector2(position.x, position.z),
                direction = direction.normalized,
                length = length,
                width = width,
                strength = Mathf.Clamp01(strength),
                softness = Mathf.Clamp01(softness),
                noise = noise,
            });
        }

        /// <summary>
        /// Terrain height under a world XZ point, bilinearly sampled from the
        /// window's CPU height field. False until the window exists or when
        /// the point is outside it.
        /// </summary>
        public bool TryGetGroundHeight(float x, float z, out float height)
        {
            height = 0f;
            if (!m_WindowValid) return false;
            float u = (x - m_Origin.x) / HeightTexel - 0.5f;
            float v = (z - m_Origin.y) / HeightTexel - 0.5f;
            int i0 = Mathf.FloorToInt(u);
            int j0 = Mathf.FloorToInt(v);
            if (i0 < 0 || j0 < 0 || i0 + 1 >= m_HeightResolution || j0 + 1 >= m_HeightResolution)
                return false;
            float fu = u - i0;
            float fv = v - j0;
            int res = m_HeightResolution;
            float h00 = m_Heights[j0 * res + i0];
            float h10 = m_Heights[j0 * res + i0 + 1];
            float h01 = m_Heights[(j0 + 1) * res + i0];
            float h11 = m_Heights[(j0 + 1) * res + i0 + 1];
            height = Mathf.Lerp(Mathf.Lerp(h00, h10, fu), Mathf.Lerp(h01, h11, fu), fv);
            return true;
        }

        /// <summary>
        /// Trample amount 0..1 at a world XZ point, from the CPU mirror of
        /// the trample map. 0 outside the window (treated as fresh snow).
        /// </summary>
        public float SampleTrample01(float x, float z)
        {
            if (!m_WindowValid || m_Trample01 == null) return 0f;
            float u = (x - m_Origin.x) / HeightTexel - 0.5f;
            float v = (z - m_Origin.y) / HeightTexel - 0.5f;
            int i0 = Mathf.FloorToInt(u);
            int j0 = Mathf.FloorToInt(v);
            if (i0 < 0 || j0 < 0 || i0 + 1 >= m_HeightResolution || j0 + 1 >= m_HeightResolution)
                return 0f;
            float fu = u - i0;
            float fv = v - j0;
            int res = m_HeightResolution;
            float t00 = m_Trample01[j0 * res + i0];
            float t10 = m_Trample01[j0 * res + i0 + 1];
            float t01 = m_Trample01[(j0 + 1) * res + i0];
            float t11 = m_Trample01[(j0 + 1) * res + i0 + 1];
            return Mathf.Lerp(Mathf.Lerp(t00, t10, fu), Mathf.Lerp(t01, t11, fu), fv);
        }

        /// <summary>
        /// World Y of the visible snow surface (terrain + trample-aware shell
        /// offset, matching the SnowSurface vertex displacement). False until
        /// the window exists or when the point is outside it.
        /// </summary>
        public bool TrySampleSnowSurface(float x, float z, out float height)
        {
            if (!TryGetGroundHeight(x, z, out height)) return false;
            float t = SampleTrample01(x, z);
            height += m_SnowDepth * (1f - m_Compression * Mathf.Clamp01(t)) + m_MinClearance;
            return true;
        }

        private void OnEnable()
        {
            Instance = this;

            Shader surface = Resources.Load<Shader>("SnowSurface");
            Shader stamp = Resources.Load<Shader>("SnowStamp");
            Shader maint = Resources.Load<Shader>("SnowMaintenance");
            if (surface == null || stamp == null || maint == null)
            {
                Debug.LogWarning("[SnowDeform] Shaders not found in Resources; snow deformation disabled.");
                enabled = false;
                return;
            }

            // The scroll quantum is one height texel; keeping the trample
            // resolution an exact multiple means scrolls are also whole
            // trample texels, so shifted prints never blur.
            m_HeightResolution = Mathf.Clamp(m_HeightResolution, 64, 1024);
            int ratio = Mathf.Max(1, m_TrampleResolution / m_HeightResolution);
            m_TrampleResolution = m_HeightResolution * ratio;

            m_SurfaceMat = new Material(surface) { name = "SnowSurface (runtime)", hideFlags = HideFlags.DontSave };
            m_StampMat = new Material(stamp) { name = "SnowStamp (runtime)", hideFlags = HideFlags.DontSave };
            m_MaintMat = new Material(maint) { name = "SnowMaintenance (runtime)", hideFlags = HideFlags.DontSave };
            m_StampMat.SetFloat(WriteInvId, 1f / m_WindowSize);
            m_MaintMat.SetFloat(WriteInvId, 1f / m_WindowSize);
            ApplyLook();

            m_TrampleA = CreateTrampleRT("SnowTrampleA");
            m_TrampleB = CreateTrampleRT("SnowTrampleB");
            m_TrampleCurrent = m_TrampleA;
            ClearRT(m_TrampleA);
            ClearRT(m_TrampleB);

            m_HeightTex = new Texture2D(m_HeightResolution, m_HeightResolution, TextureFormat.RFloat, false, true)
            {
                name = "SnowHeight",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            m_Heights = new float[m_HeightResolution * m_HeightResolution];
            m_HeightsScratch = new float[m_Heights.Length];
            m_Trample01 = new float[m_Heights.Length];
            m_TrampleScratch = new float[m_Heights.Length];

            m_Cmd = new CommandBuffer { name = "SnowDeform" };
            m_StampProps = new MaterialPropertyBlock();
            m_UnitQuad = BuildUnitQuad();
            BuildSnowMesh();
            CacheTerrains();
            ApplySnowTexture();
            m_WindowValid = false;
            m_FillRow = -1;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // This object survives scene loads; the terrain cache and the whole
        // deformation window don't.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive) m_Target = null;
            CacheTerrains();
            InvalidateWindow();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Instance == this) Instance = null;
            if (m_MeshTransform != null) Destroy(m_MeshTransform.gameObject);
            Destroy(m_SurfaceMat);
            Destroy(m_StampMat);
            Destroy(m_MaintMat);
            Destroy(m_SnowMesh);
            Destroy(m_UnitQuad);
            Destroy(m_HeightTex);
            if (m_TrampleA != null) m_TrampleA.Release();
            if (m_TrampleB != null) m_TrampleB.Release();
            Destroy(m_TrampleA);
            Destroy(m_TrampleB);
            m_Cmd?.Release();
            m_Cmd = null;
        }

        private void LateUpdate()
        {
            if (!ResolveTarget()) return;

            Vector3 targetPos = m_Target.position;
            bool scrolled = UpdateWindow(targetPos);

            // No snow until the height field holds real terrain data - a
            // fresh Texture2D's contents are undefined.
            m_MeshRenderer.enabled = m_WindowValid;
            if (!m_WindowValid) return;

            if (m_RefillSeconds > 0.01f)
                m_PendingDecay += Time.deltaTime / m_RefillSeconds;

            // RHalf loses increments below ~0.001 near 1.0, so bank decay
            // until it survives the subtraction.
            bool wantDecay = m_PendingDecay >= 0.002f;
            RunGpuWork(scrolled, wantDecay);

            // Keep the mesh on a coarse-grid lattice so vertices resample the
            // fields at stable phases.
            float snap = m_OuterSpacing;
            var meshPos = new Vector3(
                Mathf.Round(targetPos.x / snap) * snap, 0f,
                Mathf.Round(targetPos.z / snap) * snap);
            m_MeshTransform.position = meshPos;

            Shader.SetGlobalVector(WindowId, new Vector4(m_Origin.x, m_Origin.y, 1f / m_WindowSize, m_WindowSize));
            Shader.SetGlobalVector(ShapeId, new Vector4(m_SnowDepth, m_Compression, m_RimHeight, m_MinClearance));
            Shader.SetGlobalVector(FadeId, new Vector4(m_FadeStart, m_FadeEnd, meshPos.x, meshPos.z));
            Shader.SetGlobalVector(TexelsId, new Vector4(TrampleTexel, HeightTexel, m_SkirtDepth, 0f));
            Shader.SetGlobalTexture(TrampleTexId, m_TrampleCurrent);
            Shader.SetGlobalTexture(HeightTexId, m_HeightTex);
        }

        private bool ResolveTarget()
        {
            if (m_Target != null) return true;
            if (Time.unscaledTime < m_NextTargetSearch) return false;
            m_NextTargetSearch = Time.unscaledTime + 1f;

            var player = FindAnyObjectByType<PlayerController>();
            if (player != null)
            {
                m_Target = player.transform;
                if (player.GetComponent<SnowFootprints>() == null)
                    player.gameObject.AddComponent<SnowFootprints>();
            }
            else if (Camera.main != null)
            {
                m_Target = Camera.main.transform;
            }
            if (m_Target != null)
                Debug.Log($"[SnowDeform] following '{m_Target.name}' at {m_Target.position}");
            return m_Target != null;
        }

        // Recenters the window on the target when needed. Returns true when
        // the window moved this frame (the trample RT then needs a shift).
        private bool UpdateWindow(Vector3 targetPos)
        {
            float half = m_WindowSize * 0.5f;
            float texel = HeightTexel;

            if (!m_WindowValid)
            {
                // Time-sliced initial fill; restart if the target wanders off
                // the origin chosen when the fill began.
                if (m_FillRow < 0
                    || Mathf.Abs(targetPos.x - (m_Origin.x + half)) > m_ScrollMargin
                    || Mathf.Abs(targetPos.z - (m_Origin.y + half)) > m_ScrollMargin)
                {
                    m_Origin = new Vector2(
                        Mathf.Floor((targetPos.x - half) / texel) * texel,
                        Mathf.Floor((targetPos.z - half) / texel) * texel);
                    m_FillRow = 0;
                    Debug.Log($"[SnowDeform] filling height window at origin {m_Origin}");
                }
                int end = Mathf.Min(m_FillRow + FillRowsPerFrame, m_HeightResolution);
                RefillHeights(0, m_FillRow, m_HeightResolution, end);
                m_FillRow = end;
                if (m_FillRow >= m_HeightResolution)
                {
                    PushHeights();
                    m_WindowValid = true;
                    m_FillRow = -1;
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (float h in m_Heights) { if (h < lo) lo = h; if (h > hi) hi = h; }
                    Debug.Log($"[SnowDeform] window ready, heights {lo:F2}..{hi:F2}, tiles={m_Tiles.Length}");
                }
                return false; // RTs start cleared; nothing to shift
            }

            if (Mathf.Abs(targetPos.x - (m_Origin.x + half)) <= m_ScrollMargin
                && Mathf.Abs(targetPos.z - (m_Origin.y + half)) <= m_ScrollMargin)
                return false;

            var newOrigin = new Vector2(
                Mathf.Floor((targetPos.x - half) / texel) * texel,
                Mathf.Floor((targetPos.z - half) / texel) * texel);

            int dx = Mathf.RoundToInt((newOrigin.x - m_Origin.x) / texel);
            int dz = Mathf.RoundToInt((newOrigin.y - m_Origin.y) / texel);
            if (dx == 0 && dz == 0) return false;

            if (Mathf.Abs(dx) >= m_HeightResolution || Mathf.Abs(dz) >= m_HeightResolution)
            {
                // Teleport beyond the window: old prints are meaningless,
                // restart the fill fresh at the new location.
                InvalidateWindow();
                return false;
            }

            newOrigin = m_Origin + new Vector2(dx * texel, dz * texel);
            ShiftHeights(dx, dz);
            m_Origin = newOrigin;
            PushHeights();
            m_LastScrollTexels = new Vector2Int(dx, dz);
            return true;
        }

        private void InvalidateWindow()
        {
            m_WindowValid = false;
            m_FillRow = -1;
            m_PendingDecay = 0f;
            m_Stamps.Clear();
            if (m_Trample01 != null) System.Array.Clear(m_Trample01, 0, m_Trample01.Length);
            if (m_TrampleA != null) ClearRT(m_TrampleA);
            if (m_TrampleB != null) ClearRT(m_TrampleB);
        }

        private Vector2Int m_LastScrollTexels;

        // Executes the frame's RT work in one command buffer: an optional
        // shift/decay ping-pong followed by the queued stamps.
        private void RunGpuWork(bool scrolled, bool wantDecay)
        {
            if (!m_WindowValid) { m_Stamps.Clear(); return; }
            bool maintenance = scrolled || wantDecay;
            if (!maintenance && m_Stamps.Count == 0) return;

            m_Cmd.Clear();

            if (maintenance)
            {
                RenderTexture src = m_TrampleCurrent;
                RenderTexture dst = m_TrampleCurrent == m_TrampleA ? m_TrampleB : m_TrampleA;
                float ratio = (float)m_TrampleResolution / m_HeightResolution;
                Vector2 uvOffset = scrolled
                    ? new Vector2(m_LastScrollTexels.x * ratio / m_TrampleResolution,
                                  m_LastScrollTexels.y * ratio / m_TrampleResolution)
                    : Vector2.zero;
                float decay = wantDecay ? m_PendingDecay : 0f;
                if (wantDecay) m_PendingDecay = 0f;

                // Keep the CPU mirror decaying in step with the RT.
                if (decay > 0f && m_Trample01 != null)
                    for (int i = 0; i < m_Trample01.Length; i++)
                        m_Trample01[i] = Mathf.Max(0f, m_Trample01[i] - decay);

                m_MaintMat.SetTexture(PrevTrampleId, src);
                m_MaintMat.SetVector(MaintId, new Vector4(uvOffset.x, uvOffset.y, decay, 0f));

                m_Cmd.SetRenderTarget(dst, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                var maintTRS = Matrix4x4.TRS(
                    new Vector3(m_WindowSize * 0.5f, m_WindowSize * 0.5f, 0f),
                    Quaternion.identity,
                    new Vector3(m_WindowSize, m_WindowSize, 1f));
                m_Cmd.DrawMesh(m_UnitQuad, maintTRS, m_MaintMat, 0, 0);
                m_TrampleCurrent = dst;
            }

            if (m_Stamps.Count > 0)
            {
                m_Cmd.SetRenderTarget(m_TrampleCurrent, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                foreach (var s in m_Stamps)
                {
                    Vector2 local = s.position - m_Origin;
                    float angle = Mathf.Atan2(s.direction.y, s.direction.x) * Mathf.Rad2Deg;
                    var trs = Matrix4x4.TRS(
                        new Vector3(local.x, local.y, 0f),
                        Quaternion.AngleAxis(angle, Vector3.forward),
                        new Vector3(s.length, s.width, 1f));
                    m_StampProps.SetVector(StampParamsId, new Vector4(s.strength, s.softness, s.noise, 0f));
                    m_Cmd.DrawMesh(m_UnitQuad, trs, m_StampMat, 0, 0, m_StampProps);
                    SplatMirror(s);
                }
                m_Stamps.Clear();
            }

            Graphics.ExecuteCommandBuffer(m_Cmd);
        }

        // Max-blends a stamp into the CPU trample mirror, approximating the
        // oriented ellipse as a circle of the mean radius. The mirror texel
        // (~16 cm) is coarser than a footprint anyway; gameplay queries only
        // need "roughly how trampled is it here".
        private void SplatMirror(in StampRequest s)
        {
            if (m_Trample01 == null) return;
            float texel = HeightTexel;
            int res = m_HeightResolution;
            float cx = (s.position.x - m_Origin.x) / texel - 0.5f;
            float cz = (s.position.y - m_Origin.y) / texel - 0.5f;
            float r = Mathf.Max(0.5f, 0.25f * (s.length + s.width) / texel);
            int i0 = Mathf.Max(0, Mathf.FloorToInt(cx - r));
            int i1 = Mathf.Min(res - 1, Mathf.CeilToInt(cx + r));
            int j0 = Mathf.Max(0, Mathf.FloorToInt(cz - r));
            int j1 = Mathf.Min(res - 1, Mathf.CeilToInt(cz + r));
            float soft = Mathf.Max(s.softness, 1e-3f);
            for (int j = j0; j <= j1; j++)
            {
                int row = j * res;
                for (int i = i0; i <= i1; i++)
                {
                    float dx = i - cx;
                    float dz = j - cz;
                    // Treat each cell as half a texel wide: a stamp smaller than a texel still registers
                    // fully in its containing cell instead of vanishing between cell centers.
                    float d = Mathf.Max(0f, Mathf.Sqrt(dx * dx + dz * dz) - 0.5f) / r;
                    if (d >= 1f) continue;
                    // Matches SnowStamp.shader: profile = 1 - smoothstep(1 - soft, 1, d).
                    float x = Mathf.Clamp01((d - (1f - soft)) / soft);
                    float profile = 1f - x * x * (3f - 2f * x);
                    float value = s.strength * profile;
                    if (value > m_Trample01[row + i]) m_Trample01[row + i] = value;
                }
            }
        }

        // ---- Terrain height field ----

        private void CacheTerrains()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            m_LastTile = 0;
            m_Tiles = new TerrainTile[terrains.Length];
            for (int i = 0; i < terrains.Length; i++)
            {
                Vector3 pos = terrains[i].transform.position;
                Vector3 size = terrains[i].terrainData.size;
                m_Tiles[i] = new TerrainTile
                {
                    terrain = terrains[i],
                    rect = new Rect(pos.x, pos.z, size.x, size.z),
                    baseY = pos.y,
                };
            }
            if (m_Tiles.Length == 0)
                Debug.LogWarning("[SnowDeform] No active terrains; snow will lie at y=0.");
        }

        private float SampleTerrainHeight(float x, float z)
        {
            if (m_Tiles == null || m_Tiles.Length == 0) return 0f;

            // Spatial locality: the last tile usually still contains us.
            var p = new Vector2(x, z);
            if (m_Tiles[m_LastTile].rect.Contains(p))
                return SampleTile(m_LastTile, x, z);
            for (int i = 0; i < m_Tiles.Length; i++)
            {
                if (!m_Tiles[i].rect.Contains(p)) continue;
                m_LastTile = i;
                return SampleTile(i, x, z);
            }

            // Off the terrain: extend the nearest tile's edge flatly.
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < m_Tiles.Length; i++)
            {
                Rect r = m_Tiles[i].rect;
                float cx = Mathf.Clamp(x, r.xMin, r.xMax);
                float cz = Mathf.Clamp(z, r.yMin, r.yMax);
                float d = (cx - x) * (cx - x) + (cz - z) * (cz - z);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            Rect br = m_Tiles[best].rect;
            return SampleTile(best,
                Mathf.Clamp(x, br.xMin, br.xMax),
                Mathf.Clamp(z, br.yMin, br.yMax));
        }

        private float SampleTile(int index, float x, float z)
        {
            ref TerrainTile tile = ref m_Tiles[index];
            return tile.baseY + tile.terrain.SampleHeight(new Vector3(x, 0f, z));
        }

        // Fills heights for texel range [x0,x1) x [z0,z1) at the current origin.
        private void RefillHeights(int x0, int z0, int x1, int z1)
        {
            float texel = HeightTexel;
            int res = m_HeightResolution;
            for (int j = z0; j < z1; j++)
            {
                float wz = m_Origin.y + (j + 0.5f) * texel;
                int row = j * res;
                for (int i = x0; i < x1; i++)
                    m_Heights[row + i] = SampleTerrainHeight(m_Origin.x + (i + 0.5f) * texel, wz);
            }
        }

        // Shifts the height array by (dx, dz) texels (window moving +x means
        // content moves -x) and samples the newly exposed strips. Callers
        // guarantee |dx|, |dz| < resolution (larger jumps restart the fill).
        private void ShiftHeights(int dx, int dz)
        {
            int res = m_HeightResolution;
            for (int j = 0; j < res; j++)
            {
                int srcJ = j + dz;
                int row = j * res;
                if (srcJ < 0 || srcJ >= res)
                    continue; // filled below
                int srcRow = srcJ * res;
                for (int i = 0; i < res; i++)
                {
                    int srcI = i + dx;
                    m_HeightsScratch[row + i] = (srcI >= 0 && srcI < res) ? m_Heights[srcRow + srcI] : float.NaN;
                }
            }

            (m_Heights, m_HeightsScratch) = (m_HeightsScratch, m_Heights);

            // Shift the trample mirror the same way; exposed strips are fresh
            // snow (0), matching the maintenance shader's out-of-bounds rule.
            if (m_Trample01 != null)
            {
                for (int j = 0; j < res; j++)
                {
                    int srcJ = j + dz;
                    int row = j * res;
                    if (srcJ < 0 || srcJ >= res)
                    {
                        System.Array.Clear(m_TrampleScratch, row, res);
                        continue;
                    }
                    int srcRow = srcJ * res;
                    for (int i = 0; i < res; i++)
                    {
                        int srcI = i + dx;
                        m_TrampleScratch[row + i] = (srcI >= 0 && srcI < res) ? m_Trample01[srcRow + srcI] : 0f;
                    }
                }
                (m_Trample01, m_TrampleScratch) = (m_TrampleScratch, m_Trample01);
            }

            // Advance the origin, then sample every texel the shift exposed.
            Vector2 shifted = m_Origin + new Vector2(dx * HeightTexel, dz * HeightTexel);
            Vector2 saved = m_Origin;
            m_Origin = shifted;
            if (dx > 0) RefillHeights(res - dx, 0, res, res);
            else if (dx < 0) RefillHeights(0, 0, -dx, res);
            if (dz > 0) RefillHeights(0, res - dz, res, res);
            else if (dz < 0) RefillHeights(0, 0, res, -dz);
            m_Origin = saved;
        }

        private void PushHeights()
        {
            m_HeightTex.SetPixelData(m_Heights, 0);
            m_HeightTex.Apply(false, false);
        }

        // ---- GPU resources ----

        private RenderTexture CreateTrampleRT(string rtName)
        {
            var rt = new RenderTexture(m_TrampleResolution, m_TrampleResolution, 0, RenderTextureFormat.RHalf)
            {
                name = rtName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.DontSave,
            };
            rt.Create();
            return rt;
        }

        private static void ClearRT(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        private static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh { name = "SnowUnitQuad", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) });
            mesh.SetIndices(new[] { 0, 2, 1, 1, 2, 3 }, MeshTopology.Triangles, 0);
            return mesh;
        }

        // Binds the tiling snow diffuse: the serialized override if set, else
        // the diffuse of the terrain's own snow-named layer (matching its
        // tile size), else the first painted layer. No layer -> flat color.
        private void ApplySnowTexture()
        {
            Texture2D tex = m_SnowTexture;
            float tile = m_SnowTextureTile;
            if (tex == null && m_Tiles != null)
            {
                TerrainLayer best = null;
                foreach (var t in m_Tiles)
                {
                    TerrainLayer[] layers = t.terrain.terrainData.terrainLayers;
                    if (layers == null) continue;
                    foreach (var layer in layers)
                    {
                        if (layer == null || layer.diffuseTexture == null) continue;
                        if (best == null) best = layer;
                        if (layer.name.ToLowerInvariant().Contains("snow")) { best = layer; break; }
                    }
                    if (best != null && best.name.ToLowerInvariant().Contains("snow")) break;
                }
                if (best != null)
                {
                    tex = best.diffuseTexture;
                    tile = Mathf.Max(best.tileSize.x, 0.01f);
                    Debug.Log($"[SnowDeform] snow texture from terrain layer '{best.name}': {tex.name} (tile {tile}m)");
                }
            }
            if (tex != null)
            {
                m_SurfaceMat.SetTexture(BaseMapId, tex);
                m_SurfaceMat.SetFloat(TexTilingId, tile);
            }
        }

        private void ApplyLook()
        {
            m_SurfaceMat.SetColor(SnowAlbedoId, m_Albedo);
            m_SurfaceMat.SetColor(TrenchAlbedoId, m_TrenchAlbedo);
            m_SurfaceMat.SetFloat(TrenchAOId, m_TrenchDarkening);
            m_SurfaceMat.SetColor(ShadowTintId, m_ShadowTint);
            m_SurfaceMat.SetFloat(LightBandsId, m_LightBands);
        }

        private void OnValidate()
        {
            if (m_SurfaceMat != null) ApplyLook();
            if (m_MeshRenderer != null)
                m_MeshRenderer.shadowCastingMode = m_CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        // ---- Snow mesh ----

        // Grid layout: a dense inner square for crisp prints near the player,
        // a coarse outer ring for the middle distance, and double-sided
        // vertical skirts at every resolution boundary so the T-junctions
        // between rings can never open into visible cracks. Vertex Y encodes
        // the skirt drop (0 = surface, -1 = skirt bottom); the shader turns
        // it into meters.
        private void BuildSnowMesh()
        {
            var verts = new List<Vector3>(160000);
            var indices = new List<int>(900000);

            AppendGrid(verts, indices, m_InnerHalfExtent, m_InnerSpacing, 0f);
            AppendGrid(verts, indices, m_OuterHalfExtent, m_OuterSpacing, m_InnerHalfExtent);

            AppendSkirtSquare(verts, indices, m_InnerHalfExtent, m_InnerSpacing);
            AppendSkirtSquare(verts, indices, m_InnerHalfExtent, m_OuterSpacing);
            AppendSkirtSquare(verts, indices, m_OuterHalfExtent, m_OuterSpacing);

            m_SnowMesh = new Mesh
            {
                name = "SnowSurfaceGrid",
                hideFlags = HideFlags.DontSave,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            m_SnowMesh.SetVertices(verts);
            m_SnowMesh.SetIndices(indices, MeshTopology.Triangles, 0);
            // Displacement happens in the shader; bounds must cover any
            // terrain height the window can slide over.
            m_SnowMesh.bounds = new Bounds(Vector3.zero,
                new Vector3(m_OuterHalfExtent * 2f + 2f, 4000f, m_OuterHalfExtent * 2f + 2f));

            var go = new GameObject("SnowSurface");
            go.transform.SetParent(transform, false);
            m_MeshTransform = go.transform;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = m_SnowMesh;
            m_MeshRenderer = go.AddComponent<MeshRenderer>();
            m_MeshRenderer.sharedMaterial = m_SurfaceMat;
            m_MeshRenderer.shadowCastingMode = m_CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            m_MeshRenderer.receiveShadows = true;
            m_MeshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            m_MeshRenderer.enabled = false; // until the height window is filled
        }

        // Square grid of quads out to halfExtent; cells fully inside holeHalf
        // are skipped (0 = no hole).
        private static void AppendGrid(List<Vector3> verts, List<int> indices,
            float halfExtent, float spacing, float holeHalf)
        {
            int cells = Mathf.RoundToInt(halfExtent * 2f / spacing);
            int vertsPerRow = cells + 1;
            int baseIndex = verts.Count;

            for (int j = 0; j <= cells; j++)
                for (int i = 0; i <= cells; i++)
                    verts.Add(new Vector3(-halfExtent + i * spacing, 0f, -halfExtent + j * spacing));

            for (int j = 0; j < cells; j++)
            {
                for (int i = 0; i < cells; i++)
                {
                    if (holeHalf > 0f)
                    {
                        float x0 = -halfExtent + i * spacing;
                        float z0 = -halfExtent + j * spacing;
                        if (x0 >= -holeHalf - 0.001f && x0 + spacing <= holeHalf + 0.001f
                            && z0 >= -holeHalf - 0.001f && z0 + spacing <= holeHalf + 0.001f)
                            continue;
                    }
                    int v0 = baseIndex + j * vertsPerRow + i;
                    int v1 = v0 + 1;
                    int v2 = v0 + vertsPerRow;
                    int v3 = v2 + 1;
                    indices.Add(v0); indices.Add(v2); indices.Add(v1);
                    indices.Add(v1); indices.Add(v2); indices.Add(v3);
                }
            }
        }

        // Double-sided vertical skirt around the square |x|,|z| = halfExtent.
        private static void AppendSkirtSquare(List<Vector3> verts, List<int> indices,
            float halfExtent, float spacing)
        {
            int cells = Mathf.RoundToInt(halfExtent * 2f / spacing);
            for (int edge = 0; edge < 4; edge++)
            {
                int baseIndex = verts.Count;
                for (int i = 0; i <= cells; i++)
                {
                    float t = -halfExtent + i * spacing;
                    Vector3 top = edge switch
                    {
                        0 => new Vector3(t, 0f, -halfExtent),
                        1 => new Vector3(t, 0f, halfExtent),
                        2 => new Vector3(-halfExtent, 0f, t),
                        _ => new Vector3(halfExtent, 0f, t),
                    };
                    verts.Add(top);
                    verts.Add(new Vector3(top.x, -1f, top.z));
                }
                for (int i = 0; i < cells; i++)
                {
                    int v0 = baseIndex + i * 2;      // top i
                    int v1 = v0 + 1;                 // bottom i
                    int v2 = v0 + 2;                 // top i+1
                    int v3 = v0 + 3;                 // bottom i+1
                    // Both windings: crack filler must be visible from
                    // either side, and per-edge orientation isn't worth
                    // tracking for a dozen quads.
                    indices.Add(v0); indices.Add(v1); indices.Add(v2);
                    indices.Add(v2); indices.Add(v1); indices.Add(v3);
                    indices.Add(v0); indices.Add(v2); indices.Add(v1);
                    indices.Add(v2); indices.Add(v3); indices.Add(v1);
                }
            }
        }
    }
}
