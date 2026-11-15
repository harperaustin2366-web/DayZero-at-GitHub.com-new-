# Unity port notes

## data.gd and regions.gd

Data loading: JSON is loaded with `Resources.Load<TextAsset>` from `Assets/Resources/Data/*.json` and `Assets/Resources/Map/{city,roadgraph}.json`. If a file isn't there, `DataLoader` falls back to `Assets/StreamingAssets/<path>.json`, using File IO on desktop and a synchronous `UnityWebRequest` on Android jar: paths.
The `data/*.json` files live only in `Resources/Data` (and `Resources/Map`). There is no `StreamingAssets` copy; the StreamingAssets fallback in `DataLoader` stays in code but is unused.

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
- ~~**Audio playback**~~ done: `Runtime/UnityAudioBackend.cs` (10 AudioSources, clips from `Resources/Sfx`). `NullAudio` stays the headless default.
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

## Headless Android build (Unity Build Automation)

Nobody opens this project in the Unity editor, so everything the build needs is in files.

**Editor version: Unity 6000.3.28f1 (6.3 LTS, changeset 8f082d2fe476, released 3 Nov 2026).** 6.3 is the current LTS (supported to Dec 2027). 6.0 LTS support ended in October 2026. Build Automation lists both 6000.3 LTS and 6000.0 LTS as supported. Set the Build Automation target to "6000.3.28f1", or to "latest 6000.3 LTS". Sources: https://unity.com/releases/editor/whats-new/6000.3.28f1 and https://docs.unity.com/en-us/build-automation/reference/supported-unity-versions

**Min SDK is 25, not 24.** Unity 6.3 supports Android 7.1 (API 25) and newer, so API 24 is below its floor (https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/android/introducing/requirements-and-compatibility). To get API 24, switch to 6000.0.85f2 (6.0 LTS, now out of support): change `ProjectVersion.txt`, `AndroidMinSdkVersion` in ProjectSettings.asset and `AndroidApiLevel25` in BuildScript.cs.

### Files
| Path | What |
|---|---|
| `ProjectSettings/ProjectVersion.txt` | 6000.3.28f1 (8f082d2fe476) |
| `ProjectSettings/ProjectSettings.asset` | company Austin Harper, product DayZero, version 0.11.0a, version code 1, `com.austinharper.dayzero`, auto-rotation landscape left+right only, IL2CPP, ARM64, min SDK 25, target SDK auto (highest installed), stripping Minimal, old Input Manager (`activeInputHandler: 0`, nothing uses input APIs), built-in render pipeline |
| `ProjectSettings/EditorBuildSettings.asset` | one scene: `Assets/Scenes/Boot.unity` |
| `ProjectSettings/EditorSettings.asset` | force-text serialization, visible meta files |
| `Packages/manifest.json` | newtonsoft-json 3.2.1, ugui 2.0.0 (core package, pinned to the editor), and the modules used: audio, imgui, jsonserialize, unitywebrequest (DataLoader), androidjni, ui, uielements, animation, physics, physics2d, imageconversion |
| `Assets/Scenes/Boot.unity` | Main Camera (solid dark clear, AudioListener) + `Bootstrap` GameObject with the Bootstrap script (guid 55da3875…) |
| `Assets/Scripts/Runtime/Bootstrap.cs` | MonoBehaviour: builds `Game` one frame after start, wires Sfx and decisions, runs `Process` each frame, saves to `persistentDataPath/saves`, and saves on pause/quit. `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` creates it (and a camera) if the scene wiring fails. Forces InvariantCulture. |
| `Assets/Scripts/Runtime/GameUI.cs` | IMGUI front end (see below) |
| `Assets/Scripts/Runtime/UnityAudioBackend.cs` | `UnityAudioBackend : IAudioBackend` + `PlayerPrefsSettingsStore : ISettingsStore` |
| `Assets/Resources/Sfx/*.wav` | the 13 Godot sound effects (copied from `assets/sfx`) |
| `Assets/Editor/BuildScript.cs` | `DayZero.EditorTools.BuildScript`: `PreExport()` (applies every Player setting above from code and puts Boot.unity first in the scene list), `PostExport(string)`, `BuildAndroid()` (`-executeMethod`, optional `-output x.apk`/`.aab`) |
| `Assets/link.xml` | keeps Assembly-CSharp and Newtonsoft.Json from IL2CPP stripping (save classes are built by reflection) |
| `*.meta` | one for every file and folder under Assets. GUID = md5("dayzero:" + asset path), so regenerating a meta gives the same GUID. |

### Build Automation setup (dashboard, once)
1. Connect the GitHub repo and branch `main`. The project is at the repo root.
2. Add an Android target. Unity version: 6000.3.28f1.
3. Advanced settings: Pre-export method `DayZero.EditorTools.BuildScript.PreExport`. Post-export is optional: `DayZero.EditorTools.BuildScript.PostExport`.
4. Leave "Build App Bundle" off to get an APK you can sideload. Without a keystore, Build Automation signs the APK with a debug key: fine for your own phone, not for Google Play.

