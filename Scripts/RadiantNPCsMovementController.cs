using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using FullSerializer;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Utility;


namespace RadiantNPCsMod
{
    public class RadiantNPCsMovementController : MonoBehaviour
    {
        private const float MoveSpeed = 1.3f;
        private const float TurnSpeed = 180f;
        private const float WaypointThreshold = 0.2f;
        private const float GoalThreshold = 0.65f;
        private const float MinLaneOffsetDistance = CityNavigation.HalfTile * 0.28f;
        private const float MaxLaneOffsetDistance = CityNavigation.HalfTile * 0.95f;
        private const float MinLaneClearanceScale = 0.45f;
        private const int MaxSideLaneTiles = 3;
        private const float MaxDynamicLaneWorldOffset = CityNavigation.HalfTile * 2.2f;
        private const float DodgeLookAheadDistance = 1.1f;
        private const float MaxDodgeYawDegrees = 15f;
        private const float HardBlockRadius = CityNavigation.HalfTile * 0.48f;
        private const float SoftCrowdRadius = CityNavigation.HalfTile * 2.1f;
        private const float SeparationRadius = CityNavigation.HalfTile * 1.85f;
        private const float MaxSeparationWeight = 1.15f;
        private const float YieldFacingAngleDegrees = 12f;
        private const int RecoverySearchRadius = 8;
        private const int MaxLocalDetourDistanceIncrease = 3;
        private const int PreferredLaneDistanceSlack = 1;
        private const float TerrainPreferenceMagnitude = 0.28f;
        private const float StepNoiseMagnitude = 0.12f;
        private const float DetourDistancePenalty = 1.35f;
        private const float LaneDriftPenalty = 0.7f;
        private const float BacktrackPenalty = 2.5f;
        private const float LoopPenalty = 5.5f;
        private const float AdjacentOccupancyPenalty = 2.25f;
        private const float NearbyOccupancyPenalty = 0.9f;
        private const float CandidateCrowdPenalty = 1.8f;
        private const float CandidateQueuePenalty = 1.15f;
        private const float MinSpeedMultiplier = 0.88f;
        private const float MaxSpeedMultiplier = 1.16f;
        private const float EncounterPauseMinSeconds = 0.75f;
        private const float EncounterPauseMaxSeconds = 1.5f;
        private const float EncounterPauseRange = 1.1f;
        private const float EncounterCrowdRange = 2.4f;
        private const float EncounterPauseCooldownMin = 8f;
        private const float EncounterPauseCooldownMax = 18f;
        private const float EncounterPauseChance = 0.06f;
        private const int EncounterRequiredFreeNeighbours = 2;
        private const int MaxConversationParticipants = 3;
        private const float ThirdConversationChance = 0.22f;
        private const float ConversationReleaseDelayMin = 0.22f;
        private const float ConversationReleaseDelayMax = 0.65f;
        private const float ConversationFormationMoveSpeedMultiplier = 0.72f;
        private const float ConversationPairRadius = 0.42f;
        private const float ConversationTriangleRadius = 0.58f;
        private const float ConversationSlotThreshold = 0.08f;
        private const float YieldPauseMinSeconds = 0.18f;
        private const float YieldPauseMaxSeconds = 0.42f;
        private const int YieldOccupiedNeighbourThreshold = 2;
        private const float PartnerPreferredSeparation = 1.8f;
        private const float PartnerHardSeparation = 3.4f;
        private const float PartnerGoalLeadTolerance = 0.75f;
        private const float GuardThreatCheckInterval = 0.9f;
        private const float GuardThreatRadius = 10f;

        private static readonly FieldInfo MotorMoveCountField =
            typeof(MobilePersonMotor).GetField("moveCount", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] BillboardFieldNamesToFreeze = new string[] { "currentFrame", "animTimer", "animSpeed" };
        private static readonly List<RadiantNPCsMovementController> ActiveControllers = new List<RadiantNPCsMovementController>();
        private static readonly Dictionary<int, ConversationGroup> ActiveConversationGroups = new Dictionary<int, ConversationGroup>();
        private static int NextConversationGroupId = 1;

        private RadiantNPCsMain main;
        private MobilePersonNPC npc;
        private MobilePersonMotor motor;
        private CityNavigation cityNavigation;
        private RadiantNPCsMain.LocationResidentsDataV1 locationData;
        private RadiantNPCsFlowField activeField;
        private Vector3 locationOrigin;
        private Vector3 activeAnchorLocalPosition;
        private DFPosition occupiedNavPosition = new DFPosition(-1, -1);
        private DFPosition previousNavPosition = new DFPosition(-1, -1);
        private DFPosition olderNavPosition = new DFPosition(-1, -1);
        private DFPosition stepTargetNavPosition = new DFPosition(-1, -1);
        private int residentId = -1;
        private int activeTargetKey = 0;
        private int patrolDirection = 1;
        private float dodgeSidePreference = 1f;
        private float laneOffsetDistance = 0f;
        private float lanePositionBias = 0f;
        private float terrainPreferenceBias = 0f;
        private float moveSpeedMultiplier = 1f;
        private float encounterPauseUntil = -1f;
        private float nextEncounterAllowedAt = 0f;
        private float nextNavigationRecoveryAt = 0f;
        private float nextGuardThreatCheckAt = 0f;
        private float yieldUntil = -1f;
        private Vector3 yieldLookDirection = Vector3.zero;
        private int conversationGroupId = 0;
        private int partnerResidentId = -1;
        private bool directedMovement = false;
        private bool hasStepTarget = false;
        private bool patrolMovement = false;
        private int loggedConfigurations = 0;
        private RadiantNPCsMovementController encounterPartner;
        private bool encounterVisualActive = false;
        private RadiantNPCsMain.ResidentState currentState = RadiantNPCsMain.ResidentState.AtHome;

        private sealed class ConversationGroup
        {
            public int Id;
            public readonly List<RadiantNPCsMovementController> Participants = new List<RadiantNPCsMovementController>(MaxConversationParticipants);
            public Vector3 Anchor;
            public Vector3 Axis;
        }

        public RadiantNPCsMain Main
        {
            get { return main; }
            set { main = value; }
        }

        private void Awake()
        {
            npc = GetComponent<MobilePersonNPC>();
            motor = GetComponent<MobilePersonMotor>();
        }

        private void OnEnable()
        {
            if (!ActiveControllers.Contains(this))
                ActiveControllers.Add(this);
        }

        private void OnDisable()
        {
            LeaveConversationGroup();
            ReleaseTileClaim();
            hasStepTarget = false;
            stepTargetNavPosition = new DFPosition(-1, -1);
            encounterPartner = null;
            encounterPauseUntil = -1f;
            yieldUntil = -1f;
            yieldLookDirection = Vector3.zero;
            ClearEncounterPauseVisual();
            ActiveControllers.Remove(this);
        }

        private void Update()
        {
            if (!directedMovement || npc == null || cityNavigation == null || activeField == null)
                return;
            if (GameManager.IsGamePaused)
                return;
            if (TryPromoteGuardForThreat())
                return;
            if (UpdateYieldPause())
                return;
            if (UpdateEncounterPause())
                return;
            if (TryHoldForPartner())
                return;
            if (encounterVisualActive)
                ClearEncounterPauseVisual();

            if (hasStepTarget)
            {
                if (AdvanceTowardStep())
                    OnStepCompleted();
                return;
            }

            DFPosition currentNav = GetCurrentOccupiedNavPosition();
            if (HasReachedGoal(currentNav))
            {
                HandleArrivalAtGoal();
                return;
            }

            DFPosition nextNav;
            if (!TryResolveNextNav(currentNav, out nextNav))
            {
                if (TryStartYieldPause(currentNav))
                {
                    ApplyEncounterPauseVisual();
                    return;
                }

                if (!TryStartEncounterPause(currentNav))
                {
                    if (TryRecoverFromNavigationFailure())
                        return;
                    SetIdle(true);
                }
                return;
            }

            BeginStep(currentNav, nextNav);
            SetIdle(false);
        }

