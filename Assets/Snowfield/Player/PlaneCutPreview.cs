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
    /// The length is the one honest approximation here. The cut spans a fixed distance in the world, and a fixed
    /// world length does not project to a fixed number of pixels — it shrinks with distance. So the segment is
    /// drawn at the length the cut has *at the point you are aiming at*, which is the part of it you are judging.
    /// Snow much nearer or further than the cursor is cut slightly longer or shorter than the segment suggests.
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
        /// as a segment <paramref name="lengthPixels"/> long. The doomed region hangs off the normal side of that
        /// segment and runs off screen, because the cut takes everything on that side within the segment's span.
        /// </summary>
        public void Show(float angleDeg, float lengthPixels)
        {
            // Far enough to leave the screen at any rotation, so the doomed side never stops short in a corner.
            float span = ToCanvas(new Vector2(Screen.width, Screen.height).magnitude);
            float length = Mathf.Max(ToCanvas(lengthPixels), 1f);
            var rot = Quaternion.Euler(0f, 0f, angleDeg);
            float n = (angleDeg + 90f) * Mathf.Deg2Rad;
            var normal = new Vector2(Mathf.Cos(n), Mathf.Sin(n));

            if (line != null)
            {
                if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
                line.anchoredPosition = Vector2.zero;
                line.sizeDelta = new Vector2(length, Mathf.Max(line.sizeDelta.y, 2f));
                line.localRotation = rot;
            }

            if (doomed != null)
            {
                if (!doomed.gameObject.activeSelf) doomed.gameObject.SetActive(true);
                // Local X runs along the line, local Y across it: as long as the segment, deep enough to leave
                // the screen, and pushed half that depth along the normal so its near edge sits on the line.
                doomed.sizeDelta = new Vector2(length, span);
                doomed.localRotation = rot;
                doomed.anchoredPosition = normal * (span * 0.5f);
            }
        }
    }
}