### Runtime UI (IMGUI, everything built in code)
Virtual 720-px-high canvas, scaled to the screen inside the safe area.
- **Title:** Continue (if a save exists), New game: Easy, Normal or Hard. The camp site is picked automatically with `SuggestStart(0)` because there's no map view yet.
- **Top bar:** clock (day, time, night), phase line (infection %, the Fall countdown, night-attack warnings), food, water, meds, ammo, fuel, materials, population, morale. Pause/Play, speed x1/x2/x4 (`Process` calls per frame), +1h and +6h (`Advance`).
- **Colony tab:** name, survivors, camps, zombies, kills, rescued; camps list; stockpile; job counts; raid warning.
- **Jobs tab:** survivors 7 per page (name, state, hp, life stage), with < > buttons that cycle `Data.JOBS` through `DoAction("set_job")`. A job the survivor can't take is skipped, and the reason is shown.
- **Log tab:** the colony log, newest first, 12 per page.
- **Menu tab:** Save now, Load last save, Title screen, sound on/off and volume (saved in PlayerPrefs).
- **Popups:** decisions (title, text, one button per choice with its hint, via `ApplyChoice`; the sim pauses while one is open), game over or won (New game / Title), and toasts.
- **Not covered yet:** the map and world view, picking a start site, building, squads, research, trade, crafting, vehicles and multiplayer. Those still need UI.

### Compile check (work/unity-verify, not committed)
`bash work/unity-verify/check.sh [days]` builds three projects and then runs the smoke test:
1. `Verify.csproj`: net8 + `UnityStubs.cs` + `Harness.cs`. It runs the 30-day smoke test plus a `UiFlow()` that mirrors what Bootstrap does: a random-seed new game, auto start, a `set_job` change, save, load, and a job check.
2. `UnityCompile/Runtime`: all of `Assets/Scripts`, compiled as Assembly-CSharp for **netstandard2.1** against real UnityEngine module DLLs (2022.3 player assemblies, NuGet `RocketModFix.UnityEngine.Redist` 2022.3.62.3). This catches .NET APIs Unity doesn't have. It found `Math.ScaleB` in GdRng.cs (not in .NET Standard 2.1), now replaced by an exact power of 2. The smoke-test output is unchanged.
3. `UnityCompile/Editor`: `Assets/Editor` against UnityEditor.dll 2021.1 (NuGet `Unity3D.SDK`). That DLL has no `NamedBuildTarget` and no `ManagedStrippingLevel.Minimal`, so check.sh compiles a copy with those two mapped to the old equivalents. Both were checked in the 6000.3 scripting docs.

Result: all three build with 0 errors and 0 warnings. The smoke test matches the earlier run (fall on day 28 at 03:36), and the UI flow passes.

### Not verifiable without a Unity editor (first cloud build will tell)
- The hand-written YAML (ProjectSettings.asset, Boot.unity, metas) is valid YAML with plausible serialized versions, but Unity hasn't imported it yet. Unity normally upgrades or fills in missing fields. The Bootstrap fallback starts the game even if the scene's script link breaks.
- That IMGUI looks right on a phone (font size, safe area, button sizes), and that touch maps to IMGUI clicks (it does by default).
- Newtonsoft under IL2CPP with the generic `ODict<,>` state types (AOT generics). link.xml keeps the types, but a save/load on the device is the real test.
- Speed: `new Game()` (world and forest generation) runs on the main thread during the loading frame. On desktop .NET, `new Game()` plus `NewRun` took about 0.3 s. A phone may be 5–10× slower.

## v0.11.2a: map ground fix (Nov 2026)

Device test of 0.11.1a (Android): buildings drew, but no ground and no roads; the forest zoomed in was a pale/white screen.

- **Root cause**: `DayZeroMap.shader` computed the distance haze per vertex. The ground was a single 4-vertex quad covering the whole ~886×938-tile map, so all its vertices sat hundreds of units from the camera at ~0.9 haze, and interpolation fogged the entire ground (and the roads painted into its texture) to ~90 % of the fog colour, which is also the camera clear colour. The ground was there but invisible; buildings survived because their triangles are small. The forest is mostly ground, so it read as a blank pale screen.
- **Shader**: haze now per pixel from the interpolated world position; no texture any more, vertex colour only.
- **Ground**: no texture, no ground plane. Each 64×64 chunk mesh now starts with its ground: one quad per road/avenue/bridge tile (asphalt with darker gutters, worn lighter crown, faded dashes on avenue interiors, cracks, weeds, silt), and merged runs of up to 16 tiles for everything else (corner-sampled colours from the old palette). Roads sit at y=0, other ground at 0.04 with concrete curb faces where they meet roads, water at −0.05 with dirt banks. Void skirt around the map in 64-unit cells. City estimate: ~134k ground quads + ~90k curb/bank quads (~0.9 M verts), culled with the chunks.
- **Material**: one material for everything. `DayZero/Map` (Resources) → `Hidden/Internal-Colored` → `Sprites/Default`/`UI/Default`. A fallback shows a line at the bottom-left of the HUD (`MapView.RenderWarning`). `BuildScript.ApplyAndroidSettings` adds DayZero/Map, Hidden/Internal-Colored, Sprites/Default and UI/Default to GraphicsSettings > Always Included Shaders (via SerializedObject; no hand-written GraphicsSettings.asset).
- Version 0.11.2a, Android versionCode 3.

