using System.Collections.Generic;

namespace SeaPowerAICommander.Picture
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
        /// What this force is trying to achieve.
        ///
        /// Without one, self-preservation is the only rational policy and withdrawal is
        /// always the right answer - which is exactly what a commander given no mission
        /// does, every cycle, however good its tactical reasoning. An objective is what
        /// makes risk worth taking.
        ///
        /// Deliberately NOT the game's MissionManager.Objectives: those are authored from
        /// the player's side, and handing them to the opposing commander would be both
        /// wrong and a form of cheating.
        /// </summary>
        public string Objective;

        /// <summary>Mission file name, used to cache a derived objective for the mission.</summary>
        public string MissionName;

        /// <summary>
        /// The scenario's own description, as written in the mission file.
        ///
        /// Neutral by construction - it sets up the situation and names what both sides
        /// are trying to do, rather than stating either one's plan. That makes it the
        /// right thing to infer a mission from.
        /// </summary>
        public string MissionDescription;

        /// <summary>
        /// Every briefing the mission carries - runtime objectives plus the task forces'
        /// opening messages.
        ///
        /// NOT attributed to a side, and now named for what it actually holds. The mission
        /// file numbers its opening messages by task force (<c>Taskforce1StartMessage=</c>)
        /// but nothing in the header maps a number onto the force being commanded, so all
        /// this can honestly claim is "the briefings this mission contains".
        ///
        /// <see cref="BriefingIsOwnSide"/> is what says whether they were written FOR the
        /// force being commanded, and that decides whether they are adopted or inferred
        /// against. This was called OpposingObjectives, which held only while the
        /// commander exclusively drove the enemy.
        /// </summary>
        public List<string> MissionBriefings = new List<string>();

        /// <summary>
        /// True when the force being commanded is the one the mission was written for -
        /// the player's own task force, handed over to the commander.
        ///
        /// This flips the objective logic end for end, which is why it travels with the
        /// picture. A briefing written for the OTHER side is evidence to infer a posture
        /// against, and its specifics are intelligence this force has not earned. A
        /// briefing written for THIS side is simply its orders, and must be adopted
        /// rather than mirrored.
        ///
        /// Without this a delegated fleet pursued roughly the opposite of its mission:
        /// the game's objectives are authored for the player, so pointing the commander
        /// at the player's own force fed it its real orders labelled as the enemy's and
        /// asked it to plan against them.
        /// </summary>
        public bool BriefingIsOwnSide;

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
        /// Hostile units destroyed since the last decision, as far as we can observe.
        ///
        /// The other half of the ledger. A commander given only its own casualties can
        /// see what its plan costs but never what it achieves - and a destroyed contact
        /// simply stops appearing, which is indistinguishable from losing the track.
        /// Without this it cannot tell a successful strike from a failed one.
        /// </summary>
        public List<LostUnit> RecentKills = new List<LostUnit>();

        /// <summary>Running total of hostile units observed destroyed.</summary>
        public int TotalKills;

        /// <summary>
        /// Game-seconds since the previous decision, so the brain can judge how stale
        /// its standing orders are. -1 on the first decision of a mission.
        /// </summary>
        public float SecondsSinceLastDecision = -1f;

        /// <summary>
        /// Standing orders that the units are demonstrably not carrying out, in plain words.
        /// Without this the commander plans against its own intentions: it has no way to learn
        /// that the tactical AI rewrote a speed, that a formation follower cannot be steered,
        /// or that an order it is still counting on quietly did nothing.
        /// </summary>
        public List<string> OrderProblems = new List<string>();

        /// <summary>
        /// Air strikes this task force has going, and how far each has actually got. A strike
        /// is asynchronous - the order only creates it, and it can sit unable to find aircraft
        /// forever while looking, from the outside, exactly like one that is on its way.
        /// </summary>
        public List<AirstrikeStatus> Airstrikes = new List<AirstrikeStatus>();

        /// <summary>
        /// Air strikes ordered this mission, and how many ever got aircraft. A stalled strike
        /// is dropped from Airstrikes once the game finishes with it, taking the evidence with
        /// it - so without a running count the commander rediscovers an unusable airbase one
        /// wasted order at a time, having correctly written it off the cycle before.
        /// </summary>
        public int AirstrikesOrdered;

        /// <summary>How many of those ever progressed past assigning aircraft.</summary>
        public int AirstrikesThatFlew;
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
        /// True when this unit is emitting nothing - the game's own EMCON flag, which it
        /// derives from whether any radar, active sonar or jammer is actually on.
        ///
        /// Carried because emission control decides who detects whom, and the commander
        /// was previously blind to it: it could not tell a ship that had gone quiet from
        /// one whose radar had been switched off by its own tactical AI, and wrote plans
        /// around a sensor picture whose cause it could not see.
        /// </summary>
        public bool EmconSilent;

        /// <summary>Which emitters are actually radiating. Separate from
        /// <see cref="EmconSilent"/> so a partially-silent unit is legible.</summary>
        public bool AirSearchRadarOn;
        public bool SurfaceSearchRadarOn;
        public bool ActiveSonarOn;

        /// <summary>
        /// What this unit has sitting on its deck or in its hangar. Null for anything
        /// without a flight deck, which is most units.
        ///
        /// Without it the commander could not tell a carrier from a cruiser except by
        /// reading the name, and it showed: it repeatedly described its aircraft as
        /// wasted assets while having no way to know what was aboard, let alone launch
        /// it. An air group you cannot see is an air group you will not use.
        /// </summary>
        public List<DeckAircraft> AircraftAboard;

        /// <summary>
        /// True when this unit is stationed in a formation.
        ///
        /// A formation follower takes its movement from the leader, so a MoveTo aimed at
        /// it individually may quietly do nothing - the order is accepted and the unit
        /// keeps following. Move the formation by ordering its leader.
        /// </summary>
        public bool InFormation;

        /// <summary>
        /// True when this unit LEADS its formation.
        ///
        /// InFormation is true for the leader as well as its followers, so it alone does
        /// not say whether a unit can be ordered. The leader holds the route and takes
        /// movement orders; the followers take station on it. Order the leader to move a
        /// formation.
        /// </summary>
        public bool IsFormationLeader;

        /// <summary>
        /// True when the unit manoeuvres on its own account despite being in a formation.
        /// Such a unit does respond to individual movement orders.
        /// </summary>
        public bool ActsIndependentlyInFormation;

        /// <summary>
        /// Fastest the unit's formation can go, limited by its slowest member. 0 when not
        /// in a formation.
        ///
        /// A speed order above this is silently clamped - ordering 28 knots on a formation
        /// capped at 16 leaves the unit at 16 and looks like the order was ignored. To go
        /// faster, the formation has to be broken up or its slow members detached.
        /// </summary>
        public float MaxFormationSpeedKnots;

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

    /// <summary>One airframe type sitting on a flight deck, and how many of it.</summary>
    public class DeckAircraft
    {
        /// <summary>Airframe name as the game displays it, e.g. "SH-3D Sea King".</summary>
        public string Type;

        /// <summary>How many are aboard and available.</summary>
        public int Count;

        /// <summary>What it is for - ASW, AAW and so on. Comma-separated; the reason a
        /// commander can tell a sub-hunter from an interceptor without knowing the
        /// airframe.</summary>
        public string Roles;
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

        /// <summary>
        /// Surface / Air / Subsurface / Unknown - which of your reach figures applies.
        ///
        /// Stated rather than left to be inferred. A commander that has to pair a
        /// contact with the right one of three reach numbers will eventually pair it with
        /// the wrong one, and it did: it read a missile boat's 8.6nm AIR-defence reach as
        /// its anti-surface reach and began closing a 65nm-capable ship to knife range.
        /// </summary>
        public string Domain;

        /// <summary>
        /// WHAT it is: the class is known, so the threat envelope below is meaningful.
        /// Mirrors the game's <c>Vehicle.Identified</c>, which is set from
        /// <c>Class.HasValue</c>.
        /// </summary>
        public bool Identified;

        /// <summary>
        /// WHOSE it is: the side has been established, so <see cref="Relationship"/> is
        /// meaningful.
        ///
        /// This is the game's <c>Vehicle.IsClassified</c>, which is literally
        /// <c>UnitTaskforce.Value != null</c> - a SIDE, not a domain. This used to be
        /// documented as "the contact has a type (surface/air/subsurface)", which is
        /// <see cref="Identified"/>'s job and led to the two being treated as one thing.
        ///
        /// Classified without identified is the ordinary state of a hostile you have found
        /// but not yet inspected, and it is enough to shoot: the commander prompt leans on
        /// this distinction, so keep the two apart.
        /// </summary>
        public bool Classified;

        /// <summary>Contact is held but currently stale - no recent sensor update.</summary>
        public bool Dormant;

        /// <summary>
        /// Hostile / Friendly / Neutral / Unknown, answered by the game's own
        /// <c>Vehicle.CurrentRelationship()</c> rather than re-derived - see
        /// PictureBuilder.DescribeRelationship for why that distinction cost a battle.
        /// </summary>
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

    /// <summary>How far an ordered air strike has actually got.</summary>
    public class AirstrikeStatus
    {
        /// <summary>Contact id the strike is aimed at, when it is one this force holds.</summary>
        public int TargetContactId;

        /// <summary>Bomb, Missile, SEAD or Jam.</summary>
        public string StrikeType;

        /// <summary>
        /// The strike's own state. "AssigningAircraft" means it has not found aircraft yet -
        /// if it stays there, none are available and nothing will ever fly.
        /// </summary>
        public string State;

        /// <summary>Seconds since the strike was ordered.</summary>
        public float AgeSeconds;

        /// <summary>Aircraft actually committed to it.</summary>
        public int AircraftAssigned;

        /// <summary>The game's own id for this strike, so one can be followed between cycles.</summary>
        public int Id;
    }
}
