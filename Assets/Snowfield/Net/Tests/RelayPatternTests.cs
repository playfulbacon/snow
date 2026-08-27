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
            _prefab.AddComponent<HandsEcho>();
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

        /// <summary>
        /// The hand stream's exact wire configuration: a client-OWNED object sending SendTo.NotMe with
        /// unreliable delivery and owner-only permission. NGO proxies that through the host by itself — this
        /// pins that behaviour down, because NetAvatar has no manual relay to fall back on.
        /// </summary>
        [UnityTest]
        public IEnumerator OwnerHandStream_ReachesOtherPeers_WithoutAManualRelay()
        {
            HandsEcho.Received.Clear();
            var clientNm = m_ClientNetworkManagers[0];
            var instance = SpawnObject(_prefab, clientNm); // owned by the client, as an avatar is
            var netObj = instance.GetComponent<NetworkObject>();
            yield return WaitForSpawnedOnAllOrTimeOut(netObj);
            AssertOnTimeout("client-owned object never spawned on the host");

            var owned = clientNm.SpawnManager.SpawnedObjects[netObj.NetworkObjectId].GetComponent<HandsEcho>();
            var pose = default(HandSyncPose);
            pose.RightPosition = new Vector3(0.25f, 1.05f, 0.6f);
            pose.RightAim = new Vector3(0f, 0f, 1f);
            pose.RightWeight = 1f;
            pose.LeftWeight = 0f;

            // Unreliable: on loopback nothing drops, but send a few anyway — the real stream is continuous.
            for (int i = 0; i < 5; i++) { owned.Send(pose); yield return null; }

            yield return WaitForConditionOrTimeOut(() => HandsEcho.Received.Count > 0);
            AssertOnTimeout("the owner's hand pose never reached the other peer");

            var (got, localClient) = HandsEcho.Received[0];
            Assert.AreEqual(m_ServerNetworkManager.LocalClientId, localClient,
                "the host is the peer that should have received it");
            Assert.AreEqual(pose.RightWeight, got.RightWeight, 1e-4f);
            Assert.Less((pose.RightPosition - got.RightPosition).magnitude, 1e-4f, "hand position was mangled in transit");
            Assert.Less((pose.RightAim - got.RightAim).magnitude, 1e-4f, "hand aim was mangled in transit");
            Assert.IsFalse(got.LeftActive, "an idle hand should arrive idle");
        }
    }

    public class HandsEcho : NetworkBehaviour
    {
        public static readonly List<(HandSyncPose pose, ulong localClient)> Received =
            new List<(HandSyncPose, ulong)>();

        public void Send(HandSyncPose pose) => HandsRpc(pose);

        [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        void HandsRpc(HandSyncPose pose)
        {
            Received.Add((pose, NetworkManager.LocalClientId));
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
