using System.Collections.Generic;
using Snowfield.Voxel;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// The structural rules that run after any removal (bite, shave, score) on a fixed sculpture:
    ///   1. thinness — features thinner than the compaction-scaled limit crumble (twig-supported snow is exempt);
    ///   2. connectivity — anything no longer reaching the ground lifts out as a loose ball and drops.
    /// Charming failure, not punishment: the crumbs go to the caller to shed at the player's feet, the islands
    /// become balls that inherit their compaction. Loose balls are never checked (they are in hand or resting).
    /// Every outcome is raised through <see cref="SculptureNet"/> with its exact voxels, so peers never re-run this.
    /// </summary>
    public static class SculptureStructure
    {
        /// <summary>World position → ground height. Set by the player layer (the ground lives in Assembly-CSharp). Null = use the snow's own floor.</summary>
        public static System.Func<Vector3, float> GroundHeight;

        public struct Result
        {
            /// <summary>Snow that crumbled (m³): thin features and islands too small to be a ball.</summary>
            public float CrumbVolume;
            /// <summary>Islands that broke off and are now falling balls.</summary>
            public List<Snowball> Detached;
        }

        /// <summary>Region-limited thinness, then whole-grid connectivity. <paramref name="regionMax"/> is exclusive.</summary>
        public static Result Check(SnowSculpture s, int3 regionMin, int3 regionMax)
        {
            var result = new Result { CrumbVolume = 0f, Detached = new List<Snowball>() };
            if (s == null || s.Grid == null || !s.Grid.IsCreated) return result;
            var ball = s.GetComponent<Snowball>();
            if (ball != null && ball.IsLoose) return result;
            var cfg = s.Config;
            if (cfg == null) return result;

            result.CrumbVolume += Thinness(s, regionMin, regionMax);
            result.CrumbVolume += Connectivity(s, result.Detached);
            return result;
        }

        static float Thinness(SnowSculpture s, int3 regionMin, int3 regionMax)
        {
            var cfg = s.Config;
            var info = s.Info;
            float vs = info.voxelSize;
            float rFluffy = cfg.thinLimitFluffy / vs * 0.5f;
            float rPacked = cfg.thinLimitPacked / vs * 0.5f;
            int margin = Mathf.CeilToInt(rFluffy) + 3;
            int3 min = math.clamp(regionMin - margin, 0, info.size);
            int3 max = math.clamp(regionMax + margin, 0, info.size);
            int3 ext = max - min;
            int count = ext.x * ext.y * ext.z;
            if (count <= 0) return 0f;

            var segments = s.SupportSegments(Allocator.TempJob, out int segCount);
            var mask = new NativeArray<byte>(count, Allocator.TempJob);
            var res = new NativeArray<float>(2, Allocator.TempJob);
            new ThinnessJob
            {
                Density = s.Grid.Density, Compaction = s.Grid.Compaction, Info = info,
                RegionMin = min, RegionExtent = ext,
                ThinRadiusFluffy = rFluffy, ThinRadiusPacked = rPacked,
                Segments = segments, SegmentCount = segCount, SupportRadiusVoxels = cfg.twigSupportRadius / vs,
                Mask = mask, Result = res,
            }.Schedule().Complete();
            float removedVoxels = res[0];
            int removedCount = (int)res[1];
            float volume = 0f;
            if (removedCount > 0)
            {
                s.ClearMasked(min, ext, mask);
                SculptureNet.RaiseThinned(s, min, ext, mask.ToArray());
                volume = removedVoxels * vs * vs * vs;
            }
            segments.Dispose(); mask.Dispose(); res.Dispose();
            return volume;
        }

        static float Connectivity(SnowSculpture s, List<Snowball> detached)
        {
            var cfg = s.Config;
            var factory = SculptureFactory.Instance;
            var info = s.Info;
            int size = info.size;
            float vs = info.voxelSize;

            // Ground line per XZ column, in voxel units of y — so a sculpture on a slope keeps every foot it has.
            var groundY = new NativeArray<float>(GroundHeight != null ? size * size : 1, Allocator.TempJob);
            if (GroundHeight != null)
            {
                for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    Vector3 w = s.VoxelToWorld(new float3(x + 0.5f, 0f, z + 0.5f));
                    float gy = GroundHeight(w);
                    groundY[x + z * size] = float.IsNaN(gy) ? float.NaN : s.WorldToVoxel(new float3(w.x, gy, w.z)).y;
                }
            }

            var segments = s.SupportSegments(Allocator.TempJob, out int segCount);
            var label = new NativeArray<int>(info.VoxelCount, Allocator.TempJob);
            var islands = new NativeList<IslandInfo>(8, Allocator.TempJob);
            new ConnectivityJob
            {
                Density = s.Grid.Density, Info = info, Threshold = (byte)Mathf.Clamp(cfg.connectThreshold, 1, 255),
                GroundY = groundY, SeedDepthVoxels = cfg.groundSeedDepth / vs,
                Segments = segments, SegmentCount = segCount, SupportRadiusVoxels = cfg.twigSupportRadius / vs,
                Label = label, Islands = islands,
            }.Schedule().Complete();

            float crumbs = 0f;
            for (int k = 0; k < islands.Length; k++)
            {
                var island = islands[k];
                if (island.Mass <= 0f) continue;
                float volume = island.Mass * vs * vs * vs;
                if (volume < cfg.islandCrumbVolume || factory == null)
                {
                    // Too small to be a ball: crumble it (the same wire shape as a thin break).
                    int3 min = island.Min, ext = island.Max - island.Min + 1;
                    var mask = new NativeArray<byte>(ext.x * ext.y * ext.z, Allocator.TempJob);
                    for (int i = 0; i < mask.Length; i++)
                        mask[i] = (byte)(label[info.Index(BrushMath.AabbCoord(i, min, ext))] == island.Label ? 1 : 0);
                    s.ClearMasked(min, ext, mask);
                    SculptureNet.RaiseThinned(s, min, ext, mask.ToArray());
                    mask.Dispose();
                    crumbs += volume;
                    continue;
                }

                // Lift it into a voxel-aligned ball grid: destination voxel p ↔ source voxel p + offset.
                int3 extent = island.Max - island.Min + 1;
                int longest = math.cmax(extent) + 6;
                int gridSize = Mathf.Max(cfg.snowballGridSize, Mathf.CeilToInt(longest / 16f) * 16);
                int3 offset = (island.Min + island.Max + 1) / 2 - gridSize / 2;
                Vector3 centre = s.VoxelToWorld((float3)offset + gridSize * 0.5f);
                var ball = factory.CreateEmptySnowball(centre, SnowballRadius(volume), gridSize, vs);
                ball.transform.rotation = s.transform.rotation; // voxel axes must match for the aligned copy
                new ExtractIslandJob
                {
                    Density = ball.Sculpture.Grid.Density, Compaction = ball.Sculpture.Grid.Compaction, Info = ball.Sculpture.Info,
                    Source = s.Grid.Density, SourceCompaction = s.Grid.Compaction, SourceLabel = label, SourceInfo = info,
                    Offset = offset, Label = island.Label,
                }.Schedule(ball.Sculpture.Info.VoxelCount, 256).Complete();
                new ClearLabelJob { Density = s.Grid.Density, Compaction = s.Grid.Compaction, Label = label, Target = island.Label }
                    .Schedule(info.VoxelCount, 256).Complete();
                ball.Sculpture.TouchAll();
                ball.radius = SnowballRadius(volume);
                ball.Sculpture.Remesh();
                SculptureNet.RaiseDetached(s, ball, offset);
                ball.Launch(Vector3.zero); // gravity takes it; Land() raises the authoritative rest for peers
                detached.Add(ball);
            }
            if (islands.Length > 0) s.TouchAll();

            groundY.Dispose(); segments.Dispose(); label.Dispose(); islands.Dispose();
            return crumbs;
        }

        public static float SnowballRadius(float volume) => Mathf.Clamp(Mathf.Pow(Mathf.Max(0f, volume) * 3f / (4f * Mathf.PI), 1f / 3f), 0.04f, 1.5f);
    }
}
