using System;
using System.Collections.Generic;
using SeaPower;

namespace SeaPowerAICommander.Orders
{
    /// <summary>
    /// Applies a <see cref="ForceOrderSet"/> to the game.
    ///
    /// This is the validation boundary. Orders may come from a heuristic, a sidecar
    /// process, or a language model - none of them are trusted. Every order is checked
    /// against the live task force before it touches a unit, and a bad order is dropped
    /// with a log line rather than thrown.
    /// </summary>
    public static class OrderExecutor
    {
        /// <summary>
        /// Applies a set of orders and returns those that actually took effect, so they
        /// can be replayed to the brain next cycle as standing orders. Replaying a
        /// rejected order would tell the brain a sunk ship is still under way.
        /// </summary>
        public static List<ForceOrder> Apply(Taskforce tf, ForceOrderSet set)
        {
            var accepted = new List<ForceOrder>();
            if (tf == null || set == null || set.Orders == null) return accepted;

            var byId = IndexOwnUnits(tf);
            int rejected = 0;

            // Coordinated attacks are planned as a group, not executed one by one - the
            // whole point is that their release times depend on each other.
            var groups = new Dictionary<string, List<ForceOrder>>();
            foreach (var order in set.Orders)
            {
                if (order == null || order.Kind != ForceOrderKind.CoordinatedAttack) continue;

                var key = string.IsNullOrEmpty(order.CoordinationGroup) ? "default" : order.CoordinationGroup;
                List<ForceOrder> members;
                if (!groups.TryGetValue(key, out members))
                {
                    members = new List<ForceOrder>();
                    groups[key] = members;
                }
                members.Add(order);
            }

            foreach (var group in groups)
            {
                var scheduled = AttackScheduler.Schedule(
                    group.Key,
                    group.Value,
                    id => { ObjectBase u; return byId.TryGetValue(id, out u) ? u : null; },
                    id => ResolveContact(tf, id));

                accepted.AddRange(scheduled);
                rejected += group.Value.Count - scheduled.Count;
            }

            foreach (var order in set.Orders)
            {
                if (order == null) { rejected++; continue; }

                // Already handled above as part of a coordination group.
                if (order.Kind == ForceOrderKind.CoordinatedAttack) continue;

                ObjectBase unit;
                if (!byId.TryGetValue(order.UnitId, out unit))
                {
                    // Not ours, sunk between the picture being sent and the orders coming
                    // back, or hallucinated. Never trust the id.
                    Plugin.Log.LogWarning($"[orders] rejected {order.Kind} - unit {order.UnitId} not in {tf._nameInMissionFile}");
                    rejected++;
                    continue;
                }

                try
                {
                    if (ApplyOne(unit, order)) accepted.Add(order);
                    else rejected++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[orders] {order.Kind} on {unit.getName()} threw: {ex.Message}");
                    rejected++;
                }
            }

            if (accepted.Count > 0 || rejected > 0)
                Plugin.Log.LogInfo($"[orders] {tf._nameInMissionFile}: applied {accepted.Count}, rejected {rejected}");

            // A count alone cannot be checked against what the units actually do. Every
            // accepted order says what it was and why, so the log can be read back against
            // observed behaviour instead of inferred from it.
            foreach (var order in accepted)
            {
                ObjectBase target;
                var who = byId.TryGetValue(order.UnitId, out target)
                    ? $"{target.getName()} ({order.UnitId})"
                    : order.UnitId.ToString();
                Plugin.Log.LogInfo($"[order] {Describe(order)} <- {who}: {order.Reason}");
            }

            return accepted;
        }

