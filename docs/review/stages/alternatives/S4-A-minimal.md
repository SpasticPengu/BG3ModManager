# Stage 4, Perf A: startup load. Design angle A-minimal

Branch: `perf/startup-load`, cut from a clean `master` (fc35358). Everything is in `src/Core` except
two small call-site edits and the snapshot hook in `MainWindowViewModel.cs`. There are no LSLib
(submodule) changes and no fork-only dependencies.

Evidence rules used here: **[certain]** means read in the source at the cited path:line.
**[inferred]** means reasoned from the source or framework behaviour but not measured; each one
comes with the log line that would confirm or kill it. Nothing in this document was built or run
(the container cannot build WPF).

---

## 0. Summary

**Headline.** The 9.27 s is almost certainly the per-pak `PackageReader.Read` plus a walk over every
file entry of every pak, done **serially, and very probably on the UI thread**, followed by a
dedupe pass. The design keeps the loader's semantics byte for byte. It does this by splitting pak
loading into a *scan* (pak → plain facts, independent of the base-game mod list) and a *build*
(facts + the current base-game list → `DivinityModData`). It then persists the scan facts in
`Data/modcache.json`, keyed by relative path + length + last-write time (UTC) + a 72-byte header
and tail sentinel. A warm start opens no package at all. It re-parses the cached `meta.lsx` text
with the existing `ParseMetaFile`, so a hit and a miss go through the same code. A small parallel
and off-UI-thread commit covers the cold start. An env-gated mod-list snapshot proves R1 at every
commit.

**Expected outcome.** These are inferred ranges, Debug build, owner's 1,121-mod set. §1 gives the
instrumentation that replaces them with measurements.

| Scenario | Today | After C1–C7 | After C8 (optional game-pak cache) |
|---|---|---|---|
| Warm start (cache valid, OS file cache warm) | 9.27 s | **~1.5–3 s** (base-game VFS still uncached) | **~0.4–0.9 s** |
| App-cold (no `modcache.json`, OS cache warm) | 9.27 s | **~3–5 s** (parallel scan) | same as left (the game cache is cold too) |
| OS-cold (after reboot) | not measured | I/O-bound, measure | measure |

The series is eight commits, the last one optional, each small and reviewable on its own (§4).
Memory should also drop. Every pak's full file-name list is retained for the life of the app in a
property nothing reads (C4). That retention is plausibly a large share of the 478 MB private
bytes **[inferred]**.

### Owner intentions this design is built around (gleaned from the notes)

1. Measure first. Instrumentation is commit 1, and every later commit has a gate based on those
   numbers (plan §8 4.1; your Facet experience, where the obvious first fix turned out wrong).
2. Upstreamable perf only: `perf/*` cut from `master`, self-contained, reviewable by LaughingLeader
   (plan §2–3). Measurement knobs live in `TEMP:` commits that are dropped before the PR, as on
   `perf/list-render`.
3. Correctness beats speed (R1). A before/after diff of the full mod list must be clean, and it
   must cover dedupe winners and force-loaded/override classification.
4. Toolkit-neutral and Linux-friendly where it costs nothing. Put the work in Core, not WPF
   (plan: "mostly src/Core, so this survives a future Avalonia port").
5. House rules: the ReactiveUI.Fody pin is untouched, `DivinityApp.Log`, no `string?`,
   tabs/CRLF/BOM/Allman/`var`, new types unprefixed, Newtonsoft for persistence.
6. Debug numbers are the control for relative improvement, and every stage is also tested in a
   Publish build (R5).
7. No Redux code. No WPF grouping (not relevant here).

---

## 1. Where the 9.27 s most likely goes

### 1.1 What runs inside the window

The baseline window "RefreshAsync starts at 3.61 s → mods finished loading at 12.88 s" is
`LoadModsAsync` (`src/GUI/ViewModels/MainWindowViewModel.cs:1609-1718`), called from `RefreshAsync`
at `:2375`. On first run `IsInitialized` is false, so the `LoadSettings` at `:2366-2372` is skipped
**[certain]**. Its parts, in order:

| # | Step | Call site | Work |
|---|---|---|---|
| a | Base-game mods | `:1627` → `DivinityModDataLoader.LoadBuiltinModsAsync` (`DivinityModDataLoader.cs:1783-1825`) | LSLib `VFS.AttachGameDirectory` reads the file table of every non-blacklisted pak in `Data/` and `Data/Localization/` (`VFS.cs:177-193`, blacklist `:91-115`). `FinishBuild` inserts **every entry of every attached pak** into a directory tree (`VFS.cs:195-224`). `ModPathVisitor.DiscoverMods` (`ModResources.cs:239-257`) runs, then each module's `meta.lsx` is read and parsed (`DivinityModDataLoader.cs:1727-1781`). |
| b | Editor projects | `:1637` → `LoadEditorProjectsAsync` (`:397-425`) | Sequential awaited reads of `Data/Mods/*/meta.lsx`. These folders are *also* found by the VFS in step a through `RootDir` (`VFS.cs:352-366`), which is the "double enumeration" in plan 4.2. |
| c | User paks | `:1665` → `LoadModPackageDataAsync` (`:745-807`) | Recursive enumeration of the Mods folder, run twice because `allPaks` is lazy (`:763`, `:764`). Then, for each of the 1,097 paks, `PackageReader.Read` (`:692`) and a loop over **all** file entries (`:484-561`). After that, dedupe (`:786-806`). |
| d | Merge and duplicates | `:1669-1714` | `MergeModLists` (`:1535-1553`, O(n²) `FirstOrDefault`), a base-mod `GroupBy`, and the duplicate dialog. |
| e | Final sort | `:1717` | `OrderBy(Name)` |

### 1.2 Which thread it runs on: probably the UI thread

`RefreshAsync` starts on the task pool (`:4145`, `:4990`). Its first `await` is
`SetMainProgressTextAsync` (`:2232-2239`), which awaits `Observable.Start(..., RxApp.MainThreadScheduler)`.
Rx's awaiter (`AsyncSubject<T>.OnCompleted`) captures `SynchronizationContext.Current`. That is null on
a pool thread, so the continuation runs on the thread that completed the subject: the Dispatcher
thread. The enclosing `await SetMainProgressTextAsync(...)` then resumes inline on that same thread.
So `LoadModsAsync` resumes on the UI thread after every progress update (`:1625`, `:1635`, `:1664`),
and it **calls `LoadBuiltinModsAsync` and `LoadModPackageDataAsync` synchronously from the UI thread**.
**[inferred, high confidence]**

What then runs on the UI thread is everything up to the first `await` that actually yields:

- In step a, all of `AttachGameDirectory`, `FinishBuild` and `DiscoverMods` (`:1789-1803`) are
  synchronous.
- In step c, the `foreach` at `:778-782` calls `LoadModDataFromPakToBagAsync` once per pak. Each
  call runs `PackageReader.Read` and the whole entry loop **synchronously**, until the first `await`
  on a compressed entry stream. LSLib's `LZ4DecompressionStream` (`LSLib/LS/Compression.cs:8-65`)
  does not override `ReadAsync`. The base `Stream.ReadAsync` therefore queues a pool task, and that
  is where the method first yields. An uncompressed entry (`MemoryMappedViewStream`) completes
  synchronously and does not yield at all.

Consequences:

