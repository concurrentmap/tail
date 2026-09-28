using Tailed.Core.Roads;
using Tailed.Core.Util;
using Xunit;

namespace Tailed.Core.Tests
{
    public class MapGridTests
    {
        static RoadNetwork Net() => TownGenerator.Generate(new TownConfig { Seed = 42 });

        [Fact]
        public void Cells_RunWestToEast_AndNorthToSouth()
        {
            var net = Net();
            var g = new MapGrid(net);
            var size = net.Size;
            Assert.Equal("A1", g.CellName(new Vec2(1f, size.Y - 1f)));                 // north-west corner
            Assert.Equal($"{MapGrid.ColName(g.Cols - 1)}{g.Rows}", g.CellName(new Vec2(size.X - 1f, 1f))); // south-east
            Assert.Equal("A1", g.CellName(new Vec2(-50f, size.Y + 50f)));              // off-map clamps
            for (int c = 0; c < g.Cols; c++)
            for (int r = 0; r < g.Rows; r++)
                Assert.Equal(MapGrid.ColName(c) + MapGrid.RowName(r), g.CellName(g.CellCentre(c, r)));
        }

        [Theory]
        [InlineData(0f, 1f, "north", "N")]
        [InlineData(1f, 0f, "east", "E")]
        [InlineData(0f, -1f, "south", "S")]
        [InlineData(-1f, 0f, "west", "W")]
        [InlineData(1f, 1f, "north-east", "NE")]
        [InlineData(-0.2f, -1f, "south", "S")]
        public void Headings_AreCompassWords(float x, float y, string word, string abbrev)
        {
            Assert.Equal(word, MapGrid.Heading(new Vec2(x, y)));
            Assert.Equal(abbrev, MapGrid.HeadingShort(new Vec2(x, y)));
        }
    }
}