        /// <summary>The order's own parameters, so the log says what was asked, not just how many.</summary>
        private static string Describe(ForceOrder order)
        {
            switch (order.Kind)
            {
                case ForceOrderKind.MoveTo:
                    return $"MoveTo {order.Latitude:F4},{order.Longitude:F4}";
                case ForceOrderKind.SetSpeed:
                    return $"SetSpeed {order.SpeedKnots:F0}kt";
                case ForceOrderKind.SetWeaponStatus:
                    return $"SetWeaponStatus {order.WeaponStatus}";
                case ForceOrderKind.AttackTarget:
                    return $"AttackTarget contact {order.TargetContactId} salvo {order.Salvo}";
                case ForceOrderKind.CoordinatedAttack:
                    return $"CoordinatedAttack[{order.CoordinationGroup}] contact {order.TargetContactId} salvo {order.Salvo}";
                case ForceOrderKind.LaunchAirstrike:
                    return $"LaunchAirstrike {order.StrikeType} contact {order.TargetContactId}";
                case ForceOrderKind.SetEmcon:
                    return $"SetEmcon {order.Emcon}";
                case ForceOrderKind.LaunchAircraft:
                    return $"LaunchAircraft {order.AirMission} x{(order.Salvo <= 0 ? 1 : order.Salvo)}";
                default:
                    return order.Kind.ToString();
            }
        }

        private static bool ApplyOne(ObjectBase unit, ForceOrder order)
        {
            switch (order.Kind)
            {
                case ForceOrderKind.MoveTo:
                    return MoveTo(unit, order);

                case ForceOrderKind.SetSpeed:
                    return SetSpeed(unit, order);

                case ForceOrderKind.SetWeaponStatus:
                    return SetWeaponStatus(unit, order);

                case ForceOrderKind.AttackTarget:
                case ForceOrderKind.CoordinatedAttack:
                    return AttackTarget(unit, order);

                case ForceOrderKind.Disengage:
                    return Disengage(unit);

                case ForceOrderKind.LaunchAirstrike:
                    return LaunchAirstrike(unit, order);

                case ForceOrderKind.SetEmcon:
                    return SetEmcon(unit, order);

                case ForceOrderKind.LaunchAircraft:
                    return LaunchAircraft(unit, order);

                default:
                    Plugin.Log.LogWarning($"[orders] unknown kind {order.Kind}");
                    return false;
            }
        }

        /// <summary>
        /// Reposition a surface or sub-surface unit.
        ///
        /// AIR UNITS ARE REFUSED, and that is a correction rather than a limitation newly
        /// imposed. The waypoint was always being written and always being thrown away:
        /// an aircraft under any of its own AI states - MPA, CAP, Intercept, AEW - rebuilds
        /// its route every tick from SetRelativeToStationWaypointTask, relative to its
        /// formation station rather than to the world. Ours survived until the next tick.
        ///
        /// Reporting that as applied was the actual damage. The commander spent cycles and
        /// money re-issuing orders that could not work, then reasoned its way to a false
        /// belief about itself - "MoveTo orders ineffective for aircraft", "cannot directly
        /// control airborne aircraft" - and started planning around a limitation it had
        /// inferred rather than been told.
        ///
        /// Redirecting aircraft properly means moving the station or retasking, which is a
        /// feature and not a fix. Until then, refusing loudly beats pretending.
        /// </summary>
        private static bool MoveTo(ObjectBase unit, ForceOrder order)
        {
            if (order.Latitude < -90.0 || order.Latitude > 90.0 ||
                order.Longitude < -180.0 || order.Longitude > 180.0)
            {
                Plugin.Log.LogWarning($"[orders] MoveTo out of range: {order.Latitude},{order.Longitude}");
                return false;
            }

            if (unit is Aircraft || unit is Helicopter)
            {
                Plugin.Log.LogWarning(
                    $"[orders] MoveTo refused for air unit {unit.getName()} ({unit.UniqueID}): " +
                    "aircraft fly their tasking, not waypoints - their AI state rewrites the " +
                    "route every tick. Retask or reposition the parent instead.");
                return false;
            }

            unit.RemoveWaypoints();
            unit.setWaypointTask(
                new GeoPosition(order.Latitude, order.Longitude),
                "ai-commander",
                WaypointData.WaypointHeightState.NoChange);
            return true;
        }

