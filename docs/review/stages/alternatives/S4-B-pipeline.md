# Stage 4 design, angle B: fix the pipeline first, then add the cache

Branch `perf/startup-load`, cut from `master` at `fc35358`. Author: design agent S4-B, 2026-11-03.
Sources: the repo at `fc35358`, `External/lslib` at `2b88c0f`, the owner's CLAUDE.md, implementation-plan and
plan-overview, and `upstream/avalonia` (`a9b61fb`), which I read for ideas only. No build or run was possible
in this container, so every timing below is an **estimate** until the 4.1 instrumentation has produced real
numbers. Where a statement is certain from code, it carries a `path:line` citation. Where it is inferred,
it says *(inferred)*. `LOAD.md` was not in `scratchpad/reports/` when this was written, so I could not
cross-check this design against that independent review.

---

## 0. The owner's intentions, as I read them

The user asked for these to be listed. Each comes from the owner's own docs.

1. **Stay on WPF `master`.** Windows comes first. Linux stays possible: where a toolkit-neutral choice costs nothing, take it.
2. **Upstream only bugfixes and perf.** Those go in `fix/*`, `perf/*` and `chore/*` branches cut from a clean `master`.
   Fork-only work goes on `fork/main` (`feat/*`, `ui/*`). Merge with `--no-ff`, and merge rather than rebase when syncing upstream.
3. **Measure first.** The obvious first fix has been wrong before, both in the Facet spec and in the Stage 5 throttle.
   Every candidate is to be confirmed or killed by numbers. Baseline (Debug build): 1,121 mods, 9.27 s spent loading mods out of about 15.2 s.
4. **R1: correctness beats speed.** No perf change may alter which mod wins dedupe, or whether a mod counts as
   force-loaded or override. Prove it with a before/after snapshot diff of the full mod list.
5. **Stage 4 is "mostly `src/Core`"** so that it survives a future Avalonia port.
6. **The first launch after a game patch or a big mod update must be fast too.** A cache alone does not do that.
7. **House rules.** Keep the ReactiveUI.Fody 19.5.41 + RxUI 20 pin. Surface failures with `ShowAlert` and log with `DivinityApp.Log`.
   No `string?` in Core or GUI. Tabs, CRLF, BOM, Allman braces, `var`. New types get no `Divinity` prefix.
8. **Copy no Redux code. Categories never use WPF grouping.** Neither touches Stage 4.
9. **Errors must not stay invisible (R8).** Logging is off by default.
10. **Publish builds are tested, not only Debug (R5).**
11. **Forward-looking: hardlink deployment (the LIB design task).** It shapes how the cache must stat files (see §2.6).

---

## 1. Where the 9.27 s most likely goes

The span runs from "RefreshAsync starts" (3.61 s) to "mods finished loading" (12.88 s). It is the whole of
`LoadModsAsync` (`src/GUI/ViewModels/MainWindowViewModel.cs:1609-1718`), and it includes three UI-thread
round trips that are awaited (`:1629`, `:1667`, `:1706`).

### 1.1 Structural facts (certain from the code)

| # | Fact | Evidence |
|---|---|---|
| F1 | **User-pak loading is effectively serial.** The dispatch loop calls an `async` method per pak with no `Task.Run`. Each call runs synchronously up to its first real `await`. That synchronous stretch covers `PackageReader.Read` (open, memory-map, LZ4-decode the file table, one `PackagedFileInfo` per entry) **and the whole per-entry loop**. All of it runs on the single RefreshAsync thread, one pak after another. Only the work after the first yielding `await` overlaps. | `DivinityModDataLoader.cs:778-782` → `:701` → `:691-693` → loop `:485-558`. The first `await`s are `:518` (goal files, inside the loop) and `:597` (meta.lsx). |
| F2 | Whether those `await`s yield depends on how the entry is compressed. LZ4 entries come back as `LZ4DecompressionStream`, which does not override `ReadAsync`. The base `Stream.ReadAsync` then queues `BeginRead` on the thread pool, so it yields. Zlib and uncompressed entries are read through `UnmanagedMemoryStream`, which completes synchronously, so they do not yield. | `External/lslib/LSLib/LS/Compression.cs:8-62,129-157`. The `Stream` behaviour is .NET runtime knowledge *(not verifiable here)*. |
| F3 | **The base game is loaded serially through a full VFS.** `AttachGameDirectory` reads every non-blacklisted Data pak plus the Localization paks, one at a time (blacklist: `VFS.cs:93-117`). `FinishBuild` then builds a directory tree with one node insert per entry, allocating a substring per path segment. All of this runs to find about 10-20 `Mods/*/meta.lsx` files. The existing "Took … to load builtin mods" log times only the meta loop, not the VFS build. | `DivinityModDataLoader.cs:1789-1816`; `VFS.cs:173-223`; `Mods/ModResources.cs:231-251` |
| F4 | **The three loads run one after another** (base game, then editor projects, then user paks), and each is awaited. Yet the user-pak load does not depend on the base-game result: override detection keys on `DivinityApp.IgnoredMods` (from `IgnoredMods.json`), not on what `LoadBuiltinModsAsync` returns. | `MainWindowViewModel.cs:1627,1637,1665`; `DivinityModDataLoader.cs:747`. `IgnoredMods` is only mutated by `LoadAppConfig` (`:1138`, `:2418`) and `SetLoadedMods` (`:1508-1515`), and both run outside `LoadModsAsync`. |
| F5 | Per-entry work inside the loop: a `HashSet.Add`; an ordinal `Contains`; `Path.GetFileName`; **an interpreted (not Compiled) regex match** that allocates a `Match` and a group string; `TrimEnd`; `GetFileName`; **an interpolated string built for every entry** (`$"Mods/{modFolder}/Story/RawFiles/Goals"`); and a dictionary lookup. Entries under builtin folders also allocate an `IgnoreBuiltinPath.Any` closure. | `DivinityModDataLoader.cs:458,485-557` |
| F6 | Reading the file table allocates per entry: `Marshal.PtrToStructure` on a struct with a 256-byte `ByValArray` (a new 256-byte array plus marshalling), a UTF-8 decode to a string, and a `PackagedFileInfo` object. | `PackageReader.cs:116-128`; `BinUtils.cs:78-91,135-140`; `PackageFormat.cs:415-436` |
| F7 | **Every pak's full file list is kept in memory for the whole session** (`modData.Files = files`) and never read anywhere in `src/`. | `DivinityModDataLoader.cs:478,488,628`; `DivinityModData.cs:234`; grep finds no reader |
| F8 | Each goal file in `Story/RawFiles/Goals` (other than `ForceRecompile.txt`) is decompressed and read in full to look for `NRD_KillStory`. There is no short-circuit once the status is already `MODFIXER`. | `:504-523` |
| F9 | Dedupe is O(n²) and nondeterministic. `dupes` is a lazy `GroupBy`, re-evaluated for every mod by `mods.Where(x => !dupes.Contains(x))`: 1,121 regroupings of 1,121 items. **On equal `VersionInt` the winner is whichever came first in the `ConcurrentBag`, and that order is effectively random.** | `:787-806` |
| F10 | **There is no metadata hashing.** MD5 is only a `meta.lsx` attribute that gets copied. `ComputeHash` and `MD5.Create` appear only on the write path (`PackageWriter.cs:211`). | grep across `src` and `LSLib` |
| F11 | **Editor projects are loaded twice.** Once by the VFS, because `RootDir` is the Data folder and the filesystem `Data/Mods/*` dirs are enumerated (`VFS.cs:367-376`, then `DivinityModDataLoader.cs:1758-1762` with `IsEditorMod = true`). Once more by `LoadEditorProjectsAsync` (`:397-427`). | — |
| F12 | Logging was **on** during the baseline (the `debug` file). `Trace.AutoFlush = true` (`MainWindow.xaml.cs:85`) plus `Trace`'s global lock means every log line from every thread is serialised and flushed. The listener also runs 3 `string.Replace` calls per line (`LogTraceListener.cs:24-34`). | — |
| F13 | Every `DivinityModData` constructor wires about 35 Rx pipelines. Several of them (`ToUIPropertyImmediate`, `Connect().ObserveOn(Main)`) post to the Dispatcher at once. The awaited `Observable.Start` progress calls then queue behind thousands of those posts. | `DivinityModData.cs:386-566`; `ReactiveExtensions.cs:17-36`; `MainWindowViewModel.cs:272-279,2232-2238` |
| F14 | The pak directory is walked twice. `allPaks` is a lazy `IEnumerable`, enumerated once by `UnionWith` and again by `AddRange`. On top of that, `File.Exists` and `File.GetLastWriteTime` are called per pak even though the enumeration already had the data. | `:757-764,642-646` |

