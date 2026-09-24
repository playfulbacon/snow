using System.Collections.Generic;
using NUnit.Framework;

namespace Snowfield.Net.Tests
{
    /// <summary>
    /// Keeps <see cref="NetAvatar.SyncedParameters"/> honest against the animator controller the avatars
    /// actually run. This is the test that was missing when LocomotionSpeed went unsynced: remote legs cycled
    /// at the authored clip rate while the body travelled at the real speed, the planted foot slid about
    /// 1.5 m/s, and SnowFootprints — which only stamps a foot moving under 1.1 m/s — never saw a footfall.
    /// Nothing looked broken; other players just left no tracks walking forwards.
    /// </summary>
    public class AvatarParameterTests
    {
        const string ControllerPath = "Assets/Player/FirstPersonPlayer.controller";

        /// <summary>Parameters that must NOT be mirrored as values, with the reason they are exempt.</summary>
        static readonly Dictionary<string, string> Exempt = new Dictionary<string, string>
        {
            { "Jump", "a one-shot trigger: it needs an event, not a mirrored value (see NETWORKING.md gaps)" },
        };

        [Test]
        public void Every_Controller_Parameter_Is_Synced_Or_Explicitly_Exempt()
        {
#if UNITY_EDITOR
            var controller = UnityEditor.AssetDatabase
                .LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(ControllerPath);
            Assert.IsNotNull(controller, $"{ControllerPath} is missing; regenerate it with PlayerSceneSetup.");

            var synced = new HashSet<string>(NetAvatar.SyncedParameters);
            foreach (var parameter in controller.parameters)
            {
                Assert.IsTrue(synced.Contains(parameter.name) || Exempt.ContainsKey(parameter.name),
                    $"Animator parameter '{parameter.name}' is neither mirrored onto remote avatars nor listed " +
                    "as exempt. Add it to NetAvatar.SyncedParameters (and push/pull it), or record why it is " +
                    "exempt here — an unsynced parameter that drives the legs breaks remote footprints.");
            }
#else
            Assert.Ignore("Needs the AnimatorController asset, which is editor-only.");
#endif
        }

        [Test]
        public void Nothing_Synced_Has_Gone_Stale()
        {
#if UNITY_EDITOR
            var controller = UnityEditor.AssetDatabase
                .LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(ControllerPath);
            Assert.IsNotNull(controller);

            var declared = new HashSet<string>();
            foreach (var parameter in controller.parameters) declared.Add(parameter.name);
            foreach (var name in NetAvatar.SyncedParameters)
                Assert.IsTrue(declared.Contains(name),
                    $"NetAvatar syncs '{name}', which the controller no longer declares.");
            foreach (var name in Exempt.Keys)
                Assert.IsTrue(declared.Contains(name),
                    $"'{name}' is listed as exempt but the controller no longer declares it.");
#else
            Assert.Ignore("Needs the AnimatorController asset, which is editor-only.");
#endif
        }
    }
}
