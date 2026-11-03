using System;
using UnityEngine;
using UnityEngine.UI;

namespace Snowfield.UI
{
    /// <summary>
    /// The "are you sure?" box. One prefab (Assets/Prefabs/UI/ConfirmDialog.prefab), one instance, asked for by
    /// anything that is about to do something you cannot take back — quitting to the title, quitting to the
    /// desktop, leaving a shared field.
    ///
    /// Deliberately not a queue: a second <see cref="Ask"/> while one is open replaces the question, because two
    /// stacked confirmations is a bug, not a feature. The dim behind it is part of the prefab and eats clicks, so
    /// the menu underneath cannot be used while the question stands.
    /// </summary>
    public sealed class ConfirmDialog : MonoBehaviour
    {
        public Text title;
        public Text body;
        public Button confirmButton;
        public Button cancelButton;
        [Tooltip("Label on the confirm button — the question's own verb reads better than a bare 'OK'.")]
        public Text confirmLabel;
        public Text cancelLabel;

        Action _onConfirm;

        public bool IsOpen => gameObject.activeSelf;

        void Awake()
        {
            if (confirmButton != null) confirmButton.onClick.AddListener(Confirm);
            if (cancelButton != null) cancelButton.onClick.AddListener(Close);
            gameObject.SetActive(false);
        }

        public void Ask(string question, string detail, string confirmText, Action onConfirm)
        {
            _onConfirm = onConfirm;
            if (title != null) title.text = question;
            if (body != null) body.text = detail;
            if (confirmLabel != null) confirmLabel.text = confirmText;
            gameObject.SetActive(true);
        }

        public void Close()
        {
            _onConfirm = null;
            gameObject.SetActive(false);
        }

        void Confirm()
        {
            var action = _onConfirm;
            Close();                 // closed first: the action usually loads a scene out from under this object
            action?.Invoke();
        }
    }
}
