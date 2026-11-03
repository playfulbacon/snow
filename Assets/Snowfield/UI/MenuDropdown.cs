using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.UI
{
    /// <summary>
    /// A labelled pick-one row (resolution, quality). Styled once in Assets/Prefabs/UI/MenuDropdown.prefab.
    /// </summary>
    public sealed class MenuDropdown : MonoBehaviour
    {
        public Text label;
        public Dropdown dropdown;

        Action<int> _onChanged;

        public void Bind(string text, IList<string> options, int current, Action<int> onChanged)
        {
            if (label != null) label.text = text;
            _onChanged = null;
            if (dropdown != null)
            {
                dropdown.onValueChanged.RemoveListener(OnPicked);
                var list = new List<Dropdown.OptionData>(options.Count);
                foreach (var option in options) list.Add(new Dropdown.OptionData(option));
                dropdown.options = list;
                dropdown.value = Mathf.Clamp(current, 0, Mathf.Max(0, list.Count - 1));
                dropdown.RefreshShownValue();
                dropdown.onValueChanged.AddListener(OnPicked);
            }
            _onChanged = onChanged;
        }

        void OnPicked(int index) => _onChanged?.Invoke(index);

        void OnDestroy()
        {
            if (dropdown != null) dropdown.onValueChanged.RemoveListener(OnPicked);
        }
    }
}
