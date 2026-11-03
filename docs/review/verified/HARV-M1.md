# HARV-M1 verification results

Scope: read-only review of the SpasticPengu fork of BG3 Mod Manager. Statuses come from the workflow's independent verifiers. Note: most locations in the ledger leave out the `src/` prefix (for example, `GUI/...` should be `src/GUI/...`).

## Summary

| Status | Count |
|---|---|
| Confirmed | 7 |
| Plausible | 1 |
| Disputed | 0 |
| Refuted | 1 |
| Unverified | 0 |
| **Total** | **9** |

| Final severity | Count | Items |
|---|---|---|
| Medium | 3 | HARV-22, HARV-10, HARV-28 (Refuted) |
| Low | 6 | HARV-02, HARV-04, HARV-08, HARV-25, HARV-26, HARV-29 |

Downgrades from Medium to Low: HARV-02, HARV-04, HARV-08, HARV-25, HARV-26, HARV-29.

---

## Medium

### HARV-22 [Medium] Cancelled export leaves a partial zip and the temp folder; cleanup can throw after the success alert (E3, E5) (Confirmed, KNOWN)
Location: src/GUI/ViewModels/MainWindowViewModel.cs:3595, 3650
- Cancelling between mods runs `return false` inside the `using` (:3595). That disposes the writer, which leaves a zip that looks finished but is incomplete, and skips Directory.Delete. File.Delete runs only on success (:3620-3625).
- Cancelling during WriteZipAsync throws OperationCanceledException, which is caught, so the user sees an "Error writing" alert and the partial zip remains. If CreatePackageAsync fails partway (DivinityFileUtils.cs:156-176), its partial pak stays in tempDir and the success alert is still scheduled (:3630-3636). The non-recursive Directory.Delete at :3650, outside the try, then throws and skips OnMainProgressComplete, so the progress overlay stays stuck.

Fix caveats: Write the zip to a temp file and rename it on success. Delete the partial zip on failure. Delete the temp folder recursively in a `finally`. The editor-mod part matters less here, because the owner's mods are mostly paks.
docRef: plan Stage 2 E3, E5; AUDIT-PLAN claims 3, 5; AUDIT-PLAN-03

### HARV-10 [Medium] Tab is swallowed app-wide by the SwapListFocus hotkey (Plausible, NEW)
Location: src/GUI/ViewModels/AppKeys.cs:70; src/GUI/Views/MainViewControl.xaml.cs:34-41; src/GUI/Views/HorizontalModLayout.xaml.cs:605-616
- Every hotkey becomes a window-level KeyBinding (MainViewControl.xaml.cs:35-41). SwapListFocus (Tab) has no canExecute beyond Enabled && !IsLocked (Hotkey.cs:67-81,122-124; HorizontalModLayout.xaml.cs:605-616), so it can run almost all the time.
- When CommandManager.TranslateInput runs a bound command, it sets e.Handled, so KeyboardNavigation skips the Tab move. The filter TextBoxes set AcceptsTab=False (HorizontalModLayout.xaml:65,172), so Tab bubbles up to the window and is swallowed. This breaks keyboard and screen-reader navigation. The one verifier was medium-confidence, and confirming it needs a run on Windows.

Fix caveats: `whenAnyListFocused` does not exist yet. It has to be built from IsKeyboardFocusWithin on the Active, Inactive and probably ForceLoaded list views, then passed to AddAction. The same issue exists in wt-perf at HorizontalModLayout.xaml.cs:648.

### HARV-28 [Medium] The order-JSON entry read in an archive may come back short (I1) (Refuted, KNOWN)
Location: src/GUI/ViewModels/MainWindowViewModel.cs:3461-3464
- Refuted because the shipped SharpCompress 0.39 decoders do not produce short reads. The code does make one ReadAsync call and ignores its return value. But EntryStream passes Read straight through (EntryStream.cs:60-68). ZlibBaseStream loops until the buffer is full or input ends (ZlibBaseStream.cs:557-598). Deflate64 loops with `while(true)` (:124), LzmaStream loops until total == count (:168), and RarStream only returns once the request is filled (:72-136).
- Corrected statement: this breaks the Stream contract, but zip, 7z and rar entries fill the buffer. Truncation is only possible at a truncated or corrupt EOF. It is a hygiene issue, not a reachable bug. (The verifier suggested Low; the ledger keeps Medium.)

Fix caveats: Switching to ReadExactlyAsync or StreamReader.ReadToEndAsync is cheap hardening and stops relying on file.Size. Low priority; do it opportunistically.
docRef: plan Stage 3 I1; AUDIT-PLAN claim 9

## Low

### HARV-02 [Low] A failed update check reports "up-to-date" (Confirmed, NEW)
Location: src/GUI/ViewModels/AppUpdateWindowViewModel.cs:54-87
- App.xaml.cs:39-45 subscribes CheckForUpdateEvent, so AutoUpdater's ShowError raises the event with only Error set and IsUpdateAvailable false (AutoUpdater.cs:369-372,548-553). The handler never checks Error, and WebHelper.cs:32-39 swallows changelog download failures, so the catch does not run either. The else branch shows "Already up-to-date" (AppUpdateWindowViewModel.cs:54-95,109-113).
- This only happens on a manual check (showAlert=true); automatic checks stay silent. The message is wrong but nothing breaks, hence Low rather than Medium.