        /// <summary>
        /// Go silent, or start radiating.
        ///
        /// The two directions are NOT symmetrical, and the game is the reason. setEMCON
        /// (true) shuts down search radars, active sonar, offensive jamming, towed active
        /// arrays and decoys together. setEMCON(false) only clears the flag - it turns
        /// nothing back on - and CheckEMCON then recomputes the flag from whether anything
        /// is actually radiating, so a bare setEMCON(false) is undone within a tick.
        ///
        /// Radiate therefore switches the search radars back on explicitly, which is also
        /// what a force-level order should mean. Active sonar is deliberately NOT restored:
        /// pinging is a much louder decision than turning on a search radar, it is usually
        /// the tactical AI's call in a prosecution, and quietly starting it on a whole task
        /// force because someone asked for "radiate" is not what anyone meant.
        /// </summary>
        private static bool SetEmcon(ObjectBase unit, ForceOrder order)
        {
            var want = (order.Emcon ?? "").Trim();

            if (want.Equals("Silent", StringComparison.OrdinalIgnoreCase))
            {
                unit.setEMCON(true);
                return true;
            }

            if (!want.Equals("Radiate", StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning(
                    $"[orders] SetEmcon for {unit.getName()}: unrecognised state '{order.Emcon}' " +
                    "(expected Silent or Radiate)");
                return false;
            }

            // Only what the unit actually has - asking a minesweeper for air search is a
            // no-op at best and a log full of noise at worst.
            var lit = false;
            if (unit.HasAirSearchRadar())     { unit.EnableAirSearchRadars();     lit = true; }
            if (unit.HasSurfaceSearchRadar()) { unit.EnableSurfaceSearchRadars(); lit = true; }

            if (!lit)
            {
                Plugin.Log.LogInfo(
                    $"[orders] SetEmcon Radiate for {unit.getName()}: no search radar fitted, nothing to switch on.");
                return false;
            }

            // Leave the flag to CheckEMCON - it derives Emcon from what is actually
            // radiating, and setting it here would only be overwritten anyway.
            return true;
        }

        private static bool SetSpeed(ObjectBase unit, ForceOrder order)
        {
            if (order.SpeedKnots < 0f)
            {
                Plugin.Log.LogWarning($"[orders] negative speed {order.SpeedKnots}");
                return false;
            }

            if (order.SpeedKnots <= 0.01f)
            {
                unit.SetSpeedCommand(new ZeroSpeed());
                return true;
            }

            // Clamp rather than reject - a brain asking for flank is not an error.
            var max = unit.MaxForwardSpeedInKnots;
            var knots = (max > 0f && order.SpeedKnots > max) ? max : order.SpeedKnots;

            unit.SetSpeedCommand(new ConstantSpeed(knots, unit));
            return true;
        }

        /// <summary>
        /// Engage a named contact.
        ///
        /// The target is resolved through the task force's OWN plotting table, never the
        /// global object list. A commander cannot order an attack on something it has not
        /// detected, which is the same rule the rest of the picture obeys - and the rule
        /// the shipped air-strike code breaks.
        /// </summary>
        /// <summary>
        /// Finds a contact in the task force's OWN plotting table.
        ///
        /// Never the global object list. A commander cannot order an attack on something
        /// it has not detected - the same rule the rest of the picture obeys, and the one
        /// the shipped air-strike code breaks.
        /// </summary>
        internal static ObjectBase ResolveContact(Taskforce tf, int contactId)
        {
            var plot = tf != null ? tf.PlottingTable : null;
            if (plot == null) return null;

            foreach (var veh in plot.Vehicles)
            {
                if (veh == null) continue;
                var obj = veh.Object;
                if (obj == null || obj.IsDestroyed) continue;
                if (obj.UniqueID != contactId) continue;

                // Refuse to turn our own guns on ourselves.
                if (obj._taskforce == tf) return null;

                return obj;
            }

            return null;
        }

        private static bool AttackTarget(ObjectBase unit, ForceOrder order)
        {
            var tf = unit._taskforce;

            var target = ResolveContact(tf, order.TargetContactId);
            if (target == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] {order.Kind}: contact {order.TargetContactId} not held by this task force");
                return false;
            }

            if (unit._ai == null)
            {
                Plugin.Log.LogWarning($"[orders] {order.Kind}: {unit.getName()} has no AI to engage with");
                return false;
            }

            var salvo = order.Salvo > 0 ? order.Salvo : 1;
            unit._ai.AutoAttackByClick(target, Ammunition.Type.None, ignoreExecuting: false, salvo: salvo);
            return true;
        }

