using System.Collections.Generic;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Audio
{
    /// <summary>
    /// The town's background: a distant traffic rumble, birds by day, crickets at night, rain hiss,
    /// and engine notes on the few NPC cars nearest the listener. All synthesised, no assets.
    /// </summary>
    public sealed class Ambience : MonoBehaviour
    {
        static Ambience _instance;
        volatile bool _night, _rain;
        int _rate;
        uint _seed = 2463534242u;
        float _brown, _prevWhite, _hp;
        // Bird chirp / cricket state (audio thread).
        int _untilChirp, _chirpLeft, _chirpLen;
        float _chirpFreq, _chirpPhase, _cricketPhase, _cricketGate;
        int _untilCricket, _cricketLeft;

        const int EngineVoices = 6;
        readonly List<NpcEngine> _voices = new List<NpcEngine>();
        readonly List<VehicleView> _near = new List<VehicleView>();
        float _assignTimer;

        public static void Set(bool night, bool rain)
        {
            if (_instance == null)
            {
                var go = new GameObject("Ambience");
                go.AddComponent<AudioSource>(); // before the filter, so the filter feeds it
                _instance = go.AddComponent<Ambience>();
            }
            _instance._night = night;
            _instance._rain = rain;
        }

        void Awake()
        {
            _rate = AudioSettings.outputSampleRate;
            var s = GetComponent<AudioSource>();
            s.spatialBlend = 0f;
            s.loop = true;
            s.volume = 0.8f;
            s.Play();
            for (int i = 0; i < EngineVoices; i++)
            {
                var v = new GameObject("NpcEngine").AddComponent<NpcEngine>();
                v.transform.SetParent(transform, false);
                _voices.Add(v);
            }
        }

        void Update()
        {
            // Follow the listener so the 2D bed stays centred (and voices can find neighbours).
            var cam = Camera.main;
            if (cam == null) return;
            if ((_assignTimer -= Time.deltaTime) > 0f) return;
            _assignTimer = 0.25f;
            var at = cam.transform.position;
            _near.Clear();
            foreach (var v in VehicleView.Active)
                if (v != null && v.Speed > 0.3f && (v.transform.position - at).sqrMagnitude < 45f * 45f) _near.Add(v);
            _near.Sort((a, b) => (a.transform.position - at).sqrMagnitude.CompareTo((b.transform.position - at).sqrMagnitude));
            for (int i = 0; i < _voices.Count; i++) _voices[i].Target = i < _near.Count ? _near[i] : null;
        }

        float White()
        {
            _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
            return (_seed & 0xFFFFFF) / (float)0xFFFFFF * 2f - 1f;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            bool night = _night, rain = _rain;
            float dt = 1f / _rate;
            for (int i = 0; i < data.Length; i += channels)
            {
                float w = White();
                // Distant traffic: brown noise (integrated white), quiet.
                _brown = Mathf.Clamp(_brown * 0.998f + w * 0.03f, -1f, 1f);
                float v = _brown * (night ? 0.05f : 0.09f);

                if (rain)
                {
                    // Rain: bright hiss (high-passed white noise) with slow swells.
                    _hp = 0.6f * (_hp + w - _prevWhite);
                    v += _hp * 0.07f;
                }
                else if (!night)
                {
                    // Birds: short upward chirps with a little trill, at random intervals.
                    if (_chirpLeft > 0)
                    {
                        float t = 1f - _chirpLeft / (float)_chirpLen;
                        float f = _chirpFreq * (1f + 0.35f * t) * (1f + 0.04f * Mathf.Sin(t * 60f));
                        _chirpPhase += f * dt;
                        v += Mathf.Sin(_chirpPhase * 2f * Mathf.PI) * Mathf.Sin(t * Mathf.PI) * 0.035f;
                        _chirpLeft--;
                    }
                    else if (--_untilChirp <= 0)
                    {
                        _chirpLen = _chirpLeft = (int)(_rate * (0.05f + 0.08f * (White() * 0.5f + 0.5f)));
                        _chirpFreq = 2800f + 1800f * (White() * 0.5f + 0.5f);
                        // Often a quick run of 2-4 chirps, then a pause.
                        _untilChirp = White() > 0.2f ? (int)(_rate * 0.07f) : (int)(_rate * (0.6f + 2.5f * (White() * 0.5f + 0.5f)));
                    }
                }
                else
                {
                    // Crickets: a fast-pulsed 4.6 kHz tone in bursts.
                    if (_cricketLeft > 0)
                    {
                        _cricketPhase += 4600f * dt;
                        _cricketGate += 32f * dt;
                        float gate = Mathf.Repeat(_cricketGate, 1f) < 0.5f ? 1f : 0f;
                        v += Mathf.Sin(_cricketPhase * 2f * Mathf.PI) * gate * 0.012f;
                        _cricketLeft--;
                    }
                    else if (--_untilCricket <= 0)
                    {
                        _cricketLeft = (int)(_rate * 0.35f);
                        _untilCricket = (int)(_rate * (0.4f + 0.6f * (White() * 0.5f + 0.5f)));
                    }
                }
                _prevWhite = w;
                for (int c = 0; c < channels; c++) data[i + c] = v;
            }
        }
    }

    /// <summary>A pooled 3D engine voice that sits on a nearby NPC car and follows its speed.</summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class NpcEngine : MonoBehaviour
    {
        public VehicleView Target;
        float _phase, _freq = 30f, _vol, _targetVol;
        int _rate;
        float _pitch;

        void Awake()
        {
            _rate = AudioSettings.outputSampleRate;
            var s = GetComponent<AudioSource>();
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 4f;
            s.maxDistance = 45f;
            s.dopplerLevel = 0.5f;
            s.loop = true;
            s.Play();
            _pitch = Random.Range(0.85f, 1.2f);
        }

        void Update()
        {
            if (Target == null || !Target.isActiveAndEnabled) { _targetVol = 0f; return; }
            transform.position = Target.transform.position + Vector3.up * 0.6f;
            float speed = Target.Speed;
            float gear = Mathf.Repeat(speed, 8f) / 8f;
            _freq = (26f + speed * 1.1f + gear * 20f) * _pitch;
            _targetVol = 0.05f + 0.06f * Mathf.Clamp01(Target.Accel / 1.5f) + 0.02f * Mathf.Min(speed, 14f) / 14f;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            float inc = _freq / _rate;
            for (int i = 0; i < data.Length; i += channels)
            {
                _vol += (_targetVol - _vol) * 0.0005f; // de-click when voices swap cars
                _phase = (_phase + inc) % 1f;
                float p = _phase * 2f * Mathf.PI;
                float v = (Mathf.Sin(p) + 0.6f * Mathf.Sin(2 * p) + 0.25f * Mathf.Sin(3 * p)) * _vol;
                for (int c = 0; c < channels; c++) data[i + c] = v;
            }
        }
    }
}
