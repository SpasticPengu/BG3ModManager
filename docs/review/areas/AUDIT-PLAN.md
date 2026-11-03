# AUDIT-PLAN: adversarial audit of implementation-plan.txt code claims

Auditor scope: every code-level claim in `notes/implementation-plan.txt` for Stages 0.3, 2, 3, 4, 5, 6.1, 6.2, 6.3, 8 and 9, checked against master `fc35358`, the `External/lslib` submodule, `origin/perf/list-render`, and the source of the two third-party libraries whose behaviour the claims depend on. Those are SharpCompress 0.39.0 (GUI.csproj:62), cloned at tag `0.39.0`, and AdonisUI 1.17.1 (GUI.csproj:48-49), cloned at tag `1.17.1`. Both clones are in the scratchpad (`sc/src`, `adonis/src`). I built nothing and ran nothing. The only exception is the Stage 6.1 cherry-picks, which I ran in a throwaway worktree that I have since removed (`git worktree list` is clean).

All paths are repo-relative unless noted. MWVM = `src/GUI/ViewModels/MainWindowViewModel.cs`, DMDL = `src/Core/Util/DivinityModDataLoader.cs`.

## Summary

- 36 rows checked: **21 Confirmed, 10 Partly, 4 Refuted**, plus 1 explanation (claim 25).
- **Refuted:**
  - **E6.** The divide-by-zero has no effect.
  - **Stage 5 step 1.** "Only assign Visibility when it changes" is already done: the `[Reactive]` setter calls `RaiseAndSetIfChanged`.
  - **Stage 5 step 2.** The measured filter path does no per-mod string allocation.
  - **Stage 4 MD5.** No hashing exists anywhere in the source.
- **Most important Partly corrections:**
  - **MeasureText `=` dividers.** `=` cannot throw in `MeasureText`, and only one string per list is ever measured. The real bug is that unmapped glyphs are measured as zero width.
  - **I1.** The causal chain does not hold for zip files. SharpCompress 0.39 Deflate and Stored entry streams fill the whole buffer in one read. Also, `SafeDeserialize` does not return null: it logs and can return a *partial* order.
  - **E3.** The real cancellation path is different from the one the plan names. `Task.Delay(0, token)` throws, and the abandoned child task keeps writing while the `using` blocks dispose the writer. The result is a genuinely corrupt zip. The `return` at 3595 instead leaves a *valid but incomplete* zip.
  - **I2.** The shared flag leaks, but the leak loses no extra mod. The real damage is that `File.Create` truncates the user's existing pak before the copy, and the partial pak is never deleted.
  - **Inactive list slower than active.** The per-item filter loop cannot explain it. The costs are flat across 186 vs 935 rows and across "nothing changed" vs "everything changed". The 3.27 s run was the first filter of the session.
- **Top missed defects:**
  - **AUDIT-PLAN-01 (High).** Importing an archive exported from the "Current" order runs `SetFrom`. That flips the live Current order to `IsModSettings=false`, or overwrites a saved order. A later Save then writes JSON into `modsettings.lsx`.
  - **AUDIT-PLAN-02 (High).** `.bz2`, `.xz` and `.zst` imports write a 0-byte pak.
  - **AUDIT-PLAN-03 (High).** Export cancellation races the writer.
  - **AUDIT-PLAN-06 to 08 (Medium, perf).** These outrank the regex as Stage 4 candidates:
    - Pak loading is effectively serial despite `Task.WhenAll`.
    - `DivinityModData.Files` stores every file path of every pak and is never read.
    - The builtin VFS builds a full tree over the Data paks just to find about 30 base mods.
- **Stage 6.1 branch claims.** All verified mechanically. One nuance: `7fa8347` conflicts on its own because it depends on `2858a23`, so the cherry-pick order matters.

## Claims

