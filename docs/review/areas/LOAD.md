# LOAD: mod discovery, parsing, loading pipeline and startup performance

Reviewer scope: `src/Core/Util/DivinityModDataLoader.cs` (all 1,826 lines), the other Core utils named in the brief, `src/Core/Models/*.cs`, `src/Core/Models/App/*.cs`, `src/Core/Extensions/*.cs`, the MWVM call sites (SetLoadedMods/MergeModLists, LoadModsAsync, LoadProfilesAsync, BuildModOrderList, LoadModOrder, RefreshAsync, plus the export/import call sites they feed), and the LSLib code the loader relies on (`External/lslib/LSLib/LS/PackageReader.cs`, `PackageCommon.cs`, `PackageFormat.cs`, `Compression.cs`, `BinUtils.cs`, `VFS.cs`, `Mods/ModResources.cs`, `NodeAttribute.cs`, `Resource.cs`, `Resources/LSX/*`), plus CrossSpeak's `ScreenReaderWindows.cs`. Master `fc35358`. Nothing built or run. Library behaviour was checked against downloaded source: .NET 8 `ConcurrentBag.cs` (tag v8.0.0) and Rx.NET `AsyncSubject.cs` (tag rxnet-v6.0.1, the version GUI.csproj:65 pins), saved in `scratchpad/dl-load/`.

Abbreviations: DMDL = `src/Core/Util/DivinityModDataLoader.cs`, MWVM = `src/GUI/ViewModels/MainWindowViewModel.cs`, DMD = `src/Core/Models/DivinityModData.cs`, LS = `External/lslib/LSLib/LS/`.

Already-known defects that I re-verified but do not re-describe in full: AUDIT-PLAN-06 (serial pak loading), -07 (`Files` retained), -08 (full base-game VFS), -09 (goal files read), -12 (quadratic modsettings concatenation), AUDIT-PLAN rows 26-29, S4-final §1. HARV.md did not exist when I finished.

## Map

### Files and responsibilities

| File | Role |
|---|---|
| DMDL | Everything: meta.lsx parsing (`ParseMetaFile` :168), editor projects (:340-427), pak scan (`InternalLoadModDataFromPakAsync` :466-687), pak enumeration and dedupe (`LoadModPackageDataAsync` :745-808), modsettings read (`LoadModSettingsFileAsync` :831), profiles (:878), order JSON read/write (:1080-1207), text/TSV order import (:1209-1329), modsettings write (`ExportModSettingsToFileAsync` :1331, `GenerateModSettingsFile` :1471), dependency expansion (`GetDependencyMods` :1400, `BuildOutputList` :1431), save-game order (`GetLoadOrderFromSave` :1560), SE config (:1654-1725), base game (`LoadModFromModInfo` :1727, `LoadBuiltinModsAsync` :1783) |
| DMD, `DivinityBaseModData.cs` | Mod model; ~40 Rx pipelines per instance built in the constructor (DMD:386-567) |
| `DivinityModVersion2.cs` | Packed 64-bit version; `Empty` is one shared mutable instance (:113) |
| `DivinityLoadOrder.cs` | Order + entries; only `Order` is a `[DataMember]` |
| `DivinityProfileActiveModData.cs` | One `ModuleShortDesc` row of modsettings.lsx |
| `DivinityFileUtils.cs`, `DivinityJsonUtils.cs`, `TempFile.cs`, `DivinityStreamUtils.cs`, `DivinitySaveTools.cs` | File helpers, Newtonsoft with log-and-continue error handler, temp copies, save rename |
| MWVM:1492-1555, 1609-1720, 1727, 1755, 2093, 2340-2558 | Orchestration: RefreshAsync → LoadModsAsync → merge → SetLoadedMods → BuildModOrderList → LoadModOrder |
| LS PackageReader/Common/Format/Compression | LSPK open (memory-mapped), file-table decode, per-entry streams (None = view stream, Zlib = `ZLibStream`, LZ4 = `LZ4DecompressionStream`, Zstd, Solid = `ReadOnlySubstream`) |
| LS VFS + Mods/ModResources | Base-game discovery: attach every non-blacklisted Data pak, build a directory tree, find `Mods/*/meta.lsx` |

### Main flow (call chain)

```mermaid
flowchart TD
  A[RefreshCommand / startup<br/>MWVM:4990, 4145<br/>TaskpoolScheduler.ScheduleAsync] --> B[RefreshAsync MWVM:2340<br/>pool thread]
  B -->|await Observable.Start(.., MainThreadScheduler)<br/>MWVM:2365 or 2374| C[continuation runs INLINE on the Dispatcher<br/>SynchronizationContext = Dispatcher from here on]
  C --> D[LoadModsAsync MWVM:1609]
  D --> E[LoadBuiltinModsAsync DMDL:1783<br/>VFS.AttachGameDirectory + FinishBuild + DiscoverMods<br/>synchronous]
  D --> F[LoadEditorProjectsAsync DMDL:397]
  D --> G[LoadModPackageDataAsync DMDL:745]
  G --> G1[EnumerateFiles recursive + Localization/Data/bin filter :757-764]
  G --> G2[foreach pak: LoadModDataFromPakToBagAsync :781<br/>PackageReader.Read + entry loop run synchronously<br/>until the first LZ4 ReadAsync]
  G2 --> G3[ConcurrentBag, then lazy GroupBy dedupe :787-806]
  D --> H[base-UUID collision → Duplicates.Add(baseMod) MWVM:1695-1702<br/>→ DeleteMods dialog MWVM:1709]
  D --> I[MergeModLists base → paks → projects MWVM:1670-1714]
  C --> J[LoadProfilesAsync → LoadModSettingsFileAsync DMDL:831]
  C --> K[FindLoadOrderFilesInDirectoryAsync DMDL:1147]
  C --> L[Observable.Start: LoadAppConfig, SetLoadedMods MWVM:1492,<br/>BuildModOrderList MWVM:1755 → LoadModOrder MWVM:2093]
  M[Export to game MWVM:2897] --> N[BuildOutputList DMDL:1431 → GetDependencyMods :1400]
  N --> O[ExportModSettingsToFileAsync DMDL:1331 → GenerateModSettingsFile :1471<br/>FileMode.Create in place]
```

