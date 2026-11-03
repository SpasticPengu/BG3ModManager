# CLAIMS-3: Verified results

This is a read-only review of the SpasticPengu fork of BG3 Mod Manager. Each item is a claim that an earlier audit or design made about the code. "Confirmed" means the claim holds. "Refuted" means the claim is wrong.

## Summary

| Status | Count |
|---|---|
| Confirmed | 3 |
| Plausible | 1 |
| Disputed | 0 |
| Refuted | 1 |
| Unverified | 0 |
| **Total** | **5** |

| Final severity | Count |
|---|---|
| High | 2 |
| Medium | 3 |

## Entries

### CLAIM-17 [High] Toolchain facts for .NET 10 and CI (Plausible)
Location: docs/review/areas/BLD.md BLD-01 and BLD-02; docs/review/stages/S1.md; Microsoft .NET support policy and VS/MSBuild requirements; GitHub actions/runner-images docs.

The claim is partly true.
- (a) Confirmed: targeting net10.0 needs VS 18.0+, and SDK 10.0.400 lists VS 18.0 as its minimum (learn.microsoft.com versioning-sdk-msbuild-vs).
- (d) Confirmed: .NET 8 support ends on 10 Nov 2026.
- (c) The image facts are confirmed. windows-latest is the Server 2025/VS 2026 image. It has VC.14.44 and the generic CLI.Support component, but no VC.14.44.17.14.CLI.Support (Windows2025-VS2026-Readme.md:220,339,347). That a v143 C++/CLI build fails on this image is only inferred.
- (b) Unconfirmed: no Microsoft source says that net10 C++/CLI needs the v145 toolset. BLD itself labels this inferred (docs/review/areas/BLD.md:50,385).

Fix caveats: settle (b) and (c) with a test CI build on windows-latest, run once with and once without -p:PlatformToolset=v145. Pinning windows-2022 for .NET 8 CI is a precaution, and the net10 branch needs a VS 18 image. The 18.0 floor applies to MSBuild loaded through VS. A vcxproj still needs VS MSBuild even when `dotnet build` is used.

docRef: BLD-01, BLD-02, S1

### CLAIM-14 [High] Drops into a filtered list land at the wrong load-order position (Refuted)
Location: src/GUI/ViewModels/ModListDropHandler.cs; gong-wpf-dragdrop 4.0.0 DropInfo (UnfilteredInsertIndex) in scratchpad dl-gong/DropInfo.cs.

Why it is refuted: filtering only sets each mod's Visibility (MainResourceDictionary.xaml:369; MainWindowViewModel.cs:4199-4290). ItemsSource is ActiveMods/InactiveMods with no view filter, so Items.Count equals the source count. Because the counts match, gong's correction branch never runs (dl-gong/DropInfo.cs:65-66), and UnfilteredInsertIndex returns InsertIndex. InsertIndex comes from IndexFromContainer over the full Items list, so it is already the model index (:269, :370-372). Collapsed rows fail hit tests and the IsVisible check (dl-gong-ext/ItemsControlExtensions.cs:152).

Corrected statement: a drop beside a visible row goes directly before or after that row. If nothing is visible, the drop is appended to the end of the list, not the top (ModListDropHandler.cs:133,190-198). Both verifiers rated it Low.

Fix caveats: a related risk is real. A column-header sort adds SortDescriptions to the default view (HorizontalModLayout.xaml.cs:764), so IndexFromContainer returns a sorted-view index that is then applied to the unsorted model. Computing indices from the model is still worth doing for that case. The plan's line 736 is wrong to call UnfilteredInsertIndex load-bearing.

docRef: S5-final

### CLAIM-13 [Medium] The repo stores .cs files with LF line endings, contrary to CLAUDE.md's CRLF rule (Confirmed)
Location: `git ls-files --eol` over src/**/*.cs; .gitattributes; .editorconfig end_of_line.

The claim holds. Of 185 tracked src .cs files, 168 are LF in the index, 6 are CRLF (e.g. src/GUI/AppCommands.cs) and 11 are mixed (src/GUI/Program.cs). wt-perf shows 176/6/11. .gitattributes has only merge=ours lines, with no text or eol attributes. However, .editorconfig:15,251 and notes/CLAUDE.md:436 both say CRLF, so a tool that follows them would rewrite whole LF files. Correction: some files do store CRLF in the index. Master equals upstream, so this layout comes from upstream.

Fix caveats: change CLAUDE.md, and ideally .editorconfig, to say "keep each file's existing endings". Avoid a `* text=auto` renormalisation commit, because master tracks upstream and every later upstream merge would conflict.

docRef: BLD-07; CLAUDE.md section 5

### CLAIM-15 [Medium] Master can write an override pak's file-path UUID into modsettings.lsx (Confirmed; was High)
Location: src/GUI/ViewModels/MainWindowViewModel.cs LoadModOrder ~L2093-2160, AddActiveMod ~L4841; src/Core/Util/DivinityModDataLoader.cs GenerateModSettingsFile/BuildOutputList ~L1431-1490, LoadModSettingsFileAsync ~L831-878; DivinityModData.HasInvalidUUID.

The claim holds, but only after a specific user action. An override pak without metadata gets UUID=pakPath (DivinityModDataLoader.cs:619-639). The UI toggle, drag, and txt/tsv import paths are all blocked (DivinityModData.cs:381-384,419-421). The trigger:
1. The JSON text export includes ForceLoadedMods (DivinityModDataLoader.cs:1222-1239).
2. Re-importing that JSON gives LoadModOrder an entry it finds, which sets ForceAllowInLoadOrder (MainWindowViewModel.cs:2117-2142,3802-3809).
3. Saving writes the path unescaped into a type="guid" attribute (DivinityModDataLoader.cs:1471-1483).
4. On read-back, Guid parsing throws (lslib NodeAttribute.cs). The error is swallowed (:838-846), the order comes back empty, and the reset alarm fires.

A path that contains '&' breaks LoadXml, so nothing is written at all.

Fix caveats: add a write guard that rejects non-GUID UUIDs. Escape UUID/Folder in the XML. Filter out override paks (IsForceLoaded && !IsForceLoadedMergedMod) in the JSON export/import and in LoadModOrder. The Last Exported backup keeps re-applying a bad order until that is done.

docRef: S6-final; HARV-52

### CLAIM-16 [Medium] A failed update check reports 'up to date', and cancelling or failing an update download closes the app (Confirmed; was High)
Location: src/GUI/ViewModels/AppUpdateWindowViewModel.cs ~L39-43, ~L54-87; Autoupdater.NET 1.9.2 (scratchpad dl/au).

Both halves hold.
- Failed check: AutoUpdater raises CheckForUpdateEvent with only Error set, so IsUpdateAvailable is false (dl/au/AutoUpdater.cs:548-552). The handler never reads args.Error. WebHelper swallows changelog fetch errors (src/Core/Util/WebHelper.cs:34-43). The handler therefore reaches the "up-to-date" branch (AppUpdateWindowViewModel.cs:58,70). The message is visible only on a manual check.
- Cancelled or failed download: DownloadUpdate returns false via DialogResult.Cancel (DownloadUpdateDialog.cs:268,333-334). Environment.Exit(0) then runs unconditionally (AppUpdateWindowViewModel.cs:80-86).

Severity is Medium because only the update flow is affected.

Fix caveats: check args.Error first and show a "check failed" message. Call Exit only when DownloadUpdate returns true, and otherwise hide the window. Environment.Exit skips the close handlers, so an unsaved load order can be lost. The WebException branch (L102-105) is effectively unreachable for check failures.

docRef: HARV-01
