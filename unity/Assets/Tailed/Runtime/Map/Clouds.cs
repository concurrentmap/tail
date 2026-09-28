using Tailed.Core.Roads;
using Tailed.Core.Util;
using UnityEngine;

namespace Tailed.Map
{
    /// <summary>
    /// A few fat, soft low-poly clouds drifting slowly over town. Purely cosmetic and local (not
    /// synchronised): they only exist so the sky isn't a static gradient.
    /// </summary>
    public sealed class Clouds : MonoBehaviour
    {
        Vector2 _min, _max;
        Vector3 _wind;
        /// <summary>0 clear .. 1 overcast (rain darkens and thickens them).</summary>
        public static float Overcast;
        Material _mat;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public static void Build(Transform parent, RoadNetwork net, int seed)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var n in net.Nodes)
            {
                min = Vector2.Min(min, new Vector2(n.Position.X, n.Position.Y));
                max = Vector2.Max(max, new Vector2(n.Position.X, n.Position.Y));
            }
            var go = new GameObject("Clouds");
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<Clouds>();
            c._min = min - Vector2.one * 500f;
            c._max = max + Vector2.one * 500f;
            var rng = new Rng((ulong)seed * 31UL + 5UL);
            float ang = rng.Range(0f, Mathf.PI * 2f);
            c._wind = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * rng.Range(1.5f, 3f);
            c._mat = new Material(TownBuilder.Instance.ToonMaterial) { name = "Clouds" };
            c._mat.SetFloat("_AmbientStrength", 1.2f);
            c._mat.SetFloat("_ShadeSoftness", 0.9f);
            c._mat.SetColor("_ShadowTint", new Color(0.8f, 0.84f, 0.95f));

            for (int i = 0; i < 28; i++)
            {
                var mb = new MeshBuilder();
                int puffs = 4 + rng.NextInt(4);
                float len = rng.Range(40f, 90f);
                for (int k = 0; k < puffs; k++)
                {
                    float t = puffs == 1 ? 0.5f : k / (float)(puffs - 1);
                    float r = rng.Range(10f, 18f) * (1f - Mathf.Abs(t - 0.5f) * 0.8f);
                    var at = new Vector3((t - 0.5f) * len, rng.Range(-2f, 4f), rng.Range(-8f, 8f));
                    mb.SmoothSphere(at, new Vector3(r, r * 0.6f, r * 0.9f), Color.white, 16, 10);
                }
                var cloud = new GameObject("Cloud");
                cloud.transform.SetParent(go.transform, false);
                cloud.transform.position = new Vector3(rng.Range(c._min.x, c._max.x), rng.Range(170f, 240f), rng.Range(c._min.y, c._max.y));
                cloud.transform.rotation = Quaternion.Euler(0f, rng.Range(0f, 360f), 0f);
                cloud.AddComponent<MeshFilter>().sharedMesh = mb.Build("Cloud");
                var mr = cloud.AddComponent<MeshRenderer>();
                mr.sharedMaterial = c._mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
        }

        void Update()
        {
            var step = _wind * (1f + Overcast) * Time.deltaTime;
            float w = _max.x - _min.x, h = _max.y - _min.y;
            foreach (Transform t in transform)
            {
                var p = t.position + step;
                if (p.x > _max.x) p.x -= w; else if (p.x < _min.x) p.x += w;
                if (p.z > _max.y) p.z -= h; else if (p.z < _min.y) p.z += h;
                t.position = p;
            }
            _mat.SetColor(BaseColorId, Color.Lerp(Color.white, new Color(0.62f, 0.64f, 0.68f), Overcast));
        }
    }
}
