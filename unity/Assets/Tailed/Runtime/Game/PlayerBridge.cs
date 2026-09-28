using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// File command bridge for built players (multi-instance tests), mirroring the editor bridge:
    /// DIR/inbox/*.json → DIR/outbox/*.json. Commands: ping, status, screenshot, execute, quit.
    /// Enabled with -tailed-bridge DIR.
    /// </summary>
    public sealed class PlayerBridge : MonoBehaviour
    {
        public string Directory;

        [Serializable] class Request { public string id; public string cmd; public string arg; }
        [Serializable] class Response { public string id; public bool ok; public string message; }

        string _inbox, _outbox;
        float _poll, _heartbeat;
        string _pendingShot, _pendingId;

        void Start()
        {
            _inbox = Path.Combine(Directory, "inbox");
            _outbox = Path.Combine(Directory, "outbox");
            System.IO.Directory.CreateDirectory(_inbox);
            System.IO.Directory.CreateDirectory(_outbox);
        }

        void Update()
        {
            if ((_heartbeat -= Time.unscaledDeltaTime) <= 0f)
            {
                _heartbeat = 2f;
                try { File.WriteAllText(Path.Combine(Directory, "heartbeat"), DateTime.UtcNow.ToString("o")); } catch (IOException) { }
            }
            if (_pendingShot != null && File.Exists(_pendingShot)) { Reply(_pendingId, true, _pendingShot); _pendingShot = null; }
            if ((_poll -= Time.unscaledDeltaTime) > 0f) return;
            _poll = 0.25f;
            foreach (var f in System.IO.Directory.GetFiles(_inbox, "*.json").OrderBy(x => x))
            {
                Request req;
                try { req = JsonUtility.FromJson<Request>(File.ReadAllText(f)); } catch (IOException) { continue; }
                File.Delete(f);
                try { Handle(req); }
                catch (Exception e) { Reply(req.id, false, e.ToString()); }
            }
        }

        void Handle(Request req)
        {
            switch (req.cmd)
            {
                case "ping": Reply(req.id, true, $"Tailed player {Application.version}"); break;
                case "status": Reply(req.id, true, Status()); break;
                case "screenshot":
                    _pendingShot = Path.Combine(Directory, $"{req.id}.png");
                    _pendingId = req.id;
                    ScreenCapture.CaptureScreenshot(_pendingShot);
                    break;
                case "execute":
                {
                    int dot = req.arg.LastIndexOf('.');
                    var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(req.arg.Substring(0, dot))).FirstOrDefault(t => t != null);
                    var m = type?.GetMethod(req.arg.Substring(dot + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    Reply(req.id, m != null, m != null ? m.Invoke(null, null)?.ToString() ?? "ok" : "method not found");
                    break;
                }
                case "quit": Reply(req.id, true, "bye"); Application.Quit(); break;
                default: Reply(req.id, false, "unknown command"); break;
            }
        }

        public static string Status()
        {
            var c = Net.ClientGame.Instance;
            var car = Vehicles.PlayerCar.Local;
            var net = Net.NetSession.Instance;
            string s = $"mode={GameBootstrap.Instance?.Current} fps={1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):0}";
            if (c != null)
                s += $" id={c.MyId} phase={c.Phase} t={c.PhaseTime:0}/{c.PhaseDuration:0} round={c.Round} role={c.MyRole} players={c.Players.Count}" +
                     $" pins={c.Pins.Count} next={c.NextCheckpoint}/{c.Route.Count} tokens={c.FlagTokens} dwell={(c.Dwelling ? c.DwellProgress.ToString("0.00") : "-")}" +
                     $" replica={(Net.TrafficReplica.Instance != null ? Net.TrafficReplica.Instance.Count : -1)}" +
                     $" debrief={(c.Debrief != null ? c.Debrief.Reason : "-")}";
            if (car != null) s += $" car=({car.transform.position.x:0},{car.transform.position.z:0}) v={car.ForwardSpeed:0.0}";
            if (net != null && net.IsHost && Net.HostGame.Instance != null) s += $" sent={net.BytesSent / 1024}KB";
            if (!string.IsNullOrEmpty(UI.GameUI.NavText)) s += $" nav=\"{UI.GameUI.NavText}\"";
            if (c != null && c.Notices.Count > 0) s += $" lastNotice=\"{c.Notices[c.Notices.Count - 1].text}\"";
            return s;
        }

        void Reply(string id, bool ok, string message)
        {
            var tmp = Path.Combine(_outbox, id + ".tmp");
            File.WriteAllText(tmp, JsonUtility.ToJson(new Response { id = id, ok = ok, message = message }));
            File.Move(tmp, Path.Combine(_outbox, id + ".json"));
        }
    }
}
