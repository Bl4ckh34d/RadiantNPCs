using System;
using System.Collections.Generic;
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

namespace RadiantNPCsMod
{
    public class RadiantNPCsMain : MonoBehaviour, IHasModSaveData
    {
        private const string SaveVersion = "v1";
        private const int MaxFaceVariant = 24;

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
            Debug.LogFormat("RadiantNPCs: initialized mod '{0}' v{1}.", mod.Title, mod.ModInfo.ModVersion);
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
            Debug.LogFormat("RadiantNPCs: restored save data for {0} locations.", saveData.locations.Count);
        }

        private void HookEvents()
        {
            PlayerGPS.OnEnterLocationRect += PlayerGPS_OnEnterLocationRect;
            PlayerEnterExit.OnTransitionExterior += PlayerEnterExit_OnTransitionExterior;
            SaveLoadManager.OnStartLoad += SaveLoadManager_OnStartLoad;
            SaveLoadManager.OnLoad += SaveLoadManager_OnLoad;
            PopulationManager.OnMobileNPCDisable += PopulationManager_OnMobileNPCDisable;

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

            lastPreparedMapId = currentMapId;
            ApplyPopulationLimit(locationData.residents.Count);
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
            Debug.LogFormat(
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

            List<BuildingSummary> residences = GetResidentialBuildings(buildingDirectory);
            residences.Sort(CompareBuildingsByKey);

            LocationResidentsDataV1 locationData = new LocationResidentsDataV1();
            locationData.mapId = playerGPS.CurrentMapID;
            locationData.locationId = (int)playerGPS.CurrentLocation.Exterior.ExteriorData.LocationId;
            locationData.locationIndex = playerGPS.CurrentLocation.LocationIndex;
            locationData.locationName = playerGPS.CurrentLocation.Name;
            locationData.regionName = playerGPS.CurrentRegionName;

            NameHelper.BankTypes nameBank = playerGPS.GetNameBankOfCurrentRegion();
            Races mobileRace = ResolveSupportedMobileRace(playerGPS);
            int householdId = 1;
            int residentId = 1;
            int house1Count = 0;
            int house2Count = 0;
            int house3Count = 0;
            int house4Count = 0;

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

                int occupantCount = GetOccupantCount(residence);
                if (occupantCount <= 0)
                    continue;

                HouseholdDataV1 household = new HouseholdDataV1();
                household.householdId = householdId++;
                household.buildingKey = residence.buildingKey;
                household.buildingType = (int)residence.BuildingType;
                household.surname = GenerateSurname(nameBank, residence.buildingKey);
                locationData.households.Add(household);

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
                    locationData.residents.Add(resident);
                }
            }

            float averageResidentsPerHousehold = locationData.households.Count > 0
                ? (float)locationData.residents.Count / locationData.households.Count
                : 0f;

            Debug.LogFormat(
                "RadiantNPCs: household breakdown for {0}/{1}: House1={2}, House2={3}, House3={4}, House4={5}, avgResidentsPerHousehold={6:F2}.",
                locationData.regionName,
                locationData.locationName,
                house1Count,
                house2Count,
                house3Count,
                house4Count,
                averageResidentsPerHousehold);

            return locationData;
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
            Debug.LogFormat(
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

            ResidentAssignment assignment = new ResidentAssignment();
            assignment.mapId = locationData.mapId;
            assignment.residentId = resident.residentId;
            activeAssignments[poolItem.npc.GetInstanceID()] = assignment;
            Debug.LogFormat(
                "RadiantNPCs: assigned resident '{0}' (ResidentID={1}, HouseholdID={2}, Home={3}) to exterior mobile NPC in MapID={4}.",
                resident.fullName,
                resident.residentId,
                resident.householdId,
                resident.homeBuildingKey,
                locationData.mapId);
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

            npc.raceToBeSet = race;
            npc.genderToBeSet = gender;
            npc.outfitVariantToBeSet = resident.outfitVariant;
            npc.ApplyPersonSettingsViaInspector();

            npc.NameNPC = resident.fullName;
            npc.PersonFaceRecordId = resident.faceRecordId;
            npc.PickpocketByPlayerAttempted = false;
            npc.IsGuard = false;

            if (npc.Asset != null)
                npc.Asset.SetPerson(race, gender, resident.outfitVariant, false, resident.faceVariant, resident.faceRecordId);
        }

        private ResidentDataV1 GetNextAvailableResident(LocationResidentsDataV1 locationData)
        {
            if (locationData.residents == null || locationData.residents.Count == 0)
                return null;

            int startIndex = 0;
            if (spawnCursorByMapId.ContainsKey(locationData.mapId))
                startIndex = spawnCursorByMapId[locationData.mapId];

            for (int offset = 0; offset < locationData.residents.Count; offset++)
            {
                int index = (startIndex + offset) % locationData.residents.Count;
                ResidentDataV1 resident = locationData.residents[index];
                if (!IsResidentAssigned(locationData.mapId, resident.residentId))
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

        private int GetOccupantCount(BuildingSummary residence)
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
            public List<HouseholdDataV1> households = new List<HouseholdDataV1>();
            public List<ResidentDataV1> residents = new List<ResidentDataV1>();
        }

        [fsObject(SaveVersion)]
        public class HouseholdDataV1
        {
            public int householdId;
            public int buildingKey;
            public int buildingType;
            public string surname;
        }

        [fsObject(SaveVersion)]
        public class ResidentDataV1
        {
            public int residentId;
            public int householdId;
            public int homeBuildingKey;
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
        }

        private struct ResidentAssignment
        {
            public int mapId;
            public int residentId;
        }
    }
}
