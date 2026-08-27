using System;
using System.Collections.Generic;
using System.IO;
using Snowfield.Config;
using Snowfield.Player;
using Snowfield.Sculpture;
using Snowfield.Voxel;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// The gameplay ⇄ wire translation layer, deliberately transport-free so it tests without netcode:
    ///   outgoing — subscribes to the <see cref="SculptureNet"/> committed hooks, encodes each local intent to a
    ///     compact binary event and hands it to <see cref="Send"/>;
    ///   incoming — <see cref="Apply"/> decodes an event and replays it through the same factory/sculpture APIs
    ///     with <see cref="SculptureNet.Suppress"/> held, so replays never rebroadcast;
    ///   snapshots — the save-format density blob plus pose, per sculpture, for late joiners.
    /// Per CLAUDE.md: brush strokes and structural results are events; physics flights never replay.
    /// </summary>
    public sealed class SnowWorldSync
    {
        public const byte Version = 2;

        enum Kind : byte
        {
            Stroke = 1, Scoop = 2, GroundScoop = 3, Fuse = 4, Regrow = 5,
            Throw = 6, Rest = 7, Grab = 8, PropPlace = 9, PropRemove = 10,
        }

        public readonly SculptureRegistry Registry = new SculptureRegistry();

        /// <summary>Where encoded local events go (the channel's submit RPC). Null while offline.</summary>
        public Action<byte[]> Send;

        static SculptureFactory Factory => SculptureFactory.Instance;
        static SculptFeelConfig Config => Factory != null ? Factory.config : null;

        readonly Dictionary<SnowSculpture, float> _colliderDue = new Dictionary<SnowSculpture, float>();
        readonly List<SnowSculpture> _colliderScratch = new List<SnowSculpture>();
        float _sweepTimer;
        bool _attached;

        // ------------------------------------------------------------------ lifecycle

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            Registry.Attach();
            SculptureNet.Stroke += OnStroke;
            SculptureNet.Scooped += OnScooped;
            SculptureNet.GroundScooped += OnGroundScooped;
            SculptureNet.FuseCommitted += OnFuse;
            SculptureNet.RegrowCommitted += OnRegrow;
            SculptureNet.Thrown += OnThrown;
            SculptureNet.Rested += OnRested;
            SculptureNet.Grabbed += OnGrabbed;
            SculptureNet.PropPlaced += OnPropPlaced;
            SculptureNet.PropRemoved += OnPropRemoved;
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            Registry.Detach();
            SculptureNet.Stroke -= OnStroke;
            SculptureNet.Scooped -= OnScooped;
            SculptureNet.GroundScooped -= OnGroundScooped;
            SculptureNet.FuseCommitted -= OnFuse;
            SculptureNet.RegrowCommitted -= OnRegrow;
            SculptureNet.Thrown -= OnThrown;
            SculptureNet.Rested -= OnRested;
            SculptureNet.Grabbed -= OnGrabbed;
            SculptureNet.PropPlaced -= OnPropPlaced;
            SculptureNet.PropRemoved -= OnPropRemoved;
        }

        /// <summary>Deferred collider cooks for remote strokes + registry hygiene. Call every frame.</summary>
        public void Tick(float dt)
        {
            _sweepTimer += dt;
            if (_sweepTimer >= 5f) { _sweepTimer = 0f; Registry.Sweep(); }

            if (_colliderDue.Count == 0) return;
            _colliderScratch.Clear();
            foreach (var kv in _colliderDue)
                if (Time.time >= kv.Value) _colliderScratch.Add(kv.Key);
            foreach (var s in _colliderScratch)
            {
                _colliderDue.Remove(s);
                if (s != null) s.RebuildColliders();
            }
        }

        void DeferColliders(SnowSculpture s) => _colliderDue[s] = Time.time + 0.4f;

        // ------------------------------------------------------------------ outgoing

        static BinaryWriter NewEvent(Kind kind, out MemoryStream ms)
        {
            ms = new MemoryStream(64);
            var w = new BinaryWriter(ms);
            w.Write(Version);
            w.Write((byte)kind);
            return w;
        }

        void Dispatch(BinaryWriter w, MemoryStream ms)
        {
            w.Flush();
            Send?.Invoke(ms.ToArray());
        }

        static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static void WriteQ(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        static Quaternion ReadQ(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        void WriteIds(BinaryWriter w, IReadOnlyList<SnowSculpture> targets)
        {
            int n = 0;
            Span<ulong> ids = stackalloc ulong[Math.Min(targets.Count, 32)];
            for (int i = 0; i < targets.Count && n < ids.Length; i++)
                if (Registry.TryGetId(targets[i], out ulong id))
                    ids[n++] = id;
            w.Write((byte)n);
            for (int i = 0; i < n; i++) w.Write(ids[i]);
        }

        void OnStroke(SculptureNet.StrokeInfo info)
        {
            if (Send == null) return;
            var w = NewEvent(Kind.Stroke, out var ms);
            w.Write((byte)info.op);
            WriteV3(w, info.point);
            w.Write(info.radius);
            w.Write((ushort)info.ticks);
            WriteIds(w, info.targets);
            Dispatch(w, ms);
        }

        void OnScooped(SculptureNet.ScoopInfo info)
        {
            if (Send == null || !Registry.TryGetId(info.chunk.Sculpture, out ulong chunkId)) return;
            var w = NewEvent(Kind.Scoop, out var ms);
            WriteV3(w, info.point);
            w.Write(info.radius);
            w.Write(info.resultRadius);
            w.Write(chunkId);
            WriteIds(w, info.targets);
            // The chunk's actual density rides along (a 48³ handful RLE-compresses to ~1-2 KB): ExtractFrom on a
            // remote reads whatever is under the kernel THERE, so a scoop racing a concurrent stroke would fork
            // the chunk per peer. The blob makes every peer's chunk byte-identical regardless of arrival order.
            byte[] blob = GridSerializer.Encode(info.chunk.Sculpture.Grid.Density);
            w.Write(blob.Length);
            w.Write(blob);
            Dispatch(w, ms);
        }

        void OnGroundScooped(Vector3 groundPoint, Snowball ball)
        {
            if (Send == null || !Registry.TryGetId(ball.Sculpture, out ulong id)) return;
            var w = NewEvent(Kind.GroundScoop, out var ms);
            WriteV3(w, groundPoint);
            w.Write(id);
            Dispatch(w, ms);
        }

        void OnFuse(SnowSculpture target, SnowSculpture source)
        {
            if (Send == null) return;
            if (!Registry.TryGetId(target, out ulong targetId) || !Registry.TryGetId(source, out ulong sourceId)) return;
            var w = NewEvent(Kind.Fuse, out var ms);
            w.Write(targetId);
            w.Write(sourceId);
            WriteV3(w, source.transform.position);
            WriteQ(w, source.transform.rotation);
            w.Write(source.GetComponent<Snowball>() is { } ball ? ball.radius : 0f);
            Dispatch(w, ms);
        }

        void OnRegrow(SnowSculpture replacement, int sizeVox, Vector3 origin)
        {
            if (Send == null || !Registry.TryGetId(replacement, out ulong id)) return;
            var w = NewEvent(Kind.Regrow, out var ms);
            w.Write(id);
            w.Write(sizeVox);
            WriteV3(w, origin);
            Dispatch(w, ms);
        }

        void OnThrown(Snowball ball, Vector3 velocity, Vector3 spin)
        {
            if (Send == null || !Registry.TryGetId(ball.Sculpture, out ulong id)) return;
            var w = NewEvent(Kind.Throw, out var ms);
            w.Write(id);
            WriteV3(w, ball.transform.position);
            WriteV3(w, velocity);
            WriteV3(w, spin);
            w.Write(ball.radius); // rolling growth only streams unreliably; the throw pins the final size
            Dispatch(w, ms);
        }

        void OnRested(SnowSculpture s, Vector3 pos, Quaternion rot)
        {
            if (Send == null || !Registry.TryGetId(s, out ulong id)) return;
            Send(EncodeRest(id, pos, rot, s.GetComponent<Snowball>() is { } ball ? ball.radius : 0f));
        }

        /// <summary>Also used by the host to settle a ball whose carrier disconnected mid-carry.</summary>
        public byte[] EncodeRest(ulong id, Vector3 pos, Quaternion rot, float radius)
        {
            var w = NewEvent(Kind.Rest, out var ms);
            w.Write(id);
            WriteV3(w, pos);
            WriteQ(w, rot);
            w.Write(radius);
            w.Flush();
            return ms.ToArray();
        }

        void OnGrabbed(SnowSculpture s)
        {
            if (Send == null || !Registry.TryGetId(s, out ulong id)) return;
            var w = NewEvent(Kind.Grab, out var ms);
            w.Write(id);
            Dispatch(w, ms);
        }

        void OnPropPlaced(SnowSculpture s, string accessoryId, Vector3 point, Vector3 normal)
        {
            if (Send == null || !Registry.TryGetId(s, out ulong id)) return;
            var w = NewEvent(Kind.PropPlace, out var ms);
            w.Write(id);
            w.Write(accessoryId);
            WriteV3(w, point);
            WriteV3(w, normal);
            Dispatch(w, ms);
        }

        void OnPropRemoved(SnowSculpture s, string accessoryId, Vector3 localPos)
        {
            if (Send == null || !Registry.TryGetId(s, out ulong id)) return;
            var w = NewEvent(Kind.PropRemove, out var ms);
            w.Write(id);
            w.Write(accessoryId);
            WriteV3(w, localPos);
            Dispatch(w, ms);
        }

        // ------------------------------------------------------------------ incoming

        /// <summary>Replay one remote event. Never rebroadcasts (Suppress held for the duration).</summary>
        public void Apply(byte[] data)
        {
            if (data == null || data.Length < 2) return;
            SculptureNet.Suppress = true;
            try
            {
                using var r = new BinaryReader(new MemoryStream(data, false));
                byte version = r.ReadByte();
                if (version != Version) { Debug.LogWarning($"[SnowNet] Event version {version} ≠ {Version}; dropped"); return; }
                var kind = (Kind)r.ReadByte();
                switch (kind)
                {
                    case Kind.Stroke: ApplyStroke(r); break;
                    case Kind.Scoop: ApplyScoop(r); break;
                    case Kind.GroundScoop: ApplyGroundScoop(r); break;
                    case Kind.Fuse: ApplyFuse(r); break;
                    case Kind.Regrow: ApplyRegrow(r); break;
                    case Kind.Throw: ApplyThrow(r); break;
                    case Kind.Rest: ApplyRest(r); break;
                    case Kind.Grab: ApplyGrab(r); break;
                    case Kind.PropPlace: ApplyPropPlace(r); break;
                    case Kind.PropRemove: ApplyPropRemove(r); break;
                    default: Debug.LogWarning($"[SnowNet] Unknown event kind {kind}"); break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SnowNet] Failed to apply remote event: {e}");
            }
            finally
            {
                SculptureNet.Suppress = false;
                Registry.PendingId = null; // a replay that threw mid-create must not leak its id to the next one
            }
        }

        int ReadTargets(BinaryReader r, List<SnowSculpture> into)
        {
            into.Clear();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                ulong id = r.ReadUInt64();
                if (Registry.TryGet(id, out var s)) into.Add(s);
            }
            return into.Count;
        }

        readonly List<SnowSculpture> _targetScratch = new List<SnowSculpture>();

        void ApplyStroke(BinaryReader r)
        {
            var cfg = Config;
            if (cfg == null) return;
            int op = r.ReadByte();
            Vector3 point = ReadV3(r);
            float radius = r.ReadSingle();
            // Honest peers batch ≤8 ticks/frame; a forged 65535 would freeze the main thread in brush jobs.
            int ticks = Mathf.Min(r.ReadUInt16(), (ushort)64);
            radius = Mathf.Clamp(radius, 0.01f, 4f);
            ReadTargets(r, _targetScratch);
            foreach (var s in _targetScratch)
            {
                for (int i = 0; i < ticks; i++)
                {
                    switch (op)
                    {
                        case 1: s.ApplyAdd(point, radius, cfg.addRatePerTick, cfg.addShoulder); break;
                        case 2: s.ApplyAdd(point, radius, -cfg.addRatePerTick, cfg.addShoulder); break;
                        case 3: s.ApplySmooth(point, radius, cfg.smoothStrength, cfg.smoothShoulder); break;
                    }
                }
                s.Remesh();
                DeferColliders(s);
            }
        }

        void ApplyScoop(BinaryReader r)
        {
            var factory = Factory;
            var cfg = Config;
            if (factory == null || cfg == null) return;
            Vector3 point = ReadV3(r);
            float radius = r.ReadSingle();
            float resultRadius = r.ReadSingle();
            ulong chunkId = r.ReadUInt64();
            ReadTargets(r, _targetScratch);
            int blobLen = r.ReadInt32();
            if (blobLen <= 0 || blobLen > 4 * 1024 * 1024)
            { Debug.LogWarning($"[SnowNet] Scoop dropped: bad blob length {blobLen}"); return; }
            byte[] blob = r.ReadBytes(blobLen);

            Registry.PendingId = chunkId;
            var chunk = factory.CreateEmptySnowball(point, radius);
            Registry.PendingId = null;
            // The chunk contents come off the wire (byte-identical on every peer); only the carve is replayed.
            GridSerializer.Decode(blob, chunk.Sculpture.Grid.Density);
            chunk.Sculpture.Grid.MarkAllDirty();
            foreach (var s in _targetScratch)
            {
                if (s == chunk.Sculpture) continue;
                s.ApplyAdd(point, radius, -255f, cfg.addShoulder);
                s.Remesh();
                s.RebuildColliders();
            }
            chunk.radius = resultRadius;
            chunk.Sculpture.Remesh();
            chunk.SetInteractable(false);
            chunk.SetState(Snowball.State.Carrying);
            RemoteBallDrive.Ensure(chunk.Sculpture);
        }

        void ApplyGroundScoop(BinaryReader r)
        {
            var factory = Factory;
            var cfg = Config;
            if (factory == null || cfg == null) return;
            Vector3 groundPoint = ReadV3(r);
            ulong id = r.ReadUInt64();
            float rr = cfg.scoopRadius;
            Registry.PendingId = id;
            var ball = factory.CreateSnowball(groundPoint + Vector3.up * rr, rr);
            Registry.PendingId = null;
            ball.SetInteractable(false);
            ball.SetState(Snowball.State.Carrying);
            RemoteBallDrive.Ensure(ball.Sculpture);
            var ground = SnowGround.Instance;
            if (ground != null)
                ground.StampDepression(groundPoint, rr * 1.6f, cfg.scoopDivotDepth, 0.6f);
        }

        void ApplyFuse(BinaryReader r)
        {
            var factory = Factory;
            if (factory == null) return;
            ulong targetId = r.ReadUInt64();
            ulong sourceId = r.ReadUInt64();
            Vector3 pos = ReadV3(r);
            Quaternion rot = ReadQ(r);
            float radius = r.ReadSingle();
            if (!Registry.TryGet(targetId, out var target) || !Registry.TryGet(sourceId, out var source))
            {
                Debug.LogWarning($"[SnowNet] Fuse dropped: unknown id {targetId:x}/{sourceId:x}");
                return;
            }
            RemoteBallDrive.Clear(source);
            source.transform.SetPositionAndRotation(pos, rot);
            ReconcileRadius(source, radius);
            SetInteractableDeep(source, true);
            factory.Fuse(target, source);
        }

        /// <summary>
        /// Snap a ball's radius to the reliable-event value: rolling growth only reaches peers through the lossy
        /// 10 Hz pose stream, and Promote/Fuse geometry derives from the radius, so it must be exact at the
        /// moment anything structural consumes the ball.
        /// </summary>
        static void ReconcileRadius(SnowSculpture s, float radius)
        {
            if (radius <= 0f || radius > 2f) return;
            var ball = s.GetComponent<Snowball>();
            if (ball == null || Mathf.Abs(ball.radius - radius) < 1e-4f) return;
            if (radius > ball.radius) { ball.Grow(radius); s.Remesh(); }
            else ball.radius = radius; // never came up: the density is already there, only the number was high
        }

        void ApplyRegrow(BinaryReader r)
        {
            var factory = Factory;
            if (factory == null) return;
            ulong id = r.ReadUInt64();
            int sizeVox = r.ReadInt32();
            Vector3 origin = ReadV3(r);
            if (!Registry.TryGet(id, out var s)) { Debug.LogWarning($"[SnowNet] Regrow dropped: unknown id {id:x}"); return; }
            // Wire values bypass Regrow's clamp; a hostile/corrupt size would allocate gigabytes (or overflow
            // VoxelCount into an out-of-bounds native write in release builds). Mirror the design cap.
            var cfg = Config;
            int maxSize = cfg != null ? Mathf.Max(cfg.gridSize, cfg.maxGridSize / 16 * 16) : 512;
            if (sizeVox <= 0 || sizeVox % 16 != 0 || sizeVox > maxSize)
            { Debug.LogWarning($"[SnowNet] Regrow dropped: bad grid size {sizeVox}"); return; }
            factory.RegrowExact(s, sizeVox, origin);
        }

        void ApplyThrow(BinaryReader r)
        {
            ulong id = r.ReadUInt64();
            Vector3 pos = ReadV3(r);
            Vector3 vel = ReadV3(r);
            Vector3 spin = ReadV3(r);
            float radius = r.ReadSingle();
            if (!Registry.TryGet(id, out var s)) return;
            var ball = s.GetComponent<Snowball>();
            if (ball == null) return;
            s.transform.position = pos;
            ReconcileRadius(s, radius);
            ball.SetState(Snowball.State.Flying);
            RemoteBallDrive.Ensure(s).BeginFlight(vel, spin);
        }

        void ApplyRest(BinaryReader r)
        {
            ulong id = r.ReadUInt64();
            Vector3 pos = ReadV3(r);
            Quaternion rot = ReadQ(r);
            float radius = r.ReadSingle();
            if (!Registry.TryGet(id, out var s)) return;
            RemoteBallDrive.Clear(s);
            s.transform.SetPositionAndRotation(pos, rot);
            ReconcileRadius(s, radius);
            SetInteractableDeep(s, true);
            var ball = s.GetComponent<Snowball>();
            if (ball != null) ball.SetState(Snowball.State.Resting);
            s.Remesh();
            s.ForceRebuildAllColliders();
            Physics.SyncTransforms();
        }

        void ApplyGrab(BinaryReader r)
        {
            ulong id = r.ReadUInt64();
            if (!Registry.TryGet(id, out var s)) return;
            SetInteractableDeep(s, false);
            var ball = s.GetComponent<Snowball>();
            if (ball != null) ball.SetState(Snowball.State.Carrying);
            RemoteBallDrive.Ensure(s);
        }

        void ApplyPropPlace(BinaryReader r)
        {
            ulong id = r.ReadUInt64();
            string accessoryId = r.ReadString();
            Vector3 point = ReadV3(r);
            Vector3 normal = ReadV3(r);
            if (!Registry.TryGet(id, out var s)) return;
            var entry = AccessoryCatalog.Find(accessoryId);
            if (entry == null) { Debug.LogWarning($"[SnowNet] Unknown accessory '{accessoryId}'"); return; }
            AccessoryPlacer.PlaceEntry(s, entry, point, normal);
        }

        void ApplyPropRemove(BinaryReader r)
        {
            ulong id = r.ReadUInt64();
            string accessoryId = r.ReadString();
            Vector3 localPos = ReadV3(r);
            if (!Registry.TryGet(id, out var s)) return;
            SculptureProp best = null;
            float bestDist = float.MaxValue;
            foreach (var p in s.Props)
            {
                if (p == null || p.prefabId != accessoryId) continue;
                float d = (p.LocalPos - localPos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = p; }
            }
            if (best != null) best.Remove();
        }

        /// <summary>Carried-pose stream (outside the event bus: unreliable, latest-wins).</summary>
        public void ApplyCarried(ulong id, Vector3 pos, Quaternion rot, float radius, bool carried)
        {
            if (!Registry.TryGet(id, out var s)) return;
            var drive = RemoteBallDrive.Ensure(s);
            if (carried && !drive.CarriedActive) SetInteractableDeep(s, false);
            drive.SetCarriedTarget(pos, rot, radius, carried);
        }

        /// <summary>Mirror of SnowballRoller.SetInteractable for replay (that one is private).</summary>
        internal static void SetInteractableDeep(SnowSculpture s, bool on)
        {
            int layer = on ? 0 : LayerMask.NameToLayer("Ignore Raycast");
            foreach (var t in s.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            s.SetCollidersEnabled(on);
            foreach (var c in s.GetComponentsInChildren<Collider>(true))
                if (c.GetComponentInParent<SculptureProp>() != null) c.enabled = on;
        }

        // ------------------------------------------------------------------ snapshots (late join)

        /// <summary>Host bootstrap: the local field becomes the shared field.</summary>
        public void RegisterExistingWorld() => Registry.RegisterExisting();

        /// <summary>Client bootstrap: the host's snapshot replaces whatever this client had locally.</summary>
        public void WipeWorld()
        {
            foreach (var s in UnityEngine.Object.FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(s.gameObject);
            Registry.Clear();
        }

        public List<byte[]> EncodeWorld()
        {
            var records = new List<byte[]>();
            foreach (var kv in Registry.All())
            {
                var s = kv.Value;
                if (s.Grid == null || !s.Grid.IsCreated) continue;
                records.Add(EncodeSnapshot(kv.Key, s));
            }
            return records;
        }

        public static byte[] EncodeSnapshot(ulong id, SnowSculpture s)
        {
            var ms = new MemoryStream(4096);
            var w = new BinaryWriter(ms);
            w.Write(Version);
            w.Write(id);
            WriteV3(w, s.transform.position);
            WriteQ(w, s.transform.rotation);
            w.Write(s.Info.size);
            w.Write(s.Info.voxelSize);
            WriteV3(w, s.gridOffset);
            var ball = s.GetComponent<Snowball>();
            w.Write(ball != null);
            w.Write(ball != null ? ball.radius : 0f);
            w.Write(ball != null && ball.IsLoose);
            byte[] blob = GridSerializer.Encode(s.Grid.Density);
            w.Write(blob.Length);
            w.Write(blob);
            w.Write(s.Props.Count);
            foreach (var p in s.Props)
            {
                w.Write(p.prefabId ?? "");
                WriteV3(w, p.LocalPos);
                WriteQ(w, p.LocalRot);
            }
            w.Flush();
            return ms.ToArray();
        }

        public void ApplySnapshot(byte[] data)
        {
            var factory = Factory;
            if (factory == null) return;
            SculptureNet.Suppress = true;
            try
            {
                using var r = new BinaryReader(new MemoryStream(data, false));
                byte version = r.ReadByte();
                if (version != Version) { Debug.LogWarning($"[SnowNet] Snapshot version {version} ≠ {Version}; dropped"); return; }
                ulong id = r.ReadUInt64();
                Vector3 pos = ReadV3(r);
                Quaternion rot = ReadQ(r);
                int gridSize = r.ReadInt32();
                float voxelSize = r.ReadSingle();
                Vector3 gridOffset = ReadV3(r);
                bool isSnowball = r.ReadBoolean();
                float radius = r.ReadSingle();
                bool isLoose = r.ReadBoolean();
                int blobLen = r.ReadInt32();
                if (gridSize <= 0 || gridSize % 16 != 0 || gridSize > 512
                    || blobLen <= 0 || blobLen > 32 * 1024 * 1024)
                { Debug.LogWarning($"[SnowNet] Snapshot with bad grid size {gridSize} / blob {blobLen}; dropped"); return; }
                byte[] blob = r.ReadBytes(blobLen);

                if (Registry.TryGet(id, out var existing))
                    UnityEngine.Object.DestroyImmediate(existing.gameObject); // re-sync: replace

                Registry.PendingId = id;
                var s = factory.CreateEmpty(gridSize, gridOffset, pos, rot);
                Registry.PendingId = null;
                try
                {
                    GridSerializer.Decode(blob, s.Grid.Density);
                }
                catch
                {
                    // A corrupt blob must not leave a mapped empty ghost behind.
                    UnityEngine.Object.DestroyImmediate(s.gameObject);
                    Registry.Sweep();
                    throw;
                }
                s.Grid.MarkAllDirty();

                if (isSnowball)
                {
                    var ball = s.gameObject.AddComponent<Snowball>();
                    ball.radius = radius;
                    if (!isLoose) ball.Fix();
                }

                int propCount = r.ReadInt32();
                for (int i = 0; i < propCount; i++)
                {
                    string prefabId = r.ReadString();
                    Vector3 localPos = ReadV3(r);
                    Quaternion localRot = ReadQ(r);
                    var entry = AccessoryCatalog.Find(prefabId);
                    if (entry == null) continue;
                    var go = entry.Build();
                    AccessoryCatalog.MakePickable(go);
                    var prop = go.AddComponent<SculptureProp>();
                    prop.Attach(s, prefabId, s.transform.TransformPoint(localPos), s.transform.rotation * localRot);
                }

                s.Remesh();
                s.RebuildColliders();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SnowNet] Failed to apply snapshot: {e}");
            }
            finally
            {
                SculptureNet.Suppress = false;
            }
        }

        /// <summary>Feel-config parity check: peers with different configs would silently diverge.</summary>
        public static int ConfigHash()
        {
            var c = Config;
            if (c == null) return 0;
            unchecked
            {
                int h = 17;
                h = h * 31 + c.gridSize;
                h = h * 31 + c.snowballGridSize;
                h = h * 31 + c.maxGridSize;
                h = h * 31 + c.regrowMarginVoxels;
                h = h * 31 + BitConverter.SingleToInt32Bits(c.voxelSize);
                h = h * 31 + BitConverter.SingleToInt32Bits(c.addRatePerTick);
                h = h * 31 + BitConverter.SingleToInt32Bits(c.addShoulder);
                h = h * 31 + BitConverter.SingleToInt32Bits(c.smoothStrength);
                h = h * 31 + BitConverter.SingleToInt32Bits(c.smoothShoulder);
                h = h * 31 + BitConverter.SingleToInt32Bits(c.scoopRadius);
                h = h * 31 + BitConverter.SingleToInt32Bits(c.ticksPerSecond);
                return h;
            }
        }
    }
}
