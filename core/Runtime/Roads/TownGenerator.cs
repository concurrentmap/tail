using System;
using System.Collections.Generic;
using Tailed.Core.Util;

namespace Tailed.Core.Roads
{
    [Serializable]
    public sealed class TownConfig
    {
        public ulong Seed = 1;
        public int CellsX = 16, CellsY = 16;
        public float CellSize = 100f;
        /// <summary>Max displacement of local-street lattice points (m). Arterials stay straight.</summary>
        public float Jitter = 14f;
        /// <summary>Every Nth lattice line (and the town boundary) is an arterial.</summary>
        public int ArterialEvery = 4;
        /// <summary>Downtown radius as a fraction of town width.</summary>
        public float DowntownRadius = 0.2f;
        /// <summary>Industrial zone radius around one town corner, as a fraction of town width.</summary>
        public float IndustrialRadius = 0.32f;
        public int ParkCount = 2;
        /// <summary>Probability of removing a local street, merging two cells into one block.</summary>
        public float SuburbRemoval = 0.3f, IndustrialRemoval = 0.55f;
        public int PoiCount = 30;
    }

    /// <summary>
    /// Fictional town layout: a jittered lattice of streets on a straight arterial grid,
    /// thinned per district (dense downtown grid, irregular suburbs, big industrial blocks,
    /// parks). Always connected, never has dead ends (cul-de-sacs are a later feature).
    /// </summary>
    public static class TownGenerator
    {
        public static RoadNetwork Generate(TownConfig cfg)
        {
            if (cfg.CellsX % cfg.ArterialEvery != 0 || cfg.CellsY % cfg.ArterialEvery != 0)
                throw new ArgumentException("Cell counts must be multiples of ArterialEvery so the boundary is arterial.");

            var rng = new Rng(cfg.Seed);
            var net = new RoadNetwork { CellsX = cfg.CellsX, CellsY = cfg.CellsY, CellSize = cfg.CellSize };
            int nx = cfg.CellsX + 1, ny = cfg.CellsY + 1;
            int NodeId(int i, int j) => j * nx + i;
            bool ArterialCol(int i) => i % cfg.ArterialEvery == 0;
            bool ArterialRow(int j) => j % cfg.ArterialEvery == 0;

            // 1. Lattice points. Points on an arterial line are only jittered along it.
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                float jx = ArterialCol(i) ? 0f : rng.Range(-cfg.Jitter, cfg.Jitter);
                float jy = ArterialRow(j) ? 0f : rng.Range(-cfg.Jitter, cfg.Jitter);
                net.Nodes.Add(new RoadNode { Id = NodeId(i, j), Position = new Vec2(i * cfg.CellSize + jx, j * cfg.CellSize + jy) });
            }

            // 2. Districts.
            var size = net.Size;
            var center = size * 0.5f;
            Vec2 industrialCorner = new Vec2(rng.NextInt(2) * size.X, rng.NextInt(2) * size.Y);
            for (int j = 0; j < cfg.CellsY; j++)
            for (int i = 0; i < cfg.CellsX; i++)
            {
                var c = new Cell { X = i, Y = j };
                c.Corners[0] = NodeId(i, j);
                c.Corners[1] = NodeId(i + 1, j);
                c.Corners[2] = NodeId(i + 1, j + 1);
                c.Corners[3] = NodeId(i, j + 1);
                var mid = new Vec2((i + 0.5f) * cfg.CellSize, (j + 0.5f) * cfg.CellSize);
                if (Vec2.Distance(mid, center) < cfg.DowntownRadius * size.X) c.District = District.Downtown;
                else if (Vec2.Distance(mid, industrialCorner) < cfg.IndustrialRadius * size.X) c.District = District.Industrial;
                else c.District = District.Suburb;
                net.Cells.Add(c);
            }
            Cell CellAt(int i, int j) => i >= 0 && j >= 0 && i < cfg.CellsX && j < cfg.CellsY ? net.Cells[j * cfg.CellsX + i] : null;