        public void ConfigureDirectedMovement(RadiantNPCsMain.LocationResidentsDataV1 locationData, RadiantNPCsMain.ResidentDataV1 resident, Vector3 locationOrigin, Vector3 targetLocalPosition, RadiantNPCsMain.ResidentState state)
        {
            if (motor == null)
                motor = GetComponent<MobilePersonMotor>();
            if (npc == null)
                npc = GetComponent<MobilePersonNPC>();
            if (motor == null || npc == null)
                return;

            this.locationData = locationData;
            this.locationOrigin = locationOrigin;
            residentId = resident.residentId;
            currentState = state;
            LeaveConversationGroup();
            previousNavPosition = new DFPosition(-1, -1);
            olderNavPosition = new DFPosition(-1, -1);
            patrolMovement = state == RadiantNPCsMain.ResidentState.Patrol;
            activeTargetKey = resident.currentTargetBuildingKey;
            activeAnchorLocalPosition = targetLocalPosition;
            patrolDirection = patrolMovement && main != null ? main.GetPatrolDirection(locationData, resident.residentId) : 1;
            dodgeSidePreference = ComputeDodgeSidePreference(resident.residentId);
            laneOffsetDistance = ComputeLaneOffsetDistance(resident.residentId);
            lanePositionBias = ComputeLanePositionBias(resident.residentId);
            terrainPreferenceBias = ComputeTerrainPreference(resident.residentId);
            moveSpeedMultiplier = ComputeSpeedMultiplier(resident.residentId);
            encounterPauseUntil = -1f;
            nextNavigationRecoveryAt = 0f;
            nextGuardThreatCheckAt = 0f;
            yieldUntil = -1f;
            yieldLookDirection = Vector3.zero;
            partnerResidentId = resident.partnerResidentId;
            encounterPartner = null;

            cityNavigation = motor.cityNavigation;
            if (cityNavigation == null && GameManager.Instance != null && GameManager.Instance.StreamingWorld != null)
            {
                DaggerfallLocation currentLocationObject = GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;
                if (currentLocationObject != null)
                    cityNavigation = currentLocationObject.GetComponent<CityNavigation>();
            }

            motor.enabled = false;

            if (cityNavigation == null)
            {
                SetIdle(true);
                directedMovement = false;
                activeField = null;
                LogMovement("RadiantNPCs: movement disabled for resident {0} because CityNavigation was unavailable.", resident.residentId);
                return;
            }

            if (!TryAcquireField(targetLocalPosition))
            {
                if (!TryRecoverFromNavigationFailure())
                {
                    SetIdle(true);
                    directedMovement = false;
                    activeField = null;
                    LogMovement("RadiantNPCs: movement disabled for resident {0} because no shared navigation field was available for state {1}.", resident.residentId, state);
                    return;
                }
            }

            hasStepTarget = false;
            stepTargetNavPosition = new DFPosition(-1, -1);
            ReleaseTileClaim();
            SnapToReachableNavCell();
            DFPosition currentNav = ResolveCurrentNavPosition();
            ClaimTile(currentNav);
            DFPosition validationNav;
            if (!HasReachedGoal(currentNav) && !TryResolveNextNav(currentNav, out validationNav))
            {
                if (!TryRecoverFromNavigationFailure() || !HasReachedGoal(currentNav) && !TryResolveNextNav(currentNav, out validationNav))
                {
                    SetIdle(true);
                    directedMovement = false;
                    activeField = null;
                    LogMovement("RadiantNPCs: movement disabled for resident {0} because the shared navigation field was unusable at the current position.", resident.residentId);
                    return;
                }
            }
            directedMovement = true;
            IncrementMoveCount();
            SetIdle(false);
            LogMovement(
                "RadiantNPCs: resident {0} configured shared-field movement toward target {1} (state={2}, source={3}).",
                resident.residentId,
                activeTargetKey,
                state,
                activeField.GeneratedOnGpu ? "GPU" : "CPU");
        }

        public void DisableDirectedMovement()
        {
            if (motor == null)
                motor = GetComponent<MobilePersonMotor>();
            if (motor != null)
                motor.enabled = false;

            LeaveConversationGroup();
            directedMovement = false;
            activeField = null;
            ReleaseTileClaim();
            SetIdle(true);
        }

        private DFPosition GetCurrentOccupiedNavPosition()
        {
            if (IsValidNavPosition(occupiedNavPosition))
                return occupiedNavPosition;

            occupiedNavPosition = ResolveCurrentNavPosition();
            return occupiedNavPosition;
        }

        private void BeginStep(DFPosition currentNav, DFPosition nextNav)
        {
            if (!IsValidNavPosition(nextNav))
                return;

            occupiedNavPosition = currentNav;
            stepTargetNavPosition = nextNav;
            hasStepTarget = true;
        }

        private bool AdvanceTowardStep()
        {
            Vector3 targetScenePosition = GetCandidateScenePosition(occupiedNavPosition, stepTargetNavPosition);
            targetScenePosition.y = transform.position.y;
            Vector3 flatDirection = targetScenePosition - transform.position;
            flatDirection.y = 0;
            if (flatDirection.sqrMagnitude > 0.0001f)
            {
                Vector3 separation = GetLocalSeparationSteering(flatDirection.normalized, stepTargetNavPosition);
                if (separation.sqrMagnitude > 0.0001f)
                    flatDirection = (flatDirection.normalized + separation).normalized * flatDirection.magnitude;
            }

            float dodgeYawDegrees = GetLocalDodgeYawDegrees(flatDirection);
            if (Mathf.Abs(dodgeYawDegrees) > 0.001f)
                flatDirection = Quaternion.AngleAxis(dodgeYawDegrees, Vector3.up) * flatDirection;

            if (flatDirection.sqrMagnitude <= WaypointThreshold * WaypointThreshold)
            {
                transform.position = new Vector3(targetScenePosition.x, transform.position.y, targetScenePosition.z);
                return true;
            }

            Quaternion desiredRotation = Quaternion.LookRotation(flatDirection.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRotation, TurnSpeed * Time.deltaTime);

            float facingAngle = Quaternion.Angle(transform.rotation, desiredRotation);
            float speedFactor = Mathf.Clamp01(1f - facingAngle / 120f);
            float step = MoveSpeed * moveSpeedMultiplier * Mathf.Max(0.2f, speedFactor) * Time.deltaTime;
            float distance = flatDirection.magnitude;
            transform.position += transform.forward * Mathf.Min(step, distance);
            SetIdle(false);
            return false;
        }

        private void OnStepCompleted()
        {
            DFPosition completedNav = occupiedNavPosition;
            DFPosition arrivedNav = stepTargetNavPosition;
            hasStepTarget = false;
            stepTargetNavPosition = new DFPosition(-1, -1);
            olderNavPosition = previousNavPosition;
            previousNavPosition = completedNav;
            occupiedNavPosition = arrivedNav;
            yieldUntil = -1f;
            yieldLookDirection = Vector3.zero;
            IncrementMoveCount();

            if (HasReachedGoal(occupiedNavPosition))
                HandleArrivalAtGoal();
        }

        private void HandleArrivalAtGoal()
        {
            if (patrolMovement)
            {
                if (!TryAdvancePatrolGoal())
                {
                    ReleaseTileClaim();
                    SetIdle(true);
                    directedMovement = false;
                }
                return;
            }

            ReleaseTileClaim();
            if (main != null && main.NotifyResidentReachedTarget(locationData, npc, residentId, currentState, activeTargetKey))
            {
                SetIdle(true);
                directedMovement = false;
                return;
            }

            SetIdle(true);
            directedMovement = false;
        }

        private void ClaimTile(DFPosition navPosition)
        {
            occupiedNavPosition = navPosition;
        }

        private void ReleaseTileClaim()
        {
            occupiedNavPosition = new DFPosition(-1, -1);
            stepTargetNavPosition = new DFPosition(-1, -1);
            hasStepTarget = false;
        }

        private float ComputeSpeedMultiplier(int stableResidentId)
        {
            uint hash = (uint)(stableResidentId * 1103515245 + 12345);
            float t = (hash & 0xffff) / 65535f;
            return Mathf.Lerp(MinSpeedMultiplier, MaxSpeedMultiplier, t);
        }

        private float ComputeLaneOffsetDistance(int stableResidentId)
        {
            uint hash = (uint)(stableResidentId * 668265263 + 2246822519u);
            float t = ((hash >> 1) & 0xffff) / 65535f;
            return Mathf.Lerp(MinLaneOffsetDistance, MaxLaneOffsetDistance, t);
        }

        private float ComputeLanePositionBias(int stableResidentId)
        {
            uint hash = (uint)(stableResidentId * 3266489917u + 374761393u);
            float t = (hash & 0xffff) / 65535f;
            return Mathf.Lerp(-0.95f, 0.95f, t);
        }

        private float ComputeDodgeSidePreference(int stableResidentId)
        {
            uint hash = (uint)(stableResidentId * 2246822519u + 3266489917u);
            return (hash & 1u) == 0u ? -1f : 1f;
        }

        private float ComputeTerrainPreference(int stableResidentId)
        {
            uint hash = (uint)(stableResidentId * 747796405 + 2891336453u);
            float t = (hash & 0xffff) / 65535f;
            return Mathf.Lerp(-TerrainPreferenceMagnitude, TerrainPreferenceMagnitude, t);
        }

