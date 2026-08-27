using System.Collections;
using NUnit.Framework;
using Snowfield.Config;
using Snowfield.Player;
using UnityEngine;
using UnityEngine.TestTools;

namespace Snowfield.Net.Tests
{
    /// <summary>
    /// The arm sync end to end, minus the RPC hop (RelayPatternTests covers the wire): build two real humanoid
    /// rigs, drive one the way SculptTool drives the local player's, sample it, and replay onto the other —
    /// then assert the second rig's hand bone actually went where the first one's was asked to go.
    /// </summary>
    public class HandSyncTests
    {
        const string ModelFbxPath = "Assets/CharacterTest/base_basic_shaded.fbx";
        const string ControllerPath = "Assets/Player/FirstPersonPlayer.controller";

        SculptFeelConfig _config;
        GameObject _ownerGo, _remoteGo;
        HandRig _ownerRig, _remoteRig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _config = ScriptableObject.CreateInstance<SculptFeelConfig>();
            _ownerGo = BuildRig("OwnerBody", new Vector3(10f, 0f, 10f), Quaternion.identity, out _ownerRig);
            // Deliberately a different pose: body-relative goals must land correctly on a body standing
            // somewhere else, facing somewhere else — that is the whole point of not sending world space.
            _remoteGo = BuildRig("RemoteBody", new Vector3(-4f, 0f, 25f), Quaternion.Euler(0f, 125f, 0f), out _remoteRig);
            yield return null; // Awake → Build resolves the humanoid bones