            // Parks: 2×2 cell squares inside the suburbs, not touching an arterial line's interior
            // (so each park is a clean block bounded by streets).
            for (int p = 0, attempts = 0; p < cfg.ParkCount && attempts < 200; attempts++)
            {
                int i = rng.NextInt(cfg.CellsX - 1), j = rng.NextInt(cfg.CellsY - 1);
                if (ArterialCol(i + 1) || ArterialRow(j + 1)) continue;
                var quad = new[] { CellAt(i, j), CellAt(i + 1, j), CellAt(i, j + 1), CellAt(i + 1, j + 1) };
                if (Array.Exists(quad, c => c.District != District.Suburb)) continue;
                foreach (var c in quad) c.District = District.Park;
                p++;
            }

            // 3. Candidate edges: every lattice segment. Horizontal edge (i,j)-(i+1,j) borders
            //    cells (i,j-1) below and (i,j) above; vertical (i,j)-(i,j+1) borders (i-1,j) and (i,j).
            var candidates = new List<(int a, int b, RoadClass cls, Cell c1, Cell c2, int side1, int side2)>();
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < cfg.CellsX; i++)
                candidates.Add((NodeId(i, j), NodeId(i + 1, j), ArterialRow(j) ? RoadClass.Arterial : RoadClass.Local,
                                CellAt(i, j - 1), CellAt(i, j), 2 /*N of lower*/, 0 /*S of upper*/));
            for (int j = 0; j < cfg.CellsY; j++)
            for (int i = 0; i < nx; i++)
                candidates.Add((NodeId(i, j), NodeId(i, j + 1), ArterialCol(i) ? RoadClass.Arterial : RoadClass.Local,
                                CellAt(i - 1, j), CellAt(i, j), 1 /*E of left*/, 3 /*W of right*/));

            // 4. Thin local streets per district. Adjacency as edge-index sets for cheap removal checks.
            var alive = new bool[candidates.Count];
            var incident = new List<int>[net.Nodes.Count];
            for (int n = 0; n < incident.Length; n++) incident[n] = new List<int>();
            for (int e = 0; e < candidates.Count; e++)
            {
                alive[e] = true;
                incident[candidates[e].a].Add(e);
                incident[candidates[e].b].Add(e);
            }
            int Degree(int n) { int d = 0; foreach (var e in incident[n]) if (alive[e]) d++; return d; }

            var order = new int[candidates.Count];
            for (int e = 0; e < order.Length; e++) order[e] = e;
            for (int k = order.Length - 1; k > 0; k--) { int r = rng.NextInt(k + 1); (order[k], order[r]) = (order[r], order[k]); }

            // Parks first, all at once: removing their interior streets one by one would pass
            // through a dead-end state and be rejected.
            for (int e = 0; e < candidates.Count; e++)
            {
                var c = candidates[e];
                if (c.c1 != null && c.c2 != null && c.c1.District == District.Park && c.c2.District == District.Park)
                    alive[e] = false;
            }

            foreach (int e in order)
            {
                if (!alive[e]) continue;
                var cand = candidates[e];
                if (cand.cls == RoadClass.Arterial) continue;
                double roll = rng.NextDouble();
                if (roll >= RemovalChance(cfg, cand.c1, cand.c2)) continue;
                int da = Degree(cand.a) - 1, db = Degree(cand.b) - 1;
                if (da == 1 || db == 1) continue; // would leave a dead end
                alive[e] = false;
                if (!Connected(candidates, alive, incident, net.Nodes.Count)) alive[e] = true;
            }

