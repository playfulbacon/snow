using Snowfield.Config;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Field
{
    /// <summary>
    /// Slow snowfall: periodically lifts trampled snow (negative heights — footprints, trenches, carves) back toward
    /// the fresh surface. Player-raised snow is never touched. Hold N to "let it snow" hard (debug).
    /// </summary>
    public class SnowfallCycle : MonoBehaviour
    {
        public SculptFeelConfig config;
        [Tooltip("Terrain to heal. Defaults to SnowTerrain.Instance.")]
        public SnowTerrain terrain;

        float _accumulator;

        void Update()
        {
            if (terrain == null) terrain = SnowTerrain.Instance;
            if (terrain == null || !terrain.IsCreated) return;
            if (config == null) config = terrain.Config;
            if (config == null) return;

            // Debug dump: hold N for a visible blizzard.
            var kb = Keyboard.current;
            if (kb != null && kb.nKey.isPressed)
            {
                if (kb.nKey.wasPressedThisFrame) Debug.Log("[Snowfield] Let it snow (hold N)");
                terrain.RecoverTowardFresh(config.letItSnowPerSecond * Time.deltaTime);
                _accumulator = 0f;
                return;
            }

            _accumulator += Time.deltaTime;
            if (_accumulator < config.snowfallTickSeconds) return;
            // depth cap refills over snowfallRecoverHours
            float perSecond = config.snowfallRecoverHours <= 0f
                ? 0f
                : config.terrainPathDepthCap / (config.snowfallRecoverHours * 3600f);
            if (perSecond <= 0f) { _accumulator = 0f; return; }
            terrain.RecoverTowardFresh(perSecond * _accumulator);
            _accumulator = 0f;
        }
    }
}
