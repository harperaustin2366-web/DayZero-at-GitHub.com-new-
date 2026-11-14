# Unity port notes: data.gd and regions.gd

Data loading: JSON is loaded with `Resources.Load<TextAsset>` from `Assets/Resources/Data/*.json` and `Assets/Resources/Map/{city,roadgraph}.json`. If a file isn't there, `DataLoader` falls back to `Assets/StreamingAssets/<path>.json`, using File IO on desktop and a synchronous `UnityWebRequest` on Android jar: paths.
The `data/*.json` files are copied unchanged into **both** `StreamingAssets/Data` and `Resources/Data`. Delete one copy before shipping, or the build carries both.

Package: `com.unity.nuget.newtonsoft-json` 3.2.1 (in Packages/manifest.json).

## Stubbed
- `IForestGen` (forest.gd). Plug it in with `Data.ForestGenFactory = d => new ForestGenImpl(d)`. Until it's set, `SetWorldSeed` skips the forest pass and logs a warning.
- `ForestGen.H32/Hf` is fully ported because regions.gd needs it. It matches the GDScript 64-bit math.
- `IRng` replaces RandomNumberGenerator. `SystemRng` does **not** reproduce Godot's PCG32 sequence.

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
