using System;
using System.Collections.Generic;
using Snowfield.Config;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// The shape of a draped strand — geometry, not simulation.
    ///
    /// A strand is laid the way you would lay a real one: start from a sagging curve between the clicked points, put
    /// every point of it *on* the snow (exactly, by <see cref="SurfaceProbe"/>), then alternate pulling it straight
    /// with laying it back down until it has tightened onto the shape. That is a relaxation toward the shortest path
    /// over the surface, which is what "wraps neatly" means; the sag keeps it from tightening into a bare geodesic.
    /// The strand's own thickness is added at the very end, so everything in between reasons about one curve lying on
    /// one surface rather than a tube hovering near it.
    ///
    /// The direction each point searches along is the thing that matters. It starts as the two anchors' own surface
    /// normals blended along the span — on anything rounded that points almost exactly at the surface — and from then
    /// on it is the normal where the point last landed. (Marching toward the grid's bounding-box centre lived here
    /// first, and that direction has nothing to do with the local surface, so neighbouring points flew off to
    /// unrelated places and the strand zigzagged.)
    ///
    /// Every step is a pure function of the anchors, so the strand cannot drift, oscillate or shimmer: move a point
    /// and the whole thing moves with it, exactly and repeatably. (A verlet rope lived here too and jittered
    /// constantly — a running simulation never settles while its ends are being dragged around.) Life comes from
    /// <see cref="DrapedAccessory"/>'s jiggle instead, which is animation on top and never touches the stored points.
    ///
    /// Resting on snow is done by density, not colliders: chunk colliders are only cooked on brush release and are
    /// switched off entirely for carried balls, whereas the density field is always current.
    /// </summary>
    public static class DrapeCurve
    {
        /// <summary>Ground height at a world position. Assigned at runtime by the player side (Sculpture cannot see SnowGround).</summary>
        public static Func<Vector3, float> GroundSampler;

        // Per-point working state, parallel to the path. Static scratch, cleared on entry: Build is pure.
        static readonly List<Vector3> Axis = new List<Vector3>();   // where this point believes the outside is
        static readonly List<bool> OnSnow = new List<bool>();       // did it find a surface to lie on
        static readonly List<bool> Pinned = new List<bool>();       // an anchor the player clicked: never moves

        /// <summary>
        /// Lay a strand through <paramref name="anchors"/> and write its points into <paramref name="into"/>.
        /// <paramref name="normalsInto"/>, when given, receives the surface normal each point ended up lying
        /// against — what a ribbon lies flat to, and what the jiggle swings along.
        /// </summary>
        public static void Build(IReadOnlyList<Vector3> anchors, DrapeStyle style, SculptFeelConfig cfg,
                                 IReadOnlyList<SnowSculpture> surfaces, List<Vector3> into,
                                 List<Vector3> normalsInto = null)
        {
            into.Clear();
            normalsInto?.Clear();
            Axis.Clear(); OnSnow.Clear(); Pinned.Clear();
            if (anchors == null || anchors.Count == 0) return;
            if (anchors.Count == 1)
            {
                into.Add(anchors[0]);
                normalsInto?.Add(OutwardAt(surfaces, anchors[0]));
                return;
            }

            float spacing = Mathf.Max(0.01f, cfg != null ? cfg.drapeSegmentLength : 0.05f);
            int budget = cfg != null ? Mathf.Max(8, cfg.drapeMaxParticles) : 400;
            float offset = (cfg != null ? cfg.drapeSurfaceOffset : 0.01f) + style.Radius;
            int passes = cfg != null ? cfg.drapeRelaxPasses : 5;
            float pull = cfg != null ? cfg.drapeSagPull : 0.35f;

            // How far a point will reach to find snow. The style says how much the material clings, but every strand
            // has to be able to find the surface it was clicked onto, so never less than a few voxels.
            float reach = Mathf.Max(style.SurfaceCling, 0.06f);
            // Wrapping a shape is a longer trip than cutting across it, so a clinging strand is given its points up
            // front instead of stretching its spacing around the curve.
            float allowance = Mathf.Lerp(1f, 1.6f, Mathf.Clamp01(style.SurfaceCling / 0.5f));
            // How far a point slides downhill per pass. Slack is extra rope, and extra rope has to go somewhere.
            float sag = spacing * pull * Mathf.Clamp01((style.Slack - 1f) * 4f);
            // Once a point is on the surface it only moves by one tension step, so later passes need no reach at all.
            float settled = Mathf.Max(spacing * 2f, 0.06f);

            Seed(anchors, surfaces, style, spacing, budget, allowance, into);
            Settle(into, surfaces, reach, settled);
            for (int i = 0; i < passes; i++)
            {
                Relax(into, sag);
                Settle(into, surfaces, reach, settled);
            }
            Lift(into, surfaces, offset, normalsInto);
        }

        /// <summary>
        /// The starting shape: a sagging curve from anchor to anchor, each point carrying the direction it expects
        /// the outside to be in. That expectation is the two anchors' own surface normals blended along the span,
        /// which is what makes the first lay-down land on the shape rather than somewhere near it.
        /// </summary>
        static void Seed(IReadOnlyList<Vector3> anchors, IReadOnlyList<SnowSculpture> surfaces, DrapeStyle style,
                         float spacing, int budget, float allowance, List<Vector3> into)
        {
            Vector3 fromUp = OutwardAt(surfaces, anchors[0]);
            Add(into, anchors[0], fromUp, pinned: true);

            for (int a = 0; a + 1 < anchors.Count; a++)
            {
                Vector3 from = anchors[a], to = anchors[a + 1];
                Vector3 toUp = OutwardAt(surfaces, to);
                float span = Vector3.Distance(from, to);
                int steps = Mathf.Max(1, Mathf.CeilToInt(span * allowance / spacing));
                // Extra rope has to go somewhere: for a shallow catenary an arc this much longer than its span sags
                // by about L*sqrt(3(slack-1)/8). Close enough, and it costs one square root instead of a solver.
                float drop = span * Mathf.Sqrt(Mathf.Max(0f, 3f * (style.Slack - 1f) / 8f));

                for (int i = 1; i <= steps; i++)
                {
                    if (into.Count >= budget) return;
                    float t = i / (float)steps;
                    Vector3 p = Vector3.Lerp(from, to, t);
                    p.y -= drop * 4f * t * (1f - t);   // parabola: nothing at the ends, full sag in the middle
                    Vector3 up = Vector3.Lerp(fromUp, toUp, t);
                    Add(into, p, up.sqrMagnitude < 1e-6f ? Vector3.up : up.normalized, pinned: i == steps);
                }
                fromUp = toUp;
            }
        }

        /// <summary>
        /// Put every point that can reach snow onto it, exactly, and keep the normal it landed against: that is both
        /// the strand's own up vector and the direction the next pass searches along. A point that finds nothing is
        /// hanging in the air, which is correct for a light cord strung between two snowmen.
        /// </summary>
        static void Settle(List<Vector3> points, IReadOnlyList<SnowSculpture> surfaces, float reach, float settled)
        {
            for (int i = 0; i < points.Count; i++)
            {
                // A point that lost its surface (slid round a corner) gets the full reach again to find it back.
                float range = OnSnow[i] ? settled : reach;
                if (SurfaceProbe.Lay(surfaces, points[i], Axis[i], range, out var hit, out var normal))
                {
                    if (!Pinned[i]) points[i] = hit;   // an anchor is where the player clicked; only its normal updates
                    Axis[i] = normal;
                    OnSnow[i] = true;
                }
                else OnSnow[i] = false;
            }
        }

        /// <summary>
        /// One tension pass: each point moves halfway toward the middle of its neighbours — the strand pulling itself
        /// straight — and a little downhill. On the surface those two together are what tightens the curve into a
        /// path that hugs the shape; <see cref="Settle"/> immediately puts it back on the snow.
        /// </summary>
        static void Relax(List<Vector3> points, float sag)
        {
            if (points.Count < 3) return;
            Vector3 prev = points[0];
            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector3 current = points[i];
                if (!Pinned[i])
                {
                    Vector3 middle = (prev + points[i + 1]) * 0.5f;
                    points[i] = (current + middle) * 0.5f + Vector3.down * sag;
                }
                prev = current;
            }
        }

        /// <summary>
        /// Last pass: the curve so far lies *on* the snow, so the strand's centreline has to come off it by its own
        /// thickness. Points hanging in the air only have to stay out of any snow and above the ground.
        /// </summary>
        static void Lift(List<Vector3> points, IReadOnlyList<SnowSculpture> surfaces, float offset,
                         List<Vector3> normalsInto)
        {
            var ground = GroundSampler;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                Vector3 up = OnSnow[i] && Axis[i].sqrMagnitude > 1e-6f ? Axis[i].normalized : Vector3.up;
                if (OnSnow[i]) p += up * offset;
                else if (surfaces != null)
                    for (int c = 0; c < surfaces.Count; c++) SurfaceProbe.PushOut(surfaces[c], ref p, offset);

                if (ground != null)
                {
                    float gy = ground(p) + offset;
                    if (p.y < gy)
                    {
                        p.y = gy;
                        up = GroundUp(p, 0.05f);   // a strand trailing on the ground lies along the ground's slope
                    }
                }
                points[i] = p;
                normalsInto?.Add(up);
            }
        }

        /// <summary>The outward surface direction at a point sitting on snow; the ground's slope, or up, if it is not.</summary>
        static Vector3 OutwardAt(IReadOnlyList<SnowSculpture> surfaces, Vector3 p)
        {
            if (surfaces != null)
                for (int i = 0; i < surfaces.Count; i++)
                {
                    Vector3 n = SurfaceProbe.Normal(surfaces[i], p);
                    if (n.sqrMagnitude > 1e-6f) return n;
                }
            return GroundUp(p, 0.05f);
        }

        /// <summary>Ground normal from the height sampler, by central differences. World up without a sampler.</summary>
        static Vector3 GroundUp(Vector3 p, float h)
        {
            var ground = GroundSampler;
            if (ground == null) return Vector3.up;
            float dx = ground(p + new Vector3(h, 0f, 0f)) - ground(p - new Vector3(h, 0f, 0f));
            float dz = ground(p + new Vector3(0f, 0f, h)) - ground(p - new Vector3(0f, 0f, h));
            var n = new Vector3(-dx, 2f * h, -dz);
            return n.sqrMagnitude < 1e-8f ? Vector3.up : n.normalized;
        }

        static void Add(List<Vector3> into, Vector3 p, Vector3 up, bool pinned)
        {
            into.Add(p);
            Axis.Add(up);
            OnSnow.Add(false);
            Pinned.Add(pinned);
        }
    }
}