        private bool UpdateEncounterPause()
        {
            if (conversationGroupId == 0)
                return false;

            ConversationGroup group;
            if (!TryGetConversationGroup(conversationGroupId, out group))
            {
                conversationGroupId = 0;
                encounterPauseUntil = -1f;
                ClearEncounterPauseVisual();
                return false;
            }

            PruneConversationGroup(group);
            if (conversationGroupId == 0)
                return false;

            if (group.Participants.Count < 2)
                ScheduleConversationEndSoon();

            if (Time.time >= encounterPauseUntil)
            {
                LeaveConversationGroup();
                return false;
            }

            Vector3 slotPosition = GetConversationSlotPosition(group);
            Vector3 toSlot = slotPosition - transform.position;
            toSlot.y = 0f;
            Vector3 lookDirection = toSlot.sqrMagnitude > ConversationSlotThreshold * ConversationSlotThreshold ?
                toSlot.normalized :
                GetConversationLookDirection(group);
            if (lookDirection.sqrMagnitude > 0.0001f)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRotation, TurnSpeed * Time.deltaTime);
            }

            if (toSlot.sqrMagnitude > ConversationSlotThreshold * ConversationSlotThreshold)
            {
                float step = MoveSpeed * moveSpeedMultiplier * ConversationFormationMoveSpeedMultiplier * Time.deltaTime;
                transform.position += transform.forward * Mathf.Min(step, toSlot.magnitude);
            }

