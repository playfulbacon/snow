using System.Collections.Generic;
using System.Linq;
using Snowfield.Net;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Builds the Start scene: a title, Host Game and Join Game, and a join panel with a code field. Structure
    /// and wiring only — no styling, no layout opinions beyond "it is on screen and you can click it". Everything
    /// is a plain uGUI object assigned to <see cref="StartMenu"/> by reference, so it can all be dragged around
    /// and restyled in the editor afterwards.
    ///
    /// <b>Create-only.</b> If Assets/Scenes/Start.unity already exists this does nothing, because re-running it
    /// would throw away whatever was done to the scene by hand. Delete the scene to regenerate it.
    /// </summary>
    public static class StartSceneSetup
    {
        const string ScenePath = "Assets/Scenes/Start.unity";
        const string GameScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("Snowfield/Create Start Scene")]
        public static void Run()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                Debug.Log($"[StartSceneSetup] {ScenePath} already exists; leaving it alone. Delete it to rebuild.");
                AddToBuildSettings();
                return;
            }
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // A menu still needs something to render it and something to read the mouse. This project is on the
            // new Input System, so the old StandaloneInputModule would silently never fire.
            var camGo = new GameObject("Camera", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.9f, 0.9f, 0.88f);
            cam.orthographic = true;
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var menu = canvasGo.AddComponent<StartMenu>();
            menu.gameScene = System.IO.Path.GetFileNameWithoutExtension(GameScenePath);

            // --- title ---
            Text(canvasGo.transform, "Title", "SNOWFIELD", 96, new Vector2(0f, 220f), new Vector2(1200f, 160f));

            // --- main panel: the two choices ---
            var main = Panel(canvasGo.transform, "MainPanel");
            menu.mainPanel = main;
            menu.hostButton = Button(main.transform, "HostButton", "HOST GAME", new Vector2(0f, 20f));
            menu.joinButton = Button(main.transform, "JoinButton", "JOIN GAME", new Vector2(0f, -70f));

            // --- join panel: a code and a way to use it ---
            var join = Panel(canvasGo.transform, "JoinPanel");
            menu.joinPanel = join;
            menu.codeField = Field(join.transform, "CodeField", "Enter a join code", new Vector2(0f, 20f));
            menu.playButton = Button(join.transform, "PlayButton", "PLAY", new Vector2(0f, -70f));
            menu.backButton = Button(join.transform, "BackButton", "BACK", new Vector2(0f, -160f));

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"[StartSceneSetup] Could not save {ScenePath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            AddToBuildSettings();
            Debug.Log($"[StartSceneSetup] Built {ScenePath}. Style it freely — this tool will not overwrite it.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Both scenes have to be in Build Settings or SceneManager.LoadScene cannot find the game scene at all,
        /// and Start has to be first so the build opens on the menu rather than dropping straight into the field.
        /// </summary>
        static void AddToBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            scenes.RemoveAll(s => s.path == ScenePath);
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            if (!scenes.Any(s => s.path == GameScenePath))
                scenes.Add(new EditorBuildSettingsScene(GameScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        // ---------------------------------------------------------------- bare uGUI
        // Centre-anchored and sized in pixels: the simplest thing that is visible and clickable. No layout
        // groups, because the point is that these can be moved by hand.

        static GameObject Panel(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Stretch(go.GetComponent<RectTransform>());
            return go;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static RectTransform Place(GameObject go, Transform parent, Vector2 pos, Vector2 size)
        {
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        static Text Text(Transform parent, string name, string content, int size, Vector2 pos, Vector2 box)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Place(go, parent, pos, box);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size;
            t.text = content;
            t.color = Color.black;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            return t;
        }

        static Button Button(Transform parent, string name, string label, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            Place(go, parent, pos, new Vector2(460f, 70f));
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.35f);
            Text(go.transform, "Label", label, 40, Vector2.zero, new Vector2(460f, 70f));
            return go.GetComponent<Button>();
        }

        static InputField Field(Transform parent, string name, string placeholder, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            var rt = Place(go, parent, pos, new Vector2(620f, 70f));
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.6f);

            // InputField needs a text object to write into and, optionally, one to show while empty. Both must be
            // children and both must be assigned, or it renders nothing at all as you type.
            var text = Text(go.transform, "Text", "", 36, Vector2.zero, rt.sizeDelta);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            var hint = Text(go.transform, "Placeholder", placeholder, 36, Vector2.zero, rt.sizeDelta);
            hint.alignment = TextAnchor.MiddleLeft;
            hint.fontStyle = FontStyle.Italic;
            hint.color = new Color(0f, 0f, 0f, 0.4f);

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = hint;
            field.characterLimit = 16;
            return field;
        }
    }
}
