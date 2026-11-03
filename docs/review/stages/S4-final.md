# Stage 4 (startup load): judged designs and final plan

Judge: S4 synthesis agent, 2026-11-03. Inputs: `docs/review/stages/alternatives/S4-A-minimal.md`, `S4-B-pipeline.md`,
`S4-C-architecture.md` (all three present), `docs/review/areas/AUDIT-PLAN.md` (rows 26-29, findings 06-09), the
plan's Stage 4 and Stage 0.3 sections, CLAUDE.md. **`docs/review/areas/LOAD.md` did not exist** when this was written
(the folder was checked at the start and again at the end). Nothing was built or run. **[c]** = read in source at
the cited `path:line`. **[i]** = inferred, with the Windows measurement that settles it.
Abbreviations: DMDL = `src/Core/Util/DivinityModDataLoader.cs`, MWVM = `src/GUI/ViewModels/MainWindowViewModel.cs`,
DMD = `src/Core/Models/DivinityModData.cs`, LS = `External/lslib/LSLib/LS/`.

## Scores (1-5, higher is better on every column: "R1" 5 = safest, "Effort" 5 = least work)

| Design | Saving vs 9.27 s (cold / warm, inferred) | R1 | Upstream | Effort | Testability | Port-proof | Total |
|---|---|---|---|---|---|---|---|
| **A minimal** | 4 (cold ~3-5 s, warm ~0.4-0.9 s with game cache) | 4 | **5** | **5** | 4 | 4 | **26** |
| B pipeline | 5 (cold ~1.5-3 s, warm ~0.4-1.0 s) | 3 | 3 | 2 | **5** | 4 | 22 |
| C architecture | 5 (cold ~2.5-4.5 s, warm ~0.5-1.2 s, plus an unmeasured screen-reader lever) | 4 | 3 | 3 | **5** | **5** | 25 |

- **A** has the best correctness model. Its cached facts do not depend on the base-game folder set, so a game
  patch, an `IgnoredMods.json` edit or an F5 never rescans a user pak and can never freeze a stale override
  decision. It also has the smallest, most reviewable diff, and it uses JSON as the rest of the house does. Its
  weaknesses: a wrong threading model (it says the tail already runs on the pool), sentinel-only staleness
  defence, and no per-pak equivalence oracle.
- **B** has the best measurement design: a per-pak CSV, a rule against per-pak logging, experiments E1-E7 and a
  `verifyscan` dual-run oracle. It also has the racy-clean rule and an O(n) dedupe written out. It loses points
  on R1 and effort for the hand-written regex parser, the custom LSPK table reader and the per-entry
  builtin-context revalidation. Its C6 concurrency also does not overlap anything as written (§1, item 2).
- **C** has the correct threading analysis, the one new suspect (the per-mod screen-reader probe), per-refresh
  snapshots (r1 against r1, r2 against r2), a `## builtin` section and the best port story. It loses points for
  service and warm-up scaffolding that upstream may not want, and for name-trimming with a `CanClassify`
  rescan where A's capped facts make that unnecessary.

**Spine: A.** Grafted on top: C's threading fix, screen-reader memo, per-refresh and builtin snapshots and
concurrent loads, and B's profiler, `verifyscan` oracle, racy rule, error-outcome caching and O(n) dedupe.

---

## 1. Spot-checks: claims accepted, corrected or rejected

