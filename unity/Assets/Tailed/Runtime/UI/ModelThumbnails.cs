using System.Collections.Generic;
using Tailed.Core.Identity;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.UI
{
    /// <summary>
    /// Three-quarter thumbnails of every model, rendered once from the game's own meshes (for the flag
    /// form, motor pool and rentals), so players pick cars by the silhouettes they'll see on the road.
    /// </summary>
    public static class ModelThumbnails
    {
        static readonly Dictionary<int, Texture2D> Cache = new Dictionary<int, Texture2D>();

        public static Texture2D Get(int modelId)
        {
            if (Cache.TryGetValue(modelId, out var tex)) return tex;
            var root = new GameObject("ThumbStage");
            var stage = new Vector3(0f, -2000f, 0f);
            var view = VehicleView.Create(root.transform);
            view.Bind(90000 + modelId, new IdentityService(7).Create(modelId, 3));
            view.SetPose(stage, Vector3.right, 0f, Core.Traffic.VehicleFlags.None, 0f);
            var camGo = new GameObject("ThumbCam");
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.33f);
            cam.fieldOfView = 26f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 60f;
            var shape = VehicleMeshFactory.Shape(modelId);
            var target = stage + Vector3.up * shape.Height * 0.45f;
            cam.transform.position = target + new Vector3(-7f, 3.2f, -9f).normalized * (shape.Length * 2.45f);
            cam.transform.LookAt(target);
            cam.enabled = false;
            var rt = RenderTexture.GetTemporary(320, 180, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex = new Texture2D(320, 180, TextureFormat.RGB24, false) { name = "Thumb" + modelId };
            tex.ReadPixels(new Rect(0, 0, 320, 180), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(root);
            Cache[modelId] = tex;
            return tex;
        }

        /// <summary>
        /// Target overview portraits (GD §3 pickup): the Mark's exact car — model, paint, plate, driver
        /// and hat — from the front three-quarter, and a close-up of the driver through the windscreen.
        /// </summary>
        public static (Texture2D car, Texture2D driver) Portraits(VehicleIdentity id)
        {
            var root = new GameObject("PortraitStage");
            var stage = new Vector3(40f, -2000f, 0f);
            var view = VehicleView.Create(root.transform);
            view.Bind(91000, id);
            view.SetPose(stage, Vector3.right, 0f, Core.Traffic.VehicleFlags.None, 0f, steerFromMotion: false);
            var shape = VehicleMeshFactory.Shape(id.ModelId);
            var camGo = new GameObject("PortraitCam");
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.33f);
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 80f;
            cam.enabled = false;

            // Car faces +X; its left (driver's side) is +Z.
            var carTarget = stage + Vector3.up * shape.Height * 0.42f + Vector3.right * shape.Length * 0.08f;
            cam.fieldOfView = 24f;
            cam.transform.position = carTarget + new Vector3(1f, 0.32f, 0.62f).normalized * (shape.Length * 1.85f);
            cam.transform.LookAt(carTarget);
            var car = Render(cam, 640, 360);

            var head = view.transform.TransformPoint(new Vector3(shape.Eye.x, shape.Eye.y - 0.08f, shape.Eye.z));
            cam.fieldOfView = 20f;
            cam.transform.position = head + Vector3.right * 2.6f + Vector3.up * 0.25f + Vector3.forward * 0.5f;
            cam.transform.LookAt(head);
            var driver = Render(cam, 360, 360);
            Object.Destroy(root);
            return (car, driver);
        }

        static Texture2D Render(Camera cam, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false) { name = "Portrait" };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }
    }
}
