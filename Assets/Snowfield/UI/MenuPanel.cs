using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.UI
{
    /// <summary>
    /// The box a page of menu lives in: a background, a heading, and a column the rows go into. One prefab
    /// (Assets/Prefabs/UI/MenuPanel.prefab) styled once and used by every page of the pause menu — the pages
    /// differ in what is in the column, not in what the box looks like.
    ///
    /// The column is a <see cref="VerticalLayoutGroup"/> that positions its children but does not resize them
    /// (childControl* off), because the row prefabs already carry their own size through a LayoutElement +
    /// ContentSizeFitter the way <c>MenuButton.prefab</c> does. Spacing and padding live on that group, in the
    /// prefab, so the rhythm of the menu is editable without touching code.
    /// </summary>
    public sealed class MenuPanel : MonoBehaviour
    {
        [Tooltip("Heading at the top of the panel.")]
        public Text title;

        [Tooltip("Column the rows are instantiated into. Cleared on every page change.")]
        public RectTransform content;

        public void SetTitle(string text)
        {
            if (title != null) title.text = text;
        }

        /// <summary>
        /// Empty the column. Rows are built fresh per page rather than hidden and reused.
        /// Deactivated before being destroyed: a runtime Destroy only takes effect at the end of the frame, and
        /// a layout group counts a doomed child but not an inactive one — without this, the frame you change page
        /// on lays out the old page and the new one together and the menu visibly jumps.
        /// </summary>
        public void Clear()
        {
            if (content == null) return;
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var child = content.GetChild(i).gameObject;
                if (Application.isPlaying)
                {
                    child.SetActive(false);
                    child.transform.SetParent(null, false);
                    Object.Destroy(child);
                }
                else
                {
                    Object.DestroyImmediate(child);
                }
            }
        }

        /// <summary>Instantiate a row prefab into the column and hand back its component.</summary>
        public T Add<T>(GameObject prefab) where T : Component
        {
            if (prefab == null || content == null) return null;
            var go = Instantiate(prefab, content);
            go.transform.SetAsLastSibling();
            var component = go.GetComponent<T>();
            if (component == null) Debug.LogWarning($"[PauseMenu] {prefab.name} has no {typeof(T).Name} on it.");
            return component;
        }
    }
}
