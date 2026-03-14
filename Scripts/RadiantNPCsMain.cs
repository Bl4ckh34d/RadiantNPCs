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
    public partial class RadiantNPCsMain : MonoBehaviour, IHasModSaveData
    {
        private const string SaveVersion = "v1";
        private const int MaxFaceVariant = 24;
        private const float ActiveHouseholdRadius = RMBLayout.RMBSide * 2.5f;
        private const int SpawnSampleCount = 6;
        private const float SpawnDistancePenalty = 0.18f;
        private const float SpawnOtherNpcAvoidanceRadius = 4.5f;
        private const float TargetLoadPenalty = 1.75f;
        private const float TargetFailurePenalty = 6f;
        private const int TargetBlacklistThreshold = 3;
        private const int GuardPortraitRecordId = 403;
        private const string LogFileName = "RadiantNPCs.log.txt";
        private const string FlowFieldComputeShaderAssetName = "RadiantNPCsFlowField.compute";
        private const uint SingleBedModelIdA = 41000;
        private const uint SingleBedModelIdB = 41001;
        private const uint DoubleBedModelId = 41002;

        private static readonly int[] MaleRedguardFaceRecordIndex = new int[] { 336, 312, 336, 312 };
        private static readonly int[] FemaleRedguardFaceRecordIndex = new int[] { 144, 144, 120, 96 };
        private static readonly int[] MaleNordFaceRecordIndex = new int[] { 240, 264, 168, 192 };
        private static readonly int[] FemaleNordFaceRecordIndex = new int[] { 72, 0, 48, 0 };
        private static readonly int[] MaleBretonFaceRecordIndex = new int[] { 192, 216, 288, 240 };
        private static readonly int[] FemaleBretonFaceRecordIndex = new int[] { 72, 72, 24, 72 };

        private static readonly FieldInfo PopulationManagerMaxPopulationField =
            typeof(PopulationManager).GetField("maxPopulation", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Mod mod;
        private static RadiantNPCsMain instance;
        private static PopulationManager.MobileNPCGenerationHandler previousMobileNpcGenerator;

        private RadiantNPCsSaveDataV1 saveData = new RadiantNPCsSaveDataV1();
        private Dictionary<int, ResidentAssignment> activeAssignments = new Dictionary<int, ResidentAssignment>();
        private Dictionary<long, MobilePersonNPC> activeResidentNpcs = new Dictionary<long, MobilePersonNPC>();
        private Dictionary<long, GameObject> activePromotedGuards = new Dictionary<long, GameObject>();
        private Dictionary<int, int> spawnCursorByMapId = new Dictionary<int, int>();
        private Dictionary<int, int> assignmentLogsByMapId = new Dictionary<int, int>();
        private Dictionary<long, float> residentLastAssignedAt = new Dictionary<long, float>();
        private Dictionary<long, int> targetFailureCounts = new Dictionary<long, int>();
        private Dictionary<long, int> shelteredBuildingByResidentKey = new Dictionary<long, int>();
        private readonly List<GameObject> activeInteriorNpcObjects = new List<GameObject>();
        private RadiantNPCsSharedNavigationContext activeNavigationContext;
        private ComputeShader flowFieldComputeShader;
        private bool flowFieldShaderLoadAttempted = false;
        private int lastPreparedMapId = -1;

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;

            GameObject go = new GameObject(mod.Title);
            DontDestroyOnLoad(go);
            instance = go.AddComponent<RadiantNPCsMain>();

            mod.SaveDataInterface = instance;
            mod.IsReady = true;
            LogInfo("RadiantNPCs: initialized mod '{0}' v{1}.", mod.Title, mod.ModInfo.ModVersion);
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);

            HookEvents();
            mod.SaveDataInterface = this;
        }

        private void OnDestroy()
        {
            if (instance != this)
                return;

            DisposeNavigationContext();
            UnhookEvents();
            instance = null;
        }

        private void Update()
        {
            PrepareCurrentLocationIfNeeded();
        }

        public Type SaveDataType
        {
            get { return typeof(RadiantNPCsSaveDataV1); }
        }

        public object NewSaveData()
        {
            return new RadiantNPCsSaveDataV1();
        }

        public object GetSaveData()
        {
            return saveData;
        }

        public void RestoreSaveData(object saveDataIn)
        {
            if (saveDataIn is RadiantNPCsSaveDataV1)
                saveData = (RadiantNPCsSaveDataV1)saveDataIn;
            else
                saveData = new RadiantNPCsSaveDataV1();

            ClearRuntimeState();
            lastPreparedMapId = -1;
            LogInfo("RadiantNPCs: restored save data for {0} locations.", saveData.locations.Count);
        }

        private void HookEvents()
        {
            PlayerGPS.OnEnterLocationRect += PlayerGPS_OnEnterLocationRect;
            PlayerEnterExit.OnTransitionInterior += PlayerEnterExit_OnTransitionInterior;
            PlayerEnterExit.OnTransitionExterior += PlayerEnterExit_OnTransitionExterior;
            SaveLoadManager.OnStartLoad += SaveLoadManager_OnStartLoad;
            SaveLoadManager.OnLoad += SaveLoadManager_OnLoad;
            PopulationManager.OnMobileNPCCreate += PopulationManager_OnMobileNPCCreate;
            PopulationManager.OnMobileNPCEnable += PopulationManager_OnMobileNPCEnable;
            PopulationManager.OnMobileNPCDisable += PopulationManager_OnMobileNPCDisable;
            WorldTime.OnNewHour += WorldTime_OnNewHour;

            previousMobileNpcGenerator = PopulationManager.MobileNPCGenerator;
            PopulationManager.MobileNPCGenerator = HandleMobileNpcGeneration;
        }

        private void UnhookEvents()
        {
            PlayerGPS.OnEnterLocationRect -= PlayerGPS_OnEnterLocationRect;
            PlayerEnterExit.OnTransitionInterior -= PlayerEnterExit_OnTransitionInterior;
            PlayerEnterExit.OnTransitionExterior -= PlayerEnterExit_OnTransitionExterior;
            SaveLoadManager.OnStartLoad -= SaveLoadManager_OnStartLoad;
            SaveLoadManager.OnLoad -= SaveLoadManager_OnLoad;
            PopulationManager.OnMobileNPCCreate -= PopulationManager_OnMobileNPCCreate;
            PopulationManager.OnMobileNPCEnable -= PopulationManager_OnMobileNPCEnable;
            PopulationManager.OnMobileNPCDisable -= PopulationManager_OnMobileNPCDisable;
            WorldTime.OnNewHour -= WorldTime_OnNewHour;

            if (PopulationManager.MobileNPCGenerator == HandleMobileNpcGeneration)
                PopulationManager.MobileNPCGenerator = previousMobileNpcGenerator;
        }

        private void PlayerGPS_OnEnterLocationRect(DFLocation location)
        {
            PrepareCurrentLocation(force: true);
        }

        private void PlayerEnterExit_OnTransitionExterior(PlayerEnterExit.TransitionEventArgs args)
        {
            DestroyInteriorResidents();
            PrepareCurrentLocation(force: true);
        }

        private void PlayerEnterExit_OnTransitionInterior(PlayerEnterExit.TransitionEventArgs args)
        {
            DestroyInteriorResidents();

            if (args == null || args.DaggerfallInterior == null)
                return;

            LocationResidentsDataV1 locationData = GetOrCreateCurrentLocationData();
            if (locationData == null)
                return;

            SpawnInteriorResidents(locationData, args.StaticDoor.buildingKey, args.DaggerfallInterior);
        }

        private void RefreshCurrentInteriorResidents()
        {
            if (GameManager.Instance == null || GameManager.Instance.PlayerEnterExit == null)
                return;

            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (!playerEnterExit.IsPlayerInsideBuilding || playerEnterExit.Interior == null)
                return;

            LocationResidentsDataV1 locationData = GetOrCreateCurrentLocationData();
            if (locationData == null)
                return;

            int buildingKey = playerEnterExit.BuildingDiscoveryData.buildingKey;
            if (buildingKey <= 0 && playerEnterExit.Interior.EntryDoor.buildingKey > 0)
                buildingKey = playerEnterExit.Interior.EntryDoor.buildingKey;
            if (buildingKey <= 0)
                return;

            DestroyInteriorResidents();
            SpawnInteriorResidents(locationData, buildingKey, playerEnterExit.Interior);
        }

        private void SaveLoadManager_OnStartLoad(SaveData_v1 saveDataIn)
        {
            ClearRuntimeState();
            lastPreparedMapId = -1;
        }

        private void SaveLoadManager_OnLoad(SaveData_v1 saveDataIn)
        {
            PrepareCurrentLocation(force: true);
        }

        private void PopulationManager_OnMobileNPCDisable(PopulationManager.PoolItem poolItem)
        {
            int instanceId = poolItem.npc.GetInstanceID();
            if (activeAssignments.ContainsKey(instanceId))
            {
                ResidentAssignment assignment = activeAssignments[instanceId];
                RecordResidentExteriorPosition(assignment.mapId, assignment.residentId, poolItem.npc.transform.position);
                activeResidentNpcs.Remove(GetResidentUsageKey(assignment.mapId, assignment.residentId));
                activeAssignments.Remove(instanceId);
            }
        }

        private void PopulationManager_OnMobileNPCCreate(PopulationManager.PoolItem poolItem)
        {
            RadiantNPCsNpcDeathRelay relay = poolItem.npc.GetComponent<RadiantNPCsNpcDeathRelay>();
            if (relay == null)
                relay = poolItem.npc.gameObject.AddComponent<RadiantNPCsNpcDeathRelay>();

            relay.Main = this;

            RadiantNPCsMovementController movement = poolItem.npc.GetComponent<RadiantNPCsMovementController>();
            if (movement == null)
                movement = poolItem.npc.gameObject.AddComponent<RadiantNPCsMovementController>();

            movement.Main = this;
        }

        private void PopulationManager_OnMobileNPCEnable(PopulationManager.PoolItem poolItem)
        {
            ResidentAssignment assignment;
            if (!activeAssignments.TryGetValue(poolItem.npc.GetInstanceID(), out assignment))
                return;

            LocationResidentsDataV1 locationData = FindLocationData(assignment.mapId);
            ResidentDataV1 resident = FindResidentById(locationData, assignment.residentId);
            if (locationData == null || resident == null || resident.isDead)
                return;

            PositionResidentForCurrentState(locationData, poolItem.npc, resident);
        }

        internal void NotifyResidentDeath(MobilePersonNPC npc)
        {
            if (npc == null)
                return;

            ResidentAssignment assignment;
            if (!activeAssignments.TryGetValue(npc.GetInstanceID(), out assignment))
                return;

            LocationResidentsDataV1 locationData = FindLocationData(assignment.mapId);
            ResidentDataV1 resident = FindResidentById(locationData, assignment.residentId);
            if (resident == null || resident.isDead)
                return;

            resident.isDead = true;
            resident.currentState = (int)ResidentState.Dead;
            activeAssignments.Remove(npc.GetInstanceID());
            activeResidentNpcs.Remove(GetResidentUsageKey(assignment.mapId, assignment.residentId));
            LogInfo("RadiantNPCs: resident '{0}' (ResidentID={1}, MapID={2}) has died and will not respawn.", resident.fullName, resident.residentId, assignment.mapId);
            PrepareCurrentLocation(force: true);
        }

        internal void NotifyResidentDeath(int mapId, int residentId)
        {
            LocationResidentsDataV1 locationData = FindLocationData(mapId);
            int residentIndex = FindResidentIndexById(locationData, residentId);
            if (residentIndex < 0)
                return;

            ResidentDataV1 resident = locationData.residents[residentIndex];
            if (resident == null || resident.isDead)
                return;

            resident.isDead = true;
            resident.currentState = (int)ResidentState.Dead;
            locationData.residents[residentIndex] = resident;
            activePromotedGuards.Remove(GetResidentUsageKey(mapId, residentId));
            LogInfo("RadiantNPCs: resident '{0}' (ResidentID={1}, MapID={2}) has died and will not respawn.", resident.fullName, resident.residentId, mapId);
            PrepareCurrentLocation(force: true);
        }

        private void WorldTime_OnNewHour()
        {
            PrepareCurrentLocation(force: true);
            RefreshCurrentInteriorResidents();
        }

        private void PrepareCurrentLocationIfNeeded()
        {
            PlayerGPS playerGPS = GetPlayerGPS();
            if (playerGPS == null || !playerGPS.HasCurrentLocation)
                return;

            if (playerGPS.CurrentMapID != lastPreparedMapId)
                PrepareCurrentLocation(force: false);
        }

        private void PrepareCurrentLocation(bool force)
        {
            PlayerGPS playerGPS = GetPlayerGPS();
            if (playerGPS == null || !playerGPS.HasCurrentLocation)
                return;

            int currentMapId = playerGPS.CurrentMapID;
            if (!force && currentMapId == lastPreparedMapId)
                return;

            LocationResidentsDataV1 locationData = GetOrCreateCurrentLocationData();
            if (locationData == null)
                return;

            EnsureNavigationContext(locationData);
            RefreshResidentStates(locationData);
            lastPreparedMapId = currentMapId;
            ApplyPopulationLimit(GetDesiredActiveResidentCount(locationData));
            RefreshActiveResidentControllers(locationData);
        }

        private LocationResidentsDataV1 GetOrCreateCurrentLocationData()
        {
            PlayerGPS playerGPS = GetPlayerGPS();
            if (playerGPS == null || !playerGPS.HasCurrentLocation)
                return null;

            int mapId = playerGPS.CurrentMapID;
            LocationResidentsDataV1 existing = FindLocationData(mapId);
            if (existing != null)
                return existing;

            StreamingWorld streamingWorld = GameManager.Instance.StreamingWorld;
            if (streamingWorld == null)
                return null;

            BuildingDirectory buildingDirectory = streamingWorld.GetCurrentBuildingDirectory();
            if (buildingDirectory == null)
                return null;

            LocationResidentsDataV1 created = GenerateLocationData(buildingDirectory);
            if (created == null)
                return null;

            saveData.locations.Add(created);
            LogInfo(
                "RadiantNPCs: created resident pool for {0}/{1} (MapID={2}, households={3}, residents={4}).",
                created.regionName,
                created.locationName,
                created.mapId,
                created.households.Count,
                created.residents.Count);
            return created;
        }

        private LocationResidentsDataV1 GenerateLocationData(BuildingDirectory buildingDirectory)
        {
            PlayerGPS playerGPS = GetPlayerGPS();
            if (playerGPS == null || !playerGPS.HasCurrentLocation)
                return null;

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            DFLocation location = buildingDirectory.LocationData;
            DFBlock[] locationBlocks = RMBLayout.GetLocationBuildingData(location);
            int locationWidth = location.Exterior.ExteriorData.Width;
            int locationHeight = location.Exterior.ExteriorData.Height;
            Dictionary<int, Vector3> doorAnchors = BuildDoorAnchorMap(buildingDirectory);

            PopulateLocationTargets(locationData: out LocationResidentsDataV1 locationData, buildingDirectory, playerGPS, doorAnchors);
            List<BuildingSummary> residences = GetResidentialBuildings(buildingDirectory);
            residences.Sort(CompareBuildingsByKey);

            NameHelper.BankTypes nameBank = playerGPS.GetNameBankOfCurrentRegion();
            Races mobileRace = ResolveSupportedMobileRace(playerGPS);
            int householdId = 1;
            int residentId = 1;
            int house1Count = 0;
            int house2Count = 0;
            int house3Count = 0;
            int house4Count = 0;
            int totalBedCapacity = 0;
            int housesUsingFallbackCapacity = 0;
            int housesWithDetectedBeds = 0;
            List<int> fallbackSamples = new List<int>();

            for (int i = 0; i < residences.Count; i++)
            {
                BuildingSummary residence = residences[i];
                int layoutX = GetLayoutX(residence.buildingKey);
                int layoutY = GetLayoutY(residence.buildingKey);
                switch (residence.BuildingType)
                {
                    case DFLocation.BuildingTypes.House1:
                        house1Count++;
                        break;
                    case DFLocation.BuildingTypes.House2:
                        house2Count++;
                        break;
                    case DFLocation.BuildingTypes.House3:
                        house3Count++;
                        break;
                    case DFLocation.BuildingTypes.House4:
                        house4Count++;
                        break;
                }

                bool usedFallbackCapacity;
                int bedCapacity = GetBedCapacity(locationBlocks, locationWidth, locationHeight, residence, out usedFallbackCapacity);
                if (usedFallbackCapacity)
                {
                    housesUsingFallbackCapacity++;
                    if (fallbackSamples.Count < 8)
                        fallbackSamples.Add(residence.buildingKey);
                }
                else
                {
                    housesWithDetectedBeds++;
                }

                int occupantCount = GetOccupantCount(residence, bedCapacity);
                if (occupantCount <= 0)
                    continue;

                HouseholdDataV1 household = new HouseholdDataV1();
                household.householdId = householdId++;
                household.buildingKey = residence.buildingKey;
                household.buildingType = (int)residence.BuildingType;
                household.bedCapacity = bedCapacity;
                household.homeLocalPositionX = GetBuildingLocalPosition(layoutX, layoutY, residence.Position).x;
                household.homeLocalPositionZ = GetBuildingLocalPosition(layoutX, layoutY, residence.Position).z;
                household.surname = GenerateSurname(nameBank, residence.buildingKey);
                locationData.households.Add(household);
                totalBedCapacity += bedCapacity;
                int residentListStartIndex = locationData.residents.Count;

                for (int occupantIndex = 0; occupantIndex < occupantCount; occupantIndex++)
                {
                    ResidentRole role = GetResidentRole(residence.buildingKey, occupantIndex);
                    Genders gender = role == ResidentRole.Guard ? Genders.Male : GetHouseholdGender(occupantCount, occupantIndex, residence.buildingKey);
                    string firstName = GenerateFirstName(nameBank, gender, residence.buildingKey + occupantIndex * 19);
                    string fullName = ComposeFullName(firstName, household.surname);
                    int outfitVariant = GetOutfitVariant(residence.buildingKey, occupantIndex);
                    int faceVariant = GetFaceVariant(residence.buildingKey, occupantIndex);
                    int faceRecordId = role == ResidentRole.Guard ? GuardPortraitRecordId : ComputeFaceRecordId(mobileRace, gender, outfitVariant, faceVariant);

                    ResidentDataV1 resident = new ResidentDataV1();
                    resident.residentId = residentId++;
                    resident.householdId = household.householdId;
                    resident.householdMemberIndex = occupantIndex;
                    resident.homeBuildingKey = residence.buildingKey;
                    resident.firstName = firstName;
                    resident.surname = household.surname;
                    resident.fullName = fullName;
                    resident.gender = (int)gender;
                    resident.race = (int)mobileRace;
                    resident.outfitVariant = outfitVariant;
                    resident.faceVariant = faceVariant;
                    resident.faceRecordId = faceRecordId;
                    resident.disposition = GetDisposition(residence.buildingKey, occupantIndex);
                    resident.canVisitOtherHouses = true;
                    resident.canVisitShops = true;
                    resident.role = (int)role;
                    resident.prefersNightlife = GetPrefersNightlife(residence.buildingKey, occupantIndex);
                    resident.prefersShopping = GetPrefersShopping(residence.buildingKey, occupantIndex);
                    resident.prefersSocialVisits = GetPrefersSocialVisits(residence.buildingKey, occupantIndex);
                    resident.currentState = (int)ResidentState.AtHome;
                    resident.currentTargetBuildingKey = residence.buildingKey;
                    resident.hasKnownExteriorPosition = true;
                    resident.exteriorLocalPositionX = household.homeLocalPositionX;
                    resident.exteriorLocalPositionZ = household.homeLocalPositionZ;
                    resident.isDead = false;
                    resident.lastScheduleSignature = -1;
                    locationData.residents.Add(resident);
                }

                if (occupantCount >= 2)
                {
                    ResidentDataV1 firstResident = locationData.residents[residentListStartIndex];
                    ResidentDataV1 secondResident = locationData.residents[residentListStartIndex + 1];
                    firstResident.partnerResidentId = secondResident.residentId;
                    secondResident.partnerResidentId = firstResident.residentId;
                    firstResident.sharedBedGroupId = household.householdId;
                    secondResident.sharedBedGroupId = household.householdId;
                }
            }

            float averageResidentsPerHousehold = locationData.households.Count > 0
                ? (float)locationData.residents.Count / locationData.households.Count
                : 0f;

            LogInfo(
                "RadiantNPCs: household breakdown for {0}/{1}: House1={2}, House2={3}, House3={4}, House4={5}, totalBedCapacity={6}, avgResidentsPerHousehold={7:F2}.",
                locationData.regionName,
                locationData.locationName,
                house1Count,
                house2Count,
                house3Count,
                house4Count,
                totalBedCapacity,
                averageResidentsPerHousehold);

            LogInfo(
                "RadiantNPCs: bed detection for {0}/{1}: housesWithDetectedBeds={2}, housesUsingFallbackCapacity={3}, fallbackSamples=[{4}].",
                locationData.regionName,
                locationData.locationName,
                housesWithDetectedBeds,
                housesUsingFallbackCapacity,
                string.Join(", ", fallbackSamples.ToArray()));
            stopwatch.Stop();
            LogInfo(
                "RadiantNPCs: resident generation for {0}/{1} completed in {2} ms.",
                locationData.regionName,
                locationData.locationName,
                stopwatch.ElapsedMilliseconds);

            return locationData;
        }

        private void PopulateLocationTargets(out LocationResidentsDataV1 locationData, BuildingDirectory buildingDirectory, PlayerGPS playerGPS, Dictionary<int, Vector3> doorAnchors)
        {
            locationData = new LocationResidentsDataV1();
            locationData.mapId = playerGPS.CurrentMapID;
            locationData.locationId = (int)playerGPS.CurrentLocation.Exterior.ExteriorData.LocationId;
            locationData.locationIndex = playerGPS.CurrentLocation.LocationIndex;
            locationData.locationName = playerGPS.CurrentLocation.Name;
            locationData.regionName = playerGPS.CurrentRegionName;

            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House1, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House2, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House3, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House4, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Tavern, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Alchemist, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Armorer, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Bookseller, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.ClothingStore, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.FurnitureStore, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.GemStore, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.GeneralStore, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.PawnShop, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.WeaponSmith, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.GuildHall, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Temple, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Bank, doorAnchors);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Palace, doorAnchors);
        }

        private void AddTargetsForType(LocationResidentsDataV1 locationData, BuildingDirectory buildingDirectory, DFLocation.BuildingTypes buildingType, Dictionary<int, Vector3> doorAnchors)
        {
            List<BuildingSummary> buildings = buildingDirectory.GetBuildingsOfType(buildingType);
            for (int i = 0; i < buildings.Count; i++)
            {
                BuildingSummary building = buildings[i];
                int layoutX = GetLayoutX(building.buildingKey);
                int layoutY = GetLayoutY(building.buildingKey);
                Vector3 localPosition = GetBuildingLocalPosition(layoutX, layoutY, building.Position);
                Vector3 doorAnchor = Vector3.zero;
                bool hasDoorAnchor = doorAnchors != null && doorAnchors.TryGetValue(building.buildingKey, out doorAnchor);

                BuildingTargetDataV1 target = new BuildingTargetDataV1();
                target.buildingKey = building.buildingKey;
                target.buildingType = (int)building.BuildingType;
                target.localPositionX = hasDoorAnchor ? doorAnchor.x : localPosition.x;
                target.localPositionZ = hasDoorAnchor ? doorAnchor.z : localPosition.z;
                target.isResidence = RMBLayout.IsResidence(building.BuildingType);
                target.isShop = RMBLayout.IsShop(building.BuildingType);
                target.isTavern = RMBLayout.IsTavern(building.BuildingType);
                target.isGuildHall = building.BuildingType == DFLocation.BuildingTypes.GuildHall;
                target.isTemple = building.BuildingType == DFLocation.BuildingTypes.Temple;
                target.isBank = building.BuildingType == DFLocation.BuildingTypes.Bank;
                target.isPalace = building.BuildingType == DFLocation.BuildingTypes.Palace;
                target.isPublicVenue = target.isShop || target.isTavern || target.isGuildHall || target.isTemple || target.isBank || target.isPalace;
                target.hasDoorAnchor = hasDoorAnchor;
                locationData.targets.Add(target);
            }
        }

        private Dictionary<int, Vector3> BuildDoorAnchorMap(BuildingDirectory buildingDirectory)
        {
            Dictionary<int, Vector3> anchors = new Dictionary<int, Vector3>();
            if (buildingDirectory == null)
                return anchors;

            DaggerfallStaticDoors[] doorCollections = buildingDirectory.GetComponentsInChildren<DaggerfallStaticDoors>();
            for (int c = 0; c < doorCollections.Length; c++)
            {
                DaggerfallStaticDoors collection = doorCollections[c];
                if (collection == null || collection.Doors == null)
                    continue;

                for (int i = 0; i < collection.Doors.Length; i++)
                {
                    StaticDoor door = collection.Doors[i];
                    if (door.doorType != DoorTypes.Building)
                        continue;

                    Vector3 localDoorPosition = collection.GetDoorPosition(i) - buildingDirectory.transform.position;
                    if (!anchors.ContainsKey(door.buildingKey))
                    {
                        anchors.Add(door.buildingKey, localDoorPosition);
                    }
                    else
                    {
                        Vector3 existing = anchors[door.buildingKey];
                        if (localDoorPosition.y < existing.y)
                            anchors[door.buildingKey] = localDoorPosition;
                    }
                }
            }

            return anchors;
        }

        private void ApplyPopulationLimit(int residentCount)
        {
            DaggerfallLocation currentLocationObject = GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;
            if (currentLocationObject == null || PopulationManagerMaxPopulationField == null)
                return;

            PopulationManager populationManager = currentLocationObject.GetComponent<PopulationManager>();
            if (populationManager == null)
                return;

            PopulationManagerMaxPopulationField.SetValue(populationManager, residentCount);
            LogInfo(
                "RadiantNPCs: set max exterior resident population to {0} for MapID={1}.",
                residentCount,
                GameManager.Instance.PlayerGPS.CurrentMapID);
        }

        private void HandleMobileNpcGeneration(PopulationManager.PoolItem poolItem)
        {
            LocationResidentsDataV1 locationData = GetOrCreateCurrentLocationData();
            if (locationData == null || locationData.residents.Count == 0)
            {
                FallbackToVanilla(poolItem);
                return;
            }

            ResidentDataV1 resident = GetNextAvailableResident(locationData);
            if (resident == null)
            {
                poolItem.npc.Motor.gameObject.SetActive(false);
                return;
            }

            ApplyResidentToMobileNpc(poolItem.npc, resident);

            ResidentAssignment assignment = new ResidentAssignment();
            assignment.mapId = locationData.mapId;
            assignment.residentId = resident.residentId;
            activeAssignments[poolItem.npc.GetInstanceID()] = assignment;
            activeResidentNpcs[GetResidentUsageKey(locationData.mapId, resident.residentId)] = poolItem.npc;
            residentLastAssignedAt[GetResidentUsageKey(locationData.mapId, resident.residentId)] = Time.realtimeSinceStartup;
            LogResidentAssignment(locationData, resident);
        }

        private void RefreshActiveResidentControllers(LocationResidentsDataV1 locationData)
        {
            if (locationData == null)
                return;

            foreach (KeyValuePair<long, MobilePersonNPC> pair in activeResidentNpcs)
            {
                MobilePersonNPC npc = pair.Value;
                if (npc == null)
                    continue;

                ResidentAssignment assignment;
                if (!activeAssignments.TryGetValue(npc.GetInstanceID(), out assignment))
                    continue;
                if (assignment.mapId != locationData.mapId)
                    continue;

                ResidentDataV1 resident = FindResidentById(locationData, assignment.residentId);
                if (resident == null || resident.isDead)
                    continue;

                RadiantNPCsMovementController movement = npc.GetComponent<RadiantNPCsMovementController>();
                if (movement == null)
                    continue;

                movement.ConfigureDirectedMovement(locationData, resident, GetLocationOrigin(), GetResidentAnchorLocalPosition(locationData, resident), (ResidentState)resident.currentState);
            }
        }

        private void FallbackToVanilla(PopulationManager.PoolItem poolItem)
        {
            if (previousMobileNpcGenerator != null)
            {
                previousMobileNpcGenerator(poolItem);
            }
            else
            {
                poolItem.npc.RandomiseNPC(ResolveSupportedMobileRace(GetPlayerGPS()));
            }
        }

        private void ApplyResidentToMobileNpc(MobilePersonNPC npc, ResidentDataV1 resident)
        {
            Genders gender = (Genders)resident.gender;
            Races race = (Races)resident.race;
            bool isGuard = (ResidentRole)resident.role == ResidentRole.Guard &&
                           (ResidentState)resident.currentState == ResidentState.Patrol;

            // Pooled civilians can inherit guard state from a previous vanilla assignment.
            // Clear this before rebuilding appearance so guard-only texture arrays are not used.
            npc.IsGuard = isGuard;
            npc.raceToBeSet = race;
            npc.genderToBeSet = gender;
            npc.outfitVariantToBeSet = isGuard ? 0 : resident.outfitVariant;
            npc.ApplyPersonSettingsViaInspector();

            npc.NameNPC = resident.fullName;
            npc.PersonFaceRecordId = resident.faceRecordId;
            npc.PickpocketByPlayerAttempted = false;

            if (npc.Asset != null)
                npc.Asset.SetPerson(race, gender, isGuard ? 0 : resident.outfitVariant, isGuard, resident.faceVariant, resident.faceRecordId);
        }

        private void PositionResidentForCurrentState(LocationResidentsDataV1 locationData, MobilePersonNPC npc, ResidentDataV1 resident)
        {
            if (GameManager.Instance == null || GameManager.Instance.StreamingWorld == null)
                return;

            DaggerfallLocation currentLocationObject = GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;
            if (currentLocationObject == null)
                return;

            ResidentState state = (ResidentState)resident.currentState;
            RadiantNPCsMovementController movement = npc.GetComponent<RadiantNPCsMovementController>();
            if (movement == null)
                return;

            Vector3 spawnLocalPosition = GetResidentExteriorLocalPosition(locationData, resident);
            Vector3 partnerPosition;
            if (TryGetActivePartnerPosition(locationData.mapId, resident, out partnerPosition))
            {
                spawnLocalPosition = partnerPosition - currentLocationObject.transform.position + GetPartnerOffset(resident);
            }

            Vector3 targetLocalPosition = GetResidentAnchorLocalPosition(locationData, resident);
            Vector3 localPosition = ResolveSpawnLocalPosition(locationData, npc, resident, state, spawnLocalPosition);
            npc.Motor.transform.position = currentLocationObject.transform.position + localPosition;
            GameObjectHelper.AlignBillboardToGround(npc.Motor.gameObject, new Vector2(0, 2f));

            movement.ConfigureDirectedMovement(locationData, resident, currentLocationObject.transform.position, targetLocalPosition, state);
        }

        private Vector3 ResolveSpawnLocalPosition(LocationResidentsDataV1 locationData, MobilePersonNPC npc, ResidentDataV1 resident, ResidentState state, Vector3 spawnAnchorLocalPosition)
        {
            Vector3 bestLocalPosition = spawnAnchorLocalPosition + GetSpawnOffset(resident, state, 0);
            float bestScore = ScoreSpawnCandidate(locationData, npc, spawnAnchorLocalPosition, bestLocalPosition);

            for (int i = 1; i < SpawnSampleCount; i++)
            {
                Vector3 candidateLocalPosition = spawnAnchorLocalPosition + GetSpawnOffset(resident, state, i);
                float candidateScore = ScoreSpawnCandidate(locationData, npc, spawnAnchorLocalPosition, candidateLocalPosition);
                if (candidateScore > bestScore)
                {
                    bestScore = candidateScore;
                    bestLocalPosition = candidateLocalPosition;
                }
            }

            return bestLocalPosition;
        }

        private float ScoreSpawnCandidate(LocationResidentsDataV1 locationData, MobilePersonNPC npc, Vector3 spawnAnchorLocalPosition, Vector3 candidateLocalPosition)
        {
            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (currentLocationObject == null)
                return 0f;

            Vector3 candidateScenePosition = currentLocationObject.transform.position + candidateLocalPosition;
            float bestDistanceSq = SpawnOtherNpcAvoidanceRadius * SpawnOtherNpcAvoidanceRadius;
            bool foundOther = false;

            foreach (KeyValuePair<long, MobilePersonNPC> pair in activeResidentNpcs)
            {
                if ((int)(pair.Key >> 32) != locationData.mapId)
                    continue;

                MobilePersonNPC other = pair.Value;
                if (other == null || other == npc)
                    continue;

                Vector3 delta = other.transform.position - candidateScenePosition;
                delta.y = 0f;
                float sqrDistance = delta.sqrMagnitude;
                if (!foundOther || sqrDistance < bestDistanceSq)
                {
                    bestDistanceSq = sqrDistance;
                    foundOther = true;
                }
            }

            if (!foundOther)
                bestDistanceSq = SpawnOtherNpcAvoidanceRadius * SpawnOtherNpcAvoidanceRadius;

            float anchorDistancePenalty = (candidateLocalPosition - spawnAnchorLocalPosition).sqrMagnitude * SpawnDistancePenalty;
            return bestDistanceSq - anchorDistancePenalty;
        }

        private Vector3 GetSpawnOffset(ResidentDataV1 resident, ResidentState state, int sampleIndex)
        {
            int radius = 4;
            switch (state)
            {
                case ResidentState.Shopping:
                    radius = 10;
                    break;
                case ResidentState.SocialVisit:
                    radius = 12;
                    break;
                case ResidentState.Tavern:
                    radius = 14;
                    break;
                case ResidentState.ExteriorWander:
                default:
                    radius = 6;
                    break;
            }

            return WithDFSeed(resident.residentId * 83 + (int)state * 17 + sampleIndex * 101, delegate ()
            {
                float x = DFRandom.random_range_inclusive(-radius, radius) * MeshReader.GlobalScale * 8f;
                float z = DFRandom.random_range_inclusive(-radius, radius) * MeshReader.GlobalScale * 8f;
                return new Vector3(x, 0, z);
            });
        }

        private bool TryGetActivePartnerPosition(int mapId, ResidentDataV1 resident, out Vector3 positionOut)
        {
            positionOut = Vector3.zero;
            if (resident.partnerResidentId <= 0)
                return false;

            MobilePersonNPC partnerNpc;
            if (!activeResidentNpcs.TryGetValue(GetResidentUsageKey(mapId, resident.partnerResidentId), out partnerNpc) || partnerNpc == null)
                return false;

            positionOut = partnerNpc.transform.position;
            return true;
        }

        private Vector3 GetPartnerOffset(ResidentDataV1 resident)
        {
            return WithDFSeed(resident.residentId * 89, delegate ()
            {
                float side = DFRandom.random_range(100) < 50 ? -1f : 1f;
                return new Vector3(side * 0.75f, 0, 0);
            });
        }

        private ResidentDataV1 GetNextAvailableResident(LocationResidentsDataV1 locationData)
        {
            if (locationData.residents == null || locationData.residents.Count == 0)
                return null;

            Vector3 playerLocalPosition = GetPlayerLocalPosition();
            List<int> nearbyEligible = new List<int>();
            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.isDead)
                    continue;
                if (!IsExteriorState((ResidentState)resident.currentState))
                    continue;
                if (IsResidentSheltered(locationData.mapId, resident.residentId))
                    continue;
                if (IsResidentAssigned(locationData.mapId, resident.residentId))
                    continue;

                Vector3 anchor = GetResidentExteriorLocalPosition(locationData, resident);
                Vector2 home = new Vector2(anchor.x, anchor.z);
                Vector2 player = new Vector2(playerLocalPosition.x, playerLocalPosition.z);
                float distance = Vector2.Distance(home, player);
                if (distance <= ActiveHouseholdRadius)
                    nearbyEligible.Add(i);
            }

            int selectedNearby = SelectLeastRecentlyUsedResidentIndex(locationData, nearbyEligible);
            if (selectedNearby >= 0)
                return locationData.residents[selectedNearby];

            return null;
        }

        private int SelectLeastRecentlyUsedResidentIndex(LocationResidentsDataV1 locationData, List<int> candidateIndices)
        {
            int bestIndex = -1;
            float oldestUseTime = float.MaxValue;
            for (int i = 0; i < candidateIndices.Count; i++)
            {
                int index = candidateIndices[i];
                ResidentDataV1 resident = locationData.residents[index];
                float lastAssigned = GetResidentLastAssignedAt(locationData.mapId, resident.residentId);
                if (bestIndex < 0 || lastAssigned < oldestUseTime)
                {
                    bestIndex = index;
                    oldestUseTime = lastAssigned;
                }
            }

            if (bestIndex >= 0)
                spawnCursorByMapId[locationData.mapId] = (bestIndex + 1) % locationData.residents.Count;

            return bestIndex;
        }

        private bool IsResidentAssigned(int mapId, int residentId)
        {
            foreach (KeyValuePair<int, ResidentAssignment> pair in activeAssignments)
            {
                if (pair.Value.mapId == mapId && pair.Value.residentId == residentId)
                    return true;
            }

            return false;
        }

        internal bool TryGetActiveResidentNpc(int mapId, int residentId, out MobilePersonNPC npc)
        {
            return activeResidentNpcs.TryGetValue(GetResidentUsageKey(mapId, residentId), out npc) && npc != null;
        }

        private void ClearRuntimeState()
        {
            activeAssignments.Clear();
            activeResidentNpcs.Clear();
            activePromotedGuards.Clear();
            spawnCursorByMapId.Clear();
            assignmentLogsByMapId.Clear();
            residentLastAssignedAt.Clear();
            targetFailureCounts.Clear();
            shelteredBuildingByResidentKey.Clear();
            DestroyInteriorResidents();
            DisposeNavigationContext();
        }

        private void DisposeNavigationContext()
        {
            activeNavigationContext = null;
        }

        private void EnsureNavigationContext(LocationResidentsDataV1 locationData)
        {
            if (locationData == null)
            {
                DisposeNavigationContext();
                return;
            }

            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (currentLocationObject == null)
            {
                DisposeNavigationContext();
                return;
            }

            CityNavigation cityNavigation = currentLocationObject.GetComponent<CityNavigation>();
            if (cityNavigation == null)
            {
                DisposeNavigationContext();
                return;
            }

            if (activeNavigationContext != null &&
                activeNavigationContext.Matches(locationData.mapId, currentLocationObject, cityNavigation))
            {
                return;
            }

            activeNavigationContext = new RadiantNPCsSharedNavigationContext(
                locationData.mapId,
                currentLocationObject,
                cityNavigation,
                LoadFlowFieldComputeShader());

            LogInfo(
                "RadiantNPCs: prepared shared navigation context for MapID={0} (patrolAnchors={1}, gpuFields={2}).",
                locationData.mapId,
                activeNavigationContext.PatrolAnchorCount,
                activeNavigationContext.UsesGpuFields);
        }

        private ComputeShader LoadFlowFieldComputeShader()
        {
            if (flowFieldShaderLoadAttempted)
                return flowFieldComputeShader;

            flowFieldShaderLoadAttempted = true;
            if (mod == null)
                return null;

            try
            {
                flowFieldComputeShader = mod.GetAsset<ComputeShader>(FlowFieldComputeShaderAssetName);
                if (flowFieldComputeShader == null)
                    LogInfo("RadiantNPCs: shared navigation compute shader asset '{0}' was not found. CPU field generation will be used.", FlowFieldComputeShaderAssetName);
            }
            catch (Exception ex)
            {
                flowFieldComputeShader = null;
                LogInfo("RadiantNPCs: failed to load compute shader asset '{0}'. Falling back to CPU fields. Error={1}", FlowFieldComputeShaderAssetName, ex.Message);
            }

            return flowFieldComputeShader;
        }

        private bool TryGetActiveNavigationContext(LocationResidentsDataV1 locationData, out RadiantNPCsSharedNavigationContext context)
        {
            context = activeNavigationContext;
            return context != null && locationData != null && context.MapId == locationData.mapId;
        }

        internal bool TryGetBuildingFlowField(LocationResidentsDataV1 locationData, int targetBuildingKey, int residentId, Vector3 targetLocalPosition, out Vector3 resolvedAnchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu = false)
        {
            resolvedAnchorLocalPosition = targetLocalPosition;
            field = null;
            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context))
                return false;

            return context.TryGetBuildingField(targetBuildingKey, targetLocalPosition, residentId, out resolvedAnchorLocalPosition, out field, preferCpu);
        }

        internal bool TryGetPatrolFlowField(LocationResidentsDataV1 locationData, int residentId, int requestedTargetKey, out int resolvedTargetKey, out Vector3 anchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu = false)
        {
            resolvedTargetKey = requestedTargetKey;
            anchorLocalPosition = Vector3.zero;
            field = null;

            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context))
                return false;

            return context.TryGetPatrolField(requestedTargetKey, residentId, out resolvedTargetKey, out anchorLocalPosition, out field, preferCpu);
        }

        internal bool TryAdvancePatrolFlowField(LocationResidentsDataV1 locationData, int currentTargetKey, int patrolDirection, out int nextTargetKey, out Vector3 anchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu = false)
        {
            nextTargetKey = currentTargetKey;
            anchorLocalPosition = Vector3.zero;
            field = null;

            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context))
                return false;

            return context.TryGetNextPatrolField(currentTargetKey, patrolDirection, out nextTargetKey, out anchorLocalPosition, out field, preferCpu);
        }

        private bool TryGetPatrolTargetKey(LocationResidentsDataV1 locationData, ResidentDataV1 resident, out int targetKey)
        {
            targetKey = resident.homeBuildingKey;
            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context))
                return false;

            targetKey = context.GetInitialPatrolTargetKey(resident.residentId);
            return targetKey <= RadiantNPCsSharedNavigationContext.PatrolTargetKeyBase;
        }

        internal int GetPatrolDirection(LocationResidentsDataV1 locationData, int residentId)
        {
            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context))
                return 1;

            return context.GetPatrolDirection(residentId);
        }

        private bool TryGetPatrolAnchorLocalPosition(LocationResidentsDataV1 locationData, int targetKey, out Vector3 localPosition)
        {
            localPosition = Vector3.zero;
            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context))
                return false;

            return context.TryGetPatrolAnchorLocalPosition(targetKey, out localPosition);
        }

        internal bool NotifyResidentReachedTarget(LocationResidentsDataV1 locationData, MobilePersonNPC npc, int residentId, ResidentState state, int targetBuildingKey)
        {
            if (locationData == null || npc == null)
                return false;
            if (targetBuildingKey <= 0)
                return false;

            RecordTargetSuccess(locationData.mapId, targetBuildingKey);
            RecordResidentExteriorPosition(locationData.mapId, residentId, npc.transform.position);
            long residentKey = GetResidentUsageKey(locationData.mapId, residentId);
            shelteredBuildingByResidentKey[residentKey] = targetBuildingKey;
            RequestRecycleActiveNpc(npc);

            if (npc.Asset != null)
            {
                MeshRenderer renderer = npc.Asset.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.enabled = false;
            }

            LogInfo(
                "RadiantNPCs: resident {0} reached target building {1} in state {2} and was sheltered indoors.",
                residentId,
                targetBuildingKey,
                state);

            return true;
        }

        private bool IsResidentSheltered(int mapId, int residentId)
        {
            return shelteredBuildingByResidentKey.ContainsKey(GetResidentUsageKey(mapId, residentId));
        }

        private void RecordResidentExteriorPosition(int mapId, int residentId, Vector3 scenePosition)
        {
            LocationResidentsDataV1 locationData = FindLocationData(mapId);
            int residentIndex = FindResidentIndexById(locationData, residentId);
            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (residentIndex < 0 || currentLocationObject == null)
                return;

            ResidentDataV1 resident = locationData.residents[residentIndex];
            if (resident == null)
                return;

            Vector3 localPosition = scenePosition - currentLocationObject.transform.position;
            resident.hasKnownExteriorPosition = true;
            resident.exteriorLocalPositionX = localPosition.x;
            resident.exteriorLocalPositionZ = localPosition.z;
            locationData.residents[residentIndex] = resident;
        }

        private Vector3 GetResidentExteriorLocalPosition(LocationResidentsDataV1 locationData, ResidentDataV1 resident)
        {
            if (resident != null && resident.hasKnownExteriorPosition)
                return new Vector3(resident.exteriorLocalPositionX, 0, resident.exteriorLocalPositionZ);

            return GetHouseholdHomeLocalPosition(locationData, resident);
        }

        private void ClearShelteredResidentsForMap(int mapId)
        {
            List<long> toRemove = new List<long>();
            foreach (KeyValuePair<long, int> pair in shelteredBuildingByResidentKey)
            {
                int shelteredMapId = (int)(pair.Key >> 32);
                if (shelteredMapId == mapId)
                    toRemove.Add(pair.Key);
            }

            for (int i = 0; i < toRemove.Count; i++)
                shelteredBuildingByResidentKey.Remove(toRemove[i]);
        }

        private void RequestRecycleActiveNpc(MobilePersonNPC npc)
        {
            if (npc == null)
                return;

            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (currentLocationObject == null)
                return;

            PopulationManager populationManager = currentLocationObject.GetComponent<PopulationManager>();
            if (populationManager == null)
                return;

            List<PopulationManager.PoolItem> pool = populationManager.PopulationPool;
            for (int i = 0; i < pool.Count; i++)
            {
                PopulationManager.PoolItem poolItem = pool[i];
                if (poolItem.npc != npc)
                    continue;

                poolItem.scheduleRecycle = true;
                poolItem.scheduleEnable = false;
                pool[i] = poolItem;
                return;
            }

            Destroy(npc.gameObject);
        }

        internal DaggerfallEntityBehaviour FindNearbyHostileThreat(Vector3 scenePosition, float radius)
        {
            float bestDistanceSq = radius * radius;
            DaggerfallEntityBehaviour best = null;
            DaggerfallEntityBehaviour[] entities = FindObjectsOfType<DaggerfallEntityBehaviour>();
            for (int i = 0; i < entities.Length; i++)
            {
                DaggerfallEntityBehaviour entityBehaviour = entities[i];
                if (entityBehaviour == null || entityBehaviour == GameManager.Instance.PlayerEntityBehaviour)
                    continue;

                EnemyMotor enemyMotor = entityBehaviour.GetComponent<EnemyMotor>();
                if (enemyMotor == null || !enemyMotor.IsHostile)
                    continue;

                EnemyEntity enemyEntity = entityBehaviour.Entity as EnemyEntity;
                if (enemyEntity == null || enemyEntity.MobileEnemy.ID == (int)MobileTypes.Knight_CityWatch)
                    continue;

                Vector3 delta = entityBehaviour.transform.position - scenePosition;
                delta.y = 0f;
                float sqrDistance = delta.sqrMagnitude;
                if (sqrDistance > bestDistanceSq)
                    continue;

                bestDistanceSq = sqrDistance;
                best = entityBehaviour;
            }

            return best;
        }

        internal bool PromoteGuardResidentToActualGuard(LocationResidentsDataV1 locationData, int residentId, MobilePersonNPC guardNpc, DaggerfallEntityBehaviour threat)
        {
            if (guardNpc == null || threat == null)
                return false;
            long residentKey = GetResidentUsageKey(locationData.mapId, residentId);
            if (activePromotedGuards.ContainsKey(residentKey))
                return false;

            RecordResidentExteriorPosition(locationData.mapId, residentId, guardNpc.transform.position);
            GameObject cityWatch = SpawnActualCityWatch(guardNpc.transform.position, guardNpc.transform.forward, threat, hostileToPlayer: false);
            if (cityWatch == null)
                return false;

            RadiantNPCsActualGuardController controller = cityWatch.GetComponent<RadiantNPCsActualGuardController>();
            if (controller == null)
                controller = cityWatch.AddComponent<RadiantNPCsActualGuardController>();
            controller.Configure(this, locationData.mapId, residentId);
            activePromotedGuards[residentKey] = cityWatch;

            RequestRecycleActiveNpc(guardNpc);
            if (guardNpc.Asset != null)
            {
                MeshRenderer renderer = guardNpc.Asset.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.enabled = false;
            }

            return true;
        }

        internal bool TryRestorePromotedGuardResident(int mapId, int residentId, Vector3 scenePosition, Vector3 forward, GameObject promotedGuardObject)
        {
            long residentKey = GetResidentUsageKey(mapId, residentId);
            GameObject trackedGuard;
            if (!activePromotedGuards.TryGetValue(residentKey, out trackedGuard))
                return false;
            if (trackedGuard != promotedGuardObject)
                return false;

            activePromotedGuards.Remove(residentKey);
            RecordResidentExteriorPosition(mapId, residentId, scenePosition);

            LocationResidentsDataV1 locationData = FindLocationData(mapId);
            int residentIndex = FindResidentIndexById(locationData, residentId);
            if (residentIndex < 0)
            {
                Destroy(promotedGuardObject);
                return false;
            }

            ResidentDataV1 resident = locationData.residents[residentIndex];
            if (resident == null || resident.isDead)
            {
                Destroy(promotedGuardObject);
                return false;
            }

            resident.currentState = (int)ResidentState.Patrol;
            resident.currentTargetBuildingKey = ChoosePatrolTarget(locationData, resident, DaggerfallUnity.Instance.WorldTime.Now);
            locationData.residents[residentIndex] = resident;

            if (!RestoreResidentPatrolNpc(locationData, resident, scenePosition, forward))
            {
                Destroy(promotedGuardObject);
                PrepareCurrentLocation(force: true);
                return false;
            }

            Destroy(promotedGuardObject);
            return true;
        }

        private bool RestoreResidentPatrolNpc(LocationResidentsDataV1 locationData, ResidentDataV1 resident, Vector3 scenePosition, Vector3 forward)
        {
            if (locationData == null || resident == null)
                return false;
            if (IsResidentAssigned(locationData.mapId, resident.residentId))
                return true;

            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (currentLocationObject == null || locationData.mapId != GameManager.Instance.PlayerGPS.CurrentMapID)
                return false;
            if (DaggerfallUnity.Instance == null || DaggerfallUnity.Instance.Option_MobileNPCPrefab == null)
                return false;

            GameObject go = GameObjectHelper.InstantiatePrefab(DaggerfallUnity.Instance.Option_MobileNPCPrefab.gameObject, "RadiantGuardResident", currentLocationObject.transform, Vector3.zero);
            if (go == null)
                return false;

            MobilePersonNPC npc = go.GetComponent<MobilePersonNPC>();
            MobilePersonMotor motor = go.GetComponent<MobilePersonMotor>();
            if (npc == null || motor == null)
            {
                Destroy(go);
                return false;
            }

            motor.cityNavigation = currentLocationObject.GetComponent<CityNavigation>();
            npc.Motor = motor;
            npc.Asset = motor.MobileAsset;
            if (npc.Asset == null)
            {
                Destroy(go);
                return false;
            }

            RadiantNPCsNpcDeathRelay relay = go.GetComponent<RadiantNPCsNpcDeathRelay>();
            if (relay == null)
                relay = go.AddComponent<RadiantNPCsNpcDeathRelay>();
            relay.Main = this;

            RadiantNPCsMovementController movement = go.GetComponent<RadiantNPCsMovementController>();
            if (movement == null)
                movement = go.AddComponent<RadiantNPCsMovementController>();
            movement.Main = this;

            ApplyResidentToMobileNpc(npc, resident);
            go.transform.position = scenePosition;
            go.transform.forward = forward;
            GameObjectHelper.AlignBillboardToGround(go, new Vector2(0, 2f));

            ResidentAssignment assignment = new ResidentAssignment();
            assignment.mapId = locationData.mapId;
            assignment.residentId = resident.residentId;
            activeAssignments[npc.GetInstanceID()] = assignment;
            activeResidentNpcs[GetResidentUsageKey(locationData.mapId, resident.residentId)] = npc;
            residentLastAssignedAt[GetResidentUsageKey(locationData.mapId, resident.residentId)] = Time.realtimeSinceStartup;

            movement.ConfigureDirectedMovement(locationData, resident, currentLocationObject.transform.position, GetResidentAnchorLocalPosition(locationData, resident), (ResidentState)resident.currentState);
            return true;
        }

        private GameObject SpawnActualCityWatch(Vector3 position, Vector3 direction, DaggerfallEntityBehaviour threat, bool hostileToPlayer)
        {
            GameObject[] cityWatch = GameObjectHelper.CreateFoeGameObjects(
                position,
                MobileTypes.Knight_CityWatch,
                1,
                hostileToPlayer ? MobileReactions.Hostile : MobileReactions.Passive,
                null,
                alliedToPlayer: !hostileToPlayer);

            if (cityWatch == null || cityWatch.Length == 0 || cityWatch[0] == null)
                return null;

            GameObject guard = cityWatch[0];
            guard.transform.forward = direction;

            EnemyMotor enemyMotor = guard.GetComponent<EnemyMotor>();
            EnemySenses enemySenses = guard.GetComponent<EnemySenses>();
            DaggerfallEntityBehaviour entityBehaviour = guard.GetComponent<DaggerfallEntityBehaviour>();
            if (enemyMotor != null)
            {
                if (hostileToPlayer)
                {
                    enemyMotor.MakeEnemyHostileToAttacker(GameManager.Instance.PlayerEntityBehaviour);
                }
                else
                {
                    enemyMotor.IsHostile = false;
                    if (entityBehaviour != null && entityBehaviour.Entity != null)
                        entityBehaviour.Entity.Team = MobileTeams.PlayerAlly;
                    enemyMotor.GiveUpTimer *= 3;
                }
            }

            if (!hostileToPlayer && enemySenses != null)
            {
                enemySenses.Target = threat;
                enemySenses.SecondaryTarget = threat;
                enemySenses.LastKnownTargetPos = threat.transform.position;
                enemySenses.OldLastKnownTargetPos = threat.transform.position;
                enemySenses.PredictedTargetPos = threat.transform.position;
            }

            guard.SetActive(true);
            return guard;
        }

        private void DestroyInteriorResidents()
        {
            for (int i = 0; i < activeInteriorNpcObjects.Count; i++)
            {
                if (activeInteriorNpcObjects[i] != null)
                {
                    MobilePersonMotor motor = activeInteriorNpcObjects[i].GetComponent<MobilePersonMotor>();
                    if (motor != null && motor.cityNavigation == null)
                    {
                        DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
                        if (currentLocationObject != null)
                            motor.cityNavigation = currentLocationObject.GetComponent<CityNavigation>();
                    }

                    Destroy(activeInteriorNpcObjects[i]);
                }
            }

            activeInteriorNpcObjects.Clear();
        }

        private void SpawnInteriorResidents(LocationResidentsDataV1 locationData, int buildingKey, DaggerfallInterior interior)
        {
            if (locationData == null || interior == null || buildingKey <= 0)
                return;
            if (DaggerfallUnity.Instance == null || DaggerfallUnity.Instance.Option_MobileNPCPrefab == null)
                return;

            List<InteriorSpawnCandidate> candidates = new List<InteriorSpawnCandidate>();
            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.isDead)
                    continue;

                long residentKey = GetResidentUsageKey(locationData.mapId, resident.residentId);
                bool shelteredHere = shelteredBuildingByResidentKey.ContainsKey(residentKey) &&
                    shelteredBuildingByResidentKey[residentKey] == buildingKey;
                bool atHomeHere = (ResidentState)resident.currentState == ResidentState.AtHome &&
                    resident.homeBuildingKey == buildingKey;
                if (!shelteredHere && !atHomeHere)
                    continue;

                InteriorSpawnCandidate candidate = new InteriorSpawnCandidate();
                candidate.Resident = resident;
                candidate.IsSheltered = shelteredHere;
                candidates.Add(candidate);
            }

            if (candidates.Count == 0)
                return;

            List<Vector3> spawnMarkers = new List<Vector3>();
            Vector3[] restMarkers = interior.FindMarkers(DaggerfallInterior.InteriorMarkerTypes.Rest);
            for (int i = 0; i < restMarkers.Length; i++)
                spawnMarkers.Add(restMarkers[i]);

            Vector3 enterMarker = Vector3.zero;
            bool hasEnterMarker = interior.FindMarker(out enterMarker, DaggerfallInterior.InteriorMarkerTypes.Enter);
            if (hasEnterMarker)
            {
                for (int i = 0; i < 6; i++)
                {
                    float angle = i / 6f * Mathf.PI * 2f;
                    Vector3 offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.9f;
                    spawnMarkers.Add(enterMarker + offset);
                }
            }

            if (spawnMarkers.Count == 0)
                return;

            StaticNPC[] staticNpcs = interior.GetComponentsInChildren<StaticNPC>(true);
            List<StaticNPC> activeStaticNpcs = new List<StaticNPC>();
            for (int i = 0; i < staticNpcs.Length; i++)
            {
                if (staticNpcs[i] != null && staticNpcs[i].gameObject.activeInHierarchy)
                    activeStaticNpcs.Add(staticNpcs[i]);
            }

            CityNavigation exteriorNavigation = null;
            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (currentLocationObject != null)
                exteriorNavigation = currentLocationObject.GetComponent<CityNavigation>();

            int spawnCount = Mathf.Min(candidates.Count, spawnMarkers.Count);
            for (int i = 0; i < spawnCount; i++)
            {
                GameObject go = GameObjectHelper.InstantiatePrefab(
                    DaggerfallUnity.Instance.Option_MobileNPCPrefab.gameObject,
                    "RadiantInteriorNPC",
                    interior.transform,
                    Vector3.zero);
                if (go == null)
                    continue;

                if (candidates[i].IsSheltered && hasEnterMarker)
                    go.transform.position = enterMarker + GetInteriorEntryOffset(candidates[i].Resident.residentId, i);
                else
                    go.transform.position = spawnMarkers[i];
                MobilePersonNPC npc = go.GetComponent<MobilePersonNPC>();
                if (npc == null)
                {
                    Destroy(go);
                    continue;
                }

                MobilePersonMotor interiorMotor = go.GetComponent<MobilePersonMotor>();
                if (interiorMotor != null)
                {
                    interiorMotor.cityNavigation = exteriorNavigation;
                    interiorMotor.InitMotor();
                    npc.Motor = interiorMotor;
                    npc.Asset = interiorMotor.MobileAsset;
                }
                if (npc.Asset == null)
                {
                    Destroy(go);
                    continue;
                }

                ApplyResidentToMobileNpc(npc, candidates[i].Resident);
                if (npc.Motor != null)
                    npc.Motor.enabled = false;

                GameObjectHelper.AlignBillboardToGround(go, new Vector2(0, 2f), 4f);

                RadiantNPCsMovementController movement = go.GetComponent<RadiantNPCsMovementController>();
                if (movement != null)
                    movement.enabled = false;

                RadiantNPCsNpcDeathRelay relay = go.GetComponent<RadiantNPCsNpcDeathRelay>();
                if (relay == null)
                    relay = go.AddComponent<RadiantNPCsNpcDeathRelay>();
                relay.Main = this;

                if (candidates[i].IsSheltered && activeStaticNpcs.Count > 0 && hasEnterMarker)
                {
                    StaticNPC focusNpc = ChooseInteriorConversationNpc(activeStaticNpcs, candidates[i].Resident.residentId, i);
                    if (focusNpc != null)
                    {
                        RadiantNPCsInteriorVisitorController controller = go.GetComponent<RadiantNPCsInteriorVisitorController>();
                        if (controller == null)
                            controller = go.AddComponent<RadiantNPCsInteriorVisitorController>();

                        Vector3 talkPosition = ComputeInteriorConversationPosition(focusNpc.transform.position, candidates[i].Resident.residentId, i);
                        controller.Configure(this, locationData.mapId, buildingKey, candidates[i].Resident.residentId, npc, focusNpc.transform, talkPosition, enterMarker);
                    }
                    else if (npc.Asset != null)
                    {
                        npc.Asset.IsIdle = true;
                    }
                }
                else if (npc.Asset != null)
                {
                    npc.Asset.IsIdle = true;
                }

                activeInteriorNpcObjects.Add(go);
            }

            LogInfo(
                "RadiantNPCs: spawned {0} interior residents for building {1}.",
                spawnCount,
                buildingKey);
        }

        private StaticNPC ChooseInteriorConversationNpc(List<StaticNPC> activeStaticNpcs, int residentId, int ordinal)
        {
            if (activeStaticNpcs == null || activeStaticNpcs.Count == 0)
                return null;

            int index = Mathf.Abs(residentId * 73 + ordinal * 17) % activeStaticNpcs.Count;
            return activeStaticNpcs[index];
        }

        private Vector3 ComputeInteriorConversationPosition(Vector3 npcPosition, int residentId, int ordinal)
        {
            float angle = ((residentId * 37 + ordinal * 53) & 1023) / 1023f * Mathf.PI * 2f;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.8f;
            return npcPosition + offset;
        }

        private Vector3 GetInteriorEntryOffset(int residentId, int ordinal)
        {
            float angle = ((residentId * 19 + ordinal * 29) & 1023) / 1023f * Mathf.PI * 2f;
            return new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.45f;
        }

        internal void HandleInteriorVisitorDeparture(int mapId, int buildingKey, int residentId, GameObject visitorObject)
        {
            if (visitorObject != null)
            {
                activeInteriorNpcObjects.Remove(visitorObject);
                Destroy(visitorObject);
            }

            long residentKey = GetResidentUsageKey(mapId, residentId);
            shelteredBuildingByResidentKey.Remove(residentKey);

            LocationResidentsDataV1 locationData = FindLocationData(mapId);
            int residentIndex = FindResidentIndexById(locationData, residentId);
            if (residentIndex < 0)
                return;

            ResidentDataV1 resident = locationData.residents[residentIndex];
            if (resident == null || resident.isDead)
                return;

            BuildingTargetDataV1 target = FindTarget(locationData, buildingKey);
            if (target != null)
            {
                resident.hasKnownExteriorPosition = true;
                resident.exteriorLocalPositionX = target.localPositionX;
                resident.exteriorLocalPositionZ = target.localPositionZ;
            }

            if ((ResidentState)resident.currentState != ResidentState.AtHome)
            {
                resident.currentState = (int)ResidentState.ExteriorWander;
                resident.currentTargetBuildingKey = ChooseWanderTarget(locationData, resident, DaggerfallUnity.Instance.WorldTime.Now, new Dictionary<int, int>());
                locationData.residents[residentIndex] = resident;
            }
        }

        private LocationResidentsDataV1 FindLocationData(int mapId)
        {
            for (int i = 0; i < saveData.locations.Count; i++)
            {
                if (saveData.locations[i].mapId == mapId)
                    return saveData.locations[i];
            }

            return null;
        }

        private HouseholdDataV1 FindHousehold(LocationResidentsDataV1 locationData, int householdId)
        {
            if (locationData == null || locationData.households == null)
                return null;

            for (int i = 0; i < locationData.households.Count; i++)
            {
                if (locationData.households[i].householdId == householdId)
                    return locationData.households[i];
            }

            return null;
        }

        private ResidentDataV1 FindResidentById(LocationResidentsDataV1 locationData, int residentId)
        {
            if (locationData == null || locationData.residents == null)
                return null;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                if (locationData.residents[i].residentId == residentId)
                    return locationData.residents[i];
            }

            return null;
        }

        private int FindResidentIndexById(LocationResidentsDataV1 locationData, int residentId)
        {
            if (locationData == null || locationData.residents == null)
                return -1;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                if (locationData.residents[i].residentId == residentId)
                    return i;
            }

            return -1;
        }

        private List<ResidentDataV1> GetHouseholdResidents(LocationResidentsDataV1 locationData, int householdId)
        {
            List<ResidentDataV1> results = new List<ResidentDataV1>();
            if (locationData == null || locationData.residents == null)
                return results;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                if (locationData.residents[i].householdId == householdId)
                    results.Add(locationData.residents[i]);
            }

            return results;
        }

        private List<BuildingSummary> GetResidentialBuildings(BuildingDirectory buildingDirectory)
        {
            List<BuildingSummary> residences = new List<BuildingSummary>();
            residences.AddRange(buildingDirectory.GetBuildingsOfType(DFLocation.BuildingTypes.House1));
            residences.AddRange(buildingDirectory.GetBuildingsOfType(DFLocation.BuildingTypes.House2));
            residences.AddRange(buildingDirectory.GetBuildingsOfType(DFLocation.BuildingTypes.House3));
            residences.AddRange(buildingDirectory.GetBuildingsOfType(DFLocation.BuildingTypes.House4));
            return residences;
        }

        private static int CompareBuildingsByKey(BuildingSummary left, BuildingSummary right)
        {
            return left.buildingKey.CompareTo(right.buildingKey);
        }

        private int GetLayoutX(int buildingKey)
        {
            int layoutX;
            int layoutY;
            int recordIndex;
            BuildingDirectory.ReverseBuildingKey(buildingKey, out layoutX, out layoutY, out recordIndex);
            return layoutX;
        }

        private int GetLayoutY(int buildingKey)
        {
            int layoutX;
            int layoutY;
            int recordIndex;
            BuildingDirectory.ReverseBuildingKey(buildingKey, out layoutX, out layoutY, out recordIndex);
            return layoutY;
        }

        private Vector3 GetBuildingLocalPosition(int layoutX, int layoutY, Vector3 positionWithinBlock)
        {
            return new Vector3(layoutX * RMBLayout.RMBSide, 0, layoutY * RMBLayout.RMBSide) + positionWithinBlock;
        }

        private int GetOccupantCount(BuildingSummary residence, int bedCapacity)
        {
            if (bedCapacity > 0)
                return bedCapacity;

            return GetFallbackOccupantCount(residence);
        }

        private int GetFallbackOccupantCount(BuildingSummary residence)
        {
            int min = 1;
            int max = 2;
            switch (residence.BuildingType)
            {
                case DFLocation.BuildingTypes.House1:
                    min = 1;
                    max = 2;
                    break;
                case DFLocation.BuildingTypes.House2:
                    min = 2;
                    max = 3;
                    break;
                case DFLocation.BuildingTypes.House3:
                    min = 3;
                    max = 4;
                    break;
                case DFLocation.BuildingTypes.House4:
                    min = 4;
                    max = 6;
                    break;
            }

            return WithDFSeed(residence.buildingKey * 31 + 7, delegate ()
            {
                return DFRandom.random_range_inclusive(min, max);
            });
        }

        private int GetBedCapacity(DFBlock[] locationBlocks, int locationWidth, int locationHeight, BuildingSummary residence, out bool usedFallbackCapacity)
        {
            int markerCapacity = CountInteriorRestMarkerCapacity(locationBlocks, locationWidth, locationHeight, residence.buildingKey);
            int bedCapacity = CountInteriorBedCapacity(locationBlocks, locationWidth, locationHeight, residence.buildingKey);
            int combinedCapacity = Mathf.Max(markerCapacity, bedCapacity);
            if (combinedCapacity > 0)
            {
                usedFallbackCapacity = false;
                return combinedCapacity;
            }

            usedFallbackCapacity = true;
            int fallbackCapacity = GetFallbackOccupantCount(residence);
            return fallbackCapacity;
        }

        private int CountInteriorBedCapacity(DFBlock[] locationBlocks, int locationWidth, int locationHeight, int buildingKey)
        {
            DFBlock.RmbSubRecord subRecord;
            if (!TryGetInteriorSubRecord(locationBlocks, locationWidth, locationHeight, buildingKey, out subRecord))
                return 0;
            int bedCapacity = 0;
            for (int i = 0; i < subRecord.Interior.Block3dObjectRecords.Length; i++)
            {
                uint modelId = subRecord.Interior.Block3dObjectRecords[i].ModelIdNum;
                switch (modelId)
                {
                    case SingleBedModelIdA:
                    case SingleBedModelIdB:
                        bedCapacity += 1;
                        break;
                    case DoubleBedModelId:
                        bedCapacity += 2;
                        break;
                }
            }

            return bedCapacity;
        }

        private int CountInteriorRestMarkerCapacity(DFBlock[] locationBlocks, int locationWidth, int locationHeight, int buildingKey)
        {
            DFBlock.RmbSubRecord subRecord;
            if (!TryGetInteriorSubRecord(locationBlocks, locationWidth, locationHeight, buildingKey, out subRecord))
                return 0;
            int markerCount = 0;
            for (int i = 0; i < subRecord.Interior.BlockFlatObjectRecords.Length; i++)
            {
                DFBlock.RmbBlockFlatObjectRecord flat = subRecord.Interior.BlockFlatObjectRecords[i];
                if (flat.TextureArchive == TextureReader.EditorFlatsTextureArchive &&
                    flat.TextureRecord == (int)DaggerfallInterior.InteriorMarkerTypes.Rest)
                {
                    markerCount++;
                }
            }

            return markerCount;
        }

        private bool TryGetInteriorSubRecord(DFBlock[] locationBlocks, int locationWidth, int locationHeight, int buildingKey, out DFBlock.RmbSubRecord subRecordOut)
        {
            subRecordOut = default(DFBlock.RmbSubRecord);
            if (locationBlocks == null)
                return false;

            int layoutX;
            int layoutY;
            int recordIndex;
            BuildingDirectory.ReverseBuildingKey(buildingKey, out layoutX, out layoutY, out recordIndex);
            if (layoutX < 0 || layoutY < 0 || layoutX >= locationWidth || layoutY >= locationHeight)
                return false;

            int blockIndex = layoutY * locationWidth + layoutX;
            if (blockIndex < 0 || blockIndex >= locationBlocks.Length)
                return false;

            DFBlock block = locationBlocks[blockIndex];
            if (block.Type != DFBlock.BlockTypes.Rmb)
                return false;

            if (recordIndex < 0 || recordIndex >= block.RmbBlock.SubRecords.Length)
                return false;

            subRecordOut = block.RmbBlock.SubRecords[recordIndex];
            return true;
        }

        private Genders GetHouseholdGender(int occupantCount, int occupantIndex, int buildingKey)
        {
            if (occupantCount >= 2)
            {
                if (occupantIndex == 0)
                    return Genders.Male;
                if (occupantIndex == 1)
                    return Genders.Female;
            }

            return WithDFSeed(buildingKey * 17 + occupantIndex * 13, delegate ()
            {
                return (DFRandom.rand() & 1) == 0 ? Genders.Male : Genders.Female;
            });
        }

        private int GetOutfitVariant(int buildingKey, int occupantIndex)
        {
            return WithDFSeed(buildingKey * 13 + occupantIndex * 7, delegate ()
            {
                return DFRandom.random_range(0, 4);
            });
        }

        private int GetFaceVariant(int buildingKey, int occupantIndex)
        {
            return WithDFSeed(buildingKey * 19 + occupantIndex * 11, delegate ()
            {
                return DFRandom.random_range(0, MaxFaceVariant);
            });
        }

        private int GetDisposition(int buildingKey, int occupantIndex)
        {
            return WithDFSeed(buildingKey * 23 + occupantIndex * 29, delegate ()
            {
                return DFRandom.random_range_inclusive(35, 75);
            });
        }

        private string GenerateFirstName(NameHelper.BankTypes bankType, Genders gender, int seed)
        {
            return WithDFSeed(seed, delegate ()
            {
                return DaggerfallUnity.Instance.NameHelper.FirstName(bankType, gender);
            });
        }

        private string GenerateSurname(NameHelper.BankTypes bankType, int seed)
        {
            string surname = WithDFSeed(seed, delegate ()
            {
                return DaggerfallUnity.Instance.NameHelper.Surname(bankType);
            });

            if (!string.IsNullOrEmpty(surname))
                return surname;

            return WithDFSeed(seed + 101, delegate ()
            {
                return DaggerfallUnity.Instance.NameHelper.Surname(NameHelper.BankTypes.Breton);
            });
        }

        private string ComposeFullName(string firstName, string surname)
        {
            if (string.IsNullOrEmpty(surname))
                return firstName;

            return string.Format("{0} {1}", firstName, surname);
        }

        private ResidentRole GetResidentRole(int buildingKey, int occupantIndex)
        {
            return WithDFSeed(buildingKey * 47 + occupantIndex * 5, delegate ()
            {
                return DFRandom.random_range(100) < 8 ? ResidentRole.Guard : ResidentRole.Civilian;
            });
        }

        private bool GetPrefersNightlife(int buildingKey, int occupantIndex)
        {
            return WithDFSeed(buildingKey * 59 + occupantIndex * 7, delegate ()
            {
                return DFRandom.random_range(100) < 30;
            });
        }

        private bool GetPrefersShopping(int buildingKey, int occupantIndex)
        {
            return WithDFSeed(buildingKey * 61 + occupantIndex * 9, delegate ()
            {
                return DFRandom.random_range(100) < 55;
            });
        }

        private bool GetPrefersSocialVisits(int buildingKey, int occupantIndex)
        {
            return WithDFSeed(buildingKey * 67 + occupantIndex * 11, delegate ()
            {
                return DFRandom.random_range(100) < 25;
            });
        }

        private void RefreshResidentStates(LocationResidentsDataV1 locationData)
        {
            if (locationData == null || locationData.residents == null)
                return;

            DaggerfallDateTime now = DaggerfallUnity.Instance.WorldTime.Now;
            int scheduleSignature = now.DayOfYear * 24 + now.Hour;
            if (locationData.lastScheduleSignature == scheduleSignature)
            {
                RefreshPatrolTargets(locationData, now);
                return;
            }

            ClearShelteredResidentsForMap(locationData.mapId);

            int homeCount = 0;
            int wanderCount = 0;
            int shopCount = 0;
            int visitCount = 0;
            int tavernCount = 0;
            int patrolCount = 0;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.isDead)
                {
                    resident.currentState = (int)ResidentState.Dead;
                    resident.currentTargetBuildingKey = resident.homeBuildingKey;
                    resident.lastScheduleSignature = scheduleSignature;
                    locationData.residents[i] = resident;
                    continue;
                }

                ResidentState state = ComputeResidentState(locationData, resident, now);
                resident.currentState = (int)state;
                resident.currentTargetBuildingKey = resident.homeBuildingKey;
                resident.lastScheduleSignature = scheduleSignature;
                locationData.residents[i] = resident;
            }

            BalanceHouseholdPresence(locationData, now);
            SynchronizeCoupleStates(locationData, now);
            SynchronizeGuardPatrolStates(locationData, now);

            Dictionary<int, int> plannedTargetLoads = new Dictionary<int, int>();
            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.isDead)
                    continue;

                ResidentState state = (ResidentState)resident.currentState;
                resident.currentTargetBuildingKey = GetTargetBuildingKeyForState(locationData, resident, state, now, plannedTargetLoads);
                IncrementTargetLoad(plannedTargetLoads, resident.currentTargetBuildingKey);
                locationData.residents[i] = resident;
            }

            SynchronizeCoupleTargets(locationData);
            SynchronizeGuardPatrolTargets(locationData, now);

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentState state = (ResidentState)locationData.residents[i].currentState;
                switch (state)
                {
                    case ResidentState.ExteriorWander:
                        wanderCount++;
                        break;
                    case ResidentState.Shopping:
                        shopCount++;
                        break;
                    case ResidentState.SocialVisit:
                        visitCount++;
                        break;
                    case ResidentState.Tavern:
                        tavernCount++;
                        break;
                    case ResidentState.Patrol:
                        patrolCount++;
                        break;
                    default:
                        homeCount++;
                        break;
                }
            }

            locationData.lastScheduleSignature = scheduleSignature;
            LogInfo(
                "RadiantNPCs: schedule summary for {0}/{1} at {2:00}:00 - home={3}, wander={4}, shop={5}, visit={6}, tavern={7}, patrol={8}.",
                locationData.regionName,
                locationData.locationName,
                now.Hour,
                homeCount,
                wanderCount,
                shopCount,
                visitCount,
                tavernCount,
                patrolCount);
        }

        private void RefreshPatrolTargets(LocationResidentsDataV1 locationData, DaggerfallDateTime now)
        {
            if (locationData == null || locationData.residents == null)
                return;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.isDead || (ResidentState)resident.currentState != ResidentState.Patrol)
                    continue;

                resident.currentTargetBuildingKey = ChoosePatrolTarget(locationData, resident, now);
                locationData.residents[i] = resident;
            }
        }

        private ResidentState ComputeResidentState(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now)
        {
            int hour = now.Hour;
            bool weekend = now.DayValue == DaggerfallDateTime.Days.Sundas || now.DayValue == DaggerfallDateTime.Days.Loredas;

            if ((ResidentRole)resident.role == ResidentRole.Guard)
            {
                if (hour >= 7 && hour < 20)
                {
                    return WithDFSeed(locationData.mapId + resident.residentId + hour * 13, delegate ()
                    {
                        return DFRandom.random_range(100) < 75 ? ResidentState.Patrol : ResidentState.AtHome;
                    });
                }

                if (hour >= 20 || hour < 6)
                {
                    return WithDFSeed(locationData.mapId + resident.residentId + hour * 17, delegate ()
                    {
                        return DFRandom.random_range(100) < 40 ? ResidentState.Patrol : ResidentState.AtHome;
                    });
                }

                return ResidentState.AtHome;
            }

            if (hour < 6)
                return ResidentState.AtHome;
            if (hour < 9)
            {
                return WithDFSeed(locationData.mapId + resident.residentId + hour * 19, delegate ()
                {
                    return DFRandom.random_range(100) < 35 ? ResidentState.ExteriorWander : ResidentState.AtHome;
                });
            }
            if (hour < 12)
            {
                if (resident.prefersShopping && WithDFSeed(locationData.mapId + resident.residentId + hour * 23, delegate () { return DFRandom.random_range(100) < 40; }))
                    return ResidentState.Shopping;
                return WithDFSeed(locationData.mapId + resident.residentId + hour * 29, delegate ()
                {
                    return DFRandom.random_range(100) < 55 ? ResidentState.ExteriorWander : ResidentState.AtHome;
                });
            }
            if (hour < 17)
            {
                if (resident.prefersSocialVisits && WithDFSeed(locationData.mapId + resident.residentId + hour * 31, delegate () { return DFRandom.random_range(100) < 20; }))
                    return ResidentState.SocialVisit;
                if (resident.prefersShopping && WithDFSeed(locationData.mapId + resident.residentId + hour * 37, delegate () { return DFRandom.random_range(100) < 30; }))
                    return ResidentState.Shopping;
                return WithDFSeed(locationData.mapId + resident.residentId + hour * 41, delegate ()
                {
                    return DFRandom.random_range(100) < 65 ? ResidentState.ExteriorWander : ResidentState.AtHome;
                });
            }
            if (hour < 22)
            {
                if (resident.prefersNightlife && WithDFSeed(locationData.mapId + resident.residentId + hour * 43 + (weekend ? 101 : 0), delegate () { return DFRandom.random_range(100) < (weekend ? 55 : 35); }))
                    return ResidentState.Tavern;
                return WithDFSeed(locationData.mapId + resident.residentId + hour * 47, delegate ()
                {
                    return DFRandom.random_range(100) < 30 ? ResidentState.ExteriorWander : ResidentState.AtHome;
                });
            }

            return ResidentState.AtHome;
        }

        private int GetTargetBuildingKeyForState(LocationResidentsDataV1 locationData, ResidentDataV1 resident, ResidentState state, DaggerfallDateTime now, Dictionary<int, int> plannedTargetLoads)
        {
            switch (state)
            {
                case ResidentState.ExteriorWander:
                    return ChooseWanderTarget(locationData, resident, now, plannedTargetLoads);
                case ResidentState.Shopping:
                    return ChooseShopTarget(locationData, resident, now, plannedTargetLoads);
                case ResidentState.SocialVisit:
                    return ChooseVisitTarget(locationData, resident, now, plannedTargetLoads);
                case ResidentState.Tavern:
                    return ChooseTavernTarget(locationData, resident, now, plannedTargetLoads);
                case ResidentState.Patrol:
                    return ChoosePatrolTarget(locationData, resident, now);
                default:
                    return resident.homeBuildingKey;
            }
        }

        private int ChooseWanderTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now, Dictionary<int, int> plannedTargetLoads)
        {
            List<BuildingTargetDataV1> targets = GetWanderTargets(locationData, now);
            return ChooseBalancedTarget(locationData, resident, now.Hour * 43, targets, plannedTargetLoads, avoidHome: true);
        }

        private int ChooseShopTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now, Dictionary<int, int> plannedTargetLoads)
        {
            List<BuildingTargetDataV1> shops = GetShopTargets(locationData);
            return ChooseBalancedTarget(locationData, resident, now.Hour * 53, shops, plannedTargetLoads, avoidHome: false);
        }

        private int ChooseVisitTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now, Dictionary<int, int> plannedTargetLoads)
        {
            List<BuildingTargetDataV1> homes = GetTargets(locationData, onlyShops: false, onlyTaverns: false, onlyResidences: true);
            return ChooseBalancedTarget(locationData, resident, now.Hour * 57, homes, plannedTargetLoads, avoidHome: true);
        }

        private int ChooseTavernTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now, Dictionary<int, int> plannedTargetLoads)
        {
            List<BuildingTargetDataV1> taverns = GetTavernTargets(locationData);
            return ChooseBalancedTarget(locationData, resident, now.Hour * 61, taverns, plannedTargetLoads, avoidHome: false);
        }

        private int ChoosePatrolTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now)
        {
            int patrolTargetKey;
            if (TryChoosePatrolTarget(locationData, resident, out patrolTargetKey))
                return patrolTargetKey;

            return resident.homeBuildingKey;
        }

        private bool TryChoosePatrolTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, out int patrolTargetKey)
        {
            patrolTargetKey = resident.homeBuildingKey;

            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context) || context.PatrolAnchorCount <= 0)
                return false;

            int direction = context.GetPatrolDirection(resident.residentId);
            int startTargetKey = context.GetInitialPatrolTargetKey(resident.residentId);
            int startIndex = RadiantNPCsSharedNavigationContext.PatrolTargetKeyBase - startTargetKey;
            if (startIndex < 0)
                startIndex = 0;

            for (int offset = 0; offset < context.PatrolAnchorCount; offset++)
            {
                int candidateIndex = startIndex + offset * (direction >= 0 ? 1 : -1);
                while (candidateIndex < 0)
                    candidateIndex += context.PatrolAnchorCount;
                candidateIndex %= context.PatrolAnchorCount;

                int candidateTargetKey = RadiantNPCsSharedNavigationContext.PatrolTargetKeyBase - candidateIndex;
                if (IsTargetBlacklisted(locationData.mapId, candidateTargetKey))
                    continue;

                patrolTargetKey = candidateTargetKey;
                return true;
            }

            return false;
        }

        internal bool TryRetargetResidentAfterNavigationFailure(LocationResidentsDataV1 locationData, int residentId, ResidentState state, int failedTargetKey, out int resolvedTargetKey, out Vector3 resolvedAnchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu = false)
        {
            resolvedTargetKey = failedTargetKey;
            resolvedAnchorLocalPosition = Vector3.zero;
            field = null;
            if (locationData == null)
                return false;

            RecordTargetFailure(locationData.mapId, failedTargetKey);

            int residentIndex = FindResidentIndexById(locationData, residentId);
            if (residentIndex < 0)
                return false;

            ResidentDataV1 resident = locationData.residents[residentIndex];
            if (resident == null || resident.isDead)
                return false;

            if (state == ResidentState.Patrol)
            {
                if (!TryRetargetPatrolResident(locationData, resident, failedTargetKey, out resolvedTargetKey, out resolvedAnchorLocalPosition, out field, preferCpu))
                    return false;
            }
            else
            {
                if (!TryRetargetBuildingResident(locationData, resident, state, failedTargetKey, out resolvedTargetKey, out resolvedAnchorLocalPosition, out field, preferCpu))
                    return false;
            }

            resident.currentTargetBuildingKey = resolvedTargetKey;
            locationData.residents[residentIndex] = resident;
            return true;
        }

        private bool TryRetargetBuildingResident(LocationResidentsDataV1 locationData, ResidentDataV1 resident, ResidentState state, int failedTargetKey, out int resolvedTargetKey, out Vector3 resolvedAnchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu)
        {
            resolvedTargetKey = failedTargetKey;
            resolvedAnchorLocalPosition = Vector3.zero;
            field = null;

            List<BuildingTargetDataV1> candidates = GetTargetsForState(locationData, state);
            if (candidates.Count == 0)
                return false;

            DaggerfallDateTime now = DaggerfallUnity.Instance.WorldTime.Now;
            List<ScoredTargetCandidate> scoredTargets = new List<ScoredTargetCandidate>();
            for (int i = 0; i < candidates.Count; i++)
            {
                BuildingTargetDataV1 target = candidates[i];
                if (target.buildingKey == failedTargetKey)
                    continue;
                if (state == ResidentState.SocialVisit && target.buildingKey == resident.homeBuildingKey)
                    continue;

                int failureCount = GetTargetFailureCount(locationData.mapId, target.buildingKey);
                if (failureCount >= TargetBlacklistThreshold)
                    continue;

                float baseScore = failureCount * TargetFailurePenalty;
                baseScore += GetStableTargetNoise(locationData.mapId, resident.residentId, now.Hour * 79 + state.GetHashCode(), target.buildingKey);
                scoredTargets.Add(new ScoredTargetCandidate(target, baseScore));
            }

            scoredTargets.Sort(CompareScoredTargets);
            for (int i = 0; i < scoredTargets.Count; i++)
            {
                BuildingTargetDataV1 target = scoredTargets[i].Target;
                Vector3 targetLocalPosition = new Vector3(target.localPositionX, 0, target.localPositionZ);
                RadiantNPCsFlowField candidateField;
                Vector3 candidateAnchorLocalPosition;
                if (!TryGetBuildingFlowField(locationData, target.buildingKey, resident.residentId, targetLocalPosition, out candidateAnchorLocalPosition, out candidateField, preferCpu))
                    continue;

                resolvedTargetKey = target.buildingKey;
                resolvedAnchorLocalPosition = candidateAnchorLocalPosition;
                field = candidateField;
                return true;
            }

            return false;
        }

        private bool TryRetargetPatrolResident(LocationResidentsDataV1 locationData, ResidentDataV1 resident, int failedTargetKey, out int resolvedTargetKey, out Vector3 resolvedAnchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu)
        {
            resolvedTargetKey = failedTargetKey;
            resolvedAnchorLocalPosition = Vector3.zero;
            field = null;

            RadiantNPCsSharedNavigationContext context;
            if (!TryGetActiveNavigationContext(locationData, out context) || context.PatrolAnchorCount <= 0)
                return false;

            int direction = context.GetPatrolDirection(resident.residentId);
            int startTargetKey = failedTargetKey;
            if (startTargetKey > RadiantNPCsSharedNavigationContext.PatrolTargetKeyBase)
                startTargetKey = context.GetInitialPatrolTargetKey(resident.residentId);

            int startIndex = RadiantNPCsSharedNavigationContext.PatrolTargetKeyBase - startTargetKey;
            if (startIndex < 0)
                startIndex = 0;

            for (int offset = 1; offset <= context.PatrolAnchorCount; offset++)
            {
                int candidateIndex = startIndex + offset * (direction >= 0 ? 1 : -1);
                while (candidateIndex < 0)
                    candidateIndex += context.PatrolAnchorCount;
                candidateIndex %= context.PatrolAnchorCount;

                int candidateTargetKey = RadiantNPCsSharedNavigationContext.PatrolTargetKeyBase - candidateIndex;
                if (candidateTargetKey == failedTargetKey || IsTargetBlacklisted(locationData.mapId, candidateTargetKey))
                    continue;

                RadiantNPCsFlowField candidateField;
                Vector3 candidateAnchorLocalPosition;
                int candidateResolvedTargetKey;
                if (!TryGetPatrolFlowField(locationData, resident.residentId, candidateTargetKey, out candidateResolvedTargetKey, out candidateAnchorLocalPosition, out candidateField, preferCpu))
                    continue;

                resolvedTargetKey = candidateResolvedTargetKey;
                resolvedAnchorLocalPosition = candidateAnchorLocalPosition;
                field = candidateField;
                return true;
            }

            return false;
        }

        private int ChooseBalancedTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, int seedSalt, List<BuildingTargetDataV1> candidates, Dictionary<int, int> plannedTargetLoads, bool avoidHome)
        {
            if (candidates == null || candidates.Count == 0)
                return resident.homeBuildingKey;

            int bestTargetKey = resident.homeBuildingKey;
            float bestScore = float.MaxValue;
            bool found = false;

            for (int i = 0; i < candidates.Count; i++)
            {
                BuildingTargetDataV1 target = candidates[i];
                if (avoidHome && target.buildingKey == resident.homeBuildingKey)
                    continue;
                if (IsTargetBlacklisted(locationData.mapId, target.buildingKey))
                    continue;

                int load = GetTargetLoad(plannedTargetLoads, target.buildingKey);
                int failureCount = GetTargetFailureCount(locationData.mapId, target.buildingKey);
                float noise = GetStableTargetNoise(locationData.mapId, resident.residentId, seedSalt, target.buildingKey);
                float score = load * TargetLoadPenalty + failureCount * TargetFailurePenalty + noise;

                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    bestTargetKey = target.buildingKey;
                }
            }

            if (found)
                return bestTargetKey;

            return resident.homeBuildingKey;
        }

        private List<BuildingTargetDataV1> GetWanderTargets(LocationResidentsDataV1 locationData, DaggerfallDateTime now)
        {
            List<BuildingTargetDataV1> results = new List<BuildingTargetDataV1>();
            if (locationData == null || locationData.targets == null)
                return results;

            bool preferPublicVenues = now.Hour >= 9 && now.Hour < 19;
            for (int i = 0; i < locationData.targets.Count; i++)
            {
                BuildingTargetDataV1 target = locationData.targets[i];
                if (preferPublicVenues)
                {
                    if (target.isPublicVenue || target.isResidence)
                        results.Add(target);
                }
                else
                {
                    if (target.isResidence || target.isTavern || target.isPublicVenue)
                        results.Add(target);
                }
            }

            return results;
        }

        private List<BuildingTargetDataV1> GetShopTargets(LocationResidentsDataV1 locationData)
        {
            List<BuildingTargetDataV1> results = new List<BuildingTargetDataV1>();
            if (locationData == null || locationData.targets == null)
                return results;

            for (int i = 0; i < locationData.targets.Count; i++)
            {
                BuildingTargetDataV1 target = locationData.targets[i];
                if (target.isShop || target.isGuildHall || target.isTemple || target.isBank)
                    results.Add(target);
            }

            return results;
        }

        private List<BuildingTargetDataV1> GetTavernTargets(LocationResidentsDataV1 locationData)
        {
            List<BuildingTargetDataV1> results = new List<BuildingTargetDataV1>();
            if (locationData == null || locationData.targets == null)
                return results;

            for (int i = 0; i < locationData.targets.Count; i++)
            {
                if (locationData.targets[i].isTavern)
                    results.Add(locationData.targets[i]);
            }

            return results;
        }

        private float GetStableTargetNoise(int mapId, int residentId, int seedSalt, int targetBuildingKey)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)mapId) * 16777619u;
                hash = (hash ^ (uint)residentId) * 16777619u;
                hash = (hash ^ (uint)seedSalt) * 16777619u;
                hash = (hash ^ (uint)targetBuildingKey) * 16777619u;
                return (hash & 0xffff) / 65535f;
            }
        }

        private int GetTargetLoad(Dictionary<int, int> plannedTargetLoads, int buildingKey)
        {
            if (plannedTargetLoads == null)
                return 0;

            int load;
            if (plannedTargetLoads.TryGetValue(buildingKey, out load))
                return load;

            return 0;
        }

        private void IncrementTargetLoad(Dictionary<int, int> plannedTargetLoads, int buildingKey)
        {
            if (plannedTargetLoads == null)
                return;

            int load;
            plannedTargetLoads.TryGetValue(buildingKey, out load);
            plannedTargetLoads[buildingKey] = load + 1;
        }

        private int CompareScoredTargets(ScoredTargetCandidate left, ScoredTargetCandidate right)
        {
            int scoreComparison = left.Score.CompareTo(right.Score);
            if (scoreComparison != 0)
                return scoreComparison;

            return left.Target.buildingKey.CompareTo(right.Target.buildingKey);
        }

        private void SynchronizeCoupleStates(LocationResidentsDataV1 locationData, DaggerfallDateTime now)
        {
            if (locationData == null || locationData.residents == null)
                return;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.partnerResidentId <= 0 || resident.partnerResidentId < resident.residentId)
                    continue;

                ResidentDataV1 partner = FindResidentById(locationData, resident.partnerResidentId);
                if (partner == null)
                    continue;

                ResidentState residentState = (ResidentState)resident.currentState;
                ResidentState partnerState = (ResidentState)partner.currentState;

                if (now.Hour >= 17 && now.Hour < 22 &&
                    (residentState == ResidentState.Tavern || partnerState == ResidentState.Tavern))
                {
                    bool goTogether = WithDFSeed(locationData.mapId + resident.householdId + now.Hour * 71, delegate ()
                    {
                        return DFRandom.random_range(100) < 65;
                    });
                    if (goTogether)
                    {
                        resident.currentState = (int)ResidentState.Tavern;
                        partner.currentState = (int)ResidentState.Tavern;
                    }
                }
                else if (now.Hour >= 9 && now.Hour < 18)
                {
                    bool moveTogether = WithDFSeed(locationData.mapId + resident.householdId + now.Hour * 73, delegate ()
                    {
                        return DFRandom.random_range(100) < 35;
                    });
                    if (moveTogether)
                    {
                        if (residentState == ResidentState.Shopping || partnerState == ResidentState.Shopping)
                        {
                            resident.currentState = (int)ResidentState.Shopping;
                            partner.currentState = (int)ResidentState.Shopping;
                        }
                        else if (residentState == ResidentState.ExteriorWander || partnerState == ResidentState.ExteriorWander)
                        {
                            resident.currentState = (int)ResidentState.ExteriorWander;
                            partner.currentState = (int)ResidentState.ExteriorWander;
                        }
                    }
                }
            }
        }

        private void SynchronizeGuardPatrolStates(LocationResidentsDataV1 locationData, DaggerfallDateTime now)
        {
            if (locationData == null || locationData.residents == null)
                return;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if ((ResidentRole)resident.role != ResidentRole.Guard || resident.partnerResidentId <= 0 || resident.partnerResidentId < resident.residentId)
                    continue;

                ResidentDataV1 partner = FindResidentById(locationData, resident.partnerResidentId);
                if (partner == null || (ResidentRole)partner.role != ResidentRole.Guard)
                    continue;

                bool patrolTogether = WithDFSeed(locationData.mapId + resident.householdId + now.Hour * 151, delegate ()
                {
                    return DFRandom.random_range(100) < 55;
                });
                if (!patrolTogether)
                    continue;

                ResidentState residentState = (ResidentState)resident.currentState;
                ResidentState partnerState = (ResidentState)partner.currentState;
                if (residentState == ResidentState.Patrol || partnerState == ResidentState.Patrol)
                {
                    resident.currentState = (int)ResidentState.Patrol;
                    partner.currentState = (int)ResidentState.Patrol;
                }
            }
        }

        private void SynchronizeGuardPatrolTargets(LocationResidentsDataV1 locationData, DaggerfallDateTime now)
        {
            if (locationData == null || locationData.residents == null)
                return;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if ((ResidentRole)resident.role != ResidentRole.Guard || resident.partnerResidentId <= 0 || resident.partnerResidentId < resident.residentId)
                    continue;
                if ((ResidentState)resident.currentState != ResidentState.Patrol)
                    continue;

                ResidentDataV1 partner = FindResidentById(locationData, resident.partnerResidentId);
                if (partner == null || (ResidentRole)partner.role != ResidentRole.Guard || (ResidentState)partner.currentState != ResidentState.Patrol)
                    continue;

                int sharedTarget = resident.currentTargetBuildingKey;
                if (sharedTarget <= 0 || sharedTarget > RadiantNPCsSharedNavigationContext.PatrolTargetKeyBase)
                    sharedTarget = ChoosePatrolTarget(locationData, resident, now);

                resident.currentTargetBuildingKey = sharedTarget;
                partner.currentTargetBuildingKey = sharedTarget;
            }
        }

        private void SynchronizeCoupleTargets(LocationResidentsDataV1 locationData)
        {
            if (locationData == null || locationData.residents == null)
                return;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.partnerResidentId <= 0 || resident.partnerResidentId < resident.residentId)
                    continue;

                int partnerIndex = FindResidentIndexById(locationData, resident.partnerResidentId);
                if (partnerIndex < 0)
                    continue;

                ResidentDataV1 partner = locationData.residents[partnerIndex];
                if (partner == null || partner.isDead)
                    continue;

                ResidentState residentState = (ResidentState)resident.currentState;
                ResidentState partnerState = (ResidentState)partner.currentState;
                if (residentState != partnerState)
                    continue;
                if (residentState != ResidentState.Shopping &&
                    residentState != ResidentState.ExteriorWander &&
                    residentState != ResidentState.SocialVisit &&
                    residentState != ResidentState.Tavern)
                    continue;

                int sharedTarget = resident.currentTargetBuildingKey;
                if (sharedTarget <= 0)
                    sharedTarget = partner.currentTargetBuildingKey;
                if (sharedTarget <= 0)
                    continue;

                resident.currentTargetBuildingKey = sharedTarget;
                partner.currentTargetBuildingKey = sharedTarget;
                locationData.residents[i] = resident;
                locationData.residents[partnerIndex] = partner;
            }
        }

        private void BalanceHouseholdPresence(LocationResidentsDataV1 locationData, DaggerfallDateTime now)
        {
            if (locationData == null || locationData.households == null || locationData.residents == null)
                return;

            if (now.Hour < 8 || now.Hour >= 22)
                return;

            for (int i = 0; i < locationData.households.Count; i++)
            {
                HouseholdDataV1 household = locationData.households[i];
                List<ResidentDataV1> members = GetHouseholdResidents(locationData, household.householdId);
                if (members.Count == 0)
                    continue;

                int homeCount = 0;
                for (int m = 0; m < members.Count; m++)
                {
                    if ((ResidentState)members[m].currentState == ResidentState.AtHome)
                        homeCount++;
                }

                if (members.Count >= 3 && homeCount == 0)
                {
                    ResidentDataV1 selected = members[WithDFSeed(locationData.mapId + household.householdId + now.Hour * 97, delegate ()
                    {
                        return DFRandom.random_range(members.Count);
                    })];
                    selected.currentState = (int)ResidentState.AtHome;
                }
                else if (members.Count == 1 && homeCount > 0 && now.Hour >= 10 && now.Hour < 17)
                {
                    bool shouldLeave = WithDFSeed(locationData.mapId + household.householdId + now.Hour * 101, delegate ()
                    {
                        return DFRandom.random_range(100) < 55;
                    });
                    if (shouldLeave)
                    {
                        ResidentDataV1 onlyResident = members[0];
                        onlyResident.currentState = (int)(onlyResident.prefersShopping ? ResidentState.Shopping : ResidentState.ExteriorWander);
                    }
                }
            }
        }

        private int GetExteriorEligibleResidentCount(LocationResidentsDataV1 locationData)
        {
            if (locationData == null || locationData.residents == null)
                return 0;

            int count = 0;
            for (int i = 0; i < locationData.residents.Count; i++)
            {
                if (locationData.residents[i].isDead)
                    continue;

                ResidentState state = (ResidentState)locationData.residents[i].currentState;
                if (IsExteriorState(state))
                    count++;
            }

            return Mathf.Max(1, count);
        }

        private int GetDesiredActiveResidentCount(LocationResidentsDataV1 locationData)
        {
            if (locationData == null || locationData.residents == null)
                return 0;

            Vector3 playerLocalPosition = GetPlayerLocalPosition();
            int nearbyEligible = 0;
            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (resident.isDead)
                    continue;
                if (!IsExteriorState((ResidentState)resident.currentState))
                    continue;

                Vector3 anchor = GetResidentExteriorLocalPosition(locationData, resident);
                Vector2 anchor2 = new Vector2(anchor.x, anchor.z);
                Vector2 player = new Vector2(playerLocalPosition.x, playerLocalPosition.z);
                if (Vector2.Distance(anchor2, player) <= ActiveHouseholdRadius)
                    nearbyEligible++;
            }

            if (nearbyEligible <= 0)
                return 0;

            return Mathf.Clamp(nearbyEligible, 0, 64);
        }

        private Vector3 GetResidentAnchorLocalPosition(LocationResidentsDataV1 locationData, ResidentDataV1 resident)
        {
            if ((ResidentState)resident.currentState == ResidentState.Patrol)
            {
                Vector3 patrolLocalPosition;
                if (TryGetPatrolAnchorLocalPosition(locationData, resident.currentTargetBuildingKey, out patrolLocalPosition))
                    return patrolLocalPosition;
            }

            BuildingTargetDataV1 target = FindTarget(locationData, resident.currentTargetBuildingKey);
            if (target != null)
                return new Vector3(target.localPositionX, 0, target.localPositionZ);

            return GetHouseholdHomeLocalPosition(locationData, resident);
        }

        private Vector3 GetHouseholdHomeLocalPosition(LocationResidentsDataV1 locationData, ResidentDataV1 resident)
        {
            HouseholdDataV1 household = FindHousehold(locationData, resident.householdId);
            if (household != null)
                return new Vector3(household.homeLocalPositionX, 0, household.homeLocalPositionZ);

            return Vector3.zero;
        }

        private BuildingTargetDataV1 FindTarget(LocationResidentsDataV1 locationData, int buildingKey)
        {
            if (locationData == null || locationData.targets == null)
                return null;

            for (int i = 0; i < locationData.targets.Count; i++)
            {
                if (locationData.targets[i].buildingKey == buildingKey)
                    return locationData.targets[i];
            }

            return null;
        }

        private List<BuildingTargetDataV1> GetTargets(LocationResidentsDataV1 locationData, bool onlyShops, bool onlyTaverns, bool onlyResidences)
        {
            List<BuildingTargetDataV1> results = new List<BuildingTargetDataV1>();
            if (locationData == null || locationData.targets == null)
                return results;

            for (int i = 0; i < locationData.targets.Count; i++)
            {
                BuildingTargetDataV1 target = locationData.targets[i];
                if (onlyShops && !target.isShop)
                    continue;
                if (onlyTaverns && !target.isTavern)
                    continue;
                if (onlyResidences && !target.isResidence)
                    continue;
                results.Add(target);
            }

            return results;
        }

        private List<BuildingTargetDataV1> GetTargetsForState(LocationResidentsDataV1 locationData, ResidentState state)
        {
            DaggerfallDateTime now = DaggerfallUnity.Instance.WorldTime.Now;
            switch (state)
            {
                case ResidentState.AtHome:
                    return GetTargets(locationData, onlyShops: false, onlyTaverns: false, onlyResidences: true);
                case ResidentState.Shopping:
                    return GetShopTargets(locationData);
                case ResidentState.Tavern:
                    return GetTavernTargets(locationData);
                case ResidentState.SocialVisit:
                    return GetTargets(locationData, onlyShops: false, onlyTaverns: false, onlyResidences: true);
                case ResidentState.ExteriorWander:
                default:
                    return GetWanderTargets(locationData, now);
            }
        }

        private bool IsExteriorState(ResidentState state)
        {
            return state == ResidentState.ExteriorWander ||
                   state == ResidentState.Shopping ||
                   state == ResidentState.SocialVisit ||
                   state == ResidentState.Tavern ||
                   state == ResidentState.Patrol;
        }

        private void LogResidentAssignment(LocationResidentsDataV1 locationData, ResidentDataV1 resident)
        {
            int count = 0;
            assignmentLogsByMapId.TryGetValue(locationData.mapId, out count);
            if (count < 12)
            {
                LogInfo(
                    "RadiantNPCs: assigned resident '{0}' (ResidentID={1}, HouseholdID={2}, Home={3}, Target={4}, Role={5}, State={6}) to exterior mobile NPC in MapID={7}.",
                    resident.fullName,
                    resident.residentId,
                    resident.householdId,
                    resident.homeBuildingKey,
                    resident.currentTargetBuildingKey,
                    (ResidentRole)resident.role,
                    (ResidentState)resident.currentState,
                    locationData.mapId);
            }
            else if (count == 12)
            {
                LogInfo("RadiantNPCs: suppressing further per-NPC assignment logs for MapID={0}.", locationData.mapId);
            }

            assignmentLogsByMapId[locationData.mapId] = count + 1;
        }

        private int ComputeFaceRecordId(Races race, Genders gender, int outfitVariant, int faceVariant)
        {
            int[] recordIndices = GetFaceRecordIndices(race, gender);
            int safeOutfitVariant = Mathf.Clamp(outfitVariant, 0, recordIndices.Length - 1);
            return recordIndices[safeOutfitVariant] + faceVariant;
        }

        private int[] GetFaceRecordIndices(Races race, Genders gender)
        {
            switch (race)
            {
                case Races.Redguard:
                    return gender == Genders.Male ? MaleRedguardFaceRecordIndex : FemaleRedguardFaceRecordIndex;
                case Races.Nord:
                    return gender == Genders.Male ? MaleNordFaceRecordIndex : FemaleNordFaceRecordIndex;
                case Races.Breton:
                default:
                    return gender == Genders.Male ? MaleBretonFaceRecordIndex : FemaleBretonFaceRecordIndex;
            }
        }

        private PlayerGPS GetPlayerGPS()
        {
            if (GameManager.Instance == null)
                return null;

            return GameManager.Instance.PlayerGPS;
        }

        private Vector3 GetPlayerLocalPosition()
        {
            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (currentLocationObject == null)
                return Vector3.zero;

            return GameManager.Instance.PlayerObject.transform.position - currentLocationObject.transform.position;
        }

        private Vector3 GetLocationOrigin()
        {
            DaggerfallLocation currentLocationObject = GetCurrentLocationObject();
            if (currentLocationObject == null)
                return Vector3.zero;

            return currentLocationObject.transform.position;
        }

        private DaggerfallLocation GetCurrentLocationObject()
        {
            if (GameManager.Instance == null || GameManager.Instance.StreamingWorld == null)
                return null;

            return GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;
        }

        private Races ResolveSupportedMobileRace(PlayerGPS playerGPS)
        {
            if (playerGPS == null)
                return Races.Breton;

            switch (playerGPS.ClimateSettings.People)
            {
                case FactionFile.FactionRaces.Redguard:
                    return Races.Redguard;
                case FactionFile.FactionRaces.Nord:
                    return Races.Nord;
                default:
                    return Races.Breton;
            }
        }

        private T WithDFSeed<T>(int seed, Func<T> action)
        {
            DFRandom.SaveSeed();
            DFRandom.srand(seed);
            T result = action();
            DFRandom.RestoreSeed();
            return result;
        }

        private long GetResidentUsageKey(int mapId, int residentId)
        {
            return ((long)(uint)mapId << 32) | (uint)residentId;
        }

        private long GetTargetUsageKey(int mapId, int targetKey)
        {
            return ((long)(uint)mapId << 32) | (uint)targetKey;
        }

        private float GetResidentLastAssignedAt(int mapId, int residentId)
        {
            float lastAssigned;
            if (residentLastAssignedAt.TryGetValue(GetResidentUsageKey(mapId, residentId), out lastAssigned))
                return lastAssigned;

            return float.MinValue;
        }

        private int GetTargetFailureCount(int mapId, int targetKey)
        {
            int count;
            if (targetFailureCounts.TryGetValue(GetTargetUsageKey(mapId, targetKey), out count))
                return count;

            return 0;
        }

        private void RecordTargetFailure(int mapId, int targetKey)
        {
            long key = GetTargetUsageKey(mapId, targetKey);
            int count;
            targetFailureCounts.TryGetValue(key, out count);
            targetFailureCounts[key] = count + 1;
        }

        private void RecordTargetSuccess(int mapId, int targetKey)
        {
            long key = GetTargetUsageKey(mapId, targetKey);
            int count;
            if (!targetFailureCounts.TryGetValue(key, out count))
                return;

            if (count <= 1)
                targetFailureCounts.Remove(key);
            else
                targetFailureCounts[key] = count - 1;
        }

        private bool IsTargetBlacklisted(int mapId, int targetKey)
        {
            return GetTargetFailureCount(mapId, targetKey) >= TargetBlacklistThreshold;
        }

        private static void LogInfo(string format, params object[] args)
        {
            string message = string.Format(format, args);
            Debug.Log(message);
            try
            {
                File.AppendAllText(GetLogFilePath(), string.Format("[{0}] {1}{2}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), message, Environment.NewLine));
            }
            catch
            {
            }
        }

        private static string GetLogFilePath()
        {
            return Path.Combine(Application.dataPath, "Game", "Mods", "RadiantNPCs", LogFileName);
        }

    }


}
