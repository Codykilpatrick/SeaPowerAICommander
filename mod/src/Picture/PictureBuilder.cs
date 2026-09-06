using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using UnityEngine;

namespace SeaPowerForceAI.Picture
{
    /// <summary>
    /// Turns a live <see cref="Taskforce"/> into a serializable <see cref="TacticalPicture"/>.
    ///
    /// Reads only the task force's own plotting table, so the resulting picture carries the
    /// same uncertainty the AI is actually entitled to.
    /// </summary>
    public static class PictureBuilder
    {
        private const float MetresPerSecondToKnots = 1.943844f;

        /// <summary>
        /// Replaces NaN and Infinity with 0.
        ///
        /// Game physics produces non-finite floats readily - a Mach-based speed command
        /// reports an infinite knots value, for one. Newtonsoft writes those as bare
        /// Infinity/NaN tokens, which System.Text.Json rejects outright, so a single bad
        /// unit takes down the whole decision with a parse error. Every float sourced
        /// from the game goes through here.
        /// </summary>
        private static float Finite(float value)
        {
            return (float.IsNaN(value) || float.IsInfinity(value)) ? 0f : value;
        }

        private static double Finite(double value)
        {
            return (double.IsNaN(value) || double.IsInfinity(value)) ? 0.0 : value;
        }

        public static TacticalPicture Build(Taskforce tf)
        {
            var picture = new TacticalPicture
            {
                TimeSeconds = GameTime.time,
                TaskforceName = string.IsNullOrEmpty(tf._nameInMissionFile) ? "(unnamed)" : tf._nameInMissionFile,
                Side = tf.Side.ToString(),
                IsOnAlert = tf._isOnAlert,
                TimeCompression = GameTime.TimeCompression,
                Objective = Plugin.ForceObjective,
            };

            AddOwnUnits(picture, tf._taskforceVessels, "Vessel");
            AddOwnUnits(picture, tf._taskforceSubmarines, "Submarine");
            AddOwnUnits(picture, tf._taskforceAircraft, "Aircraft");
            AddOwnUnits(picture, tf._taskforceHelicopters, "Helicopter");
            AddOwnUnits(picture, tf._taskforceLandUnits, "LandUnit");

            ApplyMission(picture);
            ApplyConditions(picture);
            AddContacts(picture, tf);

            return picture;
        }

