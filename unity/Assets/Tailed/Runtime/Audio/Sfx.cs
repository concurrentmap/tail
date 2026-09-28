using UnityEngine;

namespace Tailed.Audio
{
    /// <summary>Procedurally generated sound effects (no audio assets): horn, indicator tick, crash thump.</summary>
    public static class Sfx
    {
        const int Rate = 44100;
        static AudioClip _horn, _tick, _thump, _ding, _buzz, _drum, _fanfare;

        public static AudioClip Horn => _horn ? _horn : _horn = Make("Horn", 0.5f, t =>
        {
            // Two-tone car horn: slightly detuned square-ish waves.
            float a = Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 410f * t)), b = Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 515f * t));
            return 0.18f * (a + b) * 0.5f;
        });

        public static AudioClip Tick => _tick ? _tick : _tick = Make("Tick", 0.04f, t => Mathf.Exp(-t * 180f) * Mathf.Sin(2 * Mathf.PI * 1800f * t) * 0.5f);

        public static AudioClip Thump => _thump ? _thump : _thump = Make("Thump", 0.35f, t =>
            Mathf.Exp(-t * 14f) * (Mathf.Sin(2 * Mathf.PI * 70f * t) * 0.8f + (Random.value - 0.5f) * Mathf.Exp(-t * 40f)));

        // Reveal show (debrief): game-show stings.
        /// <summary>Bright two-note "found it!" chime.</summary>
        public static AudioClip Ding => _ding ? _ding : _ding = Make("Ding", 0.7f, t =>
        {
            float f = t < 0.12f ? 1046.5f : 1568f; // C6 → G6
            float env = t < 0.12f ? Mathf.Exp(-t * 18f) : Mathf.Exp(-(t - 0.12f) * 5f);
            return 0.28f * env * (Mathf.Sin(2 * Mathf.PI * f * t) + 0.35f * Mathf.Sin(4 * Mathf.PI * f * t));
        });

        /// <summary>Low wrong-answer buzzer.</summary>
        public static AudioClip Buzz => _buzz ? _buzz : _buzz = Make("Buzz", 0.45f, t =>
        {
            float saw = 2f * Mathf.Repeat(t * 110f, 1f) - 1f, saw2 = 2f * Mathf.Repeat(t * 116f, 1f) - 1f;
            return 0.16f * (saw + saw2) * Mathf.Clamp01((0.45f - t) * 10f);
        });

        /// <summary>One snare hit for the suspense roll.</summary>
        public static AudioClip Drum => _drum ? _drum : _drum = Make("Drum", 0.09f, t =>
            (Random.value * 2f - 1f) * 0.22f * Mathf.Exp(-t * 45f) + Mathf.Sin(2 * Mathf.PI * 190f * t) * 0.12f * Mathf.Exp(-t * 30f));

        /// <summary>Winner's fanfare: a rising major arpeggio and a held chord.</summary>
        public static AudioClip Fanfare => _fanfare ? _fanfare : _fanfare = Make("Fanfare", 1.8f, t =>
        {
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };
            float v = 0f;
            for (int i = 0; i < notes.Length; i++)
            {
                float start = i * 0.13f;
                if (t < start) continue;
                float lt = t - start;
                float env = (i < 3 ? Mathf.Exp(-lt * 2.5f) : Mathf.Clamp01(1.6f - lt)) * Mathf.Clamp01(lt * 60f);
                float ph = 2 * Mathf.PI * notes[i] * t;
                v += env * (Mathf.Sin(ph) + 0.4f * Mathf.Sin(2 * ph) + 0.2f * Mathf.Sin(3 * ph));
            }
            return 0.09f * v;
        });

        static AudioSource _ui;
        /// <summary>Play a non-spatial UI sound.</summary>
        public static void PlayUi(AudioClip clip, float volume = 1f)
        {
            if (_ui == null)
            {
                var go = new GameObject("UiSfx");
                Object.DontDestroyOnLoad(go);
                _ui = Source(go, false);
            }
            _ui.PlayOneShot(clip, volume);
        }

        static AudioClip Make(string name, float seconds, System.Func<float, float> f)
        {
            int n = Mathf.RoundToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = f(i / (float)Rate);
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        public static AudioSource Source(GameObject go, bool spatial, float maxDistance = 60f)
        {
            var s = go.AddComponent<AudioSource>();
            s.spatialBlend = spatial ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 3f;
            s.maxDistance = maxDistance;
            s.dopplerLevel = 0.3f;
            s.playOnAwake = false;
            return s;
        }
    }

    /// <summary>Engine hum for the local car: a few harmonics of an RPM-ish tone plus rumble.</summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class EngineAudio : MonoBehaviour
    {
        public Vehicles.PlayerCar Car;
        float _phase, _freq = 30f, _vol;
        int _rate;
        uint _noise = 12345;

        void Start()
        {
            _rate = AudioSettings.outputSampleRate;
            var s = GetComponent<AudioSource>();
            s.spatialBlend = 0.6f;
            s.loop = true;
            s.Play();
        }

        void Update()
        {
            if (Car == null) return;
            float speed = Mathf.Abs(Car.ForwardSpeed);
            // Fake gearbox: frequency climbs within each "gear".
            float gear = Mathf.Repeat(speed, 9f) / 9f;
            _freq = 28f + speed * 1.2f + gear * 22f;
            _vol = 0.05f + 0.1f * Car.Throttle + 0.02f * Mathf.Min(speed, 15f) / 15f;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            float inc = _freq / _rate;
            for (int i = 0; i < data.Length; i += channels)
            {
                _phase = (_phase + inc) % 1f;
                float p = _phase * 2f * Mathf.PI;
                _noise = _noise * 1664525u + 1013904223u;
                float n = ((_noise >> 9) / (float)(1 << 23) - 0.5f) * 0.15f;
                float v = (Mathf.Sin(p) + 0.5f * Mathf.Sin(2 * p) + 0.3f * Mathf.Sin(3 * p) + n) * _vol;
                for (int c = 0; c < channels; c++) data[i + c] = v;
            }
        }
    }
}
