# Stage 4, Perf A: startup load. Design angle C: architecture-first, port-proof mod index

Author: design agent, 2026-11-03. Static analysis only: this Linux container cannot build or run the WPF
app. Every claim is tagged **[certain]** (read in the source, cited `path:line`) or **[inferred]**
(reasoned, not measured). Abbreviations: `DMDL` = `src/Core/Util/DivinityModDataLoader.cs`,
`MWVM` = `src/GUI/ViewModels/MainWindowViewModel.cs`, `DMD` = `src/Core/Models/DivinityModData.cs`,
`LSLib/` = `External/lslib/LSLib/LS/`.

Read alongside: `docs/review/stages/alternatives/S4-A-minimal.md` (the minimal angle on the same stage) and `docs/review/areas/AUDIT-PLAN.md`
findings 06–09. `docs/review/areas/LOAD.md` did not exist when this was written (folder re-checked at the end).
This design agrees with S4-A's ranking but differs in two places, called out in §1.2 and §1.3.

---

## 0. Summary

**Headline.** Split the loader into three layers that already exist implicitly in
`InternalLoadModDataFromPakAsync` (`DMDL:466-687`):

1. **Facts**: what is physically in a pak. File-table summary, chosen `meta.lsx` text, extender config
   text, Osiris status. These depend only on the pak's bytes and its path.
2. **Classification**: force-loaded, merged, override-only, `BuiltinOverrideModsText`. These depend on
   the facts **and** on the current builtin-folder set (`IgnoredMods`) and `IgnoreBuiltinPath`.
3. **Model**: building the WPF-bound `DivinityModData`.

Facts are cached on disk per pak, keyed by full path and validated by size, mtime, ctime and a 72-byte
header/tail sentinel. Classification is **recomputed on every load** from cached facts, so a cache can
never freeze a stale override decision. This is the core of the R1 guard. A Core-level, toolkit-neutral
`ModIndexService` owns the facts cache for user paks and a second cache for the game's built-in
modules. The service also gives background warm-up and an optional file-watcher. The WPF adapter
(`DivinityModData` construction) stays in `DMDL`, so a CLI or an Avalonia front end reuses everything
above layer 3.

**Ships in 10 small commits on `perf/startup-load`, cut from `master`** (§4). The first two give the
owner the snapshot harness and the timings. The next four are cheap, cache-free wins on the cold path:
drop the unused file lists, early-exit goal reads, stop probing the screen reader per mod, and get the
load off the UI thread and parallel. Only then does the cache land.

**Expected effect [inferred, to be confirmed by §1.5 numbers], Debug build, vs the 9.27 s "mods
finished loading" window:**

| Scenario | Today | After cold-path commits (P3–P6) | After cache (P7–P9) |
|---|---|---|---|
| Cold (no cache, or first run after an app update) | 9.27 s | ~2.5–4.5 s, and the UI stays responsive | same ~2.5–4.5 s |
| Warm (nothing changed) | 9.27 s | ~2.5–4.5 s | **~0.5–1.2 s** |
| Warm, 5 mods added/updated | 9.27 s | ~2.5–4.5 s | ~0.6–1.3 s |
| Game patched (built-in paks changed) | 9.27 s | ~2.5–4.5 s | warm + ~1–2.5 s once |
| Memory, private bytes (478 MB) | 478 MB | −(E × ~150–200 B); E is the total pak entry count | a further −30–80 MB of GC slack (speculative) |

The warm floor (~0.5 s) is `DivinityModData` construction (~40 Rx pipelines per mod, `DMD:386-567`). That
is Stage 6's data-model work (`docs/review/stages/alternatives/S6-A-data-model.md`), not this stage's.

### Owner intentions this design is built around (gleaned from the notes)

You asked for this list. Each item cites its source.

1. **Purpose:** a fork for you and friends, published publicly. Four workstreams, in your words: bugfixes
   (zip export, archive import), performance (startup, scrolling, filtering), UI (dated look, UX) and
   features (multi-order export, categories/dividers, overrides folded into the order; later a downloader,
   order syncing with friends, native mods). *(plan §1)*
2. **Platform:** stay on WPF `master`. Windows now, Linux kept open, Steam Deck "nice, not a driver".
   Prefer toolkit-neutral choices when they cost nothing. Note where an Avalonia port would invalidate work.
   *(plan §2)*
3. **Upstream:** only bugfixes and perf go to LaughingLeader. They go on `fix/*`, `perf/*` or `chore/*` cut
   from a clean `master`. Fork-only work lives on `fork/main` (`feat/*`, `ui/*`). Merge with `--no-ff`, one
   branch per issue. Merge, never rebase, when syncing upstream. Never define the `merge.ours` driver.
   *(plan §3, CLAUDE.md §8)*