| # | Stage | Claim | Verdict | Evidence | Correction |
|---|---|---|---|---|---|
| 1 | 2 | E1: `File.OpenWrite` at ~3579 doesn't truncate, so overwriting a larger zip corrupts it | Confirmed | MWVM:3579; SharpCompress `ZipWriter.Dispose` writes CD+EOCD at the new end (Writers/Zip/ZipWriter.cs:50-61), so the old tail and its old EOCD remain after it | Only reachable through **Export As** onto an existing file (`OverwritePrompt=true`, MWVM:3722). The default export names the file with a timestamp to the second (MWVM:3568) and never overwrites. So "sometimes" here means "only when the user picks an existing, larger file in Save As". |
| 2 | 2 | E2: the catch at ~3660-3670 swallows every failure | Confirmed (lines wrong) | Catch is at MWVM:3679-3683 inside `WriteZipAsync` (3663-3694) | Two failure modes behave differently. (a) Missing or locked file: `IWriterExtensions.Write(string)` checks `Exists` and calls `OpenRead` *before* `WriteToStream` (Writers/IWriterExtensions.cs:12-23), so no entry is written and the zip stays valid but lacks the mod. (b) Read error mid-copy: `ZipWriter.Write` does `using var output = WriteToStream(..); source.TransferTo(output)` (ZipWriter.cs:83-86), so the dispose *finalizes a truncated entry with a matching CRC*. The zip then silently carries a corrupt pak. "Very likely the real cause of import didn't get all my mods" is unproven: see AUDIT-PLAN-01, 02 and 04 for import-side causes. |
| 3 | 2 | E3: `return` inside the `using` at ~3593 leaves a partial zip and skips temp cleanup | Partly | `return false` is at MWVM:3595 (3593 is the `foreach`); `Directory.Delete` at 3650 is skipped; `ZipWriter.Dispose` writes the central directory (ZipWriter.cs:50-61) | That path leaves a **well-formed but incomplete** zip, not a truncated one. It also shows no alert at all. The *usual* cancel path is different. `Task.Delay(0, token)` throws `TaskCanceledException` (MWVM:3689), which propagates to the catch at 3640 and shows a Danger alert containing the exception text. Meanwhile the `StartNew` child task (3673) is still inside `writer.Write`, and the `using` blocks dispose the writer and stream underneath it. That race produces a corrupt zip on disk. See AUDIT-PLAN-03. |
| 4 | 2 | E4: the `Task.Delay(0)` spin loop at ~3675-3685, and `AttachedToParent` is a no-op | Partly | MWVM:3670-3691 | `Task.Delay(0, ct)` returns an already-completed task, so the `await` continues synchronously. The loop is a **synchronous busy-spin pinning one pool thread at 100% of a core**, with no scheduling traffic, so it is not "hammering the thread pool". The `AttachedToParent` claim is Confirmed: `Task.Run` uses `DenyChildAttach`. |
| 5 | 2 | E5: non-recursive `Directory.Delete` at ~3650, outside the try | Confirmed | MWVM:3650; temp paks exist only for editor mods (3603-3625); `CreatePackageAsync` can fail after `PackageWriterFactory.Create` and leave a partial file (src/Core/Util/DivinityFileUtils.cs:161-177) | Narrow trigger: only editor mods leave files in the temp dir. When it throws, the exception escapes `ExportLoadOrderToArchiveAsync`, so `OnMainProgressComplete` (MWVM:3543, 3737) never runs and the progress overlay stays up. |
| 6 | 2 | E6: `1d / modPaks.Count` at ~3574 is Infinity, rated HIGH (cosmetic) | **Refuted** | MWVM:3575; used only at 3628, inside the `foreach` over `modPaks` | When `Count == 0` the loop body never runs, so the Infinity is never used. No visible effect; a guard is harmless but this is not a defect. |
| 7 | 2 | "Exported!" is shown regardless of failures | Confirmed (with nuance) | MWVM:3632-3636 runs after the loop; per-mod failures are swallowed at 3679 | Per-mod failures do still show success, and the text is actually "Exported load order to '…'". On cancel you get either a Danger alert containing `TaskCanceledException` or, through the `return` at 3595, no message at all. The callers ignore the bool (3541, 3735). |
| 8 | 2→8 | Export keys entries by `Path.GetFileName(mod.FilePath)`; Stage 8 must decide what happens when two orders reference different versions of one mod | Partly | MWVM:3598 (editor mods 3622); `mods` is `SourceCache<DivinityModData,string>(mod => mod.UUID)` (MWVM:82) | The filename keying is confirmed. But on the exporting machine each UUID maps to exactly one loaded pak, so a multi-order union cannot contain two versions of one mod. The version conflict only arises on import, against the receiver's existing paks. A real related risk: `LoadModPackageDataAsync` scans recursively (DMDL:757), so two different mods with the same pak filename in different subfolders produce duplicate zip entry names. That interacts with I5. |
| 9 | 3 | I1: the single unchecked `ReadAsync` at ~3457-3462 short-reads, `SafeDeserialize` returns null, and the order is silently lost | Partly | Read at MWVM:3462-3465. SharpCompress: `ZipArchiveEntry.OpenEntryStream` → `CreateDecompressionStream` (Common/Zip/ZipFilePart.cs:67-123). Deflate → `DeflateStream.Read` → `ZlibBaseStream.Read`, which loops `while (AvailableBytesOut > 0 && !nomoreinput && rc == Z_OK)` (Compressors/Deflate/ZlibBaseStream.cs:463-640). Stored → `ReadOnlySubStream` over a local `FileStream`. None of these override `ReadAsync`, so the base `Stream.ReadAsync` runs the same `Read`. DivinityJsonUtils: src/Core/Util/DivinityJsonUtils.cs:12-19, 29-37 | The API misuse is real but **does not short-read for zip** (Deflate or Stored), which covers everything this app exports. The intermittent order loss needs another cause: AUDIT-PLAN-01 is a stronger candidate. `SafeDeserialize` has no try/catch. Its `Error` handler *logs* (`DivinityApp.Log`) and sets `Handled`, so truncated JSON tends to produce a **partially populated, non-null order** rather than null. So "nothing logged" is also wrong: it is logged, but logging is off by default. |
| 10 | 3 | I2: `success` declared once (~3395) and used at ~3428-3444 leaks across entries, so the mod vanishes without a visible error | Partly | Declared MWVM:3396, set 3433, read 3442; `LoadModDataFromPakAsync` catches and returns null (DMDL:731-743) | The leak's only effect is one futile load attempt on a truncated file that returns null. That pak's copy had already failed, so the mod was lost either way. The error *is* recorded (`taskResult.AddError` at 3437 goes to `_Logs/ImportOrderFromArchive_*_Errors.log`, MWVM:3092-3099) but no alert mentions it. What the plan misses: `File.Create` at 3429 truncates any existing same-named pak *before* the copy, and the partial file is never deleted. See AUDIT-PLAN-04. |
| 11 | 3 | I3: throwaway full-archive buffer at ~3411, and the int cast overflows above 2 GB | Confirmed (corrected) | MWVM:3412-3413; **duplicated** in `ImportCompressedFileAsync` at MWVM:3252-3253, which the plan does not mention | Above `Array.MaxLength` (about 2 GB), `new byte[fileStream.Length]` throws `OutOfMemoryException` *before* the `(int)` cast matters. Even below that, a 1-2 GB contiguous allocation can fail. Either way the whole import aborts with "Error extracting archive". The fix (delete both lines) is correct and needs applying in both places. |
| 12 | 3 | I4: 7z/rar accepted with the guard commented out (~3048, ~3066), and solid archives re-decompress per entry | Confirmed (stronger than stated) | Filter MWVM:3048 uses `_archiveFormatsStr` = `.7z .7zip .gzip .rar .tar .tar.gz .zip` (1998-2000); guard commented out at 3065-3069. 7z: `SevenZipFilePart.GetCompressedStream` re-opens the folder stream from the start and `Skip`s over all earlier files (Common/SevenZip/SevenZipFilePart.cs:39-58), O(n²) on a solid folder. RAR: SharpCompress FORMATS.md note 1 says "SOLID Rars are only supported in the RarReader API", and `RarArchiveEntry.OpenEntryStream` (Archives/Rar/RarArchiveEntry.cs:69-84) has no solid handling | For solid RAR this is likely **wrong output or an exception**, not just slow; the result is lost mods. The proposed fix is also wrong for 7z: SharpCompress has no 7z Reader API (FORMATS.md table, note 4), so `ReaderFactory` cannot open it. Use `IArchive.ExtractAllEntries()` (Archives/AbstractArchive.cs:145-150), which gives solid-aware forward extraction for 7z and RAR. |
| 13 | 3 | I5: same-named paks collide at ~3422-3424 | Confirmed | MWVM:3424-3429 | It also silently overwrites a same-named pak *already in the user's Mods folder*, with no prompt and no backup. |
| 14 | 0.3/6 | `ElementHelper.MeasureText` has its try/catch inside the per-character loop (~110-128); 58 exceptions, triggered by `=` divider entries and non-Latin titles | Partly | src/GUI/Util/ElementHelper.cs:108-129; called with *one* string per list, the longest by `.Length` (src/GUI/Views/HorizontalModLayout.xaml.cs:806-812, 846, 872-877, 910), only on `OrderJustLoaded` (515-521) | Loop placement confirmed. But `=` is mapped in every UI font, so divider entries cannot throw. The exceptions come from unmapped characters (CJK, emoji; each surrogate half counts separately because it indexes `char`s) in the single longest name. 58 first-chance exceptions cost milliseconds: negligible. The real bug: unmapped glyphs are counted as width 0, so the name column ends up too narrow, and the string chosen is the longest, not the widest. The proposed `TryGetValue` fix stops the throws but keeps the wrong width; using `FormattedText` (`MeasureTextSize`, ElementHelper.cs:83-89) is the right fix. There is an unused copy *without* try/catch in src/GUI/Controls/Behavior/GridViewAutosizeColumnsBehavior.cs:105-139. |
| 15 | 0.3/6 | `VirtualizingPanel.ScrollUnit` is set nowhere | Confirmed | grep over src `*.xaml`/`*.cs`: 0 hits | — |
| 16 | 0.3/6 | `IsVirtualizing` and `VirtualizationMode=Recycling` at HorizontalModLayout.xaml:119, 223 | Confirmed | src/GUI/Views/HorizontalModLayout.xaml:119-120, 223-224 | — |
| 17 | 6 | Landmine: `MainResourceDictionary.xaml:461` IsGrouping trigger sets `CanContentScroll=False` | Confirmed (premise overstated) | src/GUI/Themes/MainResourceDictionary.xaml:460-461. The template's `ListScrollViewer` (448) does not set `CanContentScroll` itself; `True` comes only from AdonisUI's `GridViewScrollViewerStyleKey` style (AdonisUI.ClassicTheme/DefaultStyles/ListView.xaml:6-7) | Virtualization on these lists therefore depends on a third-party theme style. In WPF ≥4.5 a grouped list *can* virtualize if this trigger is removed and `VirtualizingPanel.IsVirtualizingWhenGrouping=True` is set. The landmine is removable; the owner's "no WPF grouping" decision stands, but not because grouping is technically impossible. |
| 18 | 6 | "Never set CanContentScroll=False": is it already set anywhere? | Confirmed: not set on the mod lists | Only the inert trigger above, plus ComboBox styles (src/GUI/Themes/DefaultStyles/ComboBox.xaml:205, 314-315) | When adding `ScrollUnit="Pixel"`, also set `CanContentScroll="True"` explicitly on `ListScrollViewer`, so that a theme change cannot silently disable virtualization. |
| 19 | 5 | `Throttle(500ms)` at ~5250 and 5253 | Confirmed | MWVM:5250-5254 | — |
| 20 | 5 | `OnFilterTextChanged` (~4199) walks every mod and assigns Visibility, so one keystroke = N notifications + N container visibility changes | Partly | MWVM:4199-4313 | It walks only the *one* list passed in (`ActiveMods` or `InactiveMods`), not all 1,121. Each mod is assigned **once** per pass: there is no reset-then-rehide (the clear path 4203-4209 and the match paths 4264-4301 are separate branches). `Visibility` is `[Reactive]` (src/Core/Models/DivinityBaseModData.cs:69), so equal writes raise nothing. Notifications therefore equal the number of mods whose visibility *actually changes*. Container work is limited to realized containers, about 28 (plan §6.1). |
| 21 | 5 | Step 1: "Only assign Visibility when it actually changes … may be most of the win" | **Refuted** | ReactiveUI.Fody weaves `[Reactive]` into `RaiseAndSetIfChanged`; the `IsSelected` setter also uses `RaiseAndSetIfChanged` (src/Core/Models/DivinityModData.cs:185-195) | The comparison already happens in the setter, so this step saves one comparison per mod, i.e. nanoseconds. On a no-match search every mod genuinely changes from Visible to Collapsed, so there is nothing to skip. |
| 22 | 5 | Step 2: hoist per-keystroke allocations ("rebuilding strings for 1 121 mods") | **Refuted** for the measured path | The plain path uses `CompareInfo.IndexOf(m.Name, searchText, IgnoreCase)` (MWVM:4291) and allocates nothing per mod | Allocations occur only in the `@field` path (src/Core/Models/DivinityModFilterData.cs:12-23, where `Split` runs only with `separateWhitespace`). This cannot explain the measured ~1.2 s. |
| 23 | 5 | `ModListDropHandler` uses `dropInfo.UnfilteredInsertIndex` *because* the lists are visibility-filtered | Partly | src/GUI/ViewModels/ModListDropHandler.cs:133; column sort applies `SortDescriptions` to the default view (src/GUI/Views/HorizontalModLayout.xaml.cs:764-768) | `UnfilteredInsertIndex` exists to map a *filtered or sorted view* index back to the source index. With visibility filtering it equals `InsertIndex`. Here it matters because of column sorting. Moving to collection filtering is therefore already half-supported by gong; the invasive part is selection (`IsSelected`, the 309b81e/396d7b2 reconcile). |
| 24 | 5 | `DivinityModData.IsSelected` refuses `true` while hidden | Confirmed | src/Core/Models/DivinityModData.cs:185-195 | — |
| 25 | 0.3/5 | Why the inactive list (~186 rows) costs 3.27 s vs the active list (935 rows) 1.39 s | Explanation | Plan table (implementation-plan.txt:363-373); code above | The per-item loop cannot explain it. The six measurements are flat: 1.24-1.39 s for 186 or 935 rows, and for "few changes" (broad match), "all change" (zzzzzz) and "all revert" (cleared). Something per pass is *fixed*, or there is a measurement floor (idle-detection window). The 3.27 s outlier is the **first filter of the session**, which carries first-call costs: Debug JIT of the filter/Rx/DynamicData paths, culture/ICU collation initialisation, and first realization of containers. The UIA client used to drive the test is itself attached, and the plan records outside UIA tree walks of up to 5.3 s (§6.1). Re-measure in-process: a Stopwatch around `OnFilterTextChanged`, plus a `Dispatcher` `ContextIdle` callback for layout, with no UIA client attached and each list measured twice. |
| 26 | 4 | Regex `^(Mods\|Public)/(.+?)/.+$` matched against every file entry of every pak, "likely the dominant cost" | Partly | DMDL:458, 485-500 (skipped only for the extender config and meta files) | Confirmed per entry. It is not `RegexOptions.Compiled`, and each entry also allocates an interpolated string (504) and runs `IgnoreBuiltinPath.Any` (529). "Dominant" is unproven, and AUDIT-PLAN-06 to 09 are larger or as large. |
| 27 | 4 | MD5 computed over whole pak files | **Refuted** | No `MD5.`, `ComputeHash`, `HashAlgorithm` or `SHA*` anywhere in src. `MD5` is a meta.lsx attribute (DMDL:231, 267, 294) written back verbatim ("MD5 doesn't seem to actually be used", DMDL:1481-1482) | Kill this candidate. |
| 28 | 4 | Double enumeration of editor mods (LSLib VFS and `LoadEditorProjectsAsync`) | Confirmed (cost negligible) | `VFS.EnumerateDirectories` also lists real directories under `RootDir` (External/lslib/LSLib/LS/VFS.cs:356-377). `ModResources.DiscoverMods` therefore finds loose `Data/Mods/<x>/meta.lsx` (External/lslib/LSLib/LS/Mods/ModResources.cs:231-249), and `LoadModFromModInfo` marks it `IsEditorMod` (DMDL:1757-1761). `LoadEditorProjectsAsync(Data/Mods)` then loads it again (MWVM:1631-1637), and `MergeModLists` prefers the editor copy (MWVM:1542) | Cost scales with the number of toolkit projects, which is about 0 for this owner. Real, but kill it as a perf candidate. |
| 29 | 4 | No metadata cache | Confirmed | No mod-metadata cache in Core (only Nexus/Workshop/GitHub caches, src/Core/Models/Cache/) | A cache key of path+mtime+size is not enough. `IsForceLoaded`, `IsForceLoadedMergedMod` and `BuiltinOverrideModsText` depend on `builtinMods` from IgnoredMods.json (DMDL:526-551, 600-603, 625-640), which upstream changes after every BG3 patch. The key must include an IgnoredMods/app-version stamp. |
| 30 | 6.1 | `309b81e` conflicts on clean master because it was written on top of the TEMP logging from `789ec87` | Confirmed | `git merge-tree --write-tree --merge-base=309b81e^ master 309b81e` reports a CONFLICT (content) in HorizontalModLayout.xaml.cs. The hunks are `Util.PerfProbe.Enabled` blocks added by 789ec87 (`git show 789ec87`, PerfProbe at +44..+66) | The hand resolution is mechanical: keep the `if (e.Source is ListView sourceView)` reconcile plus the delta fallback, and drop both PerfProbe blocks. |
| 31 | 6.1 | `b123aff`, `2858a23` and `7fa8347` cherry-pick cleanly onto master with no probe references | Confirmed (order matters) | Temporary worktree: `cherry-pick b123aff 2858a23 7fa8347` succeeds; diffstat touches 3 files (CachedTextBlock.cs +368, MainResourceDictionary.xaml, ModEntryGridAutomationPeer.cs); `grep Probe\|PerfProbe\|TEMP` returns 0 hits | `7fa8347` *alone* on master gives a modify/delete conflict because CachedTextBlock.cs comes from 2858a23, so it must go after 2858a23. `396d7b2` also applies cleanly on top **without** 309b81e, but its own message says it completes 309b81e. Ship them together. |
| 32 | 6.2/6.3 | `BuildOutputList` is the modsettings.lsx export path; dividers must be stripped there (R7) | Confirmed / Partly | DMDL:1431-1468; callers MWVM:2326, 2931 | `BuildOutputList` already drops any entry whose UUID matches no loaded mod (DMDL:1447-1465, logging "[*ERROR*] Missing mod"), so a divider with a non-mod UUID is implicitly stripped there. The other consumers are not covered: the zip export (MWVM:3572), the JSON order files (`ExportLoadOrderToFileAsync`, DMDL:1098-1110), `SaveLoadOrderAs` (MWVM:2690), and older or upstream builds, which will show a divider as a *missing mod*. |
| 33 | 6.2 | Force-loaded mods are excluded unless `IsForceLoadedMergedMod` or `ForceAllowInLoadOrder` | Confirmed | MWVM:5244-5246; src/GUI/Views/HorizontalModLayout.xaml.cs:192; DMDL:600-603; auto-allow when present in modsettings at MWVM:2128 | — |
| 34 | 6.3 | The renumbering loop in `ModListDropHandler` | Confirmed | src/GUI/ViewModels/ModListDropHandler.cs:218-221 | It is O(n²): `IndexOf` per mod, about 437k comparisons at 935 rows. With dividers in the same collection, `Index` would count divider rows, so number from a separate counter. `BuildOutputList` uses sequence order, not `Index`. |
| 35 | 9 | `GithubData` is always null and `ModUpdateHandler.cs:112` NREs | Confirmed (latent) | src/Core/Models/DivinityModData.cs:238 (never assigned; 393 commented out); src/Core/ModUpdater/ModUpdateHandler.cs:108-113 | It only fires if `Github.IsEnabled` becomes true. `GithubModsCacheHandler.IsEnabled` (src/Core/ModUpdater/Cache/GithubModsCacheHandler.cs:20) is never set anywhere. |
| 36 | 9 | The "Mod Updates" view is dead UI | Confirmed | `ModUpdatesViewData.Mods` is only ever `Clear()`ed (src/GUI/ViewModels/ModUpdatesViewData.cs:67-71); nothing constructs a `DivinityModUpdateData` or adds to it anywhere in src | — |

