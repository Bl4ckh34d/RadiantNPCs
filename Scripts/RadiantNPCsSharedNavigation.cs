using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility;

namespace RadiantNPCsMod
{
    internal sealed class RadiantNPCsSharedNavigationContext
    {
        private const int BlockedCost = -1;
        private const int BuildingFieldVariantCount = 4;
        private const int BuildingFieldSearchRadius = 5;
        private const int BuildingFieldDistinctSpacing = 3;
        private const int PerimeterSampleCount = 8;
        private const int PerimeterSnapRadius = 18;
        private const int GoalSeedRadius = 2;
        private const int GoalSnapRadius = 10;

        public const int PatrolTargetKeyBase = -1000000;

        private readonly int mapId;
        private readonly int locationInstanceId;
        private readonly int navigationInstanceId;
        private readonly DaggerfallLocation locationObject;
        private readonly CityNavigation cityNavigation;
        private readonly Vector3 locationOrigin;
        private readonly int[] tileCosts;
        private readonly int[] componentIds;
        private readonly int primaryComponentId;
        private readonly RadiantNPCsFlowFieldGenerator generator;
        private readonly Dictionary<string, RadiantNPCsFlowField> fieldCache = new Dictionary<string, RadiantNPCsFlowField>();
        private readonly Dictionary<string, RadiantNPCsFlowField> cpuFieldCache = new Dictionary<string, RadiantNPCsFlowField>();
        private readonly List<PatrolAnchor> patrolAnchors = new List<PatrolAnchor>();

        public RadiantNPCsSharedNavigationContext(int mapId, DaggerfallLocation locationObject, CityNavigation cityNavigation, ComputeShader computeShader)
        {
            this.mapId = mapId;
            this.locationObject = locationObject;
            this.cityNavigation = cityNavigation;
            this.locationOrigin = locationObject != null ? locationObject.transform.position : Vector3.zero;
            locationInstanceId = locationObject != null ? locationObject.GetInstanceID() : 0;
            navigationInstanceId = cityNavigation != null ? cityNavigation.GetInstanceID() : 0;
            tileCosts = BuildTileCosts(cityNavigation);
            componentIds = BuildComponentMap(cityNavigation, tileCosts, out primaryComponentId);
            generator = new RadiantNPCsFlowFieldGenerator(computeShader);
            BuildPatrolAnchors();
        }

        public int MapId
        {
            get { return mapId; }
        }

        public int PatrolAnchorCount
        {
            get { return patrolAnchors.Count; }
        }

        public bool UsesGpuFields
        {
            get { return generator.CanUseGpu; }
        }

        public bool Matches(int mapId, DaggerfallLocation locationObject, CityNavigation cityNavigation)
        {
            if (locationObject == null || cityNavigation == null)
                return false;

            return this.mapId == mapId &&
                   locationInstanceId == locationObject.GetInstanceID() &&
                   navigationInstanceId == cityNavigation.GetInstanceID();
        }

        public bool TryGetBuildingField(int targetKey, Vector3 targetLocalPosition, int residentId, out Vector3 resolvedAnchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu = false)
        {
            resolvedAnchorLocalPosition = targetLocalPosition;
            field = null;
            if (cityNavigation == null)
                return false;

            List<BuildingFieldCandidate> variants = CollectBuildingFieldCandidates(targetLocalPosition);
            if (variants.Count == 0)
                return false;

            int variantIndex = Mathf.Abs(residentId * 73 + targetKey * 17);
            variantIndex %= variants.Count;
            BuildingFieldCandidate selected = variants[variantIndex];
            resolvedAnchorLocalPosition = selected.LocalPosition;
            return TryGetOrCreateField(
                string.Format("building:{0}:{1}:{2}", targetKey, selected.NavPosition.X, selected.NavPosition.Y),
                selected.Seeds,
                preferCpu,
                out field);
        }

        public int GetInitialPatrolTargetKey(int residentId)
        {
            if (patrolAnchors.Count == 0)
                return 0;

            int index = Mathf.Abs(residentId * 73 + mapId * 17) % patrolAnchors.Count;
            return GetPatrolTargetKey(index);
        }

        public int GetPatrolDirection(int residentId)
        {
            return ((residentId + mapId) & 1) == 0 ? 1 : -1;
        }

        public bool TryGetPatrolField(int requestedTargetKey, int residentId, out int resolvedTargetKey, out Vector3 anchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu = false)
        {
            resolvedTargetKey = requestedTargetKey;
            anchorLocalPosition = Vector3.zero;
            field = null;

            if (patrolAnchors.Count == 0)
                return false;

            PatrolAnchor anchor;
            if (!TryGetPatrolAnchor(requestedTargetKey, out anchor))
            {
                resolvedTargetKey = GetInitialPatrolTargetKey(residentId);
                if (!TryGetPatrolAnchor(resolvedTargetKey, out anchor))
                    return false;
            }

            anchorLocalPosition = anchor.LocalPosition;
            return TryGetOrCreateField(string.Format("patrol:{0}", anchor.Index), anchor.Seeds, preferCpu, out field);
        }

