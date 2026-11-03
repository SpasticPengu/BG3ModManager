# HARV-H1 verification results

| Status | Count |
|---|---|
| Confirmed | 3 |
| Plausible | 0 |
| Disputed | 0 |
| Refuted | 0 |
| Unverified | 0 |

| Final severity | Count |
|---|---|
| High | 2 |
| Medium | 1 |

### HARV-27 [High] No build can import an archive of 2 GiB or more, and every import reads the whole file into memory (Confirmed, KNOWN)
Location: src/GUI/ViewModels/MainWindowViewModel.cs:3412 (ImportArchiveAsync), :3252 (ImportCompressedFileAsync)
All three verifiers confirmed this with high confidence. Both importers allocate a byte array the size of the whole file, read into it, throw it away and reset Position to 0, with no size check first (MWVM:3252-3253, 3412-3415; callers MWVM:1871, 1875, 3082). The build is x64 (PlatformTarget), so above Array.MaxLength (just under 2 GiB) `new byte[long]` throws OutOfMemoryException before the (int) cast matters. The catch at 3360/3486-3496 shows "Error extracting archive". The same lines are on wt-perf, and upstream avalonia still pre-reads (ImportUtils.cs:85-87, ModImportService.cs:259-261, 308-310).
Corrected claim: the failure happens at the allocation, not at the cast overflow. Export covers only the active order, not the full library. The "27 GB" figure does not appear in the owner notes.
Fix caveats: delete both the ReadAsync line and the Position reset, and keep IncreaseMainProgressValue. ArchiveFactory.Open and the decompressor streams read the FileStream themselves. Fix the same throwaway read in DivinityModDataLoader.cs:969 (LoadResourceAsync) in the same commit, then confirm with a 2 GiB zip import on Windows.
docRef: plan Stage 3 I3; AUDIT-PLAN-11

### HARV-31 [High] Importing an archive order named "Current" corrupts the selected order or Current (Confirmed, KNOWN)
Location: src/GUI/ViewModels/MainWindowViewModel.cs:3112-3126; src/Core/Extensions/ModelExtensions.cs:29-42
All three verifiers confirmed this with high confidence. SetFrom copies every non-null property, and boxed bool and DateTime values are never null (ModelExtensions.cs:29-41). Only Order is a [DataMember] (DivinityLoadOrder.cs:24-44), and the import never calls AddNewModOrder (MWVM:3505-3516). Exporting while Current is selected writes Current.json (MWVM:3582), so a user who imports their own export reaches this path. Case (b) is as stated: the selected order becomes a second Current with an aliased Order, and the import is discarded.
Case (a) is worse than the finding says. The imported FilePath is null, so Current keeps its modsettings.lsx path but gets IsModSettings=false. The next save then writes JSON into modsettings.lsx (MWVM:2613-2634; DivinityModDataLoader.cs:1098-1111), and Export to Game throws at First(IsModSettings) (MWVM:2967). This lasts until a refresh or restart.
Fix caveats: never use SetFrom here. Call currentOrder.SetOrder(order) to copy the entries, or add the import as a new order named "Imported Current" after checking for a name clash. Guard against a null SelectedModOrder, and add a test that Name, FilePath and IsModSettings stay unchanged. The upstream avalonia branch (ModImportService.cs:213-227) has the same bug.
docRef: AUDIT-PLAN-01

### HARV-01 [Medium] Cancelling or failing the app-update download closes the app (Confirmed, NEW; originally High)
Location: src/GUI/ViewModels/AppUpdateWindowViewModel.cs:39-43 (the finding's path was missing src/)
All three verifiers confirmed this with high confidence. TryRunUpdate calls Environment.Exit(0) no matter what DownloadUpdate returns. AutoUpdater.NET 1.9.2 (GUI.csproj:50) returns false when the user cancels, when the download or checksum fails, and when the user declines UAC (AutoUpdater.cs:692-706; DownloadUpdateDialog.cs:248-270, 320-334). Environment.Exit also skips the window-closing handlers, so unsaved order edits are lost.
All three downgraded it to Medium. It needs a rare, user-initiated path, and it is latent today: the fork's version (1.0.12.9, GUI.csproj:16) equals the feed (Update.xml:3), so Confirm stays disabled (AppUpdateWindowViewModel.cs:74/83) until a newer feed exists, either upstream 1.0.13 or the fork after the HARV-03 repoint.
Fix caveats: call Shutdown() and Exit(0) only in the true branch, because ZipExtractor waits for the process to exit. In the else branch, set IsVisible=false and show a Warning alert; this is safe because ConfirmCommand runs on the main thread. Save the order, or prompt for it, before DownloadUpdate, because the success path also exits without saving.
docRef: CLAUDE.md section 8.1 (self-update hazard, area only)
