using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Snowfield.Net.Tests
{
    /// <summary>
    /// The Shift+N binding, driven with synthetic input. Going online is opt-in, so the chord has to fire on
    /// exactly the intended press and stay quiet the rest of the time — Shift is also the run key, and N is a
    /// key you can hit while walking.
    /// </summary>
    public class ToggleChordTests : InputTestFixture
    {
        Keyboard _keyboard;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            _keyboard = InputSystem.AddDevice<Keyboard>();
        }

        [UnityTest]
        public IEnumerator Shift_And_N_Together_Fire_Once_Per_Press()
        {
            Press(_keyboard.leftShiftKey);
            yield return null;
            Assert.IsFalse(NetBootstrap.ToggleChordPressed(_keyboard), "shift alone must not toggle networking");

            Press(_keyboard.nKey);
            yield return null;
            Assert.IsTrue(NetBootstrap.ToggleChordPressed(_keyboard), "shift+N should toggle networking");

            // Held, not re-pressed: one press is one toggle, or a lean on the key would flap the session.
            yield return null;
            Assert.IsFalse(NetBootstrap.ToggleChordPressed(_keyboard), "holding the chord must not keep firing");

            Release(_keyboard.nKey);
            yield return null;
            Press(_keyboard.nKey);
            yield return null;
            Assert.IsTrue(NetBootstrap.ToggleChordPressed(_keyboard), "a second press should toggle again");
        }

        [UnityTest]
        public IEnumerator The_Right_Shift_Works_Too()
        {
            Press(_keyboard.rightShiftKey);
            Press(_keyboard.nKey);
            yield return null;
            Assert.IsTrue(NetBootstrap.ToggleChordPressed(_keyboard));
        }

        [UnityTest]
        public IEnumerator N_Alone_Does_Nothing()
        {
            Press(_keyboard.nKey);
            yield return null;
            Assert.IsFalse(NetBootstrap.ToggleChordPressed(_keyboard),
                "N on its own must not go online — it is a bare letter key during normal play");
        }

        [UnityTest]
        public IEnumerator Running_While_Pressing_Other_Keys_Does_Nothing()
        {
            // Sprinting (shift) and sculpting: no stray toggles.
            Press(_keyboard.leftShiftKey);
            Press(_keyboard.wKey);
            Press(_keyboard.mKey);
            yield return null;
            Assert.IsFalse(NetBootstrap.ToggleChordPressed(_keyboard));
        }

        [Test]
        public void No_Keyboard_Is_Not_A_Press()
        {
            Assert.IsFalse(NetBootstrap.ToggleChordPressed(null));
        }
    }
}
