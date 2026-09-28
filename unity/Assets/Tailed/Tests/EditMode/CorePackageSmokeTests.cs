using NUnit.Framework;
using Tailed.Core.Identity;
using Tailed.Core.Traffic;

namespace Tailed.Tests
{
    /// <summary>
    /// Proves the core package compiles and behaves the same under Unity's compiler/runtime
    /// as under dotnet. Real logic tests live in dotnet/Tailed.Core.Tests.
    /// </summary>
    public class CorePackageSmokeTests
    {
        [Test]
        public void PlateGenerator_MatchesDotnetSequence()
        {
            // Same seed must give the same plates everywhere (replays, seeded matches).
            // Pinned in dotnet/Tailed.Core.Tests/PlateTests.cs too.
            Assert.AreEqual("BCH4025", new PlateGenerator(42).Next());
        }

        [Test]
        public void Idm_FreeRoad_FromRest()
        {
            var p = IdmParams.Default(14f);
            Assert.AreEqual(p.MaxAccel, Idm.FreeRoadAccel(p, 0f), 1e-3f);
        }
    }
}
