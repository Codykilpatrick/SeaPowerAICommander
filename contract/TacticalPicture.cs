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

        /// <summary>
        /// Game speed multiplier (1, 2, 3, 5, 10). This is not cosmetic: a decision takes
        /// real seconds, so at 10x roughly ten times as much game time passes between
        /// decisions as at 1x. The commander gets far fewer chances to intervene per
        /// game-minute and should order accordingly - durable, forward-looking intent
        /// rather than small corrections it will not be able to follow up.
        /// </summary>
        public float TimeCompression = 1f;

        /// <summary>
        /// Conditions the whole force is operating in. Night, fog and sea state change
        /// what an approach costs; layer depth and ocean noise decide whether a submarine
        /// is findable at all. A commander blind to these is missing the main variable in
        /// several of its domains.
        /// </summary>
        public EnvironmentConditions Conditions = new EnvironmentConditions();

        public List<OwnUnit> OwnUnits = new List<OwnUnit>();

        /// <summary>Detected contacts. A contact is not necessarily identified or classified.</summary>
        public List<Contact> Contacts = new List<Contact>();

        /// <summary>
        /// Diagnostic: total entries in the task force plotting table before own units
        /// were filtered out. Separates "we have detected nothing" (low) from "everything
        /// was filtered away" (high while Contacts is empty). Not tactical data.
        /// </summary>
        public int PlotEntries;

        // ---- Continuity ----
        //
        // Without these a brain is amnesiac: it re-derives intent from scratch every
        // cycle, re-commands units already carrying out its own orders, and cannot tell
        // a fleet that has lost half its ships from one that was always small. Attrition
        // is arguably the most important single input to a force-level decision, and a
        // stateless picture hides it completely.

        /// <summary>
        /// Orders issued last cycle and presumed still in force. Units acting on these
        /// need no new orders - reissuing them wastes the decision and the tokens.
        /// </summary>
        public List<Orders.ForceOrder> StandingOrders = new List<Orders.ForceOrder>();

        /// <summary>Units present at the previous decision and absent now - almost always losses.</summary>
        public List<LostUnit> RecentLosses = new List<LostUnit>();

        /// <summary>Running total of units lost since the mission began.</summary>
        public int TotalLosses;

        /// <summary>
        /// Game-seconds since the previous decision, so the brain can judge how stale
        /// its standing orders are. -1 on the first decision of a mission.
        /// </summary>
        public float SecondsSinceLastDecision = -1f;
    }

    public class EnvironmentConditions
    {
        /// <summary>
        /// LOCAL hour, 0-23 - time-zone adjusted, not Zulu. Darkness is a local
        /// phenomenon, and reading Zulu once had the commander declaring night during a
        /// late afternoon.
        /// </summary>
        public int Hour;
        public int Minutes;

        /// <summary>Derived from the local hour. Darkness favours a close approach.</summary>
        public bool IsNight;

        /// <summary>Sea state. High states degrade small-boat operations and sonar alike.</summary>
        public int SeaState;

        public bool IsFog;
        public bool IsRaining;
        public bool IsSnowing;
        public bool IsLightning;

        // ---- Acoustic conditions ----
        // These decide whether a submarine is findable. Ignoring them makes ASW guesswork.

        /// <summary>Ambient ocean noise. Higher masks passive detection both ways.</summary>
        public float OceanNoise;

        /// <summary>Depth of the thermal layer. A submarine below it is far harder to hold.</summary>
        public float LayerDepth;

        /// <summary>Surface duct strength - can carry sound far beyond normal range.</summary>
        public float SurfaceDuct;
    }

    public class LostUnit
    {
        public int Id;
        public string Name;

        /// <summary>Vessel / Submarine / Aircraft / Helicopter / LandUnit.</summary>
        public string Category;
    }

    public class OwnUnit
    {
        public int Id;
        public string Name;

        // ---- Observed state ----
        //
        // Without these the commander can see what it ASKED for but never what is
        // happening, so it cannot tell whether an order took effect, was overridden by
        // the tactical AI, or is being ignored. It was observed spending a whole decision
        // "resolving conflicting speed orders" that probably did not exist.

        /// <summary>Actual speed right now, in knots.</summary>
        public float SpeedKnots;

        /// <summary>Speed currently commanded. Differs from SpeedKnots while accelerating.</summary>
        public float CommandedSpeedKnots;

        /// <summary>Current weapons posture: Tight / Free / Hold.</summary>
        public string WeaponStatus;

        /// <summary>
        /// True when this unit is stationed in a formation.
        ///
        /// A formation follower takes its movement from the leader, so a MoveTo aimed at
        /// it individually may quietly do nothing - the order is accepted and the unit
        /// keeps following. Move the formation by ordering its leader.
        /// </summary>
        public bool InFormation;

        /// <summary>
        /// True when the unit manoeuvres on its own account despite being in a formation.
        /// Such a unit does respond to individual movement orders.
        /// </summary>
        public bool ActsIndependentlyInFormation;

        /// <summary>Waypoints still queued. 0 means the unit is not going anywhere.</summary>
        public int WaypointsRemaining;

        /// <summary>Where it is headed next, if anywhere.</summary>
        public double? NextWaypointLatitude;
        public double? NextWaypointLongitude;

        // ---- Reach ----
        //
        // How far this unit can hit back, by target type, from the ordnance it is
        // actually carrying. Paired with a contact's threat envelope this turns
        // "can I survive committing to this?" from recall into arithmetic.

        /// <summary>Longest reach against a surface ship, in nautical miles. 0 if none.</summary>
        public float AntiSurfaceReachNM;

        /// <summary>Longest reach against an aircraft, in nautical miles. 0 if none.</summary>
        public float AirDefenceReachNM;

        /// <summary>Longest reach against a submarine, in nautical miles. 0 if none.</summary>
        public float AntiSubmarineReachNM;

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

        // ---- Threat envelopes ----
        //
        // Populated ONLY when the contact is identified. That is what identification
        // means: you know the class, and a real navy carries a threat library for it.
        // Null means unknown, which should make a contact MORE frightening, not less.
        //
        // Split by target type because one number would mislead. A cruiser's 40nm SAM
        // envelope is the threat to aircraft; its Harpoons are the threat to a fast
        // attack craft, and those are wildly different distances.

        /// <summary>How far this contact can reach an aircraft, in nautical miles.</summary>
        public float? AirDefenceRangeNM;

        /// <summary>How far this contact can reach a surface ship, in nautical miles.</summary>
        public float? AntiSurfaceRangeNM;

        /// <summary>How far this contact can reach a submarine, in nautical miles.</summary>
        public float? AntiSubmarineRangeNM;

        /// <summary>Range from the force centre to this contact, in nautical miles.</summary>
        public float RangeFromForceNM;

        /// <summary>
        /// Highest terrain in metres on the bearing from the force centre to this contact.
        ///
        /// Above zero means land lies between - an approach on this bearing can be masked,
        /// which is the whole basis of a coastal attack. Zero means open water and no
        /// cover. Null when terrain could not be sampled.
        /// </summary>
        public float? TerrainOnBearingM;
    }
}