1. The expensive prefix (open, map, file table, entry loop) is **serial**, whatever `Task.WhenAll`
   at `:784` suggests. Only the tail (meta parse, `DivinityModData` construction, extender config)
   runs in parallel on the pool. This fits 24.2 s CPU against ~15 s wall: the parallel tail, GC and
   tiered JIT add CPU without shortening the serial part. **[inferred]**
2. The UI thread is blocked for most of the 9 s. A cheap check before any code: does the progress
   bar or text visibly freeze during "Loading mods from Local AppData folder..."?
3. `DivinityModData` OAPHs created on pool threads post their values to the Dispatcher
   (`ReactiveExtensions.cs:17-36`). Those posts pile up behind the blocked UI thread and drain
   later. **[inferred]**

### 1.3 Suspects, ranked

| Rank | Suspect | Evidence | Status | Rough cost (Debug) |
|---|---|---|---|---|
| 1 | **Per-entry work in user paks.** `ReadCompressedFileList` LZ4-decodes the table (`PackageReader.cs:101-128`). `BinUtils.ReadStructs` then calls `Marshal.PtrToStructure<FileEntry18>` per entry (`BinUtils.cs:78-91`), and each call allocates a `byte[256]` for `Name` (`PackageFormat.cs:420-441`). Each entry also gets a UTF-8 decode and a `PackagedFileInfo` (`PackageCommon.cs:37-48`). The loader loop then does, per entry: `HashSet.Add` (`:488`), `Contains(EXTENDER_MOD_CONFIG)` (`:490`), `Path.GetFileName` plus regex for meta (`:449-456`), an interpreted (not compiled) regex `^(Mods\|Public)/(.+?)/.+$` (`:458`, `:500`), `GetFileName`/`TrimEnd` (`:503`), an interpolated string allocation (`:504`), and a builtin lookup (`:525`). | Code certain; cost inferred | 3–6 µs × E entries |
| 2 | **Serial execution on the UI thread** (§1.2). It multiplies rank 1 by roughly the core count. | Inferred, high confidence | it is the reason rank 1 is not divided by the number of cores |
| 3 | **Base-game VFS** (step a). `FinishBuild` allocates a prefix substring for *every* segment of *every* entry (`VFS.cs:211`, `path[..endPos]`) across Gustav/Shared/Game/GustavX/Localization paks. These hold hundreds of thousands of entries. | Code certain; size inferred | ~1–2.5 s |
| 4 | **GC pressure.** Roughly 850 B allocated per entry: the 272 B table slice (the per-pak `fileBuf` is a single LOH array once a pak has more than ~312 entries), the 280 B `Name` array, `PackagedFileInfo`, the name string, and the `Files` set entry. The `Files` set is **retained forever** (`:628`) but never read: grep finds only the assignment and the declaration `DivinityModData.cs:234`. | Retention certain; size inferred | part of rank 1; memory: E × ~175 B retained |
| 5 | **Osiris goal files read in full.** Every `Mods/<x>/Story/RawFiles/Goals/*` entry is decompressed and `ReadToEndAsync`'d, even after status reached `MODFIXER` (`:504-521`). | Certain | small to moderate; measure |
| 6 | **Per-pak fixed cost.** `File.OpenRead` + `MemoryMappedFile.CreateFromFile` + a view over the whole file (`PackageReader.cs:65-71`), plus the first-touch page faults on header and table, and possibly Defender on-open scanning. | Inferred | 0.2–1 ms per pak warm; unknown cold (HDD: ~10 ms per seek) |
| 7 | **O(n²) dedupe.** `dupes` is a lazy `IEnumerable` re-run (a full `GroupBy`) for every `!dupes.Contains(x)` at `:806`, and also at `:805`. | Certain | ~0.05–0.2 s |
| 8 | `MergeModLists` O(n²) (`:1539`); enumeration done twice (`:763-764`) | Certain | < 50 ms |

**Per-pak cost model** (user paks, today, serial part, OS cache warm):

```
T_paks ≈ N·c_open + E·c_entry + G·c_goal                (the tail runs in parallel and is mostly hidden)
N = 1,097 paks          c_open  ≈ 0.2–1 ms               (open + map + header/table page faults)
E = total entries       c_entry ≈ 3–6 µs                 (decode + marshal + PackagedFileInfo + loader loop, Debug)
G = goal files          c_goal  ≈ 0.05–0.5 ms            (decompress + read + Contains)
```

27 GB over 1,097 paks is ~25 MB per pak. If step c is ~6.5–8 s and step a ~1–2.5 s, the model
implies E ≈ 1–2 M entries, about 15–25 KB per packed file, which is plausible. **E is the one
number that decides everything, and nobody has measured it yet.** If E turns out small (say
< 200 k), rank 1 is wrong and the cost is fixed per-pak I/O (rank 6). In that case the cache still
wins (a hit opens nothing except the sentinel read), but the sentinel's cost has to be watched.

Per mod, on the parallel tail: `XElement.Parse` of the meta plus about a dozen
`Descendants("attribute")` scans (`:73-104`, `:168-325`) ≈ 0.1–0.3 ms. The `DivinityModData`
constructor wires ~40 Rx pipelines (`DivinityModData.cs:386-567`) ≈ 0.1–0.4 ms. That is roughly
0.3–0.6 s of CPU per 1,121 mods **[inferred]**, and it stays even on a warm start. It is spread
over the pool once C7 lands.

### 1.4 Plan 4.2 candidates: confirmed or killed

| Candidate | Verdict |
|---|---|
| Per-pak entry enumeration + regex | **Likely dominant.** Confirm with E and Σ loop time (§1.5). |
| MD5 hashing | **Killed [certain].** No hashing anywhere in the load path: grep finds no `MD5.Create`, `ComputeHash` or `HashData` in `src/` or `PackageReader.cs`. `MD5` is only an attribute read from `meta.lsx` (`:236`). |
| No metadata cache | **Confirmed absent [certain].** Centrepiece of this design. |
| Double enumeration of loose mods | **Demoted.** It only matters when `Data/Mods` has subfolders, which is rare on a GOG install. C1 logs the count. Leave it alone unless the count is > 0 and the time shows up. |

### 1.5 Instrumentation (C1 permanent, plus a TEMP commit)

C1 is permanent and upstreamable: stage timings only, with millisecond `Stopwatch` and a
greppable prefix. It fixes the existing `"{TimeSpan:s\\.ff}"` lines (`:418`, `:787`, `:1813`),
which print only the seconds component and wrap at 60 s.

```csharp
// MainWindowViewModel.LoadModsAsync — wrap each step (lines 1627, 1637, 1665, 1669-1714, 1717)
var sw = Stopwatch.StartNew();
baseMods = await RunTask(DivinityModDataLoader.LoadBuiltinModsAsync(Settings.GameDataPath, cancelTokenSource.Token), null);
DivinityApp.Log($"[Load] base game mods: {sw.ElapsedMilliseconds} ms ({baseMods?.Count ?? 0} mods)");
```

The log lines to expect, one set per launch:

```
[Load] base game mods: 1834 ms (24 mods) | vfs 1610 ms, discover 12 ms, meta 212 ms, paks 27
[Load] editor projects: 2 ms (0 folders, 0 mods)
[Load] mod paks: 7012 ms (1097 paks, 1101 mods, 3 duplicates) | enumerate 41 ms, scan 6702 ms, dedupe 160 ms
[Load] merge + duplicates: 9 ms
[Load] total LoadModsAsync: 9230 ms
[Refresh] mods 9230 | profiles 470 | profile uuid 4 | orders 31 | lists 1262 | finish 48 (ms)
```

(The numbers above are placeholders that show the format, not predictions.)

