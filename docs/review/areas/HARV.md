# Defects noticed during stage design

Harvested from every design document in `scratchpad/designs/`: S1, S7, S8, S9, S6-final, S6-A/B/C, LIB-final, LIB-A/B/C, and also S4-A/B/C and S5-A/B/C. Each claim was re-read against `master` @ `fc35358` unless stated otherwise. Paths are relative to `src/` unless they start with a repo-root file. MWVM = `GUI/ViewModels/MainWindowViewModel.cs`, DMDL = `Core/Util/DivinityModDataLoader.cs`. Design proposals and risks of future features are left out. Defects that only exist in the upstream avalonia branch are listed as Info, for the record.

## Findings

### [High] HARV-01: Cancelling or failing the app-update download closes the app
- Location: GUI/ViewModels/AppUpdateWindowViewModel.cs:39-43
- What: `Environment.Exit(0)` sits outside the `if (AutoUpdater.DownloadUpdate(...))` branch, so it runs whatever `DownloadUpdate` returns.
- Failure scenario: The user confirms an update and then cancels the download dialog, or the download or checksum fails. `DownloadUpdate` returns false and the process exits anyway, losing unsaved order edits.
- Evidence: `if (AutoUpdater.DownloadUpdate(_updateArgs)) { Application.Current.Shutdown(); } Environment.Exit(0);`
- Fix: Move `Environment.Exit(0)` inside the true branch. In the else branch, set `IsVisible = false` and `ShowAlert("The update was cancelled or could not be downloaded", Warning)` (S1 §3.3, `fix/update-check-errors` from master).
- Confidence: Confirmed
- Source: S1 §1 F10, §3.3

### [Medium] HARV-02: A failed update check reports "up-to-date"
- Location: GUI/ViewModels/AppUpdateWindowViewModel.cs:54-87
- What: `OnUpdateCheckAsync` never reads `args.Error`. AutoUpdater 1.9.2 raises the event with `Error` set and `IsUpdateAvailable=false` when the feed fetch or parse fails, so the else branch runs.
- Failure scenario: The network is down, or the feed is a 404 or malformed. F7 (manual check) shows "Already up-to-date" and the window says "is up-to-date".
- Evidence: The method starts by downloading the changelog and then branches only on `args.IsUpdateAvailable` (:70, :80-87). There is no reference to `args.Error` anywhere in the file.
- Fix: At the top, `if (args.Error != null) { if (_showAlert) ShowAlert($"Update check failed: {args.Error.Message}", Danger, 60); else Log(...); return; }` (S1 §3.3).
- Confidence: Confirmed for the app side. AutoUpdater raising the event with `Error` is from the 1.9.2 source (`AutoUpdater.cs:548-553`), which S1 read on the web.
- Source: S1 §1 F9, §3.3

### [High] HARV-03: Fork builds read upstream's update feed and would self-update to upstream releases
- Location: Core/DivinityApp.cs:18-25; GUI/GUI.csproj:16-17
- What: `URL_UPDATE` and `URL_CHANGELOG_RAW` point at LaughingLeader's repo, and the fork's AssemblyVersion is the same as upstream's (1.0.12.9).
- Failure scenario: A friend runs a fork build. When upstream publishes 1.0.13, the startup check offers it, and accepting replaces the fork with upstream's zip (risk R4).
- Evidence: `URL_UPDATE = @"https://raw.githubusercontent.com/LaughingLeader/BG3ModManager/master/Update.xml"`
- Fix: Repoint the URLs to the fork's release assets, switch to CalVer and move all identity constants together (S1 §3.1-3.2). Do this before sharing any build.
- Confidence: Confirmed
- Source: S1 §0 (R4), §1 F1, §3.1

### [Medium] HARV-04: The update feed is trusted blindly, and a non-zip URL is executed directly
- Location: GUI/ViewModels/AppUpdateWindowViewModel.cs:70-79, :39; GUI/App.xaml.cs:36-46
- What: The app checks no host, scheme, extension or checksum before offering `args.DownloadURL`. AutoUpdater 1.9.2 extracts a `.zip` over the app folder and starts any other file through ShellExecute, without any signature check.
- Failure scenario: Someone tampers with the feed (a compromised account or a hand-edited feed) and points it at an `.exe`. The user clicks Update and the file runs as the user.
- Evidence: The `if (args.IsUpdateAvailable)` branch only formats text and enables Confirm. `TryRunUpdate` passes `_updateArgs` straight to `DownloadUpdate`.
- Fix: `IsTrustedUpdate(args)` requires the fork's HTTPS release prefix, `.zip` and a `SHA256` checksum, and refuses the update otherwise (S1 §3.3). A signed feed is deferred (D2).
- Confidence: Confirmed that the app side has no checks. AutoUpdater's execution behaviour is from its 1.9.2 source (`DownloadUpdateDialog.cs:150-238`), per S1.
- Source: S1 §1 F6-F7, §3.3, §5

### [Info] HARV-05: Local release zips can ship stray files
- Location: BuildRelease.py:55-59
- What: Only `Data`, `Orders` and `_Logs` are deleted from `bin/Publish` before it is zipped.
- Failure scenario: A developer's local publish folder contains a `debug` trigger file, `perfscroll` or `*.pdb`. They end up in the zip, so logging is switched on for users.
- Evidence: Three `SilentRemove` calls, then `zipdir("bin/Publish", file_name)`.
- Fix: Build releases only on clean CI and add the `verify-zip` deny/require list (S1 §5 table).
- Confidence: Confirmed
- Source: S1 §5 ("Stale files in the zip")

### [High] HARV-06: Exceptions thrown inside ReactiveCommands are silent, and the busy overlay can stick
- Location: GUI/ViewModels/MainWindowExceptionHandler.cs:14-19 (handler installed at MWVM:4903)
- What: `OnNext`, the path taken by a throwing `ReactiveCommand`, only calls `DivinityApp.Log`, and logging is off by default. Only `OnError` resets `MainProgressIsActive` and shows an alert.
- Failure scenario: Any command throws, for example a save or an import. Nothing is shown. If the command had raised the busy overlay, it stays up and covers the whole UI until restart.
- Evidence: `public void OnNext(Exception value) { DivinityApp.Log(...); }`. The alert and progress reset exist only in `OnError` (:21-35). The repo has no `ThrownExceptions` subscriptions.
- Fix: In `OnNext`, marshal to the UI thread, reset `MainProgressIsActive`, and send a throttled `ShowAlert(Danger)`. Add an always-on in-memory ring-buffer trace listener (S7 §6, `fix/surface-command-errors`).
- Confidence: Confirmed that it only logs. That the overlay sticks is Plausible: run a command that throws while `MainProgressIsActive` is true on Windows.
- Source: S7 §1 #1, §6

### [Low] HARV-07: Logging is switched on only after an error, so the log holds no context
- Location: GUI/Views/MainWindow.xaml.cs:97-99, 128-130, 152-154
- What: `DisplayError`, `OnUIException` and `OnUnhandledException` call `ToggleLogging(true)` after the failure, then offer to open the logs folder.
- Failure scenario: The user hits an error, opens the log and finds only the final message, with nothing that led up to it.
- Evidence: `public void DisplayError(string msg) { ToggleLogging(true); DivinityApp.Log(msg); ...`
- Fix: Keep an always-on ring buffer of the last ~2,000 lines and flush it to `_Logs` on error or on "Open log" (S7 §6).
- Confidence: Confirmed
- Source: S7 §1 #2

### [Medium] HARV-08: Menu shortcut labels go stale after a key is rebound
- Location: GUI/Views/MainViewControl.xaml.cs:95
- What: Each menu item's `InputGestureText` is set once from `key.ToString()` when the menu is built. `Hotkey.DisplayBindingText` is reactive, but nothing binds to it.
- Failure scenario: The user rebinds Save to Ctrl+Shift+S in the keybindings editor. The menu still shows "Ctrl+S" until restart.
- Evidence: `new MenuItem { Header = menuSettings.DisplayName, InputGestureText = key.ToString(), Command = key.Command }`
- Fix: Bind `InputGestureText` to `Hotkey.DisplayBindingText` (S7 §3.4).
- Confidence: Confirmed. The static assignment is certain; whether a rebind without restart leaves the label stale needs a Windows run to confirm.
- Source: S7 §1 #7, §3.4