Line-number drift: the Stage 3 numbers are consistently 1 too low (3395→3396, 3411→3412). The E2 and E4 numbers point at the wrong block: the real catch is at 3679-3683 and the spin at 3686-3690. E6 is at 3575.

## Missed defects

### [High] AUDIT-PLAN-01: Importing a "Current" order flips the live Current order to non-modsettings, or overwrites a saved order
- **Location:** MWVM:3110-3127 (`ImportOrderFromArchive`), src/Core/Extensions/ModelExtensions.cs:29-42 (`SetFrom<T>`), src/Core/Models/DivinityLoadOrder.cs:24-43
- **What:**
  - The export names the order file after `SelectedModOrder.Name` (MWVM:3582). The default selected order is "Current", so a typical export contains `Current.json`, and the import names it "Current" (MWVM:3515).
  - `DivinityLoadOrder` is `[DataContract]` with only `Order` as a `[DataMember]`. The imported object therefore has `IsModSettings=false`, `FilePath=null` and a default `LastModifiedDate`.
  - `SetFrom<T>` copies every non-null `PropertyDescriptor` value. A boxed `false` is non-null.
  - Branch A (the Current order is selected): `SelectedModOrder.SetFrom(order)` sets the live Current order's `IsModSettings` to **false** but keeps its `FilePath` = `<profile>\modsettings.lsx`.
  - Branch B (a saved order is selected): `SelectedModOrder.SetFrom(currentOrder)` turns the user's saved order into a second "Current", with `IsModSettings=true`, the modsettings path and an *aliased* `Order` list. The imported order is discarded.
