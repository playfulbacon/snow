using System.Linq;
using Snowfield.Config;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Creates sculptures and snowballs at runtime, and fuses/promotes them.
    /// Scene singleton on the "Sculptures" root; everything it makes is parented under it.
    /// </summary>
    public class SculptureFactory : MonoBehaviour
    {
        public static SculptureFactory Instance { get; private set; }

        public SculptFeelConfig config;
        public Material snowMaterial;
        [Tooltip("Parent for created sculptures. Defaults to this transform.")]
        public Transform container;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Full grid extent in metres (one axis).</summary>
        public float Extent => config.gridSize * config.voxelSize;

        /// <summary>New empty fixed sculpture whose 96³ grid is centred in XZ on <paramref name="groundCentre"/> with its floor at that height.</summary>
        public SnowSculpture CreateAt(Vector3 groundCentre)
        {
            float extent = Extent;
            var go = new GameObject("Sculpture");
            go.SetActive(false); // assign refs before Awake runs
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.position = groundCentre - new Vector3(extent * 0.5f, 0f, extent * 0.5f);
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = config.gridSize;
            go.SetActive(true);
            SculptureNet.RaiseCreated(s);
            return s;
        }

        /// <summary>Bare sculpture with an explicit grid shape and pose; density is the caller's job (loading).</summary>
        public SnowSculpture CreateEmpty(int gridSize, Vector3 gridOffset, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("Sculpture");
            go.SetActive(false);
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.SetPositionAndRotation(position, rotation);
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = gridSize;
            s.gridOffset = gridOffset;
            go.SetActive(true);
            SculptureNet.RaiseCreated(s);
            return s;
        }

        /// <summary>New fixed sculpture pre-stamped with a small hemisphere mound at the ground point.</summary>
        public SnowSculpture CreateMound(Vector3 groundPoint, float radius)
        {
            var s = CreateAt(groundPoint);
            s.StampSphere(groundPoint, radius, 0.7f, clipBelowWorldY: groundPoint.y - config.voxelSize);
            s.Remesh();
            s.RebuildColliders();
            return s;
        }

        /// <summary>
        /// New loose snowball: a small sculpture whose grid is centred on <paramref name="centre"/>, pre-stamped with a sphere.
        /// The root transform is the ball centre (so rolling can rotate it).
        /// </summary>
        public Snowball CreateSnowball(Vector3 centre, float radius)
        {
            var ball = CreateEmptySnowball(centre, radius);
            ball.Sculpture.StampSphere(centre, radius, ball.stampShoulder);
            ball.Sculpture.Remesh();
            ball.Sculpture.RebuildColliders();
            return ball;
        }

        /// <summary>An empty snowball shell (grid centred on <paramref name="centre"/>); the caller fills the density.</summary>
        public Snowball CreateEmptySnowball(Vector3 centre, float nominalRadius)
        {
            int size = Mathf.Max(16, config.snowballGridSize / 16 * 16);
            float extent = size * config.voxelSize;
            var go = new GameObject("Snowball");
            go.SetActive(false);
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.position = centre;
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = size;
            s.gridOffset = new Vector3(-extent * 0.5f, -extent * 0.5f, -extent * 0.5f);
            var ball = go.AddComponent<Snowball>();
            ball.radius = nominalRadius;
            go.SetActive(true);
            SculptureNet.RaiseCreated(s);
            return ball;
        }

        /// <summary>
        /// Move a loose ball's snow (and any props) into a brand-new fixed full-size sculpture on the ground under it.
        /// Returns the new sculpture; the ball is destroyed.
        /// </summary>
        public SnowSculpture Promote(Snowball ball)
        {
            SculptureNet.PushStructural();
            try
            {
                var big = CreateAt(ball.GroundPoint);
                big.Absorb(ball.Sculpture);
                foreach (var prop in ball.Sculpture.Props.ToArray())
                {
                    prop.transform.SetParent(big.transform, true);
                    prop.Reattach(big);
                }
                big.Remesh();
                big.RebuildColliders();
                SculptureNet.RaiseReplaced(ball.Sculpture, big);
                Retire(ball.Sculpture);
                return big;
            }
            finally { SculptureNet.PopStructural(); }
        }

        /// <summary>
        /// Rebuild a fixed sculpture into a larger, re-centred (unrotated) grid that also covers
        /// <paramref name="neededWorld"/>. Capped at config.maxGridSize; returns the sculpture unchanged at the cap.
        /// The old object is destroyed; props migrate.
        /// </summary>
        public SnowSculpture Regrow(SnowSculpture s, Bounds neededWorld)
        {
            int maxSize = Mathf.Max(config.gridSize, config.maxGridSize / 16 * 16);
            if (s.Info.size >= maxSize) return s; // at the cap: the wall is final (cheap check before the density scan)

            var needed = s.SnowBoundsWorld();
            if (needed.size == Vector3.zero) needed = neededWorld; else needed.Encapsulate(neededWorld);
            float margin = config.regrowMarginVoxels * config.voxelSize;
            needed.Expand(margin * 2f);

            float largestAxis = Mathf.Max(needed.size.x, Mathf.Max(needed.size.y, needed.size.z));
            int sizeVox = Mathf.CeilToInt(largestAxis / config.voxelSize / 16f) * 16;
            sizeVox = Mathf.Clamp(sizeVox, s.Info.size + 16, maxSize); // always at least one chunk bigger

            float extent = sizeVox * config.voxelSize;
            Vector3 origin = new Vector3(
                needed.center.x - extent * 0.5f,
                Mathf.Min(needed.min.y, s.WorldBounds.min.y),
                needed.center.z - extent * 0.5f);
            return RegrowExact(s, sizeVox, origin);
        }

        /// <summary>
        /// The rebuild half of <see cref="Regrow"/> with the target geometry fully specified — the shape the
        /// network layer replays so every peer regrows into byte-identical grids.
        /// </summary>
        public SnowSculpture RegrowExact(SnowSculpture s, int sizeVox, Vector3 origin)
        {
            SnowSculpture big;
            SculptureNet.PushStructural();
            try
            {
                big = CreateEmpty(sizeVox, Vector3.zero, origin, Quaternion.identity);
                big.Absorb(s);
                foreach (var prop in s.Props.ToArray())
                {
                    prop.transform.SetParent(big.transform, true);
                    prop.Reattach(big);
                }
                big.Remesh();
                big.RebuildColliders();
                Debug.Log($"[Snowfield] Regrew sculpture {s.Info.size}³ → {sizeVox}³");
                SculptureNet.RaiseReplaced(s, big);
                Retire(s);
            }
            finally { SculptureNet.PopStructural(); }
            SculptureNet.RaiseRegrowCommitted(big, sizeVox, origin);
            return big;
        }

        /// <summary>A target that can take more snow: loose balls are promoted to a full grid first.</summary>
        public SnowSculpture EnsureRoom(SnowSculpture target)
        {
            var ball = target.GetComponent<Snowball>();
            if (ball != null && ball.IsLoose) return Promote(ball);
            return target;
        }

        /// <summary>Fuse a snowball (at its current transform) into <paramref name="target"/>; the ball is consumed.</summary>
        public SnowSculpture Fuse(SnowSculpture target, Snowball ball) => Fuse(target, ball != null ? ball.Sculpture : null);

        /// <summary>
        /// Fuse any sculpture (at its current transform) into <paramref name="target"/>; the source is consumed,
        /// its props move across. <paramref name="spray"/> is false only for a merge: nothing arrived with force
        /// there, the two shapes were already standing where they stand — see <see cref="MergeTouching"/>.
        /// </summary>
        public SnowSculpture Fuse(SnowSculpture target, SnowSculpture source, bool spray = true)
        {
            if (target == null || source == null || target == source) return target;
            SculptureNet.RaiseFuseCommitted(target, source); // before EnsureRoom: ids + source pose still readable
            if (spray) BurstOnJoin(target, source);          // read the poses before either is rebuilt or consumed
            SculptureNet.PushStructural();
            try
            {
                target = EnsureRoom(target);
                var srcBounds = source.WorldBounds;
                if (!(target.WorldBounds.Contains(srcBounds.min) && target.WorldBounds.Contains(srcBounds.max)))
                    target = Regrow(target, srcBounds);
                target.Absorb(source);
                foreach (var prop in source.Props.ToArray())
                {
                    prop.transform.SetParent(target.transform, true);
                    prop.Reattach(target);
                }
                target.Remesh();
                target.RebuildColliders();
                var targetBall = target.GetComponent<Snowball>();
                if (targetBall != null) targetBall.Fix();
                SculptureNet.RaiseRemoved(source);
                Retire(source);
            }
            finally { SculptureNet.PopStructural(); }
            // The snow that just arrived may have closed the gap to a neighbour: one lump is one sculpture.
            return MergeTouching(target);
        }

        /// <summary>
        /// Snow that reaches a neighbour makes one sculpture out of two. Run at the end of every
        /// <see cref="Fuse"/>, because packing more snow on is the only thing that can close the gap between two
        /// shapes — and once it has, they are one lump, so they get one grid, one id, one set of props, and the
        /// brush can no longer find a seam between them.
        ///
        /// A merge is a local decision that travels as exactly what it is: an ordinary fuse of one sculpture into
        /// another, raised at depth 0 so every peer replays the same two ids and no new wire format is needed. A
        /// remote replay never goes looking for merges of its own (<see cref="SculptureNet.Suppress"/>) — the
        /// originator's events are already on their way, and two peers each deciding for themselves is how ids
        /// come apart.
        /// </summary>
        public SnowSculpture MergeTouching(SnowSculpture s)
        {
            if (s == null || s.Grid == null || config == null || SculptureNet.Suppress) return s;
            // Fuse recurses back into here, so one pass normally finishes the chain; the loop is the backstop.
            for (int i = 0; i < MaxMergesPerFuse; i++)
            {
                var other = FindTouching(s);
                if (other == null) break;
                // The bigger grid survives: Fuse regrows the TARGET to fit the source and gives up at
                // maxGridSize, so pouring the small one into the big one is the direction that cannot clip.
                // No spray: the fuse that closed the gap already threw its puff at the point of contact, and
                // BurstOnJoin would put this one at the centre of a snowman nobody touched.
                s = other.Info.size > s.Info.size ? Fuse(other, s, spray: false) : Fuse(s, other, spray: false);
            }
            return s;
        }

        const int MaxMergesPerFuse = 8;

        /// <summary>The first sculpture whose snow meets <paramref name="s"/>'s, or null. Policy lives here.</summary>
        SnowSculpture FindTouching(SnowSculpture s)
        {
            var root = container != null ? container : transform;
            float reach = Mathf.Max(0, config.mergeReachVoxels) * config.voxelSize;
            var snow = s.SnowBoundsWorld();
            if (snow.size == Vector3.zero) return null;
            var probe = snow;
            probe.Expand(reach * 2f);
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (!child.gameObject.activeSelf) continue; // retired this frame; see Retire
                var c = child.GetComponent<SnowSculpture>();
                if (c == null || c == s || c.Grid == null || !MergeCandidate(c)) continue;
                if (!c.WorldBounds.Intersects(probe)) continue;  // free: grid cubes nowhere near each other
                var cSnow = c.SnowBoundsWorld();
                if (cSnow.size == Vector3.zero || !probe.Intersects(cSnow)) continue;
                if (!FitsOneGrid(snow, cSnow)) continue;
                if (!s.TouchesSnow(c, reach)) continue;
                return c;
            }
            return null;
        }

        /// <summary>
        /// Snow that is not ours to merge. A loose ball is a thing you can still pick up and roll, and it is
        /// allowed to rest against a snowman without becoming part of it — attaching it is a deliberate tap.
        /// Anything in somebody's hands or mid-flight sits on the Ignore Raycast layer, which is the one answer
        /// to "is a player using this".
        /// </summary>
        static bool MergeCandidate(SnowSculpture s)
        {
            // Asked at the call, never cached in a field: a static initializer on a MonoBehaviour runs during
            // type init, and Unity refuses NameToLayer there (it is what the rest of the codebase does too).
            if (s.gameObject.layer == LayerMask.NameToLayer("Ignore Raycast")) return false;
            var ball = s.GetComponent<Snowball>();
            return ball == null || !ball.IsLoose;
        }

        /// <summary>
        /// Let go of a sculpture the factory has just emptied. Destroy is deferred to the end of the frame, so a
        /// consumed source (or the small grid a regrow left behind) is still sitting in the container with its
        /// snow in it when <see cref="FindTouching"/> comes looking for neighbours — and it overlaps its own
        /// replacement perfectly, so it would be merged straight back in. Deactivating it says "gone" now.
        /// </summary>
        static void Retire(SnowSculpture s)
        {
            if (s == null) return;
            s.gameObject.SetActive(false);
            Destroy(s.gameObject);
        }

        /// <summary>
        /// Would both lumps fit in one grid? Past maxGridSize <see cref="Regrow"/> gives up and Absorb clips
        /// whatever hangs outside the grid, which would quietly delete the end of somebody's sculpture — so two
        /// big shapes that meet stay two sculptures, seam and all. That is the cap doing its job.
        /// </summary>
        bool FitsOneGrid(Bounds a, Bounds b)
        {
            var u = a;
            u.Encapsulate(b);
            float margin = config.regrowMarginVoxels * config.voxelSize * 2f;
            int maxSize = Mathf.Max(config.gridSize, config.maxGridSize / 16 * 16);
            return Mathf.Max(u.size.x, Mathf.Max(u.size.y, u.size.z)) + margin <= maxSize * config.voxelSize;
        }

        /// <summary>
        /// Snow landing on snow. Every way that happens comes through <see cref="Fuse"/> — a handful pressed on
        /// by hand, a thrown ball splatting, a remote player's attach being replayed — so this is the one place
        /// that has to know about it, and remotes spray without a single extra byte on the wire.
        ///
        /// The burst goes where the two actually meet: the source's snow centre pushed back toward the target,
        /// not the source's transform (a snowball's transform is its centre, a sculpture's is a grid corner).
        /// </summary>
        static void BurstOnJoin(SnowSculpture target, SnowSculpture source)
        {
            Vector3 at;
            float radius;
            var ball = source.GetComponent<Snowball>();
            if (ball != null)
            {
                at = ball.Centre;                        // a ball's transform IS its centre, and it knows its size
                radius = ball.radius;
            }
            else
            {
                // A whole sculpture being set down: scanning its density for the real extent costs a pass over
                // the grid, and Fuse is about to do far more than that, so this one is worth being exact about.
                var snow = source.SnowBoundsWorld();
                at = snow.size == Vector3.zero ? source.transform.position : snow.center;
                radius = Mathf.Clamp(Mathf.Max(snow.extents.x, snow.extents.z), 0.05f, 0.8f);
            }
            Vector3 away = at - target.WorldBounds.center;
            Vector3 normal = away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.up;
            SnowBurst.Play(at - normal * (radius * 0.5f), normal, radius);
        }
    }
}