Fix caveats: Return early on args.Error, before the changelog download. Always log the error, and call ShowAlert (Danger) only when `_showAlert` is set. AutoUpdater.ReportErrors has no effect once the event is subscribed.

### HARV-04 [Low] The update feed is trusted blindly, and a non-zip URL is executed directly (Confirmed, PARTLY_KNOWN)
Location: src/GUI/ViewModels/AppUpdateWindowViewModel.cs:39, 70-79; src/GUI/App.xaml.cs:36-46
- The app validates nothing, and AutoUpdater 1.9.2 runs any non-zip download with ShellExecute (DownloadUpdateDialog.cs:158-163,165-222). But the feed is HTTPS from upstream LaughingLeader's repo, not the fork (DivinityApp.cs:22), and Update.xml has no checksum.
- Corrected statement: whoever can tamper with the feed can also replace the release zip, and ZipExtractor unpacks it over the app folder. That is code execution too, so the non-zip path adds little extra exposure.

Fix caveats: A SHA256 in the same feed is controlled by the same attacker, so it does not help. The useful fixes are pointing URL_UPDATE at the fork, checking the HTTPS host prefix and file extension, and signing releases later (D2).
docRef: plan Stage 1 (Repoint the updater); BLD-15

### HARV-08 [Low] Menu shortcut labels go stale after a key is rebound (Confirmed, NEW)
Location: src/GUI/Views/MainViewControl.xaml.cs:95
- RegisterKeyBindings sets InputGestureText once from `key.ToString()` (:95). The Settings keybinding editor changes the same Hotkey instances at runtime (HotkeyEditorControl.xaml.cs:58-62; SettingsWindow.xaml:258), so the label goes stale.
- Corrected statement: the shortcut itself works right away after a rebind, because Key and Modifiers are bound (:38-40). Only the label is wrong, and a restart fixes it because LoadKeybindings runs before the menu is built (MainWindowViewModel.cs:4127). The effect is cosmetic only.

Fix caveats: Bind MenuItem.InputGestureTextProperty to DisplayBindingText with Source=key, the same way the Command binding is done.

### HARV-25 [Low] Split (multi-part) paks such as Foo_1.pak are never exported (E7) (Confirmed, NEW)
Location: src/GUI/ViewModels/MainWindowViewModel.cs:3597-3599; src/Core/Util/DivinityModDataLoader.cs:429-446
- PakIsNotPartial drops `<base>_<n>.pak` files when the base pak exists (DivinityModDataLoader.cs:430-446,763-764). Export writes only `Path.GetFileName(mod.FilePath)`, so the parts are left out. The same applies in wt-perf.
- LSLib only starts a new part when a V16+ pak goes past 4 GiB (PackageWriter.cs:98-103; PackageVersion.cs:20-29). Split mod paks are therefore very rare, hence Low.

Fix caveats: Probe Package.MakePartFilename(path, 1..n) until a part is missing, or read Metadata.NumParts, and export each part next to the base pak. Editor-mod packaging through CreatePackageAsync could also produce parts.

### HARV-26 [Low] Exports leak the full Windows path, including the user name, of meta-less override paks (P1) (Confirmed, NEW)
Location: src/Core/Util/DivinityModDataLoader.cs:620; src/GUI/ViewModels/MainWindowViewModel.cs:3802-3809 (text export), :3581 (archive order JSON)
- JSON text export: a meta-less override gets UUID = pakPath (DivinityModDataLoader.cs:620), which usually sits under %LOCALAPPDATA% and includes the user name. It is IsForceLoaded (:639), so it lands in ForceLoadedMods (MWVM:5245), which is always added to the JSON export (MWVM:3803, 3809) with UUID as a DataMember (DivinitySerializedModData.cs:10).
- Corrected statement: the archive part is overstated. The zip writes only SelectedModOrder (MWVM:3582), and meta-less mods cannot be toggled into the order (DivinityModData.cs:419-421). So the archive leaks only when an imported order already contains a path-UUID (MWVM:2126). This is a privacy disclosure in a file the user chose to share.

Fix caveats: In FromMod/export, hash or redact any UUID that is not a GUID, keeping only the file name. The TSV and TXT lines use FileName and look safe, but check them.

### HARV-29 [Low] Two order JSONs with the same base name in one archive: the second is lost (Confirmed, PARTLY_KNOWN)
Location: src/GUI/ViewModels/MainWindowViewModel.cs:3468
- ImportArchiveAsync keys JSON entries by base name in a case-sensitive Dictionary (:3397). Dictionary.Add throws on a duplicate (:3468), the per-entry catch only records the error (:3470-3474), and only the first order is imported. Users reach this through Import Order From Archive (:3082).
- The app's own export writes a single order JSON (:3582), so only hand-built or multi-source archives hit this. The error goes to an _Logs error file (:3091-3098), so the loss is not fully silent.

Fix caveats: Key by the full entry path, or add a suffix to duplicate names (Main, Main_2). No export-side change is needed. Collisions need an exact case match. ImportCompressedFileAsync (:3236) is unaffected.
docRef: AUDIT-PLAN-10
