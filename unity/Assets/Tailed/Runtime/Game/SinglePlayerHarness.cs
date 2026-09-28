using System.Collections;
using System.Linq;
using Tailed.Cockpit;
using Tailed.Core.Roads;
using Tailed.Map;
using Tailed.Traffic;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// Offline drive: spawns the local car into live traffic. Used when no network session is
    /// running (dev, soak tests). Command line: -tailed-autodrive, -tailed-model N.
    /// </summary>
    public sealed class SinglePlayerHarness : MonoBehaviour
    {
        public int ModelId = 0, ColorId = 3;
        public bool Autodrive;

        public static PlayerCar SpawnLocal(int modelId, int colorId, Vector3 position, Quaternion rotation, bool autodrive)
        {
            var runner = TrafficRunner.Instance;
            var identity = runner.Sim.Identities.Create(modelId, colorId);
            var car = PlayerCar.Create(identity, position, rotation, local: true);
            var ext = runner.Sim.AddExternal(identity);
            car.SimId = ext.Id;
            runner.SetLocallyDriven(ext.Id, true);
            runner.Observers.Add(car.transform);
            car.gameObject.AddComponent<CarInput>().enabled = !autodrive;
            car.gameObject.AddComponent<DriveAssist>();
            if (autodrive) car.gameObject.AddComponent<Autopilot>();
            car.gameObject.AddComponent<MirrorSystem>().Init(car);
            PlateReadout.Create(car).transform.SetParent(car.transform, false);
            if (CameraDirector.Instance != null) { CameraDirector.Instance.Target = car; CameraDirector.Instance.SetFree(false); }
            return car;
        }

        public static (Vector3, Quaternion) SpawnPointOnLane(LaneGraph g, int laneIndexHint = 0)
        {
            var size = g.Network.Size;
            var centre = new Vector3(size.X * 0.5f, 0, size.Y * 0.5f);
            var lane = g.Lanes.Where(l => l.Index == 0 && l.Length > 70f && l.Class == RoadClass.Local)
                .OrderBy(l => (new Vector3(l.Start.X, 0, l.Start.Y) - centre).sqrMagnitude).Skip(laneIndexHint).First();
            var p = lane.PointAt(25f);
            return (new Vector3(p.X, 0.6f, p.Y), Quaternion.LookRotation(new Vector3(lane.Direction.X, 0, lane.Direction.Y)));
        }

        IEnumerator Start()
        {
            var args = System.Environment.GetCommandLineArgs();
            if (args.Contains("-tailed-autodrive") || args.Contains("-tailed-bot")) Autodrive = true;
            int mi = System.Array.IndexOf(args, "-tailed-model");
            if (mi >= 0 && mi + 1 < args.Length) int.TryParse(args[mi + 1], out ModelId);
            while (TrafficRunner.Instance == null || TrafficRunner.Instance.Sim == null) yield return null;
            var (pos, rot) = SpawnPointOnLane(TrafficRunner.Instance.Town.Lanes);
            SpawnLocal(ModelId, ColorId, pos, rot, Autodrive);
        }
    }
}