TEMP commit, not for upstream (`TEMP: per-pak load counters`). It answers the inferred items in
§1.2 and §1.3:

```csharp
// DivinityModDataLoader — TEMP counters, Interlocked so they are safe on any thread
private static long _tRead, _tLoop, _tGoal, _tMeta, _entries, _goalFiles, _readsOffPool, _solidPaks, _multiPartPaks;
private static readonly ConcurrentBag<(long Ticks, int Entries, string Name)> _slowest = new();

// in LoadModDataFromPakReaderAsync, around pr.Read (line 692)
var t0 = Stopwatch.GetTimestamp();
using var pak = stream == null ? pr.Read(pakPath) : pr.Read(pakPath, stream);
var readTicks = Stopwatch.GetTimestamp() - t0;
Interlocked.Add(ref _tRead, readTicks);
Interlocked.Add(ref _entries, pak.Files.Count);
if (!Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref _readsOffPool);
if (pak.Metadata.Flags.HasFlag(LSLib.LS.Enums.PackageFlags.Solid)) Interlocked.Increment(ref _solidPaks);
if (pak.Metadata.NumParts > 1) Interlocked.Increment(ref _multiPartPaks);
// _tLoop: lines 482-561, _tGoal: lines 514-520, _tMeta: lines 595-599 (each with GetTimestamp deltas)

// at the end of LoadModPackageDataAsync, and also time the task-creation loop (lines 777-782) on its own:
DivinityApp.Log($"[LoadTEMP] thread {Environment.CurrentManagedThreadId} pool={Thread.CurrentThread.IsThreadPoolThread} | " +
	$"entries {_entries} | read {Ms(_tRead)} | loop {Ms(_tLoop)} | goal {Ms(_tGoal)} ({_goalFiles} files) | meta {Ms(_tMeta)} | " +
	$"readsOffPool {_readsOffPool}/{modPaks.Count} | solid {_solidPaks} | multipart {_multiPartPaks} | " +
	$"taskLoop {taskLoopMs} ms | whenAll {whenAllMs} ms | alloc {GC.GetTotalAllocatedBytes() - alloc0 >> 20} MB | " +
	$"gc {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}");
foreach (var s in _slowest.OrderByDescending(x => x.Ticks).Take(10)) DivinityApp.Log($"[LoadTEMP] slow {Ms(s.Ticks)} ms, {s.Entries} entries: {s.Name}");
```

Also in TEMP: in `LoadBuiltinModsAsync`, time `AttachGameDirectory`, `FinishBuild`, `DiscoverMods`
and the meta loop separately (`:1789-1814`), and log thread id and pool flag at entry. `VFS.Packages`
is private, so count base-game entries with a second pass *after* the timed region (`PackageReader`
over the same pak list), logged as `[LoadTEMP] game entries`. Log the number of `Data/Mods`
subfolders.

**How to read it, and the decision gates:**

- `readsOffPool ≈ 1097` and `taskLoop ≈ scan` → §1.2 confirmed. C7 (off-UI, parallel) is
  mandatory. Also check: does the progress bar freeze?
- `read + loop ≥ 60%` of the mod-pak stage → C6 (cache) is justified as the main win.
- `entries` small but `read` large → fixed I/O cost dominates. Measure the sentinel cost in C6
  separately, and consider stat-only keys (§2.3).
- `base game mods ≥ 0.5 s` → do C8, otherwise drop it.
- `goal ≥ 0.3 s` → the cache absorbs it anyway. Note it for cold start.
- `solid > 0` → those paks are decompressed in full by `UnpackSolidSegment` (`PackageReader.cs:166-217`).
  The cache absorbs that too.

Capture protocol: 3 launches per scenario, take the median. Scenarios: OS-warm (second launch in a
row), OS-cold (after a reboot, or after RAMMap → Empty Standby List). Record Debug **and** Publish
(R5). Record E and the core count with the numbers.

---

## 2. The design

### 2.1 Shape

```
LoadModPackageDataAsync(root)
  enumerate paks (unchanged filter, partial-pak rule unchanged)
  cache = ModMetadataCache.Load(root)                 // never throws; empty on any problem
  for each pak i (C6: as today's loop; C7: Parallel.ForEachAsync, bounded):
      info = new FileInfo(pak)                        // stat BEFORE reading: races fail safe
      entry = cache.TryGet(rel, info)                 // length + mtime + sentinel must match
            ?? Scan(PackageReader.Read(pak))          // miss: today's loop, emitting facts only
      mods[i] = Build(entry.Scan, pak, info, builtinMods)  // same code for hit and miss
  dedupe over mods[] in enumeration order (deterministic)
  if not cancelled: cache.SaveInBackground(entries seen this run)   // atomic temp + rename
```

Two decisions carry the correctness case:

1. **Facts, not results.** The cache never stores a `DivinityModData`. It stores the raw
   `meta.lsx` text and the raw `ScriptExtender/Config.json` text, re-parsed through the existing
   `ParseMetaFile` and config parser on every load. It also stores the per-folder facts the
   override classification needs, which are **independent of the base-game mod list**. Parser
   changes therefore never need a cache bump. A game patch or an `IgnoredMods.json` update that
   changes which folders count as base-game re-derives classification without rescanning any user
   pak.
2. **One build path.** On a cold start, every pak goes Scan → Build. On a warm start it goes
   (cache) → Build. The C5 refactor that introduces Scan/Build ships *before* the cache and has to
   produce a clean R1 diff against master on its own. Once that holds, the cache only changes where
   the facts come from.

### 2.2 Scan and Build (C5): an exact re-statement of `InternalLoadModDataFromPakAsync`

The only base-game-dependent inputs in today's loop are `builtinMods.TryGetValue(modFolder, ...)`
(`:525`) and, through it, `builtinMod.Name` used in the text at `:636` and in the log at `:548`
**[certain]**. `IgnoreBuiltinPath` (`:529`) and `MAX_FILE_OVERRIDE_DISPLAY` (`:632`,
`DivinityApp.cs:65`) are app constants loaded from `IgnoredMods.json` (`MainWindowViewModel.cs:4733-4741`).
They go into the cache header.

So Scan records, for each distinct `modFolder` touched (computed exactly as at `:503`), everything
the loop would have derived *if* that folder were a base-game folder:

```csharp
// src/Core/Util/PakScanResult.cs  (new; namespace DivinityModManager.Util; tabs, CRLF, BOM)
namespace DivinityModManager.Util;

/// <summary>Facts read from one .pak, independent of which folders count as base-game. Cached by ModMetadataCache.</summary>
public class PakScanResult
{
	public string MetaEntry { get; set; }
	public string MetaText { get; set; }
	public string ExtenderConfigEntry { get; set; }
	public string ExtenderConfigText { get; set; }
	public bool ExtenderConfigReadFailed { get; set; }
	public DivinityOsirisModStatus OsirisModStatus { get; set; }
	public int FileCount { get; set; }
	public int NumParts { get; set; }
	/// <summary>Every Mods|Public/&lt;folder&gt; seen by the override loop, in first-seen order.</summary>
	public List<PakFolderFacts> Folders { get; set; } = [];
}

public class PakFolderFacts
{
	public string Folder { get; set; }
	/// <summary>Index in pak.Files of the first file in this folder not matching IgnoreBuiltinPath, or -1.</summary>
	public int FirstUnignoredIndex { get; set; } = -1;
	public string FirstUnignoredFile { get; set; }
	/// <summary>Distinct non-empty, non-ignored file names (Game/GUI: .xaml only), capped at MAX_FILE_OVERRIDE_DISPLAY.</summary>
	public List<string> BaseGameFiles { get; set; } = [];
	public bool BaseGameFilesOverflow { get; set; }
}
```