### 1.2 Ranked hypothesis (inferred, to be confirmed by §1.4)

| Rank | Suspect | Estimate (Debug) | Confidence |
|---|---|---|---|
| 1 | **Serial user-pak open + table decode + per-entry loop (F1, F5, F6).** The serial stretch is Σ over 1,097 paks of (open/mmap + header and file-list page faults + N_i × (decode + classify)). | 4-7 s | high that it is serial; medium on its share |
| 2 | **Base-game VFS build (F3)**: ~20-28 game paks, file tables likely ~10⁵-10⁶ entries in total *(unknown)*, all serial | 0.5-2 s | medium |
| 3 | **GC pressure from per-entry allocations (~1 KB/entry) and F7 retention** promoting hundreds of thousands of strings to gen2 | 0.3-1.5 s (spread across the above) | medium |
| 4 | Meta parse (`EscapeXmlAttributes` regex + `XElement.Parse` + ~15 `Descendants` scans) + `DivinityModData` constructor (F13). This part mostly runs in parallel today, which fits 24.2 s CPU over 15 s of wall time. | 0.3-0.8 s wall | medium |
| 5 | Dispatcher backlog delaying the awaited progress and duplicate-alert round trips (F13) | 0.1-0.5 s | low-medium |
| 6 | Log flushing with logging on (F12) | 0.05-0.5 s | low |
| 7 | Osiris goal reads (F8), double editor load (F11), O(n²) dedupe and merges (F9, `MainWindowViewModel.cs:1535-1553`), double directory walk (F14) | < 0.3 s combined | high that each is small |
| — | Wildcard: **Windows Defender on-access scanning** of 1,145 opened paks, and **OS-cold I/O** (2-3 random reads per pak). These could move most of the cost from CPU to I/O. | unknown | must measure |

**Per-pak cost model** *(inferred; §1.4 replaces it with a regression)*:

```
T_pak ≈ c_open (0.1-0.3 ms, + Defender ?) + c_io (SSD 0.1-0.3 ms | HDD-cold 10-25 ms)
      + N_i × (c_tbl ≈ 1 µs + c_cls ≈ 1.5-3 µs)           // F6 + F5, Debug
      + G_i × c_goal (30-100 µs)                           // goal files read (F8)
      + c_meta (≈0.05 ms) + c_parse (0.2-0.5 ms) + c_ctor (0.2-0.5 ms)
```

If 9.27 s minus 1-2 s of base game leaves about 7 s for 1,097 paks, that is **~6.5 ms per pak in serial
terms**. Getting there needs either a large mean N (about 1,500 entries per pak) or large fixed costs
(Defender, I/O). The CSV below settles which: regress `t_sync` on `num_files`. The slope is c_tbl+c_cls; the intercept is the fixed cost.

