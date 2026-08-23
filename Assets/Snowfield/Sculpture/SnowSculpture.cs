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
    /// Grid origin = this transform's position; grid axes = this transform's axes (keep it unrotated/unscaled).
    /// Call <see cref="Remesh"/> to rebuild dirty chunks; <see cref="RebuildColliders"/> on brush release.
    /// </summary>
    public class SnowSculpture : MonoBehaviour, IBrushTarget
    {
        [SerializeField] SculptFeelConfig config;
        [SerializeField] Material snowMaterial;

        public VoxelGrid Grid { get; private set; }
        public VoxelGridInfo Info => Grid.Info;
        public SculptFeelConfig Config => config;
        public Material SnowMaterial => snowMaterial;

        /// <summary>Editor/bootstrap hook for wiring references on a freshly added component.</summary>
        public void EditorAssign(SculptFeelConfig cfg, Material mat) { config = cfg; snowMaterial = mat; }

        /// <summary>Placed accessories; this list is the persisted props[] record.</summary>
        public IReadOnlyList<SculptureProp> Props => _props;
        readonly List<SculptureProp> _props = new List<SculptureProp>();
        public void RegisterProp(SculptureProp p) { if (!_props.Contains(p)) _props.Add(p); }
        public void UnregisterProp(SculptureProp p) => _props.Remove(p);

        MarchingCubesLookup _lookup;
        NativeArray<byte> _scratch; // snapshot buffer for the smooth brush
        Mesh[] _meshes;
        MeshFilter[] _filters;
        MeshCollider[] _colliders;
        readonly HashSet<int> _colliderDirty = new HashSet<int>();

        static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        };

        void Awake()
        {
            if (config == null) { Debug.LogError($"{name}: SnowSculpture needs a SculptFeelConfig", this); enabled = false; return; }
            Initialise(config.gridSize, config.voxelSize);
        }

        public void Initialise(int size, float voxelSize)
        {
            Teardown();
            Grid = new VoxelGrid(size, voxelSize);
            _lookup = MarchingCubesLookup.Create(Allocator.Persistent);
            _scratch = new NativeArray<byte>(Grid.Info.VoxelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            int n = Grid.Info.ChunkCount;
            _meshes = new Mesh[n];
            _filters = new MeshFilter[n];
            _colliders = new MeshCollider[n];
            for (int i = 0; i < n; i++)
            {
                int3 c = Grid.Info.ChunkCoord(i);
                var go = new GameObject($"Chunk_{c.x}_{c.y}_{c.z}");
                go.transform.SetParent(transform, false);
                go.layer = gameObject.layer;
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = snowMaterial;
                var mc = go.AddComponent<MeshCollider>();
                var mesh = new Mesh { name = go.name };
                mesh.MarkDynamic();
                mf.sharedMesh = mesh;
                _meshes[i] = mesh; _filters[i] = mf; _colliders[i] = mc;
            }
        }

        void OnDestroy() => Teardown();

        void Teardown()
        {
            Grid?.Dispose(); Grid = null;
            if (_lookup.IsCreated) _lookup.Dispose();
            if (_scratch.IsCreated) _scratch.Dispose();
            if (_meshes != null) foreach (var m in _meshes) if (m != null) Destroy(m);
            _meshes = null;
        }

        // ---------- coordinate helpers ----------

        public float3 WorldToVoxel(float3 world) => (float3)transform.InverseTransformPoint(world) / Info.voxelSize;
        public float3 VoxelToWorld(float3 voxel) => transform.TransformPoint((Vector3)(voxel * Info.voxelSize));
        public float MetresToVoxels(float metres) => metres / Info.voxelSize;

        // ---------- brush ops (synchronous; the AABBs are small) ----------

        public void ApplyAdd(float3 worldCenter, float radiusMetres, float ratePerTick, float shoulder)
        {
            float3 c = WorldToVoxel(worldCenter);
            float r = MetresToVoxels(radiusMetres);
            if (!Grid.SphereAabb(c, r, out var min, out var max)) return;
            int3 ext = max - min;
            new AddBrushJob
            {
                Density = Grid.Density, Info = Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, RatePerTick = ratePerTick, Shoulder = shoulder,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            Grid.MarkDirty(min, max);
        }

        public void ApplySmooth(float3 worldCenter, float radiusMetres, float strength, float shoulder)
        {
            float3 c = WorldToVoxel(worldCenter);
            float r = MetresToVoxels(radiusMetres);
            if (!Grid.SphereAabb(c, r + 1f, out var min, out var max)) return; // +1 so the snapshot covers neighbours
            int3 ext = max - min;
            int count = ext.x * ext.y * ext.z;
            var copy = new CopyRegionJob { Src = Grid.Density, Dst = _scratch, Info = Info, AabbMin = min, AabbExtent = ext }
                .Schedule(count, 256);
            new SmoothBrushJob
            {
                Source = _scratch, Density = Grid.Density, Info = Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, Strength = strength, Shoulder = shoulder,
            }.Schedule(count, 64, copy).Complete();
            Grid.MarkDirty(min, max);
        }

        /// <summary>One-shot stamp. clipBelowWorldY: leave voxels below this world height untouched (hemisphere).</summary>
        public void StampSphere(float3 worldCenter, float radiusMetres, float shoulder, float clipBelowWorldY = float.NegativeInfinity)
        {
            float3 c = WorldToVoxel(worldCenter);
            float r = MetresToVoxels(radiusMetres);
            if (!Grid.SphereAabb(c, r, out var min, out var max)) return;
            int3 ext = max - min;
            float clipVoxelY = float.IsNegativeInfinity(clipBelowWorldY) ? -1e9f : WorldToVoxel(new float3(0, clipBelowWorldY, 0)).y;
            new SphereStampJob
            {
                Density = Grid.Density, Info = Info, AabbMin = min, AabbExtent = ext,
                CenterVoxel = c, RadiusVoxels = r, Shoulder = shoulder, ClipBelowY = clipVoxelY,
            }.Schedule(ext.x * ext.y * ext.z, 64).Complete();
            Grid.MarkDirty(min, max);
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
                    Density = Grid.Density, Info = Info, ChunkCoord = Info.ChunkCoord(dirty[k]),
                    Lookup = _lookup, Vertices = verts[k], Indices = inds[k],
                }.Schedule();
            }
            JobHandle.CompleteAll(handles);
            handles.Dispose();

            for (int k = 0; k < dirty.Count; k++)
            {
                int ci = dirty[k];
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
                var mesh = _meshes[ci];
                mc.sharedMesh = null;
                mc.sharedMesh = mesh.vertexCount > 0 ? mesh : null;
            }
            _colliderDirty.Clear();
        }

        /// <summary>Sculpture-local bounds of the whole grid, in metres.</summary>
        public Bounds LocalBounds => new Bounds(Vector3.one * (Info.WorldExtent * 0.5f), Vector3.one * Info.WorldExtent);

        void OnDrawGizmosSelected()
        {
            if (Grid == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.5f, 0.8f, 1f, 0.4f);
            Gizmos.DrawWireCube(LocalBounds.center, LocalBounds.size);
        }
    }
}
