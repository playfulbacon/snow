using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.UI
{
    /// <summary>
    /// A read-only line of two halves — a name and a value. The controls page is a column of these (key on the
    /// left, what it does on the right), and the multiplayer page uses it for the connection state and the join
    /// code. Styled once in Assets/Prefabs/UI/MenuRow.prefab.
    /// </summary>
    public sealed class MenuRow : MonoBehaviour
    {
        public Text label;
        public Text value;

        public void Bind(string name, string text)
        {
            if (label != null) label.text = name;
            if (value != null) value.text = text;
        }
    }
}