**Cross-check.** `docs/review/areas/AUDIT-PLAN.md` was written independently and arrived while this design was being
finished. It reaches the same conclusions on F1/F2 (AUDIT-PLAN-06), F7 (-07), F3 (-08), F8 (-09), F10 (claim 27,
refuted) and F11 (claim 28, negligible). It adds that a cache key needs an IgnoredMods stamp (claim 29).
§2.4 #5 meets that need more precisely: a per-entry `BuiltinHitFolders` check instead of one global hash,
which would otherwise invalidate everything on each F5 (see #5). One correction to AUDIT-PLAN-08's fix:
`PackageReader.Read(path, metadataOnly: true)` returns **before** the file list is read
(`PackageReader.cs:157`), so it cannot probe for `Mods/<x>/meta.lsx`. The table has to be decoded, which is
what C7b/C9 do. `LOAD.md` had still not arrived.

### 1.3 Killed or demoted before measuring

- **"MD5 hashing dominates"** (plan 4.2): killed. Loading does no hashing at all (F10).
- **"Double enumeration of editor mods"** (plan 4.2): real (F11) but cheap. It costs one extra small file read per editor project, and the owner's editor-project count is probably 0-few. Keep it on the list only if §1.4 logs more than about 20 editor projects. It also carries R1 subtlety (§3.2 #9).

### 1.4 Instrumentation (commit C1). Run this before changing anything.

Rule: **never log per pak.** Logging is serialised and flushed (F12), so per-pak lines would distort exactly
what we are measuring. Instead, collect per-pak timings into a preallocated array, write them out as a single
CSV, and log only phase summaries.

New file `src/Core/Util/LoadProfiler.cs`. It is enabled when logging is on, or when an extension-less
`perfload` file sits next to the exe (the same convention as `debug` and `perfscroll`):

```csharp
using System.Diagnostics;

namespace DivinityModManager.Util;

public static class LoadProfiler
{
	public static bool Enabled { get; set; }
	private static readonly long _processStart = Stopwatch.GetTimestamp();

	public static long Now() => Stopwatch.GetTimestamp();
	public static double Ms(long from, long to) => (to - from) * 1000d / Stopwatch.Frequency;

	public static void Phase(string name, long from, string extra = "")
	{
		if (!Enabled) return;
		var now = Stopwatch.GetTimestamp();
		DivinityApp.Log($"[LoadProfiler] {name}: {Ms(from, now):F1} ms | t={Ms(_processStart, now):F0} ms | gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} | alloc={GC.GetTotalAllocatedBytes() >> 20} MB | pause={GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms | pool={ThreadPool.ThreadCount} {extra}");
	}
}

public struct PakLoadRecord
{
	public string FileName;
	public long SizeBytes;
	public int PakVersion, PakFlags, NumParts, NumFiles, GoalReads, ThreadStart, ThreadEnd;
	public long TStart, TOpened, TScanned, TMetaRead, TParsed, TEnd;
	public byte Outcome; // 0 mod, 1 override, 2 none, 3 error
}
```

Stopwatch points. These go on the **unchanged** legacy code, so they measure master as it is:

| Where | Phase name | Why |
|---|---|---|
| `DivinityModDataLoader.cs:757-764` | `paks.enumerate` (+ count) | walk cost (F14) |
| around the `foreach` at `:777-782` | **`paks.dispatch(sync)`** | **The decisive F1 test.** If this is most of the pak phase, loading is serial. |
| `:784` `Task.WhenAll` | `paks.await(rest)` | the parallel tail |
| `:787-806` | `paks.dedupe` | F9 |
| `:691-692` around `pr.Read` | per-pak `TOpened` (+ `pak.Metadata.Version/Flags/NumParts`, `pak.Files.Count`) | open + table decode |
| `:558` end of entry loop | per-pak `TScanned`; count `GoalReads` at `:516` | classify cost (F5) |
| `:597`, `:598`, `:672` | per-pak `TMetaRead`, `TParsed`, `TEnd`; `Environment.CurrentManagedThreadId` at start and end | parse cost; thread hops (F2) |
| `:1789-1790`, `:1791`, `:1803`, `:1808-1815` | `game.attach`, `game.finishBuild`, `game.discover`, `game.metas` | F3 |
| `MainWindowViewModel.cs:1623-1629 / 1634-1639 / 1662-1667 / 1670 / 1675-1702 / 1706-1710 / 1712-1717` | `mods.builtin`, `mods.editor` (+ count), `mods.paks`, `mods.mergeBase`, `mods.dupChecks`, `mods.dupAlertUI`, `mods.mergeRest+sort` | where the 9.27 s splits |
| After `:1665`: `var t = LoadProfiler.Now(); await Observable.Start(() => { }, RxApp.MainThreadScheduler); LoadProfiler.Phase("ui.dispatcherLatency", t);` | `ui.dispatcherLatency` | F13 backlog |
| `RefreshAsync` `:2342`, `:2375`, `:2380`, `:2387`, `:2401`; inside `:2416-2477` split into `LoadAppConfig`, `SetLoadedMods`, `Profiles.AddRange`, `BuildModOrderList`; `:2487-2550` | `refresh.step1..10` | plan 4.1 |

When the load ends, write `_Logs/load-profile-yyyyMMdd-HHmmss.csv` with one row per `PakLoadRecord`. Then
log one summary: Σ per-pak time, wall time, **parallelism factor = Σ/wall**, p50/p95/max, Debug or Release,
`Debugger.IsAttached`, `Environment.ProcessorCount`, and whether logging is on. In profile mode only, a
**post-load census** then reads the header and file table of every attached game pak in the background
and logs per-pak entry counts. It runs after the timed phases, so it does not disturb them.

**Experiments**, each with 3 runs and the median reported:

| ID | Question | Method |
|---|---|---|
| E1 | Is the pak phase serial? | `paks.dispatch(sync)` vs `paks.await(rest)`; parallelism factor; thread IDs in CSV |
| E2 | Per-entry vs fixed per-pak cost | regress `TScanned-TStart` on `NumFiles` across 1,097 rows |
| E3 | Logging overhead | `perfload` file only, no `debug` file, vs with `debug` |
| E4 | Defender share | the same run with the Mods folder temporarily excluded from real-time protection (owner's choice; revert after) |
| E5 | OS-cold vs app-cold | after a reboot (or RAMMap "Empty Standby List") vs a plain app restart. **The 15.2 s baseline was probably app-cold.** |
| E6 | Debug vs Publish | R5. The baseline is Debug; LSLib's tight loops run unoptimised in Debug. |
| E7 | Cache-validation cost (after C8) | stat-only vs stat+fingerprint warm runs |

---

## 2. The design

### 2.1 Shape

Pak loading splits into **facts** and a **build** step:

```
enumerate (1 walk, size+mtime) ──► per pak: cached PakFacts valid?  ──yes──► BuildModFromFacts ──► DivinityModData
                                                   │no
                                                   ▼
                                     PakScanner.Scan (fast v18 path | LSLib fallback) ──► PakFacts ──► (same build)
```

- `PakFacts` holds everything the old loop learned from the pak that is independent of the builtin-folder set:
  the raw `meta.lsx` text, the raw SE-config text, the Osiris status, and the folder lists.
- `BuildModFromFacts` is the **only** code that turns facts into a `DivinityModData`, whether the facts came cold
  (from a scan) or warm (from the cache). It still calls the unchanged `ParseMetaFile`. **Warm and cold differ only
  in where the bytes came from.** That is the core R1 guarantee, and it also means a parser change never needs a
  cache migration. Reparsing 1,121 metas costs about 0.1-0.2 s spread over the workers *(inferred)*.
- The cache stores `PakFacts`, keyed by path, validated by stat and a fingerprint, plus a per-entry
  builtin-context check (§2.4).

New types: none prefixed, all in Core, no WPF.

| Type | File | Role |
|---|---|---|
| `PakFacts` | `src/Core/Models/Pak/PakFacts.cs` | DTO (below) |
| `PakFileStat` | same | `readonly record struct (string Path, long Size, long LastWriteUtcTicks)` |
| `BuiltinContext` | `src/Core/Util/Pak/BuiltinContext.cs` | immutable snapshot of `IgnoredMods` by folder, plus `IgnoreBuiltinPath` and its hash |
| `PakScanner` | `src/Core/Util/Pak/PakScanner.cs` | fact collection: the legacy loop moved verbatim (C3), then the fast v18 path (C7) |
| `ModMetadataCache` | `src/Core/Util/Pak/ModMetadataCache.cs` | load, validate, atomic save |
| `LoadProfiler` | `src/Core/Util/LoadProfiler.cs` | §1.4 |
| `ModSnapshot` | `src/Core/Util/ModSnapshot.cs` | R1 harness (§3) |

```csharp
namespace DivinityModManager.Models.Pak;

public enum PakScanOutcome : byte { Facts = 0, FormatError = 1 }

public class PakFacts
{
	public string Path;                 // runtime path. Never used for UUID on the warm path: the UUID always comes from the enumerated path
	public long Size;
	public long LastWriteUtcTicks;
	public long ScannedAtUtcTicks;      // for the racy-clean rule
	public ulong Fingerprint;           // XxHash64(header[0..40) + compressed file list), 0 = not computed
	public PakScanOutcome Outcome;
	public string ErrorText;            // FormatError only

	public string MetaFileName;         // null <=> metaCount == 0 (DivinityModDataLoader.cs:561-589 selection already applied)
	public string MetaText;             // StreamReader-decoded text of MetaFileName
	public string ExtenderConfigName;   // last entry containing EXTENDER_MOD_CONFIG (:490-493)
	public string ExtenderConfigText;   // null if absent or unreadable
	public string ExtenderConfigError;  // read exception text, logged on build
	public DivinityOsirisModStatus OsirisStatus;

	public string[] Folders;            // distinct modFolder values from branch 3 (:500-556), in first-appearance order
	public string[] BuiltinHitFolders;  // Folders ∩ B at scan time, first-appearance order (validity key)
	public string[] OverrideFolders;    // the builtinModOverrides keys, insertion order (:545-549)
	public int BaseGameFileCount;       // baseGameFiles.Count (:533-543)
	public string[] BaseGameFiles;      // only when 0 < count < MAX_FILE_OVERRIDE_DISPLAY, else empty
}
```

`HasModFolderData` and `HasBuiltinDirectory` are not stored. Both follow exactly from `Folders`, `B` and
`BuiltinHitFolders`: `hasModFolderData` is set only in the `else` branch at `:552-555`, which means "some folder is not in B".

### 2.2 Commit-by-commit pipeline changes (cold path)

**C3: pure refactor.** Split `InternalLoadModDataFromPakAsync` (`:466-687`) into
`PakScanner.CollectFacts(Package pak, string pakPath, BuiltinContext ctx)` (the `:483-589` loop, and the
reads at `:516-518,594-597,659`, moved verbatim) and `BuildModFromFacts(PakFacts, BuiltinContext)` (the
`:591-672` logic). `LoadModDataFromPakReaderAsync` (`:689-694`) keeps its signature, so import
(`MainWindowViewModel.cs:1847,3283,3444`) is unchanged. Split the text parse out of
`LoadScriptExtenderConfigAsync(PackagedFileInfo)` (`:1690-1725`) into `ParseScriptExtenderConfig(string text, string label)`.

```csharp
internal static DivinityModData BuildModFromFacts(PakFacts facts, BuiltinContext ctx)
{
	var hasBuiltinDirectory = facts.BuiltinHitFolders.Length > 0;
	var isOverridingBuiltinDirectory = facts.OverrideFolders.Length > 0;
	var hasModFolderData = facts.Folders.Any(f => !ctx.BuiltinByFolder.ContainsKey(f));
	DivinityModData modData = null;

	if (facts.MetaFileName == null)
	{
		// Assume it's an override mod since it doesn't have a meta file. (:564-568)
		isOverridingBuiltinDirectory = hasBuiltinDirectory;
	}

	if (facts.MetaFileName != null)
	{
		modData = ParseMetaFile(facts.MetaText);
		if (modData != null && isOverridingBuiltinDirectory)
		{
			modData.IsForceLoadedMergedMod = hasModFolderData;
		}
	}
	else if (isOverridingBuiltinDirectory)
	{
		modData = new DivinityModData()
		{
			FilePath = facts.Path,
			Name = Path.GetFileNameWithoutExtension(facts.Path),
			Folder = facts.OverrideFolders.FirstOrDefault(), // == builtinModOverrides.FirstOrDefault().Key, null if empty
			Description = "This file overrides base game data.",
			ModType = "File Override",
			UUID = facts.Path,
		};
		DivinityApp.Log($"Adding a file override mod pak '{modData.Name}'.");
	}
	// ... :625-672 unchanged, except:
	//   modData.Files is no longer assigned (C4)
	//   the BuiltinOverrideModsText else-branch uses facts.OverrideFolders.Select(f => ctx.BuiltinByFolder[f])
	//   LastModified = new DateTime(facts.LastWriteUtcTicks, DateTimeKind.Utc).ToLocalTime()
	//     (File.GetLastWriteTime returns exactly that value)
	//   SE config: ParseScriptExtenderConfig(facts.ExtenderConfigText, ...)
	return modData;
}
```

**C4: stop retaining file lists.** Do not assign `modData.Files` (`:628`). Keep the property, since it is
public API, but leave it null. It is read nowhere (F7), so R1 is unaffected. This is a memory win; measure
private bytes against the 478 MB baseline.

**C5: bounded parallel scan, single walk, deterministic O(n) dedupe.** Rewrite `LoadModPackageDataAsync` (`:745-808`):

```csharp
public static async Task<ModLoadingResults> LoadModPackageDataAsync(string modsFolderPath, CancellationToken cts, ModMetadataCache cache = null)
{
	var ctx = BuiltinContext.Capture(); // DivinityApp.IgnoredMods.Items.SafeToDictionary(x => x.Folder, x => x), same as :747
	var results = new ModLoadingResults() { DirectoryPath = modsFolderPath };
	var paks = EnumeratePaks(modsFolderPath); // List<PakFileStat>, Directory.EnumerateFiles order, partials removed (:757-764 semantics)
	var built = new DivinityModData[paks.Count];
	var facts = new PakFacts[paks.Count];
	var options = new ParallelOptions() { MaxDegreeOfParallelism = GetLoadParallelism(), CancellationToken = cts };

	try
	{
		await Parallel.ForEachAsync(Enumerable.Range(0, paks.Count), options, (i, token) =>
		{
			var pak = paks[i];
			try
			{
				var f = cache?.TryGetValid(pak, ctx) ?? PakScanner.Scan(pak, ctx);
				facts[i] = f;
				built[i] = BuildModFromFacts(f, ctx);
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Error loading mod pak '{pak.Path}':\n{ex}"); // same text as :709
			}
			return ValueTask.CompletedTask;
		});
	}
	catch (OperationCanceledException)
	{
		DivinityApp.Log("Loading mod paks was canceled; using the paks loaded so far.");
	}

	SplitDuplicates(built.Where(x => x != null).ToList(), results);
	cache?.QueueSave(facts.Where(x => x != null));
	return results;
}

private static int GetLoadParallelism() => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);
```

- The **body is synchronous**: plain blocking reads on at most DOP workers. That is genuine synchronous I/O
  on worker threads, not sync-over-async. It replaces about 1,100 concurrently live tasks and memory maps with
  ≤ 8 workers and ≤ 8 open handles at a time.
- **Enumeration** uses `FileSystemEnumerable<PakFileStat>` with `entry.ToSpecifiedFullPath()`. That is the
  transform `Directory.EnumerateFiles` itself uses, so path strings, and therefore override-pak UUIDs, stay
  byte-identical. Same `RecursiveOptions`, same predicate as `:759-762`, materialised once (fixes F14).
  `_AllPaksNames.UnionWith(...)` stays exactly as it is, including the never-cleared quirk; see §4, out-of-scope.
  Size and mtime **for validation** come from `FileInfo` (`GetFileAttributesEx`), not from the find data.
  The reason is in §2.6.
- `SplitDuplicates` keeps the old rules exactly, with no O(n²) and in a fixed order:

```csharp
private static void SplitDuplicates(List<DivinityModData> mods, ModLoadingResults results)
{
	// mods is in pak enumeration order. Rules identical to the old :788-806:
	// group by UUID (default comparer), the first mod with the strictly highest VersionInt wins,
	// Duplicates = non-winners in group order, Mods = singles (in order) + winners (group order), OrderBy Name.
	var groups = new Dictionary<string, List<DivinityModData>>();
	var groupOrder = new List<List<DivinityModData>>();
	foreach (var m in mods)
	{
		var key = m.UUID ?? "\0<null>"; // GroupBy allowed a null key; keep null separate from ""
		if (!groups.TryGetValue(key, out var list))
		{
			list = [];
			groups.Add(key, list);
			groupOrder.Add(list);
		}
		list.Add(m);
	}
	var winners = new List<DivinityModData>();
	foreach (var list in groupOrder)
	{
		if (list.Count < 2) continue;
		var winner = list[0];
		for (var i = 1; i < list.Count; i++)
		{
			if (list[i].Version.VersionInt > winner.Version.VersionInt) winner = list[i];
		}
		winners.Add(winner);
		results.Duplicates.AddRange(list.Where(x => !ReferenceEquals(x, winner)));
	}
	var singles = mods.Where(m => groups[m.UUID ?? "\0<null>"].Count == 1);
	results.Mods.AddRange(singles.Concat(winners).OrderBy(x => x.Name));
}
```

(`DivinityModData` does not override `Equals`, so the old `Contains` calls were reference equality. Checked: `DivinityBaseModData.cs` has only `PakEquals`.)

**C6: run the three loads concurrently.** In `LoadModsAsync` (`MainWindowViewModel.cs:1621-1668`), start
the builtin task (with its 30 s CTS), the editor task (30 s), and the pak task (10 min) together, then
`await Task.WhenAll`. Everything after `:1670` (merges, dup checks, alerts) keeps its order: base, then paks,
then projects. That makes the results identical by construction (F4). Progress becomes one
"Loading mods..." text. The three `IncreaseMainProgressValueAsync(taskStepAmount)` calls stay, issued as each
task finishes, so the bar still sums to the same total.

**C7: cheaper fact collection, in two steps.**

- **C7a (low risk): cheap per-entry classification on LSLib entries.** Keep `PackageReader`. Replace the
  per-entry regex with a hand parser that has the exact semantics of `^(Mods|Public)/(.+?)/.+$` (case-sensitive
  prefix; group 2 is the shortest non-empty run ending before a `/` that has at least one character after it).
  If a name contains `\n`, fall back to the real regex for that entry. Then apply the original
  `Path.GetFileName(g2.TrimEnd(Path.DirectorySeparatorChar))` once per *distinct* group-2 string, caching the
  previous entry's value. Cache the Goals needle string per folder instead of interpolating it per entry. Stop
  reading goal files once the status is `MODFIXER` (the status never goes back down). Hoist
  `IgnoreBuiltinPath` into an array loop with no closure. **Behaviour must not change:** checked by the
  `verifyscan` equivalence mode below.
- **C7b (medium risk; only if C7a's CSV shows table decode is still big): our own LSPK v18 table reader.**
  Use `File.OpenHandle` + `RandomAccess.Read`, with no memory map. Check for the v13 trailer signature
  first, as LSLib does (`PackageReader.cs:241-246`), then the leading `LSPK` and version 18. **Fall back to
  LSLib** for any other version, for `Solid` (which would decompress the whole archive: `PackageReader.cs:171-174,179-233`),
  or for `NumParts > 1`. Decompress the list with the public `CompressionHelpers.Decompress(bytes, 272*n, MethodLZ4)`.
  Walk the 272-byte records as spans: name `[0,256)` up to NUL, `OffsetInFile` = u32@256 | u16@260<<32,
  part u8@262, flags u8@263, `SizeOnDisk` u32@264, `UncompressedSize` u32@268.
  Classify on UTF-8 bytes, and materialise strings only for: candidate metas (names ending `meta.lsx`,
  case-insensitive, which then go through the **original** `IsModMetaFile`); the config entry; goal entries;
  entries under builtin folders; and one string per distinct folder. Any name that fails `Utf8.IsValid` takes
  the slow path (decode, then run the legacy code). Content reads mirror the legacy stream semantics exactly:
  LZ4 via `LZ4Codec.Decode(..., true)` **without** the size check (as `LZ4DecompressionStream`, `Compression.cs:24-32`),
  zlib and zstd via the same stream classes, `SizeOnDisk == 0` gives empty, a deletion entry throws as
  `CreateContentReader` does. Text is decoded through `new StreamReader(new MemoryStream(bytes)).ReadToEnd()`,
  the same BOM handling as `:596-597`. Unknown compression bits throw through the same `Flags.Method()`.
  The fingerprint (`XxHash64` from System.IO.Hashing, already referenced by `LSLib.csproj:53`) is computed
  from header bytes `[0,40)` plus the compressed list. Those bytes have already been read, so it costs nothing extra.
- **`verifyscan` mode** (an extension-less file next to the exe): for every pak, run both the legacy LSLib
  collector and the new one, compare `PakFacts` field by field, and log a summary plus any mismatches.
  **C7 merges only with 0 mismatches across the owner's 1,145 paks.**

**C9 (conditional: only if `game.*` > 0.5 s after C6 hides it behind the pak phase).** Cache the base-game
discovery. Key: the game Data path plus the ordered list of attached paks (relative path, size, mtime) as
`AttachGameDirectory` selects them (`VFS.cs:165,173-181`). Value: the `ModInfo` list (name, `PackagePath`,
`ModsPath`, `Meta`, plus meta text for pak-based metas). Loose `Data/Mods/*/meta.lsx` files are **always**
re-read, because editors change them constantly. A cold miss (game patch) falls back to today's VFS path.
A further option, which also speeds the first launch after a patch, is a meta-only scan of the game pak
tables. It must replicate the VFS rules: highest `Priority` wins with first-attached on a tie (`VFS.cs:42-45`);
case-sensitive `Mods/<X>/meta.lsx`; the filesystem fallback (`VFS.cs:417-433`); and `Mods` dictionary
insertion order (first appearance of any `Mods/<X>/...` entry, then filesystem dirs). It too is gated by the
`verifyscan` comparison. This is the same idea as upstream/avalonia's `DirectoryPakParser`, which drops the
VFS for game data (`wt-avalonia/src/Core/Util/Pak/DirectoryPakParser.cs:246-271`). That class still reads
serially and keeps every `Package` open, so take the idea, not the code.

### 2.3 Cache file

- **Location:** `Path.Combine(DivinityApp.GetAppDirectory(), "Data", "Cache", "mod-metadata.bin")`, beside
  `Data/settings.json`, consistent with the portable layout. If the directory cannot be written, the cache is
  skipped and the app logs once.
- **Format:** custom binary, little-endian, `BinaryWriter` with UTF-8 length-prefixed strings. Why: no new
  package, toolkit-neutral, and a ~5 MB file reads and parses in ~10-30 ms *(inferred)*. Newtonsoft would be
  slower here, and in Core its attributes are aliased (CLAUDE.md §5). The snapshot tool can dump the file for debugging.

```
offset  field
0       magic        8 bytes  "BG3MMMC\0"
8       schema       int32    = 1        // file layout; mismatch => discard
12      logicHash    uint64   // XxHash64 of LOGIC_VERSION + EXTENDER_MOD_CONFIG + MAX_FILE_OVERRIDE_DISPLAY
                              // + _ModFolderPattern + modMetaPattern + Path.DirectorySeparatorChar
                              // (any scanner/builder semantic edit invalidates without a manual bump)
20      payloadLen   int64
28      payloadHash  uint64   // XxHash64(payload); mismatch => corrupt => discard
36      payload:
          string appVersion (diagnostic)          int64 writtenAtUtcTicks
          uint64 ignoreBuiltinPathHash            int32 userCount, then userCount × entry
          int32  gameSectionPresent, then game section (C9)
entry:  string path; int64 size; int64 mtimeUtc; int64 scannedAtUtc; uint64 fingerprint; byte outcome;
        nstring errorText; nstring metaFileName; nstring metaText; nstring cfgName; nstring cfgText;
        nstring cfgError; byte osiris; strarr folders; strarr builtinHit; strarr overrideFolders;
        int32 baseGameFileCount; strarr baseGameFiles
        (nstring = bool present + string; strarr = int32 n + n strings)
```

Size: ~1,121 × (3-6 KB meta + small fields) ≈ 4-7 MB *(inferred)*. LZ4-compressing the payload with lz4net
(already a dependency) is an optional later step, only if E7 shows the read matters.

### 2.4 Keys and invalidation

1. **Whole file:** wrong magic, schema or `logicHash`; a payload hash mismatch; `EndOfStreamException`, an
   `IOException` while reading, or any other exception. In every case: log one line, delete the file, run cold.
   The cache never throws past the loader.
2. **Entry key:** path, compared with `OperatingSystem.IsWindows() ? OrdinalIgnoreCase : Ordinal`. Paks that
   were not enumerated this run are **pruned** at save time, which covers deleted, renamed and moved paks. A
   rename or move therefore costs one rescan of that pak, a few ms.
3. **Stat:** `size` and `mtimeUtc` must match exactly. Use NTFS 100 ns ticks; do not round.
4. **Racy-clean rule** (the git index approach, which handles the mtime granularity concern). If
   `mtimeUtc >= scannedAtUtc - 2 s` (2 s covers FAT/exFAT and SMB), or if the mtime is more than 1 min in the
   future, the entry is *racy*: recompute the fingerprint, and rescan on mismatch. The next save stamps a fresh
   `scannedAt`, so the entry stops being racy.
5. **Builtin context:** for the entry to be valid, `Folders ∩ B_now`, kept in first-appearance order, must be
   sequence-equal to `BuiltinHitFolders`. If `ignoreBuiltinPathHash` changed, only entries with a non-empty
   `BuiltinHitFolders` are invalidated. Why it matters: `SetLoadedMods` adds the game's real Larian mods to
   `IgnoredMods` (`MainWindowViewModel.cs:1508-1515`), so B on F5 can be a superset of B at cold start. A
   single global context hash would invalidate the whole cache on every refresh; the per-entry check only
   rescans the handful of paks that touch the changed folders.
6. **Errors:** cache `FormatError` outcomes (`InvalidDataException`, `NotAPackageException`,
   `NotSupportedException`), so a broken pak is not re-parsed on every launch. It is re-logged with the same
   message. **Never** cache `IOException` or `UnauthorizedAccessException`: a pak locked by another program
   gets retried on the next launch.
7. **Paranoid mode:** an extension-less `cacheverify` file next to the exe fingerprints every entry. Use E7 to
   decide whether this becomes the default. My estimate: ~10-50 ms warm on an SSD, but bad on an OS-cold HDD.
8. **Escape hatch:** delete the file. Optionally (C8b) add a "Refresh and rebuild mod cache" command. The
   keybinding system already exists (`Keys.LoadKeybindings`).
9. **Known limitation, documented:** a different pak with the *same* path, size and 100 ns mtime, outside
   the racy window. In practice only a tool that copies another file's timestamp onto a different build can
   cause this. Paranoid mode covers it.

### 2.5 Concurrency, cancellation, failure

- **Load:** at most DOP worker threads (§2.2 C5). `Parallel.ForEachAsync` keeps the threadpool flat. Expect
  fewer than the 59 threads of the baseline. Cancellation keeps the partial results, as today.
- **Save:** `QueueSave` runs on the taskpool after the load (it is off the critical path), and only if
  something changed (any miss, prune or racy refresh). A static `SemaphoreSlim(1)` stops two saves from one
  process overlapping on a fast F5. The write goes to `mod-metadata.bin.<pid>.tmp`, then `Flush(true)`, then
  `File.Move(tmp, final, overwrite: true)` (`MoveFileEx(REPLACE_EXISTING)`). Readers open the file with
  `FileShare.Read | FileShare.Delete`, so a reader never blocks a rename. On `IOException`, retry 3 × 50 ms,
  then give up with a log line. Stale `*.tmp` files older than 1 h are deleted on the next save.
- **Two app instances** (there is no single-instance guard, per CLAUDE.md §3): both read safely. Both may
  write, and the last rename wins. Entries are content-keyed, so either result is valid for both.
- **Partial writes or a crash mid-write:** the final file is never partially written (temp file plus rename).
  A truncated final file (disk full during copy-on-rename) fails the payload hash and the app runs cold.
- **Memory:** about 10-15 MB transient during load (meta text held as UTF-16), released afterwards. There is
  no resident cache. C4 removes the per-pak file-list retention. That should be the largest memory change of
  the stage *(inferred tens to 100+ MB; measure against 478 MB)*.

### 2.6 Stat source and hardlinks (ties to the LIB design)

NTFS directory-index entries, which are what `FindFirstFile` returns, can hold **stale size and mtime** for a
file reached through another hard link, or one still open for writing. `GetFileAttributesEx` (`FileInfo`)
reads the file record itself. The cache therefore validates with `FileInfo.Length` and
`FileInfo.LastWriteTimeUtc`, at about 20-40 µs per pak, which is negligible across 8 workers. This keeps the
cache correct under the planned per-order hardlink deployment, where a library pak updated through one link
would otherwise look unchanged through the `Mods` link. `LastModified` keeps today's value
(`File.GetLastWriteTime`, `:646`), because it is the same file record converted to local time.

### 2.7 Hook points summary

| Change | Location |
|---|---|
| profiler marks | as listed in §1.4 |
| facts/build split | `DivinityModDataLoader.cs:466-687` → `PakScanner.CollectFacts` + `BuildModFromFacts`; `:689-694` delegate |
| SE config text parse | `:1690-1725` → `ParseScriptExtenderConfig` |
| drop `Files` | `:628` |
| parallel + dedupe + single walk | `:745-808` |
| cache plumbing | `LoadModPackageDataAsync` gains an optional `ModMetadataCache` parameter. `MainWindowViewModel.cs:1665` passes `ModMetadataCache.Default` (null when the `nocache` file exists) |
| concurrency of the 3 loads | `MainWindowViewModel.cs:1621-1668` |
| snapshot | `MainWindowViewModel.cs:1716` (before `return`) |
| game discovery cache (C9) | `DivinityModDataLoader.cs:1783-1825` |

---

## 3. R1: the correctness guard

### 3.1 Snapshot harness (commit C2, Core plus a 3-line hook)

- **Trigger:** an extension-less `modsnapshot` file next to the exe. Optionally the file holds a label (`master`,
  `C5`, `C8-warm`). The harness is off unless the file exists, so it has no cost in normal use.
- **Hook:** `MainWindowViewModel.cs:1716`, just before `return [.. finalMods.OrderBy(m => m.Name)]`:
  `ModSnapshot.TryWrite(finalMods, modLoadingResults?.Duplicates, baseMods, label)`. It is called on the
  loading thread, and it reads only plain properties.
- **Output:** `_Logs/modsnapshot-<label>-yyyyMMdd-HHmmss.tsv`. Strings have tabs and newlines escaped. Rows
  are sorted by `UUID` (ordinal) and then `FilePath` (ordinal), so any two runs can be diffed.
- **Columns** (raw loader outputs only; Rx-derived OAPH values are excluded because they settle asynchronously
  on the Dispatcher): `UUID, Name, Folder, VersionInt, HeaderVersion, PublishVersionInt, ModType, Author, MD5,
  PublishHandle, FileSize, FilePath, LastModifiedTicks, HasMetadata, IsForceLoaded, IsForceLoadedMergedMod,
  IsEditorMod, IsLarianMod, IsHidden, IsUserMod, OsirisModStatus, HasScriptExtenderSettings, SE.RequiredVersion,
  SE.FeatureFlags(joined), Tags(joined), Dependencies(sorted "UUID@VersionInt"), Conflicts(sorted),
  XxHash64(Description), BuiltinOverrideModsText(escaped)`.
- **Second section:** `DUPLICATES` (UUID, FilePath, VersionInt), sorted the same way.
- **Third section:** `TIES`. Any UUID group in which two or more mods share the winning `VersionInt`. These
  are the cases where **master itself is nondeterministic** (F9), so the diff can treat them separately.
- **Header line:** label, git hash (from the assembly informational version), Debug or Release, cache mode
  (cold, warm, or `nocache`), pak count, mod count.

**How to run.** Keep each build in its own folder. `modsettings.lsx` is never written, so this is read-only
with respect to the game.

1. Build `C2` (master plus diagnostics). Run twice: `S0a`, `S0b`. `git diff --no-index S0a.tsv S0b.tsv`
   must be empty apart from `TIES` rows, and it **reveals the baseline's built-in nondeterminism**.
2. For each later commit Cn: add a `nocache` file, run (`Sn-cold`). Remove `nocache`, run twice (the first run
   fills the cache: `Sn-fill`; the second: `Sn-warm`).
3. Acceptance: `diff(S0a, Sn-cold)`, `diff(Sn-cold, Sn-warm)` and `diff(Sn-fill, Sn-warm)` are all empty,
   except for rows in `TIES`, where any member of the tie may win in S0 but must be **the first in enumeration
   order** in Sn.
4. After C7, also run `verifyscan` once with 0 mismatches. After C9, run `verifyscan` with the game section included.

### 3.2 Every place this change could alter the winner or the classification

| # | Risk | Where | Guard |
|---|---|---|---|
| 1 | Result order changes which mod wins a **tie** | old `:788-806` (bag order), new `SplitDuplicates` | New order = enumeration order, which is one of master's possible outcomes. Shown in the `TIES` section. |
| 2 | Regex semantics in the hand parser (lazy group, case-sensitive prefix, `\n`, `Path.GetFileName` treating `\` as a separator on Windows) | `:458,500-503` | Exact rules plus a regex fallback for odd names; `verifyscan` |
| 3 | The `else if` precedence: config entries never count as meta or folder data; meta entries never count as folder data | `:490-499` | Same branch order in both collectors; `verifyscan` |
| 4 | Meta choice when several metas exist (`Directory.GetParent(f.Name).Name` against the pak name) | `:571-588` | The selection code is moved, not rewritten |
| 5 | `IgnoreBuiltinPath` substring test, `f.Size() > 0` (including `Method()` throwing), the Game/GUI `.xaml` special case | `:529-543` | Same calls on materialised names |
| 6 | Stale cache entry yields an old Version and so a wrong dedupe winner | cache | stat, racy rule, fingerprint, logicHash; `Sn-cold` vs `Sn-warm` diff |
| 7 | Builtin set B differs between the scan and now (F5 refresh) | `:747`, `MainWindowViewModel.cs:1508-1515` | Per-entry `BuiltinHitFolders` check (§2.4 #5) |
| 8 | Concurrency changes the B snapshot or the merge order | C6 | B is captured once. Merges run after `WhenAll` in the original order (`:1670-1714`) |
| 9 | Editor-project dedup (if it is ever done) changes `baseMods`: duplicate-toolkit grouping (`:1675-1694`), the pak-vs-base dup test (`:1695-1702`) that skips editor mods, the `baseMods.Count < IgnoredMods.Count` fill (`:1645`), and the edge where an `IgnoreModByFolder` folder exists only in the VFS result (`:406` vs `:1758-1762`) | `LoadModsAsync` | **Not done in this series.** If it ever is, read each meta once and build both variants from the same text. |
| 10 | Corrupt or unreadable pak: today it is logged and skipped | `:707-710` | Same message; FormatError cached; IO errors not cached |
| 11 | Override UUID = path string | `:620` | The UUID always comes from the runtime-enumerated path (`ToSpecifiedFullPath`), never from the cache |
| 12 | Partial-pak skipping (`_AllPaksNames`, `(_[0-9]+)$`) | `:429-446,763-764` | Unchanged, including the never-cleared static set |
| 13 | `LastModified` source changes | `:646` | Same file record (§2.6); in the snapshot |
| 14 | Game discovery (C9): priority tie, attach order, case sensitivity, filesystem fallback, `Mods` dict order | `VFS.cs:42-45,165,417-433`, `ModResources.cs:233-250` | `verifyscan` game section; snapshot |

---

## 4. Branch and PR series (`perf/startup-load`, cut from `master`)

Stack the sub-branches, each cut from the previous one, all rooted at `master` `fc35358`. Merge each into
`fork/main` with `--no-ff` for daily use, and open upstream PRs in order. Nothing depends on fork-only code.

| Commit | Change | Expected gain (estimate) | Risk | Measure |
|---|---|---|---|---|
| **C1** `perf: add load-phase profiler` | §1.4 | 0 (off by default) | none | run E1-E6 and **rewrite the §1.2 ranking from real numbers** |
| **C2** `chore: mod-list snapshot harness` | §3.1 | 0 | none | S0a/S0b, which reveals ties |
| *PR-1 upstream: "Load diagnostics" (C1 + C2)* | | | | |
| **C3** `refactor: split pak loading into facts and build` | §2.2 | ~0 | low | snapshot = S0 |
| **C4** `perf: stop retaining every pak's file list` | `:628` | memory −? MB, fewer gen2 GCs | none (write-only field) | private bytes, `gc2`, snapshot |
| **C5** `perf: bounded parallel pak scan, deterministic O(n) dedupe` | §2.2 | **if E1 confirms serial: pak phase ÷3-6** | low-medium (ties, log contention) | parallelism factor, snapshot + TIES |
| **C6** `perf: load base game, editor projects and paks concurrently` | §2.2 | hides `game.*` + `editor` (0.5-2 s) | low | wall clock vs Σ phases |
| *PR-2 upstream: "Parallel, deterministic mod loading" (C3-C6)* | | | | |
| **C7a** `perf: allocation-free entry classification` | §2.2 | per-entry classify ÷3-5 | low-medium | `verifyscan` = 0 mismatches, CSV slope |
| **C7b** `perf: lightweight LSPK v18 table reader` *(only if the C7a CSV shows decode still dominates)* | §2.2 | per-entry decode ÷5-10, allocs −90% | medium | `verifyscan`, snapshot, CSV |
| *PR-3 upstream: "Cheaper pak scanning" (C7)* | | | | |
| **C8** `perf: persistent pak metadata cache` | §2.3-2.6 | warm pak phase → ~0.2-0.5 s | medium (staleness) | cold/fill/warm snapshots, E7, §5 matrix |
| **C9** `perf: cache base-game mod discovery` *(conditional)* | §2.2 | `game.*` → ~10 ms warm | low-medium | `verifyscan` game, snapshot |
| *PR-4 upstream: "Mod metadata cache" (C8 + C9)* | | | | |

**Out of scope for this branch.** Each is a separate `fix/*` cut from master, so the perf diffs stay pure:

- `_AllPaksNames` is never cleared, so after `Foo.pak` is deleted, `Foo_1.pak` stays skipped until restart (`:429,763`).
- The editor meta is read with a single, unchecked `ReadAsync`, which can return a short read (`:346-347`).
- Dependency `PublishHandle` is read from `moduleInfoNode` instead of from the dependency `node` (`:269,296`).
  **Do not fix this in perf:** it would change snapshot rows.
- `LoadResourceAsync(path, format)` reads the whole file into a discarded buffer and then parses from the
  stream again (`:968-971`). This is about 0.47 s of profile time, so it belongs in a tiny optional `perf`
  commit (C10), outside the 9.27 s.

### Expected end state *(inferred; to be replaced by C1 numbers)*

| Scenario | `LoadModsAsync` today | after C3-C7 (no cache) | after C8-C9 (warm) |
|---|---|---|---|
| Debug, app-cold (OS cache warm), as the baseline was measured | 9.27 s | **~1.5-3 s** | **~0.4-1.0 s** |
| First launch after a **game patch** | 9.27 s | ~1.5-3 s | user paks still warm; game section rescans → ~0.5-1.5 s |
| After updating **N mods** in place | 9.27 s | ~1.5-3 s | ~0.4-1.0 s + N × ~2-5 ms / DOP |
| OS-cold HDD | ≫ 9 s *(likely I/O-bound)* | ~2× better (overlapped reads) | ~0.5-1.5 s (one `FileInfo` per pak, no opens) |

The whole cold start would go from ~15.2 s to ~8-9.5 s, and warm to ~6.5-7.5 s. The remaining time is the
3.61 s before refresh, profiles at 0.47 s, list building at 1.26 s and UI at 0.63 s, all outside Stage 4.
If E4 or E5 show that Defender or I/O dominates, the cold gain shrinks to ~2× and the cache carries the win.

---

## 5. Test matrix

Every row: snapshot diff against the reference run must be empty (apart from TIES), there must be no new
exceptions in the log, and profiler numbers are recorded.

| # | Scenario | Setup | Expected |
|---|---|---|---|
| T1 | Cold, no cache | `nocache` file, or delete `Data/Cache` | `Sn-cold` = S0. Cache written once after the load. |
| T2 | Warm | the second run | `Sn-warm` = `Sn-cold`. 0 scans in CSV (all hits). |
| T3 | Game patch (built-in paks change) | touch or replace a Data pak (or switch game version); also an `IgnoredMods.json` with a new module | Game section misses; user entries hit unless their folders ∩ B changed. Paks overriding the new folder are rescanned, and their override flags match a cold run. |
| T4 | Mod updated in place, new size | overwrite `X.pak` with another version | Miss on size; new Version in snapshot; dedupe matches cold. |
| T5 | In place, **same size**, mtime changed | rebuild a pak with the same total bytes (pad a file) | Miss on mtime. |
| T6 | Same size, **same mtime** inside the racy window | write the pak, launch within 2 s, change the pak again keeping size and mtime (`SetLastWriteTimeUtc`) | Racy, so fingerprint mismatch, so rescan. |
| T7 | Same as T6 outside the window | as T6, with mtime 1 h older than `scannedAt` | Known limitation: stale. Caught with `cacheverify`. Documented. |
| T8 | FAT32/exFAT or SMB Mods folder (2 s mtime) | junction or move the Mods folder | Racy rule covers it; warm run stops being racy after the next save. |
| T9 | Renamed pak | `X.pak` → `Y.pak` | `X` pruned, `Y` scanned. An override pak's UUID changes to the new path, exactly as on master. |
| T10 | Moved into a subfolder | `Mods/X.pak` → `Mods/sub/X.pak` | Same as T9. Recursive enumeration finds it, as on master. |
| T11 | Deleted pak | remove `X.pak` | Gone from the list; entry pruned at save. |
| T12 | Duplicate UUIDs, different versions | two paks, one UUID, v1 and v2 | v2 wins; v1 in DUPLICATES; duplicate alert shown. Identical cold and warm. |
| T13 | Duplicate UUIDs, **equal versions** | two copies of one pak under different names | TIES row. Winner = first in enumeration order, stable across runs (it is not stable on master). |
| T14 | Pak with the same UUID as a Larian or base mod | copy of a builtin UUID | Pak-vs-base duplicate path (`:1695-1702`) unchanged. |
| T15 | Editor projects | 2 folders in `Data/Mods` (one with `ScriptExtender/Config.json`), plus one with a name in `IgnoredMods` | Same as master: the editor version wins the merge; the ignored-folder one keeps its VFS variant. |
| T16 | Override pak without meta.lsx | pak with only `Public/Shared/...` files | `UUID == FilePath`, `IsForceLoaded`, `Folder = first override folder`, `BuiltinOverrideModsText` identical; same after warm. |
| T17 | Override pak whose builtin entries are all under `IgnoreBuiltinPath` | only `Public/Game/GUI/Assets/...` | `isOverriding = hasBuiltinDirectory` path (no meta): `Folder` null, as on master. |
| T18 | Merged mod (meta plus a builtin folder) | pak with `Mods/MyMod/meta.lsx` + `Public/Shared/...` | `IsForceLoadedMergedMod = true` cold and warm. |
| T19 | Multi-part, solid, v15/v16 paks | samples (or synthetic via LSLib) | LSLib fallback; `verifyscan` 0 mismatches. |
| T20 | Corrupt pak | truncate a pak | Same log as master, mod absent. FormatError cached; warm skips it and re-logs. Restoring the pak changes the key, so it is rescanned. |
| T21 | Locked pak | hold it open exclusively | IOException, not cached; retried on the next launch. |
| T22 | Corrupted cache | flip a byte or truncate `mod-metadata.bin` | Payload-hash failure logged, file deleted, cold load, rewrite. |
| T23 | Older or newer schema cache | hand-edit `schema` or `logicHash` | Discarded and rewritten; no exception. |
| T24 | Two instances | start two copies at once; F5 in both | Both load correctly; the final file is valid; no `.tmp` files left behind (after 1 h cleanup). |
| T25 | F5 after a load that added Larian mods to B | Data folder with an extra Larian-authored pak | Only paks touching the changed folders rescan. Snapshot equals master's F5 snapshot. |
| T26 | Hardlinked pak updated through the other link | `mklink /H`, update the library copy | `FileInfo` stat sees the change, so a miss (§2.6). |
| T27 | Read-only app folder | Program Files install without write rights | Cache skipped with one log line; load works. |
| T28 | Publish build | R5 | All of the above pass with the `_Lib` layout. |

---

## 6. What survives an Avalonia or Linux port

- **Everything new lives in Core with no WPF types:** `PakFacts`, `PakScanner`, `ModMetadataCache`,
  `LoadProfiler`, `ModSnapshot`, and `BuiltinContext`. They use only BCL APIs and LSLib's public API
  (`RandomAccess`, `FileSystemEnumerable`, `System.IO.Hashing`, `CompressionHelpers`). The cache file format
  is platform-neutral.
- **Already platform-aware:** the path comparer (case-insensitive on Windows, ordinal elsewhere) and
  `Path.DirectorySeparatorChar` folded into `logicHash`. `Path.GetFileName` treats `\` differently on
  Linux; the scanner calls the same BCL functions as the legacy code, so it matches whatever the legacy code
  does on each OS. Rename-over-open is simpler on Linux. ext4 mtime is ns, so the racy rule still holds. Under
  Proton the AppData path differs; `GetAppDirectory` already carries that note (CLAUDE.md §6).
- **Ports with renames only:** `BuildModFromFacts`, because upstream/avalonia renames `DivinityModData` to
  `ModData` and changes flags such as `IsLooseMod`. The facts/build seam is exactly where a port re-targets it.
- **GUI-coupled and left untouched:** the `DivinityModData` constructor's Rx pipelines with
  `RxApp.MainThreadScheduler` (F13). They are a real cost, but they belong to the view-model layer, and an
  Avalonia port with source-generated RxUI 23 would rewrite them anyway. The C1 `ui.dispatcherLatency`
  number tells whether they are worth a later stage.
- **Upstream/avalonia's direction agrees:** it drops the VFS for game data. C9's meta-only game scan is
  compatible with that, and C5's bounded, deterministic pipeline is something that branch lacks (it reads
  packages serially and keeps all of them open: `DirectoryPakParser.cs:256-268`).
