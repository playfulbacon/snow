using Snowfield.Voxel;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Field
{
    /// <summary>Heightmap helpers shared by the jobs. Sample index = x + z * samples.</summary>
    [BurstCompile]
    public static class HeightMath
    {
        public static int2 AabbCoord(int i, int2 min, int2 extent) => min + new int2(i % extent.x, i / extent.x);
        public static int Index(int2 p, int samples) => p.x + p.y * samples;
    }

    /// <summary>Raise (or lower, negative amount) heights inside a disc, clamped to [MinH, MaxH].</summary>
    [BurstCompile]
    public struct HeightBrushJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float> Height;
        public int Samples;
        public int2 AabbMin, AabbExtent;
        public float2 Center;          // in sample space
        public float RadiusSamples;
        public float Amount;           // metres per tick at the core
        public float Shoulder;
        public float MinH, MaxH;

        public void Execute(int i)
        {
            int2 p = HeightMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float2)p, Center) / RadiusSamples;
            float f = BrushMath.Falloff(d, Shoulder);
            if (f <= 0f) return;
            int idx = HeightMath.Index(p, Samples);
            Height[idx] = math.clamp(Height[idx] + Amount * f, MinH, MaxH);
        }
    }

    /// <summary>Blur toward the 4-neighbourhood mean. Reads a snapshot so it is order-independent.</summary>
    [BurstCompile]
    public struct HeightSmoothJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Source;
        [NativeDisableParallelForRestriction] public NativeArray<float> Height;
        public int Samples;
        public int2 AabbMin, AabbExtent;
        public float2 Center;
        public float RadiusSamples;
        public float Strength;
        public float Shoulder;

        public void Execute(int i)
        {
            int2 p = HeightMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float2)p, Center) / RadiusSamples;
            float f = BrushMath.Falloff(d, Shoulder) * Strength;
            if (f <= 0f) return;
            int idx = HeightMath.Index(p, Samples);
            float sum = Source[idx] * 2f, w = 2f;
            Acc(p + new int2(1, 0), ref sum, ref w);
            Acc(p - new int2(1, 0), ref sum, ref w);
            Acc(p + new int2(0, 1), ref sum, ref w);
            Acc(p - new int2(0, 1), ref sum, ref w);
            Height[idx] = math.lerp(Source[idx], sum / w, f);
        }

        void Acc(int2 q, ref float sum, ref float w)
        {
            if (q.x < 0 || q.y < 0 || q.x >= Samples || q.y >= Samples) return;
            sum += Source[HeightMath.Index(q, Samples)];
            w += 1f;
        }
    }

    /// <summary>
    /// Press a depression (footprint, snowball trench). Only ever lowers; cumulative packing is capped at
    /// -PathDepthCap unless the spot was already deeper (carved), which is left alone.
    /// </summary>
    [BurstCompile]
    public struct HeightStampJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float> Height;
        public int Samples;
        public int2 AabbMin, AabbExtent;
        public float2 Center;
        public float RadiusSamples;
        public float Depth;
        public float Shoulder;
        public float PathDepthCap;

        public void Execute(int i)
        {
            int2 p = HeightMath.AabbCoord(i, AabbMin, AabbExtent);
            float d = math.distance((float2)p, Center) / RadiusSamples;
            float f = BrushMath.Falloff(d, Shoulder);
            if (f <= 0f) return;
            int idx = HeightMath.Index(p, Samples);
            float h = Height[idx];
            float target = h - Depth * f;
            float floor = math.min(h, -PathDepthCap); // cannot pack below the cap, but never raises carved ground
            Height[idx] = math.max(target, floor);
        }
    }

    /// <summary>Snowfall: lift heights below the fresh surface (0) up by Amount, clamped to 0. Flags changed chunks.</summary>
    [BurstCompile]
    public struct HeightRecoverJob : IJobParallelFor
    {
        public NativeArray<float> Height;
        [NativeDisableParallelForRestriction] public NativeArray<bool> ChunkChanged;
        public int Samples;
        public int ChunkCells;
        public int ChunksPerAxis;
        public float Amount;

        public void Execute(int i)
        {
            float h = Height[i];
            if (h >= 0f) return;
            Height[i] = math.min(0f, h + Amount);
            int x = i % Samples, z = i / Samples;
            int cx = math.min(x / ChunkCells, ChunksPerAxis - 1);
            int cz = math.min(z / ChunkCells, ChunksPerAxis - 1);
            ChunkChanged[cx + cz * ChunksPerAxis] = true;
        }
    }

    /// <summary>Copy a sample-space AABB from Src to Dst (snapshot for the smooth job).</summary>
    [BurstCompile]
    public struct HeightCopyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Src;
        [NativeDisableParallelForRestriction] public NativeArray<float> Dst;
        public int Samples;
        public int2 AabbMin, AabbExtent;

        public void Execute(int i)
        {
            int idx = HeightMath.Index(HeightMath.AabbCoord(i, AabbMin, AabbExtent), Samples);
            Dst[idx] = Src[idx];
        }
    }

    public struct TerrainVertex
    {
        public float3 Position; // terrain-local metres
        public float3 Normal;
        public float2 Uv;
    }

    /// <summary>
    /// One chunk of the heightmap as a regular grid mesh. Normals from the central-difference height gradient
    /// (sampled from the full array, so chunk seams match).
    /// </summary>
    [BurstCompile]
    public struct TerrainMeshJob : IJob
    {
        [ReadOnly] public NativeArray<float> Height;
        public int Samples;
        public int ChunkCells;
        public int2 ChunkCoord;
        public float CellSize;
        public float FieldSize;

        public NativeList<TerrainVertex> Vertices;
        public NativeList<int> Indices;

        public void Execute()
        {
            Vertices.Clear();
            Indices.Clear();
            int2 s0 = ChunkCoord * ChunkCells;
            int n = ChunkCells + 1;

            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int2 p = s0 + new int2(x, z);
                float h = Height[HeightMath.Index(p, Samples)];
                float hx0 = Height[HeightMath.Index(new int2(math.max(p.x - 1, 0), p.y), Samples)];
                float hx1 = Height[HeightMath.Index(new int2(math.min(p.x + 1, Samples - 1), p.y), Samples)];
                float hz0 = Height[HeightMath.Index(new int2(p.x, math.max(p.y - 1, 0)), Samples)];
                float hz1 = Height[HeightMath.Index(new int2(p.x, math.min(p.y + 1, Samples - 1)), Samples)];
                float3 normal = math.normalize(new float3(hx0 - hx1, 2f * CellSize, hz0 - hz1));
                Vertices.Add(new TerrainVertex
                {
                    Position = new float3(p.x * CellSize, h, p.y * CellSize),
                    Normal = normal,
                    Uv = new float2(p.x * CellSize, p.y * CellSize) / FieldSize,
                });
            }

            for (int z = 0; z < ChunkCells; z++)
            for (int x = 0; x < ChunkCells; x++)
            {
                int a = x + z * n, b = a + 1, c = a + n, d = c + 1;
                // clockwise when viewed from above (Unity front face)
                Indices.Add(a); Indices.Add(c); Indices.Add(b);
                Indices.Add(b); Indices.Add(c); Indices.Add(d);
            }
        }
    }
}
