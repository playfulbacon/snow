using System.Collections.Generic;
using Snowfield.Config;
using Snowfield.Voxel;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// One sculpture: a VoxelGrid plus one child GameObject per chunk (MeshFilter/Renderer/Collider).
    /// Grid origin = this transform's position + <see cref="gridOffset"/> (local); grid axes = this transform's axes.
    /// Rotation is fine (snowballs roll); keep scale at 1.
    /// Call <see cref="Remesh"/> to rebuild dirty chunks; <see cref="RebuildColliders"/> on brush release.
    /// Every op that moves snow also moves its compaction (see <see cref="VoxelGrid"/>); <see cref="Version"/>
    /// ticks on every write so derived numbers (mean compaction) can be cached.
    /// </summary>
    public class SnowSculpture : MonoBehaviour, IBrushTarget
    {
        [SerializeField] SculptFeelConfig config;
        [SerializeField] Material snowMaterial;
        [Tooltip("Voxels per axis; 0 = config.gridSize. Snowballs use a small grid.")]
        public int gridSizeOverride = 0;
        [Tooltip("Metres per voxel; 0 = config.voxelSize. Set when loading a record saved at another resolution.")]
        public float voxelSizeOverride = 0f;
        [Tooltip("Local offset of the grid's min corner from this transform. Snowballs centre the grid on the transform.")]
        public Vector3 gridOffset = Vector3.zero;

        public VoxelGrid Grid { get; private set; }
        public VoxelGridInfo Info => Grid.Info;
        public SculptFeelConfig Config => config;
        public Material SnowMaterial => snowMaterial;
        /// <summary>Incremented by every write to the grid. Cache keys hang off this.</summary>
        public int Version { get; private set; }

        /// <summary>Editor/bootstrap hook for wiring references on a freshly added component.</summary>
        public void EditorAssign(SculptFeelConfig cfg, Material mat) { config = cfg; snowMaterial = mat; }

        /// <summary>Placed accessories; this list is the persisted props[] record.</summary>
        public IReadOnlyList<SculptureProp> Props => _props;
        readonly List<SculptureProp> _props = new List<SculptureProp>();
        public void RegisterProp(SculptureProp p) { if (!_props.Contains(p)) _props.Add(p); }
        public void UnregisterProp(SculptureProp p) => _props.Remove(p);

        MarchingCubesLookup _lookup;
        NativeArray<byte> _scratch;  // snapshot buffers for the smooth brush / rescale
        NativeArray<byte> _scratchC;
        Mesh[] _meshes;
        MeshFilter[] _filters;
        MeshCollider[] _colliders;
        readonly HashSet<int> _colliderDirty = new HashSet<int>();
        int _meanVersion = -1;
        float _meanCompaction;

        static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 1),
        };

        void Awake()
        {
            if (config == null) { Debug.LogError($"{name}: SnowSculpture needs a SculptFeelConfig", this); enabled = false; return; }
            Initialise(gridSizeOverride > 0 ? gridSizeOverride : config.gridSize,
                       voxelSizeOverride > 0f ? voxelSizeOverride : config.voxelSize);
        }

        public void Initialise(int size, float voxelSize)
        {
            Teardown();
            Grid = new VoxelGrid(size, voxelSize);
            _lookup = MarchingCubesLookup.Create(Allocator.Persistent);
            _scratch = new NativeArray<byte>(Grid.Info.VoxelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _scratchC = new NativeArray<byte>(Grid.Info.VoxelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            // Chunk objects are created lazily the first time a chunk has geometry (most of a 96³ grid stays empty).
            int n = Grid.Info.ChunkCount;
            _meshes = new Mesh[n];
            _filters = new MeshFilter[n];
            _colliders = new MeshCollider[n];
            Version++;
        }

        void EnsureChunkObject(int i)
        {
            if (_meshes[i] != null) return;
            int3 c = Grid.Info.ChunkCoord(i);
            var go = new GameObject($"Chunk_{c.x}_{c.y}_{c.z}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = gridOffset; // meshes are emitted from the grid's min corner
            go.layer = gameObject.layer;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = snowMaterial;
            var mc = go.AddComponent<MeshCollider>();
            mc.enabled = _collidersEnabled;
            var mesh = new Mesh { name = go.name };
            mesh.MarkDynamic();
            mf.sharedMesh = mesh;
            _meshes[i] = mesh; _filters[i] = mf; _colliders[i] = mc;
        }

        bool _collidersEnabled = true;

        void OnDestroy() => Teardown();

        void Teardown()
        {
            Grid?.Dispose(); Grid = null;
            if (_lookup.IsCreated) _lookup.Dispose();
            if (_scratch.IsCreated) _scratch.Dispose();
            if (_scratchC.IsCreated) _scratchC.Dispose();
            if (_meshes != null) foreach (var m in _meshes) if (m != null) Destroy(m);
            _meshes = null;
        }

        /// <summary>Mark a voxel AABB dirty and bump <see cref="Version"/>. Every write goes through here.</summary>
        public void Touch(int3 minVoxel, int3 maxVoxel)
        {
            Grid.MarkDirty(minVoxel, maxVoxel);
            Version++;
        }

        public void TouchAll()
        {
            Grid.MarkAllDirty();
            Version++;
        }

        // ---------- coordinate helpers ----------

        public float3 WorldToVoxel(float3 world) => ((float3)transform.InverseTransformPoint(world) - (float3)gridOffset) / Info.voxelSize;
        public float3 VoxelToWorld(float3 voxel) => transform.TransformPoint((Vector3)(voxel * Info.voxelSize) + gridOffset);
        /// <summary>A world direction expressed in voxel space (unit length in, unit length out: the grid is uniformly scaled).</summary>
        public float3 WorldToVoxelDir(float3 worldDir) => math.normalizesafe((float3)transform.InverseTransformDirection(worldDir), new float3(0, 1, 0));

        /// <summary>Trilinear density (0-255) at a world position; 0 outside the grid.</summary>
        public float SampleDensityWorld(float3 world) => DensitySampler.Trilinear(Grid.Density, Info, WorldToVoxel(world));

        /// <summary>Trilinear compaction (0-255) at a world position; 0 outside the grid or in air.</summary>
        public float SampleCompactionWorld(float3 world) => DensitySampler.Trilinear(Grid.Compaction, Info, WorldToVoxel(world));

        /// <summary>
        /// Compaction of the snow just under a surface point (0-255): the value the tools key their feel off.
        /// Samples a voxel inside the surface along the normal, then the best snow-holding neighbour, so a hit on
        /// the shoulder (where compaction is diluted by air) still reports the body's packing.
        /// </summary>
        public float CompactionUnderSurface(float3 worldPoint, float3 worldNormal)
        {
            float3 p = WorldToVoxel(worldPoint) - WorldToVoxelDir(worldNormal) * 1.5f;
            float best = 0f, bestMass = 0f;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int3 q = (int3)math.round(p) + new int3(dx, dy, dz);
                if (!Info.InBounds(q)) continue;
                int idx = Info.Index(q);
                float d = Grid.Density[idx];
                if (d > bestMass) { bestMass = d; best = Grid.Compaction[idx]; }
            }
            return best;
        }

        /// <summary>Axis-aligned world bounds of the grid cube (rotation-aware via the corners).</summary>
        public Bounds WorldBounds
        {
            get
            {
                float e = Info.WorldExtent;
                var b = new Bounds(VoxelToWorld(float3.zero), Vector3.zero);
                for (int i = 1; i < 8; i++)
                    b.Encapsulate(transform.TransformPoint(gridOffset + new Vector3((i & 1) * e, ((i >> 1) & 1) * e, ((i >> 2) & 1) * e)));
                return b;
            }
        }

        /// <summary>This grid's voxel space -> world.</summary>
        public float4x4 VoxelToWorldMatrix => math.mul((float4x4)transform.localToWorldMatrix,
            math.mul(float4x4.Translate(gridOffset), float4x4.Scale(Info.voxelSize)));

        /// <summary>World -> this grid's voxel space.</summary>
        public float4x4 WorldToVoxelMatrix => math.mul(float4x4.Scale(1f / Info.voxelSize),
            math.mul(float4x4.Translate(-(float3)gridOffset), (float4x4)transform.worldToLocalMatrix));

        /// <summary>
        /// Take the snow that a brush sphere overlaps out of <paramref name="source"/> and into this grid, shape and all.
        /// The caller removes the same kernel from the source, so nothing is created or lost.
        /// </summary>
        public void ExtractFrom(SnowSculpture source, float3 worldCentre, float radiusMetres, float shoulder)
        {
            if (source == null || source == this || source.Grid == null) return;
            float3 c = WorldToVoxel(worldCentre);
            float r = MetresToVoxels(radiusMetres);
            if (!Grid.SphereAabb(c, r, out var min, out var max)) return;
            int3 ext = max - min;
            new ExtractChunkJob
            {
                Density = Grid.Density, Compaction = Grid.Compaction, Info = Info,
                Source = source.Grid.Density, SourceCompaction = source.Grid.Compaction, SourceInfo = source.Info,
                AabbMin = min, AabbExtent = ext,
                ToSourceVoxel = math.mul(source.WorldToVoxelMatrix, VoxelToWorldMatrix),
                CenterVoxel = c, RadiusVoxels = r, Shoulder = shoulder,
            }.Schedule(ext.x * ext.y * ext.z, 128).Complete();
            Touch(min, max);
        }

        /// <summary>Volume of snow in this grid, in cubic metres (density-weighted).</summary>
        public float DensityVolume()
        {
            var result = new NativeArray<float>(1, Allocator.TempJob);
            new DensitySumJob { Density = Grid.Density, Result = result }.Schedule().Complete();
            float voxels = result[0];
            result.Dispose();
            float vs = Info.voxelSize;
            return voxels * vs * vs * vs;
        }

        /// <summary>Mass-weighted mean compaction of the whole grid (0-255). Cached per <see cref="Version"/>.</summary>
        public float MeanCompaction()
        {
            if (_meanVersion == Version) return _meanCompaction;
            var result = new NativeArray<float>(2, Allocator.TempJob);
            new MeanCompactionJob { Density = Grid.Density, Compaction = Grid.Compaction, Result = result }.Schedule().Complete();
            _meanCompaction = result[0];
            result.Dispose();
            _meanVersion = Version;
            return _meanCompaction;
        }

        /// <summary>Set the compaction of every snow voxel (new balls, legacy loads).</summary>
        public void FillCompaction(int value)
        {
            Grid.FillCompaction((byte)Mathf.Clamp(value, 0, 255));
            Version++;
        }

        /// <summary>Max-merge another sculpture's density into this grid, in world space (handles offset/rotation).</summary>
        public void Absorb(SnowSculpture other)
        {
            if (other == null || other == this || other.Grid == null) return;
            // Voxel range of this grid covered by the other's world bounds.
            var ob = other.WorldBounds;
            float3 lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? ob.min.x : ob.max.x, ((i >> 1) & 1) == 0 ? ob.min.y : ob.max.y, ((i >> 2) & 1) == 0 ? ob.min.z : ob.max.z);
                float3 v = WorldToVoxel(corner);
                lo = math.min(lo, v); hi = math.max(hi, v);
            }
            int3 min = math.clamp((int3)math.floor(lo), 0, Info.size - 1);
            int3 max = math.clamp((int3)math.ceil(hi) + 1, 0, Info.size);
            if (!math.all(max > min)) return;

            int3 ext = max - min;
            new AbsorbJob
            {
                Density = Grid.Density, Compaction = Grid.Compaction, Info = Info,
                Source = other.Grid.Density, SourceCompaction = other.Grid.Compaction, SourceInfo = other.Info,
                AabbMin = min, AabbExtent = ext, ToSourceVoxel = math.mul(other.WorldToVoxelMatrix, VoxelToWorldMatrix),
                WeldCompaction = config != null ? config.compactionWeld : 180f,
            }.Schedule(ext.x * ext.y * ext.z, 256).Complete();
            Touch(min, max);
        }

        /// <summary>Re-cook every chunk collider from scratch (after the sculpture moved while its colliders were off).</summary>
        public void ForceRebuildAllColliders()
        {
            if (_colliders == null) return;
            for (int i = 0; i < _colliders.Length; i++)
            {
                var mc = _colliders[i];
                if (mc == null) continue;
                var mesh = _meshes[i];
                mc.sharedMesh = null;
                mc.sharedMesh = mesh != null && mesh.vertexCount > 0 ? mesh : null;
            }
            _colliderDirty.Clear();
        }

        /// <summary>Drop the cooked meshes from every chunk collider (a dynamic Rigidbody may not carry concave meshes, even disabled ones).</summary>
        public void ClearColliderMeshes()
        {
            if (_colliders == null) return;
            foreach (var c in _colliders) if (c != null) c.sharedMesh = null;
        }

        /// <summary>True if a world-space sphere (plus a margin in voxels) fits inside the grid.</summary>
        public bool ContainsWorldSphere(float3 worldCentre, float radiusMetres, float marginVoxels)
        {
            float3 c = WorldToVoxel(worldCentre);
            float r = radiusMetres / Info.voxelSize + marginVoxels;
            return math.all(c - r >= 0f) && math.all(c + r <= Info.size - 1);
        }

        /// <summary>World-space AABB of the actual snow (non-empty density). Zero-size at the transform when empty.</summary>
        public Bounds SnowBoundsWorld()
        {
            var result = new NativeArray<int3>(2, Allocator.TempJob);
            new DensityBoundsJob { Density = Grid.Density, Info = Info, Result = result }.Schedule().Complete();
            int3 min = result[0], max = result[1];
            result.Dispose();
            if (math.any(min > max)) return new Bounds(transform.position, Vector3.zero);
            var b = new Bounds(VoxelToWorld((float3)min), Vector3.zero);
            for (int i = 1; i < 8; i++)
            {
                float3 corner = math.select((float3)min, (float3)(max + 1), new bool3((i & 1) != 0, (i & 2) != 0, (i & 4) != 0));
                b.Encapsulate(VoxelToWorld(corner));
            }
            return b;
        }

        /// <summary>Enable/disable every chunk MeshCollider (a flying ball uses a sphere instead).</summary>
        public void SetCollidersEnabled(bool on)
        {
            _collidersEnabled = on;
            if (_colliders == null) return;
            foreach (var c in _colliders) if (c != null) c.enabled = on;
        }
        public float MetresToVoxels(float metres) => metres / Info.voxelSize;

        // ---------- brush ops (synchronous; the AABBs are small) ----------

        public void ApplyAdd(float3 worldCenter, float radiusMetres, float ratePerTick, float shoulder)
            => ApplyAdd(worldCenter, radiusMetres, ratePerTick, shoulder, config != null ? config.compactionScooped : 40f);

        /// <summary>Raise (or with a negative rate, remove) density in a sphere; what arrives carries <paramref name="arrivalCompaction"/>.</summary>
        public void ApplyAdd(float3 worldCenter, float radiusMetres, float ratePerTick, float shoulder, float arrivalCompaction)
        {
            float3 c = WorldToVoxel(worldCenter);
            float r = MetresToVoxels(radiusMetres);
            if (!Grid.SphereAabb(c, r, out var min, out var max)) return;
            int3 ext = max - min;
            new AddBrushJob
            {
                Density = Grid.Density, Compaction = Grid.Compaction, Info = Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, RatePerTick = ratePerTick, Shoulder = shoulder,
                ArrivalCompaction = arrivalCompaction,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            Touch(min, max);
        }

        /// <summary>The pat: blur, plus the configured compaction gain and shrink.</summary>
        public void ApplySmooth(float3 worldCenter, float radiusMetres, float strength, float shoulder)
            => ApplySmooth(worldCenter, radiusMetres, strength, shoulder,
                           config != null ? config.patCompactionPerTick : 0f,
                           config != null ? config.packShrinkFraction : 0f);

        /// <summary>Blur with explicit compaction gain per tick (0 = a pure relaxation pass, e.g. the fluffy-shave slump).</summary>
        public void ApplySmooth(float3 worldCenter, float radiusMetres, float strength, float shoulder, float compactionPerTick, float shrinkFraction)
        {
            int k = config != null ? Mathf.Clamp(config.smoothKernelRadius, 1, 3) : 1;
            float3 c = WorldToVoxel(worldCenter);
            float r = MetresToVoxels(radiusMetres);
            if (!Grid.SphereAabb(c, r + k, out var min, out var max)) return; // +k so the snapshot covers the kernel
            int3 ext = max - min;
            int count = ext.x * ext.y * ext.z;
            var copy = new CopyRegionJob
            {
                Src = Grid.Density, Dst = _scratch, SrcB = Grid.Compaction, DstB = _scratchC,
                Info = Info, AabbMin = min, AabbExtent = ext,
            }.Schedule(count, 256);
            new SmoothBrushJob
            {
                Source = _scratch, SourceCompaction = _scratchC, Density = Grid.Density, Compaction = Grid.Compaction,
                Info = Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, Strength = strength, Shoulder = shoulder,
                KernelRadius = k, CompactionPerTick = compactionPerTick, ShrinkFraction = shrinkFraction,
            }.Schedule(count, 64, copy).Complete();
            Touch(min, max);
        }

        /// <summary>One-shot stamp. clipBelowWorldY: leave voxels below this world height untouched (hemisphere).</summary>
        public void StampSphere(float3 worldCenter, float radiusMetres, float shoulder, float clipBelowWorldY = float.NegativeInfinity)
            => StampSphere(worldCenter, radiusMetres, shoulder, config != null ? config.compactionRolled : 220f, clipBelowWorldY);

        public void StampSphere(float3 worldCenter, float radiusMetres, float shoulder, float compaction, float clipBelowWorldY)
        {
            float3 c = WorldToVoxel(worldCenter);
            float r = MetresToVoxels(radiusMetres);
            if (!Grid.SphereAabb(c, r, out var min, out var max)) return;
            int3 ext = max - min;
            float clipVoxelY = float.IsNegativeInfinity(clipBelowWorldY) ? -1e9f : WorldToVoxel(new float3(0, clipBelowWorldY, 0)).y;
            new SphereStampJob
            {
                Density = Grid.Density, Compaction = Grid.Compaction, Info = Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, Shoulder = shoulder, ClipBelowY = clipVoxelY, StampCompaction = compaction,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            Touch(min, max);
        }

        /// <summary>
        /// One shave stamp: a surface-relative disc at <paramref name="worldPoint"/> oriented by <paramref name="worldNormal"/>.
        /// Returns the density removed, in cubic metres, so the caller can shed it as crumbs (conservation).
        /// Also returns the voxel AABB it touched for the structural pass.
        /// </summary>
        public float ApplyShave(float3 worldPoint, float3 worldNormal, float radiusMetres, in ShaveParams p, out int3 aabbMin, out int3 aabbMax)
        {
            aabbMin = aabbMax = int3.zero;
            float3 c = WorldToVoxel(worldPoint);
            float3 n = WorldToVoxelDir(worldNormal);
            float r = MetresToVoxels(radiusMetres);
            float reach = math.max(r, p.DepthVoxels + p.SoftVoxels) + 1f;
            if (!Grid.SphereAabb(c, reach, out var min, out var max)) return 0f;
            int3 ext = max - min;
            int count = ext.x * ext.y * ext.z;
            var mass = new NativeArray<float>(2, Allocator.TempJob);
            var before = new RegionMassJob { Density = Grid.Density, Info = Info, AabbMin = min, AabbExtent = ext, Result = mass }.Schedule();
            var shave = new ShaveJob
            {
                Density = Grid.Density, Compaction = Grid.Compaction, Info = Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, NormalVoxel = n, RadiusVoxels = r, Params = p,
            }.Schedule(count, 64, before);
            var after = new NativeArray<float>(1, Allocator.TempJob);
            new RegionMassJob { Density = Grid.Density, Info = Info, AabbMin = min, AabbExtent = ext, Result = after }.Schedule(shave).Complete();
            float removedVoxels = math.max(0f, mass[0] - after[0]);
            mass.Dispose(); after.Dispose();
            Touch(min, max);
            aabbMin = min; aabbMax = max;
            float vs = Info.voxelSize;
            return removedVoxels * vs * vs * vs;
        }

        /// <summary>
        /// The squeeze: resample the whole grid toward its centre by <paramref name="linearScale"/> (&lt;1) and blend
        /// compaction toward fully packed. Balls only (their grid is centred on the transform).
        /// </summary>
        public void Squeeze(float linearScale, float compactionBlend)
        {
            linearScale = Mathf.Clamp(linearScale, 0.3f, 1f);
            int n = Info.VoxelCount;
            _scratch.CopyFrom(Grid.Density);
            _scratchC.CopyFrom(Grid.Compaction);
            new RescaleJob
            {
                Source = _scratch, SourceCompaction = _scratchC, Density = Grid.Density, Compaction = Grid.Compaction,
                Info = Info, CenterVoxel = WorldToVoxel(transform.position),
                InvScale = 1f / linearScale, CompactionBlend = compactionBlend,
            }.Schedule(n, 256).Complete();
            TouchAll();
        }

        /// <summary>Zero the voxels a region mask flags (the remote side of a thinness break, and the local one).</summary>
        public void ClearMasked(int3 regionMin, int3 regionExtent, NativeArray<byte> mask)
        {
            int count = regionExtent.x * regionExtent.y * regionExtent.z;
            if (count <= 0 || mask.Length < count) return;
            new ClearMaskedJob
            {
                Density = Grid.Density, Compaction = Grid.Compaction, Info = Info,
                RegionMin = regionMin, RegionExtent = regionExtent, Mask = mask,
            }.Schedule(count, 256).Complete();
            Touch(regionMin, regionMin + regionExtent);
        }

        /// <summary>Zero every voxel under an island grid's snow (voxel-aligned at <paramref name="offset"/>).</summary>
        public void ClearFromIsland(SnowSculpture island, int3 offset)
        {
            if (island == null || island.Grid == null) return;
            new ClearFromIslandJob
            {
                Island = island.Grid.Density, IslandInfo = island.Info,
                Density = Grid.Density, Compaction = Grid.Compaction, Info = Info, Offset = offset,
            }.Schedule(island.Info.VoxelCount, 256).Complete();
            TouchAll();
        }

        /// <summary>
        /// Twig armature segments in this grid's voxel space: from a little below each twig's snow contact point
        /// to its tip. Anything else in the catalog is decoration, not structure.
        /// </summary>
        public NativeArray<SupportSegment> SupportSegments(Allocator allocator, out int count)
        {
            count = 0;
            var list = new List<SupportSegment>();
            float below = config != null ? config.twigSupportBelow : 0.12f;
            foreach (var prop in _props)
            {
                if (prop == null || !AccessoryCatalog.IsArmature(prop.prefabId, out float length)) continue;
                Vector3 up = prop.transform.up;
                float3 a = WorldToVoxel(prop.transform.position - up * below);
                float3 b = WorldToVoxel(prop.transform.position + up * length);
                list.Add(new SupportSegment { A = a, B = b });
            }
            count = list.Count;
            var arr = new NativeArray<SupportSegment>(Mathf.Max(1, count), allocator);
            for (int i = 0; i < count; i++) arr[i] = list[i];
            return arr;
        }

        // ---------- meshing ----------

        /// <summary>Rebuild every dirty chunk's render mesh. Colliders are deferred to <see cref="RebuildColliders"/>.</summary>
        public void Remesh()
        {
            var dirty = new List<int>();
            for (int i = 0; i < Grid.ChunkDirty.Length; i++) if (Grid.ChunkDirty[i]) dirty.Add(i);
            if (dirty.Count == 0) return;

            var verts = new NativeList<SnowVertex>[dirty.Count];
            var inds = new NativeList<int>[dirty.Count];
            var handles = new NativeArray<JobHandle>(dirty.Count, Allocator.Temp);
            for (int k = 0; k < dirty.Count; k++)
            {
                verts[k] = new NativeList<SnowVertex>(4096, Allocator.TempJob);
                inds[k] = new NativeList<int>(8192, Allocator.TempJob);
                handles[k] = new MeshChunkJob
                {
                    Density = Grid.Density, Compaction = Grid.Compaction, Info = Info, ChunkCoord = Info.ChunkCoord(dirty[k]),
                    Lookup = _lookup, Vertices = verts[k], Indices = inds[k],
                }.Schedule();
            }
            JobHandle.CompleteAll(handles);
            handles.Dispose();

            for (int k = 0; k < dirty.Count; k++)
            {
                int ci = dirty[k];
                if (_meshes[ci] == null && verts[k].Length == 0) { Grid.ChunkDirty[ci] = false; verts[k].Dispose(); inds[k].Dispose(); continue; }
                EnsureChunkObject(ci);
                Upload(_meshes[ci], verts[k], inds[k]);
                verts[k].Dispose();
                inds[k].Dispose();
                Grid.ChunkDirty[ci] = false;
                _colliderDirty.Add(ci);
            }
        }

        static void Upload(Mesh mesh, NativeList<SnowVertex> verts, NativeList<int> inds)
        {
            int vc = verts.Length, ic = inds.Length;
            mesh.SetVertexBufferParams(vc, Layout);
            mesh.SetVertexBufferData(verts.AsArray(), 0, 0, vc, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.SetIndexBufferParams(ic, IndexFormat.UInt32);
            mesh.SetIndexBufferData(inds.AsArray(), 0, 0, ic, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, ic), MeshUpdateFlags.DontRecalculateBounds);
            mesh.RecalculateBounds();
        }

        /// <summary>Re-cook colliders for chunks remeshed since the last call. Expensive: call on brush release.</summary>
        public void RebuildColliders()
        {
            foreach (int ci in _colliderDirty)
            {
                var mc = _colliders[ci];
                if (mc == null) continue;
                var mesh = _meshes[ci];
                mc.sharedMesh = null;
                mc.sharedMesh = mesh.vertexCount > 0 ? mesh : null;
            }
            _colliderDirty.Clear();
        }

        /// <summary>Sculpture-local bounds of the whole grid, in metres.</summary>
        public Bounds LocalBounds => new Bounds(gridOffset + Vector3.one * (Info.WorldExtent * 0.5f), Vector3.one * Info.WorldExtent);

        void OnDrawGizmosSelected()
        {
            if (Grid == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.5f, 0.8f, 1f, 0.4f);
            Gizmos.DrawWireCube(LocalBounds.center, LocalBounds.size);
        }
    }
}
