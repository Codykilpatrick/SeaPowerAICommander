using System;

namespace SeaPowerAICommander.Orders
{
    /// <summary>
    /// The seven depth bands a submarine can be told to hold, and the game's index for
    /// each.
    ///
    /// Submarine.setPresetDepth takes a bare int and GetPresetDepthFeet computes what that
    /// means from the boat's own crush depth, cavitation curve and the local layer - so
    /// "BelowLayer" is a different number of feet on every boat in every patch of ocean,
    /// which is exactly why the commander should be naming the band and not the depth.
    ///
    /// Lives in one place because two sides need it: the executor turns a name into an
    /// index, and the picture turns an index back into a name so the commander can see
    /// what the boat is actually holding. Two copies of this table would drift and the
    /// symptom would be a depth order that reads as never taking effect.
    /// </summary>
    internal static class DepthBands
    {
        /// <summary>Indexed by the game's preset number - the order matters, not the spelling.</summary>
        private static readonly string[] Names =
        {
            "Surface",      // 0
            "Periscope",    // 1
            "Shallow",      // 2
            "AboveLayer",   // 3
            "BelowLayer",   // 4
            "Deep",         // 5
            "VeryDeep",     // 6
        };

        public static bool TryParse(string raw, out int preset)
        {
            preset = -1;
            if (string.IsNullOrEmpty(raw)) return false;

            var want = raw.Trim();
            for (var i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], want, StringComparison.OrdinalIgnoreCase))
                {
                    preset = i;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Null for an index outside the table - the boat is holding something we
        /// have no name for, and inventing one would be worse than saying nothing.</summary>
        public static string Name(int preset)
        {
            return (preset >= 0 && preset < Names.Length) ? Names[preset] : null;
        }
    }
}