            if (_ownerRig == null || !_ownerRig.IsReady || !_remoteRig.IsReady)
                Assert.Ignore($"No humanoid rig available at {ModelFbxPath}; skipping arm sync test.");
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownerGo != null) Object.DestroyImmediate(_ownerGo);
            if (_remoteGo != null) Object.DestroyImmediate(_remoteGo);
            if (_config != null) Object.DestroyImmediate(_config);
        }

        GameObject BuildRig(string name, Vector3 position, Quaternion rotation, out HandRig rig)
        {
            rig = null;
#if UNITY_EDITOR
            var fbx = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ModelFbxPath);
            if (fbx == null) return null;

            var root = new GameObject(name);
            root.transform.SetPositionAndRotation(position, rotation);
            var model = Object.Instantiate(fbx, root.transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            var animator = model.GetComponent<Animator>() ?? model.AddComponent<Animator>();
            foreach (var sub in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(ModelFbxPath))
                if (sub is Avatar humanoid) { animator.avatar = humanoid; break; }
            // The rig solve assumes Mecanim rewrites the animated pose every frame — it restores bone
            // lengths but not rotations, so without a controller the aim deltas compound instead.
            animator.runtimeAnimatorController =
                UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            rig = root.AddComponent<HandRig>();
            rig.config = _config;
            rig.animator = animator;
            return root;
#else
            return null;
#endif
        }

        /// <summary>
        /// Drive for a stretch of real time, not a frame count: the rig's spring and the receiver's easing are
        /// both time-based, and test-runner frames are far shorter than a game's.
        /// </summary>
        static IEnumerator DriveFor(float seconds, System.Action perFrame)
        {
            float end = Time.time + seconds;
            while (Time.time < end) { perFrame(); yield return null; }
        }

        /// <summary>Mirror owner → remote every frame, the way NetAvatar does at 15 Hz.</summary>
        IEnumerator MirrorFor(float seconds, HandSyncReceiver receiver, HandSyncPose? fixedPose = null)
            => DriveFor(seconds, () =>
            {
                receiver.Receive(fixedPose ?? HandSyncPose.Sample(_ownerRig, _ownerGo.transform));
                receiver.Tick(_remoteRig, _remoteGo.transform, Time.deltaTime);
            });

        /// <summary>A goal the arm can plausibly get to: out in front, sized off this rig's own arm.</summary>
        Vector3 ReachableGoal(HandRig.Side side)
        {
            Vector3 shoulder = _ownerRig.ShoulderPosition(side);
            float arm = Vector3.Distance(shoulder, OwnerHand(side));
            Assert.Greater(arm, 1e-3f, "the rig reported no arm length; bones did not resolve");
            return shoulder + _ownerGo.transform.forward * (arm * 1.8f) + Vector3.up * (arm * 0.4f);
        }

        Vector3 OwnerHand(HandRig.Side side) => HandPosition(_ownerRig, side);
        Vector3 RemoteHand(HandRig.Side side) => HandPosition(_remoteRig, side);

        static Vector3 HandPosition(HandRig rig, HandRig.Side side)
        {
            var animator = rig.animator;
            var bone = animator.GetBoneTransform(side == HandRig.Side.Left
                ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            return bone.position;
        }

        [UnityTest]
        public IEnumerator Sampling_Reports_The_Goal_In_Body_Space()
        {
            Vector3 localGoal = new Vector3(0.35f, 0.9f, 0.7f);
            Vector3 localAim = new Vector3(0f, 0f, 1f);
            _ownerRig.Reach(HandRig.Side.Right, _ownerGo.transform.TransformPoint(localGoal), 1f,
                _ownerGo.transform.TransformDirection(localAim));
            yield return null; // LateUpdate resolves the request

            var pose = HandSyncPose.Sample(_ownerRig, _ownerGo.transform);
            Assert.IsTrue(pose.RightActive, "a reached hand should sample as active");
            Assert.IsFalse(pose.LeftActive, "the untouched hand should sample as idle");
            Assert.Less((pose.RightPosition - localGoal).magnitude, 1e-3f, "goal was not stored in body space");
            Assert.Less((pose.RightAim - localAim).magnitude, 1e-3f, "aim was not stored in body space");
            Assert.AreEqual(1f, pose.RightWeight, 1e-3f);
        }

        [UnityTest]
        public IEnumerator Sampling_Resolves_A_Pulse_With_No_Event_Of_Its_Own()
        {
            Vector3 localGoal = new Vector3(-0.3f, 1.0f, 0.6f);
            // Pulse only — exactly what a fuse/throw/place does: one call, then nothing.
            _ownerRig.Pulse(HandRig.Side.Left, _ownerGo.transform.TransformPoint(localGoal), 0.35f, Vector3.forward);

            for (int i = 0; i < 3; i++)
            {
                yield return null;
                var held = HandSyncPose.Sample(_ownerRig, _ownerGo.transform);
                Assert.IsTrue(held.LeftActive, "a pulsed hand must keep sampling active while the pulse holds");
                Assert.Less((held.LeftPosition - localGoal).magnitude, 1e-3f,
                    "the pulse position should be what goes on the wire, with no pulse-specific message");
            }

            yield return new WaitForSeconds(0.45f);
            yield return null;
            Assert.IsFalse(HandSyncPose.Sample(_ownerRig, _ownerGo.transform).LeftActive,
                "once the pulse lapses the hand should sample idle again");
        }

        [UnityTest]
        public IEnumerator Remote_Hand_Follows_The_Owners_Hand()
        {
            Vector3 worldGoal = ReachableGoal(HandRig.Side.Right);
            Vector3 localGoal = _ownerGo.transform.InverseTransformPoint(worldGoal);
            var receiver = new HandSyncReceiver();
            Vector3 restLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));

            yield return DriveFor(1.5f, () =>
            {
                _ownerRig.Reach(HandRig.Side.Right, worldGoal, 1f, worldGoal - _ownerGo.transform.position);
                receiver.Receive(HandSyncPose.Sample(_ownerRig, _ownerGo.transform));
                receiver.Tick(_remoteRig, _remoteGo.transform, Time.deltaTime);
            });

            Vector3 ownerLocal = _ownerGo.transform.InverseTransformPoint(OwnerHand(HandRig.Side.Right));
            Vector3 remoteLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));

            // The two bodies stand in different places facing different ways: agreement can only come from the
            // goal travelling in body space.
            Assert.Less((ownerLocal - remoteLocal).magnitude, 0.05f,
                $"remote hand sits {(ownerLocal - remoteLocal).magnitude:0.000} m from the owner's, body-relative");

            // ...and both actually went somewhere, so the agreement is not two idle arms matching at rest.
            Assert.Less((remoteLocal - localGoal).magnitude, (restLocal - localGoal).magnitude * 0.5f,
                "the remote arm did not travel most of the way to the goal");
        }

        [UnityTest]
        public IEnumerator Idle_Hands_Report_Inactive_And_Ease_Home()
        {
            var receiver = new HandSyncReceiver();
            Vector3 worldGoal = ReachableGoal(HandRig.Side.Right);
            Vector3 restLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));

            yield return DriveFor(1.5f, () =>
            {
                _ownerRig.Reach(HandRig.Side.Right, worldGoal, 1f);
                receiver.Receive(HandSyncPose.Sample(_ownerRig, _ownerGo.transform));
                receiver.Tick(_remoteRig, _remoteGo.transform, Time.deltaTime);
            });
            Vector3 reachedLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));
            float travelled = (reachedLocal - restLocal).magnitude;
            Assert.Greater(travelled, 1e-3f, "the remote arm never moved, so releasing proves nothing");

            // Owner stops asking. Its sampled pose must go inactive, and the remote arm must come home.
            yield return null;
            Assert.IsFalse(HandSyncPose.Sample(_ownerRig, _ownerGo.transform).RightActive,
                "a hand nobody asked for should sample as inactive");

            yield return MirrorFor(1.5f, receiver);
            Vector3 homeLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));
            Assert.Less((homeLocal - restLocal).magnitude, travelled * 0.5f,
                "the remote hand never released back toward the animated pose");
        }

        [UnityTest]
        public IEnumerator A_Silent_Owner_Releases_The_Remote_Hands()
        {
            var receiver = new HandSyncReceiver();
            Vector3 restLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));
            var pose = default(HandSyncPose);
            pose.RightPosition = _remoteGo.transform.InverseTransformPoint(ReachableGoal(HandRig.Side.Right));
            pose.RightWeight = 1f;

            yield return MirrorFor(1.5f, receiver, pose);
            Vector3 reachedLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));
            float travelled = (reachedLocal - restLocal).magnitude;
            Assert.Greater(travelled, 1e-3f, "the remote arm never moved, so releasing proves nothing");

            // Nothing more arrives — a dropped release packet, or the owner vanished. The hand must not stick.
            yield return DriveFor(1.5f, () => receiver.Tick(_remoteRig, _remoteGo.transform, Time.deltaTime));
            Vector3 homeLocal = _remoteGo.transform.InverseTransformPoint(RemoteHand(HandRig.Side.Right));
            Assert.Less((homeLocal - restLocal).magnitude, travelled * 0.5f,
                "a hand whose owner went quiet should return to the animation, not hang in the air");
        }
    }
}