### Threading model (confirmed)

1. `RefreshAsync` starts on a pool thread (`RxApp.TaskpoolScheduler.ScheduleAsync`, MWVM:4990 / 4145).
2. Its first await is on `Observable.Start(..., RxApp.MainThreadScheduler)`: `LoadSettings` at MWVM:2365 on F5, or `SetMainProgressTextAsync` at MWVM:2374/2232 on first run. Awaiting an `IObservable` uses Rx's `AsyncSubject` awaiter. That awaiter captured `SynchronizationContext.Current` on the pool thread, which is null, so it runs the continuation **inline** in `OnCompleted`, which fires on the Dispatcher thread (Rx `AsyncSubject.cs:371-401`, rxnet-v6.0.1).
3. From then on, `SynchronizationContext.Current` is the Dispatcher's (WPF sets it while it runs an operation). `src` contains **0** `ConfigureAwait` calls (grep), so every await in LoadModsAsync, the loader and the profile/order steps posts its continuation back to the UI thread.
4. `LoadModPackageDataAsync`'s `foreach` (DMDL:778-782) calls `LoadModDataFromPakToBagAsync` 1,097 times on the UI thread. Each call runs synchronously up to its first await that really yields:
   - `PackageReader.Read` (memory-mapped open plus file-table LZ4 decode, LS/PackageReader.cs:99-129, 274-287);
   - the whole entry loop (DMDL:485-558);
   - reads through `UnmanagedMemoryStream` / `ZLibStream` / Zstd / `ReadOnlySubstream` complete synchronously.
   Only `LZ4DecompressionStream` (LS/Compression.cs:8-64) has no `ReadAsync` override, so base `Stream.ReadAsync` → `BeginRead` decodes that one entry on the pool. Its continuation is then posted back to the Dispatcher.
5. Net effect: file-table decode, the per-entry loop, meta parsing and `DivinityModData` construction (with its Rx pipelines and screen-reader probes) all run on the UI thread, one pak at a time. The only parallel work is single-entry LZ4 decoding. The Dispatcher pumps no messages during the `foreach` itself.

### Duplicate resolution (who wins)

| Stage | Rule | Deterministic? |
|---|---|---|
| Paks vs paks, DMDL:787-806 | Strictly higher `VersionInt` wins. On a tie, the first in `ConcurrentBag` enumeration order wins. A single-thread bag enumerates in **reverse insertion order** (`ConcurrentBag.cs:1025-1047`, "in reverse order"), so the tie goes to the **last pak to complete** | Only if no tied copy yields (see LOAD-05) |
| Base vs paks vs projects, MWVM:1535-1552 | Replace only if strictly higher version; an editor project always wins | yes (list order) |
| SetLoadedMods, MWVM:1510-1520 | strictly higher wins | yes |
| Pak UUID equals a base-game UUID, MWVM:1695-1702 | the **base** mod is added to `Duplicates` and offered for deletion | yes (see LOAD-01) |

### Cost model at the owner's scale (inferred; to be replaced by S4 P1/T1 numbers)

| Step | Where | Today | Notes |
|---|---|---|---|
| Base-game VFS | DMDL:1789-1803 | 0.5-2.5 s, plus a 100-300 MB transient | ~29 of 48 Data paks attached; every entry inserted into a tree with two substring allocations per path segment (LS/VFS.cs:204-223) |
| User paks | DMDL:757-806 | most of the 9.27 s | serial on the UI thread; `Files` retained (LOAD-11); goal reads (AUDIT-PLAN-09) |
| Dedupe | DMDL:788-806 | 50-150 ms, ~125 MB garbage | 1,121 full `GroupBy` re-executions (LOAD-13) |
| Screen-reader probe | DMD:521-524 | ~3,900 calls per refresh, ms unknown | LOAD-10 |
| modsettings read | DMDL:964-979 | ~548 KB read twice, parsed on the UI thread | one profile step |

## Findings

### [High] LOAD-01: A user pak that reuses a base-game UUID makes the app offer the game's own pak for deletion, preselected

- **Location:** MWVM:1695-1710; `src/Core/Models/ModFileDeletionData.cs:11-22`; `src/GUI/ViewModels/DeleteFilesViewData.cs:54-100`; DMDL:1736-1755.
- **What:**
  ```csharp
  if (baseModsDict.TryGetValue(pakMod.UUID, out var baseMod) && !baseMod.IsEditorMod)
      modLoadingResults.Duplicates.Add(baseMod);   // the BASE mod, not pakMod
  ...
  DeleteMods(modLoadingResults.Duplicates, true, modLoadingResults.Mods);
  ```
  - `baseMod.FilePath` is `modInfo.PackagePath`, which is the absolute path of the Data pak holding that meta (`VFS.GetPackagePath` → `Package.PackagePath`, LS/VFS.cs:302-310; DMDL:1736, 1755). Example: `...\Baldurs Gate 3\Data\Game.pak` or `Gustav.pak`.
  - `ModFileDeletionData.FromMod` sets `IsSelected = true`. The dialog opens automatically, titled "Duplicate Mods to Delete".
  - In Release, `DeleteFilesViewData.Run` calls `RecycleBinHelper.DeleteFile(f.FilePath, ...)` with no check that the path is under the Mods folder (:84-95). Debug builds only pretend to delete (`#if DEBUG`), so the owner's Debug baselines would never show this.
