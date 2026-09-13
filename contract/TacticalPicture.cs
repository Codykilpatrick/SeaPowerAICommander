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
        /// Whether a search radar is FITTED at all, which is a different question from
        /// whether it is on.
        ///
        /// Without this, "radar off" and "has no radar" look identical, and a Yak-38 -
        /// which carries neither - was ordered to Radiate twice before the executor's
        /// refusal explained why.
        /// </summary>
        public bool HasSearchRadar;

        /// <summary>
        /// Whether an ACTIVE sonar is fitted, and whether a towed array is.
        ///
        /// Same reason <see cref="HasSearchRadar"/> exists: without them "not pinging" and
        /// "has nothing to ping with" look identical, and a SetSonar order spent on a
        /// frigate with no tail is an order spent on nothing.
        /// </summary>
        public bool HasActiveSonar;

        /// <summary>
        /// What the towed array is doing: "Stowed", "DeployedAboveLayer" or
        /// "DeployedBelowLayer". Null when none is fitted.
        ///
        /// Which side of the layer the tail is listening on is the whole point of having
        /// one - a hull sonar above the layer is deaf to a boat sitting under it, and the
        /// array is the only sensor that can be put on the other side.
        /// </summary>
        public string TowedArray;

        /// <summary>
        /// The depth band a submarine has been told to hold: "Surface", "Periscope",
        /// "Shallow", "AboveLayer", "BelowLayer", "Deep" or "VeryDeep". Null for anything
        /// that is not a submarine.
        ///
        /// This is the COMMANDED band, not the observed depth - altitude already carries
        /// that in metres. The two differ while the boat is changing depth, and they also
        /// differ when the boat's own state machine has overridden the band, which is the
        /// case worth seeing: it re-picks a depth on every state change (sprinting,
        /// drifting, prosecuting a contact), so an ordered band does not necessarily hold.
        /// </summary>
        public string CommandedDepth;

        /// <summary>
        /// The formation this unit belongs to and the shape that formation is currently
        /// in. Null when the unit is not in one.
        ///
        /// Two units reporting the same formationName are in the same formation, which is
        /// the only way to tell a screen from a scattering of independent ships.
        /// </summary>
        public string FormationName;
        public string FormationPattern;

        /// <summary>
        /// The base or carrier this air unit would return to, when it has one. Null for
        /// everything else, and null for an aircraft with nowhere to go - which is the
        /// case that matters, because ReturnToBase is refused for it.
        /// </summary>
        public string HomeBaseName;

        /// <summary>
        /// The order the game itself believes this unit is executing - "Identify",
        /// "ReturnToBase", "Attack" and so on. Null when it is under none.
        ///
        /// This is the game's own order slot, not ours, and it is the only confirmation
        /// that an IdentifyContact or ReturnToBase order was taken up rather than dropped
        /// by a unit already busy with something of higher priority.
        /// </summary>
        public string CurrentOrder;

        /// <summary>
        /// Contacts this unit has live engagements against right now. Null when it is
        /// shooting at nothing.
        ///
        /// An attack order stays in standingOrders for as long as the contact is still
        /// held, which says what was ORDERED and not what is happening. A submarine was
        /// described by its own commander as "prosecuting the Alfa contact close aboard"
        /// long after it had stopped - the order was still standing, so the commander
        /// reasoned from it, and planned around an attack that had finished.
        ///
        /// This is read from the unit's own engage tasks, so it is the game's answer rather
        /// than ours. An order that appears in standingOrders but not here is over: the
        /// shots were taken, the target was lost, or the tactical AI dropped it.
        /// </summary>
        public List<int> EngagingContactIds;

        /// <summary>
        /// The name of the state the unit's own AI is currently in - "Default",
        /// "MovingInFormation", "CAP", "ReturnToBase" and so on.
        ///
        /// DIAGNOSTIC. Added because IdentifyContact orders were accepted and then ignored
        /// by every aircraft in a live mission, and nothing anywhere said why: the identify
        /// states are reachable only from a handful of others, so which state a unit is
        /// sitting in decides whether it can be retasked at all, and that was invisible.
        ///
        /// It also explains a depth order being overridden - the band is re-picked on every
        /// submarine state change, and this names the state that did it.
        /// </summary>
        public string AiState;

        /// <summary>
        /// The ammunition TYPES this unit still has, e.g. "Missile, Torpedo, Gun". Null
        /// when it has nothing left to shoot.
        ///
        /// The reach fields say how far it can hit a given kind of target; this says with
        /// what, which is what the weapon field on an attack order needs. A destroyer with
        /// "Gun" alone is a destroyer that has fired off its missiles.
        /// </summary>
        public string WeaponTypes;

        /// <summary>
        /// How badly hurt this unit is, 0-100.
        ///
        /// Summed integrity lost across every system, over the hull's damage capacity.
        /// The commander was previously blind to damage entirely, which is worse than it
        /// sounds: a carrier crawling at 6kt could be conducting flight operations, taking
        /// a clamped speed order, or crippled, and there was no way to tell. It kept
        /// ordering 15kt at it.
        ///
        /// A force cannot be commanded well without knowing which of its ships are still
        /// worth risking - and a damaged high-value unit is the one thing that most needs
        /// to be withdrawn rather than pushed.
        /// </summary>
        public float DamagePercent;

        /// <summary>True when the game has marked this unit disabled - it is still afloat
        /// and still yours, but it is not going to do anything useful.</summary>
        public bool Disabled;

        /// <summary>
        /// True while this ship is conducting flight operations.
        ///
        /// Its speed and heading are NOT yours while this is set. The vessel enters a
        /// dedicated PerformingAirOps state that takes the rudder to turn into the wind
        /// and restores the telegraph it had when air ops began, so a speed order issued
        /// mid-launch is reverted within a tick.
        ///
        /// Reported rather than worked around, because the game is right: turning into the
        /// wind at a controlled speed is what launching aircraft requires. A carrier
        /// launching continuously was ordered to 30kt eleven times and sat at 18-20 every
        /// time, and nothing in the picture explained why.
        /// </summary>
        public bool PerformingAirOps;

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
        /// The weapons fits this deck can send a strike out with, and how many aircraft each
        /// has ready - "AntiShip x4, Strike x8". Null for anything without a flight deck.
        ///
        /// This is what a loadout on a LaunchAirstrike order has to be named from. Without
        /// it the commander would be guessing at ini keys, and a guess that misses is
        /// refused rather than flown.
        ///
        /// It is also the answer to a question the strike type cannot settle: whether the
        /// base can actually put anti-ship weapons on a target. A deck listing only Strike
        /// and StrikeHeavy has nothing to hit a warship with, however the order is phrased.
        /// </summary>
        public string AirstrikeLoadouts;

        /// <summary>
        /// True when this unit can raise an air strike - it has a flight deck with armed
        /// aircraft ready on it.
        ///
        /// THIS EXISTS BECAUSE THE REACH FIELDS SAY THE OPPOSITE. Reach counts ordnance the
        /// unit fires itself, and an airfield fires none, so Andersen AFB reported 0nm
        /// against every domain while holding six F-4E, two B-52G and an AntiShipHeavy fit.
        /// Being built from those figures, unitsInReach listed it for no contact at all,
        /// and the commander - correctly reading the field it had been told was decisive -
        /// concluded the base could not touch anything and never launched a single sortie
        /// from it in a whole mission. Two destroyers did all the shooting.
        ///
        /// A strike's range is its aircraft's, and the game does not check it: nothing in
        /// the strike pipeline compares base to target, so the aircraft launch, fly, and go
        /// bingo if it was too far. That judgement is the commander's to make, which is why
        /// this is a capability flag and not a distance - a made-up radius would be a worse
        /// lie than the zero it replaces.
        /// </summary>
        public bool CanMountAirstrike;

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
        /// Range in nautical miles from the CLOSEST of our own units, and which one.
        ///
        /// <see cref="RangeFromForceNM"/> is measured from the centre of the force, which
        /// is the wrong number for anything detached. A scout stationed 100nm ahead of the
        /// formation had a submarine contact reported at 168nm - the distance from the
        /// formation - and the commander, having nothing better, estimated the scout's own
        /// range at 36nm and ordered a torpedo attack. The real range was far outside the
        /// 40nm the weapon had, the game declined to fire, and nothing said why.
        ///
        /// Engagement decisions are made by a UNIT, so they need that unit's range. This
        /// gives the best case directly, and names the unit so the order can be given to
        /// the one that can actually reach.
        /// </summary>
        public float RangeFromNearestUnitNM;

        /// <summary>Id of the own unit <see cref="RangeFromNearestUnitNM"/> was measured
        /// from. 0 when the force has no positioned units.</summary>
        public int NearestUnitId;

        /// <summary>
        /// Ids of every own unit that can reach this contact from where it is right now,
        /// using the reach that matches the contact's domain. Null when none can.
        ///
        /// <see cref="NearestUnitId"/> answers a different and narrower question. The
        /// nearest unit is frequently not a shooter - it may be a frigate screening ahead
        /// of the cruisers that actually carry the missiles - and for every OTHER unit the
        /// picture gave no range at all, so the commander had nothing to compare its reach
        /// against and guessed.
        ///
        /// That guess cost a strike. Two cruisers were ordered to fire four missiles each
        /// at a Sovremenny; the commander justified the second with "its 90nm reach exceeds
        /// the target's 50nm envelope" - comparing reach to the TARGET'S envelope, because
        /// no range to that ship existed to compare against. It was out of range, the game
        /// declined silently, and four missiles went in where eight were intended.
        ///
        /// Computed from the same nominal reach figures the own units report, so it does
        /// not model sensor channel or terrain masking. Treat it as the shortlist of
        /// plausible shooters, not a firing solution.
        /// </summary>
        public List<int> UnitsInReach;

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

        /// <summary>
        /// The weapons fit the strike is actually flying with - "AntiShip", "StrikeHeavy",
        /// "GuidedStrike" and so on. Null until the strike has picked one.
        ///
        /// The strike TYPE does not determine this. Missile against a ship offers the
        /// anti-ship loadouts first, but the game then chooses whichever loadout in that
        /// pool has the most airframes available rather than the one best suited to the
        /// target, so a base stocked mainly for land attack sends a land-attack fit at a
        /// destroyer and nothing says so. This is the field that says so.
        /// </summary>
        public string Loadout;
    }
}