        /// <summary>
        /// Drops the unit's attack and engagement tasks without touching its weapons
        /// posture, so it stops prosecuting but can still defend itself.
        /// </summary>
        private static bool Disengage(ObjectBase unit)
        {
            unit.ClearAttackTasks();
            unit.ClearEngageTasks();
            return true;
        }

        /// <summary>
        /// Mounts a strike from an airbase or carrier against a detected contact.
        ///
        /// Runs the game's own pipeline, so the aircraft get assigned, launched and
        /// formed up without the commander having to move them individually - which it
        /// could not do anyway, since parked aircraft ignore movement and speed orders.
        /// </summary>
        /// <summary>
        /// Put aircraft up on a standing mission, with no target required.
        ///
        /// Tries the role that matches the mission first, then any airframe. The role
        /// filter is worth attempting - it is what stops an ASW helicopter being sent up
        /// as combat air patrol - but a deck with nothing of that role returns false, and
        /// on a mixed or oddly-tagged deck a permissive launch is better than none.
        /// </summary>
        /// <summary>Ceiling on one order. A commander that asks for twelve aircraft has
        /// misunderstood the order, not found a way to empty its deck in one cycle.</summary>
        private const int MaxSortiesPerOrder = 4;

        private static bool LaunchAircraft(ObjectBase unit, ForceOrder order)
        {
            if (unit._obp == null || unit._obp._flightDeck == null)
            {
                Plugin.Log.LogWarning(
                    $"[air] LaunchAircraft: {unit.getName()} has no flight deck - order a carrier or airbase");
                return false;
            }

            var deck = unit._obp._flightDeck;
            if (deck.TotalVehiclesOnBoard() < 1)
            {
                Plugin.Log.LogWarning($"[air] LaunchAircraft: {unit.getName()} has nothing left aboard");
                return false;
            }

            if (!ParseAirMission(order.AirMission, out var mission, out var role, out var loadout))
            {
                Plugin.Log.LogWarning(
                    $"[air] LaunchAircraft: unrecognised mission '{order.AirMission}' " +
                    "(expected CAP, AEW, Recon, MPA, ASW or Intercept)");
                return false;
            }

            // One call launches ONE airframe - FlightDeckLaunchUnit returns as soon as it
            // has created a single launch task, and so does the game's own
            // FlightDeckLaunchCAP. There is no launch-a-flight entry point, so a flight
            // is N calls. Singles get picked off: a lone fighter on CAP has nobody to
            // cover it, and real air defence flies in pairs.
            var wanted = order.Salvo <= 0 ? 1 : order.Salvo;
            if (wanted > MaxSortiesPerOrder) wanted = MaxSortiesPerOrder;

            var launched = 0;
            var fellBackToAnyRole = false;

            for (var i = 0; i < wanted; i++)
            {
                if (unit.FlightDeckLaunchUnit(ObjectBase.ObjectType.Any, loadout, role, mission))
                {
                    launched++;
                    continue;
                }

                // Nothing of that role aboard, or no loadout for it. Try unfiltered
                // before giving up on this airframe.
                if (role != ObjectBaseParameters.UnitRoles.None
                    && unit.FlightDeckLaunchUnit(ObjectBase.ObjectType.Any, loadout,
                                                 ObjectBaseParameters.UnitRoles.None, mission))
                {
                    launched++;
                    fellBackToAnyRole = true;
                    continue;
                }

                // The deck ran dry or went busy part-way. Stop asking.
                break;
            }

            if (launched > 0)
            {
                Plugin.Log.LogInfo(
                    $"[air] {unit.getName()} launching {launched} of {wanted} requested " +
                    $"{mission} sortie(s)" +
                    (fellBackToAnyRole ? $" (no {role} airframe aboard - sent what there was)" : ""));
                return true;
            }

            // Said plainly, because the alternative is the stalled-airstrike problem all
            // over again: an order that reports success and produces no aircraft.
            Plugin.Log.LogWarning(
                $"[air] LaunchAircraft: {unit.getName()} could not launch a {mission} sortie - " +
                "nothing suitable aboard, no loadout, or the deck is busy");
            return false;
        }