- **Failure scenario:**
  - Branch A: export Current, import that zip, press Save. `SaveLoadOrderAsync` (MWVM:2612-2633) sees `!IsModSettings` and calls `ExportLoadOrderToFileAsync(FilePath)`, which **writes JSON over modsettings.lsx** (DMDL:1098-1110, `FileMode.Create`). Alternatively, export to the game: `ModOrderList.First(x => x.IsModSettings)` (MWVM:2967) throws `InvalidOperationException` because no modsettings order is left.
  - Branch B: the friend's order never appears, and the user's saved order is silently replaced.
  - Both are strong candidates for the owner's "import loses the order" complaint, and stronger than I1.
- **Evidence:** cited lines. JSON serialization honours `[DataContract]`/`[DataMember]` (Newtonsoft default).
- **Fix:**
  - Never `SetFrom` a whole `DivinityLoadOrder`. For the Current case, copy only `Order` (`SelectedModOrder.SetOrder(order)`, the API used at MWVM:2967), then `LoadModOrder`.
  - Otherwise add the imported order as a new saved order, renaming it if the name is taken.
  - Add an `[IgnoreSetFrom]` guard on `IsModSettings`/`FilePath`, or remove `SetFrom` from this path entirely.
- **Confidence:** Medium-High. The reflection semantics are certain from the code; not executed.