### [Low] HARV-09: The keybinding tooltip says "(Modified)" exactly when the key is at its default
- Location: Core/Models/App/Hotkey.cs:135-137
- What: The tooltip selector is inverted.
- Failure scenario: Every unmodified shortcut shows "Name (Modified)" in the keybindings window, and modified ones show no marker.
- Evidence: `.Select(x => x.Item2 ? $"{x.Item1} (Modified)" : x.Item1)`, where `Item2` is `IsDefault`. `ModifiedText` just above correctly uses `!b`.
- Fix: `!x.Item2 ? ... : ...` (S7 §3.13).
- Confidence: Confirmed
- Source: S7 §1 #7, §3.13

### [Medium] HARV-10: Tab is swallowed app-wide by the SwapListFocus hotkey
- Location: GUI/ViewModels/AppKeys.cs:70; GUI/Views/MainViewControl.xaml.cs:34-41; GUI/Views/HorizontalModLayout.xaml.cs:605-616
- What: `SwapListFocus = Key.Tab` is a window-level `KeyBinding`. The action is added with no `canExecute`, so the command can always execute, but it only does something when a mod list has focus.
- Failure scenario: With focus in the filter box, a profile combo or the toolbar, pressing Tab does nothing and focus does not move. This is a keyboard-accessibility failure.
- Evidence: `ViewModel.Keys.SwapListFocus.AddAction(() => { if (ListHasFocus(Inactive...)) ... else if (ListHasFocus(Active...)) ... });`, with no second argument.
- Fix: `AddAction(action, canExecute: whenAnyListFocused)` using the existing overload (`Hotkey.cs:67-75`) (S7 §3.13).
- Confidence: Plausible. Needs Windows: press Tab from the filter box before and after the fix.
- Source: S7 §1 #8, §3.13

### [Low] HARV-11: Every Int32/Int64 setting binds to ExtenderSettings, whatever its source object
- Location: GUI/Views/SettingsWindow.xaml.cs:255-260
- What: The IntegerUpDown binding uses `Source = ViewModel.ExtenderSettings`, while the Bool and String cases use the `source` parameter of `CreateSettingsElements`.
- Failure scenario: Latent today, because only `ScriptExtenderSettings` has int `[SettingsEntry]` properties (DebuggerPort, DebuggerFlags). The first int setting added to `DivinityModManagerSettings` or `ScriptExtenderUpdateConfig` would bind to a missing property and never save.
- Evidence: `ud.SetBinding(IntegerUpDown.ValueProperty, new Binding(prop.Property.Name) { Source = ViewModel.ExtenderSettings, ...`
- Fix: `Source = source`.
- Confidence: Confirmed (latent)
- Source: S7 §1 #12, §3.8

### [Low] HARV-12: Delete-files dialog: Esc does not cancel, a typo, and a row index outside the grid
- Location: GUI/Views/DeleteFilesConfirmationView.xaml:133-149
- What: Neither button sets `IsCancel` or `IsDefault`. The tooltip says "Reycling Bin" (:142). The button `StackPanel Grid.Row="2"` (:149) sits in a grid with only two `RowDefinition`s (:133-136).
- Failure scenario: Esc does not dismiss a destructive dialog. WPF clamps row 2 to row 1, so the layout relies on that clamp.
- Evidence: As cited. The file has no `IsCancel` or `IsDefault`.
- Fix: `CancelButton IsCancel="True" IsDefault="True"`, fix the row and the typo, and give the destructive button danger styling (S7 §3.9).
- Confidence: Confirmed
- Source: S7 §1 #14, §3.9

### [Info] HARV-13: The unused export dialog's confirm button says "Delete"
- Location: GUI/Views/ExportOrderToArchiveView.xaml:136
- What: The confirm button's label is "Delete". The view and `ExportOrderToArchiveViewModel` are never instantiated (grep).
- Failure scenario: None today. It would mislead users as soon as Stage 8 revives the view.
- Evidence: `<Button x:Name="ConfirmButton" ... Content="Delete" />`. `Run()` is a stub (`ExportOrderToArchiveViewModel.cs:61-67`).
- Fix: Change it to "Export" (S7 §3.10).
- Confidence: Confirmed
- Source: S7 §1 #14, §3.10; S8 §2.5

### [Low] HARV-14: Typo in a menu label: "Moved Selected Mods to Opposite List"
- Location: GUI/ViewModels/AppKeys.cs:60
- What: "Moved" should be "Move".
- Failure scenario: The wrong text shows in the Edit menu and the keybindings list.
- Evidence: `[MenuSettings("Edit", "Moved Selected Mods to Opposite List", true)]`
- Fix: Correct the text (S7 §6, `fix/ui-small-bugs`).
- Confidence: Confirmed
- Source: S7 §1 #7

### [Low] HARV-15: Elements placed in Grid.Column="2" of 2-column grids
- Location: GUI/Views/MainViewControl.xaml:155; GUI/Views/SettingsWindow.xaml:138
- What: `UpdatesButtonPanel` and `ResetSettingsButton` use column 2. Their grids define only two columns (MainViewControl.xaml:140-143, SettingsWindow.xaml:124-127).
- Failure scenario: WPF clamps the elements to the last column. They render, but the layout is not what the markup says.
- Evidence: As cited.
- Fix: Use the right column index (S7 §6, `fix/ui-small-bugs`).
- Confidence: Confirmed
- Source: S7 §1 (below the cut)

### [Low] HARV-16: The alert bar and tooltips use hard-coded colours that fail contrast, and alerts can't be dismissed by keyboard
- Location: GUI/Controls/AlertBar.xaml.cs:93-97, 162, 176, 190, 205; GUI/Controls/AlertBar.xaml:51-61; GUI/Themes/MainResourceDictionary.xaml:588-683
- What: The alert bar has one slot, 11 pt bold text with no wrapping, and Bootstrap hex colours that ignore the theme. Tooltip status text uses `Lime`, `Orange` and similar literals. The close control is a `MouseUp` on an Image.
- Failure scenario: In Dark, a warning or info alert is about 2:1 contrast (WCAG AA needs 4.5:1). Long error text is cut off. A timeout-0 alert stays until another alert replaces it, and keyboard users cannot dismiss it.
- Evidence: As cited. The contrast ratios were computed by S7.
- Fix: Use semantic theme tokens, wrap the text, add a focusable close button and keep a notification history (S7 §2.3, §3.6).
- Confidence: Plausible. Needs Windows to check the rendered colours in Dark.
- Source: S7 §1 #3, #9

### [Info] HARV-17: Dead UI: status-bar spinner, Help window, debug style
- Location: MWVM:287, 4899 and GUI/Views/MainViewControl.xaml:509-514; GUI/Views/MainWindow.xaml.cs:298; GUI/Themes/Light.xaml:15 vs Dark.xaml:15
- What: `StatusBarBusyIndicatorVisibility` is set once to Collapsed and never bound. `ShowHelpWindow` has no callers, so the `@` filter syntax is undocumented in the app. `ListInactiveRectangle` is `Red` in Light, `Black` in Dark and has no consumers.
- Failure scenario: None at runtime. Users cannot find out about the filter syntax.
- Evidence: Grep shows one definition and no callers or consumers.
- Fix: Wire `HelpWindow` to F1 or a Help menu item, and remove or bind the rest (S7 §3.4, §3.11).
- Confidence: Confirmed
- Source: S7 §1 #4, #6, below the cut

