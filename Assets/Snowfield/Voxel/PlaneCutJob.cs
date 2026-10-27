using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// Chop the grid along a plane: everything on the normal's side is gone, the rest is untouched. Unlike the
    /// brushes this has no radius and no falloff — it is a single committed cut, not a stroke, so it runs over the
    /// whole grid once rather than an AABB under a cursor.
    ///
    /// The feather band is what makes the cut look like a cut. A hard "zero everything above the plane" leaves
    /// marching cubes interpolating between a full voxel and an empty one, so the visible surface lands on a voxel
    /// boundary and the cut face comes out as a staircase at whatever angle you chose. Ramping density to zero
    /// across the last voxel instead puts the iso-surface exactly on the plane, at any angle.
    /// </summary>
    [BurstCompile]
    public struct PlaneCutJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> Density;
        public VoxelGridInfo Info;
        /// <summary>A point on the cut plane, in voxel space.</summary>
        public float3 PointVoxel;
        /// <summary>Unit plane normal in voxel space. Snow on this side is removed.</summary>
        public float3 NormalVoxel;
        /// <summary>How far past the plane the cut reaches, in voxels. Snow beyond it is left alone.</summary>
        public float WidthVoxels;
        /// <summary>Width of the ramp below the plane, in voxels. 1 is plenty.</summary>
        public float FeatherVoxels;

        public void Execute(int i)
        {
            int size = Info.size;
            int t = i / size;
            int3 p = new int3(i - t * size, t % size, t / size);
            float h = math.dot((float3)p - PointVoxel, NormalVoxel);

            float feather = math.max(FeatherVoxels, 0.01f);
            float w = math.max(WidthVoxels, 0f);
            if (h <= -feather || h >= w + feather) return;       // outside the slab entirely
            if (h >= 0f && h <= w) { Density[i] = 0; return; }   // inside it

            // In one of the two feather bands: ramp back to full density away from the cut, so the surfaces
            // land on the planes themselves instead of on whichever voxel boundary happens to be near.
            float over = h < 0f ? -h : h - w;
            float ceiling = 255f * (over / feather);
            if (Density[i] <= ceiling) return;
            Density[i] = (byte)math.round(ceiling);
        }
    }
}
