using System;
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
                    // Like a real car: the rear axle tracks the path and the front leads into the turn,
                    // so the body yaws smoothly (and a little ahead of the corner) instead of pivoting on
                    // its centre. Lateral is the driver's line through the turn (cutting in or swinging wide).
                    var con = g.Connectors[connector];
                    float half = length * 0.3f; // half the wheelbase, near enough
                    var rear = TurnPathPoint(g, con, c - half);
                    var front = TurnPathPoint(g, con, c + half);
                    heading = (front - rear).Normalized;
                    position = (rear + front) * 0.5f + heading.PerpLeft * lateral;
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

        /// <summary>A point <paramref name="s"/> metres along lane-in → connector → lane-out, continuously.</summary>
        static Vec2 TurnPathPoint(LaneGraph g, Connector con, float s)
        {
            if (s < 0f)
            {
                var from = g.Lanes[con.FromLane];
                return from.PointAt(Math.Max(0f, from.Length + s));
            }
            if (s > con.Length)
            {
                var to = g.Lanes[con.ToLane];
                return to.PointAt(Math.Min(to.Length, s - con.Length));
            }
            return LaneGraph.PathPoint(con.Points, s, out _);
        }
    }
}
