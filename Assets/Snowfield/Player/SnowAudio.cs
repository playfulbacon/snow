using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// The snow's voice, procedural for now (Phase 2 swaps in recorded layers behind the same calls): a short
    /// burst of shaped noise. Pitch rises with compaction, so pat, shave and squeeze all tell you how packed the
    /// snow is without a word on screen — powder is a low fwump, packed snow a bright crunch.
    /// Silent (and harmless) when the scene has no AudioListener.
    /// </summary>
    public static class SnowAudio
    {
        public enum Kind { Pat, Shave, Crunch, Crumble }

        static AudioClip _fwump, _crunch, _crumble;
        static AudioSource[] _pool;
        static int _next;
        static float _lastAt;
        static Kind _lastKind;

        /// <summary>Play one snow sound at a world position; <paramref name="pack"/> 0 = powder, 1 = fully packed.</summary>
        public static void Play(Kind kind, Vector3 at, float pack, float volume = 1f)
        {
            if (!Application.isPlaying) return;
            // Shaves land every few centimetres of travel; do not machine-gun them.
            float minGap = kind == Kind.Shave ? 0.06f : kind == Kind.Pat ? 0.12f : 0f;
            if (kind == _lastKind && Time.unscaledTime - _lastAt < minGap) return;
            _lastAt = Time.unscaledTime; _lastKind = kind;

            EnsurePool();
            var src = _pool[_next]; _next = (_next + 1) % _pool.Length;
            if (src == null) { _pool = null; return; }
            src.transform.position = at;
            src.clip = kind == Kind.Crunch ? _crunch : kind == Kind.Crumble ? _crumble : _fwump;
            src.pitch = kind switch
            {
                Kind.Crunch => Mathf.Lerp(0.85f, 1.5f, pack),
                Kind.Crumble => 0.7f,
                _ => Mathf.Lerp(0.65f, 1.35f, pack),
            } * Random.Range(0.96f, 1.04f);
            src.volume = Mathf.Clamp01(volume) * (kind == Kind.Shave ? 0.45f : 0.7f);
            src.Play();
        }

        static void EnsurePool()
        {
            if (_pool != null && _pool.Length > 0 && _pool[0] != null) return;
            _fwump = Shaped("SnowFwump", 0.22f, decay: 18f, lowpass: 0.08f);
            _crunch = Shaped("SnowCrunch", 0.12f, decay: 40f, lowpass: 0.45f);
            _crumble = Shaped("SnowCrumble", 0.35f, decay: 9f, lowpass: 0.18f);
            var root = new GameObject("SnowAudio") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(root);
            _pool = new AudioSource[6];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject("voice" + i);
                go.transform.SetParent(root.transform, false);
                var src = go.AddComponent<AudioSource>();
                src.spatialBlend = 1f;
                src.minDistance = 1.5f;
                src.maxDistance = 25f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.playOnAwake = false;
                _pool[i] = src;
            }
        }

        /// <summary>White noise through a one-pole low-pass with an exponential decay envelope: a soft thud.</summary>
        static AudioClip Shaped(string name, float seconds, float decay, float lowpass)
        {
            const int rate = 22050;
            int n = Mathf.Max(1, Mathf.RoundToInt(seconds * rate));
            var data = new float[n];
            var rng = new System.Random(name.GetHashCode());
            float y = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                y += (white - y) * lowpass;
                float attack = Mathf.Min(1f, t * 400f);
                data[i] = y * attack * Mathf.Exp(-decay * t);
            }
            var clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
