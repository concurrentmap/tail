using System;
using System.Collections.Generic;
using Tailed.Core.Net;
using Tailed.Core.Rules;
using Tailed.Net;
using Tailed.Vehicles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tailed.Audio
{
    /// <summary>
    /// Proximity + team-radio voice (GD §7) over the game's own connection — no external voice service.
    /// Mic → 20 ms frames → voice activity detection → IMA ADPCM → host, which routes by role and by
    /// distance/window state → listeners play it back positioned at the speaker's car (proximity) or
    /// as a band-limited radio (team). Y: windows up/down · hold B: team radio · N: mute mic.
    /// </summary>
    public sealed class VoiceChat : MonoBehaviour
    {
        public const int Rate = 16000, Frame = 320;
        public static VoiceChat Instance { get; private set; }
        public static bool Muted;
        /// <summary>Synthetic "speech" instead of the microphone (automated tests).</summary>
        public static bool TestTone;
        /// <summary>Hold the team-radio key (automated tests).</summary>
        public static bool ForceRadio;
        public float MicLevel { get; private set; }
        public bool Speaking { get; private set; }
        public bool Radio { get; private set; }
        public int FramesSent { get; private set; }
        public int FramesReceived { get; private set; }

        AudioClip _mic;
        string _device;
        int _micPos;
        readonly List<float> _pending = new List<float>(Frame * 4);
        float[] _read = new float[Rate];
        float _hang, _toneT;
        Adpcm.State _enc;
        ushort _seq;
        readonly Dictionary<(int, bool), VoicePlayer> _players = new Dictionary<(int, bool), VoicePlayer>();
        public readonly Dictionary<int, float> RadioTalkers = new Dictionary<int, float>(); // player → last heard

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (_mic != null) Microphone.End(_device);
        }

        void Update()
        {
            var c = ClientGame.Instance;
            if (c == null || !c.Connected) return;
            var kb = Keyboard.current;
            if (kb != null && !CarInput.Blocked)
            {
                if (kb.nKey.wasPressedThisFrame) Muted = !Muted;
                if (kb.yKey.wasPressedThisFrame && PlayerCar.Local != null) PlayerCar.Local.WindowsOpen = !PlayerCar.Local.WindowsOpen;
            }
            bool teamRole = c.MyRole == Role.Tail || c.MyRole == Role.Spotter;
            Radio = teamRole && (ForceRadio || (kb != null && kb.bKey.isPressed && !CarInput.Blocked));

            Capture();
            while (_pending.Count >= Frame)
            {
                var frame = _pending.GetRange(0, Frame).ToArray();
                _pending.RemoveRange(0, Frame);
                double sum = 0;
                foreach (var s in frame) sum += s * s;
                float rms = (float)Math.Sqrt(sum / Frame);
                MicLevel = Mathf.Lerp(MicLevel, rms, 0.3f);
                _hang = rms > 0.012f ? 0.35f : _hang - Frame / (float)Rate;
                Speaking = _hang > 0f && !Muted;
                bool proximity = Speaking && PlayerCar.Local != null;
                if (!(Radio && !Muted) && !proximity) continue;
                byte flags = (byte)((Radio ? 1 : 0) | (PlayerCar.Local != null && PlayerCar.Local.WindowsOpen ? 2 : 0) | (proximity ? 4 : 0));
                ushort seq = _seq++;
                c.Send(Msg.Voice, w => { w.U8(flags); w.U16(seq); Adpcm.Encode(frame, ref _enc, w); }, reliable: false);
                FramesSent++;
            }
        }

        void Capture()
        {
            if (TestTone)
            {
                // A wobbling vowel that "talks" in 1.5 s bursts.
                int n = Mathf.RoundToInt(Time.unscaledDeltaTime * Rate);
                for (int i = 0; i < n; i++)
                {
                    _toneT += 1f / Rate;
                    float env = (_toneT % 3f) < 1.5f ? 0.25f : 0f;
                    float f0 = 150f + 25f * Mathf.Sin(_toneT * 6f);
                    _pending.Add(env * (Mathf.Sin(2 * Mathf.PI * f0 * _toneT) + 0.4f * Mathf.Sin(4 * Mathf.PI * f0 * _toneT)));
                }
                return;
            }
            if (_mic == null)
            {
                if (Microphone.devices.Length == 0 || HasTestFlag) return;
                _device = Microphone.devices[0];
                _mic = Microphone.Start(_device, true, 1, Rate);
                _micPos = 0;
                return;
            }
            int pos = Microphone.GetPosition(_device);
            int len = _mic.samples;
            int count = (pos - _micPos + len) % len;
            if (count <= 0) return;
            if (_read.Length < count) _read = new float[count];
            // Read with wrap-around.
            int first = Mathf.Min(count, len - _micPos);
            var a = new float[first];
            _mic.GetData(a, _micPos);
            _pending.AddRange(a);
            if (count > first)
            {
                var b = new float[count - first];
                _mic.GetData(b, 0);
                _pending.AddRange(b);
            }
            _micPos = pos;
            // Mono clips at the device rate may differ from 16 kHz; Unity resamples Microphone.Start to the requested rate.
        }

        /// <summary>Host → client: a routed voice frame.</summary>
        public void Receive(ByteReader r)
        {
            int speaker = r.I32();
            byte flags = r.U8();
            r.U16(); // sequence (unused: frames are self-contained)
            float x = r.F32(), z = r.F32(), gain = r.F32();
            var pcm = new float[Frame];
            Adpcm.Decode(r, Frame, pcm);
            bool radio = (flags & 1) != 0;
            FramesReceived++;
            if (radio) RadioTalkers[speaker] = Time.time;
            var key = (speaker, radio);
            if (!_players.TryGetValue(key, out var p) || p == null) _players[key] = p = VoicePlayer.Create(transform, radio);
            if (!radio) p.transform.position = new Vector3(x, 1.2f, z);
            p.Push(pcm, gain);
        }

        public string Stats() => $"sent={FramesSent} received={FramesReceived} speaking={Speaking} radio={Radio} level={MicLevel:0.000} mic={(_mic != null ? _device : TestTone ? "test-tone" : "none")}";

        /// <summary>Bridge: turn the synthetic test voice on.</summary>
        public static string EnableTestTone() { TestTone = true; return "test tone on"; }
        public static string RadioOn() { ForceRadio = true; return "radio keyed"; }
        static readonly bool HasTestFlag = Array.IndexOf(Environment.GetCommandLineArgs(), "-tailed-test-voice") >= 0;
        public static string Report() => Instance != null ? Instance.Stats() : "no voice";
    }

    /// <summary>Streams one speaker's decoded frames through a (3D or radio-filtered) AudioSource.</summary>
    public sealed class VoicePlayer : MonoBehaviour
    {
        readonly float[] _ring = new float[VoiceChat.Rate * 2];
        int _write, _read, _buffered;
        bool _primed;
        float _gain = 1f;
        readonly object _lock = new object();

        public static VoicePlayer Create(Transform parent, bool radio)
        {
            var go = new GameObject(radio ? "RadioVoice" : "ProximityVoice");
            go.transform.SetParent(parent, false);
            var p = go.AddComponent<VoicePlayer>();
            var src = go.AddComponent<AudioSource>();
            src.clip = AudioClip.Create("voice", VoiceChat.Rate, 1, VoiceChat.Rate, true, p.OnRead);
            src.loop = true;
            src.spatialBlend = radio ? 0f : 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 2f;
            src.maxDistance = 25f;
            src.dopplerLevel = 0f;
            if (radio)
            {
                go.AddComponent<AudioHighPassFilter>().cutoffFrequency = 350f;
                go.AddComponent<AudioLowPassFilter>().cutoffFrequency = 3200f;
                go.AddComponent<AudioDistortionFilter>().distortionLevel = 0.35f;
            }
            src.Play();
            return p;
        }

        public void Push(float[] pcm, float gain)
        {
            lock (_lock)
            {
                _gain = gain;
                foreach (var s in pcm)
                {
                    _ring[_write] = s;
                    _write = (_write + 1) % _ring.Length;
                    if (_buffered < _ring.Length) _buffered++; else _read = (_read + 1) % _ring.Length;
                }
            }
        }

        void OnRead(float[] data)
        {
            lock (_lock)
            {
                // Jitter buffer: wait for ~60 ms before playing, re-prime after an underflow.
                if (!_primed && _buffered >= VoiceChat.Rate * 6 / 100) _primed = true;
                for (int i = 0; i < data.Length; i++)
                {
                    if (_primed && _buffered > 0)
                    {
                        data[i] = _ring[_read] * _gain;
                        _read = (_read + 1) % _ring.Length;
                        _buffered--;
                    }
                    else { data[i] = 0f; _primed = false; }
                }
            }
        }
    }
}