        /// <summary>
        /// Mission identity, and the opposing side's stated objectives.
        ///
        /// The objectives in MissionManager are authored for the player. They are carried
        /// here only so an objective can be inferred for this force when none was
        /// configured - the scenario author designed both sides, so the opposing briefing
        /// is the best evidence of what the situation actually is. It is never given to
        /// the commander as intelligence.
        /// </summary>
        private static void ApplyMission(TacticalPicture picture)
        {
            try
            {
                var path = Globals.currentMissionFilePath;
                picture.MissionName = string.IsNullOrEmpty(path)
                    ? "(unknown)"
                    : System.IO.Path.GetFileNameWithoutExtension(path);

                // Runtime objectives first, when a mission populates them.
                var mm = Singleton<MissionManager>.Instance;
                if (mm != null && mm.Objectives != null)
                {
                    foreach (var objective in mm.Objectives)
                    {
                        if (objective == null) continue;
                        if (string.IsNullOrWhiteSpace(objective.Text)) continue;
                        if (objective._isCanceled) continue;

                        picture.OpposingObjectives.Add(objective.Text);
                    }
                }

                // Most missions leave that collection empty - Hormuz has none at all - but
                // the mission file itself carries a neutral Description of the situation
                // and the other task forces' opening orders. That is a better source
                // anyway: the Description sets up BOTH sides rather than stating either
                // one's plan.
                ReadMissionFile(picture, path);

                if (string.IsNullOrWhiteSpace(picture.MissionDescription) && picture.OpposingObjectives.Count == 0)
                {
                    Plugin.Log.LogWarning(
                        $"[mission] {picture.MissionName}: nothing readable in the mission file or " +
                        "objectives - the force has NO mission and will optimise for survival.");
                }
                else
                {
                    Plugin.Log.LogInfo(
                        $"[mission] {picture.MissionName}: description {picture.MissionDescription?.Length ?? 0} chars, " +
                        $"{picture.OpposingObjectives.Count} opposing objective(s)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] mission unreadable: {ex.Message}");
            }
        }

        /// <summary>
        /// Pulls the English Description and the other task forces' opening orders out of
        /// the mission ini.
        ///
        /// Only the header matters, so reading stops once past it - these files run to
        /// tens of thousands of lines of unit placements. The Description is written from
        /// a neutral standpoint and names what both sides are trying to do, which is why
        /// it is a fair thing to infer this force's mission from.
        /// </summary>
        private static void ReadMissionFile(TacticalPicture picture, string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

            var inEnglish = false;
            var inDescription = false;
            var description = new System.Text.StringBuilder();

            foreach (var raw in System.IO.File.ReadLines(path, System.Text.Encoding.UTF8))
            {
                var line = raw.Trim();

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    // Unit placement sections start well after the localised header.
                    if (inEnglish) break;
                    inEnglish = line.Equals("[Language_en]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inEnglish) continue;

                if (line.StartsWith("Description=", StringComparison.OrdinalIgnoreCase))
                {
                    description.Append(line.Substring("Description=".Length));
                    inDescription = true;
                    continue;
                }

                // A description runs across several paragraphs separated by blank lines,
                // so it does not end until the next key. Reading only the first line
                // truncated Hormuz to a quarter of its briefing.
                if (inDescription)
                {
                    var isNextKey = line.Length > 0
                        && line.IndexOf('=') > 0
                        && !line.StartsWith(" ");

                    // Ending the description must NOT consume this line - it is the next
                    // key, and swallowing it here silently dropped every StartMessage.
                    if (isNextKey) inDescription = false;
                    else
                    {
                        if (line.Length > 0) description.Append(' ').Append(line);
                        continue;
                    }
                }

                if (line.IndexOf("StartMessage=", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    // "Taskforce1StartMessage=Hormuz|Commander, you need to..." - the part
                    // before the pipe is a title, the rest is the orders.
                    var value = line.Substring(line.IndexOf('=') + 1);
                    var pipe = value.IndexOf('|');
                    if (pipe >= 0) value = value.Substring(pipe + 1);

                    if (!string.IsNullOrWhiteSpace(value))
                        picture.OpposingObjectives.Add(value.Trim());
                }
            }

            picture.MissionDescription = description.ToString().Trim();
        }

        private static void ApplyConditions(TacticalPicture picture)
        {
            try
            {
                var env = Singleton<SeaPower.Environment>.Instance;
                if (env == null) return;

                var c = picture.Conditions;

                // TimeZoneAdjustedHour, not Hour. Hour is Zulu, and darkness is a LOCAL
                // phenomenon - reading Zulu made the commander announce "at night" during
                // a late afternoon and set its ROE accordingly. Confidently wrong data is
                // worse than absent data.
                c.Hour = env.TimeZoneAdjustedHour;
                c.Minutes = env.Minutes;
                c.IsNight = c.Hour < 6 || c.Hour >= 20;
                c.SeaState = env.SeaState;
                c.IsFog = env.IsFog;
                c.IsRaining = env.IsRaining;
                c.IsSnowing = env.IsSnowing;
                c.IsLightning = env.IsLightning;

                c.OceanNoise = Finite(env.CurrentOceanNoise);
                c.LayerDepth = Finite(env.CurrentLayerDepth);

                // Sampled at the force centre - ducting is position-dependent.
                var centre = ForceCentre(picture);
                if (centre.HasValue)
                    c.SurfaceDuct = Finite(env.getSurfaceDuct(centre.Value));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] conditions unreadable: {ex.Message}");
            }
        }

        /// <summary>
        /// Mean position of the force's surface and subsurface units, used as the origin
        /// for range and terrain-masking queries. Aircraft are excluded - they wander far
        /// from the group and would drag the centre with them.
        /// </summary>
        private static GeoPosition? ForceCentre(TacticalPicture picture)
        {
            double lat = 0.0, lon = 0.0;
            var count = 0;

            foreach (var u in picture.OwnUnits)
            {
                if (u.Category == "Aircraft" || u.Category == "Helicopter") continue;
                lat += u.Latitude;
                lon += u.Longitude;
                count++;
            }

            if (count == 0) return null;
            return new GeoPosition(lat / count, lon / count);
        }

        private static void AddOwnUnits(TacticalPicture picture, List<ObjectBase> units, string category)
        {
            if (units == null) return;

            foreach (var obj in units)
            {
                if (obj == null || obj.IsDestroyed) continue;

                var geo = obj._geoPosition;

                var unit = new OwnUnit
                {
                    Id = obj.UniqueID,
                    Name = obj.getName(),
                    Category = category,
                    Roles = DescribeRoles(obj),
                    Latitude = geo != null ? Finite(geo.Latitude) : 0.0,
                    Longitude = geo != null ? Finite(geo.Longitude) : 0.0,
                    Altitude = geo != null ? Finite(geo._height) : 0.0,
                    HeadingDeg = Finite(obj.getHeading()),
                    MaxSpeedKnots = Finite(obj.MaxForwardSpeedInKnots),
                    SpeedKnots = Finite(obj.getVelocityInKnots()),
                    WeaponStatus = obj._weaponStatus.ToString(),
                    InFormation = obj.InFormation != null && obj.InFormation.Value,
                    IsFormationLeader = obj.IsFormationLeader,
                    ActsIndependentlyInFormation =
                        obj.ActsIndependentlyInFormation != null && obj.ActsIndependentlyInFormation.Value,
                };

                ApplyFormation(unit, obj);
                ApplyCommandedSpeed(unit, obj);
                ApplyRoute(unit, obj);
                ApplyReach(unit, obj);

                picture.OwnUnits.Add(unit);
            }
        }

        /// <summary>
        /// What speed the unit has been told to make, as opposed to what it is doing.
        /// The gap between the two is how the commander can tell an order is still being
        /// carried out rather than complete.
        /// </summary>
        /// <summary>
        /// The formation ceiling, which is what actually caps a member's speed.
        ///
        /// Ordering 28 knots on a formation limited to 16 by its slowest member leaves the
        /// unit at 16, and from outside that is indistinguishable from the order being
        /// ignored. It cost three false "order did not take" warnings before the cause was
        /// clear.
        /// </summary>
        private static void ApplyFormation(OwnUnit unit, ObjectBase obj)
        {
            try
            {
                var formation = obj.Formation;
                if (formation == null) return;

                if (formation.MaxFormationSpeed != null)
                    unit.MaxFormationSpeedKnots = Finite(formation.MaxFormationSpeed.Value);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] formation unreadable for {unit.Name}: {ex.Message}");
            }
        }

        private static void ApplyCommandedSpeed(OwnUnit unit, ObjectBase obj)
        {
            try
            {
                var command = obj.SpeedCommand != null ? obj.SpeedCommand.Value : null;
                if (command != null)
                {
                    // A Mach-based command reports infinite knots - this is the one that
                    // took down three consecutive decisions.
                    unit.CommandedSpeedKnots = Finite(command.CommandSpeedInKnots);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] speed command unreadable for {unit.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Where the unit is actually headed. Without this the commander cannot tell a
        /// unit already en route to the right place from one sitting still.
        /// </summary>
        private static void ApplyRoute(OwnUnit unit, ObjectBase obj)
        {
            try
            {
                var waypoints = obj.ExportWaypoints();
                if (waypoints == null || waypoints.Count == 0) return;

                unit.WaypointsRemaining = waypoints.Count;

                var next = waypoints[0];
                if (next != null && next._geoposition != null)
                {
                    unit.NextWaypointLatitude = Finite(next._geoposition.Latitude);
                    unit.NextWaypointLongitude = Finite(next._geoposition.Longitude);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] waypoints unreadable for {unit.Name}: {ex.Message}");
            }
        }

        /// <summary>Engine distance units to nautical miles - the game's own constant.</summary>
        private const float UnityToNauticalMiles = 0.036285132f;

        /// <summary>
        /// Longest reach an object has against a given target type, from the ordnance it
        /// is actually carrying, in nautical miles.
        ///
        /// Mirrors the game's own AirStrike.GetMaxAirDefenseRangeNM, generalised beyond
        /// AAW - one figure would mislead, because a cruiser's SAM envelope is the threat
        /// to aircraft while its Harpoons are the threat to a fast attack craft, and those
        /// are wildly different distances.
        /// </summary>
        private static float MaxReachNM(ObjectBase obj, Ammunition.Target against)
        {
            if (obj == null || obj.AmmunitionAmountDictionary == null) return 0f;

            var best = 0f;
            foreach (var entry in obj.AmmunitionAmountDictionary)
            {
                if (entry.Value < 1) continue;

                var ammo = obj.getAmmunitionByName(entry.Key);
                var ap = ammo != null ? ammo._ap : null;
                if (ap == null) continue;

                if (ap._targetType != against && ap._secondaryTargetType != against) continue;

                var range = ap._launchRangesInUnity.y;
                if (range > best) best = range;
            }

            return Finite(best * UnityToNauticalMiles);
        }

        private static void ApplyReach(OwnUnit unit, ObjectBase obj)
        {
            try
            {
                unit.AntiSurfaceReachNM = MaxReachNM(obj, Ammunition.Target.ASuW);
                unit.AirDefenceReachNM = MaxReachNM(obj, Ammunition.Target.AAW);
                unit.AntiSubmarineReachNM = MaxReachNM(obj, Ammunition.Target.ASW);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] reach unreadable for {unit.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Threat envelopes for a contact - but only once it is identified.
        ///
        /// Reading a contact's magazine is reading ground truth, so this is gated on
        /// identification. That is exactly what identification buys you: the class is
        /// known, and a threat library follows. An unidentified contact reports null,
        /// which should make it more frightening rather than less.
        /// </summary>
        /// <summary>
        /// Range to the contact and the highest terrain between us and it.
        ///
        /// Land on the bearing means an approach can be masked, which is the entire basis
        /// of a coastal attack against a superior force. Only meaningful for a contact
        /// whose position we actually hold - a bearing-only track has no bearing geometry
        /// worth sampling.
        /// </summary>
        private static void ApplyTerrain(Contact contact, GeoPosition? centreOrNull)
        {
            if (!centreOrNull.HasValue) return;
            if (!contact.Latitude.HasValue || !contact.Longitude.HasValue) return;

            var centre = centreOrNull.Value;

            try
            {
                var target = new GeoPosition(contact.Latitude.Value, contact.Longitude.Value);

                var rangeNM = (float)centre.getDistanceInMiles(target);
                contact.RangeFromForceNM = Finite(rangeNM);

                var heading = centre.CourseTo(target);

                // Bound the sample to the target - the query walks the bearing in small
                // steps, so an unbounded distance would be needlessly expensive.
                var highest = TerrainUtils.CrudeGetHighestTerrainHeightAhead(
                    centre, heading, (float)centre.GetDistance(target));

                // The game returns a large negative sentinel when terrain is disabled.
                if (highest > -9000f)
                    contact.TerrainOnBearingM = Finite(highest);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] terrain unreadable for contact {contact.Id}: {ex.Message}");
            }
        }

        private static void ApplyThreatEnvelope(Contact contact, Vehicle veh, ObjectBase obj)
        {
            if (!contact.Identified) return;

            try
            {
                contact.AirDefenceRangeNM = MaxReachNM(obj, Ammunition.Target.AAW);
                contact.AntiSurfaceRangeNM = MaxReachNM(obj, Ammunition.Target.ASuW);
                contact.AntiSubmarineRangeNM = MaxReachNM(obj, Ammunition.Target.ASW);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[picture] threat envelope unreadable for contact {contact.Id}: {ex.Message}");
            }
        }

        private static void AddContacts(TacticalPicture picture, Taskforce tf)
        {
            var plot = tf.PlottingTable;
            if (plot == null) return;

            // Snapshot first: Vehicles is an ObservableCollection the game mutates.
            List<Vehicle> vehicles;
            try
            {
                vehicles = plot.Vehicles.ToList();
            }
            catch (Exception ex)
            {
                // Never swallow this. A silent return here looks identical to "the task
                // force has detected nothing", which is the hardest kind of bug to spot.
                Plugin.Log.LogError($"[picture] could not read plotting table for {picture.TaskforceName}: {ex}");
                return;
            }

            picture.PlotEntries = vehicles.Count;

            var centre = ForceCentre(picture);
            int skipNullVehicle = 0, skipNoObject = 0, skipOwn = 0;

            foreach (var veh in vehicles)
            {
                if (veh == null) { skipNullVehicle++; continue; }

                var obj = veh.Object;

                // A destroyed hostile is a result, not a gap. Skipping it silently made a
                // successful strike look identical to a lost track.
                if (obj != null && obj.IsDestroyed)
                {
                    skipNoObject++;
                    if (obj._taskforce != null && obj._taskforce != tf
                        && tf.RelationshipTo(obj._taskforce) == RelationsState.Hostile)
                    {
                        picture.RecentKills.Add(new LostUnit
                        {
                            Id = obj.UniqueID,
                            Name = obj.getName(),
                            Category = obj is Vessel ? "Vessel" : (obj is LandUnit ? "LandUnit" : "Air"),
                        });
                    }
                    continue;
                }

                if (obj == null) { skipNoObject++; continue; }

                // Our own units come through OwnUnits; the plotting table also holds them.
                if (obj._taskforce == tf) { skipOwn++; continue; }

                var contact = new Contact
                {
                    Id = obj.UniqueID,
                    Class = ReadClass(veh),
                    Identified = veh.Identified != null && veh.Identified.Value,
                    Classified = veh.IsClassified,
                    Dormant = veh.IsDormant != null && veh.IsDormant.Value,
                    Relationship = DescribeRelationship(tf, veh),
                    DetectingSensors = veh.DetectingSensors.ToString(),
                    FirstDetectedAt = Finite(veh.InitialDetectionEpoch),
                };

                ApplyPosition(contact, veh);
                ApplyVelocity(contact, veh);
                ApplyThreatEnvelope(contact, veh, obj);
                ApplyTerrain(contact, centre);

                picture.Contacts.Add(contact);
            }

            var beforeCap = picture.Contacts.Count;
            CapContacts(picture);

            // One line that fully accounts for every plotting-table entry, so a zero
            // contact count is always explainable without another mission run.
            Plugin.Log.LogInfo(
                $"[plot] {picture.TaskforceName}: {vehicles.Count} entries -> " +
                $"{beforeCap} contacts" +
                (picture.Contacts.Count < beforeCap ? $" -> {picture.Contacts.Count} after cap" : "") +
                $" (skipped: own={skipOwn}, noObject={skipNoObject}, nullVehicle={skipNullVehicle})");
        }

        /// <summary>
        /// Keeps the picture bounded regardless of scenario size.
        ///
        /// Decision latency tracks payload, and payload tracks contact count - a large
        /// scenario would otherwise push decisions past the point where they arrive
        /// usefully, or past the timeout entirely. A commander that must sustain 5x cannot
        /// be handed an unbounded picture.
        ///
        /// What survives the cap is chosen by tactical relevance, not arbitrarily: a
        /// hostile you have identified and that is close matters more than a distant
        /// unknown, and a dormant track matters least of all.
        /// </summary>
        private static void CapContacts(TacticalPicture picture)
        {
            var cap = Plugin.MaxContactsInPicture;
            if (cap <= 0 || picture.Contacts.Count <= cap) return;

            picture.Contacts.Sort((a, b) => Relevance(b).CompareTo(Relevance(a)));
            picture.Contacts.RemoveRange(cap, picture.Contacts.Count - cap);
        }

        private static double Relevance(Contact c)
        {
            var score = 0.0;

            if (c.Relationship == "Hostile") score += 1000.0;
            if (c.Identified) score += 400.0;
            else if (c.Classified) score += 200.0;
            if (c.Dormant) score -= 300.0;

            // Closer is more pressing. Bearing-only holds have no range to judge by, so
            // they sit mid-table rather than being dropped for lack of a number.
            if (c.RangeFromForceNM > 0f) score += 500.0 / (1.0 + c.RangeFromForceNM);
            else score += 50.0;

            return score;
        }

        private static void ApplyPosition(Contact contact, Vehicle veh)
        {
            // Null for a bearing-only hold (e.g. passive sonar or ESM with no range).
            var estimate = veh.PositionEstimate;
            if (!estimate.HasValue) return;

            var geo = estimate.Value.Item1;
            if (geo == null) return;

            contact.Latitude = Finite(geo.Latitude);
            contact.Longitude = Finite(geo.Longitude);
            contact.Altitude = Finite(geo._height);
        }

        private static void ApplyVelocity(Contact contact, Vehicle veh)
        {
            var v = veh.Velocity;
            if (v == Vector3.zero) return;

            var horizontal = new Vector3(v.x, 0f, v.z);
            if (horizontal.sqrMagnitude <= 0.0001f) return;

            contact.SpeedKnots = Finite(horizontal.magnitude * MetresPerSecondToKnots);

            var course = Mathf.Atan2(horizontal.x, horizontal.z) * Mathf.Rad2Deg;
            if (course < 0f) course += 360f;
            contact.CourseDeg = Finite(course);
        }

        private static string ReadClass(Vehicle veh)
        {
            var cls = veh.Class;
            if (!cls.HasValue) return "Unknown";

            var value = cls.Value.Value;
            return string.IsNullOrEmpty(value) ? "Unknown" : value;
        }

        private static string DescribeRelationship(Taskforce tf, Vehicle veh)
        {
            var side = veh.Side;
            if (!side.HasValue || side.Value.Value == null) return "Unknown";

            try
            {
                return tf.RelationshipTo(side.Value.Value).ToString();
            }
            catch (Exception)
            {
                return "Unknown";
            }
        }

        private static string DescribeRoles(ObjectBase obj)
        {
            var roles = obj._obp != null ? obj._obp._unitRoles : null;
            if (roles == null || roles.Count == 0) return string.Empty;

            return string.Join(",", roles.Select(r => r.ToString()).ToArray());
        }
    }
}
