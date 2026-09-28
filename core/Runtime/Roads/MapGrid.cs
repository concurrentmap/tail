using System;
using Tailed.Core.Util;

namespace Tailed.Core.Roads
{
    /// <summary>
    /// Road-atlas grid over the town for callouts ("he's in C4, heading north up Elm"). Columns are
    /// letters west → east, rows are numbers north → south, like a printed street map. North is +Y
    /// (world +Z), which is also "up" on every in-game map.
    /// </summary>
    public sealed class MapGrid
    {
        public readonly int Cols, Rows;
        public readonly float CellSize;
        /// <summary>World position of the grid's south-west corner.</summary>
        public readonly Vec2 Origin;

        public MapGrid(RoadNetwork net, int cols = 8)
        {
            var size = net.Size;
            Cols = cols;
            CellSize = size.X / cols;
            Rows = Math.Max(1, (int)MathF.Round(size.Y / CellSize));
            Origin = Vec2.Zero;
        }

        public int Col(Vec2 p) => Math.Clamp((int)MathF.Floor((p.X - Origin.X) / CellSize), 0, Cols - 1);
        /// <summary>Row index from the north edge (0 = top row = "1").</summary>
        public int Row(Vec2 p) => Math.Clamp(Rows - 1 - (int)MathF.Floor((p.Y - Origin.Y) / CellSize), 0, Rows - 1);

        public static string ColName(int col) => ((char)('A' + col)).ToString();
        public static string RowName(int row) => (row + 1).ToString();

        /// <summary>"C4".</summary>
        public string CellName(Vec2 p) => ColName(Col(p)) + RowName(Row(p));

        /// <summary>World centre of a grid square.</summary>
        public Vec2 CellCentre(int col, int row) =>
            Origin + new Vec2((col + 0.5f) * CellSize, (Rows - row - 0.5f) * CellSize);

        static readonly string[] Names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        static readonly string[] Short = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        static int Octant(Vec2 dir)
        {
            float bearing = MathF.Atan2(dir.X, dir.Y) * 180f / MathF.PI; // 0 = north, 90 = east
            return ((int)MathF.Round(bearing / 45f) % 8 + 8) % 8;
        }

        /// <summary>Compass word for a travel direction: "north", "south-west"...</summary>
        public static string Heading(Vec2 dir) => Names[Octant(dir)];
        public static string HeadingShort(Vec2 dir) => Short[Octant(dir)];
        /// <summary>Compass bearing in degrees (0 = north, clockwise).</summary>
        public static float Bearing(Vec2 dir) => (MathF.Atan2(dir.X, dir.Y) * 180f / MathF.PI + 360f) % 360f;
    }
}
