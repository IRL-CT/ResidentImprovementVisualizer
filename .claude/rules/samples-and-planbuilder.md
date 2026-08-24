---
paths:
  - "Assets/Scripts/Authoring/Interior/SampleHomes.cs"
  - "Assets/Scripts/Authoring/Interior/SampleFurniture.cs"
  - "Assets/Scripts/Authoring/Interior/SampleRefresh.cs"
  - "Assets/Scripts/Authoring/Interior/PlanBuilder.cs"
  - "Assets/Scripts/HomeViz/SampleHomeInstaller.cs"
  - "Assets/Tests/EditMode/SampleHomesTests.cs"
  - "Assets/Tests/EditMode/PlanBuilderTests.cs"
  - "Assets/Resources/FurnitureCatalog.asset"
---

# Sample homes and PlanBuilder

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Sample homes

`SampleHomes` ships six complete, furnished, single-storey dwellings, each occupied by the headcount
its blurb advertises:

| | Apartments | Houses |
|---|---|---|
| Small | Studio, 38 m², 1 person | 2 bed / 1 bath, 90 m², 2 people |
| Medium | 2 bed / 1 bath, 74 m², 2-3 people | 3 bed / 2 bath, 125 m², 4 people |
| Large | 5 bed / 4 bath, 165 m², shared home | 5 bed / 4 bath, 210 m², assisted living |

The two five-bedroom plans are the care settings the tool is aimed at: every door 36" and step-free,
roll-in showers, grab bars in every bathroom, handrails down a 1.6 m corridor, an accessible bedroom 1,
and a 1.5 m turning circle in every bedroom and bathroom. Four samples ship only the locked "Existing"
baseline; the two five-bedroom ones also ship a locked **"Smart home package"** proposal (built by
`SensorPackages.Recommend`). All open on the baseline.

- **Entry points:** seeded on first run (`HomeSettings.samplesSeeded`), and the **Sample homes** picker
  in the left rail, which adds a fresh copy (new GUID, uniqued name) any time.
- **Bump `SampleHomes.Generation` whenever a plan changes.** Each installed home carries
  `HomeDoc.sampleKey` / `sampleGeneration`; `SampleHomeInstaller.RefreshStaleSamples` (from `Start`)
  re-installs any that has fallen behind. `SampleRefresh.Evaluate` (in `CXRAuthoring`, testable)
  refreshes only a home whose variants are all `VariantDef.fromSample` and still locked, with no traced
  underlay: anything the user touched is theirs, and the rail's **Reset to the latest sample** is the
  only way back. `LegacyNames` maps retired display names to keys, and now carries the six em-dashed names the
  US-English rewrite retired; they must stay byte-exact. A schema-only change (nothing
  visible moves) does **not** bump `Generation`.
- `SampleHomeInstaller.BackfillOccupants` (guarded by `HomeSettings.occupantsBackfilled`) fills an
  empty roster in place, only for a `sample`-tagged home whose room ids still match.
- **`PlanBuilder` authors every sample**: rooms as rectangles (`Room`, `RoomPart` for an L-shape),
  walls **derived** (unioned per line and re-split at every significant point, so shared edges collapse
  to one `WallDef` and every T-junction endpoint coincides), openings by relationship
  (`DoorBetween`, `ExteriorDoor`, `Window`, asserting `OpeningFit.IsValid`), furniture by `Against` /
  `Free` / `Mount` (sliding clear of any opening the item is tall enough to reach: `OpeningSpans`,
  `TrySlideClear`, `BestEdgeFor`). Anything unresolved lands in **`PlanBuilder.Warnings`, which must be
  empty**. Openings must be declared before furniture. Occupants via `Person` / `Does`, resolved by
  `BuildOccupants(level)` after `Build()`.
- **`SampleFurniture` mirrors the 35 `FurnitureCatalog` ids** (the ScriptableObject is in
  `Assembly-CSharp`, unreachable from `CXRAuthoring`); `SampleHomeInstaller.VerifyAgainstCatalog` and
  `VerifyFloorFinishes` warn on drift at seed.

→ [`docs/design/samples-and-planbuilder.md`](../../docs/design/samples-and-planbuilder.md)
