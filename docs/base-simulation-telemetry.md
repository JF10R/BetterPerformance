# Base simulation, terrain and generation observations

### What is measured

These diagnostics time existing native calls while a capture is active. They change no wear rate, structural integrity result, terrain shape, container progress, vegetation placement or dungeon layout. They are installed with `Capture.MethodTimings`; unavailable signatures are reported as unavailable instead of guessed.

| Timing | Native scope | Interpretation |
| --- | --- | --- |
| `WearBatch` | `WearNTearUpdater.UpdateWearNTear` | Inclusive elapsed time of one wear/support batch, including every piece it reaches |
| `WearSupportUpdate` | `WearNTear.UpdateSupport` | Inclusive structural-integrity recomputation for one piece; the call count matters as much as the time |
| `HeightmapLateBatch` | `MonoUpdatersExtra.CustomLateUpdate` filtered on `MonoUpdaters.LateUpdate.Heightmap` | Inclusive dispatch of the heightmap late-update group, including nested rebuild work |
| `HeightmapRegenerate` | `Heightmap.Regenerate` | Inclusive regeneration of one heightmap |
| `HeightmapApplyModifiers` | `Heightmap.ApplyModifiers` | Inclusive application of terrain modifiers to one heightmap |
| `HeightmapCollisionRebuild` | `Heightmap.RebuildCollisionMesh` | Inclusive collision-mesh rebuild |
| `HeightmapRenderRebuild` | `Heightmap.RebuildRenderMesh` | Inclusive render-mesh rebuild |
| `TerrainCompApply` | `TerrainComp.ApplyToHeightmap` | Inclusive terrain-compiler application to one heightmap |
| `PlantUpdate` | `Plant.SUpdate` | Inclusive slow update of one plant |
| `SmelterUpdate`, `FireplaceUpdate`, `CookingStationUpdate`, `BeehiveUpdate`, `SapCollectorUpdate`, `FermenterUpdate`, `WindmillUpdate` | the matching native container tick | Inclusive per-instance tick; not a count of produced items |
| `LocationSpawn` | `ZoneSystem.SpawnLocation` | Inclusive spawn of one location; the returned object is preserved |
| `VegetationPlace` | `ZoneSystem.PlaceVegetation` | Inclusive vegetation placement for one zone |
| `ZoneSpawn` | `ZoneSystem.SpawnZone` | Inclusive zone spawn; the `out` result is preserved |
| `ZonePlaceLocations` | `ZoneSystem.PlaceLocations` | Inclusive location placement for one zone |
| `DungeonGenerate` | `DungeonGenerator.Generate(SpawnMode)` | Inclusive generation through the outer entry only |
| `DungeonSpawn` | `DungeonGenerator.Spawn` | Inclusive native spawn pass for one dungeon |

These scopes overlap by construction. `WearBatch` contains `WearSupportUpdate` calls, `HeightmapLateBatch` contains rebuild work, and zone generation contains vegetation, location and dungeon work. Never add their sums or maxima to obtain exclusive CPU time. Failed native calls keep their exceptions and are counted in `failedCalls`.

### What is deliberately not hooked

`WearNTear.UpdateWear` and `TerrainComp.Update` run per piece and per instance every frame; hooking them would cost more than it reveals. `DungeonGenerator.Generate(int, SpawnMode)` is left unpatched so the outer entry is not double counted. `SlowUpdater.UpdateLoop` returns `IEnumerator`: timing it would measure one coroutine allocation, not the work spread across frames, so it is reported as `unavailable_coroutine` and has no metric.

### Population gauges

| Gauge | Meaning |
| --- | --- |
| `population_wear_pieces` | Entries in `WearNTear.GetAllInstances()` |
| `population_heightmaps` | Entries in `Heightmap.GetAllHeightmaps()` |
| `population_terrain_modifiers_legacy` | Entries in `TerrainModifier.GetAllInstances()` |
| `population_slow_update_objects` | Entries in `SlowUpdate.GetAllInstaces()` (the vanilla spelling) |
| `slow_updates_per_frame` | Static `SlowUpdater.m_updatesPerFrame` |
| `wear_updates_per_frame_default` | Static `WearNTearUpdater.c_UpdatesPerFrame` |

Each gauge reads only the live list's `Count`. No list is enumerated, indexed or retained, and no object identifier, prefab name or coordinate is exported. A gauge is omitted when it could not be read, and its `<name>_status` label then carries `unavailable`, `unavailable_null_list` or `read_failed` instead of `enabled`.

These are registration counts, not per-frame work. A large `population_wear_pieces` does not imply that every piece was updated in any frame, and a small one does not prove the batch was cheap. `wear_updates_per_frame` is reported as `unavailable_instance_field`: the live budget is an instance field on the updater component with no static accessor, so only the compiled-in default is exported. `population_terrain_modifiers_legacy` counts the legacy modifier list, which is not the same population as `TerrainComp`.

### Reading a quick win

The pairing that matters is count against time. A large `WearSupportUpdate` count with a small sum means many cheap recomputations; a small count with a large `p99UpperBoundMs` means a few expensive ones, and only the second is addressed by reducing per-call cost. A `WearBatch` that is present while `WearSupportUpdate` stays at zero indicates that another mod disabled wear, and that zero-cost result is itself a finding rather than a missing measurement. Heightmap rebuild timings concentrated in `HeightmapCollisionRebuild` rather than `HeightmapRenderRebuild` point at physics, not rendering.

### Cost and compatibility limits

Each probe adds a Harmony prefix/finalizer pair around calls whose rate is batch- or event-scoped, never per piece per frame. The gauges add a fixed set of numbers and labels per export. Histograms use the existing bounded `MetricBook`. This is a bounded design, not a claim of measured zero overhead. Other mods can replace or disable these paths; status labels and probe failures are part of interpretation.

### Verification scope

`SimulationGameTests.Run` verifies, against the installed client assembly, the exact parameter types, return type and static/instance shape of every offline-resolvable hooked method, that the production installer registered that exact method for the stated metric, that the dungeon outer entry calls the inner overload while only the outer is hooked, that the population accessors are static and parameterless, that the wear budget really is an instance field, that the coroutine really returns `IEnumerator`, and that the batch filter literal occurs exactly once in the shipped assembly.

`Heightmap` and every other `IMonoUpdater` implementor cannot be type-loaded by a standalone CLR, so the heightmap, terrain-compiler, vegetation and location-placement contracts are proven only through the guarded installer path and are reported as `STATIC ONLY`. Their signatures were resolved from the assembly metadata, not asserted at runtime. No game method is invoked by these tests, and separately authorized runtime validation must report its own results.
