using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.Net
{
    /// <summary>
    /// Turns a plain InputField into a join-code field: six slots shown as "--- ---" that fill in as you type,
    /// with anything that is not a letter or a digit refused at the keystroke and letters put into upper case.
    ///
    /// The InputField still owns the value — this only changes what you see. The field writes the bare
    /// characters into its own text object ("8KTQZ4"), so the grouping and the dashes are drawn by a second Text
    /// sitting on top of it and the field's own is made invisible. Doing it the other way round (keeping the mask
    /// in the field's text) breaks backspace: the last character of what is shown is usually a dash, so deleting
    /// it would change nothing. The caret goes for the same reason — it lines up with the ungrouped string and
    /// would stand in the wrong place. The dashes say where the next character lands.
    ///
    /// Left to itself it builds its display from the field's own text style, so a restyled field stays restyled.
    /// </summary>
    [RequireComponent(typeof(InputField))]
    public sealed class JoinCodeField : MonoBehaviour
    {
        [Tooltip("Draws the grouped code. Left empty, one is made at startup from the field's own text style.")]
        public Text display;

        InputField _field;
        string _shown;

        void Awake()
        {
            _field = GetComponent<InputField>();

            if (display == null) display = BuildDisplay();

            // Custom content type with no validation rule of its own: with any CharacterValidation other than
            // None the InputField never calls onValidateInput, and EnforceContentType will put one back from the
            // content type if that is left saying otherwise. SingleLine keeps Enter as submit.
            _field.contentType = InputField.ContentType.Custom;
            _field.lineType = InputField.LineType.SingleLine;
            _field.characterValidation = InputField.CharacterValidation.None;
            _field.characterLimit = JoinCodeFormat.Length;
            _field.onValidateInput = Accept;
            _field.onValueChanged.AddListener(Render);

            // Hide the field's own rendering, not the object: InputField needs its text component present and
            // enabled or it stops tracking what you type at all.
            if (_field.textComponent != null) _field.textComponent.color = Color.clear;
            if (_field.placeholder != null) _field.placeholder.gameObject.SetActive(false);
            _field.customCaretColor = true;
            _field.caretColor = Color.clear;
            _field.selectionColor = Color.clear;

            _shown = null;
            Render(_field.text);
        }

        /// <summary>Only code characters get in, and they get in upper case. '\0' is InputField's "refuse this".</summary>
        char Accept(string text, int charIndex, char c) =>
            JoinCodeFormat.IsCodeChar(c) ? char.ToUpperInvariant(c) : '\0';

        void Render(string value)
        {
            string masked = JoinCodeFormat.Mask(value);
            if (masked == _shown) return;
            _shown = masked;
            if (display != null) display.text = masked;
        }

        /// <summary>
        /// A Text over the field's own, matching it: whatever the scene did to the field's font, size and colour
        /// is what the code should look like.
        /// </summary>
        Text BuildDisplay()
        {
            var src = _field.textComponent;
            var go = new GameObject("CodeDisplay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(src != null ? src.rectTransform.parent : transform, false);

            var t = go.GetComponent<Text>();
            if (src != null)
            {
                var srt = src.rectTransform;
                rt.anchorMin = srt.anchorMin;
                rt.anchorMax = srt.anchorMax;
                rt.pivot = srt.pivot;
                rt.anchoredPosition = srt.anchoredPosition;
                rt.sizeDelta = srt.sizeDelta;
                rt.localScale = srt.localScale;

                t.font = src.font;
                t.fontSize = src.fontSize;
                t.fontStyle = src.fontStyle;
                t.color = src.color;
                t.alignment = src.alignment;
                t.lineSpacing = src.lineSpacing;
                t.resizeTextForBestFit = src.resizeTextForBestFit;
            }
            else
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                t.color = Color.black;
                t.alignment = TextAnchor.MiddleCenter;
            }
            t.supportRichText = false;
            t.raycastTarget = false;
            return t;
        }
    }
}