| # | Claim (source) | Verdict | Evidence |
|---|---|---|---|
| 1 | A §1.2/§2.5: after the first yielding await, the per-pak "tail" (meta parse, `DivinityModData` construction) runs **in parallel on the pool** | **Rejected [i, high]**. C §1.2 is right. RefreshAsync starts on the pool (MWVM:4990, 4145). It first awaits an `Observable.Start(..., MainThreadScheduler)` (MWVM:2232-2238, 2374), which resumes inline on the Dispatcher. From then on `SynchronizationContext.Current` is the Dispatcher's. DMDL has **zero** `ConfigureAwait` calls (grep), so every await in LoadModsAsync and in every per-pak task posts its continuation back to the UI thread. The whole of steps a-c is serial on the UI thread; only the BCL's internal `BeginRead` for LZ4 streams touches the pool. | grep; Rx `AsyncSubject` awaiter semantics (library behaviour, not visible here). Confirm with the `tid`/`pool` fields of P1. |
| 2 | B C6: start builtin, editor and pak loads "together" | **Gap.** Without `Task.Run`, calling `LoadBuiltinModsAsync` runs `AttachGameDirectory`/`FinishBuild`/`DiscoverMods` synchronously on the caller before it returns a task (DMDL:1790-1803), so nothing overlaps. Fixed in P6 below. | DMDL:1783-1825 |
| 3 | B F4 / C H12: user-pak classification does not depend on the builtin load result | **Confirmed.** `LoadModPackageDataAsync` reads `DivinityApp.IgnoredMods` (DMDL:747). The builtin loader touches only locals (DMDL:1783-1825). `IgnoredMods` is mutated only at MWVM:1506, 1508, 4796, 4801 (grep), all outside LoadModsAsync. | grep |
| 4 | C S3: the screen-reader probe re-initialises Tolk on every call when no reader runs | **Confirmed (cost unknown).** `HasToolTip`'s `WhenAnyValue(Description, HasDependencies, UUID)` calls `DivinityApp.IsScreenReaderActive()` (DMD:521-524, DivinityApp.cs:124-127). `EnsureInit` calls `Initialize()` whenever `!IsLoaded()` (ScreenReaderService.cs:69-76). With no reader, `isLoaded = name != null` stays false (CrossSpeak `ScreenReaderWindows.cs:23-33`), so every call does `Tolk.Load()` and `DetectScreenReader()`. Tolk on pool threads is not known to be safe, so this must be memoised **before** construction moves off the UI thread. | cited |
| 5 | C: "30 s token that nothing inside observes" | **Partly wrong.** The VFS build ignores it, but `ReadToEndAsync(token)` observes it (DMDL:1732). | DMDL:1732 |
| 6 | C §1.5: "Core is optimised outside Debug" | **Corrected.** Core sets `Optimize=True` unconditionally (`DivinityModManagerCore.csproj:13`), and the Debug group (`:15-17`) does not override it, so **Core is optimised even in Debug**. LSLib is not optimised in Debug (`LSLib.csproj:19-21`), and neither is GUI (`GUI.csproj:38-40`). So the Debug baseline overstates LSLib's table decode, not the loader loop. That makes E6 (Publish) more important. | cited |
| 7 | A/C scanners record `f.Size()` for entries in **every** folder | **Divergence (edge).** `Size()` throws `NotSupportedException` on an unknown compression nibble (LS/PackageCommon.cs:17, LS/Enums/Compression.cs:32-41). Master calls it only for non-ignored entries of *builtin* folders (DMDL:531). A pak with such an entry in its own folder would fail the new scan where master loads it. **Fix:** catch per entry and record `SizeThrew` on the folder. When Build finds that folder builtin, it fails the pak with master's log text. | cited |
| 8 | Goal early exit once `MODFIXER` (A optional, B C7a, C P4) | **Exact for the status, not for failures.** The status never downgrades (DMDL:506-521). But today a goal read that throws *after* MODFIXER aborts the whole pak (caught at DMDL:707-710), and with the early exit that pak loads. It is kept only in gated P11, and `verifyscan` reports any pak it changes. | DMDL:504-523, 696-711 |
| 9 | B C7a: hand parser equivalent to `^(Mods\|Public)/(.+?)/.+$` | **Rejected in favour of C's `[GeneratedRegex]`.** That has the same semantics by construction, and DMDL is already `static partial class` (DMDL:22). | DMDL:22, 458 |
| 10 | AUDIT-PLAN-08: probe with `Read(metadataOnly)` | **B's correction confirmed:** `MetadataOnly` returns before the file list is read (LS/PackageReader.cs:157). | cited |
| 11 | AUDIT-PLAN row 29: the key must include an IgnoredMods hash and the app version | **Superseded for the IgnoredMods part** by A's facts model: classification is recomputed per load. **Kept for the app version** (header, §3.2). | §3 |
| 12 | A §2.2: Build reproduces `builtinModOverrides` order and `baseGameFiles` exactly | **Confirmed.** Insertion happens at the first non-ignored entry per folder (DMDL:545-549), and `builtinMods` is keyed by `Folder`, last-wins (DMDL:747; DictionaryExtensions.cs:97-100). `baseGameFiles` is collected uncapped (DMDL:531-543) and tested with `Count > 0 && Count < MAX` (DMDL:632). A's per-folder cap plus overflow flag gives the same decision. | cited |
| 13 | `Files` never read; no MD5 anywhere; O(n²) lazy dedupe; reference equality | **All confirmed.** Only DMDL:628 assigns `Files`, and DMD:234 declares it. No `MD5.`/`ComputeHash`/`HashData`/`SHA256` in `src`. `dupes` is a lazy `GroupBy`, re-run by `Contains` (DMDL:788, 805-806). No `Equals` override in DivinityModData or DivinityBaseModData. | grep |
| 14 | C H9: an F5 classifies against a larger builtin set than the first load | **Confirmed.** `SetLoadedMods` adds Larian mods (MWVM:1501-1508), and `LoadAppConfig` only adds (MWVM:4790-4801), never clears. So snapshots must compare r1 with r1 and r2 with r2. | cited |
| 15 | C: "Settings → Clear cache" hook | **Confirmed** at `SettingsWindowViewModel.cs:483-500`. C's side finding (`DeleteCache` short-circuits with `\|\|`) is out of scope here. | cited |
| 16 | B: `LoadResourceAsync` reads the whole file into a discarded buffer | **Confirmed** (DMDL:968-970). Attributing it "~0.47 s" is B's inference: that is the whole profiles step. Out of Stage 4's 9.27 s. | cited |
| 17 | Line-number slip | C cites `BuildModOrderList` at MWVM:2453. It is at **:2463**. | grep |

**New hazard none of the designs states.** The cache must be read with Newtonsoft
`DateParseHandling.None`. The default turns date-shaped string values into `DateTime` and re-formats
them when they are bound back to a `string` property (library behaviour [i]). A JSON round-trip check in P7
catches it either way.

---

## 2. Final Stage 4 plan: the ordered series

### 2.0 Zero-code first step (do this before any branch)

1. Grep the existing 0.3 baseline log for `Took … second(s) to load mod paks.` (DMDL:786) and `Took … seconds(s) to load builtin mods` (DMDL:1816). Both print only the seconds component (`s\.ff`). The builtin line times only the meta loop, not the VFS build.
2. Watch one launch. During "Loading mods from Local AppData folder…", does the progress bar freeze, or does the title bar show "Not Responding"? A freeze is cheap confirmation of §1 item 1.

### 2.1 Series (one stacked chain from `master` fc35358; TEMP commits live only on a local `measure/stage4` branch)

