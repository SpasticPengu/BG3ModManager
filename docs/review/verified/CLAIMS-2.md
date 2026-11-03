# CLAIMS-2 verification results

These are the results of a read-only review of the SpasticPengu fork of BG3 Mod Manager. Each item is a claim that an earlier audit or design made about the code. "Confirmed" means the claim holds and "Refuted" means it is wrong.

## Summary

| Status | Count |
|---|---|
| Confirmed | 6 |
| Plausible | 0 |
| Disputed | 0 |
| Refuted | 0 |
| Unverified | 0 |
| **Total** | **6** |

| Final severity | Count |
|---|---|
| High | 3 |
| Medium | 0 |
| Low | 3 |

Two severities changed. CLAIM-08 dropped from Medium to Low, and CLAIM-12 dropped from Medium to Low.

---

### CLAIM-07 [High] All mod loading runs one pak at a time on the UI thread (Confirmed)
Location: src/GUI/ViewModels/MainWindowViewModel.cs OnViewActivated ~L4105-4166, RefreshAsync ~L2340-2558, LoadModsAsync ~L1609-1720; src/Core/Util/DivinityModDataLoader.cs LoadModPackageDataAsync ~L745-810 and the per-pak loaders ~L466-745
RefreshAsync starts on the thread pool through TaskpoolScheduler.ScheduleAsync (MainWindowViewModel.cs:4143). Its first await is on Observable.Start(..., MainThreadScheduler), reached through SetMainProgressTextAsync (:2232-2238, :2365-2373) or LoadSettings. Rx's AsyncSubject AwaitObserver captured no context, so it runs the continuation inline on the dispatcher (System.Reactive 6.0.1 AsyncSubject.cs:369-401). From then on, LoadModsAsync (:1624) and LoadModPackageDataAsync run on the UI thread.
Each per-pak task runs a synchronous pr.Read (DivinityModDataLoader.cs:691-693; PackageReader.cs:274) before its first await, so the foreach at :779-784 parses paks one after another. Task.WhenAll adds no parallelism. The CLAUDE.md "thread pool" statement is stale after the first await.
Fix caveats: ConfigureAwait(false) alone does not fix this, because the Rx awaiter causes the hop and pr.Read is synchronous. Offload the pak loop explicitly with Task.Run or Parallel.ForEachAsync and bounded parallelism, and keep LoadModsAsync off the dispatcher. A Windows run that logs thread IDs would confirm the fix.
docRef: S4-final

### CLAIM-09 [High] Per-entry fields on DivinityLoadOrderEntry would be lost because Order is rebuilt from ActiveMods (Confirmed)
Location: src/GUI/ViewModels/MainWindowViewModel.cs UpdateOrderFromActiveMods ~L4880-4888, constructor subscription ~L5401-5408, ExportLoadOrderAsync ~L2901
UpdateOrderFromActiveMods clears Order and calls AddRange(ActiveMods, true) (MainWindowViewModel.cs:4880-4888). Each new entry comes from ToOrderEntry, which copies only UUID and Name (DivinityModData.cs:299-306). Every ActiveMods CollectionChanged event, Move included, schedules this rebuild 250 ms later (:5401-5408). Export (:2901), SaveLoadOrder and other paths (:2602, :2653, :3700, :3775) call it too. Entry.Clone copies only UUID, Name and Missing (DivinityLoadOrder.cs:44-56). wt-perf has the same code.
Corrected statement: any new per-entry field, such as a pin, would be wiped. Saying pins "must" go elsewhere overstates it, because the rebuild could carry fields over by UUID.
Fix caveats: Prefer a top-level Pins map keyed by UUID, or else carry the fields over in UpdateOrderFromActiveMods, ToOrderEntry and Entry.Clone. With a map, DivinityLoadOrder.Clone() and the "New" order at ~L2056 must copy it.
docRef: LIB-final