        private static bool ParseAirMission(string raw,
                                            out FlightDeckTask.MissionType mission,
                                            out ObjectBaseParameters.UnitRoles role,
                                            out string loadout)
        {
            mission = FlightDeckTask.MissionType.None;
            role    = ObjectBaseParameters.UnitRoles.None;
            loadout = "Default";

            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "cap":
                    mission = FlightDeckTask.MissionType.CAP;
                    role    = ObjectBaseParameters.UnitRoles.AAW;
                    return true;

                case "intercept":
                    mission = FlightDeckTask.MissionType.Intercept;
                    role    = ObjectBaseParameters.UnitRoles.AAW;
                    return true;

                case "asw":
                    mission = FlightDeckTask.MissionType.SearchAndDestroyASW;
                    role    = ObjectBaseParameters.UnitRoles.ASW;
                    // The game maps "ASW" to an ASWHunter loadout itself when one exists.
                    loadout = "ASW";
                    return true;

                case "aew":
                    mission = FlightDeckTask.MissionType.AEW;
                    return true;

                case "recon":
                    mission = FlightDeckTask.MissionType.Recon;
                    return true;

                case "mpa":
                    mission = FlightDeckTask.MissionType.MPA;
                    return true;

                default:
                    return false;
            }
        }

        private static bool LaunchAirstrike(ObjectBase unit, ForceOrder order)
        {
            var target = ResolveContact(unit._taskforce, order.TargetContactId);
            if (target == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] LaunchAirstrike: contact {order.TargetContactId} not held by this task force");
                return false;
            }

            if (unit._ai == null)
            {
                Plugin.Log.LogWarning($"[orders] LaunchAirstrike: {unit.getName()} has no AI");
                return false;
            }

            // Only something with a flight deck can mount one.
            if (unit._obp == null || unit._obp._flightDeck == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] LaunchAirstrike: {unit.getName()} has no flight deck - order an airbase or carrier");
                return false;
            }

            var type = ParseStrikeType(order.StrikeType);

            unit._ai.LaunchAirstrike(target, type);
            Plugin.Log.LogInfo(
                $"[attack] {unit.getName()} mounting a {type} strike on contact {order.TargetContactId}");
            return true;
        }

        private static AirStrike.Type ParseStrikeType(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return AirStrike.Type.Bomb;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "missile": return AirStrike.Type.Missile;
                case "sead": return AirStrike.Type.SEAD;
                case "jam": return AirStrike.Type.Jam;
                default: return AirStrike.Type.Bomb;
            }
        }

        private static bool SetWeaponStatus(ObjectBase unit, ForceOrder order)
        {
            ObjectBase.WeaponStatus status;
            if (!TryParseWeaponStatus(order.WeaponStatus, out status))
            {
                Plugin.Log.LogWarning($"[orders] bad weapon status '{order.WeaponStatus}'");
                return false;
            }

            unit.SetWeaponStatus(status);
            return true;
        }

        private static bool TryParseWeaponStatus(string raw, out ObjectBase.WeaponStatus status)
        {
            status = ObjectBase.WeaponStatus.Tight;
            if (string.IsNullOrEmpty(raw)) return false;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "tight": status = ObjectBase.WeaponStatus.Tight; return true;
                case "free":  status = ObjectBase.WeaponStatus.Free;  return true;
                case "hold":  status = ObjectBase.WeaponStatus.Hold;  return true;
                default: return false;
            }
        }

        private static Dictionary<int, ObjectBase> IndexOwnUnits(Taskforce tf)
        {
            var map = new Dictionary<int, ObjectBase>();
            AddAll(map, tf._taskforceVessels);
            AddAll(map, tf._taskforceSubmarines);
            AddAll(map, tf._taskforceAircraft);
            AddAll(map, tf._taskforceHelicopters);
            AddAll(map, tf._taskforceLandUnits);
            return map;
        }

        private static void AddAll(Dictionary<int, ObjectBase> map, List<ObjectBase> units)
        {
            if (units == null) return;
            foreach (var u in units)
            {
                if (u == null || u.IsDestroyed) continue;
                map[u.UniqueID] = u;
            }
        }
    }
}
