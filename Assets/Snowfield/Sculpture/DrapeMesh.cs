using System.Collections.Generic;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Turns a solved strand path into geometry: a swept tube for a cord, or a flat band (plus fringe at the loose
    /// ends) for a ribbon. Built in the prop's local space so the mesh rides its sculpture.
    /// </summary>
    public static class DrapeMesh
    {
        static readonly List<Vector3> Verts = new List<Vector3>();
        static readonly List<Vector3> Norms = new List<Vector3>();
        static readonly List<int> Tris = new List<int>();

        /// <summary>Rebuild <paramref name="mesh"/> from a world-space path. Surfaces orient a ribbon's flat side.</summary>
        public static void Build(Mesh mesh, IReadOnlyList<Vector3> pathWorld, Transform frame, DrapeStyle style,
                                 IReadOnlyList<SnowSculpture> surfaces)
        {
            Verts.Clear(); Norms.Clear(); Tris.Clear();
            if (mesh == null) return;
            if (pathWorld == null || pathWorld.Count < 2 || frame == null) { mesh.Clear(); return; }

            if (style.Shape == DrapeStyle.Form.Ribbon)
            {
                AppendRibbon(pathWorld, frame, style, surfaces);
                if (style.FringeLength > 0f && style.FringeCount > 0) AppendFringe(pathWorld, frame, style);
            }
            else
            {
                AppendTube(pathWorld, frame, style);
            }

            mesh.Clear();
            if (Verts.Count == 0) return;
            mesh.SetVertices(Verts);
            mesh.SetNormals(Norms);
            mesh.SetTriangles(Tris, 0);
            mesh.RecalculateBounds();
        }

        // ---------- cord ----------

        static void AppendTube(IReadOnlyList<Vector3> path, Transform frame, DrapeStyle style)
        {
            int sides = Mathf.Max(3, style.TubeSides);
            float r = Mathf.Max(0.001f, style.Radius);

            // Parallel transport: carry one reference vector along the path so the tube does not spin.
            Vector3 up = Vector3.up;
            Vector3 prevTangent = Tangent(path, 0);
            Vector3 ref0 = Vector3.Cross(prevTangent, up);
            if (ref0.sqrMagnitude < 1e-6f) ref0 = Vector3.Cross(prevTangent, Vector3.right);
            ref0.Normalize();

            int ringStart = Verts.Count;
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 t = Tangent(path, i);
                // rotate the reference by the tangent's change so consecutive rings stay aligned
                var swing = Quaternion.FromToRotation(prevTangent, t);
                ref0 = (swing * ref0).normalized;
                prevTangent = t;
                Vector3 bi = Vector3.Cross(t, ref0).normalized;

                Vector3 centre = frame.InverseTransformPoint(path[i]);
                for (int s = 0; s < sides; s++)
                {
                    float a = s / (float)sides * Mathf.PI * 2f;
                    Vector3 offset = (ref0 * Mathf.Cos(a) + bi * Mathf.Sin(a));
                    Vector3 localOffset = frame.InverseTransformDirection(offset);
                    Verts.Add(centre + localOffset * r);
                    Norms.Add(localOffset);
                }
            }

            for (int i = 0; i + 1 < path.Count; i++)
            for (int s = 0; s < sides; s++)
            {
                int a = ringStart + i * sides + s;
                int b = ringStart + i * sides + (s + 1) % sides;
                int c = a + sides, d = b + sides;
                Tris.Add(a); Tris.Add(c); Tris.Add(b);
                Tris.Add(b); Tris.Add(c); Tris.Add(d);
            }
        }

        // ---------- ribbon ----------

        static void AppendRibbon(IReadOnlyList<Vector3> path, Transform frame, DrapeStyle style,
                                 IReadOnlyList<SnowSculpture> surfaces)
        {
            float half = Mathf.Max(0.002f, style.Radius);
            int start = Verts.Count;
            Vector3 prevUp = Vector3.up;

            for (int i = 0; i < path.Count; i++)
            {
                Vector3 t = Tangent(path, i);
                // Carry the previous facing as the fallback, and never let it flip: a band whose up vector alternates
                // between neighbouring points folds itself into a bow tie.
                Vector3 up = DrapeCurve.SurfaceUp(surfaces, path[i], prevUp);
                if (Vector3.Dot(up, prevUp) < 0f) up = -up;
                prevUp = up;
                Vector3 side = Vector3.Cross(t, up);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(t, Vector3.forward);
                side.Normalize();
                up = Vector3.Cross(side, t).normalized; // re-orthogonalise so the band is not sheared

                Vector3 centre = frame.InverseTransformPoint(path[i]);
                Vector3 localSide = frame.InverseTransformDirection(side);
                Vector3 localUp = frame.InverseTransformDirection(up);
                Verts.Add(centre - localSide * half); Norms.Add(localUp);
                Verts.Add(centre + localSide * half); Norms.Add(localUp);
            }

            for (int i = 0; i + 1 < path.Count; i++)
            {
                int a = start + i * 2, b = a + 1, c = a + 2, d = a + 3;
                Tris.Add(a); Tris.Add(c); Tris.Add(b);
                Tris.Add(b); Tris.Add(c); Tris.Add(d);
                // backface so a scarf reads from underneath too
                Tris.Add(a); Tris.Add(b); Tris.Add(c);
                Tris.Add(b); Tris.Add(d); Tris.Add(c);
            }
        }

        /// <summary>Short strands hanging off both ends of a ribbon.</summary>
        static void AppendFringe(IReadOnlyList<Vector3> path, Transform frame, DrapeStyle style)
        {
            float half = Mathf.Max(0.002f, style.Radius);
            float strandHalf = Mathf.Max(0.001f, half / Mathf.Max(2, style.FringeCount) * 0.6f);

            for (int end = 0; end < 2; end++)
            {
                int i = end == 0 ? 0 : path.Count - 1;
                Vector3 tip = path[i];
                Vector3 t = Tangent(path, i) * (end == 0 ? -1f : 1f); // point outward from the band
                Vector3 side = Vector3.Cross(t, Vector3.up);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(t, Vector3.forward);
                side.Normalize();

                for (int f = 0; f < style.FringeCount; f++)
                {
                    float u = style.FringeCount == 1 ? 0f : (f / (float)(style.FringeCount - 1) - 0.5f) * 2f;
                    Vector3 root = tip + side * (u * half * 0.85f);
                    // hangs down, with a little of the outward direction so it splays off the end
                    Vector3 fall = (Vector3.down * 0.85f + t * 0.35f).normalized * style.FringeLength;
                    AppendQuad(frame, root, root + fall, side * strandHalf, Vector3.Cross(t, side).normalized);
                }
            }
        }

        static void AppendQuad(Transform frame, Vector3 from, Vector3 to, Vector3 halfWidthWorld, Vector3 normalWorld)
        {
            int a = Verts.Count;
            Vector3 w = frame.InverseTransformDirection(halfWidthWorld);
            Vector3 n = frame.InverseTransformDirection(normalWorld);
            Vector3 f = frame.InverseTransformPoint(from), t = frame.InverseTransformPoint(to);
            Verts.Add(f - w); Norms.Add(n);
            Verts.Add(f + w); Norms.Add(n);
            Verts.Add(t - w); Norms.Add(n);
            Verts.Add(t + w); Norms.Add(n);
            Tris.Add(a); Tris.Add(a + 2); Tris.Add(a + 1);
            Tris.Add(a + 1); Tris.Add(a + 2); Tris.Add(a + 3);
            Tris.Add(a); Tris.Add(a + 1); Tris.Add(a + 2);
            Tris.Add(a + 1); Tris.Add(a + 3); Tris.Add(a + 2);
        }

        // ---------- shared ----------

        static Vector3 Tangent(IReadOnlyList<Vector3> path, int i)
        {
            Vector3 t;
            if (i == 0) t = path[1] - path[0];
            else if (i == path.Count - 1) t = path[i] - path[i - 1];
            else t = path[i + 1] - path[i - 1];
            return t.sqrMagnitude < 1e-8f ? Vector3.forward : t.normalized;
        }

        /// <summary>Positions along the path every <paramref name="spacing"/> metres — where bulbs go.</summary>
        public static void SampleAlong(IReadOnlyList<Vector3> path, float spacing, List<Vector3> into)
        {
            into.Clear();
            if (path == null || path.Count < 2 || spacing <= 0.001f) return;
            float carry = spacing * 0.5f; // start half a gap in, so a bulb does not sit exactly on an anchor
            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector3 a = path[i], b = path[i + 1];
                float seg = Vector3.Distance(a, b);
                if (seg < 1e-6f) continue;
                float travelled = carry;
                while (travelled <= seg)
                {
                    into.Add(Vector3.Lerp(a, b, travelled / seg));
                    travelled += spacing;
                }
                carry = travelled - seg;
            }
        }
    }
}
