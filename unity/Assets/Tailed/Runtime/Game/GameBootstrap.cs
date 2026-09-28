using System;
using System.Linq;
using Tailed.Net;
using Tailed.Traffic;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// Entry point: host / join / offline, from the menu or the command line.
    ///   -tailed-host [port]        host a game (and play)
    ///   -tailed-join addr[:port]   join a host
    ///   -tailed-offline            drive around alone
    ///   -tailed-name NAME
    ///   -tailed-bot                autopilot + scripted choices (tests)
    ///   -tailed-autostart N        host starts the match once N players are in
    ///   -tailed-cheat-bots         bot Tails are told which car is the Mark (tests only)
    ///   -tailed-bridge DIR         enable the file command bridge in a built player
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        public static GameBootstrap Instance { get; private set; }
        public string PlayerName = "Player";
        public bool Bot;
        public enum Mode { Menu, Host, Client, Offline }
        public Mode Current { get; private set; } = Mode.Menu;
        /// <summary>Why we last dropped back to the menu (shown there), or null.</summary>
        public string LastError;
        public string JoinAddress { get; private set; }

        void Awake()
        {
            Instance = this;
            if (GetComponent<WorldEnvironment>() == null) gameObject.AddComponent<WorldEnvironment>();
            if (GetComponent<SpotterCams>() == null) gameObject.AddComponent<SpotterCams>();
        }

        void Start()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[i + 1] : i >= 0 ? "" : null; }
            PlayerName = Arg("-tailed-name") ?? Environment.UserName ?? "Player";
            Bot = args.Contains("-tailed-bot");
            var bridge = Arg("-tailed-bridge");
            if (!string.IsNullOrEmpty(bridge)) gameObject.AddComponent<PlayerBridge>().Directory = bridge;

            var host = Arg("-tailed-host");
            var join = Arg("-tailed-join");
            if (host != null) StartHost(ushort.TryParse(host, out var port) ? port : NetSession.DefaultPort, int.TryParse(Arg("-tailed-autostart"), out var n) ? n : 0, args.Contains("-tailed-cheat-bots"));
            else if (!string.IsNullOrEmpty(join)) StartClient(join);
            else if (args.Contains("-tailed-offline")) StartOffline();
        }

        NetSession EnsureSession()
        {
            var s = NetSession.Instance;
            if (s == null) s = new GameObject("Net").AddComponent<NetSession>();
            return s;
        }

        public void StartHost(ushort port, int autoStart = 0, bool cheatBots = false)
        {
            TrafficRunner.Instance.SetAuthoritative(true);
            var s = EnsureSession();
            if (!s.Host(port)) { ReturnToMenu($"Couldn't host on port {port} — is another game already hosting on this PC?"); return; }
            var host = s.gameObject.AddComponent<HostGame>();
            host.AutoStartPlayers = autoStart;
            host.CheatBots = cheatBots;
            host.Quick = Environment.GetCommandLineArgs().Contains("-tailed-quick");
            AddClient(s);
            Current = Mode.Host;
            Debug.Log($"[Tailed] Hosting on port {port}");
        }

        public void StartClient(string address)
        {
            ushort port = NetSession.DefaultPort;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(address.Substring(colon + 1), out var p)) { port = p; address = address.Substring(0, colon); }
            var runner = TrafficRunner.Instance;
            runner.SetAuthoritative(false);
            var s = EnsureSession();
            s.gameObject.AddComponent<TrafficReplica>();
            AddClient(s);
            runner.ExternalClock = () => ClientGame.Instance != null ? ClientGame.Instance.HostTime : 0f;
            JoinAddress = $"{address}:{port}";
            if (!s.Join(address, port)) { ReturnToMenu($"Couldn't start connecting to {address}:{port} — check the address."); return; }
            Current = Mode.Client;
            Debug.Log($"[Tailed] Joining {address}:{port}");
        }

        void AddClient(NetSession s)
        {
            var c = s.gameObject.AddComponent<ClientGame>();
            if (s.GetComponent<Audio.VoiceChat>() == null) s.gameObject.AddComponent<Audio.VoiceChat>();
            if (Environment.GetCommandLineArgs().Contains("-tailed-test-voice")) Audio.VoiceChat.TestTone = true;
            c.PlayerName = PlayerName;
            c.IsBot = Bot;
            if (Bot) s.gameObject.AddComponent<BotBrain>();
        }

        /// <summary>
        /// Leave any session (host, client or failed join) and go back to the main menu, with an
        /// optional reason to show there. Tears down networking and match components and restores
        /// local NPC traffic.
        /// </summary>
        public void ReturnToMenu(string reason = null)
        {
            LastError = reason;
            if (reason != null) Debug.Log($"[Tailed] Back to menu: {reason}");
            var s = NetSession.Instance;
            if (s != null) { s.Leave(); Destroy(s.gameObject); }
            var runner = TrafficRunner.Instance;
            if (runner != null)
            {
                runner.ExternalClock = null;
                runner.LocallyDriven.Clear();
                runner.Restart(authoritative: true);
            }
            Current = Mode.Menu;
        }

        /// <summary>Dev / bridge: host or join from the menu (tests re-entering a session after leaving one).</summary>
        public static string DevHost() { Instance.StartHost(NetSession.DefaultPort); return Instance.Current.ToString(); }
        public static string DevJoinLocal() { Instance.StartClient("127.0.0.1"); return Instance.Current.ToString(); }
        public static string DevLeave() { Instance.ReturnToMenu("Left via dev command"); return Instance.Current.ToString(); }

        public void StartOffline()
        {
            Current = Mode.Offline;
            gameObject.AddComponent<SinglePlayerHarness>();
        }
    }
}
