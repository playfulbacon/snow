using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Snowfield.Player.Tests
{
    [DefaultExecutionOrder(-1000)]
    public sealed class HeldAccessoryTestMover : MonoBehaviour
    {
        public Vector3 Velocity;
        void Update() => transform.position += Velocity * Time.deltaTime;
    }

    [DefaultExecutionOrder(1000)]
    public sealed class HeldAccessoryFrameProbe : MonoBehaviour
    {
        public Action Capture;
        void LateUpdate() { var capture = Capture; Capture = null; capture?.Invoke(); }
    }

    public class HeldAccessoryIntegrationTests
    {
        [UnityTearDown]
        public IEnumerator UnloadMain()
        {
            var main = SceneManager.GetSceneByName("Main");
            if (main.IsValid() && main.isLoaded)
            {
                var empty = SceneManager.CreateScene("HeldAccessoryTestTeardown");
                SceneManager.SetActiveScene(empty);
                yield return SceneManager.UnloadSceneAsync(main);
            }
        }

        [UnityTest]
        public IEnumerator MainScene_HoldsTheSelectedCarrotInTheRightHand()
        {
            SceneManager.LoadScene("Main");
            yield return null;
            var tool = UnityEngine.Object.FindAnyObjectByType<SculptTool>();
            var held = tool.GetComponent<HeldAccessoryView>();
            var placer = tool.GetComponent<AccessoryPlacer>();
            Assert.IsTrue(tool.hands.IsReady, "The real character must supply its humanoid hand bones");
            placer.Inventory.Add("carrot", 2);
            tool.SetMode(ToolMode.Accessory);
            float settleUntil = Time.time + 1f;
            while (Time.time < settleUntil) yield return null;

            var probe = tool.gameObject.AddComponent<HeldAccessoryFrameProbe>();
            bool captured = false;
            Vector3 grip = default, wrist = default, palm = default, expected = default, viewport = default;
            Quaternion palmRotation = default, itemRotation = default;
            HandRig.Goal handGoal = default;
            probe.Capture = () =>
            {
                grip = held.Model.transform.TransformPoint(held.GripLocal);
                wrist = tool.hands.HandBone(HandRig.Side.Right).position;
                palm = tool.hands.PalmPosition(HandRig.Side.Right);
                palmRotation = tool.hands.PalmRotation(HandRig.Side.Right);
                itemRotation = held.Model.transform.rotation;
                expected = held.HoldPosition;
                viewport = tool.viewCamera.WorldToViewportPoint(grip);
                handGoal = tool.hands.CurrentGoal(HandRig.Side.Right);
                var animator = tool.hands.animator;
                var upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                var lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                Directory.CreateDirectory("Screenshots/Items");
                File.WriteAllText("Screenshots/Items/held-palm-pose.txt",
                    $"camera={tool.viewCamera.transform.position:F4}\nbody={tool.reachOrigin.position:F4}\n" +
                    $"wrist={wrist:F4}\npalm={palm:F4}\npalmRotation={palmRotation.eulerAngles:F3}\nitemRotation={itemRotation.eulerAngles:F3}\nexpected={expected:F4}\ngoal={handGoal.Position:F4} weight={handGoal.Weight:F4}\n" +
                    $"upper={upper.position:F4} lower={lower.position:F4}\n" +
                    $"span={Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, wrist):F4}\n" +
                    $"localHand={tool.hands.HandBone(HandRig.Side.Right).localPosition:F4}\ndt={Time.deltaTime:F6} timeScale={Time.timeScale}\n");
                Capture(tool.viewCamera);
                captured = true;
            };
            while (!captured) yield return null;
            Assert.AreEqual("carrot", held.ItemId);
            Assert.IsTrue(held.Model.activeInHierarchy);
            Assert.Less(Vector3.Distance(grip, palm), 0.002f, "The carrot grip must sit in the solved palm");
            Assert.Greater(Vector3.Distance(grip, wrist), 0.02f, "Holding must not attach the item to the wrist joint");
            var expectedItem = tool.viewCamera.transform.rotation * Quaternion.Euler(tool.config.accessoryHoldEuler);
            var expectedPalm = expectedItem * Quaternion.Euler(tool.config.accessoryPalmEuler);
            Assert.Less(Quaternion.Angle(itemRotation, expectedItem), .1f, "Improving the hand must not change item facing");
            Assert.Less(Quaternion.Angle(palmRotation, expectedPalm), 2f, "IK must constrain both fingertip aim and palm roll");
            Assert.Less(Vector3.Distance(handGoal.Position, expected), 0.002f, "Idle hand intent is the carry pose, not the reticle");
            Assert.Less(Vector3.Distance(grip, expected), 0.08f, "The real rig should be able to reach the authored hold pose");
            Assert.Greater(viewport.x, 0.5f);
            Assert.Less(viewport.x, 0.95f);
            Assert.Greater(viewport.y, 0.05f);
            Assert.Less(viewport.y, 0.65f);
            Assert.Greater(viewport.z, 0.1f);
            Assert.AreEqual(2, placer.Inventory.Count("carrot"));
            Assert.IsNull(GameObject.Find("AccessoryPreview"));
            var hud = UnityEngine.Object.FindAnyObjectByType<ToolHud>();
            Assert.IsNull(hud.transform.Find("SelectedItem"));

            // Keeping the spring in world space would drag the hand behind the camera while walking.
            var mover = tool.reachOrigin.gameObject.AddComponent<HeldAccessoryTestMover>();
            mover.Velocity = Vector3.ProjectOnPlane(tool.viewCamera.transform.forward, Vector3.up).normalized * 3f;
            float moveUntil = Time.time + 0.5f;
            while (Time.time < moveUntil) yield return null;
            mover.enabled = false;
            captured = false;
            probe.Capture = () =>
            {
                grip = held.Model.transform.TransformPoint(held.GripLocal);
                expected = held.HoldPosition;
                captured = true;
            };
            while (!captured) yield return null;
            Assert.Less(Vector3.Distance(grip, expected), 0.08f, "Walking must keep the held item in front of the player");
        }

        static void Capture(Camera camera)
        {
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            var oldTarget = camera.targetTexture;
            var oldActive = RenderTexture.active;
            var oldAmbient = RenderSettings.ambientLight;
            var oldMode = RenderSettings.ambientMode;
            try
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.5f);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Screenshots/Items");
                File.WriteAllBytes("Screenshots/Items/held-carrot.png", image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = oldTarget;
                RenderTexture.active = oldActive;
                RenderSettings.ambientLight = oldAmbient;
                RenderSettings.ambientMode = oldMode;
                UnityEngine.Object.DestroyImmediate(image);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
