using NUnit.Framework;
using Snowfield.Config;
using UnityEngine;

namespace Snowfield.UI.Tests
{
    /// <summary>
    /// The pause menu's two statics, tested without a scene: the settings store and the pause gate. These are
    /// the parts that can go wrong silently — a clamp that lets a slider write 0.0001x look sensitivity, or a
    /// resumed game left on a timeScale of zero with no menu on screen to explain it.
    ///
    /// The real PlayerPrefs are used (there is no seam, and faking one for four keys is worse), so every value
    /// is captured before and put back afterwards: running the tests must not change how your editor plays.
    /// </summary>
    public class MenuSettingsTests
    {
        float _master, _music, _sfx, _voice, _look, _fov;
        bool _invert, _status;
        float _timeScale;

        [SetUp]
        public void CaptureSettings()
        {
            _master = GameSettings.MasterVolume;
            _music = GameSettings.MusicVolume;
            _sfx = GameSettings.SfxVolume;
            _voice = GameSettings.VoiceVolume;
            _look = GameSettings.LookSensitivity;
            _fov = GameSettings.FieldOfView;
            _invert = GameSettings.InvertLookY;
            _status = GameSettings.ShowHudStatus;
            _timeScale = Time.timeScale;
        }

        [TearDown]
        public void RestoreSettings()
        {
            GameSettings.MasterVolume = _master;
            GameSettings.MusicVolume = _music;
            GameSettings.SfxVolume = _sfx;
            GameSettings.VoiceVolume = _voice;
            GameSettings.LookSensitivity = _look;
            GameSettings.FieldOfView = _fov;
            GameSettings.InvertLookY = _invert;
            GameSettings.ShowHudStatus = _status;
            GameSettings.Save();
            Time.timeScale = _timeScale;
        }

        [Test]
        public void VolumesClampToUnitRange()
        {
            GameSettings.MasterVolume = 4f;
            Assert.AreEqual(1f, GameSettings.MasterVolume, 1e-5f);
            GameSettings.MasterVolume = -2f;
            Assert.AreEqual(0f, GameSettings.MasterVolume, 1e-5f);
        }

        [Test]
        public void MasterVolumeReachesTheListener()
        {
            GameSettings.MasterVolume = 0.25f;
            Assert.AreEqual(0.25f, AudioListener.volume, 1e-5f);
        }

        [Test]
        public void LookAndFovClampToTheirSliderRange()
        {
            GameSettings.LookSensitivity = 99f;
            Assert.AreEqual(GameSettings.LookMax, GameSettings.LookSensitivity, 1e-5f);
            GameSettings.LookSensitivity = 0f;
            Assert.AreEqual(GameSettings.LookMin, GameSettings.LookSensitivity, 1e-5f);

            GameSettings.FieldOfView = 5f;
            Assert.AreEqual(GameSettings.FovMin, GameSettings.FieldOfView, 1e-5f);
            GameSettings.FieldOfView = 500f;
            Assert.AreEqual(GameSettings.FovMax, GameSettings.FieldOfView, 1e-5f);
        }

        [Test]
        public void SavedSettingsSurviveAReload()
        {
            GameSettings.LookSensitivity = 1.75f;
            GameSettings.InvertLookY = true;
            GameSettings.Save();

            GameSettings.LookSensitivity = 1f;      // as if the next launch started from the defaults
            GameSettings.InvertLookY = false;
            GameSettings.Load();

            Assert.AreEqual(1.75f, GameSettings.LookSensitivity, 1e-4f);
            Assert.IsTrue(GameSettings.InvertLookY);
        }

        [Test]
        public void ChangedFiresOnceForARealChangeAndNeverForANoOp()
        {
            int fired = 0;
            System.Action handler = () => fired++;
            GameSettings.Changed += handler;
            try
            {
                GameSettings.MusicVolume = 0.5f;
                Assert.AreEqual(1, fired, "a changed value should raise Changed once");
                GameSettings.MusicVolume = 0.5f;
                Assert.AreEqual(1, fired, "writing the same value again is not a change");
            }
            finally
            {
                GameSettings.Changed -= handler;
            }
        }

        [Test]
        public void PausingStopsTheClockAndResumingStartsIt()
        {
            Time.timeScale = 1f;
            GamePause.Set(true);
            Assert.IsTrue(GamePause.IsPaused);
            Assert.AreEqual(0f, Time.timeScale, 1e-5f);

            GamePause.Set(false);
            Assert.IsFalse(GamePause.IsPaused);
            Assert.AreEqual(1f, Time.timeScale, 1e-5f);
        }

        [Test]
        public void PausingInASessionLeavesTheClockAlone()
        {
            // A shared field keeps running: the other players are still sculpting in it.
            Time.timeScale = 1f;
            GamePause.Set(true, freezeTime: false);
            Assert.IsTrue(GamePause.IsPaused);
            Assert.AreEqual(1f, Time.timeScale, 1e-5f, "a session must not be frozen locally");

            GamePause.Set(false);
            Assert.AreEqual(1f, Time.timeScale, 1e-5f);
        }

        [Test]
        public void ResumingDoesNotStompATimeScaleItNeverFroze()
        {
            Time.timeScale = 0.25f;           // something else owns the clock (a slow-motion moment, a test)
            GamePause.Set(true, freezeTime: false);
            GamePause.Set(false);
            Assert.AreEqual(0.25f, Time.timeScale, 1e-5f);
        }
    }
}
