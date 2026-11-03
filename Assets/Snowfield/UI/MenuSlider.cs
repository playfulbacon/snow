using System;
using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.UI
{
    /// <summary>
    /// A labelled slider row: name on the left, slider in the middle, the current value read out on the right.
    /// Styled once in Assets/Prefabs/UI/MenuSlider.prefab.
    ///
    /// <see cref="Bind"/> sets the slider's value before it subscribes, so showing a page never counts as the
    /// player changing a setting (the first <c>onValueChanged</c> would otherwise write the value straight back,
    /// which is harmless here but hides real bugs when the setter does more than store).
    /// </summary>
    public sealed class MenuSlider : MonoBehaviour
    {
        public Text label;
        public Slider slider;
        [Tooltip("Reads out the current value. Optional.")]
        public Text value;

        Action<float> _onChanged;
        Func<float, string> _format;

        public void Bind(string text, float min, float max, float current, Action<float> onChanged,
                         Func<float, string> format = null)
        {
            if (label != null) label.text = text;
            _format = format ?? (v => Mathf.RoundToInt(v * 100f) + "%");
            _onChanged = null;                       // suppress while we seed the control
            if (slider != null)
            {
                slider.onValueChanged.RemoveListener(OnSlider);
                slider.minValue = min;
                slider.maxValue = max;
                slider.value = Mathf.Clamp(current, min, max);
                slider.onValueChanged.AddListener(OnSlider);
            }
            Show(current);
            _onChanged = onChanged;
        }

        void OnSlider(float v)
        {
            Show(v);
            _onChanged?.Invoke(v);
        }

        void Show(float v)
        {
            if (value != null) value.text = _format != null ? _format(v) : v.ToString("0.00");
        }

        void OnDestroy()
        {
            if (slider != null) slider.onValueChanged.RemoveListener(OnSlider);
        }
    }
}
