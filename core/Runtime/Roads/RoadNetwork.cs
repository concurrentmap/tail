using System.Collections.Generic;
using Tailed.Core.Util;

namespace Tailed.Core.Roads
{
    public enum RoadClass : byte { Local, Arterial }

    public enum District : byte { Downtown, Suburb, Industrial, Park }

    public static class RoadSpec
    {
        public const float SidewalkWidth = 3f;

        public static int LanesPerDirection(RoadClass c) => c == RoadClass.Arterial ? 2 : 1;
        public static float LaneWidth(RoadClass c) => c == RoadClass.Arterial ? 3.5f : 3.2f;
        /// <summary>m/s: arterial 50 km/h, local 40 km/h.</summary>
        public static float SpeedLimit(RoadClass c) => c == RoadClass.Arterial ? 13.9f : 11.1f;
        /// <summary>Centreline to kerb.</summary>
        public static float HalfWidth(RoadClass c) => LanesPerDirection(c) * LaneWidth(c);
    }

    public sealed class RoadNode
    {
        public int Id;
        public Vec2 Position;
        /// <summary>Incident edge ids. Empty for lattice points swallowed by a merged block.</summary>
        public readonly List<int> Edges = new List<int>();
        public int Degree => Edges.Count;
    }

    public sealed class RoadEdge
    {
        public int Id, A, B;
        public RoadClass Class;
        /// <summary>Street name, shared by every edge on the same lattice line (e.g. "Maple Rd").</summary>
        public string StreetName;

        public int Other(int node) => node == A ? B : A;
    }

    /// <summary>One lattice cell. Adjacent cells whose shared edge was removed form one block.</summary>
    public sealed class Cell
    {
        public int X, Y;
        public District District;
        /// <summary>Node ids counter-clockwise: SW, SE, NE, NW.</summary>
        public readonly int[] Corners = new int[4];
        /// <summary>Edge id per side S, E, N, W (side k runs Corners[k] → Corners[k+1]); -1 if no road.</summary>
        public readonly int[] Sides = new int[4];
    }

    public enum PoiType : byte
    {
        Petrol, Diner, Laundromat, CarWash, Pharmacy, Bakery, Florist, Hardware, Bank, Motel,
        Warehouse, Depot, ScrapYard,
        /// <summary>Tails swap cars here (GD §5). Not a checkpoint.</summary>
        RentalLot,
        /// <summary>Burned Tails get a new car and plate here (GD §4). Not a checkpoint.</summary>
        ChopShop,
    }

    /// <summary>A business with a drive-through forecourt on one street side of a cell.</summary>
    public sealed class Poi
    {
        public int Id;
        public PoiType Type;
        public string Name;
        public int CellIndex;
        /// <summary>Cell side (0..3) the forecourt faces; the street is <see cref="Cell.Sides"/>[Side].</summary>
        public int Side;
        public bool IsCheckpointCandidate => Type != PoiType.RentalLot && Type != PoiType.ChopShop;
    }

    /// <summary>Topology + geometry of the town's roads. Built by <see cref="TownGenerator"/>.</summary>
    public sealed class RoadNetwork
    {
        public readonly List<RoadNode> Nodes = new List<RoadNode>();
        public readonly List<RoadEdge> Edges = new List<RoadEdge>();
        public readonly List<Cell> Cells = new List<Cell>();
        public readonly List<Poi> Pois = new List<Poi>();
        public int CellsX, CellsY;
        public float CellSize;

        public Vec2 Size => new Vec2(CellsX * CellSize, CellsY * CellSize);

        public Vec2 Dir(RoadEdge e, int fromNode) =>
            (Nodes[e.Other(fromNode)].Position - Nodes[fromNode].Position).Normalized;

        public float Length(RoadEdge e) => Vec2.Distance(Nodes[e.A].Position, Nodes[e.B].Position);
    }
}
