using Tailed.Core.Roads;
using Tailed.Core.Util;

namespace Tailed.Core.Traffic
{
    /// <summary>
    /// Reconstructs a vehicle's centre pose from its lane-graph state. Used by the host sim and by
    /// clients decoding snapshots — the lane graph (rebuilt from the seed) is the compression.
    /// </summary>
    public static class VehiclePose
    {
        public static void Compute(LaneGraph g, VehicleMode mode, int lane, int connector, int bay, float s, float length,
                                   float lateral, out Vec2 position, out Vec2 heading)
        {
            float c = s - length * 0.5f;
            switch (mode)
            {
                case VehicleMode.Lane:
                {
                    var l = g.Lanes[lane];
                    heading = l.Direction;
                    position = l.PointAt(c) + l.Direction.PerpLeft * lateral;
                    return;
                }
                case VehicleMode.Connector:
                {
                    var con = g.Connectors[connector];
                    if (c < 0f)
                    {
                        var from = g.Lanes[con.FromLane];
                        position = from.PointAt(from.Length + c);
                        heading = (LaneGraph.PathPoint(con.Points, s, out _) - position).Normalized;
                    }
                    else position = LaneGraph.PathPoint(con.Points, c, out heading);
                    return;
                }
                case VehicleMode.ParkIn:
                case VehicleMode.Parked:
                {
                    var b = g.Bays[bay];
                    if (c >= 0f) { position = LaneGraph.PathPoint(b.EntryPath, c, out heading); return; }
                    var l = g.Lanes[b.AccessLane];
                    heading = l.Direction;
                    position = l.PointAt(b.EntryS + c);
                    return;
                }
                case VehicleMode.ParkOut:
                {
                    var b = g.Bays[bay];
                    if (c < 0f) { position = b.Position + b.Heading * c; heading = b.Heading; return; }
                    position = LaneGraph.PathPoint(b.ExitPath, c, out heading);
                    return;
                }
                default:
                    position = Vec2.Zero;
                    heading = new Vec2(1, 0);
                    return;
            }
        }
    }
}