## v0.11.1a: 3D map + HUD restyle (Nov 2026)

### Done
- **Runtime/MapView.cs**: the city as a 3D view, built entirely at runtime from `Data` (no editor assets). The art target is the approved concept (`dayzero-style-concept.png` in the project workspace): a ruined, overgrown city, flat-shaded low-poly.
  - Ground: one plane with a 2-px-per-tile texture painted from `Data.Tiles`. Olive grass with dirt patches, dark-teal water, cracked asphalt with weeds and silt, paving, fields, piers, and lot yards (grass for houses, concrete, faded parking bays).
  - Buildings: one per lot, oriented by `Data.LotAngle` (Godot `_quad`, pad 0.32), with heights from Godot `_lot_height` (FL 0.4). Uses the Godot desaturated palette (PAL), with window bands per floor, roof clutter and moss mats. Houses, churches and barns get gable roofs. About 9% are burnt (charred, lower, rubble), 6% collapsed (rubble heap and a sapling), and 12% half-collapsed (one half sheared off). Landmarks are never ruined.
  - Scatter: trees in parks, forest and meadow, and saplings in cracked roads. Also rusted or burnt wrecks on roads and parking lots, concrete jersey-barrier runs and road rubble.
  - Combined into one mesh per 64×64-tile chunk, with lighting baked into vertex colours (warm sun) and the 32-bit index format only when a chunk tops 65k verts. Chunks farther than about 3.2× the camera distance are switched off.
  - Material: `Assets/Resources/Shaders/DayZeroMap.shader` (unlit, texture × vertex colour, own distance haze from globals `_DZFogColor` and `_DZFogParams`, Cull Off). It is loaded with `Resources.Load`, so the build always ships it, and it doesn't rely on Unity fog variants, which automatic stripping would drop. Fallback: `Hidden/Internal-Colored` for objects and `Sprites/Default` (renderQueue 1000) for the ground, with no haze.
  - Dynamic layer, rebuilt when something changes and checked every 0.75 s: camps get a wooden palisade with a gate gap. A camp in a building gets a palisade around the lot and a fire at the gate; otherwise a hut with amber lit windows, a campfire and crates. The main camp also gets a watchtower and a flag pole in colony colour. Converted lots get green roof caps; lit lots get yellow caps (yours) or red (others).
  - The build runs as a coroutine with a time-sliced 14 ms per frame. GameUI shows "Building the map n%". It rebuilds if `WorldSeed`, the lot count or the map size changes.
  - Camera: perspective, FOV 40, pitch 52 (Godot), start yaw 30, distance 55 (14–230). One finger pans (ground-plane raycast, so terrain sticks to the finger), pinch zooms, and two-finger twist rotates. Mouse drag and wheel work in the editor. The focus is clamped to the map.
- **GameUI**: the HUD sits over the map using the Godot video's layout. Stats are top-left (survivors, beds, zombies, dead, time, phase, colony line), MENU is top-centre, kills, rescued and resource chips are top-right, and HOME, +, − and MAP run down the left. The collapsible ACTIONS panel on the right holds BUILD, CAMP, GATHER, DEFENSE, CREW & JOBS, COLONY and LOG; BUILD and DEFENSE are toast placeholders. Pause, speed and skip sit bottom-centre. The old tabs (Colony, Jobs, Log, Menu) now live in the MENU overlay. Map labels show districts when zoomed out and landmarks when zoomed in, plus camp names. The style uses dark translucent panels, olive buttons and amber accents. HUD rects are hit-tested so touches on buttons don't pan the map, and modals block map input.
- Version 0.11.1a, Android versionCode 2 (ProjectSettings.asset and BuildScript.cs).

### Left to do
- **Fog of war** (Godot `sight` / `last_seen`): darken unseen chunks/tiles, e.g. a second low-res texture multiplied into the ground and a per-lot darkening of the overlay.
- **SOS markers** (rescue calls, "SOS n" labels in the video) and tapping them to send a rescue; zombies/survivors/squads as moving markers.
- **Picking the start site** on the map (still auto `SuggestStart`), **building placement** (outposts, fences, towers, traps), tapping lots to take over / convert / gut.
- **Props**: decode `recovered/assets/3d/props.bin` (Godot's prop meshes: cars, trees `fpine_a`…, rubble) and replace the procedural boxes/cones; hilltop smoke, fires, night lights (`LIGHTS`), day/night tint.
- Not verified without a device: shader compile on the phone GPU (the fallback covers it), build time for about 210 chunks on a phone (several seconds is likely), and the vertex budget. Roughly 2–3 M verts over the whole map, with about a quarter active at the default zoom. Fully zoomed out shows all of it, so lower `MAX_DIST` or thin the window bands if it stutters.
