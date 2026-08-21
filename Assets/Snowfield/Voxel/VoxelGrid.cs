using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// Owns the density array for one sculpture plus per-chunk dirty flags.
    /// No UnityEngine dependency so it is unit-testable; lifetime managed by the owner.
    /// </summary>
    public sealed class VoxelGrid : IDisposable
    {
        public VoxelGridInfo Info;
        public NativeArray<byte> Density;
        public NativeArray<bool> ChunkDirty;

        public VoxelGrid(int size, float voxelSize)
        {
            if (size % VoxelGridInfo.ChunkSize != 0)
                throw new ArgumentException($"Grid size {size} must be a multiple of {VoxelGridInfo.ChunkSize}");
            Info = new VoxelGridInfo(size, voxelSize);
            Density = new NativeArray<byte>(Info.VoxelCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            ChunkDirty = new NativeArray<bool>(Info.ChunkCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        public bool IsCreated => Density.IsCreated;

        /// <summary>
        /// Mark every chunk overlapping a voxel-space AABB (inclusive min, exclusive max) dirty.
        /// Expands by one voxel so chunks that sample this region through their apron are rebuilt too.
        /// </summary>
        public void MarkDirty(int3 minVoxel, int3 maxVoxel)
        {
            int3 lo = math.clamp(minVoxel - 1, 0, Info.size - 1) / VoxelGridInfo.ChunkSize;
            int3 hi = math.clamp(maxVoxel, 0, Info.size - 1) / VoxelGridInfo.ChunkSize;
            for (int z = lo.z; z <= hi.z; z++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
                ChunkDirty[Info.ChunkIndex(new int3(x, y, z))] = true;
        }

        public void MarkAllDirty()
        {
            for (int i = 0; i < ChunkDirty.Length; i++) ChunkDirty[i] = true;
        }

        public void ClearDirty()
        {
            for (int i = 0; i < ChunkDirty.Length; i++) ChunkDirty[i] = false;
        }

        /// <summary>Clamp a voxel-space sphere to an AABB inside the grid (min inclusive, max exclusive). False if empty.</summary>
        public bool SphereAabb(float3 centerVoxel, float radiusVoxels, out int3 min, out int3 max)
        {
            min = math.clamp((int3)math.floor(centerVoxel - radiusVoxels), 0, Info.size);
            max = math.clamp((int3)math.ceil(centerVoxel + radiusVoxels) + 1, 0, Info.size);
            return math.all(max > min);
        }

        public void Dispose()
        {
            if (Density.IsCreated) Density.Dispose();
            if (ChunkDirty.IsCreated) ChunkDirty.Dispose();
        }
    }
}