### [High] AUDIT-PLAN-02: Importing a .bz2/.xz/.zst pak writes an empty file over the target
- **Location:** MWVM:3272-3326 (`ImportCompressedFileAsync`), src/Core/Util/TempFile.cs:33-49
- **What:**
  - `TempFile.CreateAsync(filePath, decompressionStream, cts)` copies the whole decompression stream into the temp file (TempFile.cs:46-49).
  - Later, `decompressionStream.CopyToAsync(fs)` (3306) reads from the already-exhausted, non-seekable stream and copies 0 bytes. `File.Create` (3302) has already truncated the target. `success=true` and the mod is added to the list.
- **Failure scenario:** the user drops `SomeMod.pak.zst`. The UI shows the mod, but the Mods folder holds a 0-byte pak. On the next refresh the mod vanishes. If a pak named `<Folder>.pak` already existed (3296-3301), the user's working copy is destroyed.
- **Evidence:** cited lines.
- **Fix:** copy from `tempFile.Stream` after `Seek(0)`, or move the temp file into place. Write to `<target>.tmp` and `File.Move(..., overwrite: true)` only after the copy has verified.
- **Confidence:** High.

### [High] AUDIT-PLAN-03: Export cancellation races an abandoned writer and leaves a corrupt zip
- **Location:** MWVM:3577-3650, 3663-3694
- **What:**
  - On cancel, `await Task.Delay(0, token)` throws, so the outer `Task.Run` task is cancelled and the `await` at 3599/3623 throws.
  - The `using` blocks then run `zipWriter.Dispose()`, which writes the central directory to the same `FileStream` (ZipWriter.cs:50-61), *while the `StartNew` child task (3673) is still inside `writer.Write`*. Then the stream is disposed. The child's resulting exception is swallowed at 3679; that is exactly what the comment there describes.