### [Low] HARV-18: Endless animations: the Tools menu blinks and BusyIndicator storyboards never stop
- Location: GUI/Views/MainViewControl.xaml:50-78; GUI/Controls/BusyIndicator.xaml:14-40
- What: The Tools menu blinks at 0.5 s forever, which breaks WCAG 2.2.2 and ignores reduced-motion settings. BusyIndicator starts eight `RepeatBehavior="Forever"` storyboards on `Loaded` and never stops them, even while collapsed.
- Failure scenario: Constant motion for users, plus animation clocks that may keep ticking in the background.
- Evidence: As cited.
- Fix: Use a static badge. Start and stop the storyboards on `IsVisibleChanged` and honour `SystemParameters.ClientAreaAnimation` (S7 §3.4, §3.7).
- Confidence: Plausible. Needs Windows to confirm that the clocks tick while collapsed.
- Source: S7 §1 #15

### [Low] HARV-19: The main window has no MinWidth, so the toolbar clips
- Location: GUI/Views/MainWindow.xaml:20-21; GUI/Views/MainViewControl.xaml:303-500
- What: A flat strip of 16 controls with no overflow, in a window with no minimum size.
- Failure scenario: Below about 1300 px wide, toolbar controls are cut off.
- Evidence: There is no `MinWidth` on the window.
- Fix: `MinWidth="960" MinHeight="540"` and toolbar groups with an overflow (S7 §3.3-3.4).
- Confidence: Plausible. Needs Windows to measure the clipping width.
- Source: S7 §1 #11

### [High] HARV-20: Zip export over an existing file does not truncate it, leaving a corrupt archive (E1)
- Location: MWVM:3579
- What: `File.OpenWrite` opens an existing file without truncating it. If the new archive is shorter than the old one, the old bytes remain after the new central directory.
- Failure scenario: "Export Order to Archive As…" is pointed at an existing, larger zip (the dialog prompts to overwrite). Readers search for the end-of-central-directory record from the end of the file, so they find the old one and report a corrupt archive or stale contents.
- Evidence: `using (var stream = File.OpenWrite(outputPath))`
- Fix: Write to `<target>.partial` with `FileMode.Create`, dispose the writer, then `File.Move(tmp, target, true)`. Delete the temp file in `finally` (S8 §5).
- Confidence: Confirmed
- Source: S8 §2.1 (E1), §5

### [High] HARV-21: Zip export silently drops any mod it fails to write, including any pak of 4 GiB or more (E2)
- Location: MWVM:3666-3693 (catch at :3679); writer options :3039
- What: `WriteZipAsync` catches and ignores every exception from `writer.Write`. The writer is built with `WriterOptions`, not `ZipWriterOptions`, so zip64 is off. SharpCompress 0.39 throws `NotSupportedException` for an entry of 4 GiB or more, and that exception is swallowed too.
- Failure scenario: A pak is locked, unreadable or at least 4 GiB. The archive is written without it, the alert says "Exported load order to …", and the friend who imports it is missing mods.
- Evidence: `catch (Exception) { // ignored because an exception on a cancellation request cannot be avoided ... }`
- Fix: Per file, rethrow `OperationCanceledException` and record any other exception as a named failure shown in `ShowAlert`. Use `ZipWriterOptions` with per-entry `EnableZip64` for entries of 0xFFFF0000 bytes or more (S8 §5).
- Confidence: Confirmed for the swallowing. The zip64 throw comes from S8's reading of the tagged SharpCompress source (`ZipWriter.cs:451-455`).
- Source: S8 §2.1 (E2, SharpCompress notes), §5

### [Medium] HARV-22: Cancelled export leaves a partial zip and the temp folder; cleanup can throw after the success alert (E3, E5)
- Location: MWVM:3595, 3650
- What: On cancel, `return false` inside the `using` leaves a partial archive at the output path and skips `Directory.Delete(tempDir)`. On success, `Directory.Delete(tempDir)` is non-recursive and sits outside the try.
- Failure scenario: (a) The user cancels at 50%, and a valid-looking but incomplete zip plus `_Temp_<date>` stay behind. (b) Packing an editor mod fails and leaves a file in the temp folder. `Directory.Delete` then throws `IOException` inside the scheduled task, after the success alert has already been shown.
- Evidence: `if (t.IsCancellationRequested) return false;` (:3595) and `Directory.Delete(tempDir);` (:3650).
- Fix: Temp plus rename (HARV-20). Put the temp folder under `Path.GetTempPath()` and delete it recursively in `finally` (S8 §5).
- Confidence: Confirmed
- Source: S8 §2.1 (E3, E5)

### [Low] HARV-23: Export busy-waits with Task.Delay(0), and cancel cannot interrupt a large pak (E4)
- Location: MWVM:3686-3690
- What: The wrapper spins `await Task.Delay(0, token)` until a child task finishes the synchronous `writer.Write`.
- Failure scenario: One CPU core spins for the whole export. Cancel only takes effect between files, so a 2 GB pak runs to the end.
- Evidence: `while (!awaiter.IsCompleted) { await Task.Delay(0, token); }`
- Fix: `using var entry = zip.WriteToStream(name, opts); await src.CopyToAsync(entry, 1 << 20, token);` (S8 §5).
- Confidence: Confirmed
- Source: S8 §2.1 (E4)

### [Low] HARV-24: Export progress divides by zero when there is nothing to export (E6)
- Location: MWVM:3575
- What: `1d / modPaks.Count` gives Infinity when the order has no exportable mods and there are no overrides.
- Failure scenario: An empty order makes a zip that holds only the order JSON, with an undefined progress increment.
- Evidence: `var incrementProgress = 1d / modPaks.Count;`
- Fix: Return early with a "nothing to export" alert, and weight progress by bytes (S8 §5).
- Confidence: Confirmed
- Source: S8 §2.1 (E6)

### [Medium] HARV-25: Split (multi-part) paks such as Foo_1.pak are never exported (E7)
- Location: MWVM:3597-3599; DMDL:429-446
- What: The loader deliberately skips `<base>_<n>.pak` part files, so `mod.FilePath` is only the base pak. Export writes only `mod.FilePath`.
- Failure scenario: A large multi-part mod is exported without its parts. It imports broken on the friend's machine.
- Evidence: `var fileName = Path.GetFileName(mod.FilePath); await WriteZipAsync(zipWriter, fileName, mod.FilePath, t);`, plus `PakIsNotPartial` skipping `_\d+` names.
- Fix: Enumerate the parts (probe `_1`, `_2`… until one is missing, or read `NumParts`) and write them all (S8 §2.3).
- Confidence: Confirmed
- Source: S8 §2.3

### [Medium] HARV-26: Exports leak the full Windows path, including the user name, of meta-less override paks (P1)
- Location: DMDL:620; MWVM:3802-3809 (text export); MWVM:3581 (archive order JSON)
- What: A meta-less override gets `UUID = pakPath`. "Export Load Order As Text File" in JSON format always appends `ForceLoadedMods` and writes `UUID`. The archive's order JSON includes it whenever such a mod is in `Order`.
- Failure scenario: A user with any meta-less override shares a JSON text export. It contains `C:\Users\<name>\AppData\Local\Larian Studios\...\Mods\Foo.pak`.
- Evidence: `UUID = pakPath,`, and `exportMods.AddRange(ForceLoadedMods...)` serialised through `DivinitySerializedModData.UUID` (`[DataMember]`).
- Fix: Never write a non-GUID UUID raw. Write `ovr:<sha256(uuid)[0..16]>` plus the bare file name (S8 §2.4, §3.3).
- Confidence: Confirmed
- Source: S8 §0 item 5, §2.4

### [High] HARV-27: No build can import an archive of 2 GiB or more, and every import reads the whole file into memory
- Location: MWVM:3412 (ImportArchiveAsync), MWVM:3252 (ImportCompressedFileAsync)
- What: Both importers do `await fileStream.ReadAsync(new byte[fileStream.Length], 0, (int)fileStream.Length)` and then throw the buffer away.
- Failure scenario: A full-library export (the owner has 27 GB) or any zip of 2 GiB or more fails. Either the allocation fails or the `(int)` cast overflows, the outer catch runs, and the user sees "Error extracting archive". Smaller archives still allocate their full size for nothing.
- Evidence: As cited.
- Fix: Delete the line, because `ArchiveFactory.Open` reads the stream itself (Stage 3 I3 per S8).
- Confidence: Confirmed. The exact exception type needs a Windows run with a 2 GiB zip.
- Source: S8 §0 item 5, §2.2, §7 (risk table)

