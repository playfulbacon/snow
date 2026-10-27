using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.Player
{
    /// <summary>
    /// The planar-shave preview: a line across the screen and a wash over the half that will be deleted.
    ///
    /// This is drawn in screen space rather than in the world, and that is exact rather than convenient. The cut
    /// plane is built to contain the camera's forward direction, and the reticle is centred, so the camera itself
    /// lies on the plane — and a plane through the camera projects to a straight line through the centre of the
    /// screen. Rotating the line by an angle rotates the plane about the view axis, one to one. What you see is
    /// the cut, not an impression of it.
    ///
    /// Both halves are plain uGUI rects: move, recolour or replace them in the inspector without touching this.
    /// </summary>
    public sealed class PlaneCutPreview : MonoBehaviour
    {
        [Tooltip("Thin rect spanning the screen; rotated to the cut angle.")]
        public RectTransform line;
        [Tooltip("Covers the half of the screen that will be removed.")]
        public RectTransform doomed;

        void Reset()
        {
            if (line == null && transform.childCount > 0) line = transform.GetChild(0) as RectTransform;
        }

        public void Hide()
        {
            if (line != null && line.gameObject.activeSelf) line.gameObject.SetActive(false);
            if (doomed != null && doomed.gameObject.activeSelf) doomed.gameObject.SetActive(false);
        }

        /// <summary>
        /// Point the preview at <paramref name="angleDeg"/>, measured from screen-right, turning anticlockwise.
        /// The doomed half is the one the plane normal points into, which in screen terms is 90 degrees ahead of
        /// the line.
        /// </summary>
        public void Show(float angleDeg)
        {
            // A square this big covers the screen at any rotation, so neither half ever falls short in a corner.
            float span = new Vector2(Screen.width, Screen.height).magnitude;

            if (line != null)
            {
                if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
                line.anchoredPosition = Vector2.zero;
                line.sizeDelta = new Vector2(span, Mathf.Max(line.sizeDelta.y, 2f));
                line.localRotation = Quaternion.Euler(0f, 0f, angleDeg);
            }

            if (doomed != null)
            {
                if (!doomed.gameObject.activeSelf) doomed.gameObject.SetActive(true);
                doomed.sizeDelta = new Vector2(span, span);
                doomed.localRotation = Quaternion.Euler(0f, 0f, angleDeg);
                // Pushed a half-span along the line's normal, so its near edge sits exactly on the line.
                float n = (angleDeg + 90f) * Mathf.Deg2Rad;
                doomed.anchoredPosition = new Vector2(Mathf.Cos(n), Mathf.Sin(n)) * (span * 0.5f);
            }
        }
    }
}
