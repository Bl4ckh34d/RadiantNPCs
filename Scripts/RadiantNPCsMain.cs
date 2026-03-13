using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using FullSerializer;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Utility;

namespace RadiantNPCsMod
{
    public class RadiantNPCsMain : MonoBehaviour, IHasModSaveData
    {
        private const string SaveVersion = "v1";
        private const int MaxFaceVariant = 24;
        private const float ActiveHouseholdRadius = RMBLayout.RMBSide * 2.5f;
        private const string LogFileName = "RadiantNPCs.log.txt";
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
        private Dictionary<int, int> spawnCursorByMapId = new Dictionary<int, int>();
        private Dictionary<int, int> assignmentLogsByMapId = new Dictionary<int, int>();
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
            PlayerEnterExit.OnTransitionExterior += PlayerEnterExit_OnTransitionExterior;
            SaveLoadManager.OnStartLoad += SaveLoadManager_OnStartLoad;
            SaveLoadManager.OnLoad += SaveLoadManager_OnLoad;
            PopulationManager.OnMobileNPCDisable += PopulationManager_OnMobileNPCDisable;
            WorldTime.OnNewHour += WorldTime_OnNewHour;

            previousMobileNpcGenerator = PopulationManager.MobileNPCGenerator;
            PopulationManager.MobileNPCGenerator = HandleMobileNpcGeneration;
        }

        private void UnhookEvents()
        {
            PlayerGPS.OnEnterLocationRect -= PlayerGPS_OnEnterLocationRect;
            PlayerEnterExit.OnTransitionExterior -= PlayerEnterExit_OnTransitionExterior;
            SaveLoadManager.OnStartLoad -= SaveLoadManager_OnStartLoad;
            SaveLoadManager.OnLoad -= SaveLoadManager_OnLoad;
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
            PrepareCurrentLocation(force: true);
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
                activeAssignments.Remove(instanceId);
        }

