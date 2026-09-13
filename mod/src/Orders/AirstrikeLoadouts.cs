using System;
using System.Collections.Generic;
using SeaPower;

namespace SeaPowerAICommander.Orders
{
    /// <summary>
    /// Which weapons fits an airbase or carrier can actually send a strike out with.
    ///
    /// Needed because AirStrike picks a loadout by AIRFRAME COUNT, not by suitability
    /// (AssigningAircraft.cs:187 takes whichever of the pool has the most aircraft
    /// available). Missile against a ship offers the AntiShip loadouts first, but a base
    /// stocked mainly for land attack will quietly send a land-attack fit at a destroyer -
    /// and until the loadout was reported in the picture, nothing said so.
    ///
    /// Two callers, which is why it lives here rather than in either of them: the picture
    /// lists what a deck can offer, and the executor checks a named loadout against that
    /// same list before forcing it. Forcing one the deck cannot fly is the worst of the
    /// available outcomes - AssigningAircraft finds no aircraft, never leaves that state,
    /// and the strike sits in the task force doing nothing until the game prunes it.
    /// </summary>
    internal static class AirstrikeLoadouts
    {
        /// <summary>
        /// Loadout names this unit could mount a strike with right now, and how many
        /// aircraft each has available. Empty for anything without a flight deck.
        ///
        /// Read through the game's own filter, so permitted usage, airframe size and
        /// per-loadout availability are all applied the way AssigningAircraft would apply
        /// them - not re-derived here, where it would drift.
        /// </summary>
        public static List<KeyValuePair<string, int>> Available(ObjectBase unit)
        {
            var found = new List<KeyValuePair<string, int>>();

            try
            {
                var deck = unit != null && unit._obp != null ? unit._obp._flightDeck : null;
                if (deck == null || deck._vehiclesOnBoard == null) return found;

                // Collect candidate names off the airframes aboard, then let the game say
                // which of them are actually flyable. Walking the vehicles first avoids
                // guessing at a fixed list of loadout keys, which would go stale the moment
                // a mod or a patch added one.
                var names = new List<string>();
                foreach (var vehicle in deck._vehiclesOnBoard)
                {
                    if (vehicle == null || vehicle.Numbers < 1 || vehicle.Loadouts == null) continue;

                    foreach (var loadout in vehicle.Loadouts)
                    {
                        if (loadout == null || string.IsNullOrEmpty(loadout.Name)) continue;
                        if (!names.Contains(loadout.Name)) names.Add(loadout.Name);
                    }
                }

                foreach (var name in names)
                {
                    var entries = unit.FlightDeckGetUnitAndNumberForLoadout(
                        name, VehicleTypeOnBoard.PermittedUsage.Airstrike);

                    if (entries == null || entries.Count == 0) continue;

                    var total = 0;
                    foreach (var entry in entries) total += entry.Item2;
                    if (total < 1) continue;

                    found.Add(new KeyValuePair<string, int>(name, total));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[air] loadouts unreadable for {(unit != null ? unit.getName() : "(null)")}: {ex.Message}");
            }

            return found;
        }

        /// <summary>
        /// Resolves what the commander asked for against what the deck actually has, and
        /// returns the deck's own spelling.
        ///
        /// Case-insensitive on the way in and exact on the way out, because the game matches
        /// loadout names with a plain string equality (ObjectBase.cs:10324). A commander that
        /// writes "antiship" would otherwise be refused for a loadout the base is carrying,
        /// which is the kind of failure that reads as the feature not working.
        /// </summary>
        public static bool TryResolve(ObjectBase unit, string wanted, out string exact)
        {
            exact = null;
            if (string.IsNullOrEmpty(wanted)) return false;

            var trimmed = wanted.Trim();
            foreach (var entry in Available(unit))
            {
                if (!string.Equals(entry.Key, trimmed, StringComparison.OrdinalIgnoreCase)) continue;

                exact = entry.Key;
                return true;
            }

            return false;
        }

        /// <summary>"AntiShip x4, Strike x8" - for the picture and for refusal messages.</summary>
        public static string Describe(ObjectBase unit)
        {
            var available = Available(unit);
            if (available.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            foreach (var entry in available)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(entry.Key).Append(" x").Append(entry.Value);
            }

            return sb.ToString();
        }
    }
}
