using System;
using Tailed.Core.Traffic;
using Xunit;

namespace Tailed.Core.Tests
{
    public class IdmTests
    {
        static readonly IdmParams P = IdmParams.Default(desiredSpeed: 14f); // ~50 km/h

        [Fact]
        public void FreeRoad_AcceleratesFromRest_AtMaxAccel()
        {
            Assert.Equal(P.MaxAccel, Idm.FreeRoadAccel(P, 0f), 3);
        }

        [Fact]
        public void FreeRoad_ZeroAccel_AtDesiredSpeed()
        {
            Assert.Equal(0f, Idm.FreeRoadAccel(P, P.DesiredSpeed), 3);
        }

        [Fact]
        public void BrakesHard_ApproachingStoppedLeaderClosely()
        {
            float a = Idm.Accel(P, speed: 14f, gap: 10f, leaderSpeed: 0f);
            Assert.True(a < -P.ComfortDecel, $"expected hard braking, got {a}");
            Assert.True(a >= -Idm.MaxBrake);
        }

        [Fact]
        public void SingleCar_StopsBehindStopLine_WithoutOvershoot()
        {
            // Simulate a car approaching a red light 150 m away at 20 Hz.
            float x = 0f, v = 14f, stopLine = 150f, dt = 0.05f;
            for (int i = 0; i < 20 * 60; i++)
            {
                float a = Idm.Accel(P, v, stopLine - x, 0f);
                v = Math.Max(0f, v + a * dt);
                x += v * dt;
            }
            Assert.True(x <= stopLine, $"overshot stop line: x={x}");
            Assert.True(stopLine - x < P.MinGap + 0.5f, $"stopped too early: gap={stopLine - x}");
            Assert.True(v < 0.05f);
        }

        [Fact]
        public void Platoon_NoCollisions_WhenLeaderBrakes()
        {
            const int n = 10;
            const float len = 4.5f, dt = 0.05f;
            var x = new float[n];
            var v = new float[n];
            for (int i = 0; i < n; i++) { x[i] = -i * 30f; v[i] = 13f; }

            for (int step = 0; step < 20 * 90; step++)
            {
                float t = step * dt;
                var acc = new float[n];
                // Leader brakes to a stop between t=10s and t=20s, then resumes.
                acc[0] = t > 10f && t < 20f ? -3f : Idm.FreeRoadAccel(P, v[0]);
                for (int i = 1; i < n; i++)
                    acc[i] = Idm.Accel(P, v[i], x[i - 1] - x[i] - len, v[i - 1]);
                for (int i = 0; i < n; i++)
                {
                    v[i] = Math.Max(0f, v[i] + acc[i] * dt);
                    x[i] += v[i] * dt;
                }
                for (int i = 1; i < n; i++)
                    Assert.True(x[i - 1] - x[i] - len > 0f, $"collision between {i - 1} and {i} at t={t}");
            }
        }
    }
}
