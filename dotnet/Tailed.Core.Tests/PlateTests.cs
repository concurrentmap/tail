using System.Collections.Generic;
using Tailed.Core.Identity;
using Tailed.Core.Util;
using Xunit;

namespace Tailed.Core.Tests
{
    public class PlateTests
    {
        [Fact]
        public void Generator_IsDeterministicForSeed()
        {
            var a = new PlateGenerator(42);
            var b = new PlateGenerator(42);
            for (int i = 0; i < 100; i++) Assert.Equal(a.Next(), b.Next());
        }

        [Fact]
        public void Generator_SequenceIsPinned()
        {
            // Pinned in the Unity EditMode smoke test too: proves Mono/IL2CPP and .NET agree.
            Assert.Equal("BCH4025", new PlateGenerator(42).Next());
        }

        [Fact]
        public void Generator_IssuesUniqueWellFormedPlates()
        {
            var gen = new PlateGenerator(7);
            var seen = new HashSet<string>();
            for (int i = 0; i < 5000; i++)
            {
                string p = gen.Next();
                Assert.True(seen.Add(p));
                Assert.Equal(PlateFormat.Length, p.Length);
                for (int c = 0; c < 3; c++) Assert.Contains(p[c], PlateFormat.Letters);
                for (int c = 3; c < 7; c++) Assert.Contains(p[c], PlateFormat.Digits);
                Assert.DoesNotContain('O', p);
                Assert.DoesNotContain('I', p);
            }
        }

        [Theory]
        [InlineData("abc 1234", "ABC1234", 7)]
        [InlineData("ABC-12??", "ABC1234", 5)]
        [InlineData("???1234", "ABC1234", 4)]
        [InlineData("XYZ9999", "ABC1234", 0)]
        [InlineData("ABC", "ABC1234", 3)]
        public void PositionalMatches(string guess, string actual, int expected)
        {
            Assert.Equal(expected, PlateMatcher.PositionalMatches(guess, actual));
        }

        [Fact]
        public void Matcher_RequiresModelAndThreshold()
        {
            var m = new PlateMatcher(5);
            Assert.True(m.IsHit(3, "ABC 12??", 3, "ABC1234"));
            Assert.False(m.IsHit(4, "ABC 1234", 3, "ABC1234")); // wrong model
            Assert.False(m.IsHit(3, "ABC ????", 3, "ABC1234")); // too partial
        }

        [Fact]
        public void Rng_PickWeighted_RespectsWeights()
        {
            var rng = new Rng(1);
            var weights = new[] { 0f, 3f, 1f };
            var counts = new int[3];
            for (int i = 0; i < 40000; i++) counts[rng.PickWeighted(weights)]++;
            Assert.Equal(0, counts[0]);
            Assert.InRange(counts[1] / (double)counts[2], 2.7, 3.3);
        }
    }
}
