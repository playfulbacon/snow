using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// The puff of snow an impact throws off. One place, called from everywhere snow arrives somewhere with
    /// force: a thrown ball landing, a ball striking a player, and every fuse (which is what "snow attached to
    /// the sculpture" is, so a hand-placed handful, a splat and a replayed remote attach all burst for free).
    ///
    /// Deliberately one world-space <see cref="ParticleSystem"/> that nothing emits on its own: every burst is a
    /// handful of explicit <see cref="ParticleSystem.Emit(ParticleSystem.EmitParams,int)"/> calls with the
    /// position, velocity and size worked out here. A pool of pre-authored emitters would mean a prefab to keep
    /// in step with the snow look, re-orienting a cone at every impact, and an emitter running out mid-flurry.
    /// Positions come from the contact point, so the spray is where the hit was rather than where the ball is.
    ///
    /// Self-bootstrapping like SnowfallSystem and SnowDeformSystem: nothing in a scene has to remember it.
    /// Silent outside play mode, so edit-mode tooling and tests never spawn it.
    /// </summary>
    public static class SnowBurst
    {
        /// <summary>Snow is white, but slightly blue in shadow reads as snow rather than as paper.</summary>
        static readonly Color Near = new Color(1f, 1f, 1f, 0.95f);
        static readonly Color Far = new Color(0.82f, 0.88f, 0.97f, 0.7f);

        const int MinParticles = 5;
        const int MaxParticles = 44;

        static ParticleSystem _system;
        static Material _material;

        /// <summary>
        /// Throw snow off a hit. <paramref name="normal"/> points out of the surface that was struck (the spray
        /// goes that way); <paramref name="radius"/> is the size of the snow involved, which sets how wide and
        /// how fat the puff is; <paramref name="strength"/> scales the whole thing — roughly impact speed in m/s
        /// over a walking pace, so a gentle placement is a wisp and a hard throw is a cloud.
        /// </summary>
        public static void Play(Vector3 point, Vector3 normal, float radius, float strength = 1f)
        {
            if (!Application.isPlaying) return;
            var ps = Ensure();
            if (ps == null) return;

            radius = Mathf.Clamp(radius, 0.03f, 2f);
            strength = Mathf.Clamp(strength, 0.25f, 4f);
            Vector3 up = normal.sqrMagnitude > 1e-6f ? normal.normalized : Vector3.up;

            // Area, not radius: twice as wide a ball throws four times the snow, which is what reads as weight.
            int count = Mathf.Clamp(Mathf.RoundToInt(radius * radius * 260f * strength) + MinParticles,
                                    MinParticles, MaxParticles);
            float speed = (0.9f + radius * 2.2f) * strength;

            for (int i = 0; i < count; i++)
            {
                // Away from the surface, but spread wide: a tight cone reads as a jet of water, not a splat.
                Vector3 dir = (up + Random.insideUnitSphere * 0.85f).normalized;
                var emit = new ParticleSystem.EmitParams
                {
                    position = point + Random.insideUnitSphere * (radius * 0.4f),
                    velocity = dir * (speed * Random.Range(0.35f, 1f)),
                    startSize = radius * Random.Range(0.14f, 0.4f),
                    startLifetime = Random.Range(0.3f, 0.75f),
                    startColor = Color.Lerp(Near, Far, Random.value),
                    applyShapeToPosition = false,
                };
                ps.Emit(emit, 1);
            }
        }

        /// <summary>The emitter, built on first use. Null means the shader is missing and there is no spray.</summary>
        static ParticleSystem Ensure()
        {
            if (_system != null) return _system;

            var shader = Resources.Load<Shader>("SnowBurst");
            if (shader == null) shader = Shader.Find("Snowfield/SnowBurst");
            if (shader == null)
            {
                Debug.LogWarning("[SnowBurst] SnowBurst shader not found; impacts will not spray.");
                return null;
            }
            _material = new Material(shader) { name = "SnowBurst (runtime)", hideFlags = HideFlags.DontSave };

            var go = new GameObject("SnowBurst") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(go);
            _system = go.AddComponent<ParticleSystem>();

            // Emission off: every particle in the game comes from an explicit Emit above.
            var main = _system.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 600;
            main.startSpeed = 0f;                 // the velocity is handed in per particle
            main.startSize = 0.1f;
            main.startLifetime = 0.6f;
            main.gravityModifier = 0.85f;         // snow is light, but it does come down
            main.startRotation3D = false;

            var emission = _system.emission;
            emission.enabled = false;

            var shape = _system.shape;
            shape.enabled = false;

            // Shrink and fade out, so nothing ever pops out of existence mid-air.
            var size = _system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, Curve(1f, 0.25f));

            var colour = _system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(FadeOut());

            var drag = _system.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.dampen = 0.25f;                  // snow does not fly far
            drag.limit = new ParticleSystem.MinMaxCurve(8f);

            var renderer = _system.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.alignment = ParticleSystemRenderSpace.View;

            _system.Play();                       // running but emitting nothing, ready for Emit
            return _system;
        }

        static AnimationCurve Curve(float from, float to) => AnimationCurve.EaseInOut(0f, from, 1f, to);

        static Gradient FadeOut()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),       // one frame of fade-in hides the spawn point
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(0f, 1f),
                });
            return g;
        }
    }
}
