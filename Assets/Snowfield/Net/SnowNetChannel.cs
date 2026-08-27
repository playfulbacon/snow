using System.Collections;
using System.Collections.Generic;
using Snowfield.Player;
using Snowfield.Sculpture;
using Unity.Netcode;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// The one wire for shared-world traffic, on a host-spawned NetworkObject. Everything is host-relayed
    /// (P2P host-client): a peer applies its own intent locally for feel, submits it to the host, and the host
    /// broadcasts in arrival order; the origin skips its own echo.
    ///
    /// Late join is race-free: the host encodes the world synchronously inside the connect callback (so every
    /// event the new client will ever receive postdates the encode), streams it in 32 KB parts (NGO's RPC
    /// writer caps a single call at 64 KB — a grown 192³ sculpture record is bigger), and the client holds all
    /// world events until the promised snapshot count has landed, then drains them in order.
    ///
    /// A carrier that disconnects mid-carry would leave its ball non-interactable everywhere; the host tracks
    /// who streams which ball and settles orphans onto the snow with a broadcast Rest event.
    /// </summary>
    public sealed class SnowNetChannel : NetworkBehaviour
    {
        public static SnowNetChannel Instance { get; private set; }

        public SnowWorldSync Sync => _sync;
        SnowWorldSync _sync;

        SculptTool _tool;          // the local player's tool (its Roller carries the streamed object)
        float _streamTimer;
        bool _wasCarrying;

        // Host: which ball each client's pose stream is driving (settled on disconnect).
        readonly Dictionary<ulong, ulong> _carriedByClient = new Dictionary<ulong, ulong>();

        // Client: snapshot gate. Events are held until the host's join-time world has fully landed.
        bool _gateOpen;
        int _expectedSnapshots = -1;
        int _receivedSnapshots;
        readonly List<byte[]> _heldEvents = new List<byte[]>();
        byte[] _partAssembly;
        int _partAssemblyFilled;

        bool Gated => !_gateOpen;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            _sync = new SnowWorldSync();
            _sync.Registry.LocalPrefix = (uint)NetworkManager.LocalClientId + 1; // +1: prefix 0 is never valid
            _sync.Attach();
            _sync.Send = SubmitLocal;
            SaveLoadManager.BlockManualLoad = true;

            if (IsServer)
            {
                _gateOpen = true; // the host IS the world; nothing to wait for
                _sync.RegisterExistingWorld(); // the host's field is THE field
                NetworkManager.OnClientConnectedCallback += OnClientConnected;
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
                Debug.Log($"[SnowNet] Channel up as host — sharing {_sync.Registry.Count} sculptures");
            }
            else
            {
                _sync.WipeWorld(); // replaced by the host's snapshot, which is already on its way
                // Clients save the shared field to slot 1: quit-autosave must never clobber this player's own
                // solo field (slot 0) with the host's world. Deliberately NOT reset on despawn — NGO despawns
                // this channel during application quit and on host loss, both moments where the world in memory
                // is still the shared one; pointing the slot back at 0 would overwrite the solo field.
                if (SaveLoadManager.Instance != null) SaveLoadManager.Instance.field = 1;
                Debug.Log("[SnowNet] Channel up as client — awaiting world snapshot");
            }
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
            SaveLoadManager.BlockManualLoad = false;
            if (IsServer && NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }
            // Session over (host quit, we quit, or we lost the host): any ball still remote-driven would stay
            // non-interactable forever — and a survivor who re-hosts would share that bricked state. Settle them.
            foreach (var drive in FindObjectsByType<RemoteBallDrive>(FindObjectsSortMode.None))
                drive.SettleInPlace();
            _sync?.Detach();
            _sync = null;
        }

        void SubmitLocal(byte[] data) => SubmitEventRpc(data);

        void Update()
        {
            if (!IsSpawned || _sync == null) return;
            _sync.Tick(Time.deltaTime);
            StreamCarried();
        }

        // ------------------------------------------------------------------ event relay

        [Rpc(SendTo.Server)]
        void SubmitEventRpc(byte[] data, RpcParams rpcParams = default)
        {
            BroadcastEventRpc(data, rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Everyone)]
        void BroadcastEventRpc(byte[] data, ulong origin)
        {
            if (origin == NetworkManager.LocalClientId) return; // the origin already applied it locally
            if (Gated) { _heldEvents.Add(data); return; }       // joining: world snapshot still streaming in
            _sync?.Apply(data);
        }

        // ------------------------------------------------------------------ carried-pose stream (~10 Hz, unreliable)

        void StreamCarried()
        {
            if (_tool == null)
            {
                _tool = FindAnyObjectByType<SculptTool>();
                if (_tool == null) return;
            }
            var roller = _tool.Roller;
            if (roller == null) return;

            bool carrying = roller.IsCarrying && roller.Carried != null;
            if (!carrying)
            {
                _wasCarrying = false;
                return;
            }

            _streamTimer += Time.deltaTime;
            if (_streamTimer < 0.1f && _wasCarrying) return;
            _streamTimer = 0f;
            _wasCarrying = true;

            if (!_sync.Registry.TryGetId(roller.Carried, out ulong id)) return;
            var t = roller.Carried.transform;
            SubmitCarriedRpc(id, t.position, t.rotation, roller.Ball != null ? roller.Ball.radius : 0f, true);
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void SubmitCarriedRpc(ulong id, Vector3 pos, Quaternion rot, float radius, bool carried, RpcParams rpcParams = default)
        {
            ulong origin = rpcParams.Receive.SenderClientId;
            if (carried) _carriedByClient[origin] = id;
            else _carriedByClient.Remove(origin);
            BroadcastCarriedRpc(id, pos, rot, radius, carried, origin);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        void BroadcastCarriedRpc(ulong id, Vector3 pos, Quaternion rot, float radius, bool carried, ulong origin)
        {
            if (origin == NetworkManager.LocalClientId) return;
            if (Gated) return; // latest-wins: the next 10 Hz packet after the gate opens is just as good
            _sync?.ApplyCarried(id, pos, rot, radius, carried);
        }

        // ------------------------------------------------------------------ carrier disconnect

        void OnClientDisconnected(ulong clientId)
        {
            if (!IsServer || _sync == null) return;
            if (!_carriedByClient.TryGetValue(clientId, out ulong ballId)) return;
            _carriedByClient.Remove(clientId);
            if (!_sync.Registry.TryGet(ballId, out var s)) return;
            // A legitimate Rest/Fuse already cleared the drive; only a still-driven ball is orphaned.
            if (s.GetComponent<RemoteBallDrive>() == null) return;

            var ball = s.GetComponent<Snowball>();
            float radius = ball != null ? ball.radius : 0.15f;
            Vector3 pos = s.transform.position;
            var ground = SnowGround.Instance;
            if (ground != null && ground.IsCreated)
                pos.y = ground.SampleHeight(pos) + radius;
            byte[] rest = _sync.EncodeRest(ballId, pos, s.transform.rotation, radius);
            Debug.Log($"[SnowNet] Client {clientId} left mid-carry; settling their ball onto the snow");
            _sync.Apply(rest);
            BroadcastEventRpc(rest, NetworkManager.LocalClientId);
        }

        // ------------------------------------------------------------------ late join

        void OnClientConnected(ulong clientId)
        {
            if (!IsServer || clientId == NetworkManager.LocalClientId) return;
            // Encode NOW, synchronously: every event broadcast from here on reaches the new client too, so
            // "snapshot at connect + all later events" is exactly the world. Encoding inside the coroutine
            // would let events slip between encode and send — applied against ids the client doesn't have yet.
            List<byte[]> records = _sync.EncodeWorld();
            StartCoroutine(SendWorldTo(clientId, records));
        }

        const int SnapshotPartBytes = 32 * 1024; // NGO's per-RPC writer caps out at 64 KB

        IEnumerator SendWorldTo(ulong clientId, List<byte[]> records)
        {
            Debug.Log($"[SnowNet] Sending {records.Count} sculpture snapshots to client {clientId}");
            HelloRpc(SnowWorldSync.ConfigHash(), records.Count, RpcTarget.Single(clientId, RpcTargetUse.Temp));
            int partsThisFrame = 0;
            foreach (var record in records)
            {
                for (int offset = 0; offset < record.Length; offset += SnapshotPartBytes)
                {
                    int n = System.Math.Min(SnapshotPartBytes, record.Length - offset);
                    var part = new byte[n];
                    System.Buffer.BlockCopy(record, offset, part, 0, n);
                    SnapshotPartRpc(record.Length, offset, part, RpcTarget.Single(clientId, RpcTargetUse.Temp));
                    if (++partsThisFrame >= 3) { partsThisFrame = 0; yield return null; }
                }
            }
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void HelloRpc(int configHash, int sculptureCount, RpcParams rpcParams = default)
        {
            Debug.Log($"[SnowNet] Host world incoming: {sculptureCount} sculptures");
            _expectedSnapshots = Mathf.Max(0, sculptureCount);
            _receivedSnapshots = 0;
            if (configHash != SnowWorldSync.ConfigHash())
                Debug.LogWarning("[SnowNet] SculptFeelConfig differs from the host's — sculpting will drift. " +
                                 "Update the build so every peer ships the same config.");
            MaybeOpenGate();
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void SnapshotPartRpc(int totalLength, int offset, byte[] part, RpcParams rpcParams = default)
        {
            if (part == null || totalLength <= 0 || totalLength > 64 * 1024 * 1024
                || offset < 0 || offset + part.Length > totalLength) return;
            if (_partAssembly == null || _partAssembly.Length != totalLength)
            {
                _partAssembly = new byte[totalLength];
                _partAssemblyFilled = 0;
            }
            System.Buffer.BlockCopy(part, 0, _partAssembly, offset, part.Length);
            _partAssemblyFilled += part.Length;
            if (_partAssemblyFilled < totalLength) return;

            var record = _partAssembly;
            _partAssembly = null;
            _partAssemblyFilled = 0;
            _sync?.ApplySnapshot(record);
            _receivedSnapshots++;
            MaybeOpenGate();
        }

        void MaybeOpenGate()
        {
            if (_gateOpen || _expectedSnapshots < 0 || _receivedSnapshots < _expectedSnapshots) return;
            _gateOpen = true;
            if (_heldEvents.Count > 0)
                Debug.Log($"[SnowNet] World complete; applying {_heldEvents.Count} events that arrived during the join");
            foreach (var evt in _heldEvents) _sync?.Apply(evt);
            _heldEvents.Clear();
        }
    }
}
