using System;
using System.Collections.Generic;
using Tailed.Core.Net;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Tailed.Net
{
    /// <summary>
    /// Thin wrapper over Netcode for GameObjects used purely as connection + transport: every message
    /// is one named message whose payload is a core ByteWriter buffer (first byte = <see cref="Msg"/>).
    /// The host also runs a client; messages to itself are dispatched locally.
    /// </summary>
    public sealed class NetSession : MonoBehaviour
    {
        public const ushort DefaultPort = 7777;
        const string Channel = "tailed";

        public static NetSession Instance { get; private set; }
        public NetworkManager Manager { get; private set; }
        public bool IsHost => Manager != null && Manager.IsHost;
        public bool IsClient => Manager != null && Manager.IsClient;
        public bool Active => Manager != null && (Manager.IsServer || Manager.IsClient);
        public ulong LocalId => Manager != null ? Manager.LocalClientId : 0;

        /// <summary>(sender, message type, reader positioned after the type byte).</summary>
        public event Action<ulong, Msg, ByteReader> HostReceived, ClientReceived;
        public event Action<ulong> ClientConnected, ClientDisconnected;
        public event Action ConnectedToHost, DisconnectedFromHost;

        readonly ByteWriter _writer = new ByteWriter(2048);
        /// <summary>Largest reliable message (fragmented by the transport).</summary>
        public const int MaxMessageBytes = 64 * 1024;
        public int BytesSent { get; private set; }

        void Awake()
        {
            Instance = this;
            Manager = gameObject.AddComponent<NetworkManager>();
            var utp = gameObject.AddComponent<UnityTransport>();
            // The default 6 KB cap is too small for the debrief (every trail + the round's timeline).
            utp.MaxPayloadSize = MaxMessageBytes;
            Manager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = utp,
                EnableSceneManagement = false,
                ConnectionApproval = false,
                TickRate = 30,
            };
            Manager.OnClientConnectedCallback += id =>
            {
                if (Manager.IsServer && id != Manager.LocalClientId) ClientConnected?.Invoke(id);
                if (!Manager.IsServer && id == Manager.LocalClientId) ConnectedToHost?.Invoke();
            };
            Manager.OnClientDisconnectCallback += id =>
            {
                if (Manager.IsServer) ClientDisconnected?.Invoke(id);
                else DisconnectedFromHost?.Invoke();
            };
        }

        public bool Host(ushort port)
        {
            var utp = (UnityTransport)Manager.NetworkConfig.NetworkTransport;
            utp.SetConnectionData("0.0.0.0", port, "0.0.0.0");
            if (!Manager.StartHost()) return false;
            Register();
            return true;
        }

        public bool Join(string address, ushort port)
        {
            var utp = (UnityTransport)Manager.NetworkConfig.NetworkTransport;
            utp.SetConnectionData(address, port);
            if (!Manager.StartClient()) return false;
            Register();
            return true;
        }

        public void Leave()
        {
            if (Manager != null && Active) Manager.Shutdown();
        }

        void Register()
        {
            Manager.CustomMessagingManager.RegisterNamedMessageHandler(Channel, OnNamed);
        }

        void OnNamed(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out bool toHost);
            reader.ReadValueSafe(out int len);
            var bytes = new byte[len];
            reader.ReadBytesSafe(ref bytes, len);
            Dispatch(sender, toHost, bytes, len);
        }

        void Dispatch(ulong sender, bool toHost, byte[] bytes, int len)
        {
            var r = new ByteReader(bytes, 0, len);
            var type = (Msg)r.U8();
            try
            {
                if (toHost) HostReceived?.Invoke(sender, type, r);
                else ClientReceived?.Invoke(sender, type, r);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Net] handling {type} from {sender}: {e}");
            }
        }

        /// <summary>Start a message; fill it with the returned writer, then call a Send method.</summary>
        public ByteWriter Begin(Msg type)
        {
            _writer.Clear();
            _writer.U8((byte)type);
            return _writer;
        }

        public void SendToHost(ByteWriter w, bool reliable = true)
        {
            if (Manager.IsServer) { Dispatch(Manager.LocalClientId, true, w.ToArray(), w.Length); return; }
            Send(NetworkManager.ServerClientId, true, w, reliable);
        }

        public void SendToClient(ulong client, ByteWriter w, bool reliable = true)
        {
            if (Manager.IsServer && client == Manager.LocalClientId) { Dispatch(client, false, w.ToArray(), w.Length); return; }
            Send(client, false, w, reliable);
        }

        public void SendToClients(IEnumerable<ulong> clients, ByteWriter w, bool reliable = true)
        {
            var bytes = w.ToArray();
            foreach (var c in clients)
            {
                if (Manager.IsServer && c == Manager.LocalClientId) { Dispatch(c, false, bytes, bytes.Length); continue; }
                Send(c, false, w, reliable);
            }
        }

        public IReadOnlyList<ulong> Clients => Manager.ConnectedClientsIds;

        void Send(ulong target, bool toHost, ByteWriter w, bool reliable)
        {
            var delivery = reliable ? (w.Length > 1000 ? NetworkDelivery.ReliableFragmentedSequenced : NetworkDelivery.ReliableSequenced)
                                    : NetworkDelivery.UnreliableSequenced;
            using var fw = new FastBufferWriter(w.Length + 8, Allocator.Temp, w.Length + 64);
            fw.WriteValueSafe(toHost);
            fw.WriteValueSafe(w.Length);
            fw.WriteBytesSafe(w.Buffer, w.Length);
            Manager.CustomMessagingManager.SendNamedMessage(Channel, target, fw, delivery);
            BytesSent += w.Length;
        }
    }
}
