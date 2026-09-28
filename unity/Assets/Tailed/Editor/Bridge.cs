using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Tailed.EditorTools
{
    /// <summary>
    /// File-based command bridge so tools running in WSL can drive an open editor
    /// (batch mode refuses a project that's already open). tools/bridge.sh writes
    /// Temp/Bridge/inbox/&lt;id&gt;.json and waits for Temp/Bridge/outbox/&lt;id&gt;.json.
    /// Files rather than sockets because WSL's NAT networking doesn't reach Windows localhost.
    ///
    /// Commands: ping, status, refresh, play, stop, run-tests (arg: EditMode|PlayMode),
    /// screenshot, console (arg: line count), execute (arg: Namespace.Type.StaticMethod), quit.
    /// Async commands (refresh, play, stop, run-tests, screenshot) survive domain reloads
    /// via SessionState.
    /// </summary>
    [InitializeOnLoad]
    static class Bridge
    {
        [Serializable]
        class Request { public string id; public string cmd; public string arg; }

        [Serializable]
        class Response { public string id; public bool ok; public string message; }

        const string PendingKey = "Tailed.Bridge.Pending";
        const string PendingStartKey = "Tailed.Bridge.PendingStart";
        const string CompileErrorsKey = "Tailed.Bridge.CompileErrors";
        const int LogCapacity = 500;

        static readonly string Root = Path.GetFullPath("Temp/Bridge");
        static readonly string Inbox = Path.Combine(Root, "inbox");
        static readonly string Outbox = Path.Combine(Root, "outbox");
        static readonly string ScreenshotDir = Path.GetFullPath("../out/screens");

        static readonly Queue<string> Log = new Queue<string>();
        static double _nextPoll, _nextHeartbeat;

        static Bridge()
        {
            // Batch runs must not answer (tools/unity.sh uses ping to detect an open editor).
            if (Application.isBatchMode) return;
            Directory.CreateDirectory(Inbox);
            Directory.CreateDirectory(Outbox);
            EditorApplication.update += Tick;
            Application.logMessageReceivedThreaded += OnLog;
            CompilationPipeline.compilationStarted += _ => SessionState.SetString(CompileErrorsKey, "");
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            ScriptableObject.CreateInstance<TestRunnerApi>().RegisterCallbacks(new TestCallbacks());
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + 2.0;
                TryWrite(Path.Combine(Root, "heartbeat"), DateTime.UtcNow.ToString("o"));
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 0.25;

            if (Pending != null)
            {
                CheckPending();
                return; // one async command at a time
            }

            foreach (var file in Directory.GetFiles(Inbox, "*.json").OrderBy(f => f))
            {
                Request req;
                try { req = JsonUtility.FromJson<Request>(File.ReadAllText(file)); }
                catch (IOException) { continue; } // still being written
                File.Delete(file);
                Handle(req);
                if (Pending != null) break;
            }
        }

        static void Handle(Request req)
        {
            try
            {
                switch (req.cmd)
                {
                    case "ping":
                        Reply(req.id, true, $"Unity {Application.unityVersion} project={Application.productName}");
                        break;
                    case "status":
                        Reply(req.id, true, StatusText());
                        break;
                    case "refresh":
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                        SetPending(req);
                        break;
                    case "play":
                        EditorApplication.isPlaying = true;
                        SetPending(req);
                        break;
                    case "stop":
                        EditorApplication.isPlaying = false;
                        SetPending(req);
                        break;
                    case "run-tests":
                        var mode = req.arg == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode;
                        SetPending(req);
                        ScriptableObject.CreateInstance<TestRunnerApi>()
                            .Execute(new ExecutionSettings(new Filter { testMode = mode }));
                        break;
                    case "screenshot":
                        Screenshot(req);
                        break;
                    case "console":
                        int n = int.TryParse(req.arg, out var parsed) ? parsed : 50;
                        lock (Log) Reply(req.id, true, string.Join("\n", Log.Skip(Math.Max(0, Log.Count - n))));
                        break;
                    case "execute":
                        Reply(req.id, true, Execute(req.arg));
                        break;
                    case "unpause":
                        EditorApplication.isPaused = false;
                        Reply(req.id, true, "unpaused");
                        break;
                    case "quit":
                        Reply(req.id, true, "quitting");
                        EditorApplication.delayCall += () => EditorApplication.Exit(0);
                        break;
                    default:
                        Reply(req.id, false, $"unknown command '{req.cmd}'");
                        break;
                }
            }
            catch (Exception e)
            {
                ClearPending();
                Reply(req.id, false, e.ToString());
            }
        }

        // ---- async completion ----------------------------------------------------------

        static Request Pending
        {
            get
            {
                string s = SessionState.GetString(PendingKey, "");
                return s.Length == 0 ? null : JsonUtility.FromJson<Request>(s);
            }
        }

        static void SetPending(Request req)
        {
            SessionState.SetString(PendingKey, JsonUtility.ToJson(req));
            SessionState.SetFloat(PendingStartKey, (float)EditorApplication.timeSinceStartup);
        }

        static void ClearPending() => SessionState.EraseString(PendingKey);

        static void CheckPending()
        {
            var req = Pending;
            double elapsed = EditorApplication.timeSinceStartup - SessionState.GetFloat(PendingStartKey, 0f);
            bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
            switch (req.cmd)
            {
                // Give the editor a moment to start compiling before calling it settled.
                case "refresh" when elapsed > 1.5 && !busy:
                    string errors = SessionState.GetString(CompileErrorsKey, "");
                    Complete(req, errors.Length == 0, errors.Length == 0 ? "compiled OK" : errors);
                    break;
                case "play" when EditorApplication.isPlaying && !EditorApplication.isCompiling:
                    Complete(req, true, "playing");
                    break;
                case "stop" when !EditorApplication.isPlayingOrWillChangePlaymode:
                    Complete(req, true, "stopped");
                    break;
                case "screenshot" when File.Exists(req.arg):
                    Complete(req, true, req.arg);
                    break;
                case "screenshot" when elapsed > 30:
                    Complete(req, false, "screenshot timed out");
                    break;
                // run-tests completes from TestCallbacks.RunFinished.
            }
        }

        static void Complete(Request req, bool ok, string message)
        {
            ClearPending();
            Reply(req.id, ok, message);
        }

        class TestCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                var req = Pending;
                if (req == null || req.cmd != "run-tests") return;
                var sb = new StringBuilder();
                sb.AppendLine($"passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount}");
                foreach (var failed in Leaves(result).Where(r => r.TestStatus == TestStatus.Failed))
                    sb.AppendLine($"FAIL {failed.FullName}: {failed.Message}");
                Complete(req, result.FailCount == 0, sb.ToString().TrimEnd());
            }

            static IEnumerable<ITestResultAdaptor> Leaves(ITestResultAdaptor r) =>
                r.HasChildren ? r.Children.SelectMany(Leaves) : new[] { r };
        }

        // ---- commands ------------------------------------------------------------------

        static string StatusText()
        {
            string errors = SessionState.GetString(CompileErrorsKey, "");
            return $"compiling={EditorApplication.isCompiling} playing={EditorApplication.isPlaying} paused={EditorApplication.isPaused} " +
                   $"runInBackground={Application.runInBackground} focused={UnityEditorInternal.InternalEditorUtility.isApplicationActive} " +
                   $"scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().path}" +
                   (errors.Length > 0 ? "\n" + errors : "");
        }

        static void Screenshot(Request req)
        {
            Directory.CreateDirectory(ScreenshotDir);
            string path = Path.Combine(ScreenshotDir, $"{req.id}.png");
            req.arg = path;
            if (EditorApplication.isPlaying)
            {
                ScreenCapture.CaptureScreenshot(path); // written at end of frame
                SetPending(req);
                return;
            }
            var cam = Camera.main != null ? Camera.main : SceneView.lastActiveSceneView?.camera;
            if (cam == null) throw new InvalidOperationException("no camera to capture");
            var rt = RenderTexture.GetTemporary(1280, 720, 24);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
            Reply(req.id, true, path);
        }

        static string Execute(string qualifiedMethod)
        {
            int dot = qualifiedMethod.LastIndexOf('.');
            string typeName = qualifiedMethod.Substring(0, dot), methodName = qualifiedMethod.Substring(dot + 1);
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null)
                ?? throw new ArgumentException($"type not found: {typeName}");
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new ArgumentException($"static parameterless method not found: {qualifiedMethod}");
            return method.Invoke(null, null)?.ToString() ?? "ok";
        }

        // ---- plumbing ------------------------------------------------------------------

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            var errors = messages.Where(m => m.type == CompilerMessageType.Error)
                .Select(m => m.message).ToArray(); // message already includes file(line,col)
            if (errors.Length == 0) return;
            string prev = SessionState.GetString(CompileErrorsKey, "");
            SessionState.SetString(CompileErrorsKey, prev + string.Join("\n", errors) + "\n");
        }

        static void OnLog(string message, string stackTrace, LogType type)
        {
            string line = type == LogType.Log ? message : $"[{type}] {message}";
            if (type == LogType.Exception || type == LogType.Error)
                line += "\n" + stackTrace.TrimEnd();
            lock (Log)
            {
                Log.Enqueue(line);
                while (Log.Count > LogCapacity) Log.Dequeue();
            }
        }

        static void Reply(string id, bool ok, string message)
        {
            string json = JsonUtility.ToJson(new Response { id = id, ok = ok, message = message });
            string tmp = Path.Combine(Outbox, id + ".tmp");
            TryWrite(tmp, json);
            File.Move(tmp, Path.Combine(Outbox, id + ".json"));
        }

        static void TryWrite(string path, string text)
        {
            try { File.WriteAllText(path, text); }
            catch (IOException) { }
        }
    }
}
