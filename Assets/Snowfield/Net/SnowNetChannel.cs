using System.Collections;
using System.Collections.Generic;
using Snowfield.Player;
using Snowfield.Sculpture;
using Unity.Netcode;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// The one wire for shared-world traffic, on a scene-placed NetworkObject. Everything is host-relayed
    /// (P2P host-client): a peer applies its own intent locally for feel, submits it to the host, and the host
    /// broadcasts in arrival order; the origin skips its own echo. Late joiners get the host's world as
    /// per-sculpture RLE snapshots (the save format), paced a few per frame.
    /// </summary>
    public sealed class SnowNetChannel : NetworkBehaviour
    {
        public static SnowNetChannel Instance { get; private set; }

        public SnowWorldSync Sync => _sync;
        SnowWorldSync _sync;

        SculptTool _tool;          // the local player's tool (its Roller carries the streamed object)
        float _streamTimer;
        ulong _lastStreamedId;
        bool _wasCarrying;

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
                _sync.RegisterExistingWorld(); // the host's field is THE field
                NetworkManager.OnClientConnectedCallback += OnClientConnected;
                Debug.Log($"[SnowNet] Channel up as host — sharing {_sync.Registry.Count} sculptures");
            }
            else
            {
                _sync.WipeWorld(); // replaced by the host's snapshot, which is already on its way
                // Clients save the shared field to slot 1: quit-autosave must never clobber this player's own
                // solo field (slot 0) with the host's world.
                if (SaveLoadManager.Instance != null) SaveLoadManager.Instance.field = 1;
                Debug.Log("[SnowNet] Channel up as client — awaiting world snapshot");
            }
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
            SaveLoadManager.BlockManualLoad = false;
            if (!IsServer && SaveLoadManager.Instance != null) SaveLoadManager.Instance.field = 0;
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
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
            _lastStreamedId = id;
            var t = roller.Carried.transform;
            SubmitCarriedRpc(id, t.position, t.rotation, roller.Ball != null ? roller.Ball.radius : 0f, true);
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void SubmitCarriedRpc(ulong id, Vector3 pos, Quaternion rot, float radius, bool carried, RpcParams rpcParams = default)
        {
            BroadcastCarriedRpc(id, pos, rot, radius, carried, rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        void BroadcastCarriedRpc(ulong id, Vector3 pos, Quaternion rot, float radius, bool carried, ulong origin)
        {
            if (origin == NetworkManager.LocalClientId) return;
            _sync?.ApplyCarried(id, pos, rot, radius, carried);
        }

        // ------------------------------------------------------------------ late join

        void OnClientConnected(ulong clientId)
        {
            if (!IsServer || clientId == NetworkManager.LocalClientId) return;
            StartCoroutine(SendWorldTo(clientId));
        }

        IEnumerator SendWorldTo(ulong clientId)
        {
            yield return null; // let the client finish its spawn/wipe first
            List<byte[]> records = _sync.EncodeWorld();
            Debug.Log($"[SnowNet] Sending {records.Count} sculpture snapshots to client {clientId}");
            HelloRpc(SnowWorldSync.ConfigHash(), records.Count, RpcTarget.Single(clientId, RpcTargetUse.Temp));
            int sentThisFrame = 0;
            foreach (var record in records)
            {
                SnapshotRpc(record, RpcTarget.Single(clientId, RpcTargetUse.Temp));
                if (++sentThisFrame >= 3) { sentThisFrame = 0; yield return null; }
            }
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void HelloRpc(int configHash, int sculptureCount, RpcParams rpcParams = default)
        {
            Debug.Log($"[SnowNet] Host world incoming: {sculptureCount} sculptures");
            if (configHash != SnowWorldSync.ConfigHash())
                Debug.LogWarning("[SnowNet] SculptFeelConfig differs from the host's — sculpting will drift. " +
                                 "Update the build so every peer ships the same config.");
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void SnapshotRpc(byte[] record, RpcParams rpcParams = default)
        {
            _sync?.ApplySnapshot(record);
        }
    }
}