            // 5. Emit surviving edges.
            foreach (var c in net.Cells) for (int s = 0; s < 4; s++) c.Sides[s] = -1;
            for (int e = 0; e < candidates.Count; e++)
            {
                if (!alive[e]) continue;
                var cand = candidates[e];
                var edge = new RoadEdge { Id = net.Edges.Count, A = cand.a, B = cand.b, Class = cand.cls };
                net.Edges.Add(edge);
                net.Nodes[cand.a].Edges.Add(edge.Id);
                net.Nodes[cand.b].Edges.Add(edge.Id);
                if (cand.c1 != null) cand.c1.Sides[cand.side1] = edge.Id;
                if (cand.c2 != null) cand.c2.Sides[cand.side2] = edge.Id;
            }
            NameStreets(net, ref rng);
            PlacePois(cfg, net, ref rng);
            return net;
        }

        static readonly string[] ArterialNames =
        {
            "Main", "Harbor", "Central", "Grand", "Union", "Market", "Station", "Liberty", "Kings", "Queens", "Park", "Mill",
        };
        static readonly string[] LocalNames =
        {
            "Maple", "Birch", "Cedar", "Willow", "Aspen", "Juniper", "Hazel", "Poplar", "Linden", "Holly", "Rowan", "Alder",
            "Chestnut", "Laurel", "Magnolia", "Sycamore", "Hawthorn", "Elder", "Primrose", "Clover", "Bramble", "Fern", "Heather",
            "Violet", "Orchard", "Meadow", "Brook", "Sparrow", "Robin", "Finch", "Wren", "Heron",
        };

        /// <summary>
        /// One name per lattice line: arterials get "Main St"-style names, local lines tree/nature names.
        /// North-south streets are Avenues/Roads, east-west ones Streets/Lanes, so a name hints at direction.
        /// </summary>
        static void NameStreets(RoadNetwork net, ref Rng rng)
        {
            int nx = net.CellsX + 1;
            var art = new System.Collections.Generic.List<string>(ArterialNames);
            var loc = new System.Collections.Generic.List<string>(LocalNames);
            for (int k = art.Count - 1; k > 0; k--) { int r = rng.NextInt(k + 1); (art[k], art[r]) = (art[r], art[k]); }
            for (int k = loc.Count - 1; k > 0; k--) { int r = rng.NextInt(k + 1); (loc[k], loc[r]) = (loc[r], loc[k]); }
            var byLine = new System.Collections.Generic.Dictionary<(bool vertical, int index), string>();
            int ai = 0, li = 0;
            foreach (var e in net.Edges)
            {
                int ia = e.A % nx, ja = e.A / nx, ib = e.B % nx;
                bool vertical = ia == ib;
                var key = (vertical, vertical ? ia : ja);
                if (!byLine.TryGetValue(key, out var name))
                {
                    name = e.Class == RoadClass.Arterial
                        ? $"{art[ai++ % art.Count]} {(vertical ? "Ave" : "St")}"
                        : $"{loc[li++ % loc.Count]} {(vertical ? "Rd" : "Ln")}";
                    byLine[key] = name;
                }
                e.StreetName = name;
            }
        }

        static readonly PoiType[] DowntownPois = { PoiType.Diner, PoiType.Pharmacy, PoiType.Bakery, PoiType.Bank, PoiType.Laundromat };
        static readonly PoiType[] SuburbPois = { PoiType.Diner, PoiType.Laundromat, PoiType.CarWash, PoiType.Pharmacy, PoiType.Florist, PoiType.Hardware, PoiType.Bakery, PoiType.Motel };
        static readonly PoiType[] IndustrialPois = { PoiType.Warehouse, PoiType.Depot, PoiType.ScrapYard };
        static readonly string[] Adjectives = { "Sunny", "Lucky", "Golden", "Happy", "Big", "Little", "Old", "Jolly", "Rusty", "Blue", "Maple", "Corner" };

        /// <summary>
        /// Businesses spread across the town, at least two cells apart. Guarantees the special
        /// types the game needs: petrol stations (plate swaps), rental lots and chop shops.
        /// </summary>
        static void PlacePois(TownConfig cfg, RoadNetwork net, ref Rng rng)
        {
            var order = new List<int>();
            for (int i = 0; i < net.Cells.Count; i++) order.Add(i);
            for (int k = order.Count - 1; k > 0; k--) { int r = rng.NextInt(k + 1); (order[k], order[r]) = (order[r], order[k]); }

            var mandatory = new Queue<PoiType>(new[]
            {
                PoiType.Petrol, PoiType.Petrol, PoiType.Petrol, PoiType.RentalLot, PoiType.RentalLot,
                PoiType.ChopShop, PoiType.ChopShop, PoiType.Motel,
            });
            foreach (int ci in order)
            {
                if (net.Pois.Count >= cfg.PoiCount) break;
                var cell = net.Cells[ci];
                if (cell.District == District.Park) continue;
                bool tooClose = false;
                foreach (var p in net.Pois)
                {
                    var other = net.Cells[p.CellIndex];
                    if (Math.Abs(other.X - cell.X) < 2 && Math.Abs(other.Y - cell.Y) < 2) { tooClose = true; break; }
                }
                if (tooClose) continue;

                // Front onto a street; prefer local streets outside downtown (quieter, more tailing drama).
                int side = -1;
                for (int k = 0, start = rng.NextInt(4); k < 4; k++)
                {
                    int s = (start + k) % 4;
                    if (cell.Sides[s] < 0) continue;
                    if (side < 0 || (net.Edges[cell.Sides[s]].Class == RoadClass.Local && net.Edges[cell.Sides[side]].Class != RoadClass.Local)) side = s;
                }
                if (side < 0) continue;

                PoiType type;
                if (mandatory.Count > 0 && cell.District != District.Downtown) type = mandatory.Dequeue();
                else if (cell.District == District.Downtown) type = DowntownPois[rng.NextInt(DowntownPois.Length)];
                else if (cell.District == District.Industrial) type = IndustrialPois[rng.NextInt(IndustrialPois.Length)];
                else type = SuburbPois[rng.NextInt(SuburbPois.Length)];

                net.Pois.Add(new Poi
                {
                    Id = net.Pois.Count, Type = type, CellIndex = ci, Side = side,
                    Name = $"{Adjectives[rng.NextInt(Adjectives.Length)]} {PoiNoun(type)}",
                });
            }
        }

        public static string PoiNoun(PoiType t)
        {
            switch (t)
            {
                case PoiType.Petrol: return "Gas";
                case PoiType.CarWash: return "Car Wash";
                case PoiType.RentalLot: return "Rentals";
                case PoiType.ChopShop: return "Body Shop";
                case PoiType.ScrapYard: return "Scrap";
                default: return t.ToString();
            }
        }

        static double RemovalChance(TownConfig cfg, Cell c1, Cell c2)
        {
            if (c1 == null || c2 == null) return 0;
            if (c1.District == District.Downtown || c2.District == District.Downtown) return 0;
            if (c1.District == District.Park || c2.District == District.Park) return 0; // keep park edges as streets
            if (c1.District == District.Industrial || c2.District == District.Industrial) return cfg.IndustrialRemoval;
            return cfg.SuburbRemoval;
        }

        static bool Connected(List<(int a, int b, RoadClass cls, Cell c1, Cell c2, int side1, int side2)> cands,
                              bool[] alive, List<int>[] incident, int nodeCount)
        {
            var seen = new bool[nodeCount];
            var stack = new Stack<int>();
            int start = -1, withEdges = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                bool has = false;
                foreach (var e in incident[n]) if (alive[e]) { has = true; break; }
                if (!has) continue;
                withEdges++;
                if (start < 0) start = n;
            }
            if (start < 0) return true;
            stack.Push(start);
            seen[start] = true;
            int reached = 1;
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                foreach (var e in incident[n])
                {
                    if (!alive[e]) continue;
                    int m = cands[e].a == n ? cands[e].b : cands[e].a;
                    if (seen[m]) continue;
                    seen[m] = true;
                    reached++;
                    stack.Push(m);
                }
            }
            return reached == withEdges;
        }
    }
}