| # | Commit (branch slice) | Change | Expected gain [i] | Measure | R1 gate |
|---|---|---|---|---|---|
| P1 | `perf: log startup load timings in milliseconds` | `[Load]`/`[Refresh]` phase lines with `Stopwatch`. Fields: `tid`, `pool`, `alloc`, `gc0/1/2`, `T+` since process start. Covers each RefreshAsync step (MWVM:2340-2556), `LoadMods.Builtin/Editor/UserPaks/Merge` (MWVM:1627/1637/1665/1670-1714), `Builtin.Attach/FinishBuild/Discover/Meta` (DMDL:1790/1791/1803/1808-1815) and `UserPaks.Enumerate/Scan/Dedupe` (DMDL:757-764/777-784/787-806). Replaces the three `s\.ff` lines. Phase lines only. | 0 | It *is* the measurement. Three launches, median. | none |
| T1 | `TEMP: per-pak load profile` | B's `PakLoadRecord` array written once as `_Logs/load-profile-*.csv`: open/table/loop/goal/meta times, `NumFiles`, flags, `NumParts`, start and end thread. C's counters: `entries`, `solid`, `goalsRead`, `metaLz4`, **screen-reader calls/ms**. B's `ui.dispatcherLatency`. C's forced-compaction memory check. **No per-pak log lines**: `Trace.AutoFlush = true` (MainWindow.xaml.cs:85) and the global lock would distort the numbers. | 0 | Experiments: E1 serial? (Σ per pak / wall). E2 regress time on `NumFiles`. E3 logging on vs off. E4 Defender exclusion (owner's call). E5 OS-cold vs app-cold. E6 Debug vs Publish. **Gate G1** (below). | none |
| P2 | `chore: optional mod-list snapshot for load regression checks` | §4 harness. Off unless a `modsnapshot` marker file exists. | 0 | Run S0a and S0b on P2. Their diff must be empty, or it reveals master's tie nondeterminism. | baseline |
| P3 | `fix: choose duplicate mods deterministically, in linear time` (also cut alone as `fix/duplicate-mod-order`) | Results go into an array indexed by enumeration order, not a `ConcurrentBag`. Use B's `SplitDuplicates`: same rules, O(n), the first strictly highest `VersionInt` wins, so a tie goes to the first in enumeration order. | 0.05-0.3 s (the lazy `GroupBy` re-runs ~1,121 times today) | `UserPaks.Dedupe` ms | Diff empty except `## ties` rows, once; called out in the PR text |
| P4 | `perf: stop retaining every pak's file list` | Delete `Files` (DMDL:478, 488, 628; DMD:234). | 0.1-0.5 s CPU/GC; −E × ~175 B private | private bytes after load vs 478 MB; `gc2` | empty diff |
| P5 | `perf: check for a screen reader once per refresh, not per mod` | Probe once on the UI thread at the start of RefreshAsync and store a bool. `DivinityApp.IsScreenReaderActive()` returns the stored bool. Mods built before a later probe keep their value, which is today's behaviour too: `HasToolTip` only re-evaluates on a property change. **Mandatory before P6** for thread safety, whatever it gains. | 0 to 4 s (unknown: T1 `sr=`) | `sr=` count → ~1 per refresh | empty diff (`HasToolTip` is UI-only; ask a screen-reader user to check tooltips) |
| P6 | `perf: load mods off the UI thread and scan paks in parallel` | (a) In LoadModsAsync, capture the paths on the UI thread. Start `Task.Run(() => LoadBuiltinModsAsync(...))`, the editor task and the pak task **together**, then await them in today's order. The merges at MWVM:1670-1714 run unchanged, after all three complete. Keep each progress increment as each task finishes. (b) In `LoadModPackageDataAsync`, use bounded `Parallel.ForEachAsync` with DOP = `Math.Clamp(ProcessorCount - 1, 2, 8)`, add `ConfigureAwait(false)` throughout the new and touched Core code, and keep today's partial-results-on-cancel (catch `OperationCanceledException`). | Cold LoadModsAsync ~8-9 s → **~2.5-4.5 s** on an SSD; the builtin load (0.8-2.5 s) is hidden behind the pak scan; **the UI stays responsive** | `tid/pool` now pool; parallelism factor; DOP sweep 2/4/8 (TEMP knob); progress bar animates | empty diff; F5 r2 diff empty |
| P7 | `refactor: split pak loading into scan and build` | A's `PakScanResult`/`PakFolderFacts`/`BuildModDataFromScan` (A §2.2), plus the `SizeThrew` fix (§1 item 7). Extract `ParseScriptExtenderConfig(string text, string label)` from DMDL:1690-1725. The import callers (MWVM:1847, 3283, 3444) become Read → Scan → Build with no cache. | ~0 | — | **`verifyscan` = 0 mismatches** (§4.3) **and** an empty snapshot diff |
| P8 | `perf: cache pak scan results between launches` | §3. `ModMetadataCache` in Core. Clear-cache hook. `nocache` marker. | Warm user-pak stage → ~0.2-0.5 s. Warm LoadModsAsync ≈ max(builtin VFS, paks) + merge ≈ **1-2.5 s** while the base game stays uncached | `[ModCache] hits/misses/racy/sentinel/load ms/save ms` | cold, fill, warm and F5 snapshots all equal S0 (§4.2) |
| P9 | *(gate: `Builtin.*` ≥ 0.5 s after P8)* `perf: cache base-game module discovery` | §3.6 | Warm LoadModsAsync → **~0.4-1.0 s** | `Builtin.*` ≈ 10 ms warm; one VFS run after a game patch | `## builtin` diff empty; touch-a-game-pak test |
| P10 | *(gate: P1 `T+` shows the cache read on the critical path)* `perf: read the mod cache while the window starts` | C §2.7: in the `App` ctor, read the cache file and stat-validate entries on the pool, as facts only. Overlaps the ~2.5 s before RefreshAsync (0.3 baseline: 1.09 → 3.61 s). | −0.1-0.3 s | `T+` offsets | warm diff empty |
| P11 | *(gate: cold loop CPU still ≥ 1 s after P6)* `perf: cheaper per-entry classification` | `[GeneratedRegex]` for both patterns (DMDL:448, 458). Hoist the `Mods/{folder}/Story/RawFiles/Goals` needle per folder (DMDL:504). Array loop for `IgnoreBuiltinPath`. Goal early exit (§1 item 8). | cold −0.3-1.5 s | CSV slope | `verifyscan` = 0 |

**Gate G1 (after T1, before P5/P6):** `sr` ≥ 0.3 s means P5 is a headline win; ship it first, alone. `entries` ≥ 0.5 M with `loop + read` dominant means the cache is the main lever. A small `entries` count with a large `read` means per-pak fixed cost dominates: watch the sentinel cost, see W5. `Builtin.*` ≥ 1 s means P9 is worth it. `solid > 0` means those paks fully decompress cold (LS/PackageReader.cs:171-174), so the cache matters more.

**Upstream slicing** (all upstreamable; nothing depends on fork-only code; no new NuGet):

1. `fix/duplicate-mod-order` = P3.
2. `perf/startup-cold` = P1, P4, P5, P6, stacked on (1) until (1) merges.
3. `perf/mod-metadata-cache` = P7-P10.

P2 goes in as `chore/mod-snapshot`, or stays in the PR descriptions as the verification method; the owner chooses. Each slice merges into `fork/main --no-ff` after the §5 matrix passes. The owner pushes.

**Deferred, with reasons:**

- B's custom LSPK v18 reader: it re-states LSLib, which has the highest R1 risk, and the cache makes it cold-only.
- C's lean game scan (P12): it re-states VFS priority (LS/VFS.cs:42).
- The file watcher (C P11): its value is a fork-only "mods changed" toast.
- `ModuleMeta` DTO (C P13): port prep, no perf gain.
- LSLib-side `PtrToStructure` / `byte[256]` per entry (LS/BinUtils.cs:78-91, LS/PackageFormat.cs:419-441): a third repo.
- `LoadResourceAsync` double read: belongs to the profiles step.
- Editor-project double enumeration: negligible, and it carries merge semantics (MWVM:1542).

### 2.2 Expected end state [i] (to be replaced by P1 numbers)

| | LoadModsAsync cold (no cache, OS cache warm) | Warm (nothing changed) | After a game patch | Whole app to usable |
|---|---|---|---|---|
| Today (0.3) | 9.27 s | 9.27 s | 9.27 s | ~15.2 s |
| After P3-P6 | 2.5-4.5 s | 2.5-4.5 s | 2.5-4.5 s | ~8.5-10.5 s |
| After P8-P9 | 2.5-4.5 s | **0.4-1.0 s** | warm + one VFS run (0.8-2.5 s) | ~6.5-7.5 s |

The floor outside Stage 4 is ~6 s: 3.61 s before refresh, 0.47 s profiles, 1.26 s list building, 0.63 s UI
(0.3 baseline). P1's `T+` lines put the 2.52 s "init, settings, resources" span on the board for a later stage.
The warm floor inside Stage 4 is `DivinityModData` construction (~40 Rx pipelines per mod, DMD:386-567),
which is Stage 6 work.

---

## 3. Cache design

### 3.1 What is cached: facts, never results

The cache holds A's `PakScanResult` per pak, independent of which folders count as base-game:

- the chosen `meta.lsx` text (the selection at DMDL:561-589 depends on the pak file name, which is the key);
- the last `EXTENDER_MOD_CONFIG` entry's text, plus a read-failure flag (DMDL:490-493, 1690-1725);
- the Osiris status (DMDL:504-523);
- `FileCount` and `NumParts`;
- per `Mods|Public/<folder>`, in first-seen order: `FirstUnignoredIndex`, `FirstUnignoredFile`, up to
  `MAX_FILE_OVERRIDE_DISPLAY` candidate base-game names plus `Overflow`, and `SizeThrew`.

Build re-runs the unchanged `ParseMetaFile` on the text and recomputes classification against the **current**
`builtinMods` on every load. So:

- a game patch, an `IgnoredMods.json` update or an F5 superset (§1 item 14) never rescans a user pak, and a cache
  entry cannot carry a stale override decision;
- a parser fix needs no cache migration.

### 3.2 Key and invalidation

| Level | Rule |
|---|---|
| Whole file discarded | Any of these differs from the current value: `Schema`; `ScanVersion` (a constant next to `ScanPakAsync`: "bump on any change to scan logic"); `LogicHash` (B: XxHash64 of the regex pattern strings, `EXTENDER_MOD_CONFIG`, `MAX_FILE_OVERRIDE_DISPLAY`, `Path.DirectorySeparatorChar`, sorted `IgnoreBuiltinPath`); the **Core assembly version** (C: one cold start per release, ~3 s after P6, buys protection against a forgotten `ScanVersion` bump); `RootHash` (SHA-256 of the normalised Mods path, so no user name is stored). Also discarded on any parse exception or an entry-count mismatch. Discarding means: log `[ModCache] discarded: <reason>`, run cold, overwrite. It never throws past the loader. |
| Entry key | Path relative to the Mods root. `OrdinalIgnoreCase` on Windows, `Ordinal` elsewhere. |
| Entry validators | `Length` and `LastWriteTimeUtc.Ticks` from a **fresh `new FileInfo(path)`** (`GetFileAttributesEx`), never the enumeration's find data, which can be stale for hard-linked files and so matters for the LIB designs (B §2.6). For `NumParts > 1`, every part's length and mtime too. Compare exactly; never use "newer than". |
| Racy rule (B/C, git's racy-index rule) | Each entry stores `ScannedAtUtc`. If `\|mtime − ScannedAt\| < 3 s`, or the mtime is more than 1 min in the future, the entry is rescanned. This covers FAT/exFAT/SMB 2 s granularity (Steam Deck SD cards) and a pak written seconds before the save. |
| Sentinel (A/C) | First 40 bytes (the whole v15-v18 header: `FileListOffset`, `FileListSize`, flags, part count; LS/PackageFormat.cs:212-260) plus the last 32 bytes (the table tail for v18, the trailer for v13). On by default (`VerifySentinel`), and its cost is logged separately. It is still far cheaper than today, which opens and decodes every pak. Whether to turn it off is decided by W5. |
| Not used | **ctime** (C). NTFS tunnelling preserves it across delete-and-recreate, and Linux semantics differ, so it adds misses without adding safety. |
| Outcomes cached | Facts. Deterministic "no mod" results (no meta and no override; meta fails to parse), so that each one is not rescanned every launch. `FormatError` (`InvalidDataException`, `NotAPackageException`, `NotSupportedException`) with its message, so the same log line is re-emitted (B). |
| Never cached | `IOException`, `UnauthorizedAccessException` (a locked or half-copied pak is retried next launch), and any run that was cancelled: no save at all (A). |
| Prune | Entries not enumerated this run are dropped at save time: deleted, renamed, moved or now-partial paks. |
| Manual | Settings → Clear cache (`SettingsWindowViewModel.cs:483-500`) also deletes the file. A `nocache` marker file next to the exe bypasses reads and writes for measuring. |
| Documented residual | Same path, same length, same 100 ns mtime outside the racy window, and an identical 72-byte sentinel, but different content. In practice it needs a deliberate timestamp copy onto a same-size rebuild. Zip-extracted paks carry the author's packaging mtime, so two builds differ there anyway. Clear cache recovers it. |

### 3.3 Format and schema

Newtonsoft JSON, as everywhere else in the house:

- streamed with `JsonTextReader`/`JsonTextWriter`;
- `DateParseHandling.None`;
- UTF-8 without a BOM;
- written with fully qualified `Newtonsoft.Json` types, because Core also imports `System.Text.Json` globally
  (`src/Core/Usings.cs`).

The estimated size is 4-7 MB, mostly meta text. **Gate:** if `[ModCache] load` exceeds 150 ms in Publish,
switch to B's `BinaryWriter` layout (magic, schema, logic hash, payload length, XxHash64 payload checksum;
B §2.3). `System.IO.Hashing` already flows from `LSLib.csproj:53`.

```json
{ "Schema": 1, "ScanVersion": 1, "LogicHash": "…", "CoreVersion": "1.0.12.0", "RootHash": "9F2C…",
  "WrittenUtc": 638650123456789012, "EntryCount": 1097,
  "Entries": [ { "Path": "Foo.pak", "Length": 25165824, "LastWriteUtc": 6386…, "ScannedAtUtc": 6386…,
      "Head": "TFNQSxIA…", "Tail": "…", "Parts": [], "Outcome": 0, "Error": null,
      "Scan": { "MetaEntry": "Mods/Foo/meta.lsx", "MetaText": "<?xml …", "ExtenderConfigEntry": null,
        "ExtenderConfigText": null, "ExtenderConfigReadFailed": false, "OsirisModStatus": 0,
        "FileCount": 412, "NumParts": 1,
        "Folders": [ { "Folder": "Foo", "FirstUnignoredIndex": 3, "FirstUnignoredFile": "Mods/Foo/…",
          "BaseGameFiles": ["…"], "BaseGameFilesOverflow": true, "SizeThrew": false } ] } } ],
  "Game": null }
```

### 3.4 Location

`DivinityApp.GetAppDirectory("Data", "modcache.json")`, beside `settings.json` (MWVM:1115). That keeps the
install portable, and the release pipeline already strips `Data/` (CLAUDE.md §7). The path is a constructor
argument of `ModMetadataCache`. If the directory cannot be written, the app logs one line per run and works
uncached; this is not a `ShowAlert` because the user cannot act on it and nothing is lost. **Owner decision:**
an Info alert on rebuild is one line. For a future Linux/Flatpak build, fall back to `XDG_CACHE_HOME` /
`LocalApplicationData`.

### 3.5 Write and concurrency

- **Load.** The cache is loaded once before the scan and is read-only during it. Workers write only their own
  array slot, so there are no locks on the hot path.
- **Save.** It runs on the pool after `LoadModPackageDataAsync` returns, and only if something changed. A static
  `SemaphoreSlim(1)` serialises saves. The writer writes `modcache.json.<pid>.tmp`, calls `Flush(true)`, then
  `File.Move(tmp, final, overwrite: true)`.
- **Readers** open with `FileShare.ReadWrite | FileShare.Delete`. `IOException` gets 3 retries at 100 ms, then
  one log line. Stale `.tmp` files older than 1 h are deleted. Two instances: last rename wins, and both files
  are valid (there is no single-instance guard, CLAUDE.md §3).

### 3.6 Base-game section (P9, gated)

- **Key:** every `*.pak` under `Data/` (flat) and `Data/Localization/` (depth ≤ 3), *including* blacklisted and
  `_N` parts, as (relative path, length, mtime UTC) in enumeration order. That is a superset of what
  `AttachGameDirectory` attaches (LS/VFS.cs:173-181), so any patch or hotfix invalidates it.
- **Value:** the `ModInfo` list in `modResources.Mods.Values` order (name, `PackagePath`, `ModsPath`, meta path)
  plus the meta text. `LoadModFromModInfo` (DMDL:1727-1781) is refactored to take text, and it still calls
  `File.GetLastWriteTime`, so `LastModified` is byte-identical.
- **Bypass:** whenever `Data/Mods` has any subdirectory (loose editor projects shadow through VFS precedence).
  That costs nothing for this owner.

---

## 4. R1 harness

### 4.1 Snapshot (P2): `src/Core/Util/ModListSnapshot.cs`, toolkit-neutral

- **Trigger:** an extension-less `modsnapshot` file next to the exe; its content is the label. Inert otherwise
  (one `File.Exists`).
- **Output:** `_Logs/modsnapshot_<label>_r<refresh#>_<yyyyMMdd-HHmmss>.tsv`, one file **per refresh**. It is
  invariant culture, `\t \r \n` are escaped, null is written as `<null>`, and rows are sorted by `UUID`, then
  `FilePath` (Ordinal).
- **Hook A:** MWVM:1716, before `return`. It records `finalMods`, `baseMods`, `Duplicates`, `projects`, and
  dedupe winners, which P2 adds to `ModLoadingResults`.
- **Hook B:** after `BuildModOrderList` (MWVM:2463, inside the setup `Observable.Start`). It records the active
  order and how each entry resolved.
- **Sections:**
  - header: label, git hash, configuration, cache mode, counts, `IgnoredMods` count and hash, `IgnoreBuiltinPath`;
  - `## mods`: UUID, Folder, Name, VersionInt, HeaderVersion, PublishVersion, Author, MD5, PublishHandle, ModType,
    FilePath, LastModifiedUtcTicks, FileSize, IsUserMod, IsEditorMod, IsLarianMod, IsHidden, HasMetadata,
    IsForceLoaded, IsForceLoadedMergedMod, ForceAllowInLoadOrder, CanAddToLoadOrder (a synchronous `BindTo`,
    DMD:530-531), `Guid.TryParse(UUID)` (computed directly rather than reading the async `HasInvalidUUID` OAPH),
    OsirisModStatus, HasScriptExtenderSettings, SE.RequiredVersion, SE.FeatureFlags, Tags, Dependencies and
    Conflicts as sorted `uuid@ver`, BuiltinOverrideModsText (escaped), and an XxHash64 of Description;
  - `## duplicates` (losers);
  - `## winners` (UUID, winner path, candidates);
  - `## ties` (UUIDs whose top VersionInt is shared);
  - `## builtin` (`baseMods` in list order);
  - `## order` (Index, UUID, Found, ResolvedFilePath, IsForceLoaded) and the ForceLoaded set.

### 4.2 Protocol

The game install, the Mods folder and `<exe>/Resources/IgnoredMods.json` stay the same throughout. Turn
update checks off. `modsettings.lsx` is only read.

1. On P2, run S0a and S0b, each as launch, then F5, then close. `git diff --no-index S0a_r1 S0b_r1` (and r2)
   must be empty. If it is not, master is nondeterministic. The `## ties` section names the UUIDs; record them.
2. For each later commit Pn, delete the cache (or use `nocache`) and run **cold**, then run **fill**, then run
   **warm**. Each run is launch, F5, close.
3. **Pass:** `S0 vs cold`, `cold vs warm` and `fill vs warm` are empty for r1 against r1 and r2 against r2.
   The single allowed exception is the `## ties` rows at P3: the winner must become the first in enumeration
   order and then stay stable. (Master's tie winner is "first in `ConcurrentBag` order", which is unspecified
   [i], so it may differ from enumeration order.)
4. Repeat steps 1-3 once in **Publish** (R5).
5. Compare with `git diff --no-index --word-diff=color a.tsv b.tsv`, or PowerShell
   `Compare-Object (gc a) (gc b)`.

### 4.3 `verifyscan` oracle (TEMP, on top of P7, P8 and P11; from B, extended)

With an extension-less `verifyscan` file present, the build processes every one of the 1,097 paks twice:

- through the **legacy** `InternalLoadModDataFromPakAsync`, kept only in the TEMP commit;
- through **Scan → JSON round-trip → Build**.

It compares the two snapshot rows **under three builtin contexts:**

1. the current `builtinMods`;
2. an empty dictionary;
3. "every folder this pak touches is builtin", with dummy `DivinityModData { Folder = f, Name = f }`.

Context 3 drives every pak through the override, merged and `BuiltinOverrideModsText` branches. That covers
classification paths the owner's real set may never hit, with no crafted fixtures. The legacy function
already takes `builtinMods` as a parameter (DMDL:466), so this costs about 40 lines. **Merge rule:**
0 mismatches. The run logs one summary line, plus one line per mismatch with both rows.

### 4.4 Hazards the series must not trip (merged from A §3.3, B §3.2, C §3.3)

| Hazard | Guard |
|---|---|
| Tie winner changes (DMDL:787-806) | P3 rule; `## ties` |
| `else if` precedence: config, then meta, then folder (DMDL:490-499) | moved verbatim; `verifyscan` |
| Meta choice by pak name (DMDL:571-588) | moved verbatim; the key is the path |
| `metaCount == 0 ⇒ isOverriding = hasBuiltinDirectory`, which ignores `IgnoreBuiltinPath` (DMDL:564-567) | re-stated literally; `verifyscan` context 3 |
| Override-only `Folder` = first inserted key, null if none (DMDL:617) | first-unignored ordering |
| `Size()` throwing (§1 item 7) | `SizeThrew` |
| A builtin set that differs between first load and F5 (MWVM:1501-1508) | classification per load; r2 snapshots |
| A stale entry | validators, racy rule, sentinel, header; cold/warm diffs |
| OAPH values settling late once construction moves to the pool | the snapshot uses only raw and `BindTo` values. The settle order is unchanged [i]: the setup `Observable.Start` is queued after all posts on the same scheduler. |
| Partial-pak filter with the never-cleared `_AllPaksNames` (DMDL:429, 763) | untouched (a separate `fix/*`) |
| `LastModified` source | `FileInfo.LastWriteTimeUtc.ToLocalTime()` equals `File.GetLastWriteTime` (DMDL:646); a snapshot column |
| Builtin `baseMods` order (it feeds `MergeModLists` ties at MWVM:1542) | stored in `Mods.Values` order; `## builtin` |

---

## 5. Test matrix

Every row: the snapshot diff against the stated reference is empty (apart from noted rows), there are no new
exceptions in the log, and the `[Load]`/`[ModCache]` lines are recorded. "Same" means equal to master (S0)
given the same files. Fixtures: crafted paks via LSLib/Divine on Windows, or copies of real mods.

| # | Scenario | Setup | Expected | Commits |
|---|---|---|---|---|
| 1 | Master stable? | S0a vs S0b | empty, or ties listed | P2 |
| 2 | Cold | delete the cache | misses = 1,097; one save; same | P8+ |
| 3 | Warm | relaunch | hits = 1,097; no save; same | P8+ |
| 4 | Warm F5 | F5 | r2 same as S0 r2 | P8+ |
| 5 | OS-cold and warm cache | reboot or empty the standby list | hits = 1,097; sentinel ms logged | P8 |
| 6 | Game patch | change a `Data/*.pak` mtime (owner's call), or use a copied `Data` | user paks all hit; game section misses once; `## builtin` same | P8, P9 |
| 7 | `IgnoredMods.json` gains a folder that a user pak touches | edit `<exe>/Resources` | **no user-pak rescan**; that pak's `IsForceLoaded`/text match master run with the same JSON | P8 |
| 8 | `IgnoreBuiltinPath` changes | edit the JSON | header discard, cold; equals master with the same JSON | P8 |
| 9 | App version bump | 4-file rule (CLAUDE.md §2.5) | discard, one cold start | P8 |
| 10 | Mod updated, new size | replace X | 1 miss; new version; dedupe as master | P8 |
| 11 | Same size, new mtime | set `LastWriteTime` | 1 miss | P8 |
| 12 | Same size and mtime, header or table changed | flip a byte in the first 40 or last 32 bytes, then restore the mtime | sentinel miss; behaviour as master (often a load failure with master's log text) | P8 |
| 13 | Same size, mtime and sentinel, content changed | flip a byte mid-data, restore the mtime | **stale hit**: the documented residual; Clear cache fixes it | P8 |
| 14 | Racy window | copy a pak in and F5 within 3 s; or an exFAT Mods folder | rescanned next launch, then stable | P8 |
| 15 | Renamed, moved into a subfolder, deleted | — | miss and prune; a meta-less override's UUID follows the path, as master | P8 |
| 16 | Hard-linked pak updated through the other link | `mklink /H`, update the library copy | miss (fresh `FileInfo`) | P8 |
| 17 | Duplicate UUID, different versions | two paks | higher version wins on cold, warm and F5; the loser is in `## duplicates`; alert shown | P3+ |
| 18 | Duplicate UUID, equal versions | two copies | first in enumeration order wins, 5 runs in a row stable; `## ties` | P3+ |
| 19 | Pak with a base-game UUID | copy a Larian UUID | base mod added to Duplicates as master (MWVM:1695-1702) | all |
| 20 | Editor project in `Data/Mods` | create one | game cache bypassed; `IsEditorMod` row same; editor copy wins the merge (MWVM:1542) | P6, P9 |
| 21 | Override pak without meta | only `Public/Shared/...` | `UUID` = path, "File Override", `Folder` = first key, ≤ 9 names or "Folder (Name)" text; same | P7+ |
| 22 | Override with only ignored paths | only `Public/Game/GUI/Assets/...`, with and without meta | without meta: force-loaded, `Folder` null; with meta: not overriding; same | P7+ |
| 23 | Merged override | meta + own folder + `Public/Shared` | `IsForceLoadedMergedMod`, `CanAddToLoadOrder` same | P7+ |
| 24 | Unsupported compression nibble in own folder | crafted | loads as master (the §1 item 7 fix) | P7 |
| 25 | Multi-part, solid, v15/v16 | samples | `_1` skipped as partial; touching a part makes the main pak miss; `verifyscan` 0 | P7, P8 |
| 26 | Corrupt pak | truncate | master's log line; FormatError cached and re-logged; restore → miss | P8 |
| 27 | Locked pak | open exclusively | master's log line; not cached; retried | P8 |
| 28 | Corrupted cache | truncate; garbage; `Entries: null`; schema 0 | discard, cold, rewrite; no exception reaches `RunTask` | P8 |
| 29 | Two instances | start two within 1 s | both load; valid file; no `.tmp` left | P8 |
| 30 | Cancel mid-scan | TEMP short token | partial results as master; no save | P6, P8 |
| 31 | Read-only app folder | Program Files | one log line; works uncached | P8 |
| 32 | No screen reader / NVDA running | both | `sr` ≈ 1 per refresh; tooltips hidden only with NVDA, as master | P5 |
| 33 | UI responsiveness | drag the window during load | no "Not Responding"; progress animates | P6 |
| 34 | Publish | rows 2, 3, 4, 10, 17 in Publish | same (R5, `_Lib` layout) | all |

**Optional unit tests.** These need no pak files: `SplitDuplicates` ties; Build from synthetic
`PakScanResult` (the A §2.2 proof cases); cache validators against temp files; JSON round-trip. The repo has
no test project (the `.sln` lists none), so this would be a fork-only `src/Core.Tests` unless LaughingLeader
wants it. It runs on Windows only (Core is `net8.0-windows`, UseWPF).

---

## 6. Open questions that need a Windows measurement

| # | Question | Settles |
|---|---|---|
| W1 | E (total user-pak entries) and the split between read, loop, goal and meta (T1) | whether the cache or the cold path carries the win; P11 gate |
| W2 | Does LoadModsAsync run on the UI thread (`tid`/`pool`), and does the window freeze? | §1 item 1; P6's expected size |
| W3 | Screen-reader probe calls and ms, with and without NVDA | P5 size and priority |
| W4 | `Builtin.Attach/FinishBuild` ms | P9 gate; whether a lean scan is ever worth it |
| W5 | Disk type; OS-cold vs app-cold; sentinel ms at OS-cold | `VerifySentinel` default; DOP |
| W6 | Defender share (exclusion run, the owner's call) | cold-start ceiling |
| W7 | Logging overhead (the baseline ran with `debug` on) | how the 9.27 s is read |
| W8 | Debug vs Publish (LSLib is unoptimised in Debug, §1 item 6) | realistic targets |
| W9 | Count of `## ties` in the owner's set; S0a = S0b? | whether P3's behaviour note matters |
| W10 | `[ModCache] load` ms | JSON vs binary gate |
| W11 | DOP 2/4/8 sweep | P6 default |
| W12 | Private bytes before and after P4 (plus the compaction check) | the memory claim |
| W13 | Solid and multi-part pak counts | cold-path outliers |

Owner decisions, not measurements: accept the tie rule (P3); accept the residual in row 13; alert or silence on a
cache rebuild; whether the optional unit-test project stays fork-only.

---

## 7. Where the code contradicts the plan's Stage 4 candidate list

1. **"Per-pak entry enumeration + regex, likely the dominant cost."** Partly. The regex runs on every entry
   *except* the extender config and meta entries (DMDL:490-499), and it is one of several per-entry costs: the
   interpolated needle at :504, `IgnoreBuiltinPath.Any` at :529, and LSLib's `PtrToStructure` plus `byte[256]`
   per entry (LS/BinUtils.cs:78-91, LS/PackageFormat.cs:419-441). The structural cause the plan misses is that
   the whole load is **serial on the UI thread** (§1 item 1).
2. **"MD5 hashing."** Contradicted: there is no hashing anywhere in `src`. `MD5` is a meta attribute copied
   through (AUDIT-PLAN row 27, re-grepped).
3. **"Cache keyed by path + mtime + size … low risk because a stale entry is detectable."** Two corrections.
   (a) A *result* cache with that key would be wrong: `IsForceLoaded`, `IsForceLoadedMergedMod` and
   `BuiltinOverrideModsText` depend on `IgnoredMods`, which changes with no pak changing (game patches; F5
   enrichment at MWVM:1501-1508). Cache facts and reclassify (§3.1). (b) Staleness is not always detectable
   (same size and mtime): hence the racy rule, the sentinel and the documented residual.
4. **"Double enumeration of loose mods."** Real (LS/VFS.cs:356-377, DMDL:1757-1761 vs `LoadEditorProjectsAsync`)
   but negligible for ~0 projects. Removing it is not a pure perf change, because `MergeModLists` prefers the
   editor copy (MWVM:1542). Kill it.
5. **"Mostly src/Core, so this survives a future Avalonia port."** True for the cache and the scan/build split.
   But the largest cold-path fix (P6, off the UI thread) and the snapshot hooks are in MWVM; a port keeps only
   the pattern there.
6. **"Snapshot the full mod list (UUID, version, flags)."** Not enough. It also needs FilePath, the winners,
   duplicates and ties, `BuiltinOverrideModsText`, the resolved active order, and a separate F5 (r2) snapshot,
   because the first load and an F5 legitimately differ on master (MWVM:1501-1508).
7. **Missing from the list:**
   - UI-thread serial execution;
   - retained `Files` (DMDL:628);
   - the full base-game VFS build (DMDL:1790-1791);
   - the per-mod screen-reader probe (DMD:521-524);
   - O(n²) lazy dedupe (DMDL:788, 805-806);
   - **tie-winner nondeterminism**, which is a correctness issue on master today, not only a perf one.

---

## 8. Owner intentions this plan honours (gleaned from the notes)

- Measure first: P1/T1 come before any optimisation, with gates.
- R1 above speed: facts not results, the snapshot, `verifyscan`.
- Upstream only fixes and perf, on `fix/`, `perf/` and `chore/` slices cut from master. TEMP commits are dropped.
  Merge to `fork/main` with `--no-ff`. The owner pushes.
- WPF now, Linux open: everything new is in Core with no WPF types; the path comparer and cache path are
  platform-aware; there is no ctime.
- House rules: the Fody/RxUI pin is untouched; `DivinityApp.Log`; no `string?`; tabs, CRLF, BOM, Allman, `var`;
  unprefixed types (`ModMetadataCache`, `PakScanResult`, `ModListSnapshot`); Newtonsoft persistence.
- Publish tested alongside Debug (R5). Accessibility kept: P5 has an explicit screen-reader check.
- No Redux code; WPF grouping is not relevant here.