### [Medium] HARV-28: The order-JSON entry read in an archive may come back short (I1)
- Location: MWVM:3461-3464
- What: One `ReadAsync(result, 0, length)` call, with its return value ignored, is used for the whole entry.
- Failure scenario: A decompression stream returns fewer bytes than asked for. The JSON is truncated, deserialisation fails or gives a partial order, and nothing is reported.
- Evidence: `await entryStream.ReadAsync(result, 0, length);`
- Fix: `ReadExactlyAsync`, or a `StreamReader.ReadToEndAsync` (S8 §10, upstream avalonia direction).
- Confidence: Plausible. S6-A notes that the AUDIT-PLAN claim 9 test found no short read for zip entries. Needs a run with a large (935-entry) Deflate order JSON.
- Source: S8 §2.2, §3.1; S6-A §11

### [Medium] HARV-29: Two order JSONs with the same base name in one archive: the second is lost
- Location: MWVM:3468
- What: `jsonFiles.Add(Path.GetFileNameWithoutExtension(file.Key), text)` throws `ArgumentException` on a duplicate key. The per-entry catch only records an error.
- Failure scenario: An archive has `a/Main.json` and `b/Main.json`. Only the first is imported.
- Evidence: As cited. It is a `Dictionary<string,string>` keyed by base name.
- Fix: Use unique, case-insensitive names with a suffix when exporting. Key by full entry path when importing (S8 §3.1).
- Confidence: Confirmed
- Source: S8 §2.2

### [Medium] HARV-30: Archive import flattens paks, so same-name paks overwrite each other (I5)
- Location: MWVM:3424-3429
- What: Every `*.pak` at any depth goes to `Mods/<GetFileName>`.
- Failure scenario: An archive holds two different mods both named `Foo.pak` in different folders. The last one wins, and the first is silently overwritten.
- Evidence: `var outputName = Path.GetFileName(file.Key); var outputFilePath = Path.Combine(outputDirectory, outputName);`
- Fix: Detect collisions and alert, or use the `uuid/version` layout on export (S8 §3.1).
- Confidence: Confirmed
- Source: S8 §2.2 (I5)

### [High] HARV-31: Importing an archive order named "Current" corrupts the selected order or Current
- Location: MWVM:3112-3126; Core/Extensions/ModelExtensions.cs:29-42
- What: `SetFrom` copies every non-null public property through `TypeDescriptor`, including `Name`, `FilePath`, the `IsModSettings` bool and the `Order` list reference. (a) If Current is selected, the imported order (with `IsModSettings=false`) turns Current into a non-modsettings order. (b) If another order is selected, the code copies the existing Current into it, and the imported order is discarded.
- Failure scenario: A user with "Main" selected imports a friend's zip containing `Current.json`. "Main" is renamed "Current", gets Current's `FilePath` and `IsModSettings=true`, and shares Current's `Order` list object. The imported order is gone. A later save of the renamed "Main" writes modsettings.lsx instead of Main.json.
- Evidence: `else { var currentOrder = ModOrderList.FirstOrDefault(x => x.IsModSettings); if (currentOrder != null) { SelectedModOrder.SetFrom(currentOrder); } }`
- Fix: For "Current", call `SelectedModOrder.SetOrder(order)` (entries only) when Current is selected. Otherwise add the import as a new order named, for example, "Imported Current" (S8 §11 PR 2, `fix/archive-import`).
- Confidence: Confirmed for the code path. The downstream save behaviour is Plausible: import such a zip on Windows with a non-Current order selected.
- Source: S8 §2.2; S6-C §8.4 ("SetFrom path, which already mangles imported orders")

### [High] HARV-32: An imported order with the same name as an existing one overwrites that order's file on save
- Location: MWVM:3128 (AddNewModOrder), 2613-2618, 2633; DMDL:1106
- What: Imported orders get `FilePath = null`. When saved, `FilePath` becomes `<LoadOrderPath>/<Name>.json`, which is written with `FileMode.Create` and no overwrite prompt.
- Failure scenario: The user has "Main" and imports a friend's archive whose order is also "Main". They select the imported "Main" and press Ctrl+S, and their own Main.json is replaced.
- Evidence: `if (String.IsNullOrWhiteSpace(SelectedModOrder.FilePath)) { SelectedModOrder.FilePath = Path.Join(outputDirectory, outputName); ... }`, then `ExportLoadOrderToFileAsync`.
- Fix: Give imported orders a unique name on import, or confirm before overwriting an existing file (S8 §2.2, §11 PR 2).
- Confidence: Confirmed
- Source: S8 §2.2

### [Medium] HARV-33: Importing a mod replaces the installed one by UUID even when it is older
- Location: MWVM:3160-3227
- What: `AddImportedMod` swaps the existing `DivinityModData` for the imported one without comparing versions. If the file names differ, both paks stay in Mods.
- Failure scenario: Importing an old zip downgrades a mod in the UI. The next refresh shows the higher version again (DMDL:789-806), and the user is left with duplicates.
- Evidence: `var existingMod = mods.Items.FirstOrDefault(x => x.UUID == mod.UUID); ... ActiveMods.Replace(existingMod, mod);`, with no version check.
- Fix: Compare `Version64`. Warn or ask before a downgrade, and offer to remove the old file (S8 §2.2, §11 PR 2).
- Confidence: Confirmed
- Source: S8 §2.2

### [Low] HARV-34: Quick zip export skips UpdateOrderFromActiveMods and reads live UI collections off-thread
- Location: MWVM:3526-3547, 3572
- What: Ctrl+R does not refresh `Order` first, while "Export As" does (:3700). The background task enumerates `Mods` and `SelectedModOrder.Order` with no snapshot.
- Failure scenario: The user exports within 250 ms of a drag and gets the previous order. Editing the list during an export can throw "collection was modified".
- Evidence: As cited.
- Fix: Take a snapshot on the UI thread, then hand it to a pure writer (S8 §0 item 3).
- Confidence: Plausible. The race needs a Windows run.
- Source: S8 §2.1

### [Low] HARV-35: MakeSafeFilename doesn't handle reserved device names or trailing dots and spaces
- Location: DMDL:57-64
- What: It only replaces `Path.GetInvalidFileNameChars()`.
- Failure scenario: An order named `CON` or `Main.` gives an export or save file name Windows cannot create or later open.
- Evidence: A loop over the invalid characters only.
- Fix: Reject or suffix reserved names and trim trailing dots and spaces (S8 §3.1).
- Confidence: Confirmed
- Source: S8 §3.1

### [Low] HARV-36: GithubData is never initialised but is dereferenced (B1)
- Location: Core/Models/DivinityModData.cs:393; Core/ModUpdater/ModUpdateHandler.cs:112
- What: The initialiser is commented out, but `mod.GithubData.Update(...)` runs when `Github.IsEnabled`.
- Failure scenario: Latent, because nothing sets `Github.IsEnabled` today. Enabling it throws a NullReferenceException.
- Evidence: `//GithubData = new GithubModData();` and `mod.GithubData.Update(githubData);`
- Fix: Initialise it or delete the GitHub stub (S9 D7).
- Confidence: Confirmed (latent)
- Source: S9 §1.3 B1

### [Low] HARV-37: Nexus LimitExceeded is inverted for the hourly limit (B2)
- Location: Core/Util/NexusModsDataLoader.cs:67
- What: `ApiDailyLimitExceeded() || !ApiHourlyLimitExceeded()`.
- Failure scenario: Latent, with no callers. Any future caller would see "exceeded" whenever the hourly limit is not exceeded.
- Evidence: As cited.
- Fix: Remove the `!`.
- Confidence: Confirmed (latent)
- Source: S9 §1.3 B2