- **Failure scenario:**
  - Cancel during a large pak. The output has a central directory interleaved with entry data, i.e. an invalid zip, left at the user's chosen path.
  - With Export As overwrite, the previous good archive is already gone because the target was opened directly.
  - The Danger alert prints a `TaskCanceledException` stack trace.
- **Evidence:** cited lines.
- **Fix:**
  - Remove the child task. Call `writer.Write(entry, stream)` directly; the export already runs on the task pool (MWVM:3538).
  - For responsive cancel, wrap the source `FileStream` in a stream whose `Read` throws on `token.IsCancellationRequested`.
  - Treat cancellation as abort: dispose the writer, delete `<out>.tmp`, then delete the temp dir recursively in `finally`.
- **Confidence:** High.

### [Medium] AUDIT-PLAN-04: Archive import clobbers existing paks and leaves truncated paks behind
- **Location:** MWVM:3424-3440
- **What:** `File.Create(outputFilePath)` opens the final destination in the user's Mods folder before copying. If the copy fails (corrupt entry, cancellation, disk full), the truncated file stays there. Any same-named pak that was there before is already gone. There is no prompt, no backup and no cleanup.
- **Failure scenario:**
  - Import a friend's zip with an older or broken `X.pak` while you have a working `X.pak`: yours is lost.
  - Cancel mid-import: a half pak remains and loads as "corrupt" on the next refresh.
- **Evidence:** cited lines.
- **Fix:** extract to `<name>.pak.tmp` in the Mods folder, validate with `LoadModDataFromPakAsync`, then `File.Move(tmp, final, overwrite: true)`. On failure or cancel, delete the tmp. If `final` exists, decide explicitly whether to replace it, and surface that in the result.
- **Confidence:** High.

### [Medium] AUDIT-PLAN-05: The E2 "collect failures and continue" fix would still ship truncated paks
- **Location:** MWVM:3677 → SharpCompress Writers/Zip/ZipWriter.cs:83-86 and the `ZipWritingStream.Dispose` path (around 405-470)
- **What:** an I/O error *during* `source.TransferTo(output)` still disposes `output`, which finalizes the entry with the partial byte count and the partial CRC. The archive is self-consistent, but the pak inside it is truncated.
- **Failure scenario:** a read error on a large pak mid-export (network drive, AV lock taken mid-read). The zip validates in 7-Zip, the receiver imports a broken pak, and the mod is lost on their side. This matches "import didn't get all my mods" and is invisible to both users.
- **Evidence:** cited SharpCompress lines.
- **Fix:** open each source *before* writing its entry. That already happens for open/locked/missing failures. Treat any exception after the entry has started as fatal for the whole export (abort, delete tmp). Optionally verify each entry's CRC/size against the source length after writing.
- **Confidence:** Medium-High.

### [Medium] AUDIT-PLAN-06: Pak loading is effectively serial despite Task.WhenAll (Stage 4 candidate)
- **Location:** DMDL:777-784, 689-711, 466-560
- **What:**
  - `tasks.Add(LoadModDataFromPakToBagAsync(...))` runs each call synchronously up to its first *genuinely* asynchronous await.
  - Everything before that is synchronous: `PackageReader.Read` (memory-mapped open plus file-table decompress, External/lslib/LSLib/LS/PackageReader.cs:274-287), and the entire per-entry loop including the regex.
  - The streams awaited later are mostly synchronous too: memory-mapped views, `System.IO.Compression.ZLibStream` over a view (External/lslib/LSLib/LS/Compression.cs:129-157), whose `ReadAsync` completes synchronously.
  - Only LZ4 entries hop to the pool, because `LZ4DecompressionStream` (Compression.cs:8-64) has no `ReadAsync` override and the base `BeginRead` runs on a pool thread. Even then, the hop happens only at the first goal or meta read.
- **Failure scenario:** the 9.27 s "mods finished loading" step uses about one core for most of its duration.
- **Evidence:** cited lines. The plan's own 24.2 s CPU over 15.2 s wall shows *some* parallelism (Rx, UI, GC), but not 1,097-way.
- **Fix:** measure first with a Stopwatch around `LoadModPackageDataAsync`. Then either `Parallel.ForEachAsync(modPaks, new ParallelOptions{MaxDegreeOfParallelism = Environment.ProcessorCount}, ...)`, or wrap each call in `Task.Run`. Check thread safety first: `_AllPaksNames` (DMDL:429) is a static `HashSet` written during enumeration and read by `PakIsNotPartial`; that is fine today because it is single-threaded during enumeration. `DivinityApp.Log` must also be thread-safe.
- **Confidence:** Medium (reasoned from source, not profiled).