### CLAIM-10 [High] Settings.Window and Settings.Confirmations are never restored on load (Confirmed)
Location: src/Core/Models/DivinityModManagerSettings.cs ~L112, ~L122; SetFrom<T, ReactiveAttribute> (src/Core/Extensions); MainWindowViewModel.cs LoadSettings ~L1112-1222; src/GUI/Views/MainWindow.xaml.cs
Window and Confirmations carry [DataMember] but not [Reactive] (DivinityModManagerSettings.cs:112,122). SetFrom<T,ReactiveAttribute> copies only properties marked [Reactive] (ModelExtensions.cs:44-56), and nothing else restores these two (MainWindowViewModel.cs:1127-1129). ApplyWindowPosition therefore receives -1 values and does nothing (MainWindow.xaml.cs:205-235).
Worse, OnViewActivated calls SaveSettings straight after LoadSettings (:4126-4133, :4150), which writes the defaults over the saved values. Commit 7b57e5a (Aug 2025) introduced the regression when it replaced the all-properties Settings.SetFrom(settings). Upstream 73c9d42 came earlier and does not show that restoring works. One verifier rated this Medium because the loss is UX only: opt-in window placement and the admin-warning opt-out.
Fix caveats: After L1127, copy the Window fields (Maximized, Screen, X, Y, Width, Height) into the existing Settings.Window, which is a POCO so SetFrom does not apply. Call Settings.Confirmations.SetFrom(settings.Confirmations), with null guards. Never reassign either object. Add a note to CLAUDE.md that SetFrom skips sub-objects that lack [Reactive].
docRef: AUDIT-CLAUDE gap

### CLAIM-08 [Low] The screen-reader probe runs about four times per mod during load and reloads Tolk each time when no reader runs (Confirmed, originally Medium)
Location: src/Core/AppServices/ScreenReaderService.cs; External/CrossSpeak; callers in Core models and loaders
The count holds only partly. The HasToolTip WhenAnyValue in DivinityModData (DivinityModData.cs:521-524) probes on its initial value, again when the UUID and Description initialisers run (DivinityModDataLoader.cs:222-236), and once more when HasDependencies changes. That is 3-4 calls per mod.
Corrected statement: Tolk is not reloaded. With no reader, CrossSpeak's isLoaded stays false, so Initialize runs every time (ScreenReaderWindows.cs:23-41; ScreenReaderService.cs:22-28,51-77). Native Tolk_Load returns early once it has loaded (Tolk.cpp:31-46,88-108), and each call costs a CoInitializeEx plus about five cheap driver probes (ScreenReaderDriverNVDA.cpp:54-57). The total is likely milliseconds to tens of milliseconds.
Fix caveats: Cache the active state in ScreenReaderService, set at startup or refreshed on a timer or event, and stop calling Initialize again when no reader was found. The cost should be measured on Windows before this is prioritised.
docRef: S4-final

### CLAIM-11 [Low] DebugModeEnabled does not turn logging on (Confirmed)
Location: src/GUI/Views/MainWindow.xaml.cs ~L73, ~L380-443; DivinityModManagerSettings DebugModeEnabled and LogEnabled
ToggleLogging checks only its argument and ViewModel.DebugMode (MainWindow.xaml.cs:71-94). DebugMode is a plain bool that is set only when a listener already exists, which happens only with a 'debug' file next to the exe (:380-383, :440-443). Settings.DebugModeEnabled ("Mod Developer Mode") feeds only DivinityApp.DeveloperModeEnabled, the dependency filter and visibility bindings (MainWindowViewModel.cs:325, 861, 4949; DivinityModManagerSettings.cs:105-107, 215). Logging is turned on by LogEnabled (MainWindowViewModel.cs:1010, 1154-1157), the debug file, and the error handlers. wt-perf behaves the same way.
Fix caveats: Correct CLAUDE.md §5 (notes/CLAUDE.md:486). Logging is turned on by LogEnabled, which is hidden from the UI, or by the debug file, which also sets DebugMode and adds progress logging. Error handlers force logging on temporarily, and DebugModeEnabled does not turn it on.
docRef: CLAUDE.md section 5

### CLAIM-12 [Low] New Larian modules only need adding to IgnoredMods.json's Mods array, not IgnoreDependencies (Confirmed, originally Medium)
Location: src/Core/Util/DivinityModDataLoader.cs ~L40-56; src/GUI/Resources/IgnoredMods.json; dependency checks in DivinityModData and the missing-dependency UI
IgnoreModDependency returns true when a UUID is in IgnoredDependencyMods or IgnoreMod finds it in IgnoredMods, the set filled from Mods (DivinityModDataLoader.cs:42-50, 1403). Every missing-dependency path goes through it: MissingDependencies, the load-time message and GetDependencyMods (MainWindowViewModel.cs:1527, 1720-1724, 2735). ModIsAvailable checks both sets (:765). Larian paks found at load are added to IgnoredMods automatically (:1501-1509). Only Mods entries that have a UUID are loaded (:4744-4811). All 16 IgnoreDependencies UUIDs are already among the 20 in Mods, so that list does nothing on its own today.
Fix caveats: Correct CLAUDE.md §6 step 2 and the troubleshooting row at L715. Adding a module with its UUID to Mods is enough. IgnoreDependencies is needed only for a UUID that should count as installed without being in Mods, and adding a module to both lists does no harm.
docRef: CLAUDE.md section 6
