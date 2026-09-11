using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>A twig armature segment in voxel space. Snow within the support radius of it never breaks off.</summary>
    public struct SupportSegment
    {
        public float3 A, B;

        public float DistanceSq(float3 p)
        {
            float3 ab = B - A;
            float len = math.lengthsq(ab);
            float t = len > 1e-8f ? math.saturate(math.dot(p - A, ab) / len) : 0f;
            return math.distancesq(p, A + ab * t);
        }
    }

    /// <summary>Chamfer 3-4-5 helpers shared by the structural passes (distances in thirds of a voxel).</summary>
    static class Chamfer
    {
        public const int Inf = 1 << 28;

        static int Weight(int dx, int dy, int dz)
        {
            int n = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);
            return n == 1 ? 3 : n == 2 ? 4 : 5;
        }

        /// <summary>Two-pass chamfer distance transform over a region-local array (0 at sources, Inf elsewhere on entry).</summary>
        public static void Transform(NativeArray<int> dist, int3 ext)
        {
            int sx = 1, sy = ext.x, sz = ext.x * ext.y;
            // forward: neighbours already visited in (z, y, x) ascending order
            for (int z = 0; z < ext.z; z++)
            for (int y = 0; y < ext.y; y++)
            for (int x = 0; x < ext.x; x++)
            {
                int i = x * sx + y * sy + z * sz;
                int best = dist[i];
                if (best == 0) continue;
                for (int dz = -1; dz <= 0; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    bool visited = dz < 0 || (dz == 0 && (dy < 0 || (dy == 0 && dx < 0)));
                    if (!visited) continue;
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= ext.x || ny >= ext.y || nz >= ext.z) continue;
                    int d = dist[nx * sx + ny * sy + nz * sz];
                    if (d >= Inf) continue;
                    d += Weight(dx, dy, dz);
                    if (d < best) best = d;
                }
                dist[i] = best;
            }
            // backward
            for (int z = ext.z - 1; z >= 0; z--)
            for (int y = ext.y - 1; y >= 0; y--)
            for (int x = ext.x - 1; x >= 0; x--)
            {
                int i = x * sx + y * sy + z * sz;
                int best = dist[i];
                if (best == 0) continue;
                for (int dz = 0; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    bool visited = dz > 0 || (dz == 0 && (dy > 0 || (dy == 0 && dx > 0)));
                    if (!visited) continue;
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= ext.x || ny >= ext.y || nz >= ext.z) continue;
                    int d = dist[nx * sx + ny * sy + nz * sz];
                    if (d >= Inf) continue;
                    d += Weight(dx, dy, dz);
                    if (d < best) best = d;
                }
                dist[i] = best;
            }
        }
    }

    /// <summary>
    /// The thinness check: a morphological opening with a per-voxel radius that shrinks as compaction rises.
    /// Distances are quantized to voxel steps, so the limit is an integer k(c) = ceil(r(c)) steps: a feature
    /// survives only if it has an "interior" voxel — one at least k + 1 steps from any air — and every solid
    /// voxel within k steps of an interior voxel is kept. Slabs up to 2k voxels thick therefore break, 2k + 1
    /// and thicker hold. Corners of thick bodies are safe because one full step (any direction) from an interior
    /// voxel always keeps you. Snow within the support radius of a twig segment counts as interior regardless
    /// of thickness: twigs are structure.
    /// Runs over one region (the edited AABB plus margin); the rest of the grid is left alone.
    /// </summary>
    [BurstCompile]
    public struct ThinnessJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        [ReadOnly] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public int3 RegionMin, RegionExtent;
        /// <summary>Half the thickness limit, in voxels, at compaction 0 and 255.</summary>
        public float ThinRadiusFluffy, ThinRadiusPacked;
        /// <summary>Twig segments; only the first <see cref="SegmentCount"/> entries are read (a zero-length NativeArray is not allocated).</summary>
        [ReadOnly] public NativeArray<SupportSegment> Segments;
        public int SegmentCount;
        public float SupportRadiusVoxels;
        /// <summary>Region-sized output: 1 = remove this voxel.</summary>
        public NativeArray<byte> Mask;
        /// <summary>[0] = removed mass (full-voxel units), [1] = removed voxel count.</summary>
        public NativeArray<float> Result;

        int Local(int3 p) { int3 l = p - RegionMin; return l.x + l.y * RegionExtent.x + l.z * RegionExtent.x * RegionExtent.y; }
        int3 Global(int i) => BrushMath.AabbCoord(i, RegionMin, RegionExtent);

        /// <summary>The thin limit in whole voxel steps for this voxel's packing (never below 1).</summary>
        int Steps(int gi) => math.max(1, (int)math.ceil(math.lerp(ThinRadiusFluffy, ThinRadiusPacked, Compaction[gi] / 255f) - 1e-4f));

        public void Execute()
        {
            int3 ext = RegionExtent;
            int n = ext.x * ext.y * ext.z;
            var solid = new NativeArray<byte>(n, Allocator.Temp);
            var dist = new NativeArray<int>(n, Allocator.Temp);
            var distInt = new NativeArray<int>(n, Allocator.Temp);
            float supportSq = SupportRadiusVoxels * SupportRadiusVoxels;

            for (int i = 0; i < n; i++)
            {
                int3 p = Global(i);
                byte d = Density[Info.Index(p)];
                bool s = d >= VoxelGridInfo.Iso;
                solid[i] = (byte)(s ? 1 : 0);
                dist[i] = s ? Chamfer.Inf : 0;
                Mask[i] = 0;
            }
            Chamfer.Transform(dist, ext);

            // Interior: deep enough for the local limit, or held by a twig.
            for (int i = 0; i < n; i++)
            {
                bool interior = false;
                if (solid[i] != 0)
                {
                    int3 p = Global(i);
                    int gi = Info.Index(p);
                    interior = dist[i] >= (Steps(gi) + 1) * 3;
                    if (!interior && SegmentCount > 0)
                    {
                        float3 fp = p;
                        for (int k = 0; k < SegmentCount && !interior; k++)
                            interior = Segments[k].DistanceSq(fp) <= supportSq;
                    }
                }
                distInt[i] = interior ? 0 : Chamfer.Inf;
            }
            Chamfer.Transform(distInt, ext);

            float mass = 0f;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (solid[i] == 0) continue;
                int gi = Info.Index(Global(i));
                float reach = Steps(gi) * 3 + 2.5f; // k axis steps, with room for one of them to be a diagonal
                if (distInt[i] > reach)
                {
                    Mask[i] = 1;
                    mass += Density[gi];
                    count++;
                }
            }

            // Shoulder voxels (snow below iso) that hung off a removed feature go with it, so nothing invisible lingers.
            if (count > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (solid[i] != 0 || Mask[i] != 0) continue;
                    int3 p = Global(i);
                    int gi = Info.Index(p);
                    if (Density[gi] == 0) continue;
                    bool nearRemoved = false, nearKept = false;
                    for (int a = 0; a < 6; a++)
                    {
                        int3 q = p + Axis(a);
                        if (math.any(q < RegionMin) || math.any(q >= RegionMin + ext))
                        {
                            if (Info.InBounds(q) && Density[Info.Index(q)] >= VoxelGridInfo.Iso) nearKept = true;
                            continue;
                        }
                        int li = Local(q);
                        if (solid[li] == 0) continue;
                        if (Mask[li] != 0) nearRemoved = true; else nearKept = true;
                    }
                    if (nearRemoved && !nearKept) { Mask[i] = 2; mass += Density[gi]; count++; }
                }
            }

            Result[0] = mass / 255f;
            Result[1] = count;
            solid.Dispose(); dist.Dispose(); distInt.Dispose();
        }

        static int3 Axis(int a) => a switch
        {
            0 => new int3(1, 0, 0), 1 => new int3(-1, 0, 0), 2 => new int3(0, 1, 0),
            3 => new int3(0, -1, 0), 4 => new int3(0, 0, 1), _ => new int3(0, 0, -1),
        };
    }

    /// <summary>Zero every voxel a region mask flags (any non-zero mask value).</summary>
    [BurstCompile]
    public struct ClearMaskedJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public int3 RegionMin, RegionExtent;
        [ReadOnly] public NativeArray<byte> Mask;

        public void Execute(int i)
        {
            if (Mask[i] == 0) return;
            int gi = Info.Index(BrushMath.AabbCoord(i, RegionMin, RegionExtent));
            Density[gi] = 0;
            Compaction[gi] = 0;
        }
    }

    public struct IslandInfo
    {
        public int Label;
        public int3 Min, Max;   // inclusive voxel bounds
        public float Mass;      // full-voxel units
    }

    /// <summary>
    /// Connectivity: flood-fill (6-connected) from the ground contact through everything at or above
    /// <see cref="Threshold"/>; twig-supported voxels bridge gaps. Every solid voxel not reached is part of an
    /// island, labelled 2, 3, ... (1 = grounded, 0 = air). The sub-threshold shoulder around an island joins it
    /// so the island lifts out clean. <see cref="Islands"/> lists one entry per label in label order.
    /// </summary>
    [BurstCompile]
    public struct ConnectivityJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        public byte Threshold;
        /// <summary>Per XZ column (x + z * size): the ground height in voxel units, or NaN. Any other length = use the snow's own floor.</summary>
        [ReadOnly] public NativeArray<float> GroundY;
        /// <summary>Solid voxels this far (voxels) above the ground line still count as touching it.</summary>
        public float SeedDepthVoxels;
        [ReadOnly] public NativeArray<SupportSegment> Segments;
        public int SegmentCount;
        public float SupportRadiusVoxels;
        public NativeArray<int> Label;
        public NativeList<IslandInfo> Islands;

        public void Execute()
        {
            int size = Info.size;
            int n = Info.VoxelCount;
            Islands.Clear();
            for (int i = 0; i < n; i++) Label[i] = 0;

            // Twig support as a mask (only the voxels near a segment, so this stays cheap on big grids).
            var support = new NativeArray<byte>(SegmentCount > 0 ? n : 1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            float supportSq = SupportRadiusVoxels * SupportRadiusVoxels;
            for (int k = 0; k < SegmentCount; k++)
            {
                var seg = Segments[k];
                int3 lo = math.clamp((int3)math.floor(math.min(seg.A, seg.B) - SupportRadiusVoxels), 0, size - 1);
                int3 hi = math.clamp((int3)math.ceil(math.max(seg.A, seg.B) + SupportRadiusVoxels), 0, size - 1);
                for (int z = lo.z; z <= hi.z; z++)
                for (int y = lo.y; y <= hi.y; y++)
                for (int x = lo.x; x <= hi.x; x++)
                {
                    int3 p = new int3(x, y, z);
                    if (seg.DistanceSq(p) <= supportSq) support[Info.Index(p)] = 1;
                }
            }
            bool hasSupport = SegmentCount > 0;

            // Ground line: sampled heights if given, else the lowest solid voxel.
            bool useGround = GroundY.Length == size * size;
            int floorY = int.MaxValue;
            if (!useGround)
            {
                for (int i = 0; i < n && floorY == int.MaxValue; i++)
                    if (Density[i] >= Threshold) floorY = (i / size) % size;
                if (floorY == int.MaxValue) { support.Dispose(); return; } // nothing solid at all
            }

            var stack = new NativeArray<int>(n, Allocator.Temp);
            int top = 0;

            // Seeds.
            for (int i = 0; i < n; i++)
            {
                if (Density[i] < Threshold) continue;
                int x = i % size, y = (i / size) % size, z = i / (size * size);
                float line = useGround ? GroundY[x + z * size] : floorY;
                if (float.IsNaN(line)) line = floorY == int.MaxValue ? 0f : floorY;
                if (y <= line + SeedDepthVoxels) { Label[i] = 1; stack[top++] = i; }
            }
            Flood(ref stack, ref top, 1, support, hasSupport);

            // Islands.
            int next = 2;
            for (int i = 0; i < n; i++)
            {
                if (Label[i] != 0 || Density[i] < Threshold) continue;
                Label[i] = next;
                stack[top++] = i;
                Flood(ref stack, ref top, next, support, hasSupport);
                next++;
            }
            int islandCount = next - 2;
            if (islandCount == 0) { stack.Dispose(); support.Dispose(); return; }

            // Shoulder adoption: faint snow next to an island (and not next to grounded snow) leaves with it.
            for (int i = 0; i < n; i++)
            {
                if (Label[i] != 0 || Density[i] == 0) continue;
                int3 p = new int3(i % size, (i / size) % size, i / (size * size));
                int adopt = 0; bool nearGround = false;
                for (int a = 0; a < 6; a++)
                {
                    int3 q = p + Axis(a);
                    if (!Info.InBounds(q)) continue;
                    int l = Label[Info.Index(q)];
                    if (l == 1) nearGround = true;
                    else if (l >= 2 && adopt == 0) adopt = l;
                }
                if (adopt != 0 && !nearGround) Label[i] = adopt;
            }

            for (int k = 0; k < islandCount; k++)
                Islands.Add(new IslandInfo { Label = k + 2, Min = new int3(int.MaxValue), Max = new int3(int.MinValue), Mass = 0f });
            for (int i = 0; i < n; i++)
            {
                int l = Label[i];
                if (l < 2 || Density[i] == 0) continue;
                var info = Islands[l - 2];
                int3 p = new int3(i % size, (i / size) % size, i / (size * size));
                info.Min = math.min(info.Min, p);
                info.Max = math.max(info.Max, p);
                info.Mass += Density[i] / 255f;
                Islands[l - 2] = info;
            }
            stack.Dispose();
            support.Dispose();
        }

        void Flood(ref NativeArray<int> stack, ref int top, int label, NativeArray<byte> support, bool hasSupport)
        {
            int size = Info.size;
            while (top > 0)
            {
                int i = stack[--top];
                int3 p = new int3(i % size, (i / size) % size, i / (size * size));
                for (int a = 0; a < 6; a++)
                {
                    int3 q = p + Axis(a);
                    if (!Info.InBounds(q)) continue;
                    int qi = Info.Index(q);
                    if (Label[qi] != 0) continue;
                    bool passable = Density[qi] >= Threshold || (hasSupport && support[qi] != 0);
                    if (!passable) continue;
                    Label[qi] = label;
                    stack[top++] = qi;
                }
            }
        }

        static int3 Axis(int a) => a switch
        {
            0 => new int3(1, 0, 0), 1 => new int3(-1, 0, 0), 2 => new int3(0, 1, 0),
            3 => new int3(0, -1, 0), 4 => new int3(0, 0, 1), _ => new int3(0, 0, -1),
        };
    }

    /// <summary>
    /// Lift one labelled island out of a source grid into a voxel-aligned destination grid (same voxel size,
    /// no rotation): destination voxel p ↔ source voxel p + <see cref="Offset"/>. Exact copy, so conservation holds.
    /// </summary>
    [BurstCompile]
    public struct ExtractIslandJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        [ReadOnly] public NativeArray<byte> Source;
        [ReadOnly] public NativeArray<byte> SourceCompaction;
        [ReadOnly] public NativeArray<int> SourceLabel;
        public VoxelGridInfo SourceInfo;
        public int3 Offset;
        public int Label;

        public void Execute(int i)
        {
            int size = Info.size;
            int3 p = new int3(i % size, (i / size) % size, i / (size * size));
            int3 s = p + Offset;
            if (!SourceInfo.InBounds(s)) { Density[i] = 0; Compaction[i] = 0; return; }
            int si = SourceInfo.Index(s);
            bool take = SourceLabel[si] == Label;
            Density[i] = take ? Source[si] : (byte)0;
            Compaction[i] = take ? SourceCompaction[si] : (byte)0;
        }
    }

    /// <summary>Zero every voxel carrying a given label (the source side of an island extraction).</summary>
    [BurstCompile]
    public struct ClearLabelJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        [ReadOnly] public NativeArray<int> Label;
        public int Target;

        public void Execute(int i)
        {
            if (Label[i] != Target) return;
            Density[i] = 0;
            Compaction[i] = 0;
        }
    }

    /// <summary>
    /// Remote-replay twin of the extraction: given an island grid that already holds the lifted snow, zero the
    /// aligned voxels in the source wherever the island has any density.
    /// </summary>
    [BurstCompile]
    public struct ClearFromIslandJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Island;
        public VoxelGridInfo IslandInfo;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Compaction;
        public VoxelGridInfo Info;
        public int3 Offset;

        public void Execute(int i)
        {
            if (Island[i] == 0) return;
            int size = IslandInfo.size;
            int3 p = new int3(i % size, (i / size) % size, i / (size * size)) + Offset;
            if (!Info.InBounds(p)) return;
            int gi = Info.Index(p);
            Density[gi] = 0;
            Compaction[gi] = 0;
        }
    }
}