        public bool TryGetNextPatrolField(int currentTargetKey, int patrolDirection, out int nextTargetKey, out Vector3 anchorLocalPosition, out RadiantNPCsFlowField field, bool preferCpu = false)
        {
            nextTargetKey = currentTargetKey;
            anchorLocalPosition = Vector3.zero;
            field = null;

            if (patrolAnchors.Count == 0)
                return false;

            PatrolAnchor currentAnchor;
            if (!TryGetPatrolAnchor(currentTargetKey, out currentAnchor))
                currentAnchor = patrolAnchors[0];

            int nextIndex = currentAnchor.Index + (patrolDirection >= 0 ? 1 : -1);
            if (nextIndex < 0)
                nextIndex = patrolAnchors.Count - 1;
            else if (nextIndex >= patrolAnchors.Count)
                nextIndex = 0;

            PatrolAnchor nextAnchor = patrolAnchors[nextIndex];
            nextTargetKey = GetPatrolTargetKey(nextAnchor.Index);
            anchorLocalPosition = nextAnchor.LocalPosition;
            return TryGetOrCreateField(string.Format("patrol:{0}", nextAnchor.Index), nextAnchor.Seeds, preferCpu, out field);
        }

        public bool TryGetPatrolAnchorLocalPosition(int targetKey, out Vector3 localPosition)
        {
            localPosition = Vector3.zero;
            PatrolAnchor anchor;
            if (!TryGetPatrolAnchor(targetKey, out anchor))
                return false;

            localPosition = anchor.LocalPosition;
            return true;
        }

        private bool TryGetOrCreateField(string cacheKey, List<DFPosition> seeds, bool preferCpu, out RadiantNPCsFlowField field)
        {
            Dictionary<string, RadiantNPCsFlowField> cache = preferCpu ? cpuFieldCache : fieldCache;
            if (cache.TryGetValue(cacheKey, out field))
                return field != null;

            field = generator.Generate(cacheKey, cityNavigation.NavGridWidth, cityNavigation.NavGridHeight, tileCosts, seeds, preferCpu);
            if (field == null)
                return false;

            cache[cacheKey] = field;
            return true;
        }

        private bool TryGetPatrolAnchor(int targetKey, out PatrolAnchor anchor)
        {
            anchor = default(PatrolAnchor);
            if (targetKey > PatrolTargetKeyBase)
                return false;

            int index = PatrolTargetKeyBase - targetKey;
            if (index < 0 || index >= patrolAnchors.Count)
                return false;

            anchor = patrolAnchors[index];
            return true;
        }

        private int GetPatrolTargetKey(int patrolAnchorIndex)
        {
            return PatrolTargetKeyBase - patrolAnchorIndex;
        }

        private void BuildPatrolAnchors()
        {
            patrolAnchors.Clear();
            if (cityNavigation == null || locationObject == null)
                return;

            List<PatrolAnchorCandidate> candidates = new List<PatrolAnchorCandidate>();
            AddGateCandidates(candidates);
            AddPerimeterCandidates(candidates);

            if (candidates.Count == 0)
            {
                DFPosition center = FindNearestWalkable(new DFPosition(cityNavigation.NavGridWidth / 2, cityNavigation.NavGridHeight / 2), GoalSnapRadius);
                List<DFPosition> seeds = CollectSeedCluster(center, GoalSeedRadius);
                if (seeds.Count > 0)
                    candidates.Add(CreateCandidate(seeds[0], seeds));
            }

            candidates.Sort(CompareCandidatesByAngle);
            for (int i = 0; i < candidates.Count; i++)
            {
                PatrolAnchorCandidate candidate = candidates[i];
                PatrolAnchor anchor = new PatrolAnchor();
                anchor.Index = patrolAnchors.Count;
                anchor.NavPosition = candidate.NavPosition;
                anchor.LocalPosition = candidate.LocalPosition;
                anchor.Seeds = candidate.Seeds;
                patrolAnchors.Add(anchor);
            }
        }

        private void AddGateCandidates(List<PatrolAnchorCandidate> candidates)
        {
            DaggerfallCityGate[] gates = locationObject.GetComponentsInChildren<DaggerfallCityGate>(true);
            for (int i = 0; i < gates.Length; i++)
            {
                Vector3 scenePosition = gates[i].transform.position;
                DFPosition rawNav = cityNavigation.WorldToNavGridPosition(cityNavigation.SceneToWorldPosition(scenePosition));
                List<DFPosition> seeds = CollectSeedCluster(rawNav, GoalSeedRadius, GoalSnapRadius + 4);
                if (seeds.Count == 0)
                    continue;
                if (primaryComponentId >= 0 && GetComponentId(seeds[0]) != primaryComponentId)
                    continue;

                AddCandidateIfDistinct(candidates, CreateCandidate(seeds[0], seeds));
            }
        }

