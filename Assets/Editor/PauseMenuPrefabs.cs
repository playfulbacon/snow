using System.IO;
using Snowfield.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Builds the pause menu's prefab kit under Assets/Prefabs/UI — one prefab per kind of row, one for the
    /// panel they sit in, one for the confirmation box, and one that assembles the lot into the menu itself.
    ///
    /// <b>Create-only, per prefab.</b> Anything already on disk is left exactly as it is, because restyling these
    /// by hand is the entire point of them being prefabs. Delete a prefab to get the authored default back. The
    /// look here is deliberately plain and matches the Start menu's button (a pale card, black labels): it is a
    /// starting point to paint over, not a design.
    ///
    /// Why prefabs for rows the menu builds at runtime: half of every page depends on the moment (in a session or
    /// not, what resolutions this screen has, is the mic live), so the pages are assembled in code — but what a
    /// slider or a toggle *looks like* is not a runtime question, and lives here.
    ///
    /// The temporaries are built in the open scene and destroyed again, so nothing in that scene changes — but
    /// Unity may flag it dirty for having been touched, and it says so when it does.
    /// </summary>
    public static class PauseMenuPrefabs
    {
        const string Folder = "Assets/Prefabs/UI";
        public const string ButtonPath = Folder + "/MenuButton.prefab";
        public const string RowPath = Folder + "/MenuRow.prefab";
        public const string SliderPath = Folder + "/MenuSlider.prefab";
        public const string TogglePath = Folder + "/MenuToggle.prefab";
        public const string DropdownPath = Folder + "/MenuDropdown.prefab";
        public const string PanelPath = Folder + "/MenuPanel.prefab";
        public const string DialogPath = Folder + "/ConfirmDialog.prefab";
        public const string MenuPath = Folder + "/PauseMenu.prefab";

        // One rhythm for every row: the panel is a column 700 wide, interactive rows are 56 tall, readouts 44.
        static readonly Vector2 ButtonSize = new Vector2(460f, 70f);
        static readonly Vector2 RowSize = new Vector2(700f, 56f);
        static readonly Vector2 ReadoutSize = new Vector2(700f, 44f);
        const float PanelWidth = 820f;

        static readonly Color Ink = new Color(0.08f, 0.1f, 0.14f);
        static readonly Color InkSoft = new Color(0.08f, 0.1f, 0.14f, 0.65f);
        static readonly Color Card = new Color(0.93f, 0.95f, 0.97f, 0.97f);
        static readonly Color Dim = new Color(0f, 0f, 0f, 0.55f);

        [MenuItem("Snowfield/Create Pause Menu Prefabs")]
        public static void Run()
        {
            if (EnsureAll() != null)
                Debug.Log("[PauseMenuPrefabs] Pause menu kit present in " + Folder
                          + ". Edit those prefabs to restyle the menu; run Snowfield ▸ Ensure Main Scene Sculpting "
                          + "to put it in the Game prefab.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Every prefab the menu needs, creating only what is missing. Returns the assembled PauseMenu prefab, or
        /// null if it could not be built.
        /// </summary>
        internal static GameObject EnsureAll()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[PauseMenuPrefabs] Editor is in play mode; not writing prefabs.");
                return null;
            }

            // Temporaries have to be built somewhere, and that is the open scene: nothing in it is changed, but
            // adding and removing objects is enough for Unity to flag it. Clearing that flag again is not public
            // API, so the honest thing is to say so — on the MainSceneSetup path the scene is saved anyway.
            var scene = EditorSceneManager.GetActiveScene();
            bool wasDirty = scene.isDirty;

            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();

            var res = Builtin();
            var temp = new GameObject("~PauseMenuPrefabBuild").transform;
            GameObject menu;
            try
            {
                var button = Ensure(ButtonPath, temp, t => BuildButton(res, t));
                Ensure(RowPath, temp, BuildRow);
                Ensure(SliderPath, temp, t => BuildSlider(res, t));
                Ensure(TogglePath, temp, t => BuildToggle(res, t));
                Ensure(DropdownPath, temp, t => BuildDropdown(res, t));
                Ensure(PanelPath, temp, BuildPanel);
                Ensure(DialogPath, temp, t => BuildDialog(t, button));
                menu = Ensure(MenuPath, temp, BuildMenu);
            }
            finally
            {
                Object.DestroyImmediate(temp.gameObject);
                if (!wasDirty && scene.IsValid() && scene.isDirty)
                    Debug.Log("[PauseMenuPrefabs] The open scene is flagged dirty by the temporaries this built "
                              + "and destroyed; nothing in it changed, so discarding is safe.");
            }
            return menu;
        }

        /// <summary>Load the prefab if it is there, otherwise build it once and save it.</summary>
        static GameObject Ensure(string path, Transform temp, System.Func<Transform, GameObject> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var go = build(temp);
            if (go == null) return null;
            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            if (saved == null)
            {
                Debug.LogError("[PauseMenuPrefabs] Could not write " + path);
                return null;
            }
            Debug.Log("[PauseMenuPrefabs] Created " + path);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        // ---------------------------------------------------------------- the prefabs

        /// <summary>
        /// The same button the Start menu uses, if it is not there already: a pale card with a big black label,
        /// sized from a LayoutElement so editing the prefab actually reaches every instance (see
        /// <see cref="MenuButtonPrefab"/> for why the RectTransform alone cannot do that).
        /// </summary>
        static GameObject BuildButton(DefaultControls.Resources res, Transform temp)
        {
            var go = DefaultControls.CreateButton(res);
            go.name = "MenuButton";
            go.transform.SetParent(temp, false);
            Size(go, ButtonSize);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.35f);

            var label = go.GetComponentInChildren<Text>(true);
            Style(label, 40, Ink, TextAnchor.MiddleCenter);
            label.text = "BUTTON";
            Stretch((RectTransform)label.transform);

            Sized(go, ButtonSize);
            return go;
        }

        /// <summary>A name and a value, read-only: the key list, the connection state, the join code.</summary>
        static GameObject BuildRow(Transform temp)
        {
            var go = Row("MenuRow", temp, ReadoutSize);
            var row = go.AddComponent<MenuRow>();
            row.label = Left(go.transform, "Label", 26);
            row.value = Right(go.transform, "Value", 26, 0.45f);
            row.value.color = InkSoft;
            return go;
        }

        static GameObject BuildSlider(DefaultControls.Resources res, Transform temp)
        {
            var go = Row("MenuSlider", temp, RowSize);
            var widget = go.AddComponent<MenuSlider>();
            widget.label = Left(go.transform, "Label", 26);

            var sliderGo = DefaultControls.CreateSlider(res);
            sliderGo.name = "Slider";
            sliderGo.transform.SetParent(go.transform, false);
            var srt = (RectTransform)sliderGo.transform;
            srt.anchorMin = new Vector2(0.5f, 0.5f);
            srt.anchorMax = new Vector2(0.84f, 0.5f);
            srt.pivot = new Vector2(0.5f, 0.5f);
            srt.anchoredPosition = Vector2.zero;
            srt.sizeDelta = new Vector2(0f, 20f);
            widget.slider = sliderGo.GetComponent<Slider>();

            widget.value = Right(go.transform, "Value", 24, 0.86f);
            return go;
        }

        static GameObject BuildToggle(DefaultControls.Resources res, Transform temp)
        {
            var go = Row("MenuToggle", temp, RowSize);
            var widget = go.AddComponent<MenuToggle>();
            widget.label = Left(go.transform, "Label", 26);

            var toggleGo = DefaultControls.CreateToggle(res);
            toggleGo.name = "Toggle";
            toggleGo.transform.SetParent(go.transform, false);

            // The stock toggle carries its own label and a 20x20 box pinned to its top-left corner. The row owns
            // the label, and the box should simply be the toggle's rect — so the prefab's size is the box's size.
            var own = toggleGo.transform.Find("Label");
            if (own != null) Object.DestroyImmediate(own.gameObject);
            var background = toggleGo.transform.Find("Background") as RectTransform;
            if (background != null)
            {
                Stretch(background);
                var check = background.Find("Checkmark") as RectTransform;
                if (check != null) Stretch(check);
            }
            var trt = (RectTransform)toggleGo.transform;
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.52f, 0.5f);
            trt.anchoredPosition = Vector2.zero;
            trt.sizeDelta = new Vector2(34f, 34f);
            widget.toggle = toggleGo.GetComponent<Toggle>();

            widget.value = Right(go.transform, "Value", 24, 0.62f);
            widget.value.alignment = TextAnchor.MiddleLeft;
            return go;
        }

        static GameObject BuildDropdown(DefaultControls.Resources res, Transform temp)
        {
            var go = Row("MenuDropdown", temp, RowSize);
            var widget = go.AddComponent<MenuDropdown>();
            widget.label = Left(go.transform, "Label", 26);

            var dropGo = DefaultControls.CreateDropdown(res);
            dropGo.name = "Dropdown";
            dropGo.transform.SetParent(go.transform, false);
            var drt = (RectTransform)dropGo.transform;
            drt.anchorMin = new Vector2(0.5f, 0.5f);
            drt.anchorMax = new Vector2(0.98f, 0.5f);
            drt.pivot = new Vector2(0.5f, 0.5f);
            drt.anchoredPosition = Vector2.zero;
            drt.sizeDelta = new Vector2(0f, 44f);
            foreach (var text in dropGo.GetComponentsInChildren<Text>(true))
                Style(text, 24, Ink, text.alignment);
            widget.dropdown = dropGo.GetComponent<Dropdown>();
            return go;
        }

        /// <summary>
        /// The card a page of menu sits in. Two nested vertical layouts: the outer one stacks the heading and the
        /// column and grows the card to fit (so a short page is a short card), the inner one is the column the
        /// rows land in. Neither resizes its children — every row prefab carries its own size, the way the Start
        /// menu's button does — so this file owns the rhythm and the row prefabs own their looks.
        /// </summary>
        static GameObject BuildPanel(Transform temp)
        {
            var go = new GameObject("MenuPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(temp, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(PanelWidth, 400f);
            go.GetComponent<Image>().color = Card;

            Column(go, padding: 28, spacing: 14);
            // Width is the prefab's to own, height is the column's. A LayoutElement outranks a layout group when
            // the fitter asks for a preferred size, so the height it reports has to stay -1 ("no opinion").
            Sized(go, new Vector2(PanelWidth, -1f));

            var panel = go.AddComponent<MenuPanel>();

            var titleGo = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            titleGo.transform.SetParent(go.transform, false);
            panel.title = titleGo.GetComponent<Text>();
            Style(panel.title, 48, Ink, TextAnchor.MiddleCenter);
            panel.title.text = "PAUSED";
            Sized(titleGo, new Vector2(PanelWidth - 56f, 74f));

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(go.transform, false);
            Column(contentGo, padding: 0, spacing: 12);
            Sized(contentGo, new Vector2(PanelWidth - 56f, -1f));
            panel.content = (RectTransform)contentGo.transform;

            return go;
        }

        /// <summary>The "are you sure?" box, with the menu's own button prefab for the two answers.</summary>
        static GameObject BuildDialog(Transform temp, GameObject buttonPrefab)
        {
            var go = new GameObject("ConfirmDialog", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(temp, false);
            Stretch((RectTransform)go.transform);
            var dim = go.GetComponent<Image>();
            dim.color = Dim;           // eats the clicks as well as the light: the menu behind is out of reach

            var box = new GameObject("Box", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            box.transform.SetParent(go.transform, false);
            var brt = (RectTransform)box.transform;
            brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
            brt.anchoredPosition = Vector2.zero;
            brt.sizeDelta = new Vector2(1040f, 360f);
            box.GetComponent<Image>().color = Card;

            var dialog = go.AddComponent<ConfirmDialog>();

            var titleGo = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            titleGo.transform.SetParent(box.transform, false);
            var trt = (RectTransform)titleGo.transform;
            trt.anchorMin = new Vector2(0f, 1f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(-64f, 56f);   // 32 px clear of the box on each side
            trt.anchoredPosition = new Vector2(0f, -28f);
            dialog.title = titleGo.GetComponent<Text>();
            Style(dialog.title, 36, Ink, TextAnchor.UpperCenter);
            dialog.title.text = "Are you sure?";

            var bodyGo = new GameObject("Body", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            bodyGo.transform.SetParent(box.transform, false);
            var yrt = (RectTransform)bodyGo.transform;
            yrt.anchorMin = new Vector2(0f, 0f);
            yrt.anchorMax = new Vector2(1f, 1f);
            yrt.offsetMin = new Vector2(48f, 120f);
            yrt.offsetMax = new Vector2(-48f, -96f);
            dialog.body = bodyGo.GetComponent<Text>();
            Style(dialog.body, 26, InkSoft, TextAnchor.UpperCenter);
            dialog.body.horizontalOverflow = HorizontalWrapMode.Wrap;
            dialog.body.text = "";

            dialog.confirmButton = DialogButton(buttonPrefab, box.transform, "Confirm", "YES",
                                               new Vector2(-250f, -118f), out var confirmLabel);
            dialog.confirmLabel = confirmLabel;
            dialog.cancelButton = DialogButton(buttonPrefab, box.transform, "Cancel", "CANCEL",
                                               new Vector2(250f, -118f), out var cancelLabel);
            dialog.cancelLabel = cancelLabel;
            return go;
        }

        static Button DialogButton(GameObject prefab, Transform parent, string name, string label, Vector2 pos,
                                   out Text text)
        {
            text = null;
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = name;
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            text = go.GetComponentInChildren<Text>(true);
            if (text != null) text.text = label;
            return go.GetComponent<Button>();
        }

        /// <summary>
        /// The menu itself: its own overlay canvas (above the HUD, which sits at 0), a dim, the panel and the
        /// dialog. Everything that is only on screen while paused hangs off "Root", which is what the component
        /// switches — so the canvas, and therefore this component, stays alive to watch for Esc.
        /// </summary>
        static GameObject BuildMenu(Transform temp)
        {
            var go = new GameObject("PauseMenu", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                                    typeof(GraphicRaycaster));
            go.transform.SetParent(temp, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var menu = go.AddComponent<PauseMenu>();

            var root = new GameObject("Root", typeof(RectTransform));
            root.transform.SetParent(go.transform, false);
            Stretch((RectTransform)root.transform);
            menu.root = root;

            var dimGo = new GameObject("Dim", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dimGo.transform.SetParent(root.transform, false);
            Stretch((RectTransform)dimGo.transform);
            dimGo.GetComponent<Image>().color = Dim;

            var panelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PanelPath);
            if (panelPrefab != null)
            {
                var panel = (GameObject)PrefabUtility.InstantiatePrefab(panelPrefab);
                panel.transform.SetParent(root.transform, false);
                menu.panel = panel.GetComponent<MenuPanel>();
            }

            // Last child of Root, so the question draws over the page it was asked from.
            var dialogPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DialogPath);
            if (dialogPrefab != null)
            {
                var dialog = (GameObject)PrefabUtility.InstantiatePrefab(dialogPrefab);
                dialog.transform.SetParent(root.transform, false);
                Stretch((RectTransform)dialog.transform);
                menu.dialog = dialog.GetComponent<ConfirmDialog>();
            }

            menu.buttonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ButtonPath);
            menu.sliderPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SliderPath);
            menu.togglePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TogglePath);
            menu.dropdownPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DropdownPath);
            menu.rowPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RowPath);
            return go;
        }

        // ---------------------------------------------------------------- bits and pieces

        static DefaultControls.Resources Builtin() => new DefaultControls.Resources
        {
            standard = Skin("UI/Skin/UISprite.psd"),
            background = Skin("UI/Skin/Background.psd"),
            inputField = Skin("UI/Skin/InputFieldBackground.psd"),
            knob = Skin("UI/Skin/Knob.psd"),
            checkmark = Skin("UI/Skin/Checkmark.psd"),
            dropdown = Skin("UI/Skin/DropdownArrow.psd"),
            mask = Skin("UI/Skin/UIMask.psd"),
        };

        static Sprite Skin(string path) => AssetDatabase.GetBuiltinExtraResource<Sprite>(path);

        /// <summary>An empty row of the column's width, sized the way every other row is sized.</summary>
        static GameObject Row(string name, Transform temp, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(temp, false);
            Size(go, size);
            Sized(go, size);
            return go;
        }

        static Text Left(Transform parent, string name, int fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(4f, 0f);
            rt.offsetMax = new Vector2(-8f, 0f);
            var text = go.GetComponent<Text>();
            Style(text, fontSize, Ink, TextAnchor.MiddleLeft);
            text.text = name.ToUpperInvariant();
            return text;
        }

        static Text Right(Transform parent, string name, int fontSize, float anchorX)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(anchorX, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(8f, 0f);
            rt.offsetMax = new Vector2(-4f, 0f);
            var text = go.GetComponent<Text>();
            Style(text, fontSize, Ink, TextAnchor.MiddleRight);
            text.text = "";
            return text;
        }

        static void Style(Text text, int fontSize, Color color, TextAnchor anchor)
        {
            if (text == null) return;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = anchor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.resizeTextForBestFit = false;
        }

        static void Column(GameObject go, float padding, float spacing)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)padding, (int)padding, (int)padding, (int)padding);
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            // Positions its children and leaves their sizes alone: every row carries its own.
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        static void Size(GameObject go, Vector2 size)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = size;
        }

        /// <summary>
        /// Makes the size come from the prefab rather than from each instance's RectTransform, which Unity keeps
        /// as a permanent per-instance override. Same trick, same reason, as <see cref="MenuButtonPrefab"/>.
        /// </summary>
        static void Sized(GameObject go, Vector2 size)
        {
            var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;
            var fitter = go.GetComponent<ContentSizeFitter>() ?? go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = size.x >= 0f ? ContentSizeFitter.FitMode.PreferredSize
                                                : ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
