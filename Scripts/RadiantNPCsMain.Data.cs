using System.Collections.Generic;
using FullSerializer;

namespace RadiantNPCsMod
{
    public partial class RadiantNPCsMain
    {
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
            public bool isDead;
            public bool hasKnownExteriorPosition;
            public float exteriorLocalPositionX;
            public float exteriorLocalPositionZ;
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
            public bool isGuildHall;
            public bool isTemple;
            public bool isBank;
            public bool isPalace;
            public bool isPublicVenue;
            public bool hasDoorAnchor;
        }

        private struct ResidentAssignment
        {
            public int mapId;
            public int residentId;
        }

        private struct ScoredTargetCandidate
        {
            public BuildingTargetDataV1 Target;
            public float Score;

            public ScoredTargetCandidate(BuildingTargetDataV1 target, float score)
            {
                Target = target;
                Score = score;
            }
        }

        private struct InteriorSpawnCandidate
        {
            public ResidentDataV1 Resident;
            public bool IsSheltered;
        }

        private enum ResidentRole
        {
            Civilian = 0,
            Guard = 1,
        }

        public enum ResidentState
        {
            AtHome = 0,
            ExteriorWander = 1,
            Shopping = 2,
            SocialVisit = 3,
            Tavern = 4,
            Patrol = 5,
            Dead = 6,
        }
    }
}