### [Medium] AUDIT-PLAN-07: `DivinityModData.Files` retains every path of every pak and is never read (memory)
- **Location:** DMDL:478, 488, 628; src/Core/Models/DivinityModData.cs:234
- **What:** each loaded mod keeps a `HashSet<string>` of all its file names. No code reads `DivinityModData.Files`: a grep for `.Files` outside LSLib package objects finds only unrelated `DeleteFilesViewData.Files` and `PackageBuildData.Files`.
- **Failure scenario:** with 1,097 paks, some of them texture/asset packs with tens of thousands of entries, this pins every path string plus hash-set overhead. It is a plausible double-digit-MB or larger slice of the 478 MB private bytes, and the strings also keep LSLib's path strings alive past `using var pak`.
- **Evidence:** cited lines.
- **Fix:** stop collecting it (delete 478, 488, 628 and the property), or keep only a count. Measure private bytes before and after.
- **Confidence:** High that it is unused; Medium on its share of memory.

### [Medium] AUDIT-PLAN-08: The builtin-mod load builds a full VFS over the Data paks to find about 30 base mods (Stage 4 candidate)
- **Location:** DMDL:1783-1820; External/lslib/LSLib/LS/VFS.cs:173-215 (`AttachGameDirectory`, `AttachPackage`, `FinishBuild`/`TryAddFile`)
- **What:**
  - `AttachGameDirectory` reads the file table of every non-blacklisted Data and Localization pak. `Game.pak`, `Gustav.pak`, `Shared.pak` and the language paks are not in `PackageBlacklistBG3` (VFS.cs:93-113).
  - `FinishBuild` then inserts every entry into a directory tree, with substring allocations per path segment. Only `Mods/*/meta.lsx` is needed.
- **Failure scenario:** part of the 9.27 s step and a large transient allocation spike. It also runs under a 30 s cancel timeout (MWVM:1626), and a timeout there silently falls back to the IgnoredMods list.
- **Evidence:** cited lines.
- **Fix:** time it separately in Stage 4.1. If significant, read each pak with `metadataOnly`/file-table only and probe for `Mods/<x>/meta.lsx` directly, without `FinishBuild`. Upstreamable.
- **Confidence:** Medium (cost not measured).

### [Low] AUDIT-PLAN-09: Every Osiris goal file of every mod is decompressed and fully read on each startup
- **Location:** DMDL:504-523
- **What:** for each `Mods/<x>/Story/RawFiles/Goals/*` entry it calls `CreateContentReader()` and `ReadToEndAsync()` to search for `NRD_KillStory`/`NRD_BadCall`, with no caching and no early exit once `MODFIXER` is determined.
- **Failure scenario:** script-heavy mods with hundreds of goal files add decompression and string allocation to every launch.
- **Evidence:** cited lines.
- **Fix:** break out of the goal scan once `hasOsirisScripts == MODFIXER`. Put the result in the Stage 4 metadata cache. Measure the count of goal files across the owner's set.
- **Confidence:** Medium.

### [Low] AUDIT-PLAN-10: Every non-order .json in an archive becomes a bogus empty load order
- **Location:** MWVM:3457-3469, 3505-3519
- **What:** every `*.json` entry is fed to `SafeDeserialize<DivinityLoadOrder>`. With the handled-error settings, unrelated JSON such as a Nexus `info.json` deserializes to a non-null order with an empty `Order`, which is then added through `AddNewModOrder` (3130). An order JSON whose `"Order": null` gives a null `Order`, and `order.Order.Select` at 3516 throws on the UI thread with no try/catch.
- **Failure scenario:** importing a mod zip through "Import Order & Mods" adds an empty order named "info".
- **Evidence:** cited lines.
- **Fix:** strict deserialization in a try/catch; accept an order only when `Order` is non-null and contains at least one entry with a GUID-shaped UUID; report rejects.
- **Confidence:** Medium.

### [Low] AUDIT-PLAN-11: The import success alert ignores recorded errors
- **Location:** MWVM:3092-3153
- **What:** errors go only to `_Logs/ImportOrderFromArchive_*_Errors.log`. The alert says "Imported N mod(s)" and never compares `TotalPaks` with `Mods.Count`.
- **Failure scenario:** 3 of 40 paks fail; the user sees a green success message.
- **Evidence:** cited lines.
- **Fix:** if `Errors.Count > 0` or `Mods.Count < TotalPaks`, show a Warning naming the failed entries (R8).
- **Confidence:** High.

