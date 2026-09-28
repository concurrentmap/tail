using System;

namespace Tailed.Core.Traffic
{
    /// <summary>Per-driver parameters for the Intelligent Driver Model.</summary>
    [Serializable]
    public struct IdmParams
    {
        /// <summary>Desired speed on this road (m/s). Usually speed limit × driver factor.</summary>
        public float DesiredSpeed;
        /// <summary>Desired time headway to leader (s).</summary>
        public float TimeHeadway;
        /// <summary>Jam distance: minimum bumper-to-bumper gap when stopped (m).</summary>
        public float MinGap;
        /// <summary>Maximum comfortable acceleration (m/s²).</summary>
        public float MaxAccel;
        /// <summary>Comfortable deceleration (m/s², positive).</summary>
        public float ComfortDecel;
        /// <summary>Acceleration exponent, conventionally 4.</summary>
        public float Delta;

        public static IdmParams Default(float desiredSpeed) => new IdmParams
        {
            DesiredSpeed = desiredSpeed,
            TimeHeadway = 1.5f,
            MinGap = 2.0f,
            MaxAccel = 1.4f,
            ComfortDecel = 2.0f,
            Delta = 4f,
        };
    }

    /// <summary>
    /// Intelligent Driver Model (Treiber et al. 2000). Longitudinal control for every
    /// NPC. Red lights, stop lines and blocked junctions are fed in as stationary leaders.
    /// </summary>
    public static class Idm
    {
        /// <summary>Physical braking limit; IDM can demand more in emergencies.</summary>
        public const float MaxBrake = 9f;

        /// <summary>Acceleration with no vehicle ahead within look-ahead.</summary>
        public static float FreeRoadAccel(in IdmParams p, float speed)
        {
            float v0 = Math.Max(p.DesiredSpeed, 0.1f);
            return p.MaxAccel * (1f - MathF.Pow(speed / v0, p.Delta));
        }

        /// <param name="speed">Own speed (m/s).</param>
        /// <param name="gap">Bumper-to-bumper distance to leader (m).</param>
        /// <param name="leaderSpeed">Leader speed (m/s); 0 for a stop line.</param>
        public static float Accel(in IdmParams p, float speed, float gap, float leaderSpeed)
        {
            float dv = speed - leaderSpeed; // closing speed, positive when approaching
            float sStar = p.MinGap + Math.Max(0f,
                speed * p.TimeHeadway + speed * dv / (2f * MathF.Sqrt(p.MaxAccel * p.ComfortDecel)));
            float s = Math.Max(gap, 0.01f);
            float interaction = sStar / s;
            float a = FreeRoadAccel(p, speed) - p.MaxAccel * interaction * interaction;
            return Math.Max(a, -MaxBrake);
        }
    }
}
