using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.Player
{
    /// <summary>
    /// The planar-shave preview: a line across the screen and a band over the snow that will be deleted.
    ///
    /// The line is drawn in screen space, and that is exact rather than convenient. The cut plane is built to
    /// contain the camera's forward direction, and the reticle is centred, so the camera itself lies on the plane
    /// — and a plane through the camera projects to a straight line through the centre of the screen. Rotating
    /// the line rotates the plane about the view axis, one to one.
    ///
    /// The band is the one honest approximation here. The cut removes a slab of fixed world thickness, and a slab
    /// does not project to a strip of constant width: it narrows with distance, and close to the camera it opens
    /// up without limit. So the band is drawn at the thickness the slab has *at the point you are aiming at*,
    /// which is the part of it you are judging. Snow much nearer or further than the cursor is cut slightly wider
    /// or narrower than the band suggests.
    ///
    /// Both halves are plain uGUI rects: move, recolour or replace them in the inspector without touching this.
    /// </summary>
    public sealed class PlaneCutPreview : MonoBehaviour
    {
        [Tooltip("Thin rect spanning the screen; rotated to the cut angle.")]
        public RectTransform line;
        [Tooltip("Covers the snow that will be removed.")]
        public RectTransform doomed;

        Canvas _canvas;

        void Reset()
        {
            if (line == null && transform.childCount > 0) line = transform.GetChild(0) as RectTransform;
        }

        /// <summary>Screen pixels → the canvas units RectTransform actually works in.</summary>
        float ToCanvas(float pixels)
        {
            if (_canvas == null) _canvas = GetComponentInParent<Canvas>();
            float scale = _canvas != null && _canvas.scaleFactor > 0.0001f ? _canvas.scaleFactor : 1f;
            return pixels / scale;
        }

        public void Hide()
        {
            if (line != null && line.gameObject.activeSelf) line.gameObject.SetActive(false);
            if (doomed != null && doomed.gameObject.activeSelf) doomed.gameObject.SetActive(false);
        }

        /// <summary>
        /// Point the preview at <paramref name="angleDeg"/>, measured from screen-right, turning anticlockwise,
        /// with a doomed band <paramref name="bandPixels"/> thick lying on the normal side of the line.
        /// </summary>
        public void Show(float angleDeg, float bandPixels)
        {
            // Long enough to run off screen at any rotation, so the line never stops short in a corner.
            float span = ToCanvas(new Vector2(Screen.width, Screen.height).magnitude);
            float band = Mathf.Max(ToCanvas(bandPixels), 1f);
            var rot = Quaternion.Euler(0f, 0f, angleDeg);

            if (line != null)
            {
                if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
                line.anchoredPosition = Vector2.zero;
                line.sizeDelta = new Vector2(span, Mathf.Max(line.sizeDelta.y, 2f));
                line.localRotation = rot;
            }

            if (doomed != null)
            {
                if (!doomed.gameObject.activeSelf) doomed.gameObject.SetActive(true);
                // Local X runs along the line, local Y across it, so the band is as long as the line and as thick
                // as the slab; pushed half its thickness along the normal puts its near edge on the line.
                doomed.sizeDelta = new Vector2(span, band);
                doomed.localRotation = rot;
                float n = (angleDeg + 90f) * Mathf.Deg2Rad;
                doomed.anchoredPosition = new Vector2(Mathf.Cos(n), Mathf.Sin(n)) * (band * 0.5f);
            }
        }
    }
}
