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
        /// Things the game declined to do, waiting to be told to the commander.
        ///
        /// A refused order never becomes a standing order, so it never reaches the verify
        /// pass, so the commander has no way to learn that it failed - it simply sees the
        /// effect it wanted not happening and, reasonably, orders it again. That is how a
        /// strike went in at half strength twice: the second cruiser could not engage, the
        /// refusal went to our log, and the commander was never told.
        ///
        /// Drained into the next picture's orderProblems by ForceBrainTick. A list rather
        /// than a return value because the coordinated-attack path fires from the scheduler
        /// long after Apply has returned.
        /// </summary>
        internal static readonly List<string> Refusals = new List<string>();

        /// <summary>
        /// Records a refusal for the commander as well as the log.
        ///
        /// Bounded, because an unbounded list would grow all mission if nothing drained it,
        /// and because twenty problems is already more than a commander can act on.
        /// </summary>
        internal static void Refuse(string message)
        {
            if (Refusals.Count < 20) Refusals.Add(message);
        }

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
                    return $"AttackTarget contact {order.TargetContactId} salvo {order.Salvo} with {WeaponOrAuto(order)}";
                case ForceOrderKind.CoordinatedAttack:
                    return $"CoordinatedAttack[{order.CoordinationGroup}] contact {order.TargetContactId} salvo {order.Salvo} with {WeaponOrAuto(order)}";
                case ForceOrderKind.LaunchAirstrike:
                    return $"LaunchAirstrike {order.StrikeType} contact {order.TargetContactId}";
                case ForceOrderKind.SetEmcon:
                    return $"SetEmcon {order.Emcon}";
                case ForceOrderKind.LaunchAircraft:
                    return $"LaunchAircraft {order.AirMission} x{(order.Salvo <= 0 ? 1 : order.Salvo)}";
                case ForceOrderKind.IdentifyContact:
                    return $"IdentifyContact contact {order.TargetContactId}";
                case ForceOrderKind.ReturnToBase:
                    return "ReturnToBase";
                case ForceOrderKind.SetDepth:
                    return $"SetDepth {order.Depth}";
                case ForceOrderKind.SetSonar:
                    return $"SetSonar {order.Sonar}";
                case ForceOrderKind.SetFormation:
                    return $"SetFormation {order.FormationPattern}";
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

                case ForceOrderKind.IdentifyContact:
                    return IdentifyContact(unit, order);

                case ForceOrderKind.ReturnToBase:
                    return ReturnToBase(unit, order);

                case ForceOrderKind.SetDepth:
                    return SetDepth(unit, order);

                case ForceOrderKind.SetSonar:
                    return SetSonar(unit, order);

                case ForceOrderKind.SetFormation:
                    return SetFormation(unit, order);

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
                ClaimExplicitSpeed(unit);
                return true;
            }

            // Clamp rather than reject - a brain asking for flank is not an error.
            var max = unit.MaxForwardSpeedInKnots;
            var knots = (max > 0f && order.SpeedKnots > max) ? max : order.SpeedKnots;

            unit.SetSpeedCommand(new ConstantSpeed(knots, unit));
            ClaimExplicitSpeed(unit);
            return true;
        }

        /// <summary>
        /// Tell a submarine its speed was CHOSEN, not left to it.
        ///
        /// Submarine.ApplyAiTransitSpeed re-picks a telegraph from the threat level every
        /// tick unless _hasExplicitSpeedOrder is set, so setting the speed and stopping
        /// there achieves nothing - the boat reverts within a tick. Seen as K-boats pinned
        /// at 20kt whether ordered 8, 10 or 15 (20 being what their own threat scaling
        /// wanted), which the verify pass reported as "order did not take". It was right.
        ///
        /// This is the same flag the game's own waypoint task sets when a waypoint carries
        /// a speed (GoToWaypointTask:309), so it is the sanctioned way to say a human chose
        /// this. Deliberately never cleared here: the game clears it on its relative-point
        /// tasks, which is the behaviour wanted - autonomy resumes when the boat returns to
        /// station-keeping rather than being suppressed for the rest of the mission.
        /// </summary>
        private static void ClaimExplicitSpeed(ObjectBase unit)
        {
            var sub = unit as Submarine;
            if (sub != null) sub._hasExplicitSpeedOrder = true;
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
            var ammo = ResolveAmmoType(unit, target, order);

            unit._ai.AutoAttackByClick(target, ammo, ignoreExecuting: false, salvo: salvo);

            if (!IsEngaging(unit, target))
            {
                var why =
                    $"{unit.getName()} ({unit.UniqueID}) did NOT fire on contact {order.TargetContactId} - " +
                    $"the game declined the engagement. {DescribeEngagement(unit, target, ammo)}";

                Plugin.Log.LogWarning($"[orders] {order.Kind}: {why}");
                Refuse(why);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether this unit is actually engaging that target, asked of the game rather than
        /// predicted.
        ///
        /// OBSERVED, NOT PREDICTED, and the distinction is the whole point. A coordinated
        /// strike ordered from two cruisers put four missiles in the air instead of eight;
        /// both were logged as releasing on schedule, and nothing anywhere recorded that one
        /// of them never fired.
        ///
        /// Predicting the refusal is not practical. AutoAttackByClick does NOT reject a shot
        /// for being beyond the weapon's maximum range - SelectAmmunition only tests the
        /// MINIMUM (AI.cs:2216), and there is an ignoreRange fallback below that - so range
        /// arithmetic would refuse shots the game would happily take while missing the ones
        /// it actually declines. What it really gates on is WeaponSelector.isWeaponSystemUsable
        /// with ignoreSensors false: a fire-control or sensor channel to the target, and a
        /// launcher not already busy. Reimplementing that here would be a second copy of the
        /// game's weapon allocator, wrong in its own new ways.
        ///
        /// AIRCRAFT ARE A DIFFERENT SHAPE AND MUST BE, or this check refuses every air
        /// attack there is. AutoAttackByClick takes an early branch for them (AI.cs:2242):
        /// the target is handed to the airframe's own AI as _objectToDestroy and NO engage
        /// task is ever created, because an aircraft picks off its pylons as it runs in.
        /// Helicopters are not Aircraft and do take the engage-task path.
        /// </summary>
        internal static bool IsEngaging(ObjectBase unit, ObjectBase target)
        {
            if (unit == null || target == null) return false;

            if (unit is Aircraft)
                return unit._ai != null && unit._ai._objectToDestroy == target;

            var tasks = unit._currentEngageTasks;
            if (tasks == null) return false;

            foreach (var task in tasks)
            {
                if (task != null && task._targetObject == target) return true;
            }

            return false;
        }

        /// <summary>
        /// Range to the target and the longest reach this unit has against it, for the log.
        ///
        /// Diagnostic only - nothing decides on these numbers, because the game does not.
        /// They are here so a declined attack can be read back against geometry rather than
        /// guessed at, which is what the four-instead-of-eight strike needed and did not have.
        /// </summary>
        private static string DescribeEngagement(ObjectBase unit, ObjectBase target, Ammunition.Type ammoType)
        {
            try
            {
                var rangeNM = (unit.transform.position - target.transform.position).magnitude
                              * UnityToNauticalMiles;

                var bestUnity = -1f;
                var candidates = unit._ai.AmmunitionForTarget(target, ammoType);
                if (candidates != null)
                {
                    foreach (var a in candidates)
                    {
                        if (a == null || a._ap == null) continue;
                        if (a._ap._launchRangesInUnity.y > bestUnity) bestUnity = a._ap._launchRangesInUnity.y;
                    }
                }

                var reach = bestUnity < 0f
                    ? "nothing aboard matches this target"
                    : $"longest matching weapon reaches {bestUnity * UnityToNauticalMiles:F0}nm";

                return $"Range {rangeNM:F0}nm, {reach}. Usually a missing fire-control or " +
                       "sensor channel to the target, or a launcher already engaged.";
            }
            catch (Exception ex)
            {
                return $"(engagement geometry unreadable: {ex.Message})";
            }
        }

        /// <summary>Engine distance units to nautical miles - the game's own constant.</summary>
        private const float UnityToNauticalMiles = 0.036285132f;

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
                // Deck state AFTER the calls, because "launching 1 of 1" turned out to
                // mean "queued 1 of 1". Sixteen AEW sorties were reported launched in one
                // battle and no AEW aircraft ever appeared in the task force - the
                // ammunition gate (CanLaunchVehicle) would have returned false and been
                // logged, so createLaunchTask succeeded and something after it did not.
                //
                // Two theories were wrong before this line existed. Aboard/queued counts
                // are what distinguish "no airframe left" from "queued and never cycled",
                // and neither was visible.
                var aboardAfter = deck.TotalVehiclesOnBoard();
                var tasksAfter = deck.FlightDeckTasks != null ? deck.FlightDeckTasks.Count : -1;

                Plugin.Log.LogInfo(
                    $"[air] {unit.getName()} launching {launched} of {wanted} requested " +
                    $"{mission} sortie(s)" +
                    (fellBackToAnyRole ? $" (no {role} airframe aboard - sent what there was)" : "") +
                    $" [deck: {aboardAfter} aboard, {tasksAfter} task(s) queued]");
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

            // Roles are the AIRFRAME's tags, and aircraft use a different vocabulary from
            // ships. AAW is a SHIP role - an air-defence escort. A carrier fighter is
            // tagged Fighter, so asking a deck for an AAW airframe matched nothing:
            // Nimitz reported "no AAW airframe aboard" while holding ten F-14As, fell back
            // to launching anything, and put A-7E bombers up on combat air patrol.
            //
            // Verified against a live deck: F-14A=Fighter, A-7E=Bomber/SEAD, E-2C=AEW/ESM,
            // SH-3H=ASW/SAR. Every role below exists in UnitRoles.
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "cap":
                    mission = FlightDeckTask.MissionType.CAP;
                    role    = ObjectBaseParameters.UnitRoles.Fighter;
                    return true;

                case "intercept":
                    mission = FlightDeckTask.MissionType.Intercept;
                    role    = ObjectBaseParameters.UnitRoles.Fighter;
                    return true;

                case "asw":
                    mission = FlightDeckTask.MissionType.SearchAndDestroyASW;
                    role    = ObjectBaseParameters.UnitRoles.ASW;
                    // The game maps "ASW" to an ASWHunter loadout itself when one exists.
                    loadout = "ASW";
                    return true;

                case "aew":
                    mission = FlightDeckTask.MissionType.AEW;
                    role    = ObjectBaseParameters.UnitRoles.AEW;
                    return true;

                case "recon":
                    mission = FlightDeckTask.MissionType.Recon;
                    role    = ObjectBaseParameters.UnitRoles.Recon;
                    return true;

                case "mpa":
                    mission = FlightDeckTask.MissionType.MPA;
                    role    = ObjectBaseParameters.UnitRoles.MPA;
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

            // A named loadout is checked against the deck BEFORE it is forced. Forcing one
            // the base cannot fly replaces the whole candidate pool with something empty,
            // and AssigningAircraft then sits in its assigning state forever - the stalled
            // strike that six orders in one mission were wasted on. Refusing beats stalling,
            // and the refusal says what the base actually has.
            string forced = null;
            if (!string.IsNullOrWhiteSpace(order.Loadout))
            {
                if (!AirstrikeLoadouts.TryResolve(unit, order.Loadout, out forced))
                {
                    var have = AirstrikeLoadouts.Describe(unit);
                    var why =
                        $"{unit.getName()} ({unit.UniqueID}) cannot mount a \"{order.Loadout}\" strike - " +
                        (have == null
                            ? "it has no airstrike loadouts available at all"
                            : $"what it can fly is: {have}");

                    Plugin.Log.LogWarning($"[orders] LaunchAirstrike: {why}");
                    Refuse(why);
                    return false;
                }
            }

            var strike = BuildAirstrike(unit, target, type, forced);
            if (strike == null) return false;

            Plugin.Log.LogInfo(
                $"[attack] {unit.getName()} mounting a {type} strike on contact {order.TargetContactId}" +
                (forced == null
                    ? $" (loadout left to the game: {AirstrikeLoadouts.Describe(unit) ?? "none available"})"
                    : $" with {forced}"));
            return true;
        }

        /// <summary>
        /// Creates an air strike the way AI.LaunchAirstrike does, but with the loadout pool
        /// reachable.
        ///
        /// AI.LaunchAirstrike constructs the AirStrike internally and never exposes it, so
        /// _forceloadouts - the one field that decides what the strike flies with - cannot be
        /// set through it. The body below is that method's, field for field, with the single
        /// addition. It is duplication and it is worth naming as such: if the game changes
        /// how a strike is set up, this goes stale silently.
        ///
        /// The one thing deliberately NOT copied is the AI's own _airstrikeAllowedTime
        /// cooldown. It is private, it throttles the tactical AI's strikes rather than ours,
        /// and our cadence is already bounded by the tick interval and the rate gate.
        /// </summary>
        private static AirStrike BuildAirstrike(
            ObjectBase unit, ObjectBase target, AirStrike.Type type, string forcedLoadout)
        {
            try
            {
                var strike = new AirStrike();

                strike._type = type;
                strike._participatingAirBases.Add(unit);

                // The game's own figure, in engine units - roughly 10nm.
                strike._airstrikeRadius = 275.59497f;

                strike._initialTargets.Add(target);
                strike._targetGeoPos = target._geoPosition;
                strike._allowPartial = unit._obp._flightDeck._allowPartialAirstrikes;
                strike._skipReadyUp = false;
                strike._allowLarge = true;

                if (forcedLoadout != null) strike._forceloadouts.Add(forcedLoadout);

                unit._taskforce._airStrikes.Add(strike);
                strike.init();

                return strike;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[orders] LaunchAirstrike: could not create the strike: {ex.Message}");
                return null;
            }
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

        /// <summary>
        /// Send a unit to find out what a contact actually is.
        ///
        /// THE MECHANISM IS DIFFERENT PER UNIT TYPE, and picking the wrong one is a silent
        /// no-op rather than an error. The game itself splits on this at AI.cs:397 -
        /// aircraft get a target written into _objectToIdentify, everything else gets an
        /// Order. Reproduced here rather than guessed at:
        ///
        ///   Aircraft   - _ai._objectToIdentify. Aircraft.cs:258 transitions into
        ///                IdentifySurfaceContact on that field alone; an Order.Type.Identify
        ///                would sit in the order slot untouched.
        ///   Submarine  - _ai._objectToIdentify (Submarine.cs:256). No VisualIdentify state
        ///                exists for boats, so there is no order-driven path at all.
        ///   Vessel     - Order.Type.Identify (Vessel.cs:162). A ship's IdentifyContact
        ///                state also exists but is gated on !IsPlayerObject; the
        ///                order-driven VisualIdentify is not, so it works for a delegated
        ///                force as well as an AI one.
        ///   Helicopter - Order.Type.Identify (Helicopter.cs:270).
        ///
        /// This is also the only way to redirect an aircraft that is already flying. MoveTo
        /// is refused for air units because their AI rewrites the route every tick - but
        /// naming a contact is how that same AI is meant to be tasked, so it diverts.
        /// </summary>
        private static bool IdentifyContact(ObjectBase unit, ForceOrder order)
        {
            var tf = unit._taskforce;

            var target = ResolveContact(tf, order.TargetContactId);
            if (target == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] IdentifyContact: contact {order.TargetContactId} not held by this task force");
                return false;
            }

            // Nothing to learn. Every one of these state machines finishes the moment the
            // track reads identified, so this would be an order that ends before it starts.
            var veh = target.GetMapVehicle(tf);
            if (veh != null && veh.Identified != null && veh.Identified.Value)
            {
                Plugin.Log.LogWarning(
                    $"[orders] IdentifyContact: contact {order.TargetContactId} is already identified");
                return false;
            }

            // Land units have no identify path. LandUnit.cs:380 renders a status line for an
            // Identify order and nothing else acts on it, so the order would be accepted,
            // displayed, and never carried out - and a coastal battery cannot close a contact
            // to look at it in any case.
            if (unit is LandUnit)
            {
                Plugin.Log.LogWarning(
                    $"[orders] IdentifyContact: {unit.getName()} is a land unit - it cannot go and look");
                return false;
            }

            // Parked aircraft go nowhere. Same class of problem as a movement order to a
            // deck - accepted, and then nothing happens.
            if (!IsAirborneIfAir(unit))
            {
                Plugin.Log.LogWarning(
                    $"[orders] IdentifyContact: {unit.getName()} is not airborne - launch it first");
                return false;
            }

            if (unit is Aircraft || unit is Submarine)
            {
                if (unit._ai == null)
                {
                    Plugin.Log.LogWarning($"[orders] IdentifyContact: {unit.getName()} has no AI to task");
                    return false;
                }

                // Submarine.cs:256 gates the IdentifyContact state on the boat NOT being a
                // player object, so on a delegated player force the field would be set and
                // the boat would ignore it. Refuse loudly rather than accept an order that
                // cannot fire.
                if (unit is Submarine && unit.IsPlayerObject && !DM._subAIAppliesToPlayer)
                {
                    Plugin.Log.LogWarning(
                        $"[orders] IdentifyContact refused for {unit.getName()}: the game only lets " +
                        "NON-player submarines prosecute an identify task. Send a ship, a helicopter " +
                        "or an aircraft instead.");
                    return false;
                }

                unit._ai._objectToIdentify = target;
                Plugin.Log.LogInfo(
                    $"[diag] IdentifyContact {unit.getName()} -> contact {order.TargetContactId}: " +
                    DescribeTasking(unit));
                return true;
            }

            // Vessels and helicopters identify by eye, and Order.cs cancels the order
            // outright when there is nothing to look with. Catch that here so it reads as a
            // refusal rather than as an order that vanished.
            if (unit._obp == null || unit._obp._visualSensors == null || unit._obp._visualSensors.Count == 0)
            {
                Plugin.Log.LogWarning(
                    $"[orders] IdentifyContact: {unit.getName()} has no visual sensors - it cannot " +
                    "make a visual identification");
                return false;
            }

            unit.setOrder(Order.Type.Identify, target, displayOrderText: true);
            Plugin.Log.LogInfo(
                $"[diag] IdentifyContact {unit.getName()} -> contact {order.TargetContactId}: " +
                DescribeTasking(unit));
            return true;
        }

        /// <summary>
        /// Everything that decides whether a unit will actually take up a retasking, logged
        /// at the moment it is tasked.
        ///
        /// DIAGNOSTIC, and it exists because a live mission issued six IdentifyContact
        /// orders, accepted every one, and not a single aircraft diverted - while nothing
        /// anywhere said why. The identify states are reachable only from `Default`, `MPA`,
        /// `MaritimePatrol` and `Loitering` (Aircraft.cs:259-266), so the state a unit is
        /// already in decides the whole outcome, and it was the one thing not visible.
        ///
        /// The rest are the inputs to `Aircraft.InAirFormation()`, which is what diverts a
        /// unit into `MovingInFormation` - a state with no identify transition at all.
        /// A station Role of CAP, Intercept or MPA does the same thing by a different route.
        ///
        /// Delete this once the cause is known and the executor refuses the cases that
        /// cannot work; a per-order log line is not something to keep for its own sake.
        /// </summary>
        private static string DescribeTasking(ObjectBase unit)
        {
            var sb = new System.Text.StringBuilder();

            try
            {
                var machine = unit.StateMachine;
                sb.Append("state=").Append(machine != null ? machine.CurrentStateName : "(none)");
                sb.Append(" hasControl=").Append(unit._hasControl);

                var aircraft = unit as Aircraft;
                if (aircraft != null)
                {
                    sb.Append(" airFormation=").Append(aircraft.InAirFormation());
                    sb.Append(" tempFormationRef=").Append(aircraft.HasTemporaryFormationReference);
                }

                sb.Append(" followingFormation=").Append(unit.FollowingFormation);

                var formation = unit.Formation;
                if (formation != null)
                {
                    var station = formation.GetStationForUnit(unit);
                    sb.Append(" station=").Append(station != null ? station.Role.ToString() : "(none)");
                }
            }
            catch (Exception ex)
            {
                // A diagnostic that throws would take the order down with it.
                sb.Append(" (unreadable: ").Append(ex.Message).Append(")");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Send an air unit home.
        ///
        /// Order.Type.ReturnToBase drives an AtAny transition (Aircraft.cs:318), so unlike
        /// the identify path this one preempts whatever the aircraft is doing - which is
        /// what recovering an empty fighter has to mean.
        /// </summary>
        private static bool ReturnToBase(ObjectBase unit, ForceOrder order)
        {
            if (!unit.IsAirUnit)
            {
                Plugin.Log.LogWarning(
                    $"[orders] ReturnToBase: {unit.getName()} is not an air unit - ships have nowhere to return to");
                return false;
            }

            if (!IsAirborneIfAir(unit))
            {
                Plugin.Log.LogWarning($"[orders] ReturnToBase: {unit.getName()} is already on deck");
                return false;
            }

            var home = unit.getHomeBase();
            if (home == null || home.IsDestroyed)
            {
                // Its own base may have sunk under it. The game has a routine for finding
                // another, and it is the same one Order.cs falls back on.
                unit.SearchForHomeBase();
                home = unit.getHomeBase();
            }

            if (home == null || home.IsDestroyed)
            {
                Plugin.Log.LogWarning(
                    $"[orders] ReturnToBase: {unit.getName()} has no home base to return to");
                return false;
            }

            // Order.cs calls createReturnTask on the base's flight deck without checking,
            // so a base with no deck would throw inside the game's own code.
            if (home._obp == null || home._obp._flightDeck == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] ReturnToBase: {unit.getName()}'s home base {home.getName()} has no flight deck");
                return false;
            }

            unit.setOrder(Order.Type.ReturnToBase, home, displayOrderText: true);
            Plugin.Log.LogInfo($"[air] {unit.getName()} recovering to {home.getName()}");
            return true;
        }

        /// <summary>
        /// Put a submarine in a depth band.
        ///
        /// setPlayerHeightControl(false) is NOT a way of declining control - it is exactly
        /// what the game's own depth menu does, and it means "automatic depth keeping owns
        /// the planes and the ballast". True is the hand-flying case, where pitch and
        /// ballast come from a human at the controls, and setting it here would leave the
        /// boat with a commanded depth and nothing driving it there.
        ///
        /// updateAltForWaypoints matches the UI too: without it a boat with a route queued
        /// returns to whatever depth those waypoints carry.
        ///
        /// The band is NOT permanent. Every submarine AI state picks a depth on entry
        /// (Drift, Sprint, ClearingDatum, ClassifyContact), so an ordered band holds until
        /// the boat changes state and no longer. That shows in the picture as
        /// commandedDepth, and the verify pass reports it.
        /// </summary>
        private static bool SetDepth(ObjectBase unit, ForceOrder order)
        {
            var sub = unit as Submarine;
            if (sub == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] SetDepth: {unit.getName()} is not a submarine - only boats have a depth to set");
                return false;
            }

            int preset;
            if (!DepthBands.TryParse(order.Depth, out preset))
            {
                Plugin.Log.LogWarning(
                    $"[orders] SetDepth for {unit.getName()}: unrecognised band '{order.Depth}' " +
                    "(expected Surface, Periscope, Shallow, AboveLayer, BelowLayer, Deep or VeryDeep)");
                return false;
            }

            var feet = sub.setPresetDepth(preset, updateAltForWaypoints: true);
            sub.setPlayerHeightControl(commandOverride: false);

            Plugin.Log.LogInfo($"[depth] {unit.getName()} making depth {DepthBands.Name(preset)} ({feet}ft)");
            return true;
        }

        /// <summary>
        /// Ping, stream the tail, or move it across the layer.
        ///
        /// Hull sonar and the towed array are handled separately - excludeTowed on the
        /// active-sonar calls is what stops "start pinging" from also going active on the
        /// tail, which is a different and much louder decision.
        ///
        /// The layer settings deploy first, because TowedSonarsSetSearchDepth only touches
        /// arrays that are already streamed and does nothing at all to a stowed one. The
        /// game's own menu deploys first for the same reason.
        /// </summary>
        private static bool SetSonar(ObjectBase unit, ForceOrder order)
        {
            var want = (order.Sonar ?? "").Trim().ToLowerInvariant();

            switch (want)
            {
                case "activeon":
                case "activeoff":
                    if (!unit.HasActiveSonar())
                    {
                        Plugin.Log.LogWarning(
                            $"[orders] SetSonar {order.Sonar}: {unit.getName()} has no serviceable active sonar");
                        return false;
                    }

                    if (want == "activeon") unit.EnableActiveSonars(excludeTowed: true);
                    else unit.DisableActiveSonars(excludeTowed: true);
                    return true;

                case "deploytowedarray":
                case "retracttowedarray":
                case "towedarrayabovelayer":
                case "towedarraybelowlayer":
                    if (!unit.HasTowedSonar())
                    {
                        Plugin.Log.LogWarning(
                            $"[orders] SetSonar {order.Sonar}: {unit.getName()} has no towed array");
                        return false;
                    }

                    if (want == "retracttowedarray")
                    {
                        // A clip-on array is fixed to the hull and cannot be recovered.
                        // RetractTowedSonars skips those silently, so a ship carrying only
                        // one would report success and stay streamed for the rest of the
                        // mission.
                        if (!HasRetractableTowedArray(unit))
                        {
                            Plugin.Log.LogWarning(
                                $"[orders] SetSonar RetractTowedArray: {unit.getName()} carries a clip-on " +
                                "array, which cannot be retracted");
                            return false;
                        }

                        unit.RetractTowedSonars();
                        return true;
                    }

                    unit.DeployTowedSonars();

                    if (want == "towedarrayabovelayer") unit.TowedSonarsSetSearchDepth(below: false);
                    else if (want == "towedarraybelowlayer") unit.TowedSonarsSetSearchDepth(below: true);
                    return true;

                default:
                    Plugin.Log.LogWarning(
                        $"[orders] SetSonar for {unit.getName()}: unrecognised setting '{order.Sonar}' " +
                        "(expected ActiveOn, ActiveOff, DeployTowedArray, RetractTowedArray, " +
                        "TowedArrayAboveLayer or TowedArrayBelowLayer)");
                    return false;
            }
        }

        /// <summary>Whether anything on this unit's tail can actually be pulled back in.</summary>
        private static bool HasRetractableTowedArray(ObjectBase unit)
        {
            var towed = unit._obp != null ? unit._obp._towedSonarSystems : null;
            if (towed == null) return false;

            foreach (var sonar in towed)
            {
                if (sonar == null) continue;
                if (sonar.Inoperable != null && sonar.Inoperable.Value) continue;
                if (sonar.IsClipOnArray) continue;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reshape a formation.
        ///
        /// Reform acts on the whole formation whichever member is named, so this is
        /// accepted from any of them - the commander is addressing a formation, not a ship,
        /// and making it find the leader first would only invite refusals.
        ///
        /// Spacing is deliberately left alone. Reform reads its own Spacing field rather
        /// than the parameter for four of the six patterns, so a spacing argument would
        /// apply to Vic and Circle and quietly not to the rest - a lever that works two
        /// times in six is worse than no lever.
        /// </summary>
        private static bool SetFormation(ObjectBase unit, ForceOrder order)
        {
            var formation = unit.Formation;
            if (formation == null)
            {
                Plugin.Log.LogWarning($"[orders] SetFormation: {unit.getName()} is not in a formation");
                return false;
            }

            UnitFormation.FormationPattern pattern;
            if (!TryParseFormationPattern(order.FormationPattern, out pattern))
            {
                Plugin.Log.LogWarning(
                    $"[orders] SetFormation for {unit.getName()}: unrecognised pattern " +
                    $"'{order.FormationPattern}' (expected Circle, Vic, Echelon, LineAbreast, " +
                    "LineAstern or Box)");
                return false;
            }

            // Reform positions every station relative to the leader's hull. Without one
            // there is nothing to position against.
            if (formation.LeaderStation == null || formation.LeaderStation.UnitObject == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] SetFormation: {formation.Name} has no leader to form on");
                return false;
            }

            formation.Reform(pattern, formation.Spacing);
            Plugin.Log.LogInfo($"[formation] {formation.Name} reforming into {pattern}");
            return true;
        }

        /// <summary>
        /// The patterns Reform can actually produce.
        ///
        /// None, Loose, Convoy and Battlegroup are excluded on purpose. Loose is the
        /// dangerous one: Reform's switch has no case for it, so every station falls
        /// through with a zero offset and the whole formation is ordered onto the leader's
        /// position. The game's own menu hides it for air units for the same reason.
        /// </summary>
        private static bool TryParseFormationPattern(string raw, out UnitFormation.FormationPattern pattern)
        {
            pattern = UnitFormation.FormationPattern.None;
            if (string.IsNullOrEmpty(raw)) return false;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "circle":      pattern = UnitFormation.FormationPattern.Circle;      return true;
                case "vic":         pattern = UnitFormation.FormationPattern.Vic;         return true;
                case "echelon":     pattern = UnitFormation.FormationPattern.Echelon;     return true;
                case "lineabreast": pattern = UnitFormation.FormationPattern.LineAbreast; return true;
                case "lineastern":  pattern = UnitFormation.FormationPattern.LineAstern;  return true;
                case "box":         pattern = UnitFormation.FormationPattern.Box;         return true;
                default: return false;
            }
        }

        /// <summary>
        /// True unless this is an air unit still sitting on a deck.
        ///
        /// Anything that is not an aircraft or helicopter passes: a ship is always where it
        /// is, and can always be given an order.
        /// </summary>
        private static bool IsAirborneIfAir(ObjectBase unit)
        {
            var aircraft = unit as Aircraft;
            if (aircraft != null) return aircraft._isInFlight;

            var helicopter = unit as Helicopter;
            if (helicopter != null) return helicopter._isInFlight;

            return true;
        }

        /// <summary>
        /// Which weapon an attack should use, after checking the unit is carrying one.
        ///
        /// Falls back to the unit's own allocation rather than refusing. The intent in an
        /// attack order is the ATTACK; the weapon is a preference, and turning a wrong
        /// preference into no attack at all would be the worse failure. Every fallback says
        /// so in the log, so a commander that keeps naming a weapon its ships do not carry
        /// stays visible.
        ///
        /// Aircraft are left on Auto unconditionally: AutoAttackByClick discards the
        /// ammunition type for air units entirely (AI.cs:2242) and hands the target to the
        /// airframe, which picks off its own pylons.
        /// </summary>
        internal static Ammunition.Type ResolveAmmoType(ObjectBase unit, ObjectBase target, ForceOrder order)
        {
            var wanted = (order.Weapon ?? "").Trim();
            if (wanted.Length == 0 || wanted.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                return Ammunition.Type.None;

            Ammunition.Type type;
            if (!TryParseWeapon(wanted, out type))
            {
                Plugin.Log.LogWarning(
                    $"[orders] {order.Kind}: unrecognised weapon '{order.Weapon}' for {unit.getName()} - " +
                    "letting the unit choose");
                return Ammunition.Type.None;
            }

            if (unit.IsAirUnit || unit._ai == null) return Ammunition.Type.None;

            // AmmunitionForTarget is the same filter the attack itself runs. If it comes
            // back empty the attack fires nothing at all and still reports success.
            var usable = unit._ai.AmmunitionForTarget(target, type);
            if (usable == null || usable.Count == 0)
            {
                Plugin.Log.LogWarning(
                    $"[orders] {order.Kind}: {unit.getName()} has no {wanted} usable against " +
                    $"contact {order.TargetContactId} - letting the unit choose");
                return Ammunition.Type.None;
            }

            return type;
        }

        private static bool TryParseWeapon(string raw, out Ammunition.Type type)
        {
            type = Ammunition.Type.None;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "missile": type = Ammunition.Type.Missile;    return true;
                case "torpedo": type = Ammunition.Type.Torpedo;    return true;
                case "gun":     type = Ammunition.Type.Projectile; return true;
                case "asroc":   type = Ammunition.Type.ASROC;      return true;
                case "rbu":     type = Ammunition.Type.RBU;        return true;
                default: return false;
            }
        }

        /// <summary>What an order asked to shoot with, for the log.</summary>
        private static string WeaponOrAuto(ForceOrder order)
        {
            return string.IsNullOrEmpty(order.Weapon) ? "Auto" : order.Weapon;
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