### [Low] HARV-38: The Nexus API-call estimate uses a bitwise AND (B3)
- Location: Core/Util/NexusModsDataLoader.cs:118
- What: `mods.Count(...) & 2` where `* 2` was meant.
- Failure scenario: `GetLatestDownloadsForMods`, which has no callers, would pre-check 0 or 2 calls instead of 2 per mod and could exceed the rate limit.
- Evidence: As cited.
- Fix: `* 2`.
- Confidence: Confirmed (latent)
- Source: S9 §1.3 B3

### [Low] HARV-39: An early return leaves the Nexus loader "active", so an API key change is ignored until restart (B4)
- Location: Core/Util/NexusModsDataLoader.cs:181-192, 36-63
- What: `_isActive = true` is set, then the `total == 0` branch returns without `OnTaskDone()`. Afterwards `Dispose()` returns false, so `Init(newKey)` silently does nothing.
- Failure scenario: A background refresh with no Nexus-ID mods (reachable only through a rebound RefreshModUpdates key) leaves the flag stuck. A new API key entered afterwards is never used.
- Evidence: `if (total == 0) { taskResult.Success = false; taskResult.FailureMessage = "Skipping..."; return taskResult; }`
- Fix: Use `try/finally { OnTaskDone(); }`.
- Confidence: Confirmed. The path is latent with the default keys.
- Source: S9 §1.3 B4

### [Low] HARV-40: The "would exceed limits" result has no failure message (B5)
- Location: Core/Util/NexusModsDataLoader.cs:195-201
- What: It returns `taskResult` with `Success=false` (the default) and an empty `FailureMessage`.
- Failure scenario: The caller has nothing to show the user, so the check fails silently.
- Evidence: `OnTaskDone(); return taskResult;`, with no message set.
- Fix: Set `FailureMessage` to the limits text.
- Confidence: Confirmed
- Source: S9 §1.3 B5

### [Low] HARV-41: The Nexus archive-name regex mis-parses FileId and sometimes ModId (B6)
- Location: Core/Models/NexusMods/NexusModFileVersionData.cs:11, 23-27
- What: `^.*?-(\d+)-(.*?)(\d+)`. Nexus names look like `Name-<modId>-<version>-<uploadTs>` and carry no file id, so group 3 captures a version digit. A `-digits-` inside the mod name also shifts ModId.
- Failure scenario: The wrong `FileId` (and sometimes the wrong `ModId`) is stored on import, which breaks any future update check or Nexus link.
- Evidence: As cited.
- Fix: Parse only the modId and the trailing upload timestamp (S9 §2.1).
- Confidence: Plausible. The regex is confirmed; the Nexus naming scheme comes from S9's background knowledge.
- Source: S9 §1.3 B6

### [Low] HARV-42: Update rows never get a link, because SourceToLink discards GetURL (B7)
- Location: Core/Models/Updates/DivinityModUpdateData.cs:50-57
- What: `data.Item1.GetURL(data.Item2);` is called but its result is not returned. The method always returns `""`.
- Failure scenario: Every row in the Mod Updates view has an empty link. The view is dead UI today.
- Evidence: As cited.
- Fix: `return data.Item1.GetURL(data.Item2);`
- Confidence: Confirmed
- Source: S9 §1.3 B7

### [Low] HARV-43: The mod-update copy overwrites paks in Mods without the backup it prepares (B8)
- Location: GUI/ViewModels/ModUpdatesViewData.cs:180, 195-196, 205
- What: `File.Copy(..., true)` goes straight into Mods. `Mods_Old_ModManager` is created but nothing is ever written to it.
- Failure scenario: If revived, a bad update replaces the working pak with no rollback.
- Evidence: `Directory.CreateDirectory(backupFolder);`, then `File.Copy(file, Path.Combine(args.ModPakFolder, ...), true);`
- Fix: Move the old pak to `Mods_Old_ModManager/<UUID>/<ts>/`, then `File.Move` the new one in (S9 §2.5).
- Confidence: Confirmed (dead UI today)
- Source: S9 §1.3 B8; LIB-final §1.1

### [Low] HARV-44: Mod-update copy progress always shows 0 (B9)
- Location: GUI/ViewModels/ModUpdatesViewData.cs:177, 187, 211
- What: `args.TotalMoved / totalWork` is int/int division.
- Failure scenario: The progress dialog stays at 0% until the copy finishes.
- Evidence: `public int TotalMoved;` and `var totalWork = ...Count + ...Count;`
- Fix: `(int)(100.0 * TotalMoved / totalWork)`.
- Confidence: Confirmed
- Source: S9 §1.3 B9

### [Low] HARV-45: Archive import calls the Nexus API without checking the feature flag (B10)
- Location: MWVM:3083-3086 (vs :1903); Core/ModUpdater/Cache/NexusModsCacheHandler.cs:25-35
- What: The import-from-archive path calls `UpdateHandler.Nexus.Update` without `IsEnabled`, and the handler doesn't check it either. Its `Init` and API calls run whenever a key exists.
- Failure scenario: A user with a hand-edited API key and `NexusMods: false` imports a Nexus-named zip, and API requests are made anyway.
- Evidence: `if (result.Mods.Count > 0 && result.Mods.Any(x => x.NexusModsData.ModId >= ...)) await UpdateHandler.Nexus.Update(...)`
- Fix: Add `UpdateHandler.Nexus.IsEnabled &&`, and gate it inside the handler as well.
- Confidence: Confirmed
- Source: S9 §1.2, §1.3 B10

### [Low] HARV-46: Dependency and conflict entries inherit the parent mod's PublishHandle (B11)
- Location: DMDL:269, 296
- What: `PublishHandle = GetAttributeValueWithId(moduleInfoNode, ...)` reads the parent node instead of the dependency `node`.
- Failure scenario: Every dependency gets the dependent mod's mod.io handle, which is wrong as soon as PublishHandle is used for lookups.
- Evidence: As cited.
- Fix: Use `node`.
- Confidence: Confirmed
- Source: S9 §1.3 B11

### [Medium] HARV-47: The Script Extender updater writes DWrite.dll in place, so cancel or failure leaves it truncated (B12)
- Location: MWVM:341-366; the game-running check is commented out at MWVM:5424-5425
- What: `File.Create(dllDestination)` truncates the existing `DWrite.dll` and then streams the zip entry into it. There is no temp-and-rename, no hash and no game-running check.
- Failure scenario: The user cancels, or the connection drops mid-copy. The `OperationCanceledException` or IOException is caught and logged, and a partial `DWrite.dll` stays in `bin/`. The Script Extender fails to load at the next launch.
- Evidence: `using var fs = File.Create(dllDestination, 4096, ...); await unzippedEntryStream.CopyToAsync(fs, 4096, MainProgressToken.Token);`
- Fix: Write to `.part`, verify it, then `File.Move(..., true)`, and refuse while the game is running (S9 §2.5, §4).
- Confidence: Confirmed
- Source: S9 §1.3 B12

### [Low] HARV-48: The Nexus client initialises regardless of the feature flag, and the API key is stored in plaintext
- Location: MWVM:1056-1066; Core/Models/DivinityModManagerSettings.cs:29-31
- What: `Settings.WhenAnyValue(NexusModsAPIKey)` calls `NexusModsDataLoader.Init` whatever `NexusModsEnabled` is set to. The key is serialised in plain text into `Data/settings.json`.
- Failure scenario: The key sits readable in a portable folder that users zip and share, and a client is built even though the feature is off.
- Evidence: As cited.
- Fix: Gate `Init` on the flag, and store the key with DPAPI behind `ISecretStore` (S9 §2.2).
- Confidence: Confirmed
- Source: S9 §1.1, §2.2

