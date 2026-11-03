using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Snowfield.Net
{
    /// <summary>
    /// The Start scene's menu. Two panels and nothing else: the title with Host/Join, and a join panel with a
    /// code field, which shows the code as its two groups of three with the characters you have not typed yet as
    /// dashes (see <see cref="JoinCodeField"/>). It holds no layout of its own — every object is assigned in the
    /// inspector, so the scene can be rearranged and restyled freely without touching this script.
    ///
    /// Neither button connects. Both record the choice in <see cref="NetLaunch"/> and load the game scene, where
    /// the NetworkManager lives; see that class for why it has to work that way.
    /// </summary>
    public sealed class StartMenu : MonoBehaviour
    {
        [Header("Panels")]
        public GameObject mainPanel;
        public GameObject joinPanel;

        [Header("Main panel")]
        public Button hostButton;
        public Button joinButton;

        [Header("Join panel")]
        public InputField codeField;
        public Button playButton;
        public Button backButton;

        [Tooltip("Scene loaded once a choice is made. Must be in Build Settings.")]
        public string gameScene = "Main";

        void Awake()
        {
            // The game scene locks the cursor; coming back here it has to be usable again.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (hostButton != null) hostButton.onClick.AddListener(Host);
            if (joinButton != null) joinButton.onClick.AddListener(() => ShowJoin(true));
            if (backButton != null) backButton.onClick.AddListener(() => ShowJoin(false));
            if (playButton != null) playButton.onClick.AddListener(Join);
            if (codeField != null)
            {
                codeField.onSubmit.AddListener(_ => Join());
                // The field draws itself as "--- ---" filling in; see JoinCodeField. Added here rather than
                // left to the scene so a code field dragged in by hand still behaves like one.
                if (codeField.GetComponent<JoinCodeField>() == null)
                    codeField.gameObject.AddComponent<JoinCodeField>();
            }

            ShowJoin(false);
        }

        void Update()
        {
            // Half a code is not an error worth a message; the button simply is not ready yet. The field shows
            // how many characters are still missing, which is the only prompt this needs.
            if (playButton != null) playButton.interactable = Code.Length == JoinCodeFormat.Length;
        }

        /// <summary>What is in the field, as a code. One answer for the button and the join, so they agree.</summary>
        string Code => JoinCodeFormat.Normalize(codeField != null ? codeField.text : "");

        void ShowJoin(bool joining)
        {
            if (mainPanel != null) mainPanel.SetActive(!joining);
            if (joinPanel != null) joinPanel.SetActive(joining);
            if (joining && codeField != null)
            {
                codeField.text = "";
                codeField.Select();
                codeField.ActivateInputField();
            }
        }

        public void Host()
        {
            NetLaunch.RequestHost();
            SceneManager.LoadScene(gameScene);
        }

        public void Join()
        {
            // Enter submits the field too, so the whole-code rule lives here rather than only on the button.
            string code = Code;
            if (code.Length != JoinCodeFormat.Length) return;
            NetLaunch.RequestJoin(code);
            SceneManager.LoadScene(gameScene);
        }
    }
}