        private void AddPerimeterCandidates(List<PatrolAnchorCandidate> candidates)
        {
            for (int i = 0; i < PerimeterSampleCount; i++)
            {
                float t = (float)i / PerimeterSampleCount;
                DFPosition perimeterSample = GetPerimeterSample(t);
                List<DFPosition> seeds = CollectSeedCluster(perimeterSample, GoalSeedRadius, PerimeterSnapRadius);
                if (seeds.Count == 0)
                    continue;
                if (primaryComponentId >= 0 && GetComponentId(seeds[0]) != primaryComponentId)
                    continue;

                AddCandidateIfDistinct(candidates, CreateCandidate(seeds[0], seeds));
            }
        }

        private void AddCandidateIfDistinct(List<PatrolAnchorCandidate> candidates, PatrolAnchorCandidate candidate)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                int dx = Mathf.Abs(candidates[i].NavPosition.X - candidate.NavPosition.X);
                int dy = Mathf.Abs(candidates[i].NavPosition.Y - candidate.NavPosition.Y);
                if (dx + dy <= 10)
                    return;
            }

            candidates.Add(candidate);
        }

        private PatrolAnchorCandidate CreateCandidate(DFPosition navPosition, List<DFPosition> seeds)
        {
            PatrolAnchorCandidate candidate = new PatrolAnchorCandidate();
            candidate.NavPosition = navPosition;
            candidate.LocalPosition = GetLocalScenePosition(navPosition);
            candidate.Angle = ComputeAngle(candidate.LocalPosition);
            candidate.Seeds = seeds;
            return candidate;
        }

        private int CompareCandidatesByAngle(PatrolAnchorCandidate left, PatrolAnchorCandidate right)
        {
            return left.Angle.CompareTo(right.Angle);
        }

        private float ComputeAngle(Vector3 localPosition)
        {
            Vector3 centerLocal = GetLocalScenePosition(new DFPosition(cityNavigation.NavGridWidth / 2, cityNavigation.NavGridHeight / 2));
            float angle = Mathf.Atan2(localPosition.z - centerLocal.z, localPosition.x - centerLocal.x);
            if (angle < 0f)
                angle += Mathf.PI * 2f;

            return angle;
        }

        private List<BuildingFieldCandidate> CollectBuildingFieldCandidates(Vector3 targetLocalPosition)
        {
            List<BuildingFieldCandidate> candidates = new List<BuildingFieldCandidate>();
            List<DFPosition> baseSeeds = CollectSeedCluster(targetLocalPosition, GoalSeedRadius, GoalSnapRadius + 18);
            if (baseSeeds.Count == 0)
                return candidates;

            DFPosition baseCenter = baseSeeds[0];
            int componentId = GetComponentId(baseCenter);
            List<BuildingFieldCandidate> rawCandidates = new List<BuildingFieldCandidate>();
            rawCandidates.Add(CreateBuildingFieldCandidate(baseCenter, baseSeeds, baseCenter));

            for (int radius = 1; radius <= BuildingFieldSearchRadius; radius++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    for (int x = -radius; x <= radius; x++)
                    {
                        DFPosition candidate = new DFPosition(baseCenter.X + x, baseCenter.Y + y);
                        if (!IsWalkable(candidate))
                            continue;
                        if (componentId >= 0 && GetComponentId(candidate) != componentId)
                            continue;

                        List<DFPosition> seeds = CollectSeedCluster(candidate, GoalSeedRadius, 2);
                        if (seeds.Count == 0)
                            continue;

                        rawCandidates.Add(CreateBuildingFieldCandidate(seeds[0], seeds, baseCenter));
                    }
                }
            }

            rawCandidates.Sort(CompareBuildingFieldCandidates);
            for (int i = 0; i < rawCandidates.Count && candidates.Count < BuildingFieldVariantCount; i++)
                AddBuildingFieldCandidateIfDistinct(candidates, rawCandidates[i]);

            candidates.Sort(CompareBuildingFieldCandidatesByAngle);
            return candidates;
        }

        private BuildingFieldCandidate CreateBuildingFieldCandidate(DFPosition navPosition, List<DFPosition> seeds, DFPosition baseCenter)
        {
            BuildingFieldCandidate candidate = new BuildingFieldCandidate();
            candidate.NavPosition = navPosition;
            candidate.LocalPosition = GetLocalScenePosition(navPosition);
            candidate.Seeds = seeds;
            candidate.OpenScore = ComputeOpenScore(navPosition);
            candidate.DistanceFromBase = Mathf.Abs(navPosition.X - baseCenter.X) + Mathf.Abs(navPosition.Y - baseCenter.Y);
            candidate.Angle = Mathf.Atan2(navPosition.Y - baseCenter.Y, navPosition.X - baseCenter.X);
            return candidate;
        }

        private int CompareBuildingFieldCandidates(BuildingFieldCandidate left, BuildingFieldCandidate right)
        {
            int openComparison = right.OpenScore.CompareTo(left.OpenScore);
            if (openComparison != 0)
                return openComparison;

            int distanceComparison = left.DistanceFromBase.CompareTo(right.DistanceFromBase);
            if (distanceComparison != 0)
                return distanceComparison;

            int xComparison = left.NavPosition.X.CompareTo(right.NavPosition.X);
            if (xComparison != 0)
                return xComparison;

            return left.NavPosition.Y.CompareTo(right.NavPosition.Y);
        }

        private int CompareBuildingFieldCandidatesByAngle(BuildingFieldCandidate left, BuildingFieldCandidate right)
        {
            int angleComparison = left.Angle.CompareTo(right.Angle);
            if (angleComparison != 0)
                return angleComparison;

            return CompareBuildingFieldCandidates(left, right);
        }

        private void AddBuildingFieldCandidateIfDistinct(List<BuildingFieldCandidate> candidates, BuildingFieldCandidate candidate)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                int dx = Mathf.Abs(candidates[i].NavPosition.X - candidate.NavPosition.X);
                int dy = Mathf.Abs(candidates[i].NavPosition.Y - candidate.NavPosition.Y);
                if (dx + dy <= BuildingFieldDistinctSpacing)
                    return;
            }

            candidates.Add(candidate);
        }

        private int ComputeOpenScore(DFPosition navPosition)
        {
            int componentId = GetComponentId(navPosition);
            int score = 0;
            for (int y = -2; y <= 2; y++)
            {
                for (int x = -2; x <= 2; x++)
                {
                    int manhattan = Mathf.Abs(x) + Mathf.Abs(y);
                    if (manhattan == 0 || manhattan > 2)
                        continue;

                    DFPosition candidate = new DFPosition(navPosition.X + x, navPosition.Y + y);
                    if (!IsWalkable(candidate))
                        continue;
                    if (componentId >= 0 && GetComponentId(candidate) != componentId)
                        continue;

                    score += manhattan == 1 ? 3 : 1;
                }
            }

            return score;
        }

        private List<DFPosition> CollectSeedCluster(Vector3 targetLocalPosition, int seedRadius, int snapRadius)
        {
            Vector3 sceneTarget = locationOrigin + targetLocalPosition;
            DFPosition raw = cityNavigation.WorldToNavGridPosition(cityNavigation.SceneToWorldPosition(sceneTarget));
            return CollectSeedCluster(raw, seedRadius, snapRadius);
        }

        private List<DFPosition> CollectSeedCluster(DFPosition origin, int seedRadius)
        {
            return CollectSeedCluster(origin, seedRadius, GoalSnapRadius);
        }

        private List<DFPosition> CollectSeedCluster(DFPosition origin, int seedRadius, int snapRadius)
        {
            List<DFPosition> seeds = new List<DFPosition>();
            DFPosition center = FindNearestWalkable(origin, snapRadius, primaryComponentId);
            if (!IsWalkable(center))
                return seeds;

            int centerComponent = GetComponentId(center);
            for (int radius = 0; radius <= seedRadius; radius++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    for (int x = -radius; x <= radius; x++)
                    {
                        DFPosition candidate = new DFPosition(center.X + x, center.Y + y);
                        if (!IsWalkable(candidate))
                            continue;
                        if (centerComponent >= 0 && GetComponentId(candidate) != centerComponent)
                            continue;
                        if (ContainsPosition(seeds, candidate))
                            continue;

                        seeds.Add(candidate);
                    }
                }
            }

            if (seeds.Count == 0)
                seeds.Add(center);

            return seeds;
        }

        private bool ContainsPosition(List<DFPosition> positions, DFPosition candidate)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                if (positions[i].X == candidate.X && positions[i].Y == candidate.Y)
                    return true;
            }

            return false;
        }

        private DFPosition GetPerimeterSample(float t)
        {
            int width = cityNavigation.NavGridWidth;
            int height = cityNavigation.NavGridHeight;
            int margin = Mathf.Clamp(Mathf.Min(width, height) / 10, 6, 18);
            int left = margin;
            int right = Mathf.Max(left, width - 1 - margin);
            int top = margin;
            int bottom = Mathf.Max(top, height - 1 - margin);
            int horizontal = Mathf.Max(1, right - left);
            int vertical = Mathf.Max(1, bottom - top);
            float perimeter = (horizontal + vertical) * 2f;
            float distance = Mathf.Repeat(t, 1f) * perimeter;

            if (distance < horizontal)
                return new DFPosition(left + Mathf.RoundToInt(distance), top);
            distance -= horizontal;

            if (distance < vertical)
                return new DFPosition(right, top + Mathf.RoundToInt(distance));
            distance -= vertical;

            if (distance < horizontal)
                return new DFPosition(right - Mathf.RoundToInt(distance), bottom);
            distance -= horizontal;

            return new DFPosition(left, bottom - Mathf.RoundToInt(distance));
        }

        private DFPosition FindNearestWalkable(DFPosition origin, int radius, int preferredComponentId = -1)
        {
            if (IsWalkable(origin) && (preferredComponentId < 0 || GetComponentId(origin) == preferredComponentId))
                return origin;

            DFPosition fallback = origin;
            bool hasFallback = false;
            for (int r = 1; r <= radius; r++)
            {
                for (int y = -r; y <= r; y++)
                {
                    for (int x = -r; x <= r; x++)
                    {
                        DFPosition candidate = new DFPosition(origin.X + x, origin.Y + y);
                        if (!IsWalkable(candidate))
                            continue;
                        if (preferredComponentId >= 0 && GetComponentId(candidate) == preferredComponentId)
                            return candidate;
                        if (!hasFallback)
                        {
                            fallback = candidate;
                            hasFallback = true;
                        }
                    }
                }
            }

            return hasFallback ? fallback : origin;
        }

        private bool IsWalkable(DFPosition navPosition)
        {
            return cityNavigation != null && cityNavigation.GetNavGridWeightLocal(navPosition) > 0;
        }

        private int[] BuildComponentMap(CityNavigation cityNavigation, int[] tileCosts, out int dominantComponentId)
        {
            dominantComponentId = -1;
            if (cityNavigation == null || tileCosts == null)
                return new int[0];

            int width = cityNavigation.NavGridWidth;
            int height = cityNavigation.NavGridHeight;
            int[] ids = new int[width * height];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = -1;

            Queue<int> open = new Queue<int>();
            int nextComponentId = 0;
            int largestComponentSize = 0;

            for (int index = 0; index < tileCosts.Length; index++)
            {
                if (tileCosts[index] <= 0 || ids[index] >= 0)
                    continue;

                ids[index] = nextComponentId;
                open.Enqueue(index);
                int componentSize = 0;
                while (open.Count > 0)
                {
                    int current = open.Dequeue();
                    componentSize++;
                    int x = current % width;
                    int y = current / width;
                    EnqueueComponentNeighbour(x + 1, y, width, height, tileCosts, ids, nextComponentId, open);
                    EnqueueComponentNeighbour(x - 1, y, width, height, tileCosts, ids, nextComponentId, open);
                    EnqueueComponentNeighbour(x, y + 1, width, height, tileCosts, ids, nextComponentId, open);
                    EnqueueComponentNeighbour(x, y - 1, width, height, tileCosts, ids, nextComponentId, open);
                }

                if (componentSize > largestComponentSize)
                {
                    largestComponentSize = componentSize;
                    dominantComponentId = nextComponentId;
                }

                nextComponentId++;
            }

            return ids;
        }

        private void EnqueueComponentNeighbour(int x, int y, int width, int height, int[] tileCosts, int[] ids, int componentId, Queue<int> open)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;

            int index = y * width + x;
            if (tileCosts[index] <= 0 || ids[index] >= 0)
                return;

            ids[index] = componentId;
            open.Enqueue(index);
        }

        private int GetComponentId(DFPosition navPosition)
        {
            if (componentIds == null || cityNavigation == null)
                return -1;
            if (navPosition.X < 0 || navPosition.X >= cityNavigation.NavGridWidth || navPosition.Y < 0 || navPosition.Y >= cityNavigation.NavGridHeight)
                return -1;

            return componentIds[navPosition.Y * cityNavigation.NavGridWidth + navPosition.X];
        }

        private Vector3 GetLocalScenePosition(DFPosition navPosition)
        {
            Vector3 scenePosition = cityNavigation.WorldToScenePosition(cityNavigation.NavGridToWorldPosition(navPosition), false);
            return scenePosition - locationOrigin;
        }

        private int[] BuildTileCosts(CityNavigation cityNavigation)
        {
            if (cityNavigation == null)
                return new int[0];

            int width = cityNavigation.NavGridWidth;
            int height = cityNavigation.NavGridHeight;
            int[] costs = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int weight = cityNavigation.GetNavGridWeightLocal(x, y);
                    int index = y * width + x;
                    if (weight <= 0)
                        costs[index] = BlockedCost;
                    else
                        costs[index] = CompressWeightToCost(weight);
                }
            }

            return costs;
        }

        private int CompressWeightToCost(int weight)
        {
            float normalizedWeight = Mathf.InverseLerp(1f, 15f, weight);
            float softenedCost = Mathf.Lerp(4.5f, 1f, normalizedWeight);
            return Mathf.Clamp(Mathf.RoundToInt(softenedCost), 1, 5);
        }

        private struct PatrolAnchorCandidate
        {
            public DFPosition NavPosition;
            public Vector3 LocalPosition;
            public float Angle;
            public List<DFPosition> Seeds;
        }

        private struct PatrolAnchor
        {
            public int Index;
            public DFPosition NavPosition;
            public Vector3 LocalPosition;
            public List<DFPosition> Seeds;
        }

        private struct BuildingFieldCandidate
        {
            public DFPosition NavPosition;
            public Vector3 LocalPosition;
            public List<DFPosition> Seeds;
            public int OpenScore;
            public int DistanceFromBase;
            public float Angle;
        }
    }

    internal sealed class RadiantNPCsFlowField
    {
        private readonly int width;
        private readonly int height;
        private readonly int[] distances;
        private readonly sbyte[] directions;

        public RadiantNPCsFlowField(string cacheKey, int width, int height, int[] distances, sbyte[] directions, bool generatedOnGpu)
        {
            CacheKey = cacheKey;
            this.width = width;
            this.height = height;
            this.distances = distances;
            this.directions = directions;
            GeneratedOnGpu = generatedOnGpu;
        }

        public string CacheKey { get; private set; }

        public bool GeneratedOnGpu { get; private set; }

        public int Width
        {
            get { return width; }
        }

        public int Height
        {
            get { return height; }
        }

        public bool IsGoal(DFPosition position)
        {
            int distance;
            if (!TryGetDistance(position, out distance))
                return false;

            return distance == 0;
        }

        public bool TryGetNextStep(DFPosition position, out DFPosition nextPosition)
        {
            nextPosition = position;
            int index;
            if (!TryGetIndex(position, out index))
                return false;

            int distance = distances[index];
            if (distance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                return false;

            int direction = directions[index];
            if (direction < 0)
                return false;

            switch (direction)
            {
                case 0:
                    nextPosition = new DFPosition(position.X + 1, position.Y);
                    return true;
                case 1:
                    nextPosition = new DFPosition(position.X - 1, position.Y);
                    return true;
                case 2:
                    nextPosition = new DFPosition(position.X, position.Y + 1);
                    return true;
                case 3:
                    nextPosition = new DFPosition(position.X, position.Y - 1);
                    return true;
                default:
                    return false;
            }
        }

        public bool TryGetDirection(DFPosition position, out int direction)
        {
            direction = -1;
            int index;
            if (!TryGetIndex(position, out index))
                return false;

            direction = directions[index];
            return true;
        }

        public bool TryFindNearestNavigableCell(DFPosition origin, int maxRadius, out DFPosition navigableCell)
        {
            navigableCell = origin;
            int bestScore = int.MaxValue;
            int bestDistance = int.MaxValue;
            bool found = false;

            for (int radius = 0; radius <= maxRadius; radius++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    for (int x = -radius; x <= radius; x++)
                    {
                        DFPosition candidate = new DFPosition(origin.X + x, origin.Y + y);
                        int candidateDistance;
                        if (!TryGetDistance(candidate, out candidateDistance))
                            continue;
                        if (candidateDistance >= RadiantNPCsFlowFieldGenerator.InfiniteDistance)
                            continue;

                        int score = Mathf.Abs(x) + Mathf.Abs(y);
                        if (!found || score < bestScore || (score == bestScore && candidateDistance < bestDistance))
                        {
                            found = true;
                            bestScore = score;
                            bestDistance = candidateDistance;
                            navigableCell = candidate;
                        }
                    }
                }

                if (found)
                    return true;
            }

            return false;
        }

        public bool TryGetDistance(DFPosition position, out int distance)
        {
            distance = RadiantNPCsFlowFieldGenerator.InfiniteDistance;
            int index;
            if (!TryGetIndex(position, out index))
                return false;

            distance = distances[index];
            return true;
        }

        private bool TryGetIndex(DFPosition position, out int index)
        {
            index = -1;
            if (position.X < 0 || position.X >= width || position.Y < 0 || position.Y >= height)
                return false;

            index = position.Y * width + position.X;
            return true;
        }
    }

    internal sealed class RadiantNPCsFlowFieldGenerator
    {
        public const int InfiniteDistance = 1073741823;

        private readonly ComputeShader computeShader;
        private readonly int relaxKernel = -1;
        private readonly int buildDirectionsKernel = -1;

        public RadiantNPCsFlowFieldGenerator(ComputeShader computeShader)
        {
            this.computeShader = computeShader;
            if (computeShader != null)
            {
                try
                {
                    relaxKernel = computeShader.FindKernel("RelaxDistances");
                    buildDirectionsKernel = computeShader.FindKernel("BuildDirections");
                }
                catch
                {
                    relaxKernel = -1;
                    buildDirectionsKernel = -1;
                }
            }
        }

        public bool CanUseGpu
        {
            get { return computeShader != null && relaxKernel >= 0 && buildDirectionsKernel >= 0 && SystemInfo.supportsComputeShaders; }
        }

        public RadiantNPCsFlowField Generate(string cacheKey, int width, int height, int[] tileCosts, List<DFPosition> seeds, bool preferCpu = false)
        {
            if (width <= 0 || height <= 0 || tileCosts == null || seeds == null || seeds.Count == 0)
                return null;

            int[] distances;
            sbyte[] directions;
            bool generatedOnGpu = false;

            if (!preferCpu && CanUseGpu && TryGenerateOnGpu(width, height, tileCosts, seeds, out distances, out directions))
                generatedOnGpu = true;
            else
                GenerateOnCpu(width, height, tileCosts, seeds, out distances, out directions);

            return new RadiantNPCsFlowField(cacheKey, width, height, distances, directions, generatedOnGpu);
        }

        private bool TryGenerateOnGpu(int width, int height, int[] tileCosts, List<DFPosition> seeds, out int[] distances, out sbyte[] directions)
        {
            distances = null;
            directions = null;

            int cellCount = width * height;
            int[] initialDistances = BuildInitialDistances(width, height, tileCosts, seeds);
            int[] changed = new int[1];
            int[] gpuDirections = new int[cellCount];

            ComputeBuffer costBuffer = null;
            ComputeBuffer pingBuffer = null;
            ComputeBuffer pongBuffer = null;
            ComputeBuffer changedBuffer = null;
            ComputeBuffer directionBuffer = null;

            try
            {
                costBuffer = new ComputeBuffer(cellCount, sizeof(int));
                pingBuffer = new ComputeBuffer(cellCount, sizeof(int));
                pongBuffer = new ComputeBuffer(cellCount, sizeof(int));
                changedBuffer = new ComputeBuffer(1, sizeof(int));
                directionBuffer = new ComputeBuffer(cellCount, sizeof(int));

                costBuffer.SetData(tileCosts);
                pingBuffer.SetData(initialDistances);
                pongBuffer.SetData(initialDistances);

                computeShader.SetInt("_Width", width);
                computeShader.SetInt("_Height", height);
                computeShader.SetInt("_InfiniteDistance", InfiniteDistance);
                computeShader.SetBuffer(relaxKernel, "_Costs", costBuffer);
                computeShader.SetBuffer(relaxKernel, "_Changed", changedBuffer);
                computeShader.SetBuffer(buildDirectionsKernel, "_Costs", costBuffer);
                computeShader.SetBuffer(buildDirectionsKernel, "_Directions", directionBuffer);

                int threadGroupsX = Mathf.CeilToInt(width / 8f);
                int threadGroupsY = Mathf.CeilToInt(height / 8f);
                int maxIterations = Mathf.Min(cellCount, Mathf.Max(width, height) * 8);
                bool converged = false;

                for (int iteration = 0; iteration < maxIterations; iteration++)
                {
                    changed[0] = 0;
                    changedBuffer.SetData(changed);

                    computeShader.SetBuffer(relaxKernel, "_DistanceIn", pingBuffer);
                    computeShader.SetBuffer(relaxKernel, "_DistanceOut", pongBuffer);
                    computeShader.Dispatch(relaxKernel, threadGroupsX, threadGroupsY, 1);

                    changedBuffer.GetData(changed);
                    ComputeBuffer swap = pingBuffer;
                    pingBuffer = pongBuffer;
                    pongBuffer = swap;

                    if (changed[0] == 0)
                    {
                        converged = true;
                        break;
                    }
                }

                if (!converged)
                    return false;

                computeShader.SetBuffer(buildDirectionsKernel, "_Distances", pingBuffer);
                computeShader.Dispatch(buildDirectionsKernel, threadGroupsX, threadGroupsY, 1);

                distances = new int[cellCount];
                pingBuffer.GetData(distances);
                directionBuffer.GetData(gpuDirections);
                directions = ConvertDirections(gpuDirections);
                return true;
            }
            catch
            {
                distances = null;
                directions = null;
                return false;
            }
            finally
            {
                if (costBuffer != null)
                    costBuffer.Release();
                if (pingBuffer != null)
                    pingBuffer.Release();
                if (pongBuffer != null)
                    pongBuffer.Release();
                if (changedBuffer != null)
                    changedBuffer.Release();
                if (directionBuffer != null)
                    directionBuffer.Release();
            }
        }

        private void GenerateOnCpu(int width, int height, int[] tileCosts, List<DFPosition> seeds, out int[] distances, out sbyte[] directions)
        {
            int cellCount = width * height;
            distances = BuildInitialDistances(width, height, tileCosts, seeds);
            directions = new sbyte[cellCount];
            for (int i = 0; i < directions.Length; i++)
                directions[i] = -1;

            DistanceMinHeap open = new DistanceMinHeap();
            for (int i = 0; i < seeds.Count; i++)
            {
                int seedIndex = seeds[i].Y * width + seeds[i].X;
                distances[seedIndex] = 0;
                open.Enqueue(seedIndex, 0);
            }

            while (open.Count > 0)
            {
                HeapNode node = open.Dequeue();
                if (node.Distance != distances[node.Index])
                    continue;

                int x = node.Index % width;
                int y = node.Index / width;
                int stepCost = tileCosts[node.Index];
                if (stepCost < 0)
                    continue;

                for (int i = 0; i < 4; i++)
                {
                    int nx = x;
                    int ny = y;
                    switch (i)
                    {
                        case 0:
                            nx++;
                            break;
                        case 1:
                            nx--;
                            break;
                        case 2:
                            ny++;
                            break;
                        default:
                            ny--;
                            break;
                    }

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                        continue;

                    int neighbourIndex = ny * width + nx;
                    if (tileCosts[neighbourIndex] < 0)
                        continue;

                    int nextDistance = node.Distance + stepCost;
                    if (nextDistance >= distances[neighbourIndex])
                        continue;

                    distances[neighbourIndex] = nextDistance;
                    open.Enqueue(neighbourIndex, nextDistance);
                }
            }

            BuildDirectionsOnCpu(width, height, tileCosts, distances, directions);
        }

        private void BuildDirectionsOnCpu(int width, int height, int[] tileCosts, int[] distances, sbyte[] directions)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (tileCosts[index] < 0 || distances[index] >= InfiniteDistance || distances[index] == 0)
                    {
                        directions[index] = -1;
                        continue;
                    }

                    int bestDirection = -1;
                    int bestScore = distances[index];
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = x;
                        int ny = y;
                        switch (i)
                        {
                            case 0:
                                nx++;
                                break;
                            case 1:
                                nx--;
                                break;
                            case 2:
                                ny++;
                                break;
                            default:
                                ny--;
                                break;
                        }

                        if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                            continue;

                        int neighbourIndex = ny * width + nx;
                        if (tileCosts[neighbourIndex] < 0 || distances[neighbourIndex] >= InfiniteDistance)
                            continue;

                        int score = distances[neighbourIndex];
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestDirection = i;
                        }
                    }

                    directions[index] = (sbyte)bestDirection;
                }
            }
        }

        private int[] BuildInitialDistances(int width, int height, int[] tileCosts, List<DFPosition> seeds)
        {
            int[] initialDistances = new int[width * height];
            for (int i = 0; i < initialDistances.Length; i++)
                initialDistances[i] = InfiniteDistance;

            for (int i = 0; i < seeds.Count; i++)
            {
                int index = seeds[i].Y * width + seeds[i].X;
                if (index >= 0 && index < initialDistances.Length && tileCosts[index] >= 0)
                    initialDistances[index] = 0;
            }

            return initialDistances;
        }

        private sbyte[] ConvertDirections(int[] gpuDirections)
        {
            sbyte[] converted = new sbyte[gpuDirections.Length];
            for (int i = 0; i < gpuDirections.Length; i++)
                converted[i] = (sbyte)gpuDirections[i];

            return converted;
        }

        private sealed class DistanceMinHeap
        {
            private readonly List<HeapNode> nodes = new List<HeapNode>();

            public int Count
            {
                get { return nodes.Count; }
            }

            public void Enqueue(int index, int distance)
            {
                HeapNode node = new HeapNode(index, distance);
                nodes.Add(node);
                int child = nodes.Count - 1;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (nodes[parent].Distance <= nodes[child].Distance)
                        break;

                    HeapNode swap = nodes[parent];
                    nodes[parent] = nodes[child];
                    nodes[child] = swap;
                    child = parent;
                }
            }

            public HeapNode Dequeue()
            {
                HeapNode root = nodes[0];
                int lastIndex = nodes.Count - 1;
                nodes[0] = nodes[lastIndex];
                nodes.RemoveAt(lastIndex);

                int parent = 0;
                while (true)
                {
                    int left = parent * 2 + 1;
                    int right = left + 1;
                    if (left >= nodes.Count)
                        break;

                    int smallest = left;
                    if (right < nodes.Count && nodes[right].Distance < nodes[left].Distance)
                        smallest = right;

                    if (nodes[parent].Distance <= nodes[smallest].Distance)
                        break;

                    HeapNode swap = nodes[parent];
                    nodes[parent] = nodes[smallest];
                    nodes[smallest] = swap;
                    parent = smallest;
                }

                return root;
            }
        }

        private struct HeapNode
        {
            public HeapNode(int index, int distance)
            {
                Index = index;
                Distance = distance;
            }

            public int Index;
            public int Distance;
        }
    }
}
