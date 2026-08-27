using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Headless-ish ops for a live editor. Batchmode can't run while the editor holds the project lock, so this
    /// polls the project-root NetOps/ folder for request files and executes them, writing a .result file each time:
    ///   scene_setup.request      → MainSceneSetup.Run() (skipped if the open scene has unsaved changes)
    ///   refresh.request          → AssetDatabase.Refresh()
    ///   tests_editmode.request   → run EditMode tests; file content = optional ';'-separated assembly filter
    ///   tests_playmode.request   → run PlayMode tests; same filter rule
    ///   play_smoke.request       → enter play mode in the open scene for N seconds (file content, default 25)
    /// Results land in NetOps/&lt;op&gt;.result. Used by the overnight networking session; harmless to keep.
    /// </summary>
    [InitializeOnLoad]
    public static class SnowOps
    {
        static readonly string OpsDir = Path.Combine(Directory.GetCurrentDirectory(), "NetOps");
        static double _nextPoll;
        static bool _testsRunning;

        static SnowOps()
        {
            EditorApplication.update += Poll;
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new ResultWriter());
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 2.0;

            // Play-smoke timeout watcher (survives the play-mode domain reload via SessionState).
            if (EditorApplication.isPlaying && SessionState.GetBool("snowops_smoke", false)
                && EditorApplication.timeSinceStartup > SessionState.GetFloat("snowops_smoke_end", 0f))
            {
                SessionState.SetBool("snowops_smoke", false);
                WriteResult("play_smoke", "DONE: play smoke finished");
                EditorApplication.ExitPlaymode();
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!Directory.Exists(OpsDir)) return;

            Handle("refresh", _ => { AssetDatabase.Refresh(); WriteResult("refresh", "DONE"); });
            Handle("restart", _ => RestartEditor());
            Handle("scene_setup", _ => RunSceneSetup());
            Handle("tests_editmode", f => RunTests(TestMode.EditMode, f));
            Handle("tests_playmode", f => RunTests(TestMode.PlayMode, f));
            Handle("play_smoke", RunPlaySmoke);
            Handle("build", RunBuild);
        }

        static void Handle(string op, Action<string> action)
        {
            string path = Path.Combine(OpsDir, op + ".request");
            if (!File.Exists(path)) return;
            string content;
            try { content = File.ReadAllText(path).Trim(); File.Delete(path); }
            catch { return; } // mid-write; next poll gets it
            Debug.Log($"[SnowOps] {op} requested");
            try { action(content); }
            catch (Exception e) { WriteResult(op, "EXCEPTION: " + e); }
        }

        static void WriteResult(string op, string text)
        {
            Directory.CreateDirectory(OpsDir);
            File.WriteAllText(Path.Combine(OpsDir, op + ".result"), text + "\n");
        }

        static void RestartEditor()
        {
            // Save every dirty scene that has a real path (never Save-As prompts for untitled test scenes),
            // then relaunch the editor on this project — e.g. so Burst picks up freshly added packages.
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isDirty && !string.IsNullOrEmpty(scene.path))
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            }
            WriteResult("restart", "RESTARTING");
            EditorApplication.OpenProject(Directory.GetCurrentDirectory());
        }

        static void RunSceneSetup()
        {
            var active = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (active.isDirty)
            {
                WriteResult("scene_setup", "SKIPPED: open scene has unsaved changes — not touching it");
                return;
            }
            if (EditorApplication.isPlaying)
            {
                WriteResult("scene_setup", "SKIPPED: editor is in play mode");
                return;
            }
            MainSceneSetup.Run();
            WriteResult("scene_setup", "DONE: MainSceneSetup.Run completed");
        }

        static void RunTests(TestMode mode, string assemblyFilter)
        {
            if (_testsRunning)
            {
                WriteResult(mode == TestMode.EditMode ? "tests_editmode" : "tests_playmode", "SKIPPED: a test run is already active");
                return;
            }
            _testsRunning = true;
            SessionState.SetString("snowops_testmode", mode.ToString());
            var filter = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(assemblyFilter))
                filter.assemblyNames = assemblyFilter.Split(';');
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.Execute(new ExecutionSettings(filter));
        }

        static void RunPlaySmoke(string arg)
        {
            if (EditorApplication.isPlaying)
            {
                WriteResult("play_smoke", "SKIPPED: already playing");
                return;
            }
            float seconds = 25f;
            if (float.TryParse(arg, out float parsed) && parsed > 1f) seconds = parsed;
            SessionState.SetBool("snowops_smoke", true);
            SessionState.SetFloat("snowops_smoke_end", (float)(EditorApplication.timeSinceStartup + seconds));
            WriteResult("play_smoke", $"STARTED: playing for {seconds:0}s");
            EditorApplication.EnterPlaymode();
        }

        static void RunBuild(string arg)
        {
            if (EditorApplication.isPlaying) { WriteResult("build", "SKIPPED: in play mode"); return; }
            string outPath = Path.Combine(Directory.GetCurrentDirectory(),
                string.IsNullOrEmpty(arg) ? "Builds/SnowDev.app" : arg);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Main.unity" },
                locationPathName = outPath,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.Development,
            };
            var report = UnityEditor.BuildPipeline.BuildPlayer(options);
            WriteResult("build",
                $"{report.summary.result}: {report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB → {outPath}");
        }

        sealed class ResultWriter : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                _testsRunning = false;
                string mode = SessionState.GetString("snowops_testmode", "Unknown");
                var sb = new StringBuilder();
                sb.AppendLine($"{result.TestStatus}: pass {result.PassCount} fail {result.FailCount} skip {result.SkipCount} ({mode})");
                AppendFailures(result, sb);
                WriteResult(mode == "EditMode" ? "tests_editmode" : "tests_playmode", sb.ToString());
            }

            static void AppendFailures(ITestResultAdaptor node, StringBuilder sb)
            {
                if (node.Test.IsSuite)
                {
                    if (node.Children != null)
                        foreach (var child in node.Children)
                            AppendFailures(child, sb);
                    return;
                }
                if (node.TestStatus != TestStatus.Failed) return;
                sb.AppendLine($"FAILED {node.FullName}");
                if (!string.IsNullOrEmpty(node.Message)) sb.AppendLine("  " + node.Message.Replace("\n", "\n  "));
                if (!string.IsNullOrEmpty(node.StackTrace)) sb.AppendLine("  " + node.StackTrace.Replace("\n", "\n  "));
            }
        }
    }
}