4. **Measure first:** instrumentation before optimisation; numbers over argument; `TEMP:` commits for
   measurement scaffolding that are dropped before a PR (visible in `wt-perf`'s log). Debug numbers are the
   relative control, not absolute truth. *(plan §8 "Measure first", §4 0.3, plan-overview)*
5. **Correctness outranks speed (R1):** a perf change must never alter which mod wins dedupe or how
   force-loaded/override mods are classified. You want a before/after snapshot diff of the full mod list
   for every Stage 4 change. *(plan §8, §14 R1)*
6. **Categories:** not via WPF grouping, because it kills virtualization (R2). Redux: general ideas only,
   never its code, a stricter stance than its MIT licence requires. *(plan §2)*
7. **House rules:** keep the ReactiveUI.Fody 19.5.41 + ReactiveUI 20 pin. Surface errors with `ShowAlert`
   (R8) and log with `DivinityApp.Log`. No `string?` in Core/GUI. Tabs, CRLF, BOM, Allman, `var`. New
   types unprefixed. *(CLAUDE.md §4–5)*
8. **Safety before distribution:** repoint the updater (Stage 1) before any public build (R4). Test
   Publish as well as Debug (R5). *(plan §14)*
9. **You verify, then decide:** the board's "What to verify" list asks you to check behaviour yourself
   and to say whether to push. Nothing is pushed or upstreamed without your go-ahead. Your real
   `modsettings.lsx` must stay untouched (verified byte-for-byte last time). *(plan-overview)*
10. **Docs in sync:** update `implementation-plan.html` and `plan-overview.html` together on any status
    change. A stale board is worse than none. *(CLAUDE.md header)*
11. **Accessibility matters:** screen-reader behaviour is on the verify list. *(plan-overview)* §1.3 S3
    keeps the screen-reader check, just not per mod.
12. **Fuzzy search wanted** (fork-only), but only after the per-keystroke cost is fixed. *(plan §9)*
13. **Plain-language status** for you; detail lives in the plan. *(the two-document split)*

---

## 1. Where the 9.27 s most likely goes

### 1.1 What runs in the window [certain]

`RefreshAsync` (`MWVM:2340`) → `await SetMainProgressTextAsync("Loading mods...")` (`:2374`) →
`RunTaskStep(LoadModsAsync)` (`:2375`). `LoadModsAsync` (`MWVM:1609-1718`) runs three loads **in sequence**:

| # | Call | Work |
|---|---|---|
| a | `LoadBuiltinModsAsync` (`MWVM:1627` → `DMDL:1783-1825`) | `VFS.AttachGameDirectory` reads the file table of every non-blacklisted pak in `Data/` (flat) and `Data/Localization/` (depth 3) (`LSLib/VFS.cs:173-188`). Blacklist at `VFS.cs:93-117`; `Game.pak`, `Gustav.pak`, `Shared.pak` and language paks are **not** blacklisted. `FinishBuild` inserts every entry of every package into a directory tree, with two substring allocations per path segment (`VFS.cs:190-223`). `ModPathVisitor.DiscoverMods` (`LSLib/Mods/ModResources.cs:231-251`) runs, then each module's `meta.lsx` is parsed (`DMDL:1727-1781`). Runs under a 30 s token (`MWVM:1626`) that nothing inside observes. |
| b | `LoadEditorProjectsAsync` (`MWVM:1637` → `DMDL:397-427`) | Sequential reads of `Data/Mods/*/meta.lsx`. About 0 folders for this owner (AUDIT-PLAN claim 28). |
| c | `LoadModPackageDataAsync` (`MWVM:1665` → `DMDL:745-808`) | Recursive enumeration (`:757-764`), then per pak `PackageReader.Read` (`DMDL:691-692`). That maps the whole file (`LSLib/PackageReader.cs:61-67`), LZ4-decodes the file table into one `byte[]` of `272 × numFiles` (`:99-129`), copies it again (`BinUtils.cs:78-91`, `reader.ReadBytes`), and `Marshal.PtrToStructure`s each `FileEntry18`. Each entry allocates a `byte[256]` name array and a string (`PackageFormat.cs:419-441`). A solid pak (`Flags & Solid`) gets its **entire data segment** decompressed (`PackageReader.cs:171-174, 179-233`). Then the entry loop (`DMDL:485-558`), meta/extender reads (`:594-598`, `:657-669`) and dedupe (`:787-806`). |
| d | merge, duplicate checks, sort | `MWVM:1670-1717`. `MergeModLists` is O(n²) (`:1535-1553`): ~0.6 M string compares, tens of ms. |

### 1.2 Which thread: the UI thread, including the "parallel" tail [inferred, high confidence]

`RefreshAsync` starts on a pool thread (`MWVM:4145`, `:4990`). Its first `await` is on an Rx
`Observable.Start(..., RxApp.MainThreadScheduler)` (`MWVM:2232-2239`). On a pool thread,
`SynchronizationContext.Current` is null. Rx's `AsyncSubject` awaiter then runs the continuation **inline
on the thread that completed the subject**: the WPF Dispatcher thread, inside a dispatcher operation. From
then on `SynchronizationContext.Current` is the `DispatcherSynchronizationContext`. **Every `await` in
`LoadModsAsync` and in `DMDL` (none use `ConfigureAwait(false)`) captures it and resumes on the UI
thread.**

S4-A §1.2 reaches the same conclusion for the synchronous prefix but assumes the tail after the first
yielding `await` runs on the pool. It does not: `InternalLoadModDataFromPakAsync`'s
`await sr.ReadToEndAsync()` (`DMDL:518, 597`) resumes through the captured dispatcher context. So:

- The whole of step c (open, table, loop, meta parse, `DivinityModData` construction) is **serial on the UI
  thread**. Each LZ4 meta read is one extra dispatcher round-trip (`LZ4DecompressionStream` has no
  `ReadAsync` override, `LSLib/Compression.cs:8-64`).
- `Task.WhenAll` over 1,097 tasks (`DMDL:777-784`) buys no parallelism. At most, the BCL-internal
  decompress work runs on the pool.
- The window freezes during the load. Confirm with one look: does the progress bar animate during
  "Loading mods from Local AppData folder..."?
- 24.2 s CPU over 15.2 s wall (plan §4 0.3) = ~1.6 cores on average. That fits one busy UI thread plus
  the GC background thread, tiered JIT, the render thread and the pool doing decompression.

Consequence for the design: **moving the work off the UI thread is a prerequisite for any parallel
speedup**, and it is cheap (§2.9 P5).

### 1.3 Suspects, ranked

| Rank | Suspect | Evidence | Status | Rough cost (Debug) |
|---|---|---|---|---|
| S1 | **Per-entry work on user paks, serial.** Table decode + marshal + `PackagedFileInfo` + string (above), then per entry in the loop: `HashSet.Add` (`DMDL:488`), `Contains(EXTENDER_MOD_CONFIG)` (`:490`), `GetFileName` + regex for meta (`:449-456`), the **interpreted** regex `^(Mods\|Public)/(.+?)/.+$` (`:458, :500`), `GetFileName`/`TrimEnd` (`:503`), an interpolated `$"Mods/{modFolder}/Story/RawFiles/Goals"` allocation + `Contains` (`:504`), a dictionary lookup (`:526`). | Code certain; cost inferred | E × 3–7 µs |
| S2 | **Built-in VFS** (step a). Hundreds of thousands of entries across Gustav/Shared/Game/Localization, each decoded and inserted into a tree. **Runs before step c, serially, on the UI thread, although step c does not need its result** (user-pak classification uses `DivinityApp.IgnoredMods` from the JSON, `DMDL:747`, not the VFS result; §3.3 H12). | Certain that it is serial and independent; size inferred | 0.8–2.5 s |
| S3 | **Screen-reader probe per mod (new, not in S4-A or AUDIT-PLAN).** `DMD:520-524` subscribes `HasToolTip` with `ToUIPropertyImmediate` and calls `DivinityApp.IsScreenReaderActive()` (`src/Core/DivinityApp.cs:124-127`) on the initial value and on every change of `Description`, `HasDependencies` and `UUID`. That is ~3–4 calls per mod, ~4,000 per startup. `ScreenReaderService.IsScreenReaderActive` → `EnsureInit` (`src/Core/AppServices/ScreenReaderService.cs:24-31, 49-79`). When **no** screen reader is running, `IsLoaded()` stays false, so **every call re-runs `CrossSpeakManager.Initialize()`** → `Tolk.Load()` + `Tolk.DetectScreenReader()` (`External/CrossSpeak/CrossSpeak/ScreenReaderWindows.cs:23-33, 35, 41`). What Tolk does on a repeat `Load` is closed-source here (only `lib/windows/Tolk.dll`). If it is a no-op and detection is a few window/RPC probes, the cost is ~0.1–1 ms per call. | Call chain certain; cost **unknown** | 0.05–4 s, the widest error bar in this table |
| S4 | **GC pressure from S1.** ~850 B allocated per entry (S4-A's estimate agrees). `DivinityModData.Files` **retains** every entry name of every pak (`DMDL:478, 488, 628`; property `DMD:234`); nothing reads it (AUDIT-PLAN-07, re-checked: grep finds only the assignment). | Retention certain; size inferred | part of S1; memory E × ~150–200 B |
| S5 | **Per-pak fixed cost.** `File.OpenRead` + `MemoryMappedFile.CreateFromFile` + a full-length view (`PackageReader.cs:61-67`), first-touch page faults on header and table, possibly Defender on open. | Inferred | 0.1–1 ms × 1,097 |
| S6 | **Osiris goal files read in full,** without early exit (`DMDL:504-523`). | Certain | unknown; usually small for BG3 mods |
| S7 | Meta parse (`XElement.Parse` + ~15 `Descendants("attribute")` scans, `DMDL:73-104, 168-325`) + the `DivinityModData` ctor (~40 Rx pipelines, `DMD:386-567`; `CanDelete` does a `File.Exists` per `FilePath` change, `DMD:526-528`). | Inferred | 0.3–0.6 ms × 1,121 |
| S8 | Dispatcher round-trips from §1.2, `MergeModLists` O(n²), double lazy enumeration (`DMDL:763-764`) | Certain | < 0.3 s together |

The plan's 4.2 candidates: per-entry regex = part of S1 (likely dominant). **MD5 = killed** (no hashing
anywhere: AUDIT-PLAN claim 27; my grep agrees). **No cache = confirmed**, and it is the centrepiece here.
**Double enumeration = real, negligible for ~0 editor projects.**

### 1.4 Per-pak cost model

```
T_load ≈ T_builtin + T_user + T_tail                                   (today: summed, serial, UI thread)

T_user  ≈ N·c_open + E·(c_tbl + c_loop) + G·c_goal + N_solid·c_solid
T_tail  ≈ M·(c_meta + c_ctor + k·c_sr)                                 M = 1,121 mods, k ≈ 3–4 probes per mod
T_builtin ≈ N_g·c_open + E_g·(c_tbl + c_tree) + M_g·c_meta

N   = 1,097 user paks (27 GB, avg 25 MB)     c_open  ≈ 0.1–1 ms
E   = Σ entries over user paks  (UNKNOWN)     c_tbl   ≈ 1–3 µs   (LZ4 + PtrToStructure + byte[256] + UTF-8)
G   = goal files read (UNKNOWN)               c_loop  ≈ 1.5–4 µs (Debug JIT; regex interpreted)
N_g ≈ 25 attached of 48 game paks             c_tree  ≈ 1–3 µs   (VFS.TryAddFile substrings + dict inserts)
E_g = Σ entries in attached game paks         c_meta  ≈ 0.1–0.3 ms, c_ctor ≈ 0.1–0.4 ms, c_sr ≈ ? (S3)
```

Plausible fits to 9.27 s: E ≈ 0.8–1.5 M with S3 small, or E ≈ 0.3 M with S3 ≈ 2–3 s. **E and the S3
total are the two numbers that decide the ranking; nobody has measured them.** After the cache, the warm
cost per pak is ~2 syscalls (stat + sentinel) + one string-table read: ~0.05–0.3 ms, independent of E.

### 1.5 Instrumentation: confirm before optimising

Two commits. **P2** is a permanent, low-noise phase log that upstream can keep. **T1 (TEMP)** adds per-pak
counters and probes and is dropped before the PR, as the owner already does on `perf/list-render`.

**Before writing any code:** the current log already contains two of these numbers, from the 0.3
baseline run. Grep it for `Took … second(s) to load mod paks.` (`DMDL:786`) and
`Took … seconds(s) to load builtin mods` (`DMDL:1816`). The builtin line times only the meta loop, not
the VFS build. The difference to the 9.27 s window is the VFS build plus editor projects plus merge.

**P2: `src/Core/Util/LoadTrace.cs` (new, permanent).**

```csharp
using System.Diagnostics;

namespace DivinityModManager.Util;

/// <summary>Phase timings for startup and refresh, written through DivinityApp.Log with a [LoadTrace] tag.</summary>
public static class LoadTrace
{
	private static readonly DateTime _processStart = Process.GetCurrentProcess().StartTime;

	public static long Start() => Stopwatch.GetTimestamp();

	public static void Stop(string phase, long start, string extra = "")
	{
		var elapsed = Stopwatch.GetElapsedTime(start);
		var sinceStart = DateTime.Now - _processStart;
		DivinityApp.Log($"[LoadTrace] T+{sinceStart.TotalSeconds:F2}s {phase}: {elapsed.TotalMilliseconds:F0} ms" +
			$" tid={Environment.CurrentManagedThreadId} pool={Thread.CurrentThread.IsThreadPoolThread}" +
			$" alloc={GC.GetTotalAllocatedBytes() >> 20} MB heap={GC.GetTotalMemory(false) >> 20} MB" +
			$" gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} {extra}");
	}
}
```

Points (each is `var t = LoadTrace.Start();` before and `LoadTrace.Stop("<name>", t);` after):

| Name | Where |
|---|---|
| `Refresh.Total` | `MWVM:2340` entry → after the last `Observable.Start` (`:2487-2550`) |
| `Refresh.LoadMods` | around `MWVM:2375` |
| `Refresh.Profiles` | around `:2380` |
| `Refresh.SelectedProfile` | around `:2387` |
| `Refresh.ExternalOrders` | around `:2401` |
| `Refresh.SetupLists` | inside the `Observable.Start` at `:2416`, split as `SetLoadedMods` (`:2419`) and `BuildModOrderList` (`:2453`) |
| `Refresh.Finalize` | the `Observable.Start` at `:2487` |
| `LoadMods.Builtin` / `.Editor` / `.UserPaks` / `.Merge` | around `MWVM:1627`, `:1637`, `:1665`, `:1670-1714` |
| `Builtin.Attach` / `.FinishBuild` / `.Discover` / `.Meta` | `DMDL:1790`, `:1791`, `:1803`, `:1808-1815`; `extra` = package count and Σ `Files.Count` |
| `UserPaks.Enumerate` / `.Scan` / `.Dedupe` | `DMDL:757-764`, `:777-784`, `:787-806`; `extra` = pak count |
| `Memory.AfterRefresh` | end of `RefreshAsync`; `extra` = `Process.GetCurrentProcess().PrivateMemorySize64 >> 20` and `GC.GetGCMemoryInfo()` `HeapSizeBytes`, `TotalCommittedBytes`, `FragmentedBytes` |

The `tid`/`pool` fields test §1.2 directly. If `LoadMods.UserPaks` logs `pool=False` with the UI thread's
id, the hypothesis holds.

**T1 (TEMP): per-pak counters.** One static class with `Interlocked.Add` on `long` fields, summarised
once at the end of `LoadModPackageDataAsync`:

```csharp
namespace DivinityModManager.Util;

internal static class PakScanStats
{
	public static long Paks, Entries, MaxEntries, SolidPaks, GoalReads, MetaLz4, Failures;
	public static long ReadTicks, LoopTicks, GoalTicks, MetaTicks, ExtenderTicks;
	public static long ScreenReaderCalls, ScreenReaderTicks;

	public static void AddMax(long value)
	{
		long current;
		while (value > (current = Interlocked.Read(ref MaxEntries)) && Interlocked.CompareExchange(ref MaxEntries, value, current) != current) { }
	}

	public static string Summary() => $"paks={Paks} entries={Entries} max={MaxEntries} solid={SolidPaks} goalsRead={GoalReads} metaLz4={MetaLz4} fail={Failures}" +
		$" read={Ms(ReadTicks)} loop={Ms(LoopTicks)} goals={Ms(GoalTicks)} meta={Ms(MetaTicks)} se={Ms(ExtenderTicks)}" +
		$" sr={ScreenReaderCalls}/{Ms(ScreenReaderTicks)}";

	private static string Ms(long ticks) => $"{ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0}ms";
}
```

Probe sites: `pr.Read` (`DMDL:692`, plus `Solid` flag and `Files.Count`); the loop (`:485-558`); the goal
block (`:516-522`); meta read and parse (`:594-599`, plus `metaFile.Flags.Method() == LZ4`); extender
(`:657-669`); and `DivinityApp.IsScreenReaderActive()` (`src/Core/DivinityApp.cs:124-127`), the S3
probe. Log `PakScanStats.Summary()` at `DMDL:786`.

**Is 478 MB live data or GC slack? (TEMP, one line):** under the `debug` file only, after
`Memory.AfterRefresh` run `GC.Collect(2, GCCollectionMode.Forced, true, true)`, then log
`GC.GetTotalMemory(false)` and private bytes again. A large gap means slack (fragmentation, LOH, the VFS
spike), which the cache helps. A small gap means live objects, and then `Files` (S4) and the
`DivinityModData` graph (Stage 6) are the levers. For an exact breakdown, take
`dotnet-gcdump collect -p <pid>` after startup and open it in Visual Studio; this needs no code.

**Protocol:** Debug, three launches, report the median. One extra launch right after a reboot is the
"cold OS cache" variant (I/O-bound), and it decides whether HDD/SSD matters for the sentinel (§2.4). One
Publish run as a sanity check: Core is optimised outside Debug (`DivinityModManagerCore.csproj:13`); GUI
is not (`GUI.csproj:38-41`, per BLD).

**Decision gate G1** (after T1, before P7):

| If… | Then |
|---|---|
| `sr` ≥ 0.3 s | P4b (memoise the screen-reader probe) moves to the front of the series |
| `Builtin.*` ≥ 1 s | P9 (game cache) is worth it; P12 (lean game scan) becomes a candidate |
| `entries` ≥ 0.5 M, `loop + read` dominates | the cache is the main lever; P11 (lean table reader) is a candidate for cold start |
| `entries` small, `read` dominated by per-pak time | the sentinel's open cost matters; consider `VerifySentinel = false` with racy-only checks |
| `solid` > 0 | log which paks; their cold cost is the whole pak, and the cache matters even more for them |

---

## 2. The design

### 2.1 Components

```
src/Core/ModIndex/                       (namespace DivinityModManager.ModIndex; NO System.Windows, ReactiveUI, Splat)
  PakFacts.cs          PakFacts, FolderFacts, EntryFact: raw facts about one pak (POCOs)
  PakScanner.cs        cold path: LSLib Package → PakFacts (exact re-statement of DMDL:483-598 fact gathering)
  PakClassifier.cs     pure: (PakFacts, BuiltinContext) → PakClassification (DMDL:526-551, 561-640 decisions)
  BuiltinContext.cs    immutable snapshot of the builtin folder → mod map and IgnoreBuiltinPath
  PakSentinel.cs       first 40 + last 32 bytes of a pak
  ModIndexCache.cs     binary file read/write, header, checksum, atomic replace
  GameModuleIndex.cs   cache of the base-game module scan, keyed by game-pak stats
  ModIndexService.cs   orchestration: validate → scan misses in parallel → persist; warm-up; optional watcher
  ModIndexOptions.cs   cache path, host version, DOP, VerifySentinel, NamedFolderThreshold

src/Core/Util/DivinityModDataLoader.cs   adapters: facts + classification → DivinityModData (stays WPF-bound)
src/Core/Util/LoadTrace.cs               §1.5
src/Core/Util/ModListSnapshot.cs         §3 harness
```

Rules that make it port-proof: `ModIndex/*` references only the BCL, LSLib and `DivinityApp.Log`. Logging
goes through an `Action<string>` in `ModIndexOptions`, defaulting to `DivinityApp.Log`, so a CLI can pass
`Console.Error.WriteLine`. `DivinityOsirisModStatus` (an enum in Core) is the one shared type. When LSLib
builds cross-platform, the folder can move to its own `net8.0` project without code changes (§6).

### 2.2 Data structures

```csharp
namespace DivinityModManager.ModIndex;

public sealed class EntryFact
{
	public int Index;        // position in pak.Files, the entry order the loop sees
	public string Name;      // full entry name, e.g. "Public/Game/GUI/Widgets/x.xaml"
	public bool NonZero;     // PackagedFileInfo.Size() > 0 (DMDL:533)
}

/// <summary>Entries under Mods/&lt;Folder&gt;/ or Public/&lt;Folder&gt;/ that reach the folder branch (DMDL:498-556).</summary>
public sealed class FolderFacts
{
	public string Name;            // modFolder as computed at DMDL:503
	public int FirstIndex;         // first entry index for this folder
	public int Count;              // entries for this folder
	public bool NamesStored;       // false = Entries trimmed in the cache (see §2.3 NamesStored rule)
	public List<EntryFact> Entries = [];
}

public sealed class PakFacts
{
	// identity and validation
	public string FilePath;
	public long Length;
	public long LastWriteUtcTicks;
	public long CreationUtcTicks;
	public byte[] Sentinel;        // 72 bytes, PakSentinel
	public bool Racy;              // mtime too close to the cache write time to trust

	// facts
	public int EntryCount;
	public string MetaText;        // chosen meta.lsx (DMDL:571-598), null = no meta file
	public int MetaCount;          // metaFiles.Count (DMDL:561)
	public bool HasExtenderConfig;
	public string ExtenderConfigText;  // null if HasExtenderConfig and the read failed
	public DivinityOsirisModStatus OsirisStatus;
	public List<FolderFacts> Folders = [];   // in first-appearance order
}

public sealed class BuiltinContext
{
	public IReadOnlyDictionary<string, DivinityModData> BuiltinMods;   // DMDL:747, keyed by Folder, Ordinal
	public string[] IgnoreBuiltinPaths;                                // snapshot of DMDL:33

	public bool IsIgnoredPath(string entryName) => IgnoreBuiltinPaths.Any(x => entryName.Contains(x));  // DMDL:529
}
```

`BuiltinContext.BuiltinMods` holds `DivinityModData` values because `BuiltinOverrideModsText` uses
`Name` and `Folder` (`DMDL:637`). The classifier reads only those two strings. A CLI can pass a lighter
type later, through a two-property interface.

### 2.3 Cache file

**Location.** Default `DivinityApp.GetAppDirectory("Data", "modindex.bin")`, next to `settings.json`
(`MWVM:1115`) and the Nexus/GitHub caches. The release pipeline deletes `Data/` from the zip (CLAUDE.md
§7), which is what we want. The path is a constructor option, so Linux/CLI hosts can pass
`$XDG_CACHE_HOME/BG3ModManager/modindex.bin` when the exe folder is read-only.

**Format (little-endian, `BinaryWriter`/`BinaryReader`, strings length-prefixed UTF-8).** No new NuGet:
`System.IO.Hashing` 9.0.4 already flows in transitively from LSLib (`LSLib.csproj:53`). If transitive
flow is off, use BCL `SHA256.HashData` instead (~5 ms for 5 MB).

```
Header (64 bytes)
  0   8  Magic           "BG3MMIDX"
  8   2  SchemaVersion   = 1     layout of this file
  10  2  ScannerVersion  = 1     semantics of PakFacts; bump on any scanner/regex/constant change
  12  4  Flags           reserved
  16  8  Fingerprint     XxHash64(host version, Core + LSLib assembly versions, the three regex patterns,
                         EXTENDER_MOD_CONFIG, MAX_FILE_OVERRIDE_DISPLAY, NamedFolderThreshold,
                         VFS.PackageBlacklistBG3 contents, path-comparison mode)
  24  8  WrittenUtcTicks
  32  8  PayloadLength
  40  8  PayloadXxHash64
  48 16  reserved
Payload
  GameSection   (§2.6)
  i32 PakCount
  per pak: FilePath, Length, LastWriteUtcTicks, CreationUtcTicks, Racy, Sentinel[72], EntryCount,
           MetaCount, MetaText?, HasExtenderConfig, ExtenderConfigText?, OsirisStatus,
           i32 FolderCount, per folder: Name, FirstIndex, Count, NamesStored, [i32 n, per entry: Index, Name, NonZero]
```

Size [inferred]: ~3.5 KB per pak (meta text dominates) → ~4 MB for 1,097 paks. Read + verify + parse
≈ 20–60 ms in Debug.

**Why raw `meta.lsx` text and not parsed fields.** The warm path runs the **same** `ParseMetaFile`
(`DMDL:168-325`) on the same string. Equality with the cold path holds by construction, the R1 surface is
smaller, and nothing here changes when someone later fixes a parser bug. One example of such a bug:
dependencies copy the **module's** `PublishHandle` (`DMDL:269, 296`). Cost: ~0.2 ms per mod of parse
work, spread across the pool. A parsed, toolkit-neutral `ModuleMeta` DTO is a later, optional step
(P13, §4).

**NamesStored rule (keeps the file small and classification exact).** Storing every entry name would
cost E × ~60 B (60–120 MB if E ≈ 1–2 M). Instead, at write time, a folder keeps its entry names only if
`Name ∈ L0` (the builtin folder set in effect at scan time) **or** `Count ≤ 64`. A normal mod's own big
folders are trimmed to counts. Override folders (`Public/Shared`, `Public/Game/GUI`, …) are always in L0
and keep their names. At load, `PakClassifier.CanClassify` returns false if a folder that is builtin
*now* was trimmed. The service then rescans that pak. This can only happen when the builtin set grew:
after an F5, Larian modules discovered in the game paks but absent from `IgnoredMods.json` are added to
`IgnoredMods` (`MWVM:1501-1509`, see §3.3 H9).

**Key and invalidation rules.**

| Rule | Detail |
|---|---|
| Key | `FilePath` exactly as enumerated (`DMDL:757`), compared `OrdinalIgnoreCase` on Windows and `Ordinal` elsewhere (`OperatingSystem.IsWindows()`) |
| Hit requires | `Length`, `LastWriteTimeUtc.Ticks` and `CreationTimeUtc.Ticks` all equal (any difference = rescan; never "newer than"); `!Racy`; `Sentinel` bytes equal (when `VerifySentinel`); `CanClassify` true |
| Racy | When writing, an entry with `abs(WrittenUtc − LastWriteUtc) < 3 s` is stored `Racy = true` and rescanned on the next load. Git's "racy index" rule, sized for FAT/exFAT's 2 s mtime granularity |
| Whole-file discard | magic, `SchemaVersion`, `ScannerVersion` or `Fingerprint` mismatch; length or checksum mismatch; any parse exception |
| Prune | entries under the scanned root that were not enumerated this time are dropped on write; entries under other roots are kept up to 4 roots, so switching the AppData override back and forth stays warm |
| Manual | Settings → Advanced → "Clear cache" (`SettingsWindowViewModel.cs:483-500`) also deletes `modindex.bin` |

**Sentinel.** The first 40 bytes cover the `LSPK` signature and the whole v15/v16/v18 header: version,
`FileListOffset`, `FileListSize`, flags, priority, MD5 field, part count (`PackageFormat.cs:212-260`;
`PackageReader.cs:249-261`). The last 32 bytes are the tail of the LZ4-compressed file table, which LSLib
writes at the end for v18. For v13 they hold the trailing header. Any change to the table, or to the
offsets and sizes, almost certainly changes those bytes. Reading them costs one open and two small reads.

### 2.4 Load path (user paks)

`DMDL:745-808` keeps its shape. Enumeration and partial-pak filtering (`:757-764`, including the
never-cleared static `_AllPaksNames`, `:429`) stay exactly as they are. Dedupe (`:787-806`) stays
exactly as it is. Only the per-pak loop (`:773-784`) becomes a call into the service, with results in
enumeration order.

```csharp
public static async Task<ModLoadingResults> LoadModPackageDataAsync(string modsFolderPath, CancellationToken cts)
{
	var builtinMods = DivinityApp.IgnoredMods.Items.SafeToDictionary(x => x.Folder, x => x);
	var context = new BuiltinContext(builtinMods, [.. IgnoreBuiltinPath]);
	var results = new ModLoadingResults() { DirectoryPath = modsFolderPath };

	var modPaks = new List<string>();
	// ... DMDL:755-769 unchanged ...

	var index = Services.Get<IModIndexService>() ?? ModIndexService.Uncached;
	var facts = await index.GetPakFactsAsync(modPaks, context, cts).ConfigureAwait(false);   // same order as modPaks

	var built = new DivinityModData[facts.Count];
	await Parallel.ForAsync(0, facts.Count, new ParallelOptions { MaxDegreeOfParallelism = index.Options.DegreeOfParallelism }, async (i, _) =>
	{
		if (cts.IsCancellationRequested || facts[i] == null) return;   // same partial-result semantics as DMDL:700
		built[i] = await CreateModDataAsync(facts[i], context).ConfigureAwait(false);
	}).ConfigureAwait(false);

	var mods = built.Where(x => x != null).ToList();
	// ... DMDL:788-806 unchanged (dupes, lastestDuplicates, OrderBy(Name)) ...
	return results;
}
```

`ModIndexService.GetPakFactsAsync`:

```csharp
public async Task<IReadOnlyList<PakFacts>> GetPakFactsAsync(IReadOnlyList<string> pakPaths, BuiltinContext context, CancellationToken token)
{
	await EnsureCacheLoadedAsync().ConfigureAwait(false);   // joins a running warm-up (§2.7)
	var result = new PakFacts[pakPaths.Count];
	var misses = new ConcurrentQueue<int>();
	var stats = new IndexStats();

	await Parallel.ForAsync(0, pakPaths.Count, new ParallelOptions { MaxDegreeOfParallelism = Options.DegreeOfParallelism }, (i, _) =>
	{
		var path = pakPaths[i];
		var info = new FileInfo(path);
		if (info.Exists && _entries.TryGetValue(path, out var cached) && IsValid(cached, info, context, stats))
		{
			result[i] = cached;
		}
		else
		{
			misses.Enqueue(i);
		}
		return ValueTask.CompletedTask;
	}).ConfigureAwait(false);

	await Parallel.ForEachAsync(misses, new ParallelOptions { MaxDegreeOfParallelism = Options.DegreeOfParallelism }, async (i, _) =>
	{
		if (token.IsCancellationRequested) return;
		try
		{
			var facts = await PakScanner.ScanAsync(pakPaths[i], token).ConfigureAwait(false);
			result[i] = facts;
			_entries[facts.FilePath] = facts;
			Interlocked.Increment(ref stats.Scanned);
		}
		catch (Exception ex)
		{
			Options.Log($"Error loading mod pak '{pakPaths[i]}':\n{ex}");   // same text as DMDL:709; failures are not cached
		}
	}).ConfigureAwait(false);

	Options.Log($"[ModIndex] {stats}");   // paks=1097 hit=1094 stale=2 new=1 racy=0 sentinel=0 rescanBuiltin=0 scanned=3
	ScheduleSave(pakPaths, context);      // background, §2.8
	return result;
}

private bool IsValid(PakFacts cached, FileInfo info, BuiltinContext context, IndexStats stats)
{
	if (cached.Length != info.Length || cached.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks || cached.CreationUtcTicks != info.CreationTimeUtc.Ticks)
	{
		Interlocked.Increment(ref stats.Stale);
		return false;
	}
	if (cached.Racy)
	{
		Interlocked.Increment(ref stats.Racy);
		return false;
	}
	if (Options.VerifySentinel && !PakSentinel.Matches(info.FullName, cached.Sentinel))
	{
		Interlocked.Increment(ref stats.SentinelMismatch);
		return false;
	}
	if (!PakClassifier.CanClassify(cached, context))
	{
		Interlocked.Increment(ref stats.RescanForBuiltin);
		return false;
	}
	return true;
}
```

`PakScanner.ScanAsync` is `DMDL:483-598`, minus the `files` set and minus every classification decision.
It records the folder each entry reaches, the meta chosen by the **same** selection loop (it depends on the
pak file name), the extender config text (the last entry whose name contains
`DivinityApp.EXTENDER_MOD_CONFIG`, as at `:490-493`) and the Osiris status. Goal reads stop once the
status is `MODFIXER`: from there no later entry can change the result, so this is exact. Both regexes
become `[GeneratedRegex]` with the same pattern and options. The source generator has the same semantics
as the interpreter, unlike a hand-written parser: `^(Mods|Public)/(.+?)/.+$` has edge cases such as
`Mods//x/y` → group 2 `/x`.

```csharp
for (var i = 0; i < pak.Files.Count; i++)
{
	var f = pak.Files[i];
	if (f.Name.Contains(DivinityApp.EXTENDER_MOD_CONFIG))
	{
		extenderConfig = f;
	}
	else if (IsModMetaFile(f))
	{
		metaFiles.Add(f);
	}
	else
	{
		var match = ModFolderPattern().Match(f.Name);
		if (match.Success)
		{
			var modFolder = Path.GetFileName(match.Groups[2].Value.TrimEnd(Path.DirectorySeparatorChar));
			if (facts.OsirisStatus != DivinityOsirisModStatus.MODFIXER && f.Name.Contains($"Mods/{modFolder}/Story/RawFiles/Goals"))
			{
				facts.OsirisStatus = await ReadGoalStatusAsync(f, facts.OsirisStatus).ConfigureAwait(false);   // DMDL:506-523 verbatim
			}
			if (!folders.TryGetValue(modFolder, out var folder))
			{
				folder = new FolderFacts { Name = modFolder, FirstIndex = i, NamesStored = true };
				folders.Add(modFolder, folder);
				facts.Folders.Add(folder);
			}
			folder.Count++;
			folder.Entries.Add(new EntryFact { Index = i, Name = f.Name, NonZero = f.Size() > 0 });
		}
	}
}
```

The cold path classifies from the **untrimmed** in-memory facts. Trimming happens only when the cache
is written.

`PakClassifier.Classify` re-states `DMDL:526-551, 561-640` over the folders:

```csharp
public static PakClassification Classify(PakFacts facts, BuiltinContext context)
{
	var c = new PakClassification();
	var overrides = new List<(int Index, DivinityModData Mod, string FirstName)>();
	foreach (var folder in facts.Folders)
	{
		if (context.BuiltinMods.TryGetValue(folder.Name, out var builtinMod))
		{
			c.HasBuiltinDirectory = true;
			var first = -1;
			string firstName = null;
			foreach (var e in folder.Entries)   // CanClassify guarantees NamesStored here
			{
				if (context.IsIgnoredPath(e.Name)) continue;
				c.IsOverridingBuiltinDirectory = true;
				if (e.NonZero && (folder.Name != "Game" || !e.Name.Contains("GUI") || e.Name.EndsWith(".xaml")))
				{
					c.BaseGameFiles.Add(e.Name);   // HashSet<string>, as DMDL:479
				}
				if (first < 0)
				{
					first = e.Index;
					firstName = e.Name;
				}
			}
			if (first >= 0) overrides.Add((first, builtinMod, firstName));
		}
		else
		{
			c.HasModFolderData = true;   // a folder exists only if it has ≥ 1 entry
		}
	}
	// insertion order of DMDL:545-548 = order of the first non-ignored entry
	c.BuiltinOverrides = [.. overrides.OrderBy(x => x.Index).Select(x => x.Mod)];
	foreach (var o in overrides.OrderBy(x => x.Index))
	{
		DivinityApp.Log($"Found a mod with a builtin directory. Pak({Path.GetFileNameWithoutExtension(facts.FilePath)}) Folder({o.Mod.Folder}) File({o.FirstName})");
	}
	if (facts.MetaCount == 0)
	{
		c.IsOverridingBuiltinDirectory = c.HasBuiltinDirectory;   // DMDL:567: ignores IgnoreBuiltinPath, deliberately preserved
	}
	return c;
}
```

`CreateModDataAsync(facts, c)` is `DMDL:591-673` verbatim, reading the cached text instead of a stream:
`ParseMetaFile(facts.MetaText)`; `IsForceLoadedMergedMod = HasModFolderData`; the override-only branch
with `UUID = pakPath` and `Folder = c.BuiltinOverrides.FirstOrDefault()?.Folder`. The last one is null
when the list is empty, as `FirstOrDefault().Key` is today. `BuiltinOverrideModsText` uses the same
`< MAX_FILE_OVERRIDE_DISPLAY` test and the same `OrderBy(x => x)`. `LastModified` comes from the `FileInfo`
already taken for validation: the same value `File.GetLastWriteTime` returns, minus two syscalls per pak.
Extender config is parsed by a `ParseScriptExtenderConfig(string text)` extracted from `DMDL:1690-1725`
and shared with the file-path variant `:1654-1688`.

The three import callers (`MWVM:1847, 3283, 3444`) keep calling `LoadModDataFromPakAsync`. That method
becomes scanner + classifier + adapter **without** the cache, so its output is identical to today's.
Optionally it can also update the in-memory index.

### 2.5 Concurrency and cancellation

- **Off the UI thread.** `LoadModsAsync` wraps each loader in `Task.Run` (§2.9 P5). Inside `Task.Run`
  there is no `SynchronizationContext`, and all new Core code uses `ConfigureAwait(false)`. The awaits in
  `LoadModsAsync` itself still resume on the UI thread, so VM-property threading is unchanged.
- **Built-in, editor and user loads run concurrently.** User-pak classification reads
  `DivinityApp.IgnoredMods` (`DMDL:747`), which the built-in loader does not modify (`DMDL:1783-1825`
  touches no shared state). Editor projects only read `IgnoredMods` (`:54`). Today's order of the *merge*
  (`MWVM:1670-1714`) is kept exactly; only the *scans* overlap.
- **Bounded parallelism.** `DegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 8)`,
  which leaves a core for the UI and renderer. Results go into arrays indexed by enumeration order, never a
  `ConcurrentBag`, so output order is deterministic (§3.3 H1).
- **Thread-safety prerequisites (must land before parallel construction):**
  (a) `DivinityApp.IsScreenReaderActive()` → Tolk is a native COM-ish library, not known to be
  thread-safe. Memoise it (P4b) so pool threads never call into Tolk.
  (b) `DivinityApp.Log` → `Trace.WriteLine` is serialised by `Trace.UseGlobalLock` (default true): OK.
  (c) `IgnoreBuiltinPath` is a static `HashSet` written only by `LoadAppConfig` on the UI thread before
  the scan starts (`MWVM:4733-4741`, run from `LoadSettings` `:1138` and `RefreshAsync` `:2366-2372`);
  the context snapshot copies it anyway.
  (d) `DivinityModData` ctor off-thread: its `ObserveOn(MainThread)`/`ToUIProperty` pipelines already
  marshal (`src/Core/Extensions/ReactiveExtensions.cs:17-36`). `SourceCache` reads are locked.
- **Cancellation** keeps today's semantics. The token is checked per item, and partial results are
  returned rather than thrown. Today `LoadModDataFromPakToBagAsync` simply returns on cancellation
  (`DMDL:700`), and `RunTask` would turn a throw into "no mods at all" (`MWVM:1566-1585`). Completed scans
  are still written to the cache (each entry stands alone).
- **One load at a time per service.** A `SemaphoreSlim(1)` guards `GetPakFactsAsync` and `SaveAsync`.
  `RefreshCommand` already cannot overlap a refresh (`MWVM:4985-4990`, `canRefreshObservable`).

### 2.6 Game built-in modules (`GameModuleIndex`)

Today: VFS over ~25 attached game paks, `FinishBuild`, `DiscoverMods`, then ~30 meta parses
(`DMDL:1783-1825`).

**Cache key (exact by construction):** every `*.pak` under `Data/` (flat) and `Data/Localization/` (depth
3), **including** multi-part `_N.pak` files and blacklisted ones, as `(relative path, Length,
LastWriteUtcTicks)` in enumeration order. Plus "`Data/Mods` has no sub-directory containing `meta.lsx`".
If loose folders exist (editor projects), **bypass the cache** and run the VFS path unchanged. That keeps
`VFS.EnumerateDirectories`' filesystem merge (`VFS.cs:356-378`) and the editor-flag logic
(`DMDL:1758-1762`) out of the cache. It costs nothing for this owner (~0 loose folders).

**Stored:** for each `ModInfo` in `modResources.Mods.Values` order: `Name`, `PackagePath`, `ModsPath`,
`Meta`, and the meta text read through `vfs.TryOpen`. **Warm path:** `LoadModFromModInfo` (`DMDL:1727-1781`)
is refactored to take the text instead of opening the VFS. It still calls `File.GetLastWriteTime` (about 30
calls), so `LastModified` is byte-identical.

**Game patch:** any pak stat changes → miss → one VFS run (~1–2.5 s) → re-cache. User paks are **not**
rescanned: their classification is recomputed from facts. The only exceptions are paks whose trimmed
folders are now builtin.

**P12 (optional, gated by G1):** a lean cold scan that reads each game pak's file table, keeps only
`Mods/<X>/meta.lsx` entries, and reproduces VFS precedence: highest `Metadata.Priority` wins, and on equal
priority the first attached wins (`VFS.cs:42`, `<` is strict). Attach order is `Directory.GetFiles`
order, Data then Localization (`VFS.cs:154-181`). It avoids the tree and the transient spike. It is
risky (it re-states LSLib semantics), so it ships only if `Builtin.FinishBuild` proves significant, and
only with a VFS-vs-lean comparison in the snapshot. Upstream's Avalonia branch did something similar: its
`DirectoryPakParser` reads game paks directly with no VFS tree
(`wt-avalonia/src/Core/Util/Pak/DirectoryPakParser.cs:247-272`). That is an idea only, and it still reads
packages serially.

### 2.7 Background warm-up

`App` ctor (`src/GUI/App.xaml.cs:25-28`), next to the other registrations:

```csharp
var modIndex = new ModIndexService(new ModIndexOptions
{
	CachePath = DivinityApp.GetAppDirectory("Data", "modindex.bin"),
	HostVersion = typeof(App).Assembly.GetName().Version.ToString(),   // same source as MWVM:4910
});
Services.RegisterSingleton<IModIndexService>(modIndex);
modIndex.BeginWarmUp();
```

`BeginWarmUp` runs `Task.Run` to read and verify the cache file, then re-validate (stat + sentinel) every
cached entry in parallel. It produces **facts only**: no classification and no `DivinityModData`, because
`IgnoredMods` and the Rx scheduler are not ready yet. This work overlaps the ~2.5 s between first window
and `RefreshAsync` (plan §4 0.3: 1.09 s → 3.61 s). `GetPakFactsAsync` awaits the warm-up task and re-uses
validations no older than 30 s. Expected gain ~0.1–0.3 s [inferred]. That is small, so this is P10,
after the cache.

### 2.8 Persistence, failures, two instances

| Situation | Handling |
|---|---|
| Write | After a load, `ScheduleSave` runs on the pool: serialise to `modindex.bin.<pid>.<random>.tmp`, `Flush(true)`, then `File.Move(tmp, final, overwrite: true)`. Readers open with `FileShare.ReadWrite \| FileShare.Delete`, so the replace never fails because another instance is reading. On `IOException`, retry 3× at 100 ms, then log and keep the old file. |
| Partial write / power loss | The final file is the old one or the new one, never torn. A torn file (FS bug) fails the checksum → discarded. Stale `*.tmp` older than 10 min are deleted at the next start. |
| Corrupt or foreign file | Any header, length, checksum or parse failure → log `[ModIndex] Cache ignored: <reason>`, rename it to `modindex.bin.bad` (one kept for diagnosis), cold-scan, rewrite. |
| Older/newer schema, scanner or fingerprint | Discarded whole: one cold start (~2.5–4.5 s). No migration code to maintain. |
| Two app instances | Both read a consistent snapshot. Both may scan and both write complete, self-consistent files; the last writer wins, and both files are correct for the disk state they saw. No mutex. (`settings.json` has the same last-writer-wins issue, CLAUDE.md §3. That is out of scope here.) |
| Cache directory not writable | Log once and run uncached. No alert. |
| Pak unreadable or not a package | Logged as today (`DMDL:709`); **not** cached, so it is retried next time, as today. |
| Alerts | Cache failures are self-healing and invisible to the user, so they are logged, not `ShowAlert`ed. R8 concerns errors that change what the user sees, and these do not. **Owner decision:** if you want an Info alert "Rebuilt mod cache", it is one line. |

### 2.9 Hook points

| Where | Change | Commit |
|---|---|---|
| `DMD:234`, `DMDL:478, 488, 628` | delete `Files` and its population | P3 |
| `DMDL:458, 448` | `[GeneratedRegex]` for both patterns | P4 |
| `DMDL:504-523` | skip goal reads once `MODFIXER` | P4 |
| `src/Core/DivinityApp.cs:124-127` | memoise `IsScreenReaderActive()` | P4b |
| `MWVM:1627, 1637, 1665` | `RunTask(Task.Run(() => DivinityModDataLoader.X(...)), null)`, started together and awaited in today's order | P5 |
| `DMDL:773-787` | bounded parallel scan into an index-ordered array instead of `ConcurrentBag` | P5 |
| `DMDL:466-687` | split into `PakScanner`, `PakClassifier`, `CreateModDataAsync`; `LoadModDataFromPakAsync` composes them | P7 |
| `DMDL:1654-1725` | extract `ParseScriptExtenderConfig(string)` | P7 |
| `DMDL:745-808` | `GetPakFactsAsync` (§2.4) | P8 |
| `src/GUI/App.xaml.cs:27` | register `IModIndexService` | P8 (warm-up call in P10) |
| `SettingsWindowViewModel.cs:483-500` | Clear cache also calls `IModIndexService.Invalidate()` | P8 |
| `DMDL:1727-1825` | game module cache (§2.6) | P9 |
| `MWVM:1716` and after `:2453` | snapshot hooks (§3) | P1 |
| `MWVM:2340-2556`, `:1609-1718`, `DMDL` phases | `LoadTrace` lines (§1.5) | P2 |

Memoising the screen-reader probe (P4b), house style:

```csharp
private static long _screenReaderCheckedAt = long.MinValue;
private static bool _screenReaderActive;

public static bool IsScreenReaderActive()
{
	var now = Environment.TickCount64;
	if (now - Interlocked.Read(ref _screenReaderCheckedAt) > 5000)
	{
		// Probing Tolk per mod re-initialises it on every call when no screen reader is running (ScreenReaderWindows.cs:23-33).
		_screenReaderActive = Services.Get<IScreenReaderService>()?.IsScreenReaderActive() == true;
		Interlocked.Exchange(ref _screenReaderCheckedAt, now);
	}
	return _screenReaderActive;
}
```

The value is shared, so a probe from a pool thread is still possible every 5 s. To keep Tolk strictly on
the UI thread, `MainWindowViewModel` can refresh the value on a 5 s `DispatcherTimer` and pool threads
only read it. **Accessibility check for the owner:** a screen reader started after launch is picked up
within 5 s; `HasToolTip` of mods built before then keeps its value until the next refresh. That is the
same as today for a reader started after load.

### 2.10 Memory impact (vs 478 MB private)

| Change | Effect [inferred] | Measured by |
|---|---|---|
| P3 drop `Files` | −E × ~150–200 B retained. E = 0.3 M → −50 MB; E = 1.5 M → −250 MB | `Memory.AfterRefresh` private bytes and post-compaction heap, before/after |
| P5 parallel scan | +transient: DOP × the largest in-flight table (~272 B × entries); peak only | peak working set (Task Manager column) |
| P8 warm path | no LSLib `Package`/`PackagedFileInfo`/table buffers → ~E × 0.8 KB less allocation → fewer Gen2s and less LOH fragmentation; −30–80 MB committed, speculative | `alloc=` and `gc=` deltas in `LoadTrace`; private bytes |
| P9 warm game path | the VFS tree and its transient spike never happen | same |
| In-memory index | +~5 MB (meta text, trimmed folders) | `dotnet-gcdump` |
| Not addressed here | ~40 Rx pipelines × 1,121 `DivinityModData`, and the WPF visual tree | Stage 6 (S6-A) |

---

## 3. The R1 correctness guard

### 3.1 Snapshot harness (P1, the first commit, so its parent is clean `master`)

`src/Core/Util/ModListSnapshot.cs` writes a TSV under `_Logs/`. Trigger: an extension-less file named
`snapshot` next to the exe, the same convention as `debug` (`src/GUI/Views/MainWindow.xaml.cs:380`). It
writes `modsnapshot_<yyyyMMdd_HHmmss>_r<refresh#>.tsv`, one per refresh, so the first load and an F5
can be compared like for like (they legitimately differ today: §3.3 H9).

Hook A, end of `LoadModsAsync` (`MWVM:1716`, before the `return`): `finalMods`, `baseMods`,
`modLoadingResults.Duplicates`, `projects`. Hook B, after `BuildModOrderList` (`MWVM:2453`): the active
order and how each entry resolved.

Sections and columns (tab-separated, `CultureInfo.InvariantCulture`, `\t \r \n` escaped as `\\t \\r \\n`,
null as `<null>`):

```
# ModListSnapshot v1  app=<ver> refresh=<n> mods=<n> dupes=<n> builtin=<n> projects=<n> ignoredMods=<n> ignoreBuiltinPath=<joined>
## mods        sorted by UUID (Ordinal), then FilePath (OrdinalIgnoreCase)
UUID Folder Name VersionInt FilePath IsUserMod IsEditorMod IsLarianMod IsHidden HasMetadata
IsForceLoaded IsForceLoadedMergedMod CanAddToLoadOrder ModType OsirisModStatus HasScriptExtenderSettings
SE.RequiredVersion SE.FeatureFlags MD5 PublishHandle FileSize HeaderVersion PublishVersion Author Tags
Dependencies(UUID@VersionInt;… sorted) Conflicts(…) LastModifiedUtcTicks BuiltinOverrideModsText DescriptionXxHash
## duplicates  (losers, offered for deletion by DeleteMods at MWVM:1705-1710)  UUID VersionInt FilePath
## winners     UUIDs that had >1 candidate: UUID WinnerPath WinnerVersion Candidates(path@version;…)
## ties        UUIDs whose top VersionInt is shared by >1 candidate (winner is order-dependent today, §3.3 H1)
## builtin     baseMods in list order: UUID Folder Name VersionInt FilePath IsEditorMod
## order       Index UUID Name Found ResolvedFilePath
```

`CanAddToLoadOrder` is a synchronous `BindTo` (`DMD:530-531`), so it is safe to read at hook time.
OAPH-backed values (`HasInvalidUUID`, …) are excluded because they may lag behind a dispatcher post. Write
the derived value directly instead (`Guid.TryParse(UUID)`).

### 3.2 How to run it

1. Build commit **P1** alone (it is `master` + the harness) = **BEFORE**. Drop the `snapshot` file, launch,
   wait for the list, press F5 once, close. You get `…_r1.tsv` (first load) and `…_r2.tsv` (F5).
2. Build the commit under test = **AFTER**. Delete `Data/modindex.bin`; launch, F5, close: these are the
   **cold** runs. Launch, F5, close again: these are the **warm** runs.
3. Compare r1 with r1 and r2 with r2, BEFORE vs cold and BEFORE vs warm:
   `git diff --no-index --word-diff=color before_r1.tsv cold_r1.tsv`, or in PowerShell
   `Compare-Object (gc before_r1.tsv) (gc warm_r1.tsv)`.
4. **Pass = empty diff.** Exception: rows in `## ties` may change winner once, at P5 (see H1). If `## ties`
   is empty for your set, the exception does not apply.
5. Same machine and inputs throughout: same `IgnoredMods.json` (it is read from `<exe>/Resources`,
   CLAUDE.md §6), the same game install and the same Mods folder. Turn update checks off. Do it once per
   commit P3–P10 that touches the loader.

### 3.3 Every place this series could change a winner or a classification

| # | Hazard | Where | Guard |
|---|---|---|---|
| H1 | **Result order.** Ties among same-UUID, same-`VersionInt` duplicates go to the first in list order (`>` is strict, `DMDL:799`). The list comes from `ConcurrentBag.ToList()` (`:787`), and its order depends on which thread added each item, so it is **undefined today** (§1.2: mostly the UI thread, interleaved by dispatcher posts). P5 makes it enumeration order. The losers are offered for deletion (`MWVM:1705-1710`). | `DMDL:787-806` | `## ties` section; note the one-time change in the PR text. The `OrderBy(Name)` ties (`:806`, `MWVM:1717`) affect display order only. |
| H2 | **Builtin set snapshot.** Classification must use `IgnoredMods` as it is when `LoadModPackageDataAsync` starts (`DMDL:747`), not at warm-up time. | `DMDL:747` | Warm-up produces facts only; the classifier runs inside the load. |
| H3 | **`IgnoreBuiltinPath`** (`DMDL:33, 529`; filled at `MWVM:4739`; currently `["Game/GUI/Assets"]`). | classifier | Snapshot copied per load; the names for builtin folders are stored. |
| H4 | **`metaCount == 0` rule**: `isOverriding = hasBuiltinDirectory`, which ignores `IgnoreBuiltinPath`. | `DMDL:564-568` | Re-stated literally in the classifier (comment in code). |
| H5 | **Meta selection** depends on the pak file name (`pakName.Contains(parentDir.Name)`). | `DMDL:571-589` | The selection result is a fact keyed by full path; a rename is a new key. |
| H6 | **Stale cache entry** (validators missed a change) → old UUID or version → wrong winner. | §2.3 | Size + mtime + ctime + sentinel + racy + fingerprint; manual Clear cache; T5/T6 in §5. |
| H7 | **Trimmed names** + a folder that is newly builtin. | §2.3 | `CanClassify` → rescan; counted as `rescanBuiltin=` in the log. |
| H8 | **Override-only `Folder`** = first inserted builtin key, null if none. | `DMDL:617` | First non-ignored entry index ordering; null preserved. |
| H9 | **First load vs F5 differ today.** `SetLoadedMods` adds VFS-discovered Larian mods to `IgnoredMods` (`MWVM:1501-1509`), so the second refresh classifies against a larger set. | `MWVM:1501-1509` | Classification is recomputed per load, so behaviour matches `master` on both. The harness compares r1/r1 and r2/r2. |
| H10 | **Goal early exit.** | `DMDL:504-523` | Exact: once `MODFIXER`, no later entry can change it. |
| H11 | **GeneratedRegex vs interpreted.** | `DMDL:448, 458` | Same pattern and options; the generator guarantees semantics. No hand-written parser. |
| H12 | **Concurrent builtin + user scans.** | `MWVM:1627-1666` | The built-in loader mutates no shared state; the merge order (`:1670-1714`) is unchanged. |
| H13 | **Game cache order and `LastModified`.** `baseMods` order feeds `MergeModLists` ties (`MWVM:1542`, first wins unless newer or editor) and the duplicate-toolkit dialog (`:1675-1693`). | §2.6 | Stored in `Mods.Values` order; `File.GetLastWriteTime` still called; cache bypassed when loose `Data/Mods` folders exist. |
| H14 | **`LastModified` source** for user paks (FileInfo vs `File.GetLastWriteTime`). | `DMDL:642-653` | Same underlying value; only display and filters use it (`DivinityModFilterData.cs:98`); it is a snapshot column. |
| H15 | **Cancellation** semantics (partial vs none). | `DMDL:700` | Per-item check, partial results, no throw. |
| H16 | **`ParseMetaFile` reuse.** | `DMDL:168-325` | Same function and same input string; no change until optional P13 (gated by the harness). |
| H17 | **Lean game scan** (P12, optional) re-states VFS priority. | `VFS.cs:42, 154-181` | Gated; ship only with an empty `## builtin` diff, VFS vs lean. |
| H18 | **Partial-pak filter** and the never-cleared static `_AllPaksNames`. | `DMDL:429-446, 763-764` | Untouched. Clearing it per refresh would be a separate `fix/*`, because it changes F5 behaviour. |

---

## 4. Branch and PR series: `perf/startup-load` (cut from `master` at `fc35358`)

Every commit builds on its own. `T*` commits are `TEMP:` and are dropped (interactive rebase is not
needed: cherry-pick the non-TEMP commits onto a fresh branch from `master`). Gains are [inferred] until T1
reports.

| # | Commit | Change | Expected gain | Risk | Measure |
|---|---|---|---|---|---|
| P1 | `Add a mod-list snapshot for load-order regression checks` | §3.1, file-triggered, writes only to `_Logs` | none | none (inert without the file) | builds; the `snapshot` file produces the TSVs |
| P2 | `Log startup load phase timings` | `LoadTrace` at the §1.5 points | none (~1 ms) | none | the log shows the phases |
| T1 | `TEMP: per-pak counters, screen-reader probe and GC compaction check` | §1.5 | — | — | **Gate G1.** Record the numbers in the plan's 4.1 table. |
| P3 | `Stop keeping every pak's file list in memory` | delete `Files` (`DMD:234`, `DMDL:478,488,628`) | −E×~175 B memory; ~3–8 % CPU (fewer hash inserts and Gen2s) | very low (write-only property) | `Memory.AfterRefresh`; snapshot diff empty |
| P4 | `Skip redundant Osiris goal reads and compile the pak regexes` | §2.4 notes | small (G1 `goals`, part of `loop`) | very low | snapshot diff; `goalsRead=` drops |
| P4b | `Check for a screen reader once, not per mod` | §2.9 sketch | 0–4 s (S3; G1 `sr=` decides) | low; accessibility behaviour as noted | `sr=` → ~0; ask a screen-reader user to check tooltips |
| P5 | `Load mods off the UI thread and scan paks in parallel` | `Task.Run` at `MWVM:1627,1637,1665`, started together; `Parallel.ForEachAsync` bounded; index-ordered results; `ConfigureAwait(false)` in `DMDL` | cold 9.27 → ~2.5–4.5 s; **UI stays responsive** | medium: threading (needs P4b first) and H1 ties | `LoadTrace tid/pool`; snapshot diff (ties only); progress bar animates |
| P7 | `Separate pak facts from override classification` | §2.4 split; no cache yet | ±0 | medium: re-statement | **snapshot diff must be empty**, r1 and r2 |
| P8 | `Cache pak facts between launches` | `ModIndex/*`, `GetPakFactsAsync`, Clear cache hook | warm user paks ~6 s → ~0.2–0.5 s | medium: H6/H7 | `[ModIndex] hit=…`; cold and warm snapshot diffs; §5 matrix |
| P9 | `Cache the base-game module scan by game pak stats` | §2.6 | warm −0.8–2.5 s (G1 `Builtin.*`) | low–medium: H13 | `## builtin` diff; touch-a-pak test |
| P10 | `Read the mod cache while the window starts` | §2.7 | −0.1–0.3 s | low | `LoadTrace` T+ offsets |
| P11 | *(optional)* `Rescan changed paks in the background` | `FileSystemWatcher` on the Mods root (`IncludeSubdirectories`, `*.pak`, FileName/LastWrite/Size/CreationTime); 2 s debounce; dirty set rescanned when the size is stable across two checks; `Error` (buffer overflow) → full revalidate. Raises `PaksChanged` | small for perf (F5 already validates in ~0.1–0.3 s); the main value is the hook for a fork-only "mods changed, refresh?" toast | low | F5 after dropping in 5 paks: `scanned=0` |
| P12 | *(optional, G1-gated)* `Find base-game modules without building the VFS tree` | §2.6 lean | cold built-in −0.5–2 s; lower peak | **high** (H17) | `## builtin` diff VFS vs lean, on 2 game versions |
| P13 | *(optional)* `Parse meta.lsx into a toolkit-neutral record` | `ModuleMeta` DTO; `ParseMetaFile` = parse + map | ~0; enables a CLI or Avalonia reuse | medium (field mapping) | snapshot diff empty |

Dependencies: P4b before P5 (thread-safety). P7 before P8. P1/P2 before everything. P3, P4 and P4b are
independent and could each be a tiny PR on its own. **Upstreamability:** everything is in Core and
`MWVM`'s loader, there is no fork-only dependency, and nothing new from NuGet. The only new types follow
the unprefixed rule. Offer upstream as **two PRs**: (1) P2–P5 "faster cold load" (small and
uncontroversial); (2) P7–P10 "metadata cache". Keep P1 in the second PR's description as the verification
method, or as a `chore/*` PR of its own. The owner pushes; nothing goes upstream without the owner's call.

Merge into `fork/main` with `--no-ff` once the §5 matrix passes. Update both plan documents on each status
change.

Side findings, not in this series (separate `fix/*` candidates):

- `ModUpdateHandler.DeleteCache` short-circuits with `||`, so only the first cache is deleted
  (`src/Core/ModUpdater/ModUpdateHandler.cs:145`).
- `FileWatcherWrapper.PauseWatcher` sets `EnableRaisingEvents = false` on resume
  (`src/Core/AppServices/FileWatcherService.cs:72-75`).
- `IgnoreBuiltinPath` replaces `Path.PathSeparator` (`;`), not the directory separator (`MWVM:4739`).
- `_AllPaksNames` is never cleared (H18).

---

## 5. Test matrix

Run each case on the AFTER build. Unless stated, compare against BEFORE (P1) with the snapshot harness
and read the `[ModIndex]` line.

| # | Case | Setup | Expected |
|---|---|---|---|
| T1 | Cold start | delete `Data/modindex.bin` | `hit=0 scanned=1097`; diff empty; `LoadMods.*` ≈ the P5 numbers |
| T2 | Warm start | relaunch unchanged | `hit=1097 scanned=0`; diff empty; `LoadMods.UserPaks` ≤ 0.5 s |
| T3 | Warm F5 | F5 in a warm session | r2 diff vs BEFORE r2 empty (H9); `rescanBuiltin` normally 0 |
| T4 | Game patch | set a small game pak's mtime to now, e.g. `(gi Data\Localization\<lang>.pak).LastWriteTime = Get-Date`, then restore the original value. Owner's call, since it touches the install. | `[ModIndex] game: miss`, VFS ran once; user paks `hit=1097`; `## builtin` diff empty |
| T5 | Mod updated in place, size changed | replace pak X with its newer release | `stale=1 scanned=1`; diff shows only X's new version, the same as BEFORE given the same files |
| T6 | Same size, mtime changed | `(gi X.pak).LastWriteTime = Get-Date` | `stale=1`, rescanned, no change |
| T7 | Same size and mtime, file table changed | flip one byte in the last 32 bytes, restore the mtime | `sentinel=1`, rescanned; the pak now fails to load **with the same log line as BEFORE** |
| T8 | Same size, mtime and sentinel; content changed mid-pak | flip a byte inside a stored (uncompressed) file, restore the mtime | **Not detected** (documented residual). Settings → Clear cache → F5 picks it up. Real-world likelihood: needs a same-size rebuild *and* a restored mtime *and* an identical table tail. |
| T9 | mtime granularity / racy | Mods folder on exFAT, or copy a pak in and F5 within 3 s | entry written `Racy`; next launch `racy=1 scanned=1`; the launch after that `racy=0` |
| T10 | Renamed pak | rename X.pak → Y.pak | `new=1`, the old entry pruned; meta selection re-run (H5); the diff equals BEFORE's for the same rename |
| T11 | Moved into a subfolder | `Mods\sub\X.pak` | `new=1` (recursive enumeration finds it) |
| T12 | Deleted | delete X | the mod disappears in both; pruned on write |
| T13 | Duplicate UUIDs | two paks, same UUID, versions v1 < v2 | v2 wins; v1 in `## duplicates`, same as BEFORE. Same-version pair → appears in `## ties`; the winner is the first in enumeration order (a one-time note at P5) |
| T14 | Editor projects | create `Data\Mods\Test\meta.lsx` | `[ModIndex] game: bypass (loose Data/Mods)`; the project is listed as an editor mod in both; diff empty |
| T15 | Override pak without meta | an override-only pak (e.g. a `Public/Shared` file replacer) | `UUID = full path`, `ModType="File Override"`, `IsForceLoaded`; identical cold, warm and BEFORE; moving it changes the UUID in both |
| T16 | Override with ignored path only | a pak containing only `Public/Game/GUI/Assets/...` (`IgnoreBuiltinPath`), with and without `meta.lsx` | without meta → force-loaded (H4); with meta → not overriding; identical to BEFORE |
| T17 | Corrupted cache | truncate `modindex.bin` to half; then separately flip a payload byte | `Cache ignored: length` / `checksum`; `.bad` kept; cold scan; diff empty |
| T18 | Older schema | patch header bytes 8–9 to `0`, or build with `SchemaVersion = 2` | `Cache ignored: schema`; rebuilt |
| T19 | App update | bump the version (4-file rule, CLAUDE.md §2.5) | fingerprint mismatch → one cold start |
| T20 | IgnoredMods.json edited | add a Larian-like folder that some mod overrides heavily | `rescanBuiltin=k` for the affected paks only; diff equals BEFORE with the same JSON |
| T21 | Two instances | start two copies within 1 s | both load; no `*.tmp` left after closing; the third launch is warm |
| T22 | Locked or broken pak | open X exclusively in another tool, or a zero-byte `.pak` | same error log as BEFORE; retried next launch (not cached) |
| T23 | Solid pak (if G1 finds any) | — | cold scan cost visible in `read=`; warm `hit` |
| T24 | Publish build | repeat T1/T2 in Publish (R5) | same diffs; `_Lib` resolution unaffected (no new assemblies) |

---

## 6. What survives a future Avalonia / Linux port

| Piece | Survives? | Notes |
|---|---|---|
| `ModIndex/*` (facts, classifier, cache format, sentinel, service, watcher) | **Yes, unchanged** | BCL + LSLib only. `FileSystemWatcher` uses inotify on Linux, and recursive watches are per directory (fine at default limits). Path comparer switches on `OperatingSystem.IsWindows()`. The cache path is injectable for read-only installs and `XDG_CACHE_HOME`. |
| The cache file itself | **Yes** | Endianness-fixed, no OS-specific data; ticks are UTC. A Windows cache copied to Linux is simply re-validated (paths differ → misses). |
| Warm path on Linux | **Yes, and LSLib-free** | A cache hit never opens a `Package`. Today `master`'s LSLib references the C++/CLI `LSLibNative` (`LSLib.csproj:44`), used for solid-pak LZ4 frames (`PackageReader.cs:213`). It is the Linux blocker per `docs/review/areas/AVA.md` §"Linux buildability". |
| `PakScanner` (cold path) | **Ports with LSLib** | Against upstream Avalonia's LSLibNet (managed) it needs only its `Package`/`PackagedFileInfo` equivalents, roughly 30 lines. |
| `PakClassifier` | **Yes** | Pure function. Its only coupling is `BuiltinContext`'s value type (`Name`, `Folder`). |
| Snapshot harness | **Yes, and it becomes the port oracle** | Run it on WPF and on a port against the same Mods folder: an empty diff proves the port loads identically. The best long-term asset in this design. |
| `LoadTrace` | Yes | BCL only. |
| `DivinityModData` adapter (`CreateModDataAsync`) | **No** | WPF `Visibility`-bound model; an Avalonia build writes its own ~40-line adapter. P13's `ModuleMeta` DTO shrinks it further. |
| `MWVM` hook edits (`Task.Run`, concurrency) | Pattern only | The view model is rewritten in any port. |
| P4b screen-reader memo | Yes | CrossSpeak's Linux backend returns `null` from `DetectScreenReader` (`ScreenReaderLinux.cs:64`), so the probe is cheap there anyway. |

Later step, once LSLib builds without `LSLibNative`: move `src/Core/ModIndex` into
`src/ModIndex/ModIndex.csproj` (`net8.0`, references LSLib only). The compiler then enforces the
toolkit-neutral boundary, and a CLI (`Program.cs:38` accepts `args` and ignores them today) can reference
it directly.

---

## 7. Open questions for the owner (answered by T1 or by you)

1. Are the 1,097 paks on an SSD or an HDD? The sentinel costs two random reads per pak on a cold OS cache;
   on an HDD that would be several seconds. The `VerifySentinel` option exists for that.
2. G1 numbers: `entries=`, `sr=`, `Builtin.*`, `solid=`.
3. Is touching a game pak's mtime for T4 acceptable? Alternative: run T4 against a copied `Data` folder
   holding only the non-blacklisted paks.
4. Alert or silence on cache rebuild (§2.8)?
5. Upstream as two PRs (cold-path, then cache), or one?
