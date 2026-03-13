# RadiantNPCs Notes

## Goal

Make town and settlement NPCs feel persistent instead of disposable:

- Exterior wandering NPCs should come from a persistent resident pool per location.
- Residents should keep the same name, appearance/flat choice, and disposition when the player returns.
- Houses should own residents, and residents should eventually follow schedules between outdoors and indoors.

## User Requirements

These are the requirements provided so far and should be preserved as design constraints.

### Core simulation

- Replace the current random wandering-city-NPC behavior with a persistent pool of residents per settlement.
- Do not allow the vanilla system to effectively generate infinite random mobile NPCs.
- RadiantNPCs should control the creation of mobile civilians and assign them to houses in a settlement/city/village.
- The number of residents in a village, city, farm, or settlement should be based on the number of beds, with a small amount of variation.
- Bed-based occupancy is preferred over simple building-type heuristics.
- A couple (one man and one woman) can share a single bed.
- Current data-model assumption: in households with at least two adult residents, the first generated male/female pair is treated as the core couple for shared-bed and paired-behavior logic.
- NPCs already have names and disposition data; this data should persist so the same NPCs return on revisits.
- NPCs should eventually have schedules and basic waypoint/pathfinding behavior.
- NPCs should have collision with the player.
- NPCs should have collision with other physical world objects.
- NPCs should have assigned homes and sleep there.
- Houses should assign residents based on bed count.
- A house with two adult residents should use one male and one female resident.
- Adult residents sharing a house should share the same last name.
- People in the same house should share the same last name in general.
- NPCs who actually live in the same house should share the same last name.
- Visiting NPCs are separate from the household and do not need to share the household surname.
- Houses that are for sale should not have household NPCs assigned to them.
- If all inhabitants are away, residential houses should be locked.
- Night-time locking should continue to respect vanilla DFU behavior unless RadiantNPCs intentionally extends it.

### First milestone

- Hijack the existing day/night flow instead of replacing everything at once.
- In the morning, residents should emerge and populate the exterior from their assigned pool.
- In the evening, residents should disappear by returning toward their assigned homes and entering their doors.
- Interior residents should eventually be the same assigned household NPCs, not unrelated random ones.
- Interior household NPCs should only be present when they are actually home.
- When the player enters a zone, nearby households should generate their residents instead of relying on the vanilla random mobile-NPC generation.
- As the player walks deeper into a city, more nearby houses should progressively generate their residents.
- Resident generation should be based on surrounding houses in the vicinity, not a global infinite civilian pool.
- These generated residents must persist and continue their local behavior rather than existing as throwaway spawns.
- It is acceptable, and probably preferable, to generate and persist the full city-wide resident/household data model up front when entering a city.
- The likely performance risk is not generating all resident info at once, but instantiating and fully simulating all resident GameObjects at once.

### Quest compatibility

- Do not break quests if avoidable.
- If a quest NPC needs a slot in a building that already has an assigned inhabitant, the quest NPC may override or replace that inhabitant temporarily.
- This is acceptable for now as long as it is not obvious to the player.

### Workflow

- Keep a Markdown notes file in the mod folder and keep updating it with new requirements and findings.
- Always record new user-provided design details in this file before acting on them so the mod definition is not lost.
- Mirror `RadiantNPCs:` debug logs into a text log file inside the mod folder so iteration does not depend on manually copying Unity console output.

### Long-term AI / radiant behavior

