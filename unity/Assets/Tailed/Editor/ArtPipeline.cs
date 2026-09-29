using System.Collections.Generic;
using System.IO;
using Tailed.Core.Identity;
using Tailed.Vehicles;
using UnityEditor;
using UnityEngine;

namespace Tailed.EditorTools
{
    /// <summary>
    /// Blender art pipeline glue. Unity is the authority on vehicle dimensions and anchor points
    /// (plates, mirrors, driver's eye): <see cref="WriteVehicleSpecs"/> writes them to
    /// tools/blender/specs.json; tools/blender/build_assets.py builds rounded models to match and
    /// exports FBX into Assets/Tailed/Resources/Art. The postprocessor keeps those imports readable (runtime
    /// recolouring) and free of imported materials.
    /// </summary>
    public static class ArtPipeline
    {
        [System.Serializable] class Spec
        {
            public int id; public string name, style;
            public float length, width, height, clearance, belt, wheelRadius, wheelBase;
            public float cabinRear, cabinFront, roofRear, roofFront, cabinHalfWidth, hood, trunk, cabRoofHeight;
            public bool pickupBed, cargoBox, roofRails, bus, boxBody;
            public Vector3 eye, frontPlate, rearPlate, leftMirror, rightMirror;
            public Vector2 plateSize;
        }
        [System.Serializable] class SpecFile { public List<Spec> models = new List<Spec>(); public float tagHeadlight, tagBrake, tagLeft, tagRight; }

        public static string WriteVehicleSpecs()
        {
            var file = new SpecFile
            {
                tagHeadlight = Tailed.Map.MeshBuilder.TagHeadlight, tagBrake = Tailed.Map.MeshBuilder.TagBrake,
                tagLeft = Tailed.Map.MeshBuilder.TagLeft, tagRight = Tailed.Map.MeshBuilder.TagRight,
            };
            foreach (var m in VehicleCatalog.Models)
            {
                var s = VehicleMeshFactory.Shape(m.Id);
                file.models.Add(new Spec
                {
                    id = m.Id, name = (m.Make + m.Name).Replace(" ", ""), style = s.Style,
                    length = s.Length, width = s.Width, height = s.Height, clearance = s.Clearance, belt = s.Belt,
                    wheelRadius = s.WheelRadius, wheelBase = s.WheelBase,
                    cabinRear = s.CabinRear, cabinFront = s.CabinFront, roofRear = s.RoofRear, roofFront = s.RoofFront,
                    cabinHalfWidth = s.CabinHalfWidth, hood = s.Hood, trunk = s.Trunk, cabRoofHeight = s.CabRoofHeight,
                    pickupBed = s.PickupBed, cargoBox = s.CargoBox, roofRails = s.RoofRails, bus = s.Bus, boxBody = s.BoxBody,
                    eye = s.Eye, frontPlate = s.FrontPlate, rearPlate = s.RearPlate, leftMirror = s.LeftMirror, rightMirror = s.RightMirror,
                    plateSize = new Vector2(Glyphs.PlateWidth, Glyphs.PlateHeight),
                });
            }
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../tools/blender/specs.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(file, true));
            return $"wrote {file.models.Count} vehicle specs to {path}";
        }
    }

    public static class ArtInspect
    {
        /// <summary>Dev: bounds, submeshes, colours and lamp-tag positions of the first imported vehicle.</summary>
        public static string Sedova()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Tailed/Resources/Art/Vehicles/SedovaClassic.fbx");
            if (mesh == null)
            {
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath("Assets/Tailed/Resources/Art/Vehicles/SedovaClassic.fbx"))
                    if (o is Mesh m) { mesh = m; break; }
            }
            if (mesh == null) return "no mesh";
            var cols = mesh.colors;
            var verts = mesh.vertices;
            Vector3 head = Vector3.zero, tail = Vector3.zero; int nh = 0, nt = 0, paint = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                if (Mathf.Abs(cols[i].a - Tailed.Map.MeshBuilder.TagHeadlight) < 0.02f) { head += verts[i]; nh++; }
                if (Mathf.Abs(cols[i].a - Tailed.Map.MeshBuilder.TagBrake) < 0.02f) { tail += verts[i]; nt++; }
                if (cols[i].r > 0.95f && cols[i].g < 0.05f && cols[i].b > 0.95f) paint++;
            }
            return $"verts={verts.Length} submeshes={mesh.subMeshCount} colors={cols.Length} paint={paint} bounds={mesh.bounds} " +
                   $"head={(nh > 0 ? head / nh : Vector3.zero)} tail={(nt > 0 ? tail / nt : Vector3.zero)} readable={mesh.isReadable}";
        }
    }

    /// <summary>Import settings for generated art: readable meshes, no materials/cameras/lights/animation.</summary>
    public sealed class ArtImporter : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (!assetPath.Replace('\\', '/').Contains("Assets/Tailed/Resources/Art/")) return;
            var mi = (ModelImporter)assetImporter;
            mi.isReadable = true;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.importCameras = false;
            mi.importLights = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importAnimation = false;
            mi.importBlendShapes = false;
            mi.bakeAxisConversion = true;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.importNormals = ModelImporterNormals.Import;
            mi.optimizeMeshVertices = false; // keep vertex colours per face corner as authored
        }
    }
}
