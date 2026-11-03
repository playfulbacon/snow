using System;
using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.UI
{
    /// <summary>
    /// A labelled on/off row. Styled once in Assets/Prefabs/UI/MenuToggle.prefab.
    /// Same seeding rule as <see cref="MenuSlider"/>: the state goes in before the callback is live.
    /// </summary>
    public sealed class MenuToggle : MonoBehaviour
    {
        public Text label;
        public Toggle toggle;
        [Tooltip("Optional ON/OFF readout beside the box.")]
        public Text value;

        Action<bool> _onChanged;

        public void Bind(string text, bool current, Action<bool> onChanged)
        {
            if (label != null) label.text = text;
            _onChanged = null;
            if (toggle != null)
            {
                toggle.onValueChanged.RemoveListener(OnToggle);
                toggle.isOn = current;
                toggle.onValueChanged.AddListener(OnToggle);
            }
            Show(current);
            _onChanged = onChanged;
        }

        void OnToggle(bool on)
        {
            Show(on);
            _onChanged?.Invoke(on);
        }

        void Show(bool on)
        {
            if (value != null) value.text = on ? "ON" : "OFF";
        }

        void OnDestroy()
        {
            if (toggle != null) toggle.onValueChanged.RemoveListener(OnToggle);
        }
    }
}
