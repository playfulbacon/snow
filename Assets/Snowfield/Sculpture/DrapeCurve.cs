using System;
using System.Collections.Generic;
using Snowfield.Config;
using Snowfield.Voxel;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// The shape of a hanging strand — geometry, not simulation.
    ///
    /// A sagging curve is drawn between consecutive points, laid onto whatever snow is under it, and smoothed. Every
    /// step is a pure function of the points, so the strand cannot drift, oscillate or shimmer: move a point and the
    /// whole thing moves with it, exactly and repeatably. (A verlet rope lived here first and jittered constantly —
    /// a running simulation never settles while its ends are being dragged around.) Life comes from
    /// <see cref="DrapedAccessory"/>'s jiggle instead, which is animation on top and never touches the stored points.
    ///
    /// Resting on snow is done by density, not colliders: chunk colliders are only cooked on brush release and are
    /// switched off entirely for carried balls, whereas the density field is always current.
    /// </summary>
    public static class DrapeCurve
    {
        /// <summary>Ground height at a world position. Assigned at runtime by the player side (Sculpture cannot see SnowGround).</summary>
        public static Func<Vector3, float> GroundSampler;

        /// <summary>Lay a strand through <paramref name="anchors"/> and write its points into <paramref name="into"/>.</summary>
        public static void Build(IReadOnlyList<Vector3> anchors, DrapeStyle style, SculptFeelConfig cfg,
                                 IReadOnlyList<SnowSculpture> surfaces, List<Vector3> into)
        {
            into.Clear();
            if (anchors == null || anchors.Count == 0) return;
            if (anchors.Count == 1) { into.Add(anchors[0]); return; }

            float spacing = Mathf.Max(0.01f, cfg != null ? cfg.drapeSegmentLength : 0.05f);
            int budget = cfg != null ? Mathf.Max(8, cfg.drapeMaxParticles) : 400;
            float offset = (cfg != null ? cfg.drapeSurfaceOffset : 0.01f) + style.Radius;

            into.Add(anchors[0]);
            for (int a = 0; a + 1 < anchors.Count; a++)
            {
                Vector3 from = anchors[a], to = anchors[a + 1];
                float span = Vector3.Distance(from, to);
                int steps = Mathf.Max(1, Mathf.CeilToInt(span / spacing));
                // Extra rope has to go somewhere: for a shallow catenary an arc this much longer than its span sags
                // by about L*sqrt(3(slack-1)/8). Close enough, and it costs one square root instead of a solver.
                float sag = span * Mathf.Sqrt(Mathf.Max(0f, 3f * (style.Slack - 1f) / 8f));
                for (int i = 1; i <= steps; i++)
                {
                    if (into.Count >= budget) break;
                    float t = i / (float)steps;
                    Vector3 p = Vector3.Lerp(from, to, t);
                    p.y -= sag * 4f * t * (1f - t);   // parabola: nothing at the ends, full sag in the middle
                    into.Add(p);
                }
            }

            LayOnSnow(into, surfaces, offset, style.SurfaceCling);
            int passes = cfg != null ? cfg.drapeSmoothPasses : 2;
            for (int p = 0; p < passes; p++) Smooth(into);
            if (passes > 0) LayOnSnow(into, surfaces, offset, style.SurfaceCling); // smoothing pulls it off again
        }

        /// <summary>
        /// Settle every interior point onto the snow: out of anything it is inside, and - for a clinging style -
        /// back onto the surface if it is hanging in the air beside it. Endpoints are the player's clicks; they stay.
        /// </summary>
        static void LayOnSnow(List<Vector3> points, IReadOnlyList<SnowSculpture> surfaces, float offset, float cling)
        {
            var ground = GroundSampler;
            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector3 p = points[i];
                if (surfaces != null)
                {
                    for (int c = 0; c < surfaces.Count; c++)
                        PushOutOf(surfaces[c], ref p, offset);
                    if (cling > 0f)
                        for (int c = 0; c < surfaces.Count; c++)
                            ClingTo(surfaces[c], ref p, offset, cling);
                }
                if (ground != null)
                {
                    float gy = ground(p) + offset;
                    if (p.y < gy) p.y = gy;
                }
                points[i] = p;
            }
        }

        /// <summary>
        /// Pull a point that is hanging in the air back onto a nearby surface, so a scarf wraps the shape instead of
        /// sagging off it like a flag. This has to march: away from the surface the density is flat zero, so there is
        /// no gradient to follow back in.
        /// </summary>
        static void ClingTo(SnowSculpture s, ref Vector3 p, float offset, float maxDistance)
        {
            if (s == null || s.Grid == null || !s.Grid.IsCreated) return;
            var reachBounds = s.WorldBounds;
            reachBounds.Expand(maxDistance * 2f);
            if (!reachBounds.Contains(p)) return;
            if (s.SampleDensityWorld(p) > VoxelGridInfo.Iso) return;   // inside: PushOutOf owns this case

            Vector3 toward = s.WorldBounds.center - p;
            float distance = toward.magnitude;
            if (distance < 1e-4f) return;
            toward /= distance;

            float step = Mathf.Max(s.Info.voxelSize, maxDistance / 16f);
            float travelled = 0f;
            Vector3 probe = p;
            while (travelled < maxDistance)
            {
                probe += toward * step;
                travelled += step;
                if (s.SampleDensityWorld(probe) <= VoxelGridInfo.Iso) continue;
                PushOutOf(s, ref probe, offset);   // sit just outside, not at the step we happened to land on
                p = probe;
                return;
            }
        }

        /// <summary>Laplacian pass; endpoints pinned. Takes the corners off where the curve met the surface.</summary>
        static void Smooth(List<Vector3> points)
        {
            if (points.Count < 3) return;
            Vector3 prev = points[0];
            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector3 current = points[i];
                points[i] = (prev + current * 2f + points[i + 1]) * 0.25f;
                prev = current;
            }
        }

        /// <summary>
        /// Outward surface direction near <paramref name="p"/>, from the density gradient — what a ribbon lies flat
        /// against. Falls back when the point is nowhere near snow.
        /// </summary>
        public static Vector3 SurfaceUp(IReadOnlyList<SnowSculpture> surfaces, Vector3 p, Vector3 fallback)
        {
            if (surfaces == null) return fallback;
            SnowSculpture near = null;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (s == null || s.Grid == null || !s.Grid.IsCreated) continue;
                if (!s.WorldBounds.Contains(p)) continue;
                near = s;
                Vector3 grad = Gradient(s, p, s.Info.voxelSize);
                if (grad.sqrMagnitude > 1e-6f) return (-grad).normalized;
            }
            // A hair off the surface the density is flat zero and there is no gradient to read. Pointing away from the
            // shape still beats world up, which lays a scarf out flat like a banner.
            if (near != null)
            {
                Vector3 radial = p - near.WorldBounds.center;
                if (radial.sqrMagnitude > 1e-6f) return radial.normalized;
            }
            return fallback;
        }

        /// <summary>Move a point that is inside the snow back out to just above the iso-surface.</summary>
        public static void PushOutOf(SnowSculpture s, ref Vector3 p, float offset)
        {
            if (s == null || s.Grid == null || !s.Grid.IsCreated) return;
            var bounds = s.WorldBounds;
            if (!bounds.Contains(p)) return;
            if (s.SampleDensityWorld(p) <= VoxelGridInfo.Iso) return;

            float h = s.Info.voxelSize;
            Vector3 grad = Gradient(s, p, h);
            float slope = grad.magnitude;

            if (slope < 1e-4f)
            {
                // Deep inside, density saturates at 255 and the gradient vanishes — there is no surface to follow.
                // Walk outward from the grid centre until the surface comes into view, then let the gradient finish.
                Vector3 dir = p - bounds.center;
                if (dir.sqrMagnitude < 1e-8f) dir = Vector3.up;
                dir.Normalize();
                float step = h * 4f;
                for (int i = 0; i < 64; i++)
                {
                    p += dir * step;
                    if (!bounds.Contains(p)) return;                        // left the grid entirely: it is out
                    if (s.SampleDensityWorld(p) <= VoxelGridInfo.Iso) break;
                }
                grad = Gradient(s, p, h);
                slope = grad.magnitude;
                if (slope < 1e-4f) return;
            }

            Vector3 outward = -grad / slope;
            float depth = (s.SampleDensityWorld(p) - VoxelGridInfo.Iso) / slope; // metres to the surface
            p += outward * (depth > 0f ? Mathf.Min(depth + offset, 0.5f) : offset); // cap: a bad gradient must not fling it
        }

        /// <summary>Central-difference density gradient, in density units per metre. Points into the snow.</summary>
        static Vector3 Gradient(SnowSculpture s, Vector3 p, float h) => new Vector3(
            s.SampleDensityWorld(p + new Vector3(h, 0f, 0f)) - s.SampleDensityWorld(p - new Vector3(h, 0f, 0f)),
            s.SampleDensityWorld(p + new Vector3(0f, h, 0f)) - s.SampleDensityWorld(p - new Vector3(0f, h, 0f)),
            s.SampleDensityWorld(p + new Vector3(0f, 0f, h)) - s.SampleDensityWorld(p - new Vector3(0f, 0f, h)))
            / (2f * h);
    }
}