- NPCs should eventually have somewhat randomized schedules.
- NPCs should go to shops sometimes.
- NPCs should visit friends sometimes.
- Sometimes non-resident NPCs should visit occupied houses if the owners are at home.
- NPCs should sometimes meet on the street and talk to each other.
- Dynamic NPC shoppers should enter shops, walk around inside, interact socially with the static shop owner, and then leave again.
- Shops keep their existing static shop-owner/shop-worker flat NPCs as the permanent inhabitants of the shop.
- The overall target is a lightweight radiant-style NPC system similar in spirit to Oblivion.
- Mobile NPC movement should move away from pure randomized wandering and toward waypoint-based behavior.
- NPCs should have a field of vision.
- Mobile NPC billboards/flats should not automatically turn to a "facing the player" look when the player approaches from behind.
- NPCs should only visually react/turn toward the player when the player is inside their field of vision or when the NPC is actively interacting with the player.
- NPC field of vision should also matter for NPC-to-NPC interactions.
- NPCs should notice each other through their vision logic, then sometimes stop for a short or long spontaneous conversation.
- Mobile NPCs should eventually stop moving in perfect straight-line waypoint segments with sharp turns.
- A preferred intermediate improvement is slow steering/rotation so NPCs round corners in arcs instead of snapping direction at each waypoint.
- This implies NPCs should reason about at least the next waypoint ahead, not only the immediate one.
- A longer-term preferred solution is A* pathfinding toward a true destination such as a shop, building door, or prop/landmark.
- The heavier pathfinding/simulation version should ideally be moved to a compute-shader/GPU solution if performance requires it.
- Residential interior NPCs can remain stationary at first if necessary.
- If interior movement is added later, residential NPCs should be stationary most of the time and only occasionally move to another room.
- Whether someone is home should come from their actual schedule plus some controlled variability, not pure random presence checks.
- Single-person households should have the highest chance that nobody is home.
- Larger households should have a higher chance that at least one person is home.
- Couples should sometimes walk together through the city as a pair.
- Couples can split temporarily at destinations such as markets or shops and meet up again outside before returning together.
- Guards should make up roughly 5-10% of the population.
- Guards patrol streets, especially during the day.
- Guards should still exist at night, but in reduced numbers.
- Guards may patrol alone or in pairs.
- City walls and gate/wall structures should be treated as guard-related living/patrol spaces.

### Activity / destination ideas

- Market squares as major daytime hubs.
- Wells and statues as casual social gathering points.
- Tree-filled parks or greener plazas as preferred leisure destinations.
- Inns/taverns becoming busier in the evening, especially near weekends or holidays.
- Musicians or performers attracting temporary crowds.
- Jesters or other stationary entertainer NPCs creating come-and-go audience groups.
- Couples taking walks together.
- Friends visiting houses.
- Short shopping trips to one or more stores.
- Window-shopping / wandering around named commercial streets.
- Brief stop-and-chat interactions on roads.
- Guard checkpoint / wall / gate patrol loops.
- Evening tavern visits followed by staggered departures home.
- Holiday-specific social hotspots and denser public activity.

### Performance / implementation notes

- If the eventual number of NPCs and their pathfinding becomes too expensive on the CPU, investigate GPU/compute-shader-based acceleration for heavy simulation work such as pathfinding.
- Treat compute shaders as an optimization path, not a hard requirement for the first playable implementation.
- Compute shaders are currently the most likely optimization path for handling large NPC counts plus behavior/pathfinding load, but this should be validated with testing and profiling before committing to that architecture.
- Preferred architecture: persist and simulate high-level state for the whole city, but only instantiate and run detailed movement/pathfinding for the active nearby subset of NPCs around the player.

### Future AI / voice integration ideas

- Possible future integration of MiraTTS, prewarmed in the background through a Python script or plugin, to generate NPC voice lines during conversations.
- Possible future integration of a lightweight local LLM through LM Studio for custom conversation generation or rephrasing of standard NPC lines.
- These integrations are optional future extensions and should not block the core resident/schedule simulation work.

### Bed counting notes

- Current implementation uses interior bed model IDs to estimate household capacity.
- Current implementation also checks residential interior `Rest` markers and uses them as a sleep-capacity signal.
- Initial inferred mapping:
  - `41000` -> single bed
  - `41001` -> single bed
  - `41002` -> double bed
- Effective household capacity currently uses the maximum of:
  - detected rest-marker count
  - detected bed-model capacity
- This mapping is based on editor grouping data and may need correction after inspecting more actual house interiors in-game.
- If no bed models are found for a residence, the mod currently falls back to the older house-type heuristic rather than failing.

## Code Findings

### Mobile NPCs

- Exterior wandering civilians use `MobilePersonNPC`.
- Their movement is driven by `MobilePersonMotor`.
- Their spawn/recycle lifecycle is managed by `PopulationManager`.
- `PopulationManager` already exposes a hook:
  - `PopulationManager.MobileNPCGenerator`
- This is the cleanest first seam for replacing random exterior civilians with persistent residents.

Relevant files:

- `Assets/Scripts/Game/Utility/PopulationManager.cs`
- `Assets/Scripts/Game/MobilePersonNPC.cs`
- `Assets/Scripts/Game/MobilePersonMotor.cs`
- `Assets/Scripts/Game/Utility/CityNavigation.cs`

### Static / flat NPCs

