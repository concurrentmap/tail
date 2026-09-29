using Tailed.Cockpit;
using Tailed.Core.Rules;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// Time of day and weather for a round (GD §10). Night: dark sky, lit windows, headlights on,
    /// plates harder to read. Rain: streaks, droplets on mirrors, shorter visibility.
    /// </summary>
    public sealed class WorldEnvironment : MonoBehaviour
    {
        public static WorldEnvironment Instance { get; private set; }
        public TimeOfDay TimeOfDay { get; private set; }
        public Weather Weather { get; private set; }

        Material _sky;
        ParticleSystem _rain;
        Light[] _carLights;
        PlayerCar _litCar;
        static readonly int NightId = Shader.PropertyToID("_TailedNight");
        static readonly int RainId = Shader.PropertyToID("_TailedRain");

        void Awake() => Instance = this;

        void Start() => Apply(TimeOfDay.Day, Weather.Clear);

        public void Apply(TimeOfDay tod, Weather weather)
        {
            TimeOfDay = tod;
            Weather = weather;
            if (_sky == null && RenderSettings.skybox != null) RenderSettings.skybox = _sky = new Material(RenderSettings.skybox);
            var sun = RenderSettings.sun;
            float night, vis, head;
            Color sunColor, ambSky, ambEq, ambGround, fog, skyTop, skyHorizon;
            float sunIntensity, fogDensity;
            Quaternion sunRot;
            switch (tod)
            {
                case TimeOfDay.Dusk:
                    sunRot = Quaternion.Euler(9f, -60f, 0f); sunColor = new Color(1f, 0.62f, 0.38f); sunIntensity = 1.05f;
                    ambSky = new Color(0.55f, 0.46f, 0.56f); ambEq = new Color(0.62f, 0.46f, 0.42f); ambGround = new Color(0.25f, 0.2f, 0.2f);
                    fog = new Color(0.9f, 0.62f, 0.5f); fogDensity = 0.0003f; skyTop = new Color(0.32f, 0.33f, 0.6f); skyHorizon = new Color(1f, 0.64f, 0.46f);
                    night = 0.5f; vis = 0.85f; head = 1f;
                    break;
                case TimeOfDay.Night:
                    sunRot = Quaternion.Euler(52f, 30f, 0f); sunColor = new Color(0.55f, 0.66f, 1f); sunIntensity = 0.32f;
                    ambSky = new Color(0.13f, 0.16f, 0.3f); ambEq = new Color(0.1f, 0.1f, 0.17f); ambGround = new Color(0.05f, 0.05f, 0.07f);
                    fog = new Color(0.06f, 0.07f, 0.13f); fogDensity = 0.0005f; skyTop = new Color(0.02f, 0.03f, 0.09f); skyHorizon = new Color(0.09f, 0.11f, 0.2f);
                    night = 1f; vis = 0.6f; head = 1f;
                    break;
                default:
                    // Warm key light, softer blue fill: shapes read, colours stay rich (the PEAK look).
                    sunRot = Quaternion.Euler(46f, -35f, 0f); sunColor = new Color(1f, 0.92f, 0.78f); sunIntensity = 1.55f;
                    ambSky = new Color(0.5f, 0.62f, 0.82f); ambEq = new Color(0.52f, 0.54f, 0.52f); ambGround = new Color(0.32f, 0.29f, 0.25f);
                    fog = new Color(0.78f, 0.87f, 0.96f); fogDensity = 0.00022f; skyTop = new Color(0.36f, 0.58f, 0.9f); skyHorizon = new Color(0.78f, 0.87f, 0.96f);
                    night = 0f; vis = 1f; head = 0f;
                    break;
            }
            if (weather == Weather.Rain)
            {
                var grey = new Color(0.55f, 0.58f, 0.62f);
                sunIntensity *= 0.55f;
                fog = Color.Lerp(fog, grey * (1f - 0.8f * night), 0.6f);
                fogDensity *= 2.4f;
                skyTop = Color.Lerp(skyTop, grey * (1f - 0.8f * night), 0.7f);
                skyHorizon = Color.Lerp(skyHorizon, grey * (1f - 0.7f * night), 0.7f);
                ambSky = Color.Lerp(ambSky, grey * (1f - 0.7f * night), 0.4f);
                vis *= 0.75f;
                head = Mathf.Max(head, 0.6f);
            }
            if (sun != null) { sun.transform.rotation = sunRot; sun.color = sunColor; sun.intensity = sunIntensity; }
            RenderSettings.ambientSkyColor = ambSky;
            RenderSettings.ambientEquatorColor = ambEq;
            RenderSettings.ambientGroundColor = ambGround;
            RenderSettings.fogColor = fog;
            RenderSettings.fogDensity = fogDensity;
            if (_sky != null)
            {
                _sky.SetColor("_TopColor", skyTop);
                _sky.SetColor("_HorizonColor", skyHorizon);
                _sky.SetColor("_BottomColor", skyHorizon * 0.9f);
                _sky.SetFloat("_SunGlow", night > 0.9f ? 0.05f : 0.35f);
            }
            DynamicGI.UpdateEnvironment();
            Shader.SetGlobalFloat(NightId, night);
            Shader.SetGlobalFloat(RainId, weather == Weather.Rain ? 1f : 0f);
            Shader.SetGlobalFloat("_TailedGust", weather == Weather.Rain ? 1.2f : 0f); // trees thrash in the rain
            Tailed.Map.Clouds.Overcast = weather == Weather.Rain ? 1f : 0f;
            Tailed.Audio.Ambience.Set(night > 0.5f, weather == Weather.Rain);
            Optics.WorldVisibility = vis;
            VehicleView.Headlights = head;
            SetRain(weather == Weather.Rain);
            _litCar = null; // re-evaluate car lights
            Grade(tod, weather);
        }

        /// <summary>
        /// Colour grade on the scene's global volume: richer saturation and contrast, a warm white
        /// balance by day (warmer at dusk, cool at night), a little more bloom for lamps and beacons.
        /// </summary>
        void Grade(TimeOfDay tod, Weather weather)
        {
            var vol = FindAnyObjectByType<UnityEngine.Rendering.Volume>();
            if (vol == null) return;
            var profile = vol.profile; // an instance: runtime tweaks never touch the asset
            T Get<T>() where T : UnityEngine.Rendering.VolumeComponent => profile.TryGet<T>(out var c) ? c : profile.Add<T>(true);
            var ca = Get<UnityEngine.Rendering.Universal.ColorAdjustments>();
            var wb = Get<UnityEngine.Rendering.Universal.WhiteBalance>();
            var bloom = Get<UnityEngine.Rendering.Universal.Bloom>();
            bool rain = weather == Weather.Rain;
            float sat = tod == TimeOfDay.Night ? 5f : tod == TimeOfDay.Dusk ? 12f : 14f;
            ca.saturation.Override(rain ? sat - 12f : sat);
            ca.contrast.Override(tod == TimeOfDay.Night ? 8f : 12f);
            ca.postExposure.Override(tod == TimeOfDay.Night ? 0.25f : tod == TimeOfDay.Dusk ? 0.05f : 0.1f);
            wb.temperature.Override(tod == TimeOfDay.Night ? -12f : tod == TimeOfDay.Dusk ? 18f : rain ? -4f : 7f);
            wb.tint.Override(tod == TimeOfDay.Dusk ? 6f : 0f);
            bloom.intensity.Override(tod == TimeOfDay.Night ? 0.7f : 0.35f);
            bloom.threshold.Override(0.95f);
            bloom.scatter.Override(0.6f);
        }

        void SetRain(bool on)
        {
            if (on && _rain == null) _rain = BuildRain();
            if (_rain != null) { if (on) _rain.Play(); else _rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
        }

        static ParticleSystem BuildRain()
        {
            var go = new GameObject("Rain");
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startLifetime = 0.9f;
            main.startSpeed = 0f;
            main.startSize = 0.03f;
            main.startColor = new Color(0.7f, 0.75f, 0.85f, 0.35f);
            main.maxParticles = 6000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-1f); vel.y = new ParticleSystem.MinMaxCurve(-22f); vel.z = new ParticleSystem.MinMaxCurve(0.5f);
            var em = ps.emission;
            em.rateOverTime = 5000f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(40f, 1f, 40f);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.05f;
            r.lengthScale = 1f;
            r.material = new Material(Shader.Find("Tailed/Additive"));
            return ps;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (_rain != null && cam != null) _rain.transform.position = cam.transform.position + Vector3.up * 14f + cam.transform.forward * 8f;

            // Real headlight beams on the local car at night (other cars just glow).
            var car = PlayerCar.Local;
            if (car != _litCar)
            {
                _litCar = car;
                _carLights = null;
                if (car != null && VehicleView.Headlights > 0.5f)
                {
                    _carLights = new Light[2];
                    for (int i = 0; i < 2; i++)
                    {
                        var l = new GameObject("Headlight").AddComponent<Light>();
                        l.transform.SetParent(car.transform, false);
                        l.transform.localPosition = new Vector3(i == 0 ? -0.6f : 0.6f, car.Shape.Belt - 0.2f, car.Shape.Length * 0.5f + 0.1f);
                        l.transform.localRotation = Quaternion.Euler(6f, 0f, 0f);
                        l.type = LightType.Spot;
                        l.range = 55f;
                        l.spotAngle = 62f;
                        l.intensity = 6f;
                        l.color = new Color(1f, 0.93f, 0.8f);
                        _carLights[i] = l;
                    }
                }
            }
        }

        /// <summary>Dev / bridge: cycle through day, dusk, night and rain.</summary>
        public static string Cycle()
        {
            var e = Instance;
            int k = ((int)e.TimeOfDay + (e.Weather == Weather.Rain ? 3 : 0) + 1) % 6;
            e.Apply((TimeOfDay)(k % 3), k >= 3 ? Weather.Rain : Weather.Clear);
            return $"{e.TimeOfDay} {e.Weather}";
        }
    }
}
