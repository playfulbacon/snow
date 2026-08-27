using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.TestHelpers.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace Snowfield.Net.Tests
{
    /// <summary>
    /// Proves the wire pattern SnowNetChannel relies on, over a real in-process host+client pair:
    /// a client submits bytes to the server, the server rebroadcasts to everyone tagged with the true origin,
    /// and both peers receive the identical payload. (The full channel component can't run twice in one scene —
    /// it drives world singletons — so the RPC shape is verified here and the world logic in WorldSyncTests.)
    /// </summary>
    public class RelayPatternTests : NetcodeIntegrationTest
    {
        protected override int NumberOfClients => 1;

        GameObject _prefab;

        protected override void OnServerAndClientsCreated()
        {
            _prefab = CreateNetworkObjectPrefab("EchoChan");
            _prefab.AddComponent<EchoChannel>();
        }

        [UnityTest]
        public IEnumerator ClientSubmit_RelaysToEveryPeer_WithTrueOrigin()
        {
            EchoChannel.Received.Clear();
            var serverInstance = SpawnObject(_prefab, m_ServerNetworkManager);
            var serverNo = serverInstance.GetComponent<NetworkObject>();
            yield return WaitForSpawnedOnAllOrTimeOut(serverNo);
            AssertOnTimeout("echo channel never spawned on the client");

            var clientNm = m_ClientNetworkManagers[0];
            var clientInstance = clientNm.SpawnManager.SpawnedObjects[serverNo.NetworkObjectId].GetComponent<EchoChannel>();
            byte[] payload = { 42, 7, 255, 0, 13 };
            clientInstance.Submit(payload);

            yield return WaitForConditionOrTimeOut(() => EchoChannel.Received.Count >= 2);
            AssertOnTimeout("broadcast did not reach both peers");

            var seenOn = new HashSet<ulong>();
            foreach (var (origin, data, localClient) in EchoChannel.Received)
            {
                Assert.AreEqual(clientNm.LocalClientId, origin, "origin must be the submitting client");
                CollectionAssert.AreEqual(payload, data, "payload must survive the relay untouched");
                seenOn.Add(localClient);
            }
            Assert.IsTrue(seenOn.Contains(m_ServerNetworkManager.LocalClientId), "host peer should receive the broadcast");
            Assert.IsTrue(seenOn.Contains(clientNm.LocalClientId), "origin peer should receive its own echo (and skip it by origin check)");
        }
    }

    public class EchoChannel : NetworkBehaviour
    {
        public static readonly List<(ulong origin, byte[] data, ulong localClient)> Received =
            new List<(ulong, byte[], ulong)>();

        public void Submit(byte[] data) => SubmitEventRpc(data);

        [Rpc(SendTo.Server)]
        void SubmitEventRpc(byte[] data, RpcParams rpcParams = default)
        {
            BroadcastEventRpc(data, rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Everyone)]
        void BroadcastEventRpc(byte[] data, ulong origin)
        {
            Received.Add((origin, data, NetworkManager.LocalClientId));
        }
    }
}
