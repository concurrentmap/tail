using Tailed.Core.Util;

namespace Tailed.Core.Traffic
{
    /// <summary>
    /// NPC personalities (GD §9). The quirky ones generate the false positives the Mark has to
    /// see through: lost drivers loop and double back, aggressive ones run ambers, distracted
    /// ones forget indicators.
    /// </summary>
    public enum DriverType : byte { Normal, Cautious, Aggressive, Lost, Distracted }

    public sealed class DriverProfile
    {
        public DriverType Type;
        public float SpeedFactor, TimeHeadway, MinGap, MaxAccel, ComfortDecel;
        /// <summary>MOBIL politeness 0..1 — how much others' disadvantage counts against a lane change.</summary>
        public float Politeness;
        /// <summary>Seconds of clear gap needed before pulling into/across priority traffic.</summary>
        public float CriticalGap;
        /// <summary>Probability of carrying on through an amber that could be stopped for.</summary>
        public float AmberRisk;
        /// <summary>Chance per junction of taking a wrong turn and re-routing.</summary>
        public float WrongTurnChance;
        public float IndicatorChance;
        public float ParkChance;
        /// <summary>Peak lateral drift within the lane (m). Nobody drives dead-centre.</summary>
        public float WanderAmp;
        /// <summary>How far desired speed strays (fraction), and roughly how long each swing lasts (s).</summary>
        public float SpeedWobble, WobblePeriod;
        /// <summary>Seconds between "the way is clear" and actually moving off.</summary>
        public float ReactionMin, ReactionMax;
        /// <summary>Where this driver stops relative to the line (m short of it).</summary>
        public float StopShort;
        /// <summary>Chance of U-turning instead of going round the block after missing a stop.</summary>
        public float UTurnChance;
        /// <summary>Turn speed relative to the standard one for that turn.</summary>
        public float TurnSpeedFactor;
        /// <summary>Line through a turn (m): positive cuts the corner, negative swings wide.</summary>
        public float TurnLine;
        /// <summary>How far before the corner they're down to turn speed (m): early braker vs late.</summary>
        public float TurnBrakeLead;
        /// <summary>How quickly the throttle builds (m/s³): gentle roll-on vs brisk.</summary>
        public float Jerk;

        static readonly float[] TypeWeights = { 55, 15, 15, 7, 8 };

        public static DriverProfile Sample(ref Rng rng)
        {
            var type = (DriverType)rng.PickWeighted(TypeWeights);
            var p = new DriverProfile
            {
                Type = type,
                SpeedFactor = rng.Range(0.95f, 1.08f),
                TimeHeadway = rng.Range(1.3f, 1.7f),
                MinGap = rng.Range(1.8f, 2.4f),
                MaxAccel = rng.Range(1.3f, 1.7f),
                ComfortDecel = rng.Range(1.8f, 2.4f),
                Politeness = rng.Range(0.3f, 0.6f),
                CriticalGap = rng.Range(4f, 5f),
                AmberRisk = 0.1f,
                WrongTurnChance = 0.01f,
                IndicatorChance = 1f,
                ParkChance = 0.35f,
                WanderAmp = rng.Range(0.12f, 0.24f),
                SpeedWobble = rng.Range(0.04f, 0.08f),
                WobblePeriod = rng.Range(12f, 30f),
                ReactionMin = 0.45f, ReactionMax = 1.0f,
                StopShort = rng.Range(0.6f, 2.2f),
                UTurnChance = 0.3f,
                TurnSpeedFactor = rng.Range(0.85f, 1.15f),
                TurnLine = rng.Range(-0.35f, 0.5f),
                TurnBrakeLead = rng.Range(0f, 6f),
                Jerk = rng.Range(1.6f, 3f),
            };
            switch (type)
            {
                case DriverType.Cautious:
                    p.SpeedFactor = rng.Range(0.82f, 0.93f); p.TimeHeadway = rng.Range(1.9f, 2.4f);
                    p.MinGap = rng.Range(2.4f, 3f); p.MaxAccel = rng.Range(1.0f, 1.3f);
                    p.CriticalGap = rng.Range(5.5f, 7f); p.AmberRisk = 0f; p.Politeness = 0.8f;
                    p.WanderAmp = rng.Range(0.08f, 0.15f); p.SpeedWobble = rng.Range(0.03f, 0.05f);
                    p.ReactionMin = 0.7f; p.ReactionMax = 1.4f; p.StopShort = rng.Range(1.5f, 3f); p.UTurnChance = 0.05f;
                    p.TurnSpeedFactor = rng.Range(0.72f, 0.9f); p.TurnLine = rng.Range(-0.45f, 0.1f); p.TurnBrakeLead = rng.Range(4f, 10f);
                    p.Jerk = rng.Range(1.2f, 1.8f);
                    break;
                case DriverType.Aggressive:
                    p.SpeedFactor = rng.Range(1.1f, 1.22f); p.TimeHeadway = rng.Range(0.8f, 1.1f);
                    p.MinGap = rng.Range(1.3f, 1.7f); p.MaxAccel = rng.Range(2.0f, 2.6f);
                    p.ComfortDecel = rng.Range(2.6f, 3.2f); p.CriticalGap = rng.Range(2.8f, 3.5f);
                    p.AmberRisk = 0.6f; p.Politeness = 0.05f; p.IndicatorChance = 0.8f;
                    p.ReactionMin = 0.2f; p.ReactionMax = 0.5f; p.StopShort = rng.Range(0.3f, 0.9f); p.UTurnChance = 0.6f;
                    p.TurnSpeedFactor = rng.Range(1.1f, 1.35f); p.TurnLine = rng.Range(0.2f, 0.7f); p.TurnBrakeLead = rng.Range(-2f, 1f);
                    p.Jerk = rng.Range(2.8f, 4f);
                    break;
                case DriverType.Lost:
                    p.SpeedFactor = rng.Range(0.8f, 0.95f); p.WrongTurnChance = 0.2f; p.CriticalGap = 5.5f; p.IndicatorChance = 0.95f;
                    p.WanderAmp = rng.Range(0.2f, 0.32f); p.SpeedWobble = rng.Range(0.08f, 0.14f); p.WobblePeriod = rng.Range(8f, 16f);
                    p.UTurnChance = 0.7f; p.TurnSpeedFactor = rng.Range(0.75f, 1f); p.TurnBrakeLead = rng.Range(2f, 9f);
                    break;
                case DriverType.Distracted:
                    p.TimeHeadway = rng.Range(1.2f, 2.2f); p.IndicatorChance = 0.45f; p.WrongTurnChance = 0.05f;
                    p.ComfortDecel = rng.Range(2.5f, 3.2f);
                    p.WanderAmp = rng.Range(0.25f, 0.4f); p.SpeedWobble = rng.Range(0.08f, 0.15f); p.WobblePeriod = rng.Range(6f, 14f);
                    p.ReactionMin = 0.9f; p.ReactionMax = 2.6f; p.StopShort = rng.Range(0.4f, 3.2f);
                    p.TurnLine = rng.Range(-0.5f, 0.6f); p.TurnBrakeLead = rng.Range(-1f, 8f);
                    break;
            }
            return p;
        }

        public IdmParams Idm(float speedLimit, float accelFactor, float topSpeedFactor) => new IdmParams
        {
            DesiredSpeed = speedLimit * SpeedFactor * topSpeedFactor,
            TimeHeadway = TimeHeadway,
            MinGap = MinGap,
            MaxAccel = MaxAccel * accelFactor,
            ComfortDecel = ComfortDecel,
            Delta = 4f,
        };
    }
}
