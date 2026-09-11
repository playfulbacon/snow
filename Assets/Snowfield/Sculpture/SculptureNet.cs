using System;
using System.Collections.Generic;
using Snowfield.Voxel;
using Unity.Mathematics;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// The seam between sculpting gameplay and the network layer (Snowfield.Net). Gameplay code raises what
    /// happened; the net layer (if present) subscribes, assigns ids, and broadcasts. With no subscribers the
    /// raises are no-ops, so single-player behaviour is untouched.
    ///
    /// Two kinds of hooks:
    ///   Lifecycle (Created/Replaced/Removed) — raised ALWAYS, including during remote replay and inside
    ///     composite ops. The registry tracks identity through the factory's destroy-and-replace churn with these.
    ///   Committed (Stroke/Scoop/Fuse/...) — local player intent that must replicate. Raised only when the op is
    ///     locally initiated (<see cref="Suppress"/> is false) and outermost (<see cref="StructuralDepth"/> is 0):
    ///     a Fuse replays its own inner Promote/Regrow on every peer, so those must not broadcast separately.
    /// </summary>
    public static class SculptureNet
    {
        /// <summary>True while the net layer replays a remote event; Committed hooks are not raised.</summary>
        public static bool Suppress;

        /// <summary>Nesting depth of factory structural ops (Fuse → EnsureRoom → Promote...).</summary>
        public static int StructuralDepth { get; private set; }
        public static void PushStructural() => StructuralDepth++;
        public static void PopStructural() => StructuralDepth = Mathf.Max(0, StructuralDepth - 1);
        static bool Broadcastable => !Suppress && StructuralDepth == 0;

        // ---------- lifecycle (always raised) ----------

        /// <summary>Every factory creation, including inner ones (Promote's CreateAt, load, snapshots).</summary>
        public static event Action<SnowSculpture> Created;
        /// <summary>The factory rebuilt a sculpture into a new GameObject (Promote/Regrow): (old, replacement).</summary>
        public static event Action<SnowSculpture, SnowSculpture> Replaced;
        /// <summary>A sculpture was consumed (fuse source). Other destroys are swept lazily by the registry.</summary>
        public static event Action<SnowSculpture> Removed;

        public static void RaiseCreated(SnowSculpture s) => Created?.Invoke(s);
        public static void RaiseReplaced(SnowSculpture old, SnowSculpture replacement) => Replaced?.Invoke(old, replacement);
        public static void RaiseRemoved(SnowSculpture s) => Removed?.Invoke(s);

        // ---------- committed local intents (raised only when broadcastable) ----------

        public struct StrokeInfo
        {
            public int op;              // SculptTool.BrushOp as int: 1 Add, 2 Carve, 3 Smooth
            public Vector3 point;
            public float radius;
            public int ticks;
            public IReadOnlyList<SnowSculpture> targets;
        }
        /// <summary>A frame's worth of brush ticks was applied locally.</summary>
        public static event Action<StrokeInfo> Stroke;
        public static void RaiseStroke(in StrokeInfo info) { if (Broadcastable) Stroke?.Invoke(info); }

        public struct ScoopInfo
        {
            public Vector3 point;
            public float radius;
            public float shoulder;                       // bite falloff (crisp on small packed bites)
            public IReadOnlyList<SnowSculpture> targets; // grids the bite came out of
            public Snowball chunk;                       // the freshly created hand chunk
            public float resultRadius;                   // nominal radius assigned after volume measurement
        }
        /// <summary>LMB bite out of a sculpture: chunk created + kernel removed from targets.</summary>
        public static event Action<ScoopInfo> Scooped;
        public static void RaiseScooped(in ScoopInfo info) { if (Broadcastable) Scooped?.Invoke(info); }

        /// <summary>Handful scooped off the ground: (groundPoint, new ball). The divot replays from config.</summary>
        public static event Action<Vector3, Snowball> GroundScooped;
        public static void RaiseGroundScooped(Vector3 groundPoint, Snowball ball)
        { if (Broadcastable) GroundScooped?.Invoke(groundPoint, ball); }

        public struct ShaveStamp
        {
            public Vector3 point;
            public Vector3 normal;
            public ShaveParams prm;
        }
        public struct ShaveInfo
        {
            public float radius;
            public float slump;                          // relaxation strength after the stamps (fluffy snow), 0 = none
            public IReadOnlyList<ShaveStamp> stamps;     // one frame's worth, in order
            public IReadOnlyList<SnowSculpture> targets;
        }
        /// <summary>A frame of shave/score stamps was applied locally. Params are derived on the origin and replayed verbatim.</summary>
        public static event Action<ShaveInfo> Shaved;
        public static void RaiseShaved(in ShaveInfo info) { if (Broadcastable) Shaved?.Invoke(info); }

        /// <summary>A held ball was squeezed one stage: (ball, linear scale of this stage, compaction blend of this stage).</summary>
        public static event Action<Snowball, float, float> Squeezed;
        public static void RaiseSqueezed(Snowball ball, float linearScale, float compactionBlend)
        { if (Broadcastable) Squeezed?.Invoke(ball, linearScale, compactionBlend); }

        /// <summary>Shaved-off snow shed as a loose lump at the player's feet: (new ball, launch velocity).</summary>
        public static event Action<Snowball, Vector3> Shed;
        public static void RaiseShed(Snowball ball, Vector3 velocity) { if (Broadcastable) Shed?.Invoke(ball, velocity); }

        /// <summary>Thin features crumbled: (sculpture, region min, region extent, region mask — non-zero = cleared).</summary>
        public static event Action<SnowSculpture, int3, int3, byte[]> Thinned;
        public static void RaiseThinned(SnowSculpture s, int3 regionMin, int3 regionExtent, byte[] mask)
        { if (Broadcastable) Thinned?.Invoke(s, regionMin, regionExtent, mask); }

        /// <summary>An island lifted out as a falling ball: (source, ball holding the island, voxel offset ball→source).</summary>
        public static event Action<SnowSculpture, Snowball, int3> Detached;
        public static void RaiseDetached(SnowSculpture source, Snowball island, int3 offset)
        { if (Broadcastable) Detached?.Invoke(source, island, offset); }

        /// <summary>A fluffy ball burst on landing: (the ball — already consumed, its crumbs, their launch velocities).</summary>
        public static event Action<Snowball, IReadOnlyList<Snowball>, Vector3[]> Burst;
        public static void RaiseBurst(Snowball ball, IReadOnlyList<Snowball> crumbs, Vector3[] velocities)
        { if (Broadcastable) Burst?.Invoke(ball, crumbs, velocities); }

        /// <summary>
        /// A fuse is about to run: (target before EnsureRoom/Regrow, source at its final pose). Raised at entry so
        /// the subscriber can read both ids and the source transform before the source is consumed.
        /// </summary>
        public static event Action<SnowSculpture, SnowSculpture> FuseCommitted;
        public static void RaiseFuseCommitted(SnowSculpture target, SnowSculpture source)
        { if (Broadcastable) FuseCommitted?.Invoke(target, source); }

        /// <summary>A brush-path regrow ran: (replacement sculpture, exact grid size, exact world origin).</summary>
        public static event Action<SnowSculpture, int, Vector3> RegrowCommitted;
        public static void RaiseRegrowCommitted(SnowSculpture replacement, int sizeVox, Vector3 origin)
        { if (Broadcastable) RegrowCommitted?.Invoke(replacement, sizeVox, origin); }

        /// <summary>A ball left the local player's hand under physics: (ball, velocity, spin). Zero velocity = drop.</summary>
        public static event Action<Snowball, Vector3, Vector3> Thrown;
        public static void RaiseThrown(Snowball ball, Vector3 velocity, Vector3 spin)
        { if (Broadcastable) Thrown?.Invoke(ball, velocity, spin); }

        /// <summary>A carried/flying object came to rest at its final pose (Land, DropHere, Release...).</summary>
        public static event Action<SnowSculpture, Vector3, Quaternion> Rested;
        public static void RaiseRested(SnowSculpture s, Vector3 position, Quaternion rotation)
        { if (Broadcastable) Rested?.Invoke(s, position, rotation); }

        /// <summary>The local player picked up an existing resting object.</summary>
        public static event Action<SnowSculpture> Grabbed;
        public static void RaiseGrabbed(SnowSculpture s) { if (Broadcastable) Grabbed?.Invoke(s); }

        /// <summary>An accessory was placed: (sculpture, catalog id, surface point, surface normal).</summary>
        public static event Action<SnowSculpture, string, Vector3, Vector3> PropPlaced;
        public static void RaisePropPlaced(SnowSculpture s, string accessoryId, Vector3 point, Vector3 normal)
        { if (Broadcastable) PropPlaced?.Invoke(s, accessoryId, point, normal); }

        /// <summary>An accessory was removed: (sculpture, catalog id, local position — the match key on remotes).</summary>
        public static event Action<SnowSculpture, string, Vector3> PropRemoved;
        public static void RaisePropRemoved(SnowSculpture s, string accessoryId, Vector3 localPos)
        { if (Broadcastable) PropRemoved?.Invoke(s, accessoryId, localPos); }
    }
}