### [Low] HARV-49: WebHelper.DownloadFileAsStreamAsync turns every failure into Stream.Null
- Location: Core/Util/WebHelper.cs:18-30
- What: It catches all exceptions and returns `Stream.Null`, so callers can't tell a failure from an empty result.
- Failure scenario: In the SE updater, a failed download goes to `new ZipArchive(Stream.Null)`, which throws a misleading "invalid zip" error that is only logged. Future update checks would treat failure as "no update".
- Evidence: `catch (Exception ex) { DivinityApp.Log(...); return Stream.Null; }`
- Fix: Let callers handle exceptions, or return a result type.
- Confidence: Confirmed
- Source: S9 §1.1

### [Info] HARV-50: The Mod Updates subsystem is dead code
- Location: GUI/ViewModels/ModUpdatesViewData.cs; MWVM:2262-2281, 2547; GUI/ViewModels/AppKeys.cs:58; Core/ModUpdater/ModUpdateHandler.cs:44, 119, 140; Core/ModUpdater/Cache/GithubModsCacheHandler.cs:87-90
- What: Nothing adds to `ModUpdatesViewData.Mods`. The background refresh call is commented out, and its hotkey defaults to `Key.None`. The orchestrator methods `return false`, and the GitHub `Update` is a stub.
- Failure scenario: None today. It hides defects HARV-36 to HARV-44 until the feature is revived.
- Evidence: Grep finds no `.Mods.Add` on the view data.
- Fix: Revive it behind the B-list fixes, or delete it (S9 §3).
- Confidence: Confirmed
- Source: S9 §1.1-1.2

### [Info] HARV-51: Upstream avalonia DownloadManagerService defects (not in master)
- Location: wt-avalonia src/Services/Services/DownloadManagerService.cs:61, 125-127, 132
- What: (a) `using var httpClient` disposes the client when `OpenUriAsStreamAsync` returns, before the content stream is read. (b) `_periodicDownloadStartDisp` is disposed but never nulled, so the queue may not restart. (c) `ActiveDownloads += 1` from pool threads is not atomic.
- Failure scenario: Downloads abort, or the queue stalls after it first empties.
- Evidence: As cited.
- Fix: Use a shared `HttpClient`, null the disposable and use `Interlocked`. This only matters if the idea is ported.
- Confidence: Plausible (S9 marked it [inf])
- Source: S9 §3.2

### [High] HARV-52: Master can promote a meta-less override (UUID = pak path) into the active list and write a non-GUID UUID to modsettings.lsx
- Location: MWVM:3802-3803 → DMDL:1215-1236 → MWVM:2126-2129, 2142 → DMDL:1478-1482
- What: (1) A JSON text export includes `ForceLoadedMods` with `UUID = <pak path>`. (2) "Load Mod Order From File" falls back to `List<DivinitySerializedModData>` when parsing it as `DivinityLoadOrder` fails. (3) `LoadModOrder` sets `ForceAllowInLoadOrder = true` for any force-loaded mod in the order, which makes `CanAddToLoadOrder` true. (4) `GenerateModSettingsFile` skips only blank UUIDs.
- Failure scenario: Export a text JSON, load it back with "Load order from file", then Export to Game. modsettings.lsx gets `<attribute id="UUID" type="guid" value="C:\Users\...\Foo.pak"/>` with `Folder="Gustav"`. LSLib then fails to parse the file (`new Guid(str)` throws). The manager reads zero mods and raises "reset externally", and the game probably rejects or resets the order. A hand-edited, friend's or Avalonia-written order JSON with a path UUID goes straight to step 3.
- Evidence: `if (mod.IsForceLoaded) { mod.ForceAllowInLoadOrder = true; }`, `UUID = pakPath,` and `if (!String.IsNullOrWhiteSpace(mod.UUID))`.
- Fix: `fix/modsettings-write-guard` from master. Never write a `ModuleShortDesc` whose UUID is not a GUID or that belongs to a meta-less override, and read the file back with LSLib after writing. In `LoadModOrder`, force-allow only `IsForceLoaded && HasMetadata` (S6-C §6, S6-final §5).
- Confidence: Plausible. Steps 1, 3 and 4 are confirmed. Step 2 depends on whether Newtonsoft with a handled error returns null when deserialising a JSON array as `DivinityLoadOrder`, and needs test T-B1 on Windows. The direct route (an order JSON containing a path UUID) is confirmed.
- Source: S6-C §0 item 1, §2.4; S6-final §2.1-2.2; S6-A §5.3 item 5

### [Medium] HARV-53: One unparseable entry in modsettings.lsx reads as a total reset
- Location: DMDL:838-845; MWVM:2283-2292, 5420-5445
- What: `LoadModSettingsFileAsync` catches any LSLib exception and returns an empty result. Both reset detectors treat zero active mods as "reset externally".
- Failure scenario: A single bad GUID (HARV-52) or any LSX parse error makes the startup check offer to "restore" `LastExported`, and makes the watcher raise a false reset alarm. Neither says the file is unreadable.
- Evidence: `catch (Exception ex) { DivinityApp.Log($"Error reading '{path}':\n{ex}"); }`, then the code continues with a null resource.
- Fix: Tell "unreadable" apart from "empty", and alert with the parse error.
- Confidence: Confirmed
- Source: S6-C §2.2

### [Medium] HARV-54: LastExported is shared by all profiles, so the startup "restore" offers another profile's order
- Location: MWVM:2850-2858, 2291-2327
- What: The backup is one file, `Orders/LastExported.json`. `CheckForEmptyOrderAsync` offers it whenever the selected profile's modsettings has no active mods. On yes, it overwrites both `modSettingsOrder` and `SelectedModOrder` and exports to the game.
- Failure scenario: The user creates a second in-game profile (empty modsettings) and starts the manager. It asks "Use the last exported mod order?", and Yes writes profile 1's 935-mod order into profile 2.
- Evidence: `var lastExported = SavedModOrderList.FirstOrDefault(x => x.Name == DivinityApp.PATH_LAST_EXPORTED_NAME);`, with no profile key.
- Fix: Key LastExported by profile, or at least show which profile it came from (S6-final §6.4 direction).
- Confidence: Confirmed
- Source: S6-final §2.2 (A §7 F-6 rejection); LIB-C F5

### [Low] HARV-55: ForceAllowInLoadOrder stays on after switching orders, so a meta override drops into the Inactive list
- Location: MWVM:2093-2144; filter at :5244-5246
- What: `LoadModOrder` sets `ForceAllowInLoadOrder = true` for forced mods in the order but never clears it for mods that are not in the new order.
- Failure scenario: An order with an allowed meta override (K2) is loaded, then the user switches to an order without it. The override now shows in Inactive instead of the Overrides pane (`CanAddToLoadOrder` is still true), and it looks like an ordinary inactive mod.
- Evidence: No reset of `ForceAllowInLoadOrder` anywhere in `LoadModOrder`. It is set only at :2128.
- Fix: At the top of `LoadModOrder`, reset it for `IsForceLoaded && !IsForceLoadedMergedMod` (S6-A §5.2).
- Confidence: Confirmed for the code. The UI effect is Plausible and needs Windows.
- Source: S6-A §5.2

### [Low] HARV-56: Alt+Up/Down moves never update mod.Index
- Location: GUI/Controls/ModListView.cs:228-300; MWVM:5400-5407
- What: The keyboard move uses `list.Move` and never writes `Index`. `Order` follows through the CollectionChanged rebuild, but the "#" column does not. A Move also doesn't clear `HasExported`, because only Add, Remove and Reset do.
- Failure scenario: The user Alt+Up's a mod three times. The "#" column keeps showing old numbers until the next drop or load.
- Evidence: There is no `.Index =` in the move block. The CollectionChanged handler tests Add, Remove and Reset only.
- Fix: Renumber after the move with an O(n) loop (S6-B §9, `fix/alt-move-index`).
- Confidence: Plausible. The code is confirmed; the stale "#" needs to be seen in the app.
- Source: S6-B §9; S6-final §7 D10, §10

