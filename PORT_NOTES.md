# Unity port notes

## data.gd and regions.gd

Data loading: JSON is loaded with `Resources.Load<TextAsset>` from `Assets/Resources/Data/*.json` and `Assets/Resources/Map/{city,roadgraph}.json`. If a file isn't there, `DataLoader` falls back to `Assets/StreamingAssets/<path>.json`, using File IO on desktop and a synchronous `UnityWebRequest` on Android jar: paths.
The `data/*.json` files are copied unchanged into **both** `StreamingAssets/Data` and `Resources/Data`. Delete one copy before shipping, or the build carries both.

Package: `com.unity.nuget.newtonsoft-json` 3.2.1 (in Packages/manifest.json).

## Stubbed
- ~~`IForestGen` (forest.gd)~~ ported in `Forest.cs`; `new Game()` calls `Forest.Register()`, which sets `Data.ForestGenFactory`. If `Data` is used without a `Game`, call `Forest.Register()` first or `SetWorldSeed` still skips the forest pass.
- `ForestGen.H32/Hf` now lives in `Forest.cs` (Stubs.cs removed). It matches the GDScript 64-bit math.
- `IRng` replaces RandomNumberGenerator. `GdRng` is the PCG32 port; `SystemRng` does **not** reproduce Godot's sequence.

## Needs human review
1. **Sorting:** Godot's `sort_custom` is unstable. The port uses a stable sort for `research_by_cat` (sorted by tier), the forest-graph `near` list (sorted by distance) and `label_lots`. On ties, the order can differ from the Godot build.
2. **JSON numbers:** Godot parses every JSON number as a float. Newtonsoft keeps integers as long. The port converts explicitly; code ported later should do the same.
3. **Typed tables:** `gear_def` returns null where GDScript returned `{}`. `UNARMED.Dmg` is null, and `GunHit` treats it as 35. Keys that were optional are nullable fields.
4. **Lots:** `lots` is now a `Lot` class, not a 10-slot Array. Index map: 0 X, 1 Y, 2 W, 3 H, 4 Kind, 5 DoorX, 6 DoorY, 7 District, 8 Cells, 9 Landmark. Saves or net code that serialise lots as arrays need an adapter.
5. **Loot tables:** all values are now `double[2]`. GDScript kept ints for entries that weren't materials. Check callers that pass these to `randi_range`.
6. **`lot_name` indexing:** negative `%` with Array wrap-around is emulated with `Gd.At` (world_seed = -1).
7. **Paths and rounding:** `EventArtPath` still returns the Godot `res://` path. `Gd.Capitalize` approximates Godot's `String.capitalize()`. `roundf` → `Math.Round(AwayFromZero)`.
8. **Dictionary order:** edge creation order relies on C# `Dictionary` enumerating in insertion order when nothing is removed. That holds in practice, but the spec doesn't guarantee it.
9. **Main thread:** `Data.Instance` must first be touched on the main thread, because `Resources.Load` only works there.

## game.gd, forest.gd, sfx.gd (core game logic)