The Scan loop is today's loop, with `builtinMods` removed and three things recorded instead.
The `else if` chain (extender, then meta, then folder), the meta selection (`:570-588`) and the
goal-file logic (`:504-521`) move **verbatim**:

```csharp
// inside ScanPakAsync, replacing lines 523-559 (the builtin branch)
if (!foldersByName.TryGetValue(modFolder, out var facts))
{
	facts = new PakFolderFacts { Folder = modFolder };
	foldersByName.Add(modFolder, facts);
	result.Folders.Add(facts);
}
if (facts.FirstUnignoredIndex < 0 || !facts.BaseGameFilesOverflow)
{
	if (!IgnoreBuiltinPath.Any(x => f.Name.Contains(x)))
	{
		if (facts.FirstUnignoredIndex < 0)
		{
			facts.FirstUnignoredIndex = i;
			facts.FirstUnignoredFile = f.Name;
		}
		var isCandidate = f.Size() > 0 && (!(modFolder == "Game" && f.Name.Contains("GUI")) || f.Name.EndsWith(".xaml"));
		if (isCandidate && !facts.BaseGameFiles.Contains(f.Name))
		{
			if (facts.BaseGameFiles.Count < DivinityApp.MAX_FILE_OVERRIDE_DISPLAY) facts.BaseGameFiles.Add(f.Name);
			else facts.BaseGameFilesOverflow = true;
		}
	}
}
```

Build reconstructs today's locals exactly, then runs the original tail (`:562-686`) unchanged:

```csharp
private static DivinityModData BuildModDataFromScan(PakScanResult scan, string pakPath, DateTime lastWriteTimeUtc, Dictionary<string, DivinityModData> builtinMods)
{
	var hasBuiltinDirectory = false;
	var hasModFolderData = false;
	var isOverridingBuiltinDirectory = false;
	var baseGameFiles = new HashSet<string>();
	var baseGameFilesOverflow = false;
	var hits = new List<(int Index, PakFolderFacts Facts, DivinityModData Mod)>();

	foreach (var facts in scan.Folders)
	{
		if (builtinMods.TryGetValue(facts.Folder, out var builtinMod))
		{
			hasBuiltinDirectory = true;
			if (facts.FirstUnignoredIndex > -1)
			{
				isOverridingBuiltinDirectory = true;
				hits.Add((facts.FirstUnignoredIndex, facts, builtinMod));
				baseGameFiles.UnionWith(facts.BaseGameFiles);
				baseGameFilesOverflow |= facts.BaseGameFilesOverflow;
			}
		}
		else
		{
			hasModFolderData = true;
		}
	}

	// Same insertion order as today: the first non-ignored file of each base-game folder, in pak order.
	var builtinModOverrides = new Dictionary<string, DivinityModData>();
	foreach (var hit in hits.OrderBy(x => x.Index))
	{
		builtinModOverrides[hit.Mod.Folder] = hit.Mod;
		DivinityApp.Log($"Found a mod with a builtin directory. Pak({Path.GetFileNameWithoutExtension(pakPath)}) Folder({hit.Facts.Folder}) File({hit.Facts.FirstUnignoredFile})");
	}

	// ...lines 562-686 as they are, with:
	//   ParseMetaFile(scan.MetaText) instead of reading metaFile,
	//   baseGameFiles.Count < MAX  ->  !baseGameFilesOverflow && baseGameFiles.Count < MAX,
	//   File.GetLastWriteTime(pakPath)  ->  lastWriteTimeUtc.ToLocalTime()  (same value, Kind=Local),
	//   ParseScriptExtenderConfig(scan.ExtenderConfigText, ...) instead of the PackagedFileInfo overload,
	//   modData.Files = files  removed (C4).
}
```

Why this is exact. The checks below are against `:482-561`; each claim can be checked line by line
in review.

- `hasBuiltinDirectory` is true iff some touched folder is a key. `hasModFolderData` is true iff
  some touched folder is not a key. Both are the same as today.
- `isOverridingBuiltinDirectory` (before `:564`) is true iff some key folder has a non-ignored
  file. The size and xaml checks come *after* it is set (`:531-543`), so they do not affect it.
  After that, `:564-567` overrides it exactly as today.
