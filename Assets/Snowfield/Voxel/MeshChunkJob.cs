using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>Vertex layout uploaded to the Mesh: position + gradient normal, both in sculpture-local metres.</summary>
    public struct SnowVertex
    {
        public float3 Position;
        public float3 Normal;
    }

    /// <summary>
    /// Blittable copy of the MC tables so jobs can read them. Allocate once (Persistent), share across jobs.
    /// </summary>
    public struct MarchingCubesLookup : System.IDisposable
    {
        [ReadOnly] public NativeArray<int> EdgeTable;
        [ReadOnly] public NativeArray<int> TriTable;
        [ReadOnly] public NativeArray<int3> CornerOffset;
        [ReadOnly] public NativeArray<int2> EdgeCorners;

        public static MarchingCubesLookup Create(Allocator allocator)
        {
            return new MarchingCubesLookup
            {
                EdgeTable = new NativeArray<int>(MarchingCubesTables.EdgeTable, allocator),
                TriTable = new NativeArray<int>(MarchingCubesTables.TriTable, allocator),
                CornerOffset = new NativeArray<int3>(MarchingCubesTables.CornerOffset, allocator),
                EdgeCorners = new NativeArray<int2>(MarchingCubesTables.EdgeCorners, allocator),
            };
        }

        public bool IsCreated => EdgeTable.IsCreated;

        public void Dispose()
        {
            if (EdgeTable.IsCreated) EdgeTable.Dispose();
            if (TriTable.IsCreated) TriTable.Dispose();
            if (CornerOffset.IsCreated) CornerOffset.Dispose();
            if (EdgeCorners.IsCreated) EdgeCorners.Dispose();
        }
    }

    /// <summary>
    /// Marching cubes over one 16^3 chunk. Samples density from the full grid (so the +1 apron comes for free);
    /// cells on the grid's far boundary are skipped. Normals = negated central-difference density gradient.
    /// No vertex welding in v1: snow hides it, and MC output is cheap. Revisit if index counts matter.
    /// </summary>
    [BurstCompile]
    public struct MeshChunkJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        public int3 ChunkCoord;
        public MarchingCubesLookup Lookup;

        public NativeList<SnowVertex> Vertices;
        public NativeList<int> Indices;

        public void Execute()
        {
            Vertices.Clear();
            Indices.Clear();

            int3 origin = ChunkCoord * VoxelGridInfo.ChunkSize;
            int3 end = math.min(origin + VoxelGridInfo.ChunkSize, Info.size - 1); // last cell needs corner +1

            // Per-cell scratch
            var cornerVal = new NativeArray<float>(8, Allocator.Temp);
            var edgeVert = new NativeArray<float3>(12, Allocator.Temp);
            float iso = VoxelGridInfo.Iso;

            for (int z = origin.z; z < end.z; z++)
            for (int y = origin.y; y < end.y; y++)
            for (int x = origin.x; x < end.x; x++)
            {
                int3 cell = new int3(x, y, z);
                int cubeIndex = 0;
                for (int c = 0; c < 8; c++)
                {
                    float v = Density[Info.Index(cell + Lookup.CornerOffset[c])];
                    cornerVal[c] = v;
                    if (v >= iso) cubeIndex |= 1 << c;
                }

                int edges = Lookup.EdgeTable[cubeIndex];
                if (edges == 0) continue;

                for (int e = 0; e < 12; e++)
                {
                    if ((edges & (1 << e)) == 0) continue;
                    int2 ec = Lookup.EdgeCorners[e];
                    float3 p0 = (float3)(cell + Lookup.CornerOffset[ec.x]);
                    float3 p1 = (float3)(cell + Lookup.CornerOffset[ec.y]);
                    float v0 = cornerVal[ec.x];
                    float v1 = cornerVal[ec.y];
                    float t = math.abs(v1 - v0) < 1e-5f ? 0.5f : (iso - v0) / (v1 - v0);
                    edgeVert[e] = math.lerp(p0, p1, math.saturate(t));
                }

                int row = cubeIndex * 16;
                for (int k = 0; k < 16; k += 3)
                {
                    int e0 = Lookup.TriTable[row + k];
                    if (e0 < 0) break;
                    int e1 = Lookup.TriTable[row + k + 1];
                    int e2 = Lookup.TriTable[row + k + 2];

                    int baseIndex = Vertices.Length;
                    Emit(edgeVert[e0]);
                    Emit(edgeVert[e1]);
                    Emit(edgeVert[e2]);
                    // Bourke tables wind counter-clockwise when viewed from outside for the
                    // "inside = above iso" convention with a Y-up right-handed frame; Unity is left-handed
                    // so flip to keep front faces outward.
                    Indices.Add(baseIndex);
                    Indices.Add(baseIndex + 2);
                    Indices.Add(baseIndex + 1);
                }
            }

            cornerVal.Dispose();
            edgeVert.Dispose();
        }

        void Emit(float3 voxelPos)
        {
            Vertices.Add(new SnowVertex
            {
                Position = voxelPos * Info.voxelSize,
                Normal = GradientNormal(voxelPos),
            });
        }

        /// <summary>Trilinear density sample at a fractional voxel position; clamps to grid.</summary>
        float Sample(float3 p)
        {
            p = math.clamp(p, 0f, Info.size - 1.001f);
            int3 i0 = (int3)math.floor(p);
            int3 i1 = math.min(i0 + 1, Info.size - 1);
            float3 f = p - i0;

            float c000 = Density[Info.Index(i0.x, i0.y, i0.z)];
            float c100 = Density[Info.Index(i1.x, i0.y, i0.z)];
            float c010 = Density[Info.Index(i0.x, i1.y, i0.z)];
            float c110 = Density[Info.Index(i1.x, i1.y, i0.z)];
            float c001 = Density[Info.Index(i0.x, i0.y, i1.z)];
            float c101 = Density[Info.Index(i1.x, i0.y, i1.z)];
            float c011 = Density[Info.Index(i0.x, i1.y, i1.z)];
            float c111 = Density[Info.Index(i1.x, i1.y, i1.z)];

            float c00 = math.lerp(c000, c100, f.x);
            float c10 = math.lerp(c010, c110, f.x);
            float c01 = math.lerp(c001, c101, f.x);
            float c11 = math.lerp(c011, c111, f.x);
            float c0 = math.lerp(c00, c10, f.y);
            float c1 = math.lerp(c01, c11, f.y);
            return math.lerp(c0, c1, f.z);
        }

        float3 GradientNormal(float3 p)
        {
            const float h = 1f; // one voxel; wider than the MC cell smooths the shading further
            float3 g = new float3(
                Sample(p + new float3(h, 0, 0)) - Sample(p - new float3(h, 0, 0)),
                Sample(p + new float3(0, h, 0)) - Sample(p - new float3(0, h, 0)),
                Sample(p + new float3(0, 0, h)) - Sample(p - new float3(0, 0, h)));
            float len = math.length(g);
            return len > 1e-6f ? -g / len : new float3(0, 1, 0); // density increases inward, so normal = -gradient
        }
    }
}