- Flat-based NPCs use `StaticNPC`.
- `StaticNPC` is attached to:
  - interior building residents/shopkeepers
  - dungeon flat NPCs
  - exterior faction-bearing flat NPCs
- `StaticNPC` stores a compact `NPCData` record including:
  - faction
  - name seed
  - context
  - map ID
  - location ID
  - building key
  - billboard archive/record

Relevant files:

- `Assets/Scripts/Game/StaticNPC.cs`
- `Assets/Scripts/Utility/RMBLayout.cs`
- `Assets/Scripts/Internal/DaggerfallInterior.cs`
- `Assets/Scripts/Internal/DaggerfallBillboard.cs`

### Exterior flat NPCs

- `RMBLayout` adds `StaticNPC` to exterior flats when `FactionID != 0`.
- These are not the same system as the wandering mobile civilians.
- This means RadiantNPCs needs to consider two different NPC pipelines:
  - wandering mobile civilians
  - fixed flat NPCs already authored into location data

### Interior flat NPCs

- `DaggerfallInterior` instantiates `RmbBlockPeopleRecord` NPCs for building interiors.
- Interior NPCs are disabled when the shop/building is closed.
- Interior NPCs are also disabled for player-owned houses and some guild restrictions.
- This is likely the later seam for replacing random household interiors with persistent household residents.

### Talking and identity

- Clicking a wandering exterior civilian talks to `MobilePersonNPC`.
- Clicking a flat NPC talks to `StaticNPC`.
- Mobile civilians use the current region "people" faction for general conversation.
- Static NPCs use their faction/building context and can participate in services, guild logic, merchants, and questor logic.

Relevant files:

- `Assets/Scripts/Game/TalkManager.cs`
- `Assets/Scripts/Game/PlayerActivate.cs`

### Quest interaction

- `QuestMachine.SetupIndividualStaticNPC()` already supports named/individual NPCs.
- If an individual NPC has been placed elsewhere by a quest, DFU disables the NPC at their home site.
- `StaticNPC.AssignQuestResourceBehaviour()` and `QuestMachine.ActiveQuestor(StaticNPC.NPCData)` connect world NPCs to quest resources.
- Quest-injected NPCs are created by `GameObjectHelper.AddQuestNPC()`.

Implication:

- The engine already has a precedent for "home NPC suppressed because quest moved them elsewhere".
- RadiantNPCs should follow that model instead of fighting it.

Relevant files:

- `Assets/Scripts/Game/Questing/QuestMachine.cs`
- `Assets/Scripts/Utility/GameObjectHelper.cs`
- `Assets/Scripts/Game/Questing/Actions/PlaceNpc.cs`

## Current Design Direction

### Phase 1

- Persist an exterior resident pool per location.
- Hook or replace the existing vicinity-based civilian generation so nearby houses provide the residents that appear around the player.
- Reuse the same residents instead of `RandomiseNPC()`.
- Persist at least:
  - resident ID
  - location key
  - gender
  - race/display setup
  - outfit/face selection
  - full name
  - disposition
  - assigned home building key
- The existing movement/behavior logic will likely need to be overridden or substantially redirected so generated residents act like household-driven actors rather than vanilla random wanderers.
- Current implementation direction now includes:
  - coarse resident roles (`Civilian` / `Guard`)
  - coarse hourly outside/home schedule states
  - active-bubble-style resident selection based on households near the player
  - simple pair/couple schedule synchronization for some shared outings
  - visible guard appearance for patrol-state guards
  - basic household-presence balancing so larger households are less often completely empty during the day
  - coarse destination building targets for shopping, tavern visits, and social visits

### Phase 2

- Assign residents to houses based on household rules.
- Build household surname rules.
- Start morning/evening exterior filtering from the resident pool.

### Phase 3

- Replace or inject interior household NPCs using the same resident records.
- Handle "resident is home" vs "resident is outside" state.
- Allow quest NPCs to temporarily override a resident slot when necessary.

## Open Questions

- How to estimate bed count reliably from vanilla interiors without hand-authoring data for every building.
- Which house types should count as valid residences for civilian household assignment.
- Whether disposition should be stored only in mod save data or also surfaced into existing faction/reaction systems.
- Whether exterior flat NPCs should remain untouched initially or be folded into the same resident pool later.
- Whether compute shaders are worth the added complexity for DFU mod compatibility and debugging, or whether CPU-side simulation is sufficient for the target population sizes.