### [Low] AUDIT-PLAN-12: modsettings.lsx is built by quadratic string concatenation
- **Location:** DMDL:1474-1487 (`GenerateModSettingsFile`)
- **What:** `modShortDescText += String.Format(...)` runs for every mod. For 935 mods and a 548 KB output, that is roughly n²/2 character copies, a few hundred MB of memory traffic.
- **Failure scenario:** tens to hundreds of ms on every export to the game.
- **Evidence:** cited lines.
- **Fix:** use a `StringBuilder`. A trivial upstreamable perf/* change.
- **Confidence:** High on the pattern; cost estimated.

### [Info] AUDIT-PLAN-13: `.tar.gz` in `_archiveFormats` can never match
- **Location:** MWVM:1837, 1869, 1998
- **What:** `Path.GetExtension("x.tar.gz")` returns `.gz`, which is in neither list, so drag-dropped `.tar.gz` files are ignored. Also `.gzip` is not a real extension.
- **Fix:** match on `EndsWith` or a two-part extension.
- **Confidence:** High.

## Fix-proposal review

- **E1: `File.Create`, or temp beside the target then `File.Move(tmp, out, overwrite: true)`.**
  - Correct. `File.Move(string, string, bool)` exists from .NET Core 3.0 and works on .NET 8. On Windows it replaces the destination with a same-volume rename, which is atomic enough here; it fails with `IOException` if the destination is open (Explorer preview, 7-Zip), so surface that.
  - Dispose the writer and stream *before* the move.
  - Temp-then-move alone is **not** enough on cancellation. With the current `WriteZipAsync` design the abandoned child is still writing to the tmp when you would move or delete it (AUDIT-PLAN-03). Remove the child task first.
- **E2: catch only `OperationCanceledException` and collect failures into a result.**
  - With the child task removed, cancellation no longer surfaces as `ObjectDisposedException`, so catching OCE is right.
  - Distinguish failures to open a source (skip and report; the zip stays valid) from failures after the entry has started (abort; the zip would carry a truncated pak). See AUDIT-PLAN-05.
  - `ShowAlert` already logs (MWVM:4317-4320), but logging is off by default, so the alert text must name the mods.
- **E4: `await Task.Run(() => writer.Write(entryName, source), token)`.**
  - Works, but the token only prevents *starting*. It is also unnecessary: the export already runs on the task pool (MWVM:3538).
  - Call `writer.Write` directly with a cancellation-checking source stream. SharpCompress 0.39 has no async writer API (Writers/IWriterExtensions.cs).
- **E5: `Directory.Delete(tempDir, true)` in `finally`.** Correct. Also wrap it in a try/catch so cleanup cannot mask the real error or skip `OnMainProgressComplete`.
- **I1: `ReadExactlyAsync(result, cts)`, or a MemoryStream, then "make it loud".**
  - `Stream.ReadExactlyAsync(Memory<byte>, CancellationToken)` exists from .NET 7, so it is available on .NET 8 and the `byte[]` converts implicitly.
  - It still trusts `file.Size` and the `(int)` cast. Prefer `using var sr = new StreamReader(entryStream); text = await sr.ReadToEndAsync(ct);` (handles the BOM, no size dependence).
  - "Loud" means not using `SafeDeserialize`, whose handler turns errors into partial objects. Use `JsonConvert.DeserializeObject` in a try/catch and validate the result (AUDIT-PLAN-10).
  - This fix is cheap but **will not fix the reported symptom**; fix AUDIT-PLAN-01 first.
- **I2: per-entry local flag.** Correct but cosmetic. The substantive fix is extract-to-tmp, validate, then move, with cleanup (AUDIT-PLAN-04).
- **I3: delete the two lines.** Correct; also delete MWVM:3252-3253.
- **I4: `ReaderFactory` forward-only for non-zip.** **Wrong for 7z**, which has no Reader API in SharpCompress. Use `archive.ExtractAllEntries()` (works for solid 7z and solid RAR) and drive entries through `reader.MoveToNextEntry()`/`OpenEntryStream()`. Or extract once to a temp dir. Measure on a real solid 7z as the plan says.
- **MeasureText: `CharacterToGlyphMap.TryGetValue`.** Stops the exceptions (the map is an `IDictionary<int, ushort>`) but keeps measuring unmapped glyphs as zero width. Use `FormattedText`, which already exists as `MeasureTextSize`. That resolves fallback fonts and surrogate pairs.
- **Stage 5 step 1 (compare before writing Visibility).** No-op; drop it (claim 21).
- **Stage 5 step 2 (hoist lowercase keys).** No-op for the measured path (claim 22). Keep it only as a precondition for fuzzy matching.
- **Stage 5 throttle retune.** Sound once the per-pass cost is understood. First establish *where* the flat ~1.2 s goes (claim 25): an in-process Stopwatch around `OnFilterTextChanged` versus the following layout pass. Likely suspects are layout/realization of collapsed containers in the VirtualizingStackPanel, and UIA events while a client is attached. Without that, Stage 5 has no target.
- **Stage 5 step 5 (DynamicData filtering).** Less invasive than the plan says for drag-drop, since `UnfilteredInsertIndex` already handles a filtered or sorted view (claim 23). Selection remains the hard part.
- **Stage 4 metadata cache.** A good idea, but key it on path+size+mtime **plus** an IgnoredMods/builtin-list hash and the app version (claim 29). Drop `Files` from the cached shape (AUDIT-PLAN-07).
- **Stage 6 `ScrollUnit="Pixel"`.** Safe with Recycling. Pin `CanContentScroll="True"` on `ListScrollViewer` at the same time (claim 18), and re-check `CacheLength`, because pixel scrolling realizes partial rows.
- **Stage 6.3 dividers.** `BuildOutputList` already strips non-resolving UUIDs, but give dividers a non-GUID UUID scheme *and* skip them explicitly in the zip export (MWVM:3572), the JSON order writer, `SaveLoadOrderAs` (MWVM:2690) and the `Index` renumbering (claims 32 and 34). Older or upstream builds will list them as missing mods; accept that or document it.
- **Stage 8 dedupe by UUID.** Trivially true on the export side (claim 8). Spend the design effort on *import-side* version conflicts and on unique zip entry names (UUID-prefixed, or a manifest).

## Owner intentions gleaned (as they bear on this audit)

These are what the relayed request asked to have listed, read from CLAUDE.md, the plan and the status board.

- Fork LaughingLeader's BG3MM for themselves and friends, but publish it. Stay on WPF master; Windows now, keep Linux viable; Avalonia only as a possible future port.
- Upstream every bugfix and perf change as small, clean `fix/*`, `perf/*` and `chore/*` branches cut from master. Keep fork-only features on `fork/main`. Never build an upstreamable fix on fork-only work.
- First fix the two reported data-loss bugs (zip export corruption, archive import losing mods or order), then speed up startup, filtering and scrolling for a 1,100-mod, 935-active setup. Measure first, and keep Debug baselines as controls.
- Then: a UI refresh within AdonisUI; categories/dividers as flat rows (never WPF grouping); overrides folded into the order; multi-order export.
- Eventually, each specced first: updater/downloader (Nexus, mod.io), order syncing with friends, native-mod management.
- Treat the Redux fork as ideas only, with no code reuse. Never configure `merge.ours`. Merge, never rebase, when syncing upstream. Every failure must surface in the UI, because logging is off by default.