Files (all `namespace DayZero.Core`, C# 9, no UnityEngine runtime calls besides Vector2/Vector2Int/Rect/Color/Debug):

| File | game.gd area |
|---|---|
| `Game.cs` | autoload state, colonies, new run / start, clock, `Process`/`Step`/`Advance`, hour tick, logging, resources |
| `GameTypes.cs` | typed state: `GameState` (S), `Colony`, `Person`, `Base`, `Squad`, `Camp`, `Vehicle`, `Wreck`, ... |
| `Game.World.cs` | infection spread, weights, safe zones, zombies (packed lists), buckets, flow field, sectors, paths |
| `Game.Colonists.cs` | jobs, economy, medicine, bites/deaths, movement, scavenging, combat, outposts, groups, rescues, auto settings |
| `Game.Defense.cs` | fences / segments, towers, enclosures, territory, claims, gates, traps, repairs, base levels |
| `Game.Family.cs` | guards, relationships, births, growing up, beds/arrivals, daily routines |
| `Game.Gear.cs` | loadouts, crafting queue, gun noise, vehicles, garage, wrecks, towing |
| `Game.Research.cs` | research, conversions, gutting |
| `Game.Squads.cs` | squads, travel between maps, sweeps, squad scavenging, drone / heli / air strike, camp attacks |
| `Game.Factions.cs` | camps, trade (manual + automatic), faction AI, night raids, war |
| `Game.Events.cs` | phases, hordes, events/decisions, `DoAction` dispatcher, gifts, merging colonies |
| `Game.Save.cs` | JSON save/load, autosave, net snapshots (`Serialize`, `NetColony`, `ApplyState`, zombie packing) |
| `AStarGrid.cs` | AStarGrid2D subset (Manhattan, no diagonals) |
| `GdRng.cs`, `GdMath.cs`, `GdCollections.cs` | PCG32 RNG, float32 vector helpers, insertion-ordered `ODict` |
| `Forest.cs` | forest.gd (implements `IForestGen`) |
| `Sfx.cs` | sfx.gd behind `IAudioBackend` / `ISettingsStore` |
| `Net.cs` | `INet` stub + list of what net.gd needs from Game |

Driving it: `var g = new Game(); g.NewRun(diff, seed, ncols); g.StartAt(tile); g.Running = true;` then each frame `g.Process(Time.deltaTime)` (GDScript `_process`), or `g.Advance(hours)` headless. Signals are C# events: `Changed, LogAdded, DecisionAdded, PhaseChanged, GameOver, SfxRequested (was "sfx"), HourTicked, ColonyOver`. Answer a decision with `g.ApplyChoice(ev, index)`. Player commands go through `g.DoAction(ci, action, args)` (args: `Vector2Int` for tiles, ints, strings, bools, lists).

### Stubbed / not ported
- **net.gd** (1,347 lines): only `INet` + `OfflineNet`. `Net.cs` lists every Game member net.gd calls. Game keeps `IsClient`, `Mp`, `Actor`, `Serialize(net:true)`, `NetColony`, `SeenSectors`, `ApplyState`, `ApplyZombies`, `PackZombies[Many]`, `SetClientTarget` so the host/client layer can be written against them.
- **Audio playback**: `Sfx` needs an `IAudioBackend` (AudioSource pool + clips). `NullAudio` is the headless default. Hook with `game.SfxRequested += (n, at) => sfx.Play(n, at)`.
- **Save migrations v4 to v16** (`_migrate_v4` ... `_migrate_v16`, `_shift_vectors`, `_fix_base_lots`, `_ensure_v8_keys`): not ported. They only upgrade Godot `store_var` dictionaries, which this build can't read anyway.
- The disabled legacy raider block in `_step_camps` (`if false and ...`) was dropped.

### Save format
`{"format":"dayzero-unity-json-1","ver":17, ...GameState in snake_case...}` via Newtonsoft (`Game.JsonSettings`). Vectors are `[x, y]` arrays; packed arrays are JSON arrays, except network snapshots, which send byte arrays as base64. Files: `dayzero_v4.json` / `dayzero_v4_mp.json` with `.tmp` / `.bak` rotation, through `ISaveStore` (`FileSaveStore`; set `Game.Store` or `Game.DefaultSaveDir` to `Application.persistentDataPath`). **Godot saves (`user://dayzero_v4.save`, binary `store_var`) will not load**: `LoadGame` returns false and sets `LastLoadError`. A mid-game save is about 400 KB.

### Smoke test (work/unity-verify, not committed)
`dotnet Verify.dll unity-port/Assets/Resources 30`: seed 12345, difficulty 1, one colony, start at `SuggestStart(0)`, every decision answered with its first choice, no other input, 30 days (5 steps per in-game hour) plus a save→load→+24 h round trip on day 15.
Last result: compiles with 0 warnings and throws no exceptions, about 3.3 s wall time. 645 hour ticks, 11 decisions answered (one was a war demand, answered "bend the knee"), 26 night raids. Population was 20 on day 1, 24 on day 5, 23 on day 15 and 24 on day 25. On day 28 at 03:36 the colony fell to an 85-strong night raid; no walls had been built and ammo had been 0 since day 15. Phase stayed `spread`. The save round trip matched: pop, clock and zombies were identical after loading. Whether an unattended colony also falls around day 28 in the Godot build is unverified.

## Needs human review (continued)
10. **RNG parity:** `GdRng` was written from memory of Godot's `RandomPCG`. Check it against Godot 4.4.1 (seed 12345 → first `randi()`, `randf()`, `randi_range(0, 9)`). Until then, seeded worlds and runs are deterministic in C# but not identical to the Godot build.
11. **Forest generation:** `Image.resize(INTERPOLATE_BILINEAR)` is replaced by a port of Godot's fixed-point `_scale_bilinear`. The port also reproduces a Godot quirk: a `for q in dict` loop ends when the current key is erased, which matters for the "drop short sea runs" loop. Transcendentals (`sin/cos/atan2/pow`) come from .NET, so a road or creek can shift by a tile. Compare a seed's tile map against Godot before trusting world parity.
12. **Float precision:** GDScript `float` is double and `Vector2` is float32. The port keeps that split (`V.F`, `Mul(double)` casts the scalar to float). Hot loops (zombie movement, flow field) should be spot-checked against Godot for drift.
13. **Unstable sorts:** game.gd has 22 `sort_custom` calls (Godot's is unstable, an introsort). The port uses `Gd.StableSort` everywhere. Where keys tie (job ranks, distances, war dice), the order can differ from Godot.
14. **Dictionary order:** GDScript dictionaries iterate in insertion order. The port uses `ODict` (insertion-ordered) for state dictionaries and `Dictionary<,>` only for caches. Removing from a C# `Dictionary` and re-adding can reorder it; new code should use `ODict` when iteration order matters.
15. **Typed state:** `S` and the colony, person and base dicts became classes (`GameTypes.cs`). Keys that GDScript added ad hoc (`resume`, `trav`, `vid`, `tarr`, `_to`/`_from`...) are nullable fields. `ApplyState` patches the JSON before deserialising, so a host built from a different version must send the same field names (snake_case of the C# names).
16. **Events and decisions** stay `JObject` (the events.json shape plus `col`/`uid`), so UI code reads them like before. `ApplyChoice` takes the same object.
17. **`DoAction` argument types:** GDScript type checks (`a[0] is Vector2i`) became `is Vector2Int`. Numbers can arrive as `int`, `long`, `double` or `JValue`; bad shapes return "Bad arguments." where Godot would have errored.
18. **Main-thread rule** also applies to `new Game()`: it touches `Data.Instance` and runs the forest pass.

### Player-visible differences
- Old Godot saves don't load. The game starts fresh, with no migration and no error dialog unless the UI shows `LastLoadError`.
- Sound settings move from `user://settings.cfg` to whatever `ISettingsStore` the Unity layer provides.
- Unless item 10 checks out, the same seed builds a different world (forest, wrecks, camp placement) and a different run than in Godot.
- No multiplayer until net.gd is ported.
- Wherever sorts tie (item 13), a different survivor may get picked first for a job or loadout, or a war duel may pair differently.
