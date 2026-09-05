using System.Collections.Generic;

namespace SeaPowerForceAI.Picture
{
    /// <summary>
    /// One task force's view of the battle at a moment in time.
    ///
    /// Everything here comes from the task force's OWN plotting table, never from
    /// ObjectsManager._listOfAllObjects. That distinction is the point: the shipped
    /// air-strike pipeline reads ground truth, and a force brain built on this type
    /// structurally cannot.
    /// </summary>
    public class TacticalPicture
    {
        /// <summary>Game clock (GameTime.time) when this snapshot was taken.</summary>
        public float TimeSeconds;

        public string TaskforceName;

        /// <summary>Taskforce.TfType as a string: None / Player / Ally / Enemy / Neutral.</summary>
        public string Side;

        public bool IsOnAlert;

        public List<OwnUnit> OwnUnits = new List<OwnUnit>();

        /// <summary>Detected contacts. A contact is not necessarily identified or classified.</summary>
        public List<Contact> Contacts = new List<Contact>();
    }

    public class OwnUnit
    {
        public int Id;
        public string Name;

        /// <summary>Vessel / Submarine / Aircraft / Helicopter / LandUnit.</summary>
        public string Category;

        /// <summary>Comma-separated ObjectBaseParameters.UnitRoles, e.g. "Carrier,ASuW".</summary>
        public string Roles;

        public double Latitude;
        public double Longitude;

        /// <summary>Metres. Negative for submerged units.</summary>
        public double Altitude;

        public float HeadingDeg;
        public float MaxSpeedKnots;
    }

    public class Contact
    {
        /// <summary>
        /// UniqueID of the underlying object. Present so orders can reference a contact,
        /// NOT so the brain can look up truth about it - resolve it only through the
        /// plotting table.
        /// </summary>
        public int Id;

        /// <summary>Best available label. "Unknown" until classified.</summary>
        public string Class;

        /// <summary>True once the unit's identity is established, not merely detected.</summary>
        public bool Identified;

        /// <summary>True once the contact has a type (surface/air/subsurface), short of full ID.</summary>
        public bool Classified;

        /// <summary>Contact is held but currently stale - no recent sensor update.</summary>
        public bool Dormant;

        /// <summary>Hostile / Friendly / Neutral / Unknown, from the reported side.</summary>
        public string Relationship;

        /// <summary>Estimated position. Null when the contact is bearing-only.</summary>
        public double? Latitude;
        public double? Longitude;
        public double? Altitude;

        /// <summary>Which sensor types currently hold this contact (SensorTypeSet).</summary>
        public string DetectingSensors;

        /// <summary>Game clock at first detection - lets a brain reason about track age.</summary>
        public float FirstDetectedAt;

        /// <summary>Estimated speed in knots, from the track's velocity. Null if unknown.</summary>
        public float? SpeedKnots;

        /// <summary>Estimated course. Null if unknown.</summary>
        public float? CourseDeg;
    }
}
