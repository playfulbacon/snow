using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// What a draped strand looks like. One instance per accessory style, held by its
    /// <see cref="AccessoryCatalog.Entry"/>; the shared simulation numbers live in SculptFeelConfig instead,
    /// because those are what you tune in play mode.
    /// </summary>
    public sealed class DrapeStyle
    {
        public enum Form
        {
            /// <summary>Swept tube — a cord with bulbs threaded on it.</summary>
            Cord,
            /// <summary>Flat band that lies against the snow, with fringe at the loose ends.</summary>
            Ribbon,
        }

        public Form Shape = Form.Cord;

        /// <summary>Cord radius, or half-width of a ribbon (m).</summary>
        public float Radius = 0.012f;
        /// <summary>Length between anchors as a multiple of the straight line. 1 = taut, 1.25 = a lazy sag.</summary>
        public float Slack = 1.15f;
        /// <summary>
        /// How far (m) the strand will reach back to lie against the shape it is draped over. Wool clings; a light
        /// cord strung between two snowmen should hang in the air, so keep that small.
        /// </summary>
        public float SurfaceCling = 0f;
        public Color StrandColor = new Color(0.16f, 0.17f, 0.2f);
        [Range(0f, 1f)] public float StrandSmoothness = 0.3f;

        // ---- cord: bulbs ----
        /// <summary>Distance along the strand between bulbs (m). 0 = no bulbs.</summary>
        public float BulbSpacing = 0f;
        public float BulbRadius = 0.03f;
        /// <summary>Cycled in order along the strand.</summary>
        public Color[] BulbColors;

        // ---- ribbon: fringe ----
        /// <summary>Length of the fringe strands at each loose end (m). 0 = no fringe.</summary>
        public float FringeLength = 0f;
        public int FringeCount = 0;

        /// <summary>Radial segments around a cord. Cheap: snow hides a coarse tube.</summary>
        public int TubeSides = 6;
    }
}