- **Failure scenario:**
  1. A pak ships a copy of `Mods/GustavX/meta.lsx`, or `Mods/Gustav`, `Shared` and so on. DMDL:581 documents such multi-meta paks ("for overriding NumPlayers").
  2. Its own meta is not chosen, because the pak was renamed and `pakName.Contains(folder)` fails, so the first meta wins. "Mods/GustavX" sorts before most folder names.
  3. Alternatively, the author copied a Larian UUID.
  4. On load the app shows "1 duplicate mod(s) found" plus the delete dialog, with `Data\Game.pak` preselected.
  5. One click on Delete and one confirm recycle the base-game pak. The game is broken until the user restores it from the Recycle Bin or Steam/GOG verifies the install (a multi-GB re-download if "permanently delete" was ticked).
- **Evidence:** cited lines; `git log` attributes the block to ebc0a66 "Fix loading duplicate base mods, add warning".
- **Fix:**
  1. Add `pakMod`, not `baseMod`, and explain the reason in the dialog: "this pak claims the UUID of base module X".
  2. Defence in depth in `DeleteFilesViewData.Run` and `ModFileDeletionData.FromMod`: refuse any path not under `PathwayData.AppDataModsPath` and any `IsLarianMod` mod.
  3. Never preselect a base collision.
  Upstreamable `fix/*`.
- **Confidence:** Confirmed (code path). How often a real pak triggers it is Plausible: confirm with a TEMP log of `pakMod.UUID ∈ baseModsDict` over the owner's set.

### [High] LOAD-02: "Export to game" crashes the process with a stack overflow on a self- or circular dependency

- **Location:** DMDL:1400-1429 (`GetDependencyMods`), called from `BuildOutputList` DMDL:1438-1454, from MWVM:2931 and 2326.
- **What:**
  - The function recurses into every dependency before checking `addedMods`, and nothing tracks which mods are already being visited.
  - `ParseMetaFile` keeps a mod's own UUID if it lists itself under Dependencies (DMDL:259-273). `SetLoadedMods` does not flag it as missing, because the UUID is in `uuids` (MWVM:1525-1530).
  - `AutoAddDependenciesWhenExporting` defaults to true (`DivinityModManagerSettings.cs:67-69`).
- **Failure scenario:** mod A's meta.lsx lists A (a copy-paste slip seen in the wild), or A lists B and B lists A (compat patches). Put A in the active order and press Export: `GetDependencyMods(A)` → `GetDependencyMods(A)` → … → `StackOverflowException`. That cannot be caught, so the process dies with no dialog. Every export fails until the user finds the mod. modsettings.lsx is not touched, because the crash comes before the write.
- **Evidence:** cited lines; no visited set; no `Dependencies.Remove` anywhere except `MissingModResults` (grep).
- **Fix:** pass a `visiting` set and skip any UUID already in `addedMods ∪ visiting` before recursing. Drop self-entries in `ParseMetaFile`. Report cycles in the missing-dependency UI. Upstreamable `fix/*`. Other recursive dependency walkers in the GUI (outside this scope) should be checked for the same pattern.
- **Confidence:** Confirmed.

### [High] LOAD-03: All mod loading runs serially on the UI thread; Task.WhenAll gives almost no parallelism (S4 claim (a): confirmed)

- **Location:** MWVM:2365/2374 (the first await resumes on the Dispatcher), 2375, 1627, 1665; DMDL:778-784, 1789-1803; zero `ConfigureAwait` in `src`.
- **What:** see Map → Threading model.
  - The confirmation rests on Rx's awaiter semantics (`AsyncSubject.cs:371-401`): a null captured context means the continuation runs inline on the completing (Dispatcher) thread.
  - The only real parallelism is single-entry LZ4 decoding, because `LZ4DecompressionStream` lacks `ReadAsync`. Everything else, including the base-game VFS build (synchronous before `LoadBuiltinModsAsync` returns), runs on the UI thread.
- **Failure scenario:**
  - The 9.27 s "loading mods" step uses about one core.
  - The window stops pumping messages for the whole `foreach` over 1,097 paks. Windows ghosts it as "Not Responding" after 5 s, and the progress bar freezes.
  - The 30 s builtin timeout is not observed during the VFS build.
  - It also makes the tie winner timing-dependent (LOAD-05).
- **Evidence:** cited lines; plan baseline (24.2 s CPU over 15.2 s wall shows the parallelism is Rx/GC/UI, not loading).
- **Fix (S4-final P5+P6):**
  1. Memoise the screen-reader probe first (LOAD-10).
  2. Capture paths on the UI thread.
  3. `Task.Run` the builtin, editor and pak loads together.
  4. Use bounded `Parallel.ForEachAsync` (DOP ≈ `Math.Clamp(ProcessorCount-1,2,8)`), and write results into an array indexed by enumeration order instead of a `ConcurrentBag`.
  5. Add `ConfigureAwait(false)` throughout the Core loader.
  6. Guard `_AllPaksNames` (written during enumeration only, fine) and keep all `IgnoredMods` reads read-only.
  Expected: cold LoadModsAsync ~2.5-4.5 s on SSD, UI responsive (S4 estimate, unmeasured).
- **Confidence:** Confirmed (source plus library source); the size of the gain is Plausible (W2/W11).
- **knownAs:** AUDIT-PLAN-06; S4-final §1 item 1. The added value here is the exact resume point (MWVM:2365/2374), the Rx mechanism, and that the profile and order steps are also on the UI thread.

### [Medium] LOAD-04: Any pak whose full path contains "Localization" (case-sensitive) is silently never loaded

