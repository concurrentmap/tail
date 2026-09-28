using System;
using System.Collections.Generic;
using Tailed.Core.Util;

namespace Tailed.Core.Identity
{
    /// <summary>
    /// Fictional plate format "ABC 1234": 3 letters + 4 digits = 7 significant chars.
    /// I, O and Q are never issued so they can't be confused with 1 and 0.
    /// </summary>
    public static class PlateFormat
    {
        public const int Length = 7;
        public const string Letters = "ABCDEFGHJKLMNPRSTUVWXYZ";
        public const string Digits = "0123456789";

        /// <summary>Canonical form: uppercase, no spaces or dashes. Unknown chars become '?'.</summary>
        public static string Normalize(string raw)
        {
            if (raw == null) return string.Empty;
            var chars = new List<char>(Length);
            foreach (char c in raw)
            {
                if (c == ' ' || c == '-') continue;
                chars.Add(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '?');
            }
            return new string(chars.ToArray());
        }

        public static string Display(string canonical) =>
            canonical.Length == Length ? canonical.Substring(0, 3) + " " + canonical.Substring(3) : canonical;
    }

    /// <summary>Issues unique plates from a seeded RNG. One instance per match, shared by NPCs and players.</summary>
    public sealed class PlateGenerator
    {
        Rng _rng;
        readonly HashSet<string> _issued = new HashSet<string>();

        public PlateGenerator(ulong seed) => _rng = new Rng(seed);

        public int IssuedCount => _issued.Count;

        public string Next()
        {
            var buf = new char[PlateFormat.Length];
            while (true)
            {
                for (int i = 0; i < 3; i++) buf[i] = PlateFormat.Letters[_rng.NextInt(PlateFormat.Letters.Length)];
                for (int i = 3; i < 7; i++) buf[i] = PlateFormat.Digits[_rng.NextInt(PlateFormat.Digits.Length)];
                var plate = new string(buf);
                if (_issued.Add(plate)) return plate;
            }
        }

        /// <summary>Return a plate to the pool (vehicle despawned, plate swapped).</summary>
        public void Release(string plate) => _issued.Remove(PlateFormat.Normalize(plate));
    }

    /// <summary>
    /// The Mark's flag rule (GD§4): hit when the model matches exactly and at least
    /// <see cref="RequiredMatches"/> plate characters match in position. The guess may be
    /// partial — unknown characters are entered as '?' and never match.
    /// </summary>
    public sealed class PlateMatcher
    {
        public int RequiredMatches { get; }

        public PlateMatcher(int requiredMatches = 5)
        {
            if (requiredMatches < 1 || requiredMatches > PlateFormat.Length)
                throw new ArgumentOutOfRangeException(nameof(requiredMatches));
            RequiredMatches = requiredMatches;
        }

        public static int PositionalMatches(string guess, string actual)
        {
            string g = PlateFormat.Normalize(guess), a = PlateFormat.Normalize(actual);
            int n = Math.Min(g.Length, a.Length), hits = 0;
            for (int i = 0; i < n; i++)
                if (g[i] != '?' && g[i] == a[i]) hits++;
            return hits;
        }

        public bool IsHit(int guessedModelId, string guessedPlate, int actualModelId, string actualPlate) =>
            guessedModelId == actualModelId && PositionalMatches(guessedPlate, actualPlate) >= RequiredMatches;
    }
}