### [Info] HARV-57: The Index-change Order sort subscription never fires
- Location: MWVM:5256-5259
- What: `ActiveMods.WhenAnyPropertyChanged(nameof(DivinityModData.Index))` watches the collection's own PropertyChanged, which never raises "Index".
- Failure scenario: None. It is dead code that readers may wrongly rely on.
- Evidence: As cited.
- Fix: Remove it, or replace it with an explicit renumber.
- Confidence: Confirmed (by reading the DynamicData/ReactiveUI semantics, not by a run)
- Source: S6-final §2.2 (rejecting a B §2 claim)

### [Low] HARV-58: Quadratic renumbering on drop and in SortModOrder
- Location: GUI/ViewModels/ModListDropHandler.cs:218-221; MWVM:4581-4582
- What: Each drop runs `mod.Index = ActiveMods.IndexOf(mod)` over all active mods, which is O(n²). `SortModOrder` does `mods.Items.FirstOrDefault` per comparison, which is O(n² log n).
- Failure scenario: Every drop at 935 active rows costs about 437k comparisons.
- Evidence: As cited.
- Fix: Use a counter loop, plus a dictionary for the sort (S6-final `perf/drop-renumber`).
- Confidence: Confirmed
- Source: S6-A §8, §10; S6-B §4.3; S6-C §8

### [Low] HARV-59: Recycled list rows can keep a stale screen-reader name
- Location: GUI/Controls/Behavior/ScreenReaderHelperBehavior.cs:100-128; GUI/Views/HorizontalModLayout.xaml:120, 224 (`VirtualizationMode="Recycling"`)
- What: The behaviour sets `AutomationProperties.Name`/`HelpText` as local values, and only when they are non-empty.
- Failure scenario: A recycled container rebinds to a mod with an empty name or help text. Narrator or NVDA reads the previous mod's name.
- Evidence: As cited by S6-final, which verified it with [E].
- Fix: `ClearValue` before setting, or bind instead (S6-B §7.1).
- Confidence: Plausible. Needs Windows with a screen reader.
- Source: S6-final §2.1; S6-B §7, §12 B5

### [Info] HARV-60: The reset detectors cannot see a partial drop by the game
- Location: Core/Models/App/ModSettingsParseResults.cs:7-18; MWVM:5429
- What: The detectors fire only when the active count is 0 or less.
- Failure scenario: The game drops 3 bad entries and keeps 932. Nobody is told, and the user notices only in game.
- Evidence: `CountActive() <= 0`.
- Fix: Compare the re-read UUID sequence with the exported one.
- Confidence: Confirmed
- Source: S6-C §2.2

### [High] HARV-61: All three pak import paths truncate the destination in place; on cancel or error the installed mod is destroyed
- Location: Core/Util/DivinityFileUtils.cs:364 (via MWVM:1845 AddModFromFile); MWVM:3302 (ImportCompressedFileAsync); MWVM:3429 (ImportArchiveAsync)
- What: Each opens `Mods/<name>.pak` with `File.Create` and then streams into it. In ImportArchiveAsync, `success` is never reset per entry, so after one good pak a later failed copy still runs `LoadModDataFromPakAsync` on the truncated file.
- Failure scenario: The user imports a newer version of an installed mod and cancels, or the source zip is corrupt mid-entry. The existing working pak has already been truncated, and a partial pak stays in Mods, which the game will try to mount. Under the planned hardlink library, the same write would also corrupt the shared library copy.
- Evidence: `using var outputFile = File.Create(copyToPath, ...)`, `using var fs = File.Create(outputFilePath, 4096, ...)`, and `bool success = false;` set once before the loop and only ever set true.
- Fix: Write to `<dest>.tmp-<guid>` in the same folder, then `File.Move(tmp, dest, true)`, and delete the temp in `finally`. Reset `success` per entry (LIB-A §8 PR 0, `fix/import-write-temp-rename`).
- Confidence: Confirmed
- Source: LIB-final §1.1; LIB-A §8.1; LIB-B §2; LIB-C F7

### [Medium] HARV-62: Saving an order truncates the JSON file in place
- Location: DMDL:1098-1113
- What: `new FileStream(outputFilePath, FileMode.Create, ...)` followed by one write. It is not atomic.
- Failure scenario: A crash, power loss or full disk during Ctrl+S leaves an empty or partial order JSON. At the next start, `SafeDeserialize` fails and the order disappears.
- Evidence: As cited.
- Fix: Write a temp file, then rename (LIB-C §4, PR 4).
- Confidence: Confirmed
- Source: LIB-final §1.1; LIB-C F12

### [Medium] HARV-63: Duplicate mods with equal versions pick a random winner, and the loser is offered for deletion
- Location: DMDL:787-806; MWVM:1695-1708 → DeleteMods(..., true) :4359-4371
- What: A strict `>` on `Version64` means that on a tie the first item enumerated from a `ConcurrentBag` wins, which is effectively random. The other copies go to the delete-duplicates dialog.
- Failure scenario: The user has `Foo.pak` and a re-downloaded `Foo (1).pak` with the same Version64 but different contents. Either one may be kept, and the delete dialog may propose the newer file.
- Evidence: `if (m.Version.VersionInt > existing.Version.VersionInt) lastestDuplicates[m.UUID] = m;`
- Fix: Add a deterministic tie-break (for example newest LastWriteTime, then path) and show both candidates in the dialog (LIB-C §3.4).
- Confidence: Confirmed
- Source: LIB-final §1.1; LIB-C F6

### [Low] HARV-64: JunctionPoint.OpenReparsePoint checks GetLastWin32Error instead of the handle
- Location: Core/Util/JunctionPoint.cs:290-299
- What: After `CreateFile`, it throws whenever `Marshal.GetLastWin32Error() != 0`, without checking `IsInvalid`.
- Failure scenario: A successful `CreateFile` that leaves a non-zero last error throws "Unable to open reparse point". This path is used when resolving Steam library junctions (`DivinityRegistryHelper.cs:59-61`), so game-path detection could fail.
- Evidence: `if (Marshal.GetLastWin32Error() != 0) ThrowLastWin32Error(...)`
- Fix: `if (reparsePointHandle.IsInvalid) ThrowLastWin32Error(...)` (LIB-B PR1a).
- Confidence: Plausible. Needs Windows to see whether the last error is actually non-zero after a successful call.
- Source: LIB-final §1.1; LIB-B §2

### [Info] HARV-65: FindLoadOrderFilesInDirectoryAsync sets Name before its null check
- Location: DMDL:1167-1169
- What: `order.Name = ...` runs before `if (order != null)`.
- Failure scenario: An order JSON containing `null` or nothing throws a NullReferenceException. The per-file catch logs it, so the file is skipped without any message to the user.
- Evidence: As cited. The catch is at :1177-1180.
- Fix: Move the assignment inside the null check.
- Confidence: Confirmed
- Source: LIB-final §1.1 (side finding)

### [Info] HARV-66: DivinityLoadOrder.Clone() is shallow
- Location: Core/Models/DivinityLoadOrder.cs:304-312
- What: `Order = this.Order.ToList()` shares the `DivinityLoadOrderEntry` objects.
- Failure scenario: Changing `Missing` on a clone's entry also changes the original. It is harmless today, but a trap for pins or dividers.
- Evidence: As cited by LIB-final.
- Fix: Deep-copy entries with `entry.Clone()`.
- Confidence: Confirmed
- Source: LIB-final §1.1; LIB-C F3; LIB-A §4.1

### [Low] HARV-67: No single-instance guard
- Location: GUI/App.xaml.cs (no Mutex or IPC anywhere; grep)
- What: Two copies of the app can run at once against the same settings, orders and modsettings.lsx.
- Failure scenario: The user double-launches. Both instances save settings and orders, and the last writer wins. Exports can interleave.
- Evidence: Grep finds no `Mutex` or single-instance code.
- Fix: A named mutex, plus IPC to focus the existing window (S9 §2.3, LIB-C §11).
- Confidence: Confirmed
- Source: LIB-C F13; S9 §0

