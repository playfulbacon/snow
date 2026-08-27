using UnityEngine;

namespace SnowDays
{
    // Streetlight hum-and-flicker: a slow Perlin wander keeps the lamp breathing, and a second
    // slow "instability" noise occasionally lets a fast shimmer through, like a tired ballast.
    [RequireComponent(typeof(Light))]
    public class StreetlightFlicker : MonoBehaviour
    {
        [Header("Base")]
        // <0 = adopt the Light's authored intensity at Awake, so prefab tweaks stay authoritative.
        [SerializeField] private float m_BaseIntensity = -1f;
        [SerializeField, Range(0f, 1f)] private float m_MinIntensityFactor = 0.35f;

        [Header("Wander")]
        [SerializeField, Range(0f, 1f)] private float m_WanderAmount = 0.07f;
        [SerializeField] private float m_WanderSpeed = 0.5f;

        [Header("Flicker")]
        [SerializeField, Range(0f, 1f)] private float m_FlickerAmount = 0.25f;
        [SerializeField] private float m_FlickerSpeed = 16f;
        // How often the fast shimmer gets through: 0 = never, 1 = constant buzz.
        [SerializeField, Range(0f, 1f)] private float m_Instability = 0.35f;
        [SerializeField] private float m_InstabilitySpeed = 0.15f;

        private Light m_Light;
        private float m_Base;
        private float m_Seed;

        private void Awake()
        {
            m_Light = GetComponent<Light>();
            m_Base = m_BaseIntensity >= 0f ? m_BaseIntensity : m_Light.intensity;
            // Per-instance seed so a row of lamps never pulses in lockstep.
            m_Seed = Random.value * 1000f;
        }

        private void OnDisable()
        {
            if (m_Light != null)
            {
                m_Light.intensity = m_Base;
            }
        }

        private void Update()
        {
            float t = Time.time;

            float wander = (Mathf.PerlinNoise(m_Seed, t * m_WanderSpeed) * 2f - 1f) * m_WanderAmount;

            // Gate ramps 0..1 as the instability noise crosses its threshold, so shimmer
            // fades in and out in episodes instead of switching on and off.
            float gateNoise = Mathf.PerlinNoise(m_Seed + 41.7f, t * m_InstabilitySpeed);
            float gate = Mathf.InverseLerp(1f - m_Instability, 1f, gateNoise);
            float shimmer = (Mathf.PerlinNoise(m_Seed + 17.3f, t * m_FlickerSpeed) * 2f - 1f) * m_FlickerAmount;

            float factor = Mathf.Max(m_MinIntensityFactor, 1f + wander + gate * shimmer);
            m_Light.intensity = m_Base * factor;
        }
    }
}
