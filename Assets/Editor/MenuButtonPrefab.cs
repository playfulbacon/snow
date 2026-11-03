using System.IO;
using Snowfield.Net;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Makes every button in the Start scene an instance of one prefab, so the menu has a single place to restyle.
    /// <see cref="StartSceneSetup"/> built four identical hand-made buttons; changing the look meant changing all
    /// four by hand and hoping they stayed in step.
    ///
    /// Creates Assets/Prefabs/UI/MenuButton.prefab the first time (the look it already had: a 460x70 white-at-35%
    /// panel with a black 40pt centred label) and never touches it again — editing that prefab is the point. Per
    /// instance only three things stay overridden: the object name, where it sits, and what the label says.
    ///
    /// Idempotent: a button that is already an instance of the prefab is left exactly as it is, overrides and all.
    /// Refuses to run if the open scene has unsaved changes, and puts that scene back when it is done.
    /// </summary>
    public static class MenuButtonPrefab
    {
        const string ScenePath = "Assets/Scenes/Start.unity";
        const string PrefabPath = "Assets/Prefabs/UI/MenuButton.prefab";

        [MenuItem("Snowfield/Make Start Menu Buttons Prefab Instances")]
        public static void Run()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[MenuButtonPrefab] Editor is in play mode; not touching scenes.");
                return;
            }

            // Opening Start.unity single-scene would discard whatever is unsaved in whatever is open now.
            var open = EditorSceneManager.GetActiveScene();
            if (open.isDirty)
            {
                Debug.LogError("[MenuButtonPrefab] The open scene has unsaved changes — save or discard them first.");
                return;
            }
            string returnTo = open.path;

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var menu = Object.FindAnyObjectByType<StartMenu>();
            if (menu == null)
            {
                Debug.LogError($"[MenuButtonPrefab] No StartMenu in {ScenePath}.");
                return;
            }

            // The prefab is built in this scene and saved out of it, so it is Start's own buttons that define the
            // look rather than a second opinion about what a button should be.
            var prefab = EnsurePrefab(menu);
            if (prefab == null) return;
            EnsurePrefabDrivesSize();

            int converted = 0;
            menu.hostButton = Convert(prefab, menu.hostButton, ref converted);
            menu.joinButton = Convert(prefab, menu.joinButton, ref converted);
            menu.playButton = Convert(prefab, menu.playButton, ref converted);
            menu.backButton = Convert(prefab, menu.backButton, ref converted);
            EditorUtility.SetDirty(menu);

            if (converted > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                {
                    Debug.LogError($"[MenuButtonPrefab] Could not save {ScenePath}.");
                    return;
                }
                AssetDatabase.SaveAssets();
            }
            Debug.Log($"[MenuButtonPrefab] {converted} button(s) converted; the menu's buttons all instance " +
                      $"{PrefabPath}. Edit that prefab to restyle the whole menu.");

            // Only back to a scene that is still there: the active scene can be a leftover path from a PlayMode
            // test run, and reopening that leaves the editor on nothing at all.
            if (!string.IsNullOrEmpty(returnTo) && returnTo != ScenePath && File.Exists(returnTo))
                EditorSceneManager.OpenScene(returnTo, OpenSceneMode.Single);
        }

        /// <summary>
        /// The prefab, copied off a button that is already in the scene so nothing about the look is re-decided
        /// here. The label stretches to the button's rect instead of carrying its own 460x70 — resizing the
        /// prefab should carry the text with it, which is the whole reason to have one.
        /// </summary>
        static GameObject EnsurePrefab(StartMenu menu)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null) return existing;

            var source = menu.hostButton;
            if (source == null)
            {
                Debug.LogError("[MenuButtonPrefab] StartMenu.hostButton is unassigned; nothing to copy the look from.");
                return null;
            }

            var go = Object.Instantiate(source.gameObject, source.transform.parent);
            go.name = "MenuButton";
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = Vector2.zero;

            var label = go.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = "BUTTON";
                var lrt = (RectTransform)label.transform;
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = Vector2.zero;
                lrt.offsetMax = Vector2.zero;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
            var saved = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            if (saved == null)
            {
                Debug.LogError($"[MenuButtonPrefab] Could not write {PrefabPath}.");
                return null;
            }
            AssetDatabase.ImportAsset(PrefabPath);
            Debug.Log($"[MenuButtonPrefab] Created {PrefabPath} from {source.name}.");
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        /// <summary>
        /// Proves the prefab's size actually reaches an instance that already carries a stale size override of its
        /// own, which is what every button in the scene looks like. Runs entirely in a preview scene, so the open
        /// scene is never touched and never left dirty; puts the prefab back exactly as it found it.
        /// </summary>
        [MenuItem("Snowfield/Check Start Menu Button Sizing")]
        public static void VerifySizePropagates()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[MenuButtonPrefab] No {PrefabPath} to check.");
                return;
            }

            var before = prefab.GetComponent<LayoutElement>();
            if (before == null)
            {
                Debug.LogError("[MenuButtonPrefab] The prefab has no LayoutElement; size is not prefab-driven.");
                return;
            }
            var was = new Vector2(before.preferredWidth, before.preferredHeight);
            var probe = new Vector2(was.x + 60f, was.y + 10f);

            try
            {
                SetPreferred(probe);
                var preview = EditorSceneManager.NewPreviewScene();
                try
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(
                        AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), preview);
                    var rt = (RectTransform)go.transform;
                    rt.sizeDelta = was;                       // the stale per-instance override the scene has
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                    bool ok = Mathf.Approximately(rt.rect.width, probe.x)
                              && Mathf.Approximately(rt.rect.height, probe.y);
                    Debug.Log($"[MenuButtonPrefab] Size check: prefab asked for {probe.x}x{probe.y}, an instance " +
                              $"overriding {was.x}x{was.y} ended up {rt.rect.width}x{rt.rect.height} — " +
                              (ok ? "PROPAGATES" : "DOES NOT PROPAGATE"));
                }
                finally { EditorSceneManager.ClosePreviewScene(preview); }
            }
            finally
            {
                SetPreferred(was);
                Debug.Log($"[MenuButtonPrefab] Prefab restored to {was.x}x{was.y}.");
            }
        }

        static void SetPreferred(Vector2 size)
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var element = root.GetComponent<LayoutElement>();
                element.preferredWidth = size.x;
                element.preferredHeight = size.y;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>
        /// Makes the button's size come from the prefab as well. Unity keeps a prefab instance root's
        /// RectTransform as a permanent per-instance override — position, anchors, pivot <i>and size</i> — so
        /// editing 460x70 in the prefab would otherwise reach none of the four buttons.
        ///
        /// A <see cref="LayoutElement"/> holding the preferred size, read back by a <see cref="ContentSizeFitter"/>
        /// on the same rect, drives it instead: both components live in the prefab, so the number is central, and
        /// the layout system writes it onto every instance. Deliberately <b>not</b> a layout group on the panels —
        /// that would take the buttons' positions over too, and <see cref="StartSceneSetup"/> keeps those hand-placed
        /// on purpose. Size is shared; where a button sits stays its own business.
        /// </summary>
        static void EnsurePrefabDrivesSize()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var rt = (RectTransform)root.transform;
                var size = rt.sizeDelta;

                var element = root.GetComponent<LayoutElement>();
                var fitter = root.GetComponent<ContentSizeFitter>();
                if (element != null && fitter != null) return;

                if (element == null) element = root.AddComponent<LayoutElement>();
                element.preferredWidth = size.x;
                element.preferredHeight = size.y;

                if (fitter == null) fitter = root.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"[MenuButtonPrefab] {PrefabPath} now drives its own size ({size.x}x{size.y}) through " +
                          "LayoutElement + ContentSizeFitter; change it there and every instance follows.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Swaps one hand-made button for an instance of the prefab, keeping its name, its place in the panel and
        /// its label — the three things that are genuinely per-button. Everything else now comes from the prefab.
        /// </summary>
        static Button Convert(GameObject prefab, Button existing, ref int converted)
        {
            if (existing == null) return null;
            var old = existing.gameObject;
            if (PrefabUtility.GetCorrespondingObjectFromSource(old) == prefab) return existing;

            var oldRt = (RectTransform)old.transform;
            var parent = oldRt.parent;
            int index = oldRt.GetSiblingIndex();
            var pos = oldRt.anchoredPosition;
            string name = old.name;
            var oldLabel = old.GetComponentInChildren<Text>(true);
            string text = oldLabel != null ? oldLabel.text : name;

            // Destroyed before instantiating so the sibling index we captured is still the index we can set.
            Object.DestroyImmediate(old);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos;
            rt.SetSiblingIndex(index);

            var label = go.GetComponentInChildren<Text>(true);
            if (label != null) label.text = text;

            converted++;
            return go.GetComponent<Button>();
        }
    }
}
