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
- The number of residents in a village, city, farm, or settlement should be based on the number of beds, with a small amount of variation.
- NPCs already have names and disposition data; this data should persist so the same NPCs return on revisits.
- NPCs should eventually have schedules and basic waypoint/pathfinding behavior.
- NPCs should have assigned homes and sleep there.
- Houses should assign residents based on bed count.
- A house with two adult residents should use one male and one female resident.
- Adult residents sharing a house should share the same last name.

### First milestone

- Hijack the existing day/night flow instead of replacing everything at once.
- In the morning, residents should emerge and populate the exterior from their assigned pool.
- In the evening, residents should disappear by returning toward their assigned homes and entering their doors.
- Interior residents should eventually be the same assigned household NPCs, not unrelated random ones.
- Interior household NPCs should only be present when they are actually home.

### Quest compatibility

- Do not break quests if avoidable.
- If a quest NPC needs a slot in a building that already has an assigned inhabitant, the quest NPC may override or replace that inhabitant temporarily.
- This is acceptable for now as long as it is not obvious to the player.

### Workflow

- Keep a Markdown notes file in the mod folder and keep updating it with new requirements and findings.

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
- Hook `PopulationManager.MobileNPCGenerator`.
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