### [Low] HARV-68: The static _AllPaksNames is never cleared, so a part-named pak stays hidden after its base is removed
- Location: DMDL:429-446, 763
- What: `_AllPaksNames.UnionWith(...)` runs on every refresh, and nothing ever clears it.
- Failure scenario: `Foo.pak` is deleted and F5 is pressed. A real standalone mod named `Foo_1.pak` is still skipped as a "partial pak" until restart.
- Evidence: There is no `Clear()` anywhere. The only write is `UnionWith` at :763.
- Fix: Clear it at the start of each scan, or use a local set.
- Confidence: Confirmed
- Source: S4-A §5 item 6; S4-B §9

### [Medium] HARV-69: `@desc` and `@file` filters can throw on mods with no description or no file path
- Location: Core/Models/DivinityModFilterData.cs:23, 65, 70; Core/Models/DivinityBaseModData.cs:146-162
- What: `CompareInfo.IndexOf(val, ...)` throws `ArgumentNullException` when `val` is null. `Description` has no initialiser, and `FileName` is null without a `FilePath`.
- Failure scenario: The user types `@desc foo` with a description-less mod in the list. The exception escapes the throttled `Subscribe` (MWVM:5251/5254), and that list's filter may stop working for the session.
- Evidence: `return CultureInfo.CurrentCulture.CompareInfo.IndexOf(val, FilterValue, CompareOptions.IgnoreCase) >= 0;`
- Fix: `if (String.IsNullOrEmpty(val)) return false;` (S5-A B4, in `perf/filter`).
- Confidence: Plausible. Needs Windows to check whether loaded mods really have a null `Description` (the loader may assign `""`) and what happens to the subscription.
- Source: S5-A §2.7; S5-C §2.2 S6

### [Low] HARV-70: Filter text matches Name while the row shows DisplayName, and a half-typed `@prop` blanks the list
- Location: MWVM:4291, 4172; Core/Models/DivinityBaseModData.cs:77-80
- What: Plain search matches `Name`, but rows show `DisplayName`, which is the file name when `DisplayFileForName` is on. `@auth` with no trailing space doesn't match the property regex, so it becomes a literal Name phrase.
- Failure scenario: With "display file names" on, typing a visible file name finds nothing. While typing `@author`, the list goes empty.
- Evidence: As cited.
- Fix: Match what is displayed. Treat an unfinished `@prop` as no filter (S5-C §3).
- Confidence: Confirmed
- Source: S5-A §2.7; S5-C §2.2 S1, S5

### [Medium] HARV-71: Dropping into a column-sorted active list inserts at the view index, not the load-order index
- Location: GUI/Views/HorizontalModLayout.xaml:115 (header click sorts the active list), .xaml.cs:755-769; GUI/ViewModels/ModListDropHandler.cs:150-175
- What: gong's `UnfilteredInsertIndex` remaps only when `Items.Count != ItemsSource.Count`. A sorted but unfiltered view returns the view index unchanged.
- Failure scenario: The user sorts Active by Name and drags a mod onto row 10 of the sorted view. It is inserted at load-order position 10, which is next to an unrelated mod.
- Evidence: gong `DropInfo.cs:66`, as read by S5-C. The header-click handler adds a `SortDescription` to the default view.
- Fix: Map by item to the source index (S5-B `ResolveSourceInsertIndex`), or disable sorting on the active list.
- Confidence: Plausible. Needs Windows: sort Active by Name, drop, and check the "#" column.
- Source: S5-C §2.3; S5-B §5

### [Low] HARV-72: FocusSelectedItem throws on an empty filtered view
- Location: GUI/Views/HorizontalModLayout.xaml.cs:60-85 (GetItemAt at :67)
- What: `lv.Items.GetItemAt(0)` is called without a count guard. The surrounding try/catch only logs.
- Failure scenario: Focusing a list whose filter hides everything throws and logs, and focus does not move.
- Evidence: `var firstItem = lv.Items.GetItemAt(0);`
- Fix: Guard with `lv.Items.Count > 0`.
- Confidence: Plausible (it only affects views where `Items` is empty)
- Source: S5-B §5

### [Low] HARV-73: The screen-reader probe runs per mod and re-initialises Tolk when no reader is running
- Location: Core/Models/DivinityModData.cs:520-524; Core/DivinityApp.cs:124-127; Core/AppServices/ScreenReaderService.cs:24-31, 71-79
- What: `HasToolTip` calls `IsScreenReaderActive()` on each change of Description, HasDependencies or UUID. When no reader is loaded, `EnsureInit` calls `CrossSpeakManager.Initialize()` every time.
- Failure scenario: About 4,000 Tolk initialisations at startup for 1,100 mods. The cost is unknown, somewhere between 0.05 and 4 s.
- Evidence: `if (!CrossSpeakManager.Instance.IsLoaded()) { CrossSpeakManager.Instance.Initialize(); ... }`
- Fix: Memoise `IsScreenReaderActive()` for a few seconds (S4-C P4b).
- Confidence: Plausible. Needs Windows timing.
- Source: S4-C §1 S3

### [Info] HARV-74: Path.PathSeparator used where a directory separator was meant
- Location: MWVM:4739
- What: `path.Replace(Path.PathSeparator, '/')` replaces `;`, not `\`.
- Failure scenario: Harmless today according to S4-A. A builtin ignore path containing backslashes would not be normalised.
- Evidence: As cited.
- Fix: Use `Path.DirectorySeparatorChar` (or `'\\'`).
- Confidence: Confirmed
- Source: S4-A §5 item 6

### [Low] HARV-75: The scrollbar extent counts filtered-out rows
- Location: GUI/Themes/MainResourceDictionary.xaml:369 (Visibility setter on rows)
- What: The filter collapses rows through `Visibility`, so they stay in `Items`. Under item scrolling, the extent and wheel steps include them.
- Failure scenario: With a narrow filter, the scrollbar suggests hundreds of rows, and a wheel notch can land on collapsed rows and appear to do nothing.
- Evidence: As cited by S5-B.
- Fix: Filter through `ListCollectionView.Filter` (S5-B).
- Confidence: Plausible. Needs Windows.
- Source: S5-B §1

### [Info] HARV-76: Ctrl+A on a filtered list drags hidden mods too (REFUTED)
- Location: GUI/ViewModels/ModListDragHandler.cs:70-79; Core/Models/DivinityModData.cs:185-196
- What: S5-B claims that `SelectAll` selects collapsed rows and that gong drags `SelectedItems`, so hidden mods move.
- Failure scenario: Not reproducible from the code. The custom drag handler sets `dragInfo.Data` to `ActiveMods.Where(x => x.IsSelected && x.Visibility == Visibility.Visible)`, and the drop handler uses `ExtractData(dropInfo.Data)` (ModListDropHandler.cs:150). The model also refuses `IsSelected=true` on hidden mods.
- Evidence: As cited.
- Fix: None needed for drag. ListView-level `SelectedItems` may still include hidden containers visually.
- Confidence: Refuted. The drag payload is filtered by model selection and visibility. Run S5-B T-S4 on Windows only to close it out.
- Source: S5-B §1 ("Present-day bugs"), §10 T-S4

### [Info] HARV-77: Lazy drag payload is evaluated at drop time
- Location: GUI/ViewModels/ModListDragHandler.cs:72, 77; GUI/ViewModels/ModListDropHandler.cs:150
- What: `dragInfo.Data` is an unevaluated `Where`, which is only enumerated when the drop happens.
- Failure scenario: If selection or visibility changes during the drag (for example a throttled filter fires), the dropped set differs from what was picked up.
- Evidence: `var selected = _viewModel.ActiveMods.Where(...); dragInfo.Data = selected;`
- Fix: `.ToList()` in `StartDrag` (S6-A §9).
- Confidence: Plausible (it needs a filter to fire mid-drag)
- Source: S6-A §9 table; S6-final §2.1