- **Location:** DMDL:459-464, 761.
  ```csharp
  "Baldur's Gate 3\\Data", "Baldur's Gate 3\\bin", "Localization",
  ... name.EndsWith(".pak", SCOMP) && !_IgnoredRecursiveFolders.Any(x => f.Contains(x))
  ```
- **What:**
  - `f` is the full path, including the file name. The intent was to skip copied game folders, but `"Localization"` matches any file name such as `SomeMod_Localization_RU.pak`, `ChineseLocalization.pak`, or any subfolder named Localization.
  - The match is ordinal and case-sensitive, so `localization` passes.
- **Failure scenario:**
  - A translation mod with a meta.lsx (one that needs a load-order slot) is in modsettings.lsx because it was enabled in-game or by another tool. BG3MM never lists it, and reports it as a missing mod although the file is in the Mods folder.
  - The next Export drops it from modsettings.lsx, because `BuildOutputList` skips entries with no loaded mod (DMDL:1446-1465). The translation silently turns off.
  - The same filter will hide any future library or hard-link source whose path contains `Baldur's Gate 3\Data` or `Localization` (relevant to LIB-final).
- **Evidence:** cited lines.
- **Fix:** match path *segments* relative to the Mods root, for example `Path.GetRelativePath(modsRoot, f).Split(sep)` containing a directory named `Localization`, case-insensitive. Never test the file name. Better still, drop the recursive scan (see LOAD-21).
- **Confidence:** Confirmed (code). How many affected paks the owner has: run `Get-ChildItem "$env:LOCALAPPDATA\Larian Studios\Baldur's Gate 3\Mods" -Recurse -Filter *.pak | ? FullName -clike '*Localization*'`.

### [Medium] LOAD-05: Equal-version duplicates: the winner is the last pak to complete, timing-dependent for LZ4 paks, and the loser is preselected for deletion (S4 claim (e): partly confirmed)