        private void WorldTime_OnNewHour()
        {
            PrepareCurrentLocation(force: true);
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

            RefreshResidentStates(locationData);
            lastPreparedMapId = currentMapId;
            ApplyPopulationLimit(GetDesiredActiveResidentCount(locationData));
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

            PopulateLocationTargets(locationData: out LocationResidentsDataV1 locationData, buildingDirectory, playerGPS);
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
                int bedCapacity = GetBedCapacity(buildingDirectory, residence, out usedFallbackCapacity);
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
                household.homeLocalPositionX = GetBuildingLocalPosition(layoutX: GetLayoutX(residence.buildingKey), layoutY: GetLayoutY(residence.buildingKey), residence.Position).x;
                household.homeLocalPositionZ = GetBuildingLocalPosition(layoutX: GetLayoutX(residence.buildingKey), layoutY: GetLayoutY(residence.buildingKey), residence.Position).z;
                household.surname = GenerateSurname(nameBank, residence.buildingKey);
                locationData.households.Add(household);
                totalBedCapacity += bedCapacity;
                int residentListStartIndex = locationData.residents.Count;

                for (int occupantIndex = 0; occupantIndex < occupantCount; occupantIndex++)
                {
                    Genders gender = GetHouseholdGender(occupantCount, occupantIndex, residence.buildingKey);
                    string firstName = GenerateFirstName(nameBank, gender, residence.buildingKey + occupantIndex * 19);
                    string fullName = ComposeFullName(firstName, household.surname);
                    int outfitVariant = GetOutfitVariant(residence.buildingKey, occupantIndex);
                    int faceVariant = GetFaceVariant(residence.buildingKey, occupantIndex);
                    int faceRecordId = ComputeFaceRecordId(mobileRace, gender, outfitVariant, faceVariant);

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
                    resident.role = (int)GetResidentRole(residence.buildingKey, occupantIndex);
                    resident.prefersNightlife = GetPrefersNightlife(residence.buildingKey, occupantIndex);
                    resident.prefersShopping = GetPrefersShopping(residence.buildingKey, occupantIndex);
                    resident.prefersSocialVisits = GetPrefersSocialVisits(residence.buildingKey, occupantIndex);
                    resident.currentState = (int)ResidentState.AtHome;
                    resident.currentTargetBuildingKey = residence.buildingKey;
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

            return locationData;
        }

        private void PopulateLocationTargets(out LocationResidentsDataV1 locationData, BuildingDirectory buildingDirectory, PlayerGPS playerGPS)
        {
            locationData = new LocationResidentsDataV1();
            locationData.mapId = playerGPS.CurrentMapID;
            locationData.locationId = (int)playerGPS.CurrentLocation.Exterior.ExteriorData.LocationId;
            locationData.locationIndex = playerGPS.CurrentLocation.LocationIndex;
            locationData.locationName = playerGPS.CurrentLocation.Name;
            locationData.regionName = playerGPS.CurrentRegionName;

            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House1);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House2);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House3);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.House4);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Tavern);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Alchemist);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Armorer);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.Bookseller);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.ClothingStore);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.FurnitureStore);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.GemStore);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.GeneralStore);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.PawnShop);
            AddTargetsForType(locationData, buildingDirectory, DFLocation.BuildingTypes.WeaponSmith);
        }

        private void AddTargetsForType(LocationResidentsDataV1 locationData, BuildingDirectory buildingDirectory, DFLocation.BuildingTypes buildingType)
        {
            List<BuildingSummary> buildings = buildingDirectory.GetBuildingsOfType(buildingType);
            for (int i = 0; i < buildings.Count; i++)
            {
                BuildingSummary building = buildings[i];
                int layoutX = GetLayoutX(building.buildingKey);
                int layoutY = GetLayoutY(building.buildingKey);
                Vector3 localPosition = GetBuildingLocalPosition(layoutX, layoutY, building.Position);

                BuildingTargetDataV1 target = new BuildingTargetDataV1();
                target.buildingKey = building.buildingKey;
                target.buildingType = (int)building.BuildingType;
                target.localPositionX = localPosition.x;
                target.localPositionZ = localPosition.z;
                target.isResidence = RMBLayout.IsResidence(building.BuildingType);
                target.isShop = RMBLayout.IsShop(building.BuildingType);
                target.isTavern = RMBLayout.IsTavern(building.BuildingType);
                locationData.targets.Add(target);
            }
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
                FallbackToVanilla(poolItem);
                return;
            }

            ApplyResidentToMobileNpc(poolItem.npc, resident);
            PositionResidentForCurrentState(locationData, poolItem.npc, resident);

            ResidentAssignment assignment = new ResidentAssignment();
            assignment.mapId = locationData.mapId;
            assignment.residentId = resident.residentId;
            activeAssignments[poolItem.npc.GetInstanceID()] = assignment;
            LogResidentAssignment(locationData, resident);
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
            if (state == ResidentState.Patrol)
                return;

            Vector3 localPosition = GetResidentAnchorLocalPosition(locationData, resident);
            localPosition += GetSpawnOffset(resident, state);
            npc.Motor.transform.position = currentLocationObject.transform.position + localPosition;
            GameObjectHelper.AlignBillboardToGround(npc.Motor.gameObject, new Vector2(0, 2f));
        }

        private Vector3 GetSpawnOffset(ResidentDataV1 resident, ResidentState state)
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

            return WithDFSeed(resident.residentId * 83 + (int)state * 17, delegate ()
            {
                float x = DFRandom.random_range_inclusive(-radius, radius) * MeshReader.GlobalScale * 8f;
                float z = DFRandom.random_range_inclusive(-radius, radius) * MeshReader.GlobalScale * 8f;
                return new Vector3(x, 0, z);
            });
        }

        private ResidentDataV1 GetNextAvailableResident(LocationResidentsDataV1 locationData)
        {
            if (locationData.residents == null || locationData.residents.Count == 0)
                return null;

            Vector3 playerLocalPosition = GetPlayerLocalPosition();
            int bestIndex = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (!IsExteriorState((ResidentState)resident.currentState))
                    continue;
                if (IsResidentAssigned(locationData.mapId, resident.residentId))
                    continue;

                HouseholdDataV1 household = FindHousehold(locationData, resident.householdId);
                if (household == null)
                    continue;

                Vector2 home = new Vector2(household.homeLocalPositionX, household.homeLocalPositionZ);
                Vector2 player = new Vector2(playerLocalPosition.x, playerLocalPosition.z);
                float distance = Vector2.Distance(home, player);
                if (distance <= ActiveHouseholdRadius && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                return locationData.residents[bestIndex];

            int startIndex = 0;
            if (spawnCursorByMapId.ContainsKey(locationData.mapId))
                startIndex = spawnCursorByMapId[locationData.mapId];
            for (int offset = 0; offset < locationData.residents.Count; offset++)
            {
                int index = (startIndex + offset) % locationData.residents.Count;
                ResidentDataV1 resident = locationData.residents[index];
                if (IsExteriorState((ResidentState)resident.currentState) &&
                    !IsResidentAssigned(locationData.mapId, resident.residentId))
                {
                    spawnCursorByMapId[locationData.mapId] = (index + 1) % locationData.residents.Count;
                    return resident;
                }
            }

            return null;
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

        private void ClearRuntimeState()
        {
            activeAssignments.Clear();
            spawnCursorByMapId.Clear();
            assignmentLogsByMapId.Clear();
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

        private int GetBedCapacity(BuildingDirectory buildingDirectory, BuildingSummary residence, out bool usedFallbackCapacity)
        {
            int markerCapacity = CountInteriorRestMarkerCapacity(buildingDirectory, residence.buildingKey);
            int bedCapacity = CountInteriorBedCapacity(buildingDirectory, residence.buildingKey);
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

        private int CountInteriorBedCapacity(BuildingDirectory buildingDirectory, int buildingKey)
        {
            if (buildingDirectory == null)
                return 0;

            DFLocation location = buildingDirectory.LocationData;
            if (!location.Loaded)
                return 0;

            int layoutX;
            int layoutY;
            int recordIndex;
            BuildingDirectory.ReverseBuildingKey(buildingKey, out layoutX, out layoutY, out recordIndex);

            int width = location.Exterior.ExteriorData.Width;
            int height = location.Exterior.ExteriorData.Height;
            if (layoutX < 0 || layoutY < 0 || layoutX >= width || layoutY >= height)
                return 0;

            DFBlock[] blocks = RMBLayout.GetLocationBuildingData(location);
            int blockIndex = layoutY * width + layoutX;
            if (blocks == null || blockIndex < 0 || blockIndex >= blocks.Length)
                return 0;

            DFBlock block = blocks[blockIndex];
            if (block.Type != DFBlock.BlockTypes.Rmb)
                return 0;

            if (recordIndex < 0 || recordIndex >= block.RmbBlock.SubRecords.Length)
                return 0;

            DFBlock.RmbSubRecord subRecord = block.RmbBlock.SubRecords[recordIndex];
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

        private int CountInteriorRestMarkerCapacity(BuildingDirectory buildingDirectory, int buildingKey)
        {
            if (buildingDirectory == null)
                return 0;

            DFLocation location = buildingDirectory.LocationData;
            if (!location.Loaded)
                return 0;

            int layoutX;
            int layoutY;
            int recordIndex;
            BuildingDirectory.ReverseBuildingKey(buildingKey, out layoutX, out layoutY, out recordIndex);

            int width = location.Exterior.ExteriorData.Width;
            int height = location.Exterior.ExteriorData.Height;
            if (layoutX < 0 || layoutY < 0 || layoutX >= width || layoutY >= height)
                return 0;

            DFBlock[] blocks = RMBLayout.GetLocationBuildingData(location);
            int blockIndex = layoutY * width + layoutX;
            if (blocks == null || blockIndex < 0 || blockIndex >= blocks.Length)
                return 0;

            DFBlock block = blocks[blockIndex];
            if (block.Type != DFBlock.BlockTypes.Rmb)
                return 0;

            if (recordIndex < 0 || recordIndex >= block.RmbBlock.SubRecords.Length)
                return 0;

            DFBlock.RmbSubRecord subRecord = block.RmbBlock.SubRecords[recordIndex];
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
                return;

            int homeCount = 0;
            int wanderCount = 0;
            int shopCount = 0;
            int visitCount = 0;
            int tavernCount = 0;
            int patrolCount = 0;

            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                ResidentState state = ComputeResidentState(locationData, resident, now);
                resident.currentState = (int)state;
                resident.currentTargetBuildingKey = GetTargetBuildingKeyForState(locationData, resident, state, now);
                resident.lastScheduleSignature = scheduleSignature;
                locationData.residents[i] = resident;
            }

            BalanceHouseholdPresence(locationData, now);
            SynchronizeCoupleStates(locationData, now);

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

        private int GetTargetBuildingKeyForState(LocationResidentsDataV1 locationData, ResidentDataV1 resident, ResidentState state, DaggerfallDateTime now)
        {
            switch (state)
            {
                case ResidentState.Shopping:
                    return ChooseShopTarget(locationData, resident, now);
                case ResidentState.SocialVisit:
                    return ChooseVisitTarget(locationData, resident, now);
                case ResidentState.Tavern:
                    return ChooseTavernTarget(locationData, resident, now);
                default:
                    return resident.homeBuildingKey;
            }
        }

        private int ChooseShopTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now)
        {
            List<BuildingTargetDataV1> shops = GetTargets(locationData, onlyShops: true, onlyTaverns: false, onlyResidences: false);
            if (shops.Count == 0)
                return resident.homeBuildingKey;

            int index = WithDFSeed(locationData.mapId + resident.residentId + now.Hour * 53, delegate ()
            {
                return DFRandom.random_range(shops.Count);
            });
            return shops[index].buildingKey;
        }

        private int ChooseVisitTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now)
        {
            List<BuildingTargetDataV1> homes = GetTargets(locationData, onlyShops: false, onlyTaverns: false, onlyResidences: true);
            if (homes.Count <= 1)
                return resident.homeBuildingKey;

            int startIndex = WithDFSeed(locationData.mapId + resident.residentId + now.Hour * 57, delegate ()
            {
                return DFRandom.random_range(homes.Count);
            });

            for (int offset = 0; offset < homes.Count; offset++)
            {
                BuildingTargetDataV1 target = homes[(startIndex + offset) % homes.Count];
                if (target.buildingKey != resident.homeBuildingKey)
                    return target.buildingKey;
            }

            return resident.homeBuildingKey;
        }

        private int ChooseTavernTarget(LocationResidentsDataV1 locationData, ResidentDataV1 resident, DaggerfallDateTime now)
        {
            List<BuildingTargetDataV1> taverns = GetTargets(locationData, onlyShops: false, onlyTaverns: true, onlyResidences: false);
            if (taverns.Count == 0)
                return resident.homeBuildingKey;

            int index = WithDFSeed(locationData.mapId + resident.residentId + now.Hour * 61, delegate ()
            {
                return DFRandom.random_range(taverns.Count);
            });
            return taverns[index].buildingKey;
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
                ResidentState state = (ResidentState)locationData.residents[i].currentState;
                if (IsExteriorState(state))
                    count++;
            }

            return Mathf.Max(1, count);
        }

        private int GetDesiredActiveResidentCount(LocationResidentsDataV1 locationData)
        {
            if (locationData == null || locationData.residents == null)
                return 1;

            Vector3 playerLocalPosition = GetPlayerLocalPosition();
            int nearbyEligible = 0;
            for (int i = 0; i < locationData.residents.Count; i++)
            {
                ResidentDataV1 resident = locationData.residents[i];
                if (!IsExteriorState((ResidentState)resident.currentState))
                    continue;

                Vector3 anchor = GetResidentAnchorLocalPosition(locationData, resident);
                Vector2 anchor2 = new Vector2(anchor.x, anchor.z);
                Vector2 player = new Vector2(playerLocalPosition.x, playerLocalPosition.z);
                if (Vector2.Distance(anchor2, player) <= ActiveHouseholdRadius)
                    nearbyEligible++;
            }

            if (nearbyEligible <= 0)
                return 1;

            return Mathf.Clamp(nearbyEligible + 2, 1, 64);
        }

        private Vector3 GetResidentAnchorLocalPosition(LocationResidentsDataV1 locationData, ResidentDataV1 resident)
        {
            BuildingTargetDataV1 target = FindTarget(locationData, resident.currentTargetBuildingKey);
            if (target != null)
                return new Vector3(target.localPositionX, 0, target.localPositionZ);

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
                    "RadiantNPCs: assigned resident '{0}' (ResidentID={1}, HouseholdID={2}, Home={3}, Role={4}, State={5}) to exterior mobile NPC in MapID={6}.",
                    resident.fullName,
                    resident.residentId,
                    resident.householdId,
                    resident.homeBuildingKey,
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
            if (GameManager.Instance == null || GameManager.Instance.StreamingWorld == null)
                return Vector3.zero;

            DaggerfallLocation currentLocationObject = GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;
            if (currentLocationObject == null)
                return Vector3.zero;

            return GameManager.Instance.PlayerObject.transform.position - currentLocationObject.transform.position;
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

        [fsObject(SaveVersion)]
        public class RadiantNPCsSaveDataV1
        {
            public List<LocationResidentsDataV1> locations = new List<LocationResidentsDataV1>();
        }

        [fsObject(SaveVersion)]
        public class LocationResidentsDataV1
        {
            public int mapId;
            public int locationId;
            public int locationIndex;
            public string locationName;
            public string regionName;
            public int lastScheduleSignature = -1;
            public List<HouseholdDataV1> households = new List<HouseholdDataV1>();
            public List<ResidentDataV1> residents = new List<ResidentDataV1>();
            public List<BuildingTargetDataV1> targets = new List<BuildingTargetDataV1>();
        }

        [fsObject(SaveVersion)]
        public class HouseholdDataV1
        {
            public int householdId;
            public int buildingKey;
            public int buildingType;
            public int bedCapacity;
            public float homeLocalPositionX;
            public float homeLocalPositionZ;
            public string surname;
        }

        [fsObject(SaveVersion)]
        public class ResidentDataV1
        {
            public int residentId;
            public int householdId;
            public int householdMemberIndex;
            public int homeBuildingKey;
            public int currentTargetBuildingKey;
            public int partnerResidentId;
            public int sharedBedGroupId;
            public string firstName;
            public string surname;
            public string fullName;
            public int gender;
            public int race;
            public int outfitVariant;
            public int faceVariant;
            public int faceRecordId;
            public int disposition;
            public bool canVisitOtherHouses;
            public bool canVisitShops;
            public int role;
            public bool prefersNightlife;
            public bool prefersShopping;
            public bool prefersSocialVisits;
            public int currentState;
            public int lastScheduleSignature;
        }

        [fsObject(SaveVersion)]
        public class BuildingTargetDataV1
        {
            public int buildingKey;
            public int buildingType;
            public float localPositionX;
            public float localPositionZ;
            public bool isResidence;
            public bool isShop;
            public bool isTavern;
        }

        private struct ResidentAssignment
        {
            public int mapId;
            public int residentId;
        }

        private enum ResidentRole
        {
            Civilian = 0,
            Guard = 1,
        }

        private enum ResidentState
        {
            AtHome = 0,
            ExteriorWander = 1,
            Shopping = 2,
            SocialVisit = 3,
            Tavern = 4,
            Patrol = 5,
        }
    }
}
