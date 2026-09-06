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
            };

            AddOwnUnits(picture, tf._taskforceVessels, "Vessel");
            AddOwnUnits(picture, tf._taskforceSubmarines, "Submarine");
            AddOwnUnits(picture, tf._taskforceAircraft, "Aircraft");
            AddOwnUnits(picture, tf._taskforceHelicopters, "Helicopter");
            AddOwnUnits(picture, tf._taskforceLandUnits, "LandUnit");

            AddContacts(picture, tf);

            return picture;
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
                };

                ApplyCommandedSpeed(unit, obj);
                ApplyRoute(unit, obj);

                picture.OwnUnits.Add(unit);
            }
        }

        /// <summary>
        /// What speed the unit has been told to make, as opposed to what it is doing.
        /// The gap between the two is how the commander can tell an order is still being
        /// carried out rather than complete.
        /// </summary>
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

            int skipNullVehicle = 0, skipNoObject = 0, skipOwn = 0;

            foreach (var veh in vehicles)
            {
                if (veh == null) { skipNullVehicle++; continue; }

                var obj = veh.Object;
                if (obj == null || obj.IsDestroyed) { skipNoObject++; continue; }

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

                picture.Contacts.Add(contact);
            }

            // One line that fully accounts for every plotting-table entry, so a zero
            // contact count is always explainable without another mission run.
            Plugin.Log.LogInfo(
                $"[plot] {picture.TaskforceName}: {vehicles.Count} entries -> " +
                $"{picture.Contacts.Count} contacts " +
                $"(skipped: own={skipOwn}, noObject={skipNoObject}, nullVehicle={skipNullVehicle})");
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