- `builtinModOverrides` keys and their order: Dictionary enumeration follows insertion order when
  nothing is removed. Today's insertion order is "first non-ignored file per key folder", and Build
  sorts on that. `FirstOrDefault().Key` (`:617`, the synthesized override's `Folder`) is therefore
  identical.
- `baseGameFiles`: with no overflow, every folder's set is complete and the union is exact. With an
  overflow, some folder alone already holds ≥ MAX names, so `Count < MAX` is false today too.
  `OrderBy(x => x)` at `:634` runs on the same set with the same comparer.
- Names in `BuiltinOverrideModsText` (`:636`) come from the **current** `builtinMods`. That is
  exactly what today's code would print now.

Callers that are not startup (`LoadModDataFromPakAsync` for import and drag-drop at
`MainWindowViewModel.cs:1847`, `:3283`, `:3444`) become Read → Scan → Build with no cache. Same
behaviour.

### 2.3 The cache (C6)

**Location.** `DivinityApp.GetAppDirectory("Data", "modcache.json")`, next to `settings.json`
(`MainWindowViewModel.cs:1115`). That keeps the app portable, as today.

**Format.** Newtonsoft JSON, streamed (`JsonTextReader` over a `FileStream`). It matches house
persistence, a human can inspect it, and it is ~3–6 MB, dominated by `meta.lsx` text. Note: Core
globally imports `System.Text.Json` (`src/Core/Usings.cs:17`), so write
`Newtonsoft.Json.JsonSerializer` fully qualified.

```json
{
  "Schema": 1,
  "ScanVersion": 1,
  "RootHash": "9F2C…",                  // SHA-256 of the normalised Mods path; no user name in the file
  "IgnoreBuiltinPath": ["Game/GUI/Assets"],
  "MaxOverrideDisplay": 10,
  "Entries": [
    {
      "Path": "SomeMod.pak",            // relative to the Mods root
      "Length": 25165824,
      "LastWriteUtc": 638650123456789012,
      "Head": "TFNQSxIAAAA…",           // first 64 bytes (LSPK v15-18 header lives here)
      "Tail": "AAAAAAAAAAA=",           // last 8 bytes (v13 header pointer + signature)
      "Parts": [],                      // [{Length, LastWriteUtc}] for _1.._n when NumParts > 1
      "Scan": { "MetaEntry": "Mods/SomeMod/meta.lsx", "MetaText": "<?xml …", "OsirisModStatus": 0, "Folders": [ … ], … }
    }
  ]
}
```

**Header validity.** All of `Schema`, `ScanVersion`, `RootHash`, `IgnoreBuiltinPath` (as a
sorted list) and `MaxOverrideDisplay` must equal the current values. Otherwise the cache is
discarded as a whole: cold load, then overwrite. `ScanVersion` is a constant next to `ScanPakAsync`
with the comment "bump on any change to ScanPakAsync". That is the rule a reviewer enforces.

**Entry key.**

- Relative path. The comparer is `OrdinalIgnoreCase` on Windows and `Ordinal` elsewhere
  (`OperatingSystem.IsWindows()`).
- `FileInfo.Length`.
- `FileInfo.LastWriteTimeUtc.Ticks`, in UTC so a DST change cannot invalidate anything.
- Sentinel: first 64 and last 8 bytes, compared byte for byte. One open, two small reads. For
  v16/v18 this covers `FileListOffset`, `FileListSize`, `Flags`, `Priority`, `Md5` and `NumParts`
  (`PackageFormat.cs:212-221`), plus the first data bytes. Any re-pack almost surely changes
  `FileListSize` or `FileListOffset`.
- Multi-part paks (`NumParts > 1`, `PackageReader.cs:53-60`, `:89-94`): every part's length and
  mtime must also match.

`FileInfo` is used rather than the directory enumeration's timestamps. NTFS directory entries can
lag for files still open for writing, and `FileInfo` reads the MFT record.

**Why the sentinel is there.** It covers "updated in place, same size, same mtime". That happens
with copy tools that preserve mtime, and on exFAT/FAT where mtime has 2 s granularity. It costs one
open per pak (~30–100 µs warm; Defender unknown). C6 logs its total separately. If it measures as
significant and the owner accepts the residual risk, a one-line change turns it into stat-only.

**Rules.**

- Hit = all fields equal. A miss replaces the entry.
- Entries for paks not seen in this run (deleted, renamed, moved, now partial) are dropped when the
  cache is saved, so the cache never grows without bound.
- A pak whose Read or Scan **throws** is not cached. Today it is skipped with a log (`:706-710`),
  and the same failure (a lock, a half-copied file) is retried next launch.
- Deterministic non-results *are* cached, because they still cost a full scan each time. Examples:
  a pak with no `meta.lsx` and no override (`:675-678`), or a `meta.lsx` that fails to parse. Both
  produce the same log line and a null mod on a hit.

**Save.** Only when the load was not cancelled and something changed (any miss, or any dropped
entry). Done on a pool task after `LoadModPackageDataAsync` has produced its results, so it is off
the critical path. A static lock serialises a save that overlaps a quick F5.

```csharp
private static void SaveCore(string cachePath, CacheFile file)
{
	var tmp = $"{cachePath}.{Environment.ProcessId}.tmp";
	try
	{
		using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
		using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
		using (var jw = new Newtonsoft.Json.JsonTextWriter(sw))
		{
			Newtonsoft.Json.JsonSerializer.CreateDefault().Serialize(jw, file);
			jw.Flush();
			fs.Flush(true);
		}
		File.Move(tmp, cachePath, true);   // same-volume rename: readers see old or new, never a mix
	}
	catch (Exception ex)
	{
		DivinityApp.Log($"[ModCache] Failed to save '{cachePath}': {ex.Message}");
		try { File.Delete(tmp); } catch { }
	}
}
```

### 2.4 Load path (C6, then C7)

```csharp
// DivinityModDataLoader.LoadModPackageDataAsync — replaces lines 773-806 (enumeration above stays as is)
var cache = ModMetadataCache.Load(modsFolderPath, cts);
var built = new DivinityModData[modPaks.Count];
var entries = new ModMetadataCache.Entry[modPaks.Count];
var sw = Stopwatch.StartNew();

async Task LoadOneAsync(int i, CancellationToken token)
{
	var pakPath = modPaks[i];
	try
	{
		var info = new FileInfo(pakPath);
		var entry = cache.TryGet(pakPath, info);
		entry ??= await ModMetadataCache.CreateEntryAsync(pakPath, info, token);   // Read + ScanPakAsync
		entries[i] = entry;
		built[i] = BuildModDataFromScan(entry.Scan, pakPath, info.LastWriteTimeUtc, builtinMods);
	}
	catch (Exception ex) when (ex is not OperationCanceledException)
	{
		DivinityApp.Log($"Error loading mod pak '{pakPath}':\n{ex}");
	}
}

// C6: keep today's concurrency shape so the cache's gain can be measured on its own
var tasks = new List<Task>(modPaks.Count);
for (var i = 0; i < modPaks.Count; i++)
{
	if (cts.IsCancellationRequested) break;
	tasks.Add(LoadOneAsync(i, cts));
}
await Task.WhenAll(tasks);

// C7 replaces the loop above with:
//	var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = cts };
//	try { await Parallel.ForEachAsync(Enumerable.Range(0, modPaks.Count), options, async (i, t) => await LoadOneAsync(i, t)); }
//	catch (OperationCanceledException) { DivinityApp.Log("Loading mod paks was canceled; using what loaded."); }

DivinityApp.Log($"[Load] mod paks: scan {sw.ElapsedMilliseconds} ms, cache hits {cache.Hits}/{modPaks.Count}, misses {cache.Misses}");
var mods = built.Where(x => x != null).ToList();   // enumeration order, not ConcurrentBag order (C3)
// dedupe: today's rule, with `dupes` materialised (C3)
if (!cts.IsCancellationRequested) cache.SaveInBackground(entries);
```

C7 also wraps the two heavy calls in `LoadModsAsync` so they cannot start on the Dispatcher thread
(§1.2):

```csharp
baseMods = await RunTask(Task.Run(() => DivinityModDataLoader.LoadBuiltinModsAsync(Settings.GameDataPath, cancelTokenSource.Token)), null);
...
modLoadingResults = await RunTask(Task.Run(() => DivinityModDataLoader.LoadModPackageDataAsync(PathwayData.AppDataModsPath, cancelTokenSource.Token)), null);
```

### 2.5 Concurrency and cancellation

- `cache` is built before the scan and is read-only during it: a `Dictionary` lookup, safe for
  concurrent reads. Each worker writes only its own slots `built[i]` and `entries[i]`. No locks on
  the hot path.
- Shared statics read during the scan: `IgnoreBuiltinPath` (a HashSet, read-only after
  `LoadAppConfig`), `builtinMods` (a local Dictionary, read-only), the `Regex` instances
  (thread-safe), `DivinityApp.Log` (`Trace` global lock). All of these are already touched
  concurrently today by the parallel tail.
- `DivinityModData` is already constructed on pool threads today (`ParseMetaFile` in the tail), so
  this adds no new threading hazard. C7 bounds the degree of parallelism at 2–8. That also caps
  how many memory-mapped packages are open at once. Today the serial prefix can leave hundreds open
  while their tails await **[inferred]**.
- Cancellation (the 10-minute token, `MainWindowViewModel.cs:1664`): `Parallel.ForEachAsync` throws
  on cancel. Today the loop breaks and partial results are kept (`DivinityModDataLoader.cs:780`), so the throw is caught and
  partial results kept the same way. A cancelled run never saves the cache.
- Two app instances (there is no single-instance guard, CLAUDE.md §3): each writes its own
  `.<pid>.tmp` and then renames, and the last rename wins. Both files are complete. The reader opens
  with `FileShare.ReadWrite | FileShare.Delete` so a concurrent rename cannot fail on Windows. If it
  fails anyway, the save is skipped with a log line. A stale `.tmp` (crash mid-save) is deleted at
  the next save if it is older than a day.

### 2.6 Failure handling

| Failure | Behaviour |
|---|---|
| Cache missing | Cold path, then save. |
| JSON corrupt or truncated, wrong schema, header mismatch | `Load` catches everything and logs `[ModCache] discarded: <reason>`. Cold path; the file is overwritten at save. Never fatal. |
| One entry malformed (null `Scan`, null `Path`) | That entry is skipped, so it misses. |
| Cache dir not writable (Program Files, a read-only share) | Save fails, one log line per run, and the app works as today. No `ShowAlert`: the user cannot act on it and nothing is lost. This is a deliberate exception to the R8 rule, which concerns user-visible failures. |
| Pak locked or vanishes mid-load | Same as today: logged, skipped, not cached. |
| Stat taken, then the file changes before Read | The entry stores the old stat with new content, so the next launch misses and rescans. Taking the stat first is what makes this race fail safe. |

### 2.7 Memory

- C4 removes the retained `Files` set: about E × ~175 B (strings plus set entries) that stay
  resident for the life of the app. For E ≈ 1.5 M that is ~250 MB **[inferred]**. Check with
  private bytes after load, measured the way 478 MB was measured.
- Warm start avoids about E × ~850 B of transient allocation (tables, marshalled `Name` arrays,
  `PackagedFileInfo`), and with it most Gen2 and LOH collections.
- The cache itself is ~3–6 MB on disk, ~10–20 MB of transient objects while loading and saving,
  and it is released after the save. Nothing stays resident.

### 2.8 Hook points

| Where | Change | Commit |
|---|---|---|
| `MainWindowViewModel.cs:1627, 1637, 1665, 1669-1717`; `RefreshAsync :2375-2526` | `[Load]` / `[Refresh]` stopwatches | C1 |
| `DivinityModDataLoader.cs:418, 787, 1813` | `s\.ff` replaced with ms | C1 |
| `MainWindowViewModel.cs:1716` (before `return`), and after `BuildModOrderList` at `:2463` | snapshot hook | C2 |
| `DivinityModDataLoader.cs:773-806` | indexed results array instead of `ConcurrentBag`; materialised `dupes` | C3 |
| `DivinityModDataLoader.cs:479, 488, 628`; `DivinityModData.cs:234` | delete `Files` | C4 |
| `DivinityModDataLoader.cs:466-687` → `ScanPakAsync` + `BuildModDataFromScan`; `:1690-1725` → `ParseScriptExtenderConfig(string, string)` | refactor | C5 |
| new `src/Core/Util/ModMetadataCache.cs`, `PakScanResult.cs`; `:745-807` | cache | C6 |
| `MainWindowViewModel.cs:1627, 1665` `Task.Run`; `:777-784` `Parallel.ForEachAsync` | off-UI, parallel | C7 |
| `DivinityModDataLoader.cs:1727-1825` | game-pak cache (optional) | C8 |

### 2.9 C8 (optional, gated): base-game module cache

Gate: `[Load] base game mods ≥ 0.5 s` in C1.

- Key: every `*.pak` under `Data/` (flat) and `Data/Localization/` (depth ≤ 3), with relative path,
  length and mtime UTC. This is deliberately a superset of what `VFS.AttachGameDirectory` attaches,
  so any game patch, hotfix or DLC pak invalidates it.
- **Bypass rule:** if `Data/Mods` has any subdirectory, skip the cache and build the VFS as today.
  Loose folders can shadow or be shadowed by pak modules through VFS precedence (`VFS.cs:420-436`,
  `ModResources.cs:239-257`), and reproducing that is not minimal.
- Value: per module, the name, the `PackagePath` relative to `Data`, the meta path and the
  `meta.lsx` text. Build goes through `LoadModFromModInfo`'s logic, refactored to take text
  (`:1727-1781`).
- Stored as a `"Game"` object in the same file and saved by the same atomic writer.

---

## 3. The R1 guard

### 3.1 Snapshot harness (C2)

`src/Core/Util/ModListSnapshot.cs` (new, Core, toolkit-neutral). It is enabled by an
extension-less marker file `modsnapshot` next to the exe, the same pattern as `debug` and
`perfprobe`. The file's contents (for example `before`, `c6-warm`) become the label. Output:
`_Logs/modsnapshot_<label>_<yyyyMMdd-HHmmss>.tsv`. With no marker it costs one `File.Exists`.

Contents, sorted by `UUID`, then `FilePath`, both ordinal, so a diff is meaningful:

```
# summary	mods=1121	dupes=3	forceLoaded=41	overrideOnly=12	editor=0	userMods=1097	tieGroups=0
UUID	Folder	Name	Author	VersionInt	HeaderVersion	PublishVersion	MD5	PublishHandle	ModType	FilePath	LastModifiedUtc	FileSize
	IsUserMod	IsEditorMod	IsLarianMod	IsHidden	HasMetadata	IsForceLoaded	IsForceLoadedMergedMod	ForceAllowInLoadOrder
	CanAddToLoadOrder	HasInvalidUUID	OsirisModStatus	HasScriptExtenderSettings	SE.RequiredVersion	SE.FeatureFlags
	Tags	Dependencies(uuid@ver sorted)	Conflicts(uuid@ver sorted)	BuiltinOverrideModsText(escaped)	DescriptionSha1
# duplicates (offered for deletion)
UUID	FilePath	VersionInt	IsEditorMod
# winners (UUIDs with >1 candidate)
UUID	WinnerPath	CandidateCount	Tie(yes/no)
```

The `# winners` section is filled from `LoadModPackageDataAsync`'s dedupe and from
`MergeModLists`. `ModLoadingResults` already carries `Duplicates`. C2 adds a `Winners` list next to
it, used only when the snapshot is enabled. A second file, `modsnapshot_<label>_order_*.tsv`, is
written after `BuildModOrderList` (`MainWindowViewModel.cs:2463`). It lists `ActiveMods` as
`Index, UUID, FilePath, IsForceLoaded`, plus the `ForceLoadedMods` set. That proves the 935-entry
order resolves to the same files.

### 3.2 How to run it

1. Build C2 (master behaviour plus the harness). Write `before` into `modsnapshot`. Launch twice.
   Run `git diff --no-index --stat before1.tsv before2.tsv`. **This must be empty.** If it is not,
   the tie order is nondeterministic today (§3.3 item 1). Land C3, then re-baseline.
2. For each later commit: delete `Data/modcache.json` and launch (`cN-cold`), launch again
   (`cN-warm`), press F5 in-app (`cN-refresh`). Diff each against the baseline.
   `git diff --no-index --word-diff=color` makes single-field changes obvious.
3. Expected result: byte-identical. Any line that changes blocks the commit.
4. Repeat steps 1–3 once in a Publish build (R5).

### 3.3 Every place this series could change a winner or a classification

1. **Ties in dedupe.** Today the winner among equal `VersionInt` is the first item enumerated from
   a `ConcurrentBag` (`:790-803`), filled by whichever pool thread finishes first. It is
   **nondeterministic whenever any meta read yields** **[inferred]**. Any change in scheduling
   (C6, C7) can move it. C3 fixes this: first in *pak enumeration order* wins. Directory order is
   NTFS name order, so the rule is stable and explainable. This is a behaviour change for ties
   only. It is called out in the PR, and the snapshot's `Tie` column lists exactly which UUIDs it
   affects.
2. **Scan/Build refactor (C5).** Covered by the proof in §2.2: the `else if` precedence (extender,
   then meta, then folder), the last-wins extender config, the meta choice by `pakName.Contains(parent)`
   (`:570-588`), `metaCount == 0 ⇒ isOverriding = hasBuiltinDirectory` (`:564-567`), the
   synthesized override record (`UUID = pakPath`, `Folder = first override key`, `:612-622`),
   `IsForceLoadedMergedMod = hasModFolderData` (`:600-603`), `IsForceLoaded` (`:639`), and both
   `BuiltinOverrideModsText` variants (`:632-637`).
3. **Base-game folder set changes between runs** (`IgnoredMods` from JSON on first load, then
   enriched by loaded Larian mods in `SetLoadedMods`, `MainWindowViewModel.cs:1495-1510`).
   Classification is re-derived from facts on every run, so it follows the current set exactly as
   today. The `cN-refresh` snapshot tests this.
4. **Stale cache hit.** Wrong version or UUID leads to the wrong winner. The defences are length,
   mtime UTC, the 72-byte sentinel, part stats, `ScanVersion` and the header constants. The test
   matrix rows "updated in place" exercise this.
5. **`LastModified` source.** Today it is `File.GetLastWriteTime` (`:646`). After the change it is
   `FileInfo.LastWriteTimeUtc.ToLocalTime()`. Same value and Kind. It is not used by dedupe, but it
   is used by `IsNewerThan` and the date filter (`DivinityBaseModData.cs:131-142`,
   `DivinityModFilterData.cs:98`), and the snapshot checks it.
6. **Unchanged on purpose**, and listed so reviewers can see they were considered:
   - the partial-pak rule, with its static `_AllPaksNames` never cleared across refreshes
     (`:429-446`, `:763`; a latent bug, out of scope);
   - the version-and-editor rule in `MergeModLists` (`:1541`);
   - the base-mod duplicate detection (`:1673-1698`);
   - `IgnoreModByFolder`;
   - the `Path.PathSeparator` slip in `LoadAppConfig` (`:4739`, harmless today).
7. **C8 only:** VFS priority and loose-folder shadowing. Avoided by the bypass rule; the snapshot
   checks the base-game rows.

---

## 4. Branch and PR series: `perf/startup-load`

All commits are cut from `master`. Each one builds and can be reviewed alone, and each one runs the
R1 diff. `TEMP:` commits are dropped before the PR, as on `perf/list-render`.

| # | Commit | Change | Expected gain | Risk | Measure |
|---|---|---|---|---|---|
| C1 | `perf: log startup load timings in milliseconds` | stopwatches (§1.5); fixes the `s\.ff` wrap | 0 | none | it *is* the measurement |
| — | `TEMP: per-pak load counters` | §1.5 TEMP block | 0 | — | E, thread, split, slowest paks |
| C2 | `chore: optional mod list snapshot for diffing loads` | `ModListSnapshot` + marker file | 0 | none (off by default) | two baseline runs, diff empty |
| C3 | `fix: pick duplicate mods deterministically` | results array in enumeration order; `dupes` materialised (`:788` → `.ToList()`, then a `HashSet` for `Contains`) | ~0.05–0.2 s | ties only, documented | snapshot `Tie` rows; repeat runs identical |
| C4 | `perf: stop retaining every pak's file list` | delete `DivinityModData.Files` and the `files` set. Deleting it compiles, which proves nothing reads it; XAML grep is clean. | ~0.1–0.5 s CPU/GC; tens to hundreds of MB | low | private bytes after load; `[Load]` |
| C5 | `refactor: split pak loading into scan and build` | §2.2 | ~0 | medium (logic move), with R1 as the guard | snapshot vs C4 must be identical |
| C6 | `perf: cache pak scan results between launches` | §2.3–2.6 | warm: mod-pak stage → ~0.3–0.6 s | medium; key and invalidation | hits/misses line; cold/warm/refresh snapshots; matrix §5 |
| C7 | `perf: load mods off the UI thread, in parallel` | `Task.Run` ×2 + bounded `Parallel.ForEachAsync` | cold: roughly ÷ effective cores on the scan; the UI stays responsive | low–medium; disk thrash on HDD | `[Load]` at DOP 2/4/8 (TEMP knob); progress bar animates |
| C8 | `perf: cache base-game module metadata` *(only if gated in)* | §2.9 | warm: −(VFS time), ~1–2.5 s inferred | medium | `[Load] base game mods`; base-game snapshot rows |

**Splitting for upstream.** C3 is a genuine fix and can go as `fix/duplicate-mod-order` (cut from
`master`) ahead of the perf PR. C2 can go as `chore/mod-snapshot` if LaughingLeader prefers not to
fold it in. If the maintainer prefers a smaller PR, C4 and C7 are independent of the cache and can
be submitted alone. C6 depends on C5. C8 depends on C6's file and writer.

**Not done, on purpose (minimal).**

- LSLib-side changes: `MemoryMarshal` instead of `PtrToStructure`, VFS prefix-substring
  allocations. These live in a third repo, and the cache makes them matter only on a cold start.
- Compiling the regexes and removing the per-file interpolation (`:458`, `:504`). Revisit only if
  C1 or C7 shows cold-start loop CPU still dominating.
- Rx pipeline cost in the `DivinityModData` constructor. That is Stage 6 territory.
- Caching `LoadEditorProjectsAsync`.

---

## 5. Test matrix

Each row is run as: launch, check the `[Load]`/`[ModCache]` lines, then diff the snapshot against
the expectation. "Same" means byte-identical to the baseline snapshot, apart from the row the test
changes.

| # | Scenario | Setup | Expect |
|---|---|---|---|
| 1 | Cold app cache | delete `Data/modcache.json` | misses = 1,097, then a save. Snapshot same as baseline. Time ≈ C5/C7 cold. |
| 2 | Warm | relaunch | hits = 1,097, misses 0, no save. Snapshot same. |
| 3 | OS-cold + warm cache | reboot, launch | hits = 1,097. Sentinel time is visible here. Snapshot same. |
| 4 | Game patch (base-game paks change) | touch or replace a `Data/*.pak`, or verify files via GOG Galaxy | user-pak hits unchanged (facts are base-game-independent). C8 misses. Base-game rows reflect the new versions. |
| 5 | `IgnoredMods.json` gains a folder | add a test entry with `Folder` = a folder some user pak touches | **no user-pak rescan**. That pak's `IsForceLoaded` and `BuiltinOverrideModsText` change, exactly as on master with the same JSON (run master with the same JSON to get the expectation). |
| 6 | `IgnoreBuiltinPath` changes | edit the JSON | header mismatch: full cold, then the snapshot equals master with the same JSON. |
| 7 | Mod updated in place, new size | replace `X.pak` with a new version | 1 miss. The row shows the new version. If X is a duplicate, the winner changes as on master. |
| 8 | Updated in place, **same size, same mtime** | on a copy, hex-edit 1 byte of the header (`FileListSize`), then `(Get-Item X.pak).LastWriteTime = <old>` | sentinel miss. Then edit a byte deep in the data region instead (sentinel blind): expected **hit with stale data**. This is the documented residual. Verify that it requires both size and mtime to be equal *and* the table to be unchanged. |
| 9 | Same size, mtime +1 tick | `LastWriteTime` += 100 ns | miss (exact ticks compared). |
| 10 | Coarse mtime (exFAT USB or SD, Steam Deck) | mods folder on exFAT, replace within 2 s | sentinel catches it. Log line shows the miss. |
| 11 | Renamed pak | rename `X.pak` → `Y.pak` | old entry dropped, Y misses. Meta-less override: UUID changes to the new path, as on master. |
| 12 | Moved into a subfolder | `Mods/sub/X.pak` | relative key differs: miss, then the same result as master. |
| 13 | Deleted pak | delete | entry pruned at save. The mod disappears, as on master. |
| 14 | Duplicate UUIDs, different versions | two paks with the same UUID | higher version wins on cold, warm and refresh. The loser is in `# duplicates`. |
| 15 | Duplicate UUIDs, **equal versions** | two copies with the same version | C3 rule: first in enumeration order wins, stable across 5 runs. `Tie=yes`. |
| 16 | Duplicate of a base-game module in Mods | pak with a Larian UUID | `Duplicates` gets the base mod, as on master (`MainWindowViewModel.cs:1697-1702`). |
| 17 | Editor project in `Data/Mods` | create a loose project | C1 logs the folder count. C8 bypasses its cache. `IsEditorMod` row same as master. |
| 18 | Override pak without `meta.lsx` | a pak with only `Public/Shared/...` files | synthesized row: `UUID` = full path, `ModType` = "File Override", `Folder` = first override key. The ≤ 9 files list and the "Folder (Name)" fallback both match master. |
| 19 | Override with only ignored files | only `Game/GUI/Assets/...` | `metaCount == 0` path: `isOverriding = hasBuiltinDirectory` = true with empty overrides, matching master's odd output exactly (`Folder` = null, text = ""). |
| 20 | Merged override (meta + own folder + Gustav files) | — | `IsForceLoadedMergedMod` = true, `CanAddToLoadOrder` = true. |
| 21 | Multi-part pak | `X.pak` + `X_1.pak` | `_1` skipped as partial (unchanged). Touching `X_1` causes a miss of X. |
| 22 | Solid pak | if C1 reports any | correct on miss; hit avoids the full decompress. |
| 23 | Corrupted cache | truncate the file mid-way; also write garbage; also valid JSON with `Entries: null` | `[ModCache] discarded: …`, cold path, file rewritten. No exception reaches `RunTask`. |
| 24 | Older schema | set `"Schema": 0` or `"ScanVersion": 0` | discarded, then cold. |
| 25 | Cache from another Mods root | change the AppData path | `RootHash` mismatch, then cold. |
| 26 | Two instances | start two quickly | both load. One `.tmp` rename wins and the file is valid afterwards. No crash. |
| 27 | Kill during save | kill the process right after the load log | the old cache is intact, or a stale `.tmp` is cleaned at the next save. |
| 28 | Locked pak | open a pak exclusively in another program | logged and skipped, not cached. The next launch retries. |
| 29 | Cancel or timeout path | (TEMP) cancel the token mid-scan | partial results, no save. |
| 30 | Read-only app dir | install under Program Files | one save-failure log line per run. Behaviour otherwise as master. |
| 31 | Publish build | rows 1, 2, 7, 14 in Publish | same results (R5). |

---

## 6. What survives an Avalonia / Linux port

- **All of it except two call sites.** `ModMetadataCache`, `PakScanResult`, the Scan/Build split
  and `ModListSnapshot` are plain Core with System.IO, Newtonsoft and no WPF types. The GUI touches
  are a few lines in `LoadModsAsync` and `RefreshAsync` plus the snapshot hook.
- **Upstream avalonia has the same shape and the same bottleneck.** `ModDataLoader.InternalLoadModDataFromPakAsync`
  (`wt-avalonia/src/Core/Util/ModDataLoader.cs:476-560`) is the same loop with `IDictionary` builtins.
  `DirectoryPakParser.ProcessAsync` reads packages **serially** and keeps them all open
  (`wt-avalonia/src/Core/Util/Pak/DirectoryPakParser.cs:257-266`). It has no metadata cache
  (grep). The scan/build idea would port directly. That is an idea to offer upstream, not code to
  take.
- **Linux specifics, already designed in.**
  - Ordinal path comparer when not on Windows (case-sensitive filesystems).
  - mtime in UTC ticks (ext4 has ns precision; exFAT on a Steam Deck SD card has 2 s, which the
    sentinel covers).
  - The atomic rename is POSIX `rename(2)`, which is atomic.
  - `RootHash` keeps per-user paths out of the file.
  - One gap: `GetAppDirectory` is `AppContext.BaseDirectory`, which a Linux package
    (Flatpak, `/usr/lib`) may not be able to write. A port should place the cache in
    `Environment.SpecialFolder.LocalApplicationData` (XDG) when the app dir is read-only. The save
    already tolerates that failure, so behaviour degrades to today's, never worse.
- **What would not survive.** Nothing here is WPF-specific. The `Task.Run` around the Rx-awaited
  continuation (§1.2) is still correct under Avalonia's `RxApp.MainThreadScheduler`, where the
  same Rx awaiter semantics apply.

---

## 7. Open questions for the owner, answered by C1 and the TEMP numbers

1. What is E (total user-pak entries)? It decides whether C6's gain is mostly CPU or mostly I/O.
2. Does the progress bar freeze during "Loading mods from Local AppData folder..."? This is the
   quick check of §1.2.
3. What kind of disk holds the 27 GB Mods folder (NVMe, SATA SSD or HDD)? It determines C7's
   degree of parallelism and the OS-cold numbers.
4. Is a stale hit in row 8 (identical size, mtime and header, different data) acceptable as a
   documented residual? The alternative is hashing the whole file table, which costs a full open
   and table read per pak, roughly halving the warm-start gain.

---

## 8. Cross-check with the independent reports

Available when this was written: `docs/review/areas/AUDIT-PLAN.md` and `AUDIT-CLAUDE.md`. `LOAD.md` was not
present yet.

- **AUDIT-PLAN-06 (serial despite `Task.WhenAll`).** Agrees with §1.2 and §1.3, reached
  independently. This design adds one point the audit does not make: the synchronous prefix very
  probably runs on the **Dispatcher thread**, because of the Rx awaiter continuation after
  `SetMainProgressTextAsync`. That is why C7 wraps the calls in `Task.Run` rather than only
  parallelising inside the loader. The audit's thread-safety note about `_AllPaksNames` holds:
  it is written only during enumeration, before any worker starts.
- **AUDIT-PLAN-07 (`Files` never read).** Agrees. This is C4.
- **AUDIT-PLAN-08 (full VFS for ~30 base mods).** Agrees on the cost source. Its alternative fix is
  to probe `Mods/<x>/meta.lsx` per pak without `FinishBuild`. That helps the *cold* start too, but
  it re-implements the VFS's priority and loose-folder precedence (`VFS.cs:40-46`). C8's cache
  avoids that risk and only helps warm starts. Choose after C1: if the base-game step is large on
  cold starts too, the probe is worth a follow-up `perf/*` with its own R1 diff of the base-game
  rows.
- **AUDIT-PLAN-09 (goal files).** The cache absorbs it. An early exit once `MODFIXER` is set is
  safe (the status never downgrades) and could join C5. It was left out to keep C5 a pure move.
- **AUDIT-PLAN claim 29 ("key must include an IgnoredMods/app-version stamp").** This design
  **deliberately does not** key on the IgnoredMods list. The cached facts are independent of the
  base-game mod set, and classification is re-derived on every run (§2.2). A game patch or a new
  `IgnoredMods.json` therefore costs no rescan and cannot go stale. The parts of IgnoredMods that
  *do* feed the scan (`IgnoreBuiltinPath`, `MAX_FILE_OVERRIDE_DISPLAY`) are in the header, and
  `ScanVersion` stands in for "app version" exactly where scan logic changes.
- **AUDIT-PLAN claim 8 (recursive scan, same file name in different subfolders).** Cache keys are
  relative paths, not file names, so two `X.pak` in different subfolders are separate entries.
- **AUDIT-CLAUDE H2.** CLAUDE.md and the plan live on the orphan `notes` branch. Nothing in this
  series commits them to `perf/startup-load`.
