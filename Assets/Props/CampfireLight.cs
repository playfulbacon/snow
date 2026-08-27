using UnityEngine;

namespace SnowDays
{
    // Campfire light: layered noise keeps the intensity constantly alive (slow swell + fast
    // crackle), color slides between ember-red and flame-yellow with brightness, and the
    // source point wanders a little so shadows dance like real firelight.
    [RequireComponent(typeof(Light))]
    public class CampfireLight : MonoBehaviour
    {
        [Header("Intensity")]
        // <0 = adopt the Light's authored intensity at Awake, so scene tweaks stay authoritative.
        [SerializeField] private float m_BaseIntensity = -1f;
        [SerializeField, Range(0f, 1f)] private float m_MinIntensityFactor = 0.3f;
        [SerializeField, Range(0f, 1f)] private float m_SwellAmount = 0.2f;
        [SerializeField] private float m_SwellSpeed = 0.7f;
        [SerializeField, Range(0f, 1f)] private float m_CrackleAmount = 0.25f;
        [SerializeField] private float m_CrackleSpeed = 8f;

        [Header("Color")]
        [SerializeField] private Color m_EmberColor = new Color(1f, 0.42f, 0.12f);
        [SerializeField] private Color m_FlameColor = new Color(1f, 0.7f, 0.3f);

        [Header("Movement")]
        // Metres the source wanders around its rest position (in local space).
        [SerializeField] private float m_PositionJitter = 0.1f;
        [SerializeField] private float m_JitterSpeed = 2.5f;

        [Header("Range")]
        // How much the light's range follows brightness, so the lit circle swells and shrinks.
        [SerializeField, Range(0f, 0.5f)] private float m_RangeSwell = 0.08f;

        private Light m_Light;
        private float m_Base;
        private float m_BaseRange;
        private Color m_BaseColor;
        private Vector3 m_RestPosition;
        private float m_Seed;

        private void Awake()
        {
            m_Light = GetComponent<Light>();
            m_Base = m_BaseIntensity >= 0f ? m_BaseIntensity : m_Light.intensity;
            m_BaseRange = m_Light.range;
            m_BaseColor = m_Light.color;
            m_RestPosition = transform.localPosition;
            // Per-instance seed so multiple fires never pulse in lockstep.
            m_Seed = Random.value * 1000f;
        }

        private void OnDisable()
        {
            if (m_Light != null)
            {
                m_Light.intensity = m_Base;
                m_Light.range = m_BaseRange;
                m_Light.color = m_BaseColor;
            }
            transform.localPosition = m_RestPosition;
        }

        private void Update()
        {
            float t = Time.time;

            float swell = (Mathf.PerlinNoise(m_Seed, t * m_SwellSpeed) * 2f - 1f) * m_SwellAmount;
            // Two octaves so the crackle has both flame licks and finer sputter.
            float crackle = (Mathf.PerlinNoise(m_Seed + 31.4f, t * m_CrackleSpeed) * 0.65f
                           + Mathf.PerlinNoise(m_Seed + 63.9f, t * m_CrackleSpeed * 2.7f) * 0.35f)
                          * 2f - 1f;
            crackle *= m_CrackleAmount;

            float factor = Mathf.Max(m_MinIntensityFactor, 1f + swell + crackle);
            float brightness = Mathf.InverseLerp(
                1f - m_SwellAmount - m_CrackleAmount,
                1f + m_SwellAmount + m_CrackleAmount,
                factor);

            m_Light.intensity = m_Base * factor;
            m_Light.color = Color.Lerp(m_EmberColor, m_FlameColor, brightness);
            m_Light.range = m_BaseRange * (1f + (brightness - 0.5f) * 2f * m_RangeSwell);

            float jx = Mathf.PerlinNoise(m_Seed + 5.2f, t * m_JitterSpeed) - 0.5f;
            float jy = Mathf.PerlinNoise(m_Seed + 9.8f, t * m_JitterSpeed) - 0.5f;
            float jz = Mathf.PerlinNoise(m_Seed + 14.1f, t * m_JitterSpeed) - 0.5f;
            transform.localPosition = m_RestPosition + new Vector3(jx, jy, jz) * (2f * m_PositionJitter);
        }
    }
}