- **Location:** DMDL:773-806; MWVM:1703-1710; .NET `ConcurrentBag.cs:1025-1047`.
- **What:**
  - Every `Add` runs on the UI thread (LOAD-03), so the bag has one work-stealing queue, and `ToList` returns it in **reverse insertion order**.
  - `if (m.Version.VersionInt > existing...)` keeps the first seen on a tie, so the **last-completed** copy wins.
  - For a pak whose goal and meta reads complete synchronously (stored, zlib or zstd entries), completion order is enumeration order, which on NTFS is alphabetical. That case is deterministic, and the alphabetically last path wins.
  - For LZ4 paks (the LSLib and toolkit default; the app's own `CreatePackageAsync` uses LZ4, `DivinityFileUtils.cs:150`), completion depends on when the pool finishes the decode and when the Dispatcher runs the posted continuation. That case is timing-dependent.
  - So "effectively random" is right for the common case but wrong for non-LZ4 paks, and the tie rule is "last completed", not "first".
- **Failure scenario:**
  1. BG3 authors often never bump `Version64` (the default 1.0.0.0 = 36028797018963968).
  2. The user has `ModX.pak` (old build) and `ModX (1).pak` (the update, saved by the browser), same UUID and same version.
  3. Which build is listed, and exported (its Folder and MD5), can change between launches.
  4. The duplicate dialog then preselects the other one for deletion. Half the time that recycles the newer build.
- **Fix:**
  - Deterministic tie-break: higher `VersionInt`, then newer `LastWriteTimeUtc`, then ordinal path.
  - Keep results in an enumeration-indexed array.
  - Do not preselect ties in the dialog; show both mtimes.
  - S4-final P3's "first in enumeration order" rule is deterministic but keeps the alphabetically first file, which is the old copy in the example above. Prefer mtime.
- **Confidence:** Confirmed (source plus .NET source). Whether the owner hits it is Plausible (S4 W9 `## ties`).
- **knownAs:** S4-final claim (e) / P3.

### [Medium] LOAD-06: One non-GUID UUID (or a version ≥ 2^63) in the exported order makes modsettings.lsx unreadable to BG3MM itself, so the Current order loads empty

- **Location:** writer `DivinityApp.cs:33` (`type="guid"` and `type="int64"` for every row), DMDL:1471-1487; reader DMDL:831-876 → LS `Resources/LSX/LSXReader.cs:149-169` → `NodeAttribute.ParseFromString` (`new Guid(str)`, `Convert.ToInt64(str)`, NodeAttribute.cs:430-447); catch at DMDL:842-845.
- **What:**
  - The app knowingly lets a mod with a malformed UUID into the order: `HasInvalidUUID` shows "This mod has an invalid UUID, and will likely fail to load, resetting your load order" (MainResourceDictionary.xaml:684).
  - It then writes that UUID with `type="guid"`.
  - On the next read, `new Guid("…")` throws `FormatException`. LSXReader aborts the whole resource, `LoadResourceAsync` returns null, and `ActiveMods` is empty.
  - The same happens when `VersionInt > long.MaxValue`, which needs Major ≥ 256, because `Convert.ToInt64` overflows.
- **Failure scenario:**
  1. Export once with such a mod active.
  2. After an F5 or a restart, "Current" shows zero active mods.
  3. `CheckForEmptyOrderAsync` (MWVM:2283) offers to restore LastExported. That order contains the same mod, so it loops.
  4. If the user then rebuilds and exports, the previous order is gone.
- **Fix:** in `BuildOutputList`, skip entries whose UUID fails `Guid.TryParse`, and show an alert naming them. Clamp or skip versions above `long.MaxValue`. In `LoadModSettingsFileAsync`, fall back to a tolerant `XDocument` row-by-row parse when LSX parsing fails, so one bad row cannot blank the order.
- **Confidence:** Confirmed (BG3MM side). Whether the *game* also rejects the file needs a Windows run.

### [Medium] LOAD-07: Pak and meta load failures are invisible; the dropped mods are then silently removed from modsettings.lsx on the next export

- **Location:**
  - DMDL:320-323: `ParseMetaFile` catch uses `Console.WriteLine`, so the error is never logged even with logging on.
  - DMDL:674-684 ("No meta.lsx" or "Failed to parse" go to the log only).
  - DMDL:707-710 (`LoadModDataFromPakToBagAsync` catch).
  - DMDL:842-845, 1137-1140, 1177-1180, 1819-1822.
  - DMDL:1446-1465 (missing entries dropped on export).
- **What:** an unsupported LSPK version (a future patch, v19 for example), a truncated download, a locked file or malformed XML all make the pak vanish from the lists. Logging is off by default, and no alert is shown. A failed meta parse also drops a pak that overrides base files, so the user cannot see an override that is still changing their game.
- **Failure scenario:** after a BG3 patch changes the pak format, 40 of 1,097 paks fail to load. The user sees only "missing mods" for those that were active, with no reason given. Export writes modsettings without them.
- **Fix:**
  - Collect failures in `ModLoadingResults.Errors` (path plus a short reason).
  - Show one Warning alert with the count, linking to a list.
  - Route `ParseMetaFile` errors through `DivinityApp.Log` with the pak path.
  - On export, if any active entry is unresolved because of a *load failure* (not a genuinely absent file), ask before writing.
  This matches the owner's rule "every failure must surface in the UI".
- **Confidence:** Confirmed.

### [Medium] LOAD-08: modsettings.lsx and order JSON are overwritten in place with no backup; the LastExported "backup" is written even when the export failed

- **Location:** DMDL:1348-1350 (`FileMode.Create` on the live modsettings.lsx), 1098-1114 and 1080-1096 (order JSON), 1392-1395 (launcher prefs); MWVM:2931-2934 (`BackupCurrentLoadOrderAsync` runs regardless of `result`).
- **What:**
  - The file is truncated first, then ~550 KB is written in one `WriteAsync`.
  - There is no temp-then-rename, no `Flush(true)`, and no `.bak` of the previous modsettings.
  - `LastExported.json` holds the *new* order and is written even when the write failed.
- **Failure scenario:** an AV lock, disk full, crash or power loss during the write leaves a truncated or empty modsettings.lsx. The next start loads an empty Current order (LOAD-06 path), and the game resets the mod list. The only copy of a 935-entry order may then be LastExported, which on a failed export holds an order that was never written to modsettings.
- **Fix:** write `modsettings.lsx.tmp` in the same folder, `Flush(true)`, then `File.Replace(tmp, final, final + ".bak")` (or `File.Move(tmp, final, true)` after copying the old file to `.bak`). Apply the same to order JSON and `preferences.json`. Only run `BackupCurrentLoadOrderAsync` when `result` is true. Upstreamable.
- **Confidence:** Confirmed (code). The truncation window is small; the impact is a lost load order.

### [Medium] LOAD-09: Meta-less override paks that touch only ignored paths get Folder = null, so text/TSV order export and .txt/.tsv import throw NullReferenceException

- **Location:** DMDL:564-567, 613-621 (`Folder = builtinModOverrides.FirstOrDefault().Key`, which is null when every builtin hit was under `IgnoreBuiltinPath`); `DivinityModData.OutputPakName` (DMD:24-37) and `DivinityBaseModData.PakEquals` (:118-127) call `Folder.Contains(UUID)`; MWVM:3803 (`exportMods.AddRange(ForceLoadedMods)`), 3757/3768; DMDL:1254, 1292.
- **What:**
  - A pak with only `Public/Game/GUI/Assets/...` files and no meta is classed force-loaded with `Folder == null`. Icon and UI reskins are commonly like this.
  - "Export order to text/TSV" includes every force-loaded mod, and `ModToTSVLine` evaluates `OutputPakName` outside the try (the try at MWVM:3824-3832 covers only `WriteAllText` at :3826).
  - `LoadOrderFromFile` .txt/.tsv runs `allMods.FirstOrDefault(x => x.PakEquals(...))`, which throws on the first such mod it reaches.
- **Failure scenario:** the owner exports the order as TSV. An exception dialog appears and no file is written. Importing a .txt list throws in the same way.
- **Fix:** set `Folder = ""` (or the pak name) when there is no override key. Null-guard `OutputPakName` and `PakEquals`.
- **Confidence:** Confirmed. That such a pak exists in the owner's set is Plausible.

### [Medium] LOAD-10: The screen-reader probe runs 3-4 times per mod and re-runs CrossSpeak initialisation every time when no reader is running (S4 claim (b): mechanism confirmed, "reloads Tolk" unverifiable)

- **Location:** DMD:521-524; `DivinityApp.cs:124-127`; `src/Core/AppServices/ScreenReaderService.cs:24-31, 49-80`; `External/CrossSpeak/CrossSpeak/ScreenReaderWindows.cs:23-41`.
- **What:**
  - `WhenAnyValue(Description, HasDependencies, UUID)` emits:
    - once at construction;
    - once for the `UUID` set in `ParseMetaFile`'s initializer;
    - once for `Description`;
    - once more when `HasDependencies` turns true via the `ObserveOn(MainThreadScheduler)` count.
    That is ~3.5 probes × 1,121 ≈ 3,900 per refresh, on whatever thread builds the mod (the UI thread today).
  - With no reader running, `isLoaded` stays false, so every call runs `Initialize()`, which means `Tolk.Load()` plus `Tolk.DetectScreenReader()`.
  - Whether native `Tolk_Load` re-initialises is inside `Tolk.dll`. No source is in the repo, and upstream Tolk returns early when already loaded, so "reloads the library" is probably wrong.
  - Either way, each call still runs a full detection sweep: NVDA RPC probe, window-class lookups, COM probes.
- **Failure scenario:** an estimated 0.1-1 ms per sweep gives 0.4-4 s per refresh on the UI thread. It must be memoised before construction moves off the UI thread (Tolk/COM on pool threads).
- **Fix:** S4-final P5. Probe once per refresh on the UI thread and cache the bool. `ScreenReaderService` should also cache a negative result for N seconds.
- **Confidence:** Confirmed (call count and code path); cost Plausible (W3).
- **knownAs:** S4-final §1 item 4 / P5.

### [Medium] LOAD-11: Every pak's file list is retained in DivinityModData.Files and never read (S4 claim (c): confirmed)

- **Location:** DMDL:478, 488, 628; DMD:234. The only writer is DMDL:628, and there are no readers in `src` (grep `.Files`, `Path=Files` in XAML: none).
- **What, and estimated cost:** E entries × (path string ~180 B kept alive, plus ~30 B HashSet slot). At an assumed ~500 entries per pak, that is ~550 k entries, or **~60-120 MB** of the 478 MB, plus one hash insert per entry on the UI thread.
- **Fix:** delete the field and the three lines (S4 P4). Measure private bytes before and after (W12).
- **Confidence:** Confirmed unused; size Plausible.
- **knownAs:** AUDIT-PLAN-07.

### [Medium] LOAD-12: The base-game scan builds a full VFS over every non-blacklisted Data pak to find ~30 meta.lsx files (S4 claim (d): confirmed)

- **Location:** DMDL:1789-1803; LS/VFS.cs:93-117, 173-223; LS/PackageReader.cs:149-177.
- **What:**
  - `AttachGameDirectory` fully reads the file table of every Data and Localization pak not in the 19-name blacklist (the owner's install has 48 Data paks: plan §0.3). That includes Game, Gustav, Shared, the DiceSets and the language pak.
  - `FinishBuild` then walks every entry. `TryAddFile` evaluates `path[..endPos]` **and** `path[namePos..endPos]` for every segment of every path, even when the directory already exists (VFS.cs:214).
  - So a 200 k-entry set at depth ~6 allocates roughly 2 M short strings (~100-300 MB transient), on the UI thread. The 30 s token is not observed there.
- **Fix:**
  - Read each pak's table directly and probe for `Mods/<x>/meta.lsx` without a VFS, honouring `Package.Metadata.Priority` for shadowing.
  - Or cache the result keyed on the Data pak set (S4 P9).
  - In LSLib, `TryAddFile` should look the directory up by span before allocating (a third repo).
- **Confidence:** Confirmed (code); cost Plausible (W4).
- **knownAs:** AUDIT-PLAN-08.

### [Low] LOAD-13: Duplicate detection re-runs a full GroupBy for every mod

- **Location:** DMDL:788, 805-806.
- **What:**
  - `dupes` is a lazy `GroupBy(...).Where(...).SelectMany(...)`.
  - `mods.Where(x => !dupes.Contains(x))` calls `Enumerable.Contains` on a non-`ICollection`, so each of 1,121 calls re-groups all 1,121 mods: ~1.26 M hash inserts and ~1,121 Lookups of ~110 KB each.
  - That comes to ~**125 MB of garbage and 50-150 ms**.
  - It also re-evaluates the enumeration at line 805.
- **Fix:** materialise once: `var dupeGroups = mods.GroupBy(x => x.UUID).Where(g => g.Count() > 1).ToList(); var dupeSet = dupeGroups.SelectMany(g => g).ToHashSet(ReferenceEqualityComparer.Instance);`. Combine with the LOAD-05 tie rule.
- **Confidence:** Confirmed.
- **knownAs:** S4-final P3 / §7 item 7.

### [Low] LOAD-14: ParseMetaFile double-escapes valid XML, so numeric character references show literally and entity text survives in Folder, UUID and MD5

- **Location:** DMDL:127-166, 172, 209-234.
- **What:**
  - `EscapeXmlAttributes` runs `EscapeXml` on every `value="…"`, so valid entities become `&amp;#xA;`, `&amp;amp;` and so on.
  - After parsing, only Name, Description and Author pass through `UnescapeXml`, which knows five named entities and `<br>` but **no numeric references**.
  - LSLib's own LSX writer (`XmlWriterSettings` default `NewLineHandling.Replace`, LSXWriter.cs:22-25, 145) writes newlines in attributes as `&#xA;`.
  - Also, `.` in the regex does not cross a newline, so a raw `&` in a multi-line value is not repaired, and the whole meta fails to parse (then LOAD-07).
- **Failure scenario:** a Divine-repacked mod's tooltip shows "Line one&#xA;Line two". A Folder written as `A&amp;B` is stored as the literal text `A&amp;B`. It round-trips into modsettings only because `GenerateModSettingsFile` also leaves Folder unescaped (LOAD-15).
- **Fix:** try `XElement.Parse(metaContents)` first, and use the repair pass only on failure. Then drop `UnescapeXml` for parsed values (keep it only for `<br>`).
- **Confidence:** Confirmed (mechanism). How many metas contain `&#xA;` is Plausible.

### [Low] LOAD-15: The modsettings writer escapes only Name; any XML-invalid Folder, MD5 or UUID text, or a control character, aborts export with a misleading "Is the file locked?"

- **Location:** DMDL:1471-1487 (only `SecurityElement.Escape(mod.Name)`); DMDL:1340-1341 (`LoadXml` throws `XmlException`, caught at 1358); MWVM:2988 (message).
- **What:**
  - `Folder` comes from an invalid-XML meta that the repair pass fixed: `value="A&B"` → parsed `A&B`. Written raw, it makes `LoadXml` throw.
  - `SecurityElement.Escape` does not strip XML-1.0-invalid control characters, so a Name containing `\u0001` also throws.
  - `GenerateModSettingsFile` runs *outside* the try (DMDL:1336), so a null `Version` would escape as an exception. That cannot happen today, because `Version` defaults to `Empty`.
- **Fix:** build the document with `XElement`/`XmlWriter` (escaping every attribute) and strip invalid chars. Report the offending mod by name. Use a `StringBuilder` too (AUDIT-PLAN-12).
- **Confidence:** Confirmed (mechanism); a rare trigger.

### [Low] LOAD-16: The partial-pak filter uses a static name set that is never cleared and ignores folders

- **Location:** DMDL:429-446, 763-764.
- **What:** `_AllPaksNames.UnionWith(...)` accumulates across refreshes. `Foo_1.pak` is skipped if `Foo.pak` was *ever* seen, in *any* subfolder.
- **Failure scenario:**
  - Delete `Foo.pak`, keep a genuine mod named `Foo_1.pak`, press F5: it stays hidden until restart.
  - A real mod `Bar_2.pak` next to an unrelated `Bar.pak` in another subfolder is always hidden.
- **Fix:** use a local set per call, keyed by directory plus base name. Better, open the main pak header and skip only the `_N` parts it declares (`NumParts`).
- **Confidence:** Confirmed.
- **knownAs:** S4-final §4.4 lists this as an untouched hazard.

### [Low] LOAD-17: `new DivinityModData(isBaseGameMod: true)` computes IsHidden before UUID is set, so every IgnoredMods entry, the main campaign included, is hidden

- **Location:** DMD:514-518 (the ctor reads `UUID`, which is still ""); MWVM:4746 (the only caller); MWVM:5275 (`AdventureMods` filters `!IsHidden`).
- **What:** latent. On the fallback path (game path unset, or the VFS failed or timed out at 30 s), base mods come from IgnoredMods. GustavX is then hidden, and it also has no `Type`, so it is never in AdventureMods. Export still falls back to `mods.Lookup(GUSTAVX_UUID)` (MWVM:2905-2924).
- **Fix:** compute `IsHidden` after the UUID is assigned, in `LoadAppConfig`, or make it a WhenAnyValue.
- **Confidence:** Confirmed; low impact.

### [Low] LOAD-18: Small parse slips in dependencies and profile rows

- **Location:** DMDL:269, 296; `DivinityProfileActiveModData.cs:57`; DMDL:83-94.
- **What:**
  - Dependency and conflict `PublishHandle` is read from `moduleInfoNode` (the parent mod), not from `node`.
  - `DivinityProfileActiveModData.Version` reads attribute `"Version"`, but modsettings has `Version64` (int64, boxed as `long`, never `ulong`), so it is always 0.
  - `GetAttributeValueWithId(moduleInfoNode, ["Version64","Version"])` searches all descendants, so a meta whose ModuleInfo uses the 32-bit `Version` gets its **PublishVersion child's** `Version64` instead.
  - 32-bit DOS2-style versions are decoded with the 64-bit layout (only `1` and `1<<28` are special-cased, DivinityModVersion2.cs:62-66).
- **Fix:** use `node`; read `Version64` as `long`/`ulong`; restrict lookups to direct child attributes (`node.Elements("attribute")`); detect the 32-bit layout when the attribute is `Version` (int32).
- **Confidence:** Confirmed; low impact today.

### [Low] LOAD-19: Load-order JSON discovery: dead settings.json filter and a dereference before the null check

- **Location:** DMDL:1157-1169.
- **What:**
  - `!f.Equals("settings.json", SCOMP)` compares a *full path*, so it never excludes anything.
  - `order.Name = …` runs before `if (order != null)`: an empty or `null` JSON file throws NRE. That is caught, but the file is skipped without explanation.
  - Recursive, so any JSON in Orders subfolders becomes an order, and with the log-and-continue serializer, unrelated JSON becomes an empty order (cf. AUDIT-PLAN-10).
- **Fix:** compare `Path.GetFileName(f)`; null-check first; accept only objects with a non-null `Order`.
- **Confidence:** Confirmed.

### [Low] LOAD-20: Latent helpers that would destroy data if wired up: RenameSave, TempFile.CreateAsync(path), IgnoreFile

- **Location:** `src/Core/Util/DivinitySaveTools.cs:113-121`; MWVM:4040-4065; `TempFile.cs:21-36`; `DivinityFileUtils.cs:119-130`.
- **What:**
  - `RenameSave` builds a `PackageBuildData` with **no Files** and writes it, so the result is an empty `.lsv`. On success the caller recycles the *original* save (MWVM:4063) and moves its folder. `RenameSaveCommand` (MWVM:5129) is bound in no XAML (grep), so it is unreachable today.
  - `TempFile.CreateAsync(sourcePath, token)` opens `_path` (the temp file itself, `FileShare.None`) instead of `_sourcePath`. It has no callers.
  - `IgnoreFile`'s extension rule `ignoredFileName.Substring(0) == "."` is never true, so `.log`, `.dmp`, `.ailog` and `.debugInfo` are packed into editor-mod paks by `CreatePackageAsync`. That path *is* live (zip export of editor mods).
- **Fix:**
  - Delete `RenameSave` or fix it to copy `package.Files`.
  - Fix `CopyAsync` to read `_sourcePath`.
  - Use `ignoredFileName.StartsWith('.')`.
- **Confidence:** Confirmed.

### [Low] LOAD-21: The Mods folder is scanned recursively; the game is believed to load only top-level paks (Plausible)

- **Location:** DMDL:757 (`DivinityFileUtils.RecursiveOptions`); DivinityFileUtils.cs:12-17.
- **What:** paks in subfolders (for example a user's "disabled" or "backup" folder) are listed, can be activated, and are exported to modsettings. If the game does not load subfolder paks, those mods are "active but not working". The scan also feeds the zip-entry-name collision noted in AUDIT-PLAN row 8, and LOAD-16.
- **Fix:** after a Windows check, scan `TopDirectoryOnly`, or label subfolder paks "not deployed". This matters for LIB-final, whose library lives outside Mods anyway.
- **Confidence:** Plausible. It needs a Windows run: put a known mod only in `Mods\sub\` and check in-game.

### [Info] LOAD-22: Design claims in S4-final that the code corrects

- **Location:** `docs/review/stages/S4-final.md` §1 items 1, 4; P3.
- **What:**
  - (a) "one pak at a time on the UI thread": confirmed. But the parallelism is not literally zero: single-entry LZ4 decodes run on the pool via base `Stream.ReadAsync`, and *that* is what makes tie order timing-dependent.
  - (b) "reloads the Tolk library": not supported by the source. CrossSpeak calls `Tolk.Load()` every time, but whether native Tolk re-initialises is not visible. The confirmed per-call cost is a full `DetectScreenReader` sweep.
  - (e) "winner effectively random": partly. It is deterministic (the alphabetically last path) when the tied paks' reads complete synchronously, and timing-dependent for LZ4 paks. The rule is "last completed wins", not "first in bag order" in the sense S4 implies.
  - S4's P3 tie rule ("first in enumeration order") would make the alphabetically first copy win, which is usually the *older* download. Use newest mtime (LOAD-05).
- **Fix:** amend S4-final §1 and P3.
- **Confidence:** Confirmed.
- **knownAs:** S4-final §1 items 1, 4; P3.

### [Info] LOAD-23: CLAUDE.md §6: "UUID = <full pak path> … That's why HasInvalidUUID exists" is inaccurate

- **Location:** DMD:367-374, 446-448.
- **What:** `CheckForInvalidUUID` returns false unless `CanAddToLoadOrder`. Meta-less override paks are force-loaded and not merged, so `CanAddToLoadOrder` is false, and **HasInvalidUUID never fires for path-UUIDs**. It flags orderable mods whose meta UUID is malformed, which are exactly the ones that trigger LOAD-06. AUDIT-CLAUDE row 6.2 marked the claim correct.
- **Fix:** reword CLAUDE.md §6: "HasInvalidUUID flags orderable mods whose meta UUID is not a GUID; such a mod still gets exported (see LOAD-06)."
- **Confidence:** Confirmed.
- **knownAs:** CLAUDE.md section 6; AUDIT-CLAUDE 6.2.

## Opportunities aligned with the owner's intentions

- **One `fix/export-safety` slice (upstreamable, Core plus a few MWVM lines):**
  - the cycle-safe `GetDependencyMods` (LOAD-02);
  - an invalid-UUID and version pre-flight in `BuildOutputList` with an alert (LOAD-06);
  - atomic modsettings write plus `.bak` (LOAD-08);
  - `XmlWriter`-based generation (LOAD-15, AUDIT-PLAN-12);
  - running `BackupCurrentLoadOrderAsync` only on success.

  These are small, testable without the game (unit tests on `BuildOutputList` and `GenerateModSettingsFile`), and they protect R1.
- **`fix/duplicate-handling`:** LOAD-01 (never offer non-Mods paths for deletion; add `pakMod`), LOAD-05 (version → mtime → path tie-break, no preselection on ties) and LOAD-13. This supersedes S4 P3's tie rule.
- **`fix/pak-discovery`:** segment-based exclusion (LOAD-04), a per-call partial set (LOAD-16), and, after the Windows check, a non-recursive scan (LOAD-21). This also de-risks LIB-final, whose deployment folder must never contain `Localization` or `Baldur's Gate 3\Data` in its path under today's filter.
- **Surfacing failures (owner rule: every failure must surface in the UI):** `ModLoadingResults.Errors`, plus one alert after load (LOAD-07).
- **Perf (S4 series, confirmed):** P5 probe memo (LOAD-10) → P6 off-UI-thread plus parallel scan (LOAD-03) → P4 drop `Files` (LOAD-11) → the base-game probe or cache (LOAD-12). With P6, results must go into an enumeration-indexed array (not a bag) so that dedupe stays deterministic.
- **Linux kept open:** the Windows-only separators in `_IgnoredRecursiveFolders` (`"Baldur's Gate 3\\Data"`, DMDL:461-462) and `PlayerProfilePathPattern` (`PlayerProfiles\\(.*?)\\Savegames`, DMDL:39) silently stop matching on Linux. Use `Path.DirectorySeparatorChar`-agnostic segment checks.

## Needs a Windows run to settle

1. W2: thread id during the `foreach` at DMDL:778, and whether the window ghosts as "Not Responding" during "Loading mods from Local AppData folder…" (LOAD-03).
2. W3: Stopwatch around `DivinityApp.IsScreenReaderActive()`, giving call count and total ms with and without NVDA (LOAD-10).
3. Does BG3 load paks from `Mods\<subfolder>\`? (LOAD-21)
4. Does the game reject or reset a modsettings.lsx containing one `type="guid"` row with a non-GUID value? (LOAD-06)
5. A TEMP scan over the owner's 1,097 paks for:
   - paks with `Localization` in the path (LOAD-04);
   - metas listing their own UUID or forming a cycle (LOAD-02);
   - pak UUIDs that equal a base-module UUID (LOAD-01);
   - equal-version UUID ties and how many are LZ4 (LOAD-05);
   - metas containing `&#` (LOAD-14);
   - meta-less paks touching only `Game/GUI/Assets` (LOAD-09);
   - total entry count E (LOAD-11).
6. W4/W12: `Builtin.Attach/FinishBuild` ms and allocation, and private bytes with and without `Files` (LOAD-11, LOAD-12).
7. A Release build check of LOAD-01: run against a copied game folder only, never the real install.