            ApplyEncounterPauseVisual();
            return true;
        }

        private bool UpdateYieldPause()
        {
            if (yieldUntil < 0f)
                return false;

            if (Time.time >= yieldUntil)
            {
                yieldUntil = -1f;
                yieldLookDirection = Vector3.zero;
                ClearEncounterPauseVisual();
                return false;
            }

            if (yieldLookDirection.sqrMagnitude > 0.0001f)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(yieldLookDirection.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRotation, TurnSpeed * Time.deltaTime);
            }

            ApplyEncounterPauseVisual();
            return true;
        }

        private bool TryStartYieldPause(DFPosition currentNav)
        {
            if (patrolMovement || currentState == RadiantNPCsMain.ResidentState.Patrol)
                return false;
            if (yieldUntil > Time.time)
                return true;
            DFPosition blockedNav;
            if (!TryGetBlockedDownhillCandidate(currentNav, out blockedNav))
                return false;

            uint hash = (uint)(residentId * 2654435761u ^ (uint)(currentNav.X * 73856093) ^ (uint)(currentNav.Y * 19349663));
            float t = (hash & 1023) / 1023f;
            yieldUntil = Time.time + Mathf.Lerp(YieldPauseMinSeconds, YieldPauseMaxSeconds, t);
            nextEncounterAllowedAt = Mathf.Max(nextEncounterAllowedAt, yieldUntil);
            yieldLookDirection = GetYieldLookDirection(currentNav, blockedNav);
            return true;
        }

        private bool TryStartEncounterPause(DFPosition currentNav)
        {
            if (patrolMovement || currentState == RadiantNPCsMain.ResidentState.Patrol)
                return false;
            if (Time.time < nextEncounterAllowedAt)
                return false;
            if (HasBlockedDownhillCandidate(currentNav))
                return false;
            if (!HasEncounterSpace())
                return false;
            if (CountNearbyDirectedControllers(EncounterCrowdRange) > 1)
                return false;

            List<RadiantNPCsMovementController> participants;
            if (!TryBuildConversationParticipants(currentNav, out participants))
                return false;

            uint hash = (uint)(residentId * 73856093 ^ participants[1].residentId * 19349663 ^ Mathf.RoundToInt(Time.time));
            float chanceRoll = (hash & 1023) / 1023f;
            if (chanceRoll > EncounterPauseChance)
                return false;

            float duration = Mathf.Lerp(EncounterPauseMinSeconds, EncounterPauseMaxSeconds, ((hash >> 10) & 1023) / 1023f);
            BeginConversationGroup(participants, duration);
            return true;
        }

        private void BeginConversationGroup(List<RadiantNPCsMovementController> participants, float duration)
        {
            if (participants == null || participants.Count < 2)
                return;

            int groupId = NextConversationGroupId++;
            ConversationGroup group = new ConversationGroup();
            group.Id = groupId;
            group.Anchor = ComputeConversationAnchor(participants);
            group.Axis = ComputeConversationAxis(participants);
            ActiveConversationGroups[groupId] = group;

            int orderOffset = Mathf.Abs(residentId * 31 + participants[0].residentId * 17 + participants[participants.Count - 1].residentId * 13) % participants.Count;
            float leaveAt = Time.time + duration;
            for (int i = 0; i < participants.Count; i++)
            {
                RadiantNPCsMovementController participant = participants[(orderOffset + i) % participants.Count];
                if (participant == null)
                    continue;

                group.Participants.Add(participant);
                participant.JoinConversationGroup(groupId, leaveAt);
                leaveAt += participant.ComputeConversationReleaseDelay(participants.Count, i);
            }
        }

        private void JoinConversationGroup(int groupId, float leaveAt)
        {
            if (conversationGroupId != 0 && conversationGroupId != groupId)
                LeaveConversationGroup();

            conversationGroupId = groupId;
            encounterPauseUntil = leaveAt;
            encounterPartner = null;
            yieldUntil = -1f;
            yieldLookDirection = Vector3.zero;
            nextEncounterAllowedAt = Mathf.Max(
                nextEncounterAllowedAt,
                leaveAt + Mathf.Lerp(EncounterPauseCooldownMin, EncounterPauseCooldownMax, ((residentId * 97) & 255) / 255f));
            ApplyEncounterPauseVisual();
        }

        private void LeaveConversationGroup()
        {
            int groupId = conversationGroupId;
            conversationGroupId = 0;
            encounterPauseUntil = -1f;
            encounterPartner = null;

            ConversationGroup group;
            if (groupId != 0 && TryGetConversationGroup(groupId, out group))
            {
                group.Participants.Remove(this);
                if (group.Participants.Count > 0)
                {
                    group.Anchor = ComputeConversationAnchor(group.Participants);
                    group.Axis = ComputeConversationAxis(group.Participants);
                }
                if (group.Participants.Count < 2)
                {
                    for (int i = 0; i < group.Participants.Count; i++)
                    {
                        RadiantNPCsMovementController remaining = group.Participants[i];
                        if (remaining != null)
                            remaining.ScheduleConversationEndSoon();
                    }

                    if (group.Participants.Count == 0)
                        ActiveConversationGroups.Remove(groupId);
                }
            }

            ClearEncounterPauseVisual();
        }

        private void ScheduleConversationEndSoon()
        {
            if (conversationGroupId == 0)
                return;

            float releaseDelay = ComputeConversationReleaseDelay(1, 0);
            float releaseAt = Time.time + releaseDelay;
            if (encounterPauseUntil < 0f || encounterPauseUntil > releaseAt)
                encounterPauseUntil = releaseAt;
        }

        private float ComputeConversationReleaseDelay(int participantCount, int orderIndex)
        {
            unchecked
            {
                uint hash = (uint)(residentId * 1597334677u + (uint)(participantCount * 3812015801u) + (uint)(orderIndex * 958689123u));
                float t = (hash & 1023) / 1023f;
                return Mathf.Lerp(ConversationReleaseDelayMin, ConversationReleaseDelayMax, t);
            }
        }

        private bool TryBuildConversationParticipants(DFPosition currentNav, out List<RadiantNPCsMovementController> participants)
        {
            participants = null;

            RadiantNPCsMovementController primary = FindEncounterCandidate();
            if (!CanStartConversationWith(currentNav, primary))
                return false;

            participants = new List<RadiantNPCsMovementController>(MaxConversationParticipants);
            participants.Add(this);
            participants.Add(primary);

            RadiantNPCsMovementController third;
            if (participants.Count < MaxConversationParticipants && TryFindThirdConversationCandidate(primary, out third))
                participants.Add(third);

            return participants.Count >= 2;
        }

        private bool CanStartConversationWith(DFPosition currentNav, RadiantNPCsMovementController other)
        {
            if (other == null || other == this)
                return false;
            if (!other.directedMovement || other.patrolMovement || other.conversationGroupId != 0)
                return false;
            if (other.yieldUntil > Time.time)
                return false;
            if (activeTargetKey != 0 && activeTargetKey == other.activeTargetKey)
                return false;

            DFPosition otherNav = other.GetCurrentOccupiedNavPosition();
            if (other.HasBlockedDownhillCandidate(otherNav))
                return false;
            if (!other.HasEncounterSpace())
                return false;
            if (other.CountNearbyDirectedControllers(EncounterCrowdRange) > 1)
                return false;

            Vector3 delta = other.transform.position - transform.position;
            delta.y = 0;
            return delta.magnitude <= EncounterPauseRange;
        }

        private bool TryFindThirdConversationCandidate(RadiantNPCsMovementController primary, out RadiantNPCsMovementController third)
        {
            third = null;
            if (primary == null)
                return false;

            uint hash = (uint)(residentId * 2654435761u ^ primary.residentId * 2246822519u);
            float chanceRoll = (hash & 1023) / 1023f;
            if (chanceRoll > ThirdConversationChance)
                return false;

            Vector3 midpoint = (transform.position + primary.transform.position) * 0.5f;
            float bestDistanceSq = float.MaxValue;
            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                RadiantNPCsMovementController other = ActiveControllers[i];
                if (other == null || other == this || other == primary)
                    continue;
                if (!other.directedMovement || other.patrolMovement || other.conversationGroupId != 0)
                    continue;
                if (other.yieldUntil > Time.time)
                    continue;

                DFPosition otherNav = other.GetCurrentOccupiedNavPosition();
                if (other.HasBlockedDownhillCandidate(otherNav) || !other.HasEncounterSpace())
                    continue;

                Vector3 deltaToMidpoint = other.transform.position - midpoint;
                deltaToMidpoint.y = 0;
                if (deltaToMidpoint.sqrMagnitude > EncounterPauseRange * EncounterPauseRange)
                    continue;

                Vector3 deltaToThis = other.transform.position - transform.position;
                deltaToThis.y = 0;
                if (deltaToThis.magnitude > EncounterPauseRange * 1.35f)
                    continue;

                Vector3 deltaToPrimary = other.transform.position - primary.transform.position;
                deltaToPrimary.y = 0;
                if (deltaToPrimary.magnitude > EncounterPauseRange * 1.35f)
                    continue;

                if (other.CountNearbyDirectedControllers(EncounterCrowdRange) > 2)
                    continue;

                if (deltaToMidpoint.sqrMagnitude < bestDistanceSq)
                {
                    bestDistanceSq = deltaToMidpoint.sqrMagnitude;
                    third = other;
                }
            }

            return third != null;
        }

        private RadiantNPCsMovementController FindEncounterCandidate()
        {
            RadiantNPCsMovementController best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                RadiantNPCsMovementController other = ActiveControllers[i];
                if (other == null || other == this)
                    continue;
                if (!other.directedMovement || other.patrolMovement || other.conversationGroupId != 0)
                    continue;
                if (other.yieldUntil > Time.time)
                    continue;

                Vector3 delta = other.transform.position - transform.position;
                delta.y = 0;
                float distance = delta.magnitude;
                if (distance > EncounterPauseRange || distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = other;
            }

            return best;
        }

        private bool TryGetConversationGroup(int groupId, out ConversationGroup group)
        {
            return ActiveConversationGroups.TryGetValue(groupId, out group);
        }

        private void PruneConversationGroup(ConversationGroup group)
        {
            if (group == null)
                return;

            for (int i = group.Participants.Count - 1; i >= 0; i--)
            {
                RadiantNPCsMovementController participant = group.Participants[i];
                if (IsConversationParticipantValid(participant, group.Id))
                    continue;

                group.Participants.RemoveAt(i);
                if (participant != null && participant.conversationGroupId == group.Id)
                {
                    participant.conversationGroupId = 0;
                    participant.encounterPauseUntil = -1f;
                    participant.encounterPartner = null;
                    participant.ClearEncounterPauseVisual();
                }
            }

            if (group.Participants.Count == 0)
            {
                ActiveConversationGroups.Remove(group.Id);
                return;
            }

            group.Anchor = ComputeConversationAnchor(group.Participants);
            group.Axis = ComputeConversationAxis(group.Participants);

            if (group.Participants.Count < 2)
            {
                for (int i = 0; i < group.Participants.Count; i++)
                {
                    RadiantNPCsMovementController remaining = group.Participants[i];
                    if (remaining != null)
                        remaining.ScheduleConversationEndSoon();
                }
            }
        }

        private bool IsConversationParticipantValid(RadiantNPCsMovementController participant, int expectedGroupId)
        {
            return participant != null &&
                   participant.enabled &&
                   participant.gameObject.activeInHierarchy &&
                   participant.directedMovement &&
                   participant.conversationGroupId == expectedGroupId;
        }

        private Vector3 GetConversationLookDirection(ConversationGroup group)
        {
            Vector3 focusPosition = Vector3.zero;
            int focusCount = 0;
            for (int i = 0; i < group.Participants.Count; i++)
            {
                RadiantNPCsMovementController participant = group.Participants[i];
                if (participant == null || participant == this)
                    continue;

                focusPosition += participant.transform.position;
                focusCount++;
            }

            if (focusCount == 0)
                return transform.forward;

            focusPosition /= focusCount;
            Vector3 lookDirection = focusPosition - transform.position;
            lookDirection.y = 0f;
            return lookDirection.sqrMagnitude > 0.0001f ? lookDirection.normalized : transform.forward;
        }

        private Vector3 GetConversationSlotPosition(ConversationGroup group)
        {
            int participantCount = group.Participants.Count;
            int index = group.Participants.IndexOf(this);
            if (index < 0)
                return transform.position;

            if (participantCount <= 1)
                return group.Anchor;

            Vector3 axis = group.Axis.sqrMagnitude > 0.0001f ? group.Axis.normalized : transform.forward;
            if (participantCount == 2)
            {
                float side = index == 0 ? -1f : 1f;
                return group.Anchor + axis * side * ConversationPairRadius;
            }

            float angleOffset = index * 120f;
            Vector3 rotated = Quaternion.AngleAxis(angleOffset, Vector3.up) * axis;
            return group.Anchor + rotated * ConversationTriangleRadius;
        }

        private Vector3 ComputeConversationAnchor(IList<RadiantNPCsMovementController> participants)
        {
            Vector3 anchor = Vector3.zero;
            int count = 0;
            for (int i = 0; i < participants.Count; i++)
            {
                RadiantNPCsMovementController participant = participants[i];
                if (participant == null)
                    continue;

                anchor += participant.transform.position;
                count++;
            }

            if (count == 0)
                return Vector3.zero;

            anchor /= count;
            return anchor;
        }

        private Vector3 ComputeConversationAxis(IList<RadiantNPCsMovementController> participants)
        {
            if (participants == null || participants.Count == 0)
                return Vector3.forward;

            if (participants.Count == 2)
            {
                Vector3 axis = participants[1].transform.position - participants[0].transform.position;
                axis.y = 0f;
                if (axis.sqrMagnitude > 0.0001f)
                    return axis.normalized;
            }

            Vector3 anchor = ComputeConversationAnchor(participants);
            Vector3 fallback = participants[0].transform.position - anchor;
            fallback.y = 0f;
            if (fallback.sqrMagnitude > 0.0001f)
                return fallback.normalized;

            return participants[0].transform.forward;
        }

        private void ApplyEncounterPauseVisual()
        {
            if (npc == null || npc.Asset == null)
                return;

            if (!encounterVisualActive || npc.Asset.IsIdle)
                npc.Asset.IsIdle = false;

            FreezeDirectionalMovePose();
            encounterVisualActive = true;
        }

        private void ClearEncounterPauseVisual()
        {
            if (!encounterVisualActive)
                return;

            encounterVisualActive = false;
            if (npc != null && npc.Asset != null)
                npc.Asset.IsIdle = true;
        }

        private void FreezeDirectionalMovePose()
        {
            if (npc == null || npc.Asset == null)
                return;

            System.Type assetType = npc.Asset.GetType();
            object moveAnims = GetPrivateField(assetType, npc.Asset, "moveAnims");
            if (moveAnims != null)
                SetPrivateField(assetType, npc.Asset, "stateAnims", moveAnims);

            SetPrivateField(assetType, npc.Asset, "currentAnimState", 1);
            SetPrivateField(assetType, npc.Asset, "lastOrientation", -1);
            SetPrivateField(assetType, npc.Asset, "currentFrame", 0);
            SetPrivateField(assetType, npc.Asset, "animTimer", 0f);
            SetPrivateField(assetType, npc.Asset, "animSpeed", 0.01f);
            InvokePrivateMethod(assetType, npc.Asset, "UpdateOrientation");
        }

        private void SetPrivateField(System.Type type, object instance, string fieldName, object value)
        {
            if (type == null || instance == null)
                return;

            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                if (value != null && field.FieldType.IsEnum && !(value is Enum))
                    value = Enum.ToObject(field.FieldType, value);
                field.SetValue(instance, value);
            }
        }

        private object GetPrivateField(System.Type type, object instance, string fieldName)
        {
            if (type == null || instance == null)
                return null;

            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                return null;

            return field.GetValue(instance);
        }

        private void InvokePrivateMethod(System.Type type, object instance, string methodName)
        {
            if (type == null || instance == null)
                return;

            MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null)
                method.Invoke(instance, null);
        }

        private bool IsTileClaimedByOther(DFPosition navPosition)
        {
            if (!IsValidNavPosition(navPosition))
                return false;

            return IsCandidateHardBlocked(navPosition, navPosition);
        }

        private bool IsValidNavPosition(DFPosition navPosition)
        {
            return navPosition.X >= 0 && navPosition.Y >= 0;
        }

        private bool TryAcquireField(Vector3 fallbackTargetLocalPosition)
        {
            activeField = null;
            if (main == null || locationData == null)
                return false;

            if (patrolMovement)
            {
                int resolvedTargetKey;
                Vector3 anchorLocalPosition;
                RadiantNPCsFlowField field;
                if (!main.TryGetPatrolFlowField(locationData, residentId, activeTargetKey, out resolvedTargetKey, out anchorLocalPosition, out field))
                    return false;

                activeTargetKey = resolvedTargetKey;
                activeAnchorLocalPosition = anchorLocalPosition;
                activeField = field;
                return activeField != null;
            }

            Vector3 resolvedAnchorLocalPosition;
            if (!main.TryGetBuildingFlowField(locationData, activeTargetKey, residentId, fallbackTargetLocalPosition, out resolvedAnchorLocalPosition, out activeField))
                return false;

            activeAnchorLocalPosition = resolvedAnchorLocalPosition;
            return activeField != null;
        }

        private bool TryRecoverFromNavigationFailure()
        {
            if (main == null || locationData == null || residentId < 0)
                return false;
            if (Time.time < nextNavigationRecoveryAt)
                return false;

            int resolvedTargetKey;
            Vector3 resolvedAnchorLocalPosition;
            RadiantNPCsFlowField field;
            if (!main.TryRetargetResidentAfterNavigationFailure(locationData, residentId, currentState, activeTargetKey, out resolvedTargetKey, out resolvedAnchorLocalPosition, out field, preferCpu: activeField != null && !activeField.GeneratedOnGpu))
            {
                nextNavigationRecoveryAt = Time.time + 1.5f;
                return false;
            }

            activeTargetKey = resolvedTargetKey;
            activeAnchorLocalPosition = resolvedAnchorLocalPosition;
            activeField = field;
            encounterPauseUntil = -1f;
            yieldUntil = -1f;
            yieldLookDirection = Vector3.zero;
            nextNavigationRecoveryAt = Time.time + 0.75f;
            LogMovement("RadiantNPCs: resident {0} retargeted to {1} after navigation failure.", residentId, activeTargetKey);
            return activeField != null;
        }

        private bool TryHoldForPartner()
        {
            if (partnerResidentId <= 0 || main == null || locationData == null)
                return false;
            if (yieldUntil > Time.time || conversationGroupId != 0)
                return false;

            MobilePersonNPC partnerNpc;
            if (!main.TryGetActiveResidentNpc(locationData.mapId, partnerResidentId, out partnerNpc) || partnerNpc == null)
                return false;

            RadiantNPCsMovementController partnerController = partnerNpc.GetComponent<RadiantNPCsMovementController>();
            if (partnerController == null || !partnerController.enabled || !partnerController.directedMovement)
                return false;
            if (partnerController.currentState != currentState)
                return false;

            Vector3 toPartner = partnerNpc.transform.position - transform.position;
            toPartner.y = 0f;
            float separation = toPartner.magnitude;
            if (separation <= PartnerPreferredSeparation)
                return false;

            float myGoalDistance = GetGoalSceneDistance();
            float partnerGoalDistance = partnerController.GetGoalSceneDistance();
            bool iAmAhead = myGoalDistance + PartnerGoalLeadTolerance < partnerGoalDistance;
            bool partnerDelayed = partnerController.yieldUntil > Time.time ||
                                  partnerController.conversationGroupId != 0 ||
                                  (!partnerController.hasStepTarget && separation > PartnerPreferredSeparation);
            if (!iAmAhead && separation < PartnerHardSeparation)
                return false;
            if (!partnerDelayed && separation < PartnerHardSeparation)
                return false;

            if (toPartner.sqrMagnitude > 0.0001f)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(toPartner.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRotation, TurnSpeed * Time.deltaTime);
            }

            ApplyEncounterPauseVisual();
            return true;
        }

        private float GetGoalSceneDistance()
        {
            Vector3 goalScenePosition = locationOrigin + activeAnchorLocalPosition;
            goalScenePosition.y = transform.position.y;
            Vector3 delta = goalScenePosition - transform.position;
            delta.y = 0f;
            return delta.magnitude;
        }

        private bool TryPromoteGuardForThreat()
        {
            if (!patrolMovement || main == null || npc == null)
                return false;
            if (Time.time < nextGuardThreatCheckAt)
                return false;

            nextGuardThreatCheckAt = Time.time + GuardThreatCheckInterval;

            DaggerfallEntityBehaviour threat = main.FindNearbyHostileThreat(transform.position, GuardThreatRadius);
            if (threat == null)
                return false;

            if (main.PromoteGuardResidentToActualGuard(locationData, residentId, npc, threat))
            {
                directedMovement = false;
                return true;
            }

            return false;
        }

        private bool TrySwitchToCpuField()
        {
            if (main == null || locationData == null)
                return false;

            if (patrolMovement)
            {
                int resolvedTargetKey;
                Vector3 anchorLocalPosition;
                RadiantNPCsFlowField field;
                if (!main.TryGetPatrolFlowField(locationData, residentId, activeTargetKey, out resolvedTargetKey, out anchorLocalPosition, out field, preferCpu: true))
                    return false;

                activeTargetKey = resolvedTargetKey;
                activeAnchorLocalPosition = anchorLocalPosition;
                activeField = field;
            }
            else
            {
                Vector3 resolvedAnchorLocalPosition;
                RadiantNPCsFlowField field;
                if (!main.TryGetBuildingFlowField(locationData, activeTargetKey, residentId, activeAnchorLocalPosition, out resolvedAnchorLocalPosition, out field, preferCpu: true))
                    return false;

                activeAnchorLocalPosition = resolvedAnchorLocalPosition;
                activeField = field;
            }

            if (activeField != null)
                LogMovement("RadiantNPCs: resident {0} fell back to CPU shared field for target {1}.", residentId, activeTargetKey);

            return activeField != null;
        }

        private bool TryAdvancePatrolGoal()
        {
            if (!patrolMovement || main == null || locationData == null)
                return false;

            int nextTargetKey;
            Vector3 nextAnchorLocalPosition;
            RadiantNPCsFlowField nextField;
            if (!main.TryAdvancePatrolFlowField(locationData, activeTargetKey, patrolDirection, out nextTargetKey, out nextAnchorLocalPosition, out nextField))
                return false;

            activeTargetKey = nextTargetKey;
            activeAnchorLocalPosition = nextAnchorLocalPosition;
            activeField = nextField;
            directedMovement = activeField != null;
            if (directedMovement)
            {
                IncrementMoveCount();
                SetIdle(false);
            }

            return directedMovement;
        }

        private void SnapToReachableNavCell()
        {
            if (activeField == null || cityNavigation == null)
                return;

            DFPosition currentNav = ResolveCurrentNavPosition();
            DFPosition reachableNav;
            if (!TryFindNearestUnoccupiedReachableNav(currentNav, RecoverySearchRadius * 4, out reachableNav))
                return;

            if (reachableNav.X == currentNav.X && reachableNav.Y == currentNav.Y)
                return;

            Vector3 snappedPosition = GetScenePosition(reachableNav);
            snappedPosition.y = transform.position.y;
            transform.position = snappedPosition;
        }

        private bool TryFindNearestUnoccupiedReachableNav(DFPosition origin, int radius, out DFPosition reachableNav)
        {
            reachableNav = origin;
            if (activeField == null)
                return false;

            int bestScore = int.MaxValue;
            int bestDistance = int.MaxValue;
            bool found = false;
            for (int r = 0; r <= radius; r++)
            {
                for (int y = -r; y <= r; y++)
                {
                    for (int x = -r; x <= r; x++)
                    {
                        DFPosition candidate = new DFPosition(origin.X + x, origin.Y + y);
                        int candidateDistance;
                        if (!activeField.TryGetDistance(candidate, out candidateDistance))
                            continue;
                        if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                            continue;

                        int score = Mathf.Abs(x) + Mathf.Abs(y);
                        float crowdPenalty = ComputeCandidateCrowdPenalty(origin, candidate);
                        if (IsCandidateHardBlocked(origin, candidate))
                            crowdPenalty += 4f;

                        int totalScore = score * 4 + candidateDistance + Mathf.RoundToInt(crowdPenalty * 3f);
                        if (!found || totalScore < bestScore || (totalScore == bestScore && candidateDistance < bestDistance))
                        {
                            found = true;
                            bestScore = totalScore;
                            bestDistance = candidateDistance;
                            reachableNav = candidate;
                        }
                    }
                }

                if (found)
                    return true;
            }

            return false;
        }

        private bool TryResolveNextNav(DFPosition currentNav, out DFPosition nextNav)
        {
            nextNav = currentNav;
            if (activeField == null)
                return false;

            if (TryResolveNextNavInternal(currentNav, out nextNav))
                return true;

            if (activeField.GeneratedOnGpu && TrySwitchToCpuField())
                return TryResolveNextNavInternal(currentNav, out nextNav);

            return false;
        }

        private bool TryResolveNextNavInternal(DFPosition currentNav, out DFPosition nextNav)
        {
            nextNav = currentNav;
            if (activeField == null)
                return false;

            if (TrySelectPreferredDownhillStep(currentNav, out nextNav))
                return true;

            DFPosition blockedNav;
            if (TryGetBlockedDownhillCandidate(currentNav, out blockedNav))
            {
                if (TrySelectPreferredSideStep(currentNav, blockedNav, out nextNav))
                    return true;

                return false;
            }

            DFPosition recoveryNav;
            if (activeField.TryFindNearestNavigableCell(currentNav, RecoverySearchRadius, out recoveryNav) &&
                TrySelectRecoveryStep(currentNav, recoveryNav, out nextNav))
                return true;

            return TryFindAlternativeUnoccupiedStep(currentNav, out nextNav);
        }

        private bool TrySelectPreferredDownhillStep(DFPosition currentNav, out DFPosition nextNav)
        {
            nextNav = currentNav;
            if (activeField == null)
                return false;

            int currentDistance;
            if (!activeField.TryGetDistance(currentNav, out currentDistance))
                return false;

            float bestScore = float.MaxValue;
            bool found = false;
            for (int i = 0; i < 4; i++)
            {
                DFPosition candidate = GetAdjacentNavPosition(currentNav, i);

                if (IsCandidateHardBlocked(currentNav, candidate))
                    continue;

                int candidateDistance;
                if (!activeField.TryGetDistance(candidate, out candidateDistance))
                    continue;
                if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                    continue;
                if (candidateDistance > currentDistance + PreferredLaneDistanceSlack)
                    continue;

                float score = GetPersonalizedStepScore(currentNav, candidate, candidateDistance);
                if (candidateDistance > currentDistance)
                    score += (candidateDistance - currentDistance) * LaneDriftPenalty;
                score += GetRecentPositionPenalty(candidate);
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    nextNav = candidate;
                }
            }

            return found;
        }

        private bool TrySelectRecoveryStep(DFPosition currentNav, DFPosition recoveryNav, out DFPosition nextNav)
        {
            nextNav = currentNav;
            if (!IsValidNavPosition(recoveryNav))
                return false;

            float bestScore = float.MaxValue;
            bool found = false;
            for (int i = 0; i < 4; i++)
            {
                DFPosition candidate = GetAdjacentNavPosition(currentNav, i);
                if (IsCandidateHardBlocked(currentNav, candidate))
                    continue;

                int candidateDistance;
                if (!activeField.TryGetDistance(candidate, out candidateDistance))
                    continue;
                if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                    continue;

                int recoveryOffset = Mathf.Abs(candidate.X - recoveryNav.X) + Mathf.Abs(candidate.Y - recoveryNav.Y);
                float candidateScore = GetPersonalizedStepScore(currentNav, candidate, candidateDistance) + recoveryOffset * 0.75f;
                candidateScore += GetRecentPositionPenalty(candidate);

                if (!found || candidateScore < bestScore)
                {
                    found = true;
                    bestScore = candidateScore;
                    nextNav = candidate;
                }
            }

            return found;
        }

        private float GetPersonalizedStepScore(DFPosition currentNav, DFPosition navPosition, int candidateDistance)
        {
            int tileWeight = cityNavigation != null ? cityNavigation.GetNavGridWeightLocal(navPosition) : 0;
            float normalizedWeight = Mathf.InverseLerp(1f, 15f, tileWeight);
            float terrainBias = -terrainPreferenceBias * normalizedWeight;
            float localNoise = ComputeStepNoise(navPosition);
            float occupancyPenalty = ComputeCandidateCrowdPenalty(currentNav, navPosition);
            return candidateDistance + terrainBias + localNoise + occupancyPenalty;
        }

        private float ComputeStepNoise(DFPosition navPosition)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)residentId) * 16777619u;
                hash = (hash ^ (uint)navPosition.X) * 16777619u;
                hash = (hash ^ (uint)navPosition.Y) * 16777619u;
                float t = (hash & 0xffff) / 65535f;
                return Mathf.Lerp(-StepNoiseMagnitude, StepNoiseMagnitude, t);
            }
        }

        private bool TryFindAlternativeUnoccupiedStep(DFPosition currentNav, out DFPosition nextNav)
        {
            nextNav = currentNav;
            if (activeField == null)
                return false;

            int currentDistance;
            if (!activeField.TryGetDistance(currentNav, out currentDistance))
                currentDistance = RadiantNPCsFlowFieldGenerator.InfiniteDistance;

            float bestScore = float.MaxValue;
            bool found = false;
            for (int i = 0; i < 4; i++)
            {
                DFPosition candidate = GetAdjacentNavPosition(currentNav, i);

                if (IsCandidateHardBlocked(currentNav, candidate))
                    continue;

                int candidateDistance;
                if (!activeField.TryGetDistance(candidate, out candidateDistance))
                    continue;
                if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                    continue;
                if (currentDistance < RadiantNPCsFlowFieldGenerator.InfiniteDistance &&
                    candidateDistance - currentDistance > MaxLocalDetourDistanceIncrease)
                    continue;

                float candidateScore = GetPersonalizedStepScore(currentNav, candidate, candidateDistance);
                if (currentDistance < RadiantNPCsFlowFieldGenerator.InfiniteDistance && candidateDistance > currentDistance)
                    candidateScore += (candidateDistance - currentDistance) * DetourDistancePenalty;
                candidateScore += GetRecentPositionPenalty(candidate);
                if (!found || candidateScore < bestScore)
                {
                    found = true;
                    bestScore = candidateScore;
                    nextNav = candidate;
                }
            }

            return found;
        }

        private int CountNearbyDirectedControllers(float radius)
        {
            float radiusSq = radius * radius;
            int count = 0;
            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                RadiantNPCsMovementController other = ActiveControllers[i];
                if (other == null || other == this || !other.directedMovement)
                    continue;

                Vector3 delta = other.transform.position - transform.position;
                delta.y = 0;
                if (delta.sqrMagnitude <= radiusSq)
                    count++;
            }

            return count;
        }

        private Vector3 GetSegmentLaneOffset(DFPosition currentNav, DFPosition nextNav)
        {
            if (!IsValidNavPosition(currentNav) || !IsValidNavPosition(nextNav))
                return Vector3.zero;
            if (currentNav.X == nextNav.X && currentNav.Y == nextNav.Y)
                return Vector3.zero;

            Vector3 segmentDirection = GetScenePosition(nextNav) - GetScenePosition(currentNav);
            segmentDirection.y = 0f;
            if (segmentDirection.sqrMagnitude <= 0.0001f)
                return Vector3.zero;

            float signedOffset = GetSignedLaneOffset(currentNav, nextNav);
            if (Mathf.Abs(signedOffset) <= 0.0001f)
                return Vector3.zero;

            Vector3 right = new Vector3(segmentDirection.normalized.z, 0f, -segmentDirection.normalized.x);
            return right * signedOffset;
        }

        private float GetSignedLaneOffset(DFPosition currentNav, DFPosition nextNav)
        {
            DFPosition rightOffset = GetRightSideOffset(currentNav, nextNav);
            if (!IsValidSideOffset(rightOffset))
                return 0f;

            float rightClearance = ComputeLaneClearance(currentNav, nextNav, rightOffset);
            float leftClearance = ComputeLaneClearance(currentNav, nextNav, new DFPosition(-rightOffset.X, -rightOffset.Y));
            float rightWidth = ComputeAvailableLaneWidth(currentNav, nextNav, rightOffset, rightClearance);
            float leftWidth = ComputeAvailableLaneWidth(currentNav, nextNav, new DFPosition(-rightOffset.X, -rightOffset.Y), leftClearance);
            float desiredBias = lanePositionBias;
            if (Mathf.Abs(desiredBias) < 0.05f)
                desiredBias = dodgeSidePreference * 0.2f;

            if (desiredBias >= 0f)
                return Mathf.Min(MaxDynamicLaneWorldOffset, rightWidth) * desiredBias;

            return -Mathf.Min(MaxDynamicLaneWorldOffset, leftWidth) * -desiredBias;
        }

        private float ComputeLaneClearance(DFPosition currentNav, DFPosition nextNav, DFPosition sideOffset)
        {
            if (!IsValidSideOffset(sideOffset))
                return 0f;

            float score = 0f;
            int samples = 0;

            DFPosition currentSide = new DFPosition(currentNav.X + sideOffset.X, currentNav.Y + sideOffset.Y);
            if (IsWalkableNav(currentSide))
            {
                score += IsTileClaimedByOther(currentSide) ? 0.35f : 1f;
                samples++;
            }

            DFPosition nextSide = new DFPosition(nextNav.X + sideOffset.X, nextNav.Y + sideOffset.Y);
            if (IsWalkableNav(nextSide))
            {
                score += IsTileClaimedByOther(nextSide) ? 0.35f : 1f;
                samples++;
            }

            if (samples == 0)
                return 0f;

            return Mathf.Clamp01(score / samples);
        }

        private float ComputeAvailableLaneWidth(DFPosition currentNav, DFPosition nextNav, DFPosition sideOffset, float clearance)
        {
            if (!IsValidSideOffset(sideOffset))
                return 0f;

            float width = laneOffsetDistance * Mathf.Max(MinLaneClearanceScale, clearance);
            DFPosition sampleOffset = sideOffset;
            for (int i = 0; i < MaxSideLaneTiles; i++)
            {
                DFPosition currentSide = new DFPosition(currentNav.X + sampleOffset.X, currentNav.Y + sampleOffset.Y);
                DFPosition nextSide = new DFPosition(nextNav.X + sampleOffset.X, nextNav.Y + sampleOffset.Y);
                if (!IsWalkableNav(currentSide) && !IsWalkableNav(nextSide))
                    break;

                width += CityNavigation.HalfTile * 0.82f;
                sampleOffset = new DFPosition(sampleOffset.X + sideOffset.X, sampleOffset.Y + sideOffset.Y);
            }

            return Mathf.Min(MaxDynamicLaneWorldOffset, width);
        }

        private bool IsWalkableNav(DFPosition navPosition)
        {
            return cityNavigation != null &&
                   navPosition.X >= 0 &&
                   navPosition.X < cityNavigation.NavGridWidth &&
                   navPosition.Y >= 0 &&
                   navPosition.Y < cityNavigation.NavGridHeight &&
                   cityNavigation.GetNavGridWeightLocal(navPosition) > 0;
        }

        private DFPosition GetRightSideOffset(DFPosition currentNav, DFPosition nextNav)
        {
            int dx = Mathf.Clamp(nextNav.X - currentNav.X, -1, 1);
            int dy = Mathf.Clamp(nextNav.Y - currentNav.Y, -1, 1);
            return new DFPosition(dy, -dx);
        }

        private bool IsValidSideOffset(DFPosition sideOffset)
        {
            return !(sideOffset.X == 0 && sideOffset.Y == 0);
        }

        private bool TrySelectPreferredSideStep(DFPosition currentNav, DFPosition blockedNav, out DFPosition nextNav)
        {
            nextNav = currentNav;

            DFPosition[] candidates = GetSideStepCandidates(currentNav, blockedNav);
            for (int i = 0; i < candidates.Length; i++)
            {
                DFPosition candidate = candidates[i];
                if (!IsValidNavPosition(candidate) || IsCandidateHardBlocked(currentNav, candidate))
                    continue;

                int candidateDistance;
                int currentDistance;
                if (!activeField.TryGetDistance(candidate, out candidateDistance))
                    continue;
                if (!activeField.TryGetDistance(currentNav, out currentDistance))
                    currentDistance = RadiantNPCsFlowFieldGenerator.InfiniteDistance;
                if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                    continue;
                if (currentDistance < RadiantNPCsFlowFieldGenerator.InfiniteDistance && candidateDistance > currentDistance + PreferredLaneDistanceSlack)
                    continue;
                if (AreSameNavPosition(candidate, previousNavPosition) || AreSameNavPosition(candidate, olderNavPosition))
                    continue;

                nextNav = candidate;
                return true;
            }

            return false;
        }

        private float GetLocalDodgeYawDegrees(Vector3 desiredDirection)
        {
            if (desiredDirection.sqrMagnitude <= 0.0001f)
                return 0f;

            Vector3 forward = desiredDirection.normalized;
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            float leftPressure = 0f;
            float rightPressure = 0f;
            float totalPressure = 0f;

            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                RadiantNPCsMovementController other = ActiveControllers[i];
                if (other == null || other == this)
                    continue;

                Vector3 toOther = other.GetPredictedScenePosition() - transform.position;
                toOther.y = 0f;
                float distance = toOther.magnitude;
                if (distance <= 0.001f || distance > DodgeLookAheadDistance)
                    continue;

                Vector3 directionToOther = toOther / distance;
                float ahead = Vector3.Dot(forward, directionToOther);
                if (ahead <= 0.05f)
                    continue;

                float pressure = ahead * (1f - distance / DodgeLookAheadDistance);
                float side = Vector3.Dot(right, directionToOther);
                if (side >= 0f)
                    rightPressure += pressure;
                else
                    leftPressure += pressure;

                totalPressure += pressure;
            }

            if (totalPressure <= 0f)
                return 0f;

            float dodgeSide = dodgeSidePreference;
            if (dodgeSide < 0f && leftPressure > rightPressure + 0.1f)
                dodgeSide = 1f;
            else if (dodgeSide > 0f && rightPressure > leftPressure + 0.1f)
                dodgeSide = -1f;

            float strength = Mathf.Clamp01(totalPressure);
            return dodgeSide * MaxDodgeYawDegrees * strength;
        }

        private float ComputeOccupancyPenalty(DFPosition navPosition)
        {
            return ComputeCandidateCrowdPenalty(navPosition, navPosition);
        }

        private float GetRecentPositionPenalty(DFPosition candidate)
        {
            float penalty = 0f;
            if (AreSameNavPosition(candidate, previousNavPosition))
                penalty += BacktrackPenalty;
            if (AreSameNavPosition(candidate, olderNavPosition))
                penalty += LoopPenalty;
            return penalty;
        }

        private bool HasEncounterSpace()
        {
            if (activeField == null)
                return false;

            DFPosition currentNav = IsValidNavPosition(occupiedNavPosition) ? occupiedNavPosition : ResolveCurrentNavPosition();
            int freeNeighbours = 0;
            for (int i = 0; i < 4; i++)
            {
                DFPosition candidate = GetAdjacentNavPosition(currentNav, i);
                int candidateDistance;
                if (!activeField.TryGetDistance(candidate, out candidateDistance))
                    continue;
                if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                    continue;
                if (!IsCandidateHardBlocked(currentNav, candidate))
                    freeNeighbours++;
            }

            return freeNeighbours >= EncounterRequiredFreeNeighbours;
        }

        private int CountOccupiedNeighbours(DFPosition currentNav)
        {
            int occupiedCount = 0;
            for (int i = 0; i < 4; i++)
            {
                DFPosition candidate = GetAdjacentNavPosition(currentNav, i);
                if (IsCandidateHardBlocked(currentNav, candidate))
                    occupiedCount++;
            }

            return occupiedCount;
        }

        private bool HasBlockedDownhillCandidate(DFPosition currentNav)
        {
            DFPosition blockedNav;
            return TryGetBlockedDownhillCandidate(currentNav, out blockedNav);
        }

        private bool TryGetBlockedDownhillCandidate(DFPosition currentNav, out DFPosition blockedNav)
        {
            blockedNav = currentNav;
            if (activeField == null)
                return false;

            int currentDistance;
            if (!activeField.TryGetDistance(currentNav, out currentDistance))
                return false;

            float bestScore = float.MaxValue;
            bool found = false;
            for (int i = 0; i < 4; i++)
            {
                DFPosition candidate = GetAdjacentNavPosition(currentNav, i);
                int candidateDistance;
                if (!activeField.TryGetDistance(candidate, out candidateDistance))
                    continue;
                if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                    continue;
                if (candidateDistance > currentDistance + PreferredLaneDistanceSlack)
                    continue;
                if (!IsCandidateHardBlocked(currentNav, candidate))
                    continue;

                float score = GetPersonalizedStepScore(currentNav, candidate, candidateDistance);
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    blockedNav = candidate;
                }
            }

            return found;
        }

        private DFPosition[] GetSideStepCandidates(DFPosition currentNav, DFPosition blockedNav)
        {
            int dx = blockedNav.X - currentNav.X;
            int dy = blockedNav.Y - currentNav.Y;

            DFPosition left;
            DFPosition right;
            if (dx != 0)
            {
                left = new DFPosition(currentNav.X, currentNav.Y + 1);
                right = new DFPosition(currentNav.X, currentNav.Y - 1);
            }
            else
            {
                left = new DFPosition(currentNav.X - 1, currentNav.Y);
                right = new DFPosition(currentNav.X + 1, currentNav.Y);
            }

            if (dodgeSidePreference < 0f)
                return new DFPosition[] { left, right };

            return new DFPosition[] { right, left };
        }

        private Vector3 GetYieldLookDirection(DFPosition currentNav, DFPosition blockedNav)
        {
            Vector3 blockedScenePosition = GetScenePosition(blockedNav);
            blockedScenePosition.y = transform.position.y;
            Vector3 baseDirection = blockedScenePosition - transform.position;
            baseDirection.y = 0f;
            if (baseDirection.sqrMagnitude <= 0.0001f)
                return transform.forward;

            float signedAngle = dodgeSidePreference * YieldFacingAngleDegrees;
            return Quaternion.AngleAxis(signedAngle, Vector3.up) * baseDirection.normalized;
        }

        private void ClearTileClaimFlag(DFPosition navPosition)
        {
            return;
        }

        private static bool AreSameNavPosition(DFPosition left, DFPosition right)
        {
            return left.X == right.X && left.Y == right.Y;
        }

        private static DFPosition GetAdjacentNavPosition(DFPosition currentNav, int directionIndex)
        {
            switch (directionIndex)
            {
                case 0:
                    return new DFPosition(currentNav.X + 1, currentNav.Y);
                case 1:
                    return new DFPosition(currentNav.X - 1, currentNav.Y);
                case 2:
                    return new DFPosition(currentNav.X, currentNav.Y + 1);
                default:
                    return new DFPosition(currentNav.X, currentNav.Y - 1);
            }
        }

        private bool HasReachedGoal(DFPosition currentNav)
        {
            if (activeField != null && activeField.IsGoal(currentNav))
                return true;

            Vector3 goalScenePosition = locationOrigin + activeAnchorLocalPosition;
            goalScenePosition.y = transform.position.y;
            Vector3 delta = goalScenePosition - transform.position;
            delta.y = 0;
            return delta.sqrMagnitude <= GoalThreshold * GoalThreshold;
        }

        private DFPosition ResolveCurrentNavPosition()
        {
            DFPosition worldPosition = cityNavigation.SceneToWorldPosition(transform.position);
            return cityNavigation.WorldToNavGridPosition(worldPosition);
        }

        private Vector3 GetScenePosition(DFPosition navPos)
        {
            Vector3 pos = cityNavigation.WorldToScenePosition(cityNavigation.NavGridToWorldPosition(navPos));
            pos.y += 1f;
            return pos;
        }

        private Vector3 GetCandidateScenePosition(DFPosition currentNav, DFPosition nextNav)
        {
            Vector3 position = GetScenePosition(nextNav);
            if (IsValidNavPosition(currentNav) && !AreSameNavPosition(currentNav, nextNav))
                position += GetSegmentLaneOffset(currentNav, nextNav);
            return position;
        }

        private Vector3 GetPredictedScenePosition()
        {
            if (hasStepTarget && IsValidNavPosition(stepTargetNavPosition))
                return GetCandidateScenePosition(occupiedNavPosition, stepTargetNavPosition);

            return transform.position;
        }

        private Vector3 GetLocalSeparationSteering(Vector3 desiredDirection, DFPosition candidateNav)
        {
            Vector3 candidateScenePosition = GetCandidateScenePosition(occupiedNavPosition, candidateNav);
            candidateScenePosition.y = transform.position.y;
            Vector3 separation = Vector3.zero;

            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                RadiantNPCsMovementController other = ActiveControllers[i];
                if (other == null || other == this)
                    continue;

                Vector3 otherPosition = other.GetPredictedScenePosition();
                otherPosition.y = candidateScenePosition.y;
                Vector3 away = candidateScenePosition - otherPosition;
                away.y = 0f;
                float distance = away.magnitude;
                if (distance <= 0.001f || distance > SeparationRadius)
                    continue;

                float weight = 1f - distance / SeparationRadius;
                if (desiredDirection.sqrMagnitude > 0.0001f)
                {
                    Vector3 toOther = otherPosition - transform.position;
                    toOther.y = 0f;
                    if (toOther.sqrMagnitude > 0.0001f)
                        weight *= Mathf.Lerp(0.45f, 1f, Mathf.Clamp01(Vector3.Dot(desiredDirection.normalized, toOther.normalized) * 0.5f + 0.5f));
                }

                separation += away.normalized * weight;
            }

            return Vector3.ClampMagnitude(separation, MaxSeparationWeight);
        }

        private bool IsCandidateHardBlocked(DFPosition currentNav, DFPosition candidateNav)
        {
            if (!IsValidNavPosition(candidateNav))
                return true;

            Vector3 candidateScenePosition = GetCandidateScenePosition(currentNav, candidateNav);
            candidateScenePosition.y = transform.position.y;
            float hardBlockRadiusSq = HardBlockRadius * HardBlockRadius;

            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                RadiantNPCsMovementController other = ActiveControllers[i];
                if (other == null || other == this)
                    continue;

                bool sameNav = AreSameNavPosition(other.occupiedNavPosition, candidateNav) ||
                               (other.hasStepTarget && AreSameNavPosition(other.stepTargetNavPosition, candidateNav));
                if (!sameNav)
                    continue;

                Vector3 otherPosition = other.GetPredictedScenePosition();
                otherPosition.y = candidateScenePosition.y;
                if ((otherPosition - candidateScenePosition).sqrMagnitude <= hardBlockRadiusSq)
                    return true;
            }

            return false;
        }

        private float ComputeCandidateCrowdPenalty(DFPosition currentNav, DFPosition candidateNav)
        {
            if (!IsValidNavPosition(candidateNav))
                return 0f;

            Vector3 candidateScenePosition = GetCandidateScenePosition(currentNav, candidateNav);
            candidateScenePosition.y = transform.position.y;
            float penalty = 0f;
            float crowdRadiusSq = SoftCrowdRadius * SoftCrowdRadius;

            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                RadiantNPCsMovementController other = ActiveControllers[i];
                if (other == null || other == this)
                    continue;

                Vector3 otherPosition = other.GetPredictedScenePosition();
                otherPosition.y = candidateScenePosition.y;
                Vector3 delta = otherPosition - candidateScenePosition;
                delta.y = 0f;
                float sqrDistance = delta.sqrMagnitude;
                if (sqrDistance <= 0.0001f || sqrDistance > crowdRadiusSq)
                    continue;

                float distance = Mathf.Sqrt(sqrDistance);
                float t = 1f - distance / SoftCrowdRadius;
                float localPenalty = t * t * CandidateCrowdPenalty;
                if (AreSameNavPosition(other.occupiedNavPosition, candidateNav) ||
                    (other.hasStepTarget && AreSameNavPosition(other.stepTargetNavPosition, candidateNav)))
                {
                    localPenalty += t * CandidateQueuePenalty;
                }

                penalty += localPenalty;
            }

            return penalty;
        }

        private void IncrementMoveCount()
        {
            if (motor == null || MotorMoveCountField == null)
                return;

            int moveCount = (int)MotorMoveCountField.GetValue(motor);
            MotorMoveCountField.SetValue(motor, moveCount + 1);
        }

        private void SetIdle(bool isIdle)
        {
            if (npc != null && npc.Asset != null && npc.Asset.IsIdle != isIdle)
                npc.Asset.IsIdle = isIdle;
        }

        private void LogMovement(string format, params object[] args)
        {
            if (loggedConfigurations >= 8)
                return;

            loggedConfigurations++;
            string message = string.Format(format, args);
            Debug.Log(message);
            try
            {
                File.AppendAllText(Path.Combine(Application.dataPath, "Game", "Mods", "RadiantNPCs", "RadiantNPCs.log.txt"),
                    string.Format("[{0}] {1}{2}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), message, Environment.NewLine));
            }
            catch
            {
            }
        }
    }
}
