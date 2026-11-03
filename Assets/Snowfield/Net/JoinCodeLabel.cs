using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.Net
{
    /// <summary>
    /// Shows the session's join code in the corner while there is one, so a host can read it out to whoever they
    /// want in their field. Hides itself offline.
    ///
    /// Shown in <see cref="JoinCodeFormat"/>'s two groups of three, the same shape the join field asks for it in —
    /// reading six characters out in one run is how a digit goes missing.
    ///
    /// Deliberately its own object and its own canvas rather than a line in ToolHud: ToolHud generates and wipes
    /// its entire canvas on every rebuild, so anything placed in there by hand does not survive. This one can be
    /// moved, resized and restyled in the inspector and it will stay put.
    /// </summary>
    public sealed class JoinCodeLabel : MonoBehaviour
    {
        public Text label;
        [Tooltip("{0} is the code, grouped for reading.")]
        public string format = "CODE  {0}";

        string _shown;

        void Reset() => label = GetComponentInChildren<Text>(true);

        void Update()
        {
            if (label == null) return;
            string code = NetBootstrap.JoinCode;
            if (code == _shown) return;
            _shown = code;
            label.gameObject.SetActive(!string.IsNullOrEmpty(code));
            if (!string.IsNullOrEmpty(code)) label.text = string.Format(format, JoinCodeFormat.Pretty(code));
        }
    }
}
