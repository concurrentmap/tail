using System.Collections.Generic;
using System.Linq;
using Tailed.Core.Identity;
using Tailed.Core.Rules;
using Tailed.Core.Util;
using Tailed.Net;
using Tailed.Traffic;
using Tailed.Vehicles;
using UnityEngine;

namespace Tailed.Game
{
    /// <summary>
    /// Scripted player for automated end-to-end games (-tailed-bot). Makes the choices a player would,
    /// drives with the autopilot: the Mark visits its checkpoints in order and parks in the bays; Tails
    /// roam, or follow the Mark when the host allows test cheats, and pin where the Mark stops.
    /// </summary>
    public sealed class BotBrain : MonoBehaviour
    {
        ClientGame _c;
        float _timer, _markStill;
        Phase _phase = (Phase)255;
        int _driveTarget = -1, _flaggedAt = -1;
        Rng _rng = new Rng((ulong)System.Environment.TickCount);

        void Start() => _c = ClientGame.Instance;

        void Update()
        {
            if (_c == null || !_c.Connected) return;
            if (_c.Phase != _phase) { _phase = _c.Phase; _timer = 0f; _driveTarget = -1; }
            _timer += Time.deltaTime;
            switch (_c.Phase)
            {
                case Phase.MotorPool:
                    _suspects.Clear();
                    if (_c.MyRole == Role.Tail && _timer > 1f && _timer < 1.2f)
                    {
                        // Something affordable, preferring common shapes (blend in).
                        int model = new[] { 1, 2, 0, 3, 4, 5, 6, 7 }.First(m => Match.Cost(m, 5) <= 5 || m == 7);
                        _c.PickVehicle(model, 4 + _rng.NextInt(8));
                        _c.SetReady(true);
                    }
                    break;
                case Phase.Briefing:
                    if (_c.MyRole == Role.Mark && _timer > 2f && _timer < 2.2f && _c.Candidates.Count > 0) _c.SubmitRoute(_c.Candidates);
                    break;
                case Phase.Driving:
                    if (_c.MyRole == Role.Mark) DriveMark(); else DriveTail();
                    break;
                case Phase.Marking:
                    // Mark the spots where we watched the Mark sit still (one each, a moment apart).
                    if (_c.MyRole == Role.Tail && _timer > 2f + _c.MyId && _suspects.Count > 0 && Time.time - _lastPin > 1.5f && _c.Pins.Count < _c.MarksAllowed)
                    {
                        var spot = _suspects[0];
                        _suspects.RemoveAt(0);
                        _lastPin = Time.time;
                        if (!_c.Pins.Any(p => (p.pos - spot).magnitude < 40f)) _c.AddPin(spot);
                    }
                    break;

            }
        }

        void DriveMark()
        {
            var car = PlayerCar.Local;
            var ap = car != null ? car.GetComponent<Autopilot>() : null;
            if (ap == null || _c.NextCheckpoint >= _c.Route.Count) return;
            int poi = _c.Route[_c.NextCheckpoint];
            var g = TrafficRunner.Instance.Town.Lanes;
            if (_driveTarget != poi)
            {
                _driveTarget = poi;
                _parkedFor = 0f;
                ap.ParkAtSite(poi);
            }
            // Parked but the stop isn't counting (slightly outside the bay): pull in again.
            _parkedFor = ap.Arrived && !_c.Dwelling ? _parkedFor + Time.deltaTime : 0f;
            if (_parkedFor > 6f) { _parkedFor = 0f; ap.ParkAtSite(poi); }
            // At a checkpoint: file one flag on the nearest car behind us (reading its plate, like a player would).
            if (_c.FlagWindowOpen && _flaggedAt != _c.NextCheckpoint && _c.FlagTokens > 0)
            {
                _flaggedAt = _c.NextCheckpoint;
                VehicleView best = null;
                float bestD = 35f;
                foreach (var v in FindObjectsByType<VehicleView>(FindObjectsSortMode.None))
                {
                    if (!v.gameObject.activeInHierarchy || v.VehicleId < 0) continue;
                    var d = v.transform.position - car.transform.position;
                    if (Vector3.Dot(d, car.transform.forward) > 0f) continue;
                    if (d.magnitude < bestD) { bestD = d.magnitude; best = v; }
                }
                if (best != null) _c.Flag(best.Identity.ModelId, best.Identity.Plate);
            }
        }

        void DriveTail()
        {
            var car = PlayerCar.Local;
            var ap = car != null ? car.GetComponent<Autopilot>() : null;
            if (ap == null) return;
            if (_c.BotCheatMarkVehicle < 0) return; // no cheat: just roam
            Transform mark = null;
            if (TrafficReplica.Instance != null && TrafficReplica.Instance.TryGetView(_c.BotCheatMarkVehicle, out var rv)) mark = rv.transform;
            else if (TrafficRunner.Instance.TryGetView(_c.BotCheatMarkVehicle, out var hv)) mark = hv.transform;
            ap.FollowTarget = mark;
            if (mark == null) return;

            // The Mark has been still for a while off the road: remember the spot for the marking.
            _markStill = Vector3.Distance(mark.position, _lastMarkPos) < 0.5f ? _markStill + Time.deltaTime : 0f;
            _lastMarkPos = mark.position;
            var here = new Vector2(mark.position.x, mark.position.z);
            var sim = TrafficRunner.Instance.Sim;
            bool offRoad = sim != null && !sim.Index.Project(new Vec2(here.x, here.y), new Vec2(mark.forward.x, mark.forward.z), out _);
            if (_markStill > 6f && offRoad && !_suspects.Any(p => (p - here).magnitude < 40f)) _suspects.Add(here);
        }

        readonly List<Vector2> _suspects = new List<Vector2>();

        Vector3 _lastMarkPos;
        float _parkedFor;
        float _lastPin;
    }
}
