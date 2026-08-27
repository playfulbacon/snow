using System.Collections.Generic;
using Snowfield.Config;
using Snowfield.Voxel;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Snowfield.Field
{
    /// <summary>
    /// The field: a chunked CPU heightmap of snow depth relative to the untouched surface (0). The brush raises,
    /// carves and smooths it (drawing in snow); footsteps and rolling snowballs press paths into it.
    /// Origin = this transform (min corner); +X/+Z across the field. Chunk meshes are generated children that
    /// are never saved (rebuilt on load), so the scene file stays small. Runs in edit mode so the ground is visible.
    /// </summary>
    [ExecuteAlways]
    public class SnowTerrain : MonoBehaviour, IBrushTarget
    {
        public static SnowTerrain Instance { get; private set; }

        [SerializeField] SculptFeelConfig config;
        [SerializeField] Material groundMaterial;

        public SculptFeelConfig Config => config;
        public void EditorAssign(SculptFeelConfig cfg, Material mat) { config = cfg; groundMaterial = mat; }

        public bool IsCreated => _height.IsCreated;
        public int Samples => _samples;
        public float CellSize => _cellSize;
        public float FieldSize => _cells * _cellSize;

        int _cells, _samples, _chunkCells, _chunksPerAxis;
        float _cellSize;
        NativeArray<float> _height, _scratch;
        NativeArray<bool> _dirty;
        readonly HashSet<int> _colliderDirty = new HashSet<int>();
        Mesh[] _meshes;
        MeshCollider[] _colliders;
        GameObject _chunkRoot;
        float _remeshTimer, _colliderTimer;

        static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        };

        // ------------------------------------------------------------------ lifecycle

        void OnEnable()
        {
            if (Application.isPlaying || Instance == null) Instance = this;
            if (config != null && !IsCreated) Initialise();
        }

        void OnDisable()
        {
            Teardown();
            if (Instance == this) Instance = null;
        }

        public void Initialise()
        {
            Teardown();
            _cellSize = Mathf.Max(0.01f, config.terrainCellSize);
            _cells = Mathf.Max(8, Mathf.RoundToInt(config.terrainFieldSize / _cellSize));
            _chunkCells = Mathf.Clamp(config.terrainChunkCells, 8, _cells);
            _cells = Mathf.CeilToInt(_cells / (float)_chunkCells) * _chunkCells; // whole chunks
            _chunksPerAxis = _cells / _chunkCells;
            _samples = _cells + 1;

            _height = new NativeArray<float>(_samples * _samples, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _scratch = new NativeArray<float>(_samples * _samples, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _dirty = new NativeArray<bool>(_chunksPerAxis * _chunksPerAxis, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            _chunkRoot = new GameObject("Chunks") { hideFlags = HideFlags.DontSave };
            _chunkRoot.transform.SetParent(transform, false);
            _chunkRoot.layer = gameObject.layer;
            int n = _chunksPerAxis * _chunksPerAxis;
            _meshes = new Mesh[n];
            _colliders = new MeshCollider[n];
            for (int i = 0; i < n; i++)
            {
                int2 c = ChunkCoord(i);
                var go = new GameObject($"Chunk_{c.x}_{c.y}") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(_chunkRoot.transform, false);
                go.layer = gameObject.layer;
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = groundMaterial;
                var mc = go.AddComponent<MeshCollider>();
                var mesh = new Mesh { name = go.name, hideFlags = HideFlags.DontSave };
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.MarkDynamic();
                mf.sharedMesh = mesh;
                _meshes[i] = mesh; _colliders[i] = mc;
                _dirty[i] = true;
            }
            Remesh();
            RebuildColliders();
        }

        void Teardown()
        {
            if (_height.IsCreated) _height.Dispose();
            if (_scratch.IsCreated) _scratch.Dispose();
            if (_dirty.IsCreated) _dirty.Dispose();
            if (_meshes != null) foreach (var m in _meshes) if (m != null) { if (Application.isPlaying) Destroy(m); else DestroyImmediate(m); }
            _meshes = null; _colliders = null;
            if (_chunkRoot != null) { if (Application.isPlaying) Destroy(_chunkRoot); else DestroyImmediate(_chunkRoot); }
            _chunkRoot = null;
            _colliderDirty.Clear();
        }

        void Update()
        {
            if (!Application.isPlaying || !IsCreated || config == null) return;
            // Paths are stamped from many places; the terrain owns its own remesh/collider cadence.
            _remeshTimer += Time.deltaTime;
            if (_remeshTimer >= 1f / Mathf.Max(1f, config.terrainRemeshHz)) { _remeshTimer = 0f; Remesh(); }
            _colliderTimer += Time.deltaTime;
            if (_colliderTimer >= 1f / Mathf.Max(0.5f, config.terrainColliderHz)) { _colliderTimer = 0f; RebuildColliders(); }
        }

        // ------------------------------------------------------------------ coordinates

        int2 ChunkCoord(int i) => new int2(i % _chunksPerAxis, i / _chunksPerAxis);
        int ChunkIndex(int2 c) => c.x + c.y * _chunksPerAxis;

        public float2 WorldToSample(float3 world)
        {
            float3 local = transform.InverseTransformPoint(world);
            return new float2(local.x, local.z) / _cellSize;
        }

        public float MetresToSamples(float metres) => metres / _cellSize;

        bool DiscAabb(float2 c, float r, out int2 min, out int2 max)
        {
            min = math.clamp((int2)math.floor(c - r), 0, _samples);
            max = math.clamp((int2)math.ceil(c + r) + 1, 0, _samples);
            return math.all(max > min);
        }

        void MarkDirty(int2 min, int2 max)
        {
            // a sample on a chunk border belongs to both chunks; expand by one to catch the neighbour
            int2 lo = math.clamp(min - 1, 0, _cells - 1) / _chunkCells;
            int2 hi = math.clamp(max, 0, _cells - 1) / _chunkCells;
            for (int z = lo.y; z <= hi.y; z++)
            for (int x = lo.x; x <= hi.x; x++)
                _dirty[ChunkIndex(new int2(x, z))] = true;
        }

        /// <summary>Bilinear height (world Y) under a world XZ position.</summary>
        public float SampleHeight(float3 world)
        {
            if (!IsCreated) return transform.position.y;
            float2 s = math.clamp(WorldToSample(world), 0f, _samples - 1.001f);
            int2 i0 = (int2)math.floor(s);
            int2 i1 = math.min(i0 + 1, _samples - 1);
            float2 f = s - i0;
            float h00 = _height[i0.x + i0.y * _samples], h10 = _height[i1.x + i0.y * _samples];
            float h01 = _height[i0.x + i1.y * _samples], h11 = _height[i1.x + i1.y * _samples];
            float h = math.lerp(math.lerp(h00, h10, f.x), math.lerp(h01, h11, f.x), f.y);
            return transform.position.y + h;
        }

        /// <summary>True if the snow under a point is still (nearly) untouched.</summary>
        public bool IsFreshAt(float3 world, float tolerance) => SampleHeight(world) - transform.position.y >= -tolerance;

        // ------------------------------------------------------------------ brush ops

        public void ApplyAdd(float3 worldCenter, float radiusMetres, float ratePerTick, float shoulder)
        {
            if (!IsCreated) return;
            float2 c = WorldToSample(worldCenter);
            float r = MetresToSamples(radiusMetres);
            if (!DiscAabb(c, r, out var min, out var max)) return;
            int2 ext = max - min;
            new HeightBrushJob
            {
                Height = _height, Samples = _samples, AabbMin = min, AabbExtent = ext,
                Center = c, RadiusSamples = r, Amount = ratePerTick, Shoulder = shoulder,
                MinH = -config.terrainMaxCarveDepth, MaxH = config.terrainMaxRaise,
            }.Schedule(ext.x * ext.y, 128).Complete();
            MarkDirty(min, max);
        }

        public void ApplySmooth(float3 worldCenter, float radiusMetres, float strength, float shoulder)
        {
            if (!IsCreated) return;
            float2 c = WorldToSample(worldCenter);
            float r = MetresToSamples(radiusMetres);
            if (!DiscAabb(c, r + 1f, out var min, out var max)) return;
            int2 ext = max - min;
            int count = ext.x * ext.y;
            var copy = new HeightCopyJob { Src = _height, Dst = _scratch, Samples = _samples, AabbMin = min, AabbExtent = ext }.Schedule(count, 256);
            new HeightSmoothJob
            {
                Source = _scratch, Height = _height, Samples = _samples, AabbMin = min, AabbExtent = ext,
                Center = c, RadiusSamples = r, Strength = strength, Shoulder = shoulder,
            }.Schedule(count, 128, copy).Complete();
            MarkDirty(min, max);
        }

        /// <summary>
        /// Height data as a compact blob: [version][samples][runs of (millimetres:int16, count:uint16)].
        /// Untouched snow is a single run, so a lightly used field costs almost nothing.
        /// </summary>
        public byte[] SaveHeights()
        {
            if (!IsCreated) return null;
            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms);
            w.Write((byte)1);
            w.Write(_samples);
            int i = 0, n = _height.Length;
            while (i < n)
            {
                short v = (short)Mathf.Clamp(Mathf.RoundToInt(_height[i] * 1000f), short.MinValue, short.MaxValue);
                int run = 1;
                while (i + run < n && run < ushort.MaxValue
                       && (short)Mathf.Clamp(Mathf.RoundToInt(_height[i + run] * 1000f), short.MinValue, short.MaxValue) == v)
                    run++;
                w.Write(v);
                w.Write((ushort)run);
                i += run;
            }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>Restore a blob from <see cref="SaveHeights"/>. False if it does not match this field's resolution.</summary>
        public bool LoadHeights(byte[] data)
        {
            if (!IsCreated || data == null || data.Length < 5) return false;
            using var ms = new System.IO.MemoryStream(data);
            using var r = new System.IO.BinaryReader(ms);
            if (r.ReadByte() != 1) return false;
            if (r.ReadInt32() != _samples) return false;

            int write = 0, n = _height.Length;
            while (ms.Position < ms.Length && write < n)
            {
                float v = r.ReadInt16() / 1000f;
                int run = r.ReadUInt16();
                for (int k = 0; k < run && write < n; k++) _height[write++] = v;
            }
            if (write != n) return false;

            for (int i = 0; i < _dirty.Length; i++) { _dirty[i] = true; _colliderDirty.Add(i); }
            Remesh();
            RebuildColliders();
            return true;
        }

        /// <summary>Wipe the field back to untouched snow (arriving at a different field).</summary>
        public void ResetHeights()
        {
            if (!IsCreated) return;
            for (int i = 0; i < _height.Length; i++) _height[i] = 0f;
            for (int i = 0; i < _dirty.Length; i++) { _dirty[i] = true; _colliderDirty.Add(i); }
            Remesh();
            RebuildColliders();
        }

        /// <summary>Snowfall: raise every below-fresh sample by <paramref name="metres"/> toward the untouched surface.</summary>
        public void RecoverTowardFresh(float metres)
        {
            if (!IsCreated || metres <= 0f) return;
            int chunkCount = _chunksPerAxis * _chunksPerAxis;
            var changed = new NativeArray<bool>(chunkCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            new HeightRecoverJob
            {
                Height = _height, ChunkChanged = changed, Samples = _samples,
                ChunkCells = _chunkCells, ChunksPerAxis = _chunksPerAxis, Amount = metres,
            }.Schedule(_height.Length, 4096).Complete();
            for (int i = 0; i < chunkCount; i++)
                if (changed[i]) { _dirty[i] = true; _colliderDirty.Add(i); }
            changed.Dispose();
        }

        /// <summary>Press a footprint/trench. Never raises; cumulative packing capped by terrainPathDepthCap.</summary>
        public void StampDepression(float3 worldCenter, float radiusMetres, float depthMetres, float shoulder)
        {
            if (!IsCreated) return;
            float2 c = WorldToSample(worldCenter);
            float r = MetresToSamples(radiusMetres);
            if (!DiscAabb(c, r, out var min, out var max)) return;
            int2 ext = max - min;
            new HeightStampJob
            {
                Height = _height, Samples = _samples, AabbMin = min, AabbExtent = ext,
                Center = c, RadiusSamples = r, Depth = depthMetres, Shoulder = shoulder,
                PathDepthCap = config.terrainPathDepthCap,
            }.Schedule(ext.x * ext.y, 128).Complete();
            MarkDirty(min, max);
        }

        // ------------------------------------------------------------------ meshing

        public void Remesh()
        {
            if (!IsCreated) return;
            var dirty = new List<int>();
            for (int i = 0; i < _dirty.Length; i++) if (_dirty[i]) dirty.Add(i);
            if (dirty.Count == 0) return;

            int vertsPerChunk = (_chunkCells + 1) * (_chunkCells + 1);
            var verts = new NativeList<TerrainVertex>[dirty.Count];
            var inds = new NativeList<int>[dirty.Count];
            var handles = new NativeArray<JobHandle>(dirty.Count, Allocator.Temp);
            for (int k = 0; k < dirty.Count; k++)
            {
                verts[k] = new NativeList<TerrainVertex>(vertsPerChunk, Allocator.TempJob);
                inds[k] = new NativeList<int>(_chunkCells * _chunkCells * 6, Allocator.TempJob);
                handles[k] = new TerrainMeshJob
                {
                    Height = _height, Samples = _samples, ChunkCells = _chunkCells, ChunkCoord = ChunkCoord(dirty[k]),
                    CellSize = _cellSize, FieldSize = FieldSize, Vertices = verts[k], Indices = inds[k],
                }.Schedule();
            }
            JobHandle.CompleteAll(handles);
            handles.Dispose();

            for (int k = 0; k < dirty.Count; k++)
            {
                int ci = dirty[k];
                var mesh = _meshes[ci];
                int vc = verts[k].Length, ic = inds[k].Length;
                mesh.SetVertexBufferParams(vc, Layout);
                mesh.SetVertexBufferData(verts[k].AsArray(), 0, 0, vc, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                mesh.SetIndexBufferParams(ic, IndexFormat.UInt32);
                mesh.SetIndexBufferData(inds[k].AsArray(), 0, 0, ic, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, ic), MeshUpdateFlags.DontRecalculateBounds);
                mesh.RecalculateBounds();
                verts[k].Dispose();
                inds[k].Dispose();
                _dirty[ci] = false;
                _colliderDirty.Add(ci);
            }
        }

        public void RebuildColliders()
        {
            if (_colliders == null) return;
            foreach (int ci in _colliderDirty)
            {
                var mc = _colliders[ci];
                mc.sharedMesh = null;
                mc.sharedMesh = _meshes[ci];
            }
            _colliderDirty.Clear();
        }

        void OnDrawGizmosSelected()
        {
            if (config == null) return;
            float size = config.terrainFieldSize;
            Gizmos.color = new Color(0.5f, 0.8f, 1f, 0.5f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(new Vector3(size * 0.5f, 0f, size * 0.5f), new Vector3(size, 0.01f, size));
        }
    }
}
