using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>Pure-data description of a grid; safe inside Burst jobs.</summary>
    public struct VoxelGridInfo
    {
        public int size;          // voxels per axis
        public float voxelSize;   // metres per voxel
        public const int ChunkSize = 16;
        public const byte Iso = 128;

        public VoxelGridInfo(int size, float voxelSize)
        {
            this.size = size;
            this.voxelSize = voxelSize;
        }

        public int ChunksPerAxis => size / ChunkSize;
        public int ChunkCount { get { int c = ChunksPerAxis; return c * c * c; } }
        public int VoxelCount => size * size * size;
        public float WorldExtent => size * voxelSize;

        public int Index(int x, int y, int z) => x + y * size + z * size * size;
        public int Index(int3 p) => p.x + p.y * size + p.z * size * size;
        public int ChunkIndex(int3 c) { int n = ChunksPerAxis; return c.x + c.y * n + c.z * n * n; }
        public int3 ChunkCoord(int chunkIndex)
        {
            int n = ChunksPerAxis;
            return new int3(chunkIndex % n, (chunkIndex / n) % n, chunkIndex / (n * n));
        }
        public bool InBounds(int3 p) => math.all(p >= 0) && math.all(p < size);
    }
}
