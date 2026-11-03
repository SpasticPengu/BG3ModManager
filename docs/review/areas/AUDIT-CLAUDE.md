# AUDIT-CLAUDE: CLAUDE.md checked against the source

Audited file: `scratchpad/notes/CLAUDE.md` (718 lines, the copy also on `origin/notes` at `9906953`).
Checked against: `/home/user/BG3ModManager` at `fc35358`, submodules `External/lslib` (2b88c0f) and `External/CrossSpeak` (58b7178), branches `origin/fix/publish-prebuild-quoting`, `origin/perf/list-render` and `origin/notes`, and the `upstream/avalonia` worktree.
Method: I checked each claim with grep, sed, git show/diff/ls-files and python against the tree. Nothing was built or run. The container clone is **shallow** (`.git/shallow`, 61 commits) and has no `upstream` remote, so history-depth claims are marked "Not checkable". No subagent tool was available in this session, so one agent did the whole audit, reading the code directly.
Line references are to `fc35358` unless stated otherwise. "MWVM" means `src/GUI/ViewModels/MainWindowViewModel.cs`.

## Summary

| Class | Count |
|---|---|
| Correct | 144 |
| Imprecise | 16 |
| Wrong | 6 (two are the same "22 ScheduleAsync" count stated twice) |
| Stale | 3 (1.4, 2.3b, and the S3 header date) |
| Not checkable here | 13 |
| **Total claims** | **182** (181 table rows plus S3; 9.3 is counted under 6.4) |

The five errors that matter most:
1. **§1 "zero custom commits" is stale.** `origin` now has `fix/publish-prebuild-quoting`, `perf/list-render` (13 commits, 9 of them `TEMP:`) and `notes`. An agent that believes this will cut branches from the wrong base or miss existing work.
2. **§6 IgnoredMods advice is wrong.** You don't need both lists. `IgnoreModDependency` already treats every `Mods` UUID as satisfied (`src/Core/Util/DivinityModDataLoader.cs:47-50`). The "wave of false missing-dependency reports" can't happen this way.
3. **§5 logging switches are wrong.** The `DebugModeEnabled` setting does not turn logging on. Only `LogEnabled`, the `debug` file (through the VM-only `DebugMode`) or an unhandled exception does.
4. **§4 "22 ScheduleAsync sites" is wrong.** There are 16 in MWVM and 17 in the repo. The command-style rules ("canExecute always named WhenAnyValue", "outputScheduler passed on UI-touching commands") are also wrong: one named `outputScheduler:` exists in the whole repo.
5. **§4 "eight clr-namespace declarations" in SettingsWindow.xaml is wrong.** It has 4. There are 24 across 10 XAML files, so an agent that greps only SettingsWindow after a namespace move misses 20.

There is also one serious **gap**: the [Reactive]-as-allow-list rule (which CLAUDE.md describes correctly) means `Settings.Window` and `Settings.Confirmations` are **never restored from disk**. See Gap 1.

## Claims

Class key: C = Correct, I = Imprecise, W = Wrong, S = Stale, N = Not checkable here.

### Header and §1 What this is / fork situation

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| H1 | Facts verified at `fc35358` | C | `git log -1 master` = fc35358 | |
| H2 | Roadmap lives in `implementation-plan.html` + `plan-overview.html` | I | Both files, and CLAUDE.md itself, exist only on the orphan branch `origin/notes` (`9906953`, no shared history with master). `git ls-tree origin/perf/list-render` has no CLAUDE.md. The plan's line "CLAUDE.md at the repo root" (implementation-plan.txt:61) is not true of any code branch. | Say where the docs live: "These three files live on the orphan `notes` branch, not in any code branch. Check it out in a separate worktree, or copy CLAUDE.md into your working tree without committing it to a `fix/*`/`perf/*` branch." |
| 1.1 | .NET 8, WPF, ReactiveUI, x64-only | C | GUI.csproj:4-5,31,59; Core.csproj:3,7-8 | |
| 1.2 | Both main projects `RootNamespace=DivinityModManager` | C | GUI.csproj:8; DivinityModManagerCore.csproj:6 | |
| 1.3 | `origin` and `upstream` remotes as listed | N | This container has only `origin` (`git remote -v`); `upstream/master` doesn't resolve | Add: "cloud/agent containers may lack the `upstream` remote and be shallow; run `git remote add upstream … && git fetch --unshallow upstream` before any upstream comparison." |
| 1.4 | "This fork currently has zero custom commits … `0 0`" | **S** | `git ls-remote origin`: `fix/publish-prebuild-quoting` (5f8a80a), `perf/list-render` (9f2b163), `notes` (9906953); `git log fc35358..origin/perf/list-render` = 13 commits | See replacement text R1 below |
| 1.5 | upstream/master last commit 2025-10-06 | C | `git log -1 --format=%ci fc35358` = 2025-10-06 | |
| 1.6 | upstream/avalonia last commit 2026-03-07 | C | `git log -1 upstream/avalonia` = 2026-03-07 | |
| 1.7 | avalonia 944 commits ahead | N | Shallow clone: only 30 avalonia commits present, no merge base | |
| 1.8 | Avalonia 12.0.0-preview1 | C | wt-avalonia/Directory.Packages.props:5 | |
| 1.9 | RxUI 20 + Fody vs RxUI 23 + source generators | C | GUI.csproj:59-60; wt-avalonia/Directory.Packages.props:33-34 (ReactiveUI 23.1.1, ReactiveUI.SourceGenerators 2.6.1) | |
| 1.10 | per-csproj packages vs central `Directory.Packages.props` | C | wt-avalonia/Directory.Packages.props exists; master has none | |
| 1.11 | Tests: none vs `src/Tests` | C | wt-avalonia/src/Tests/Tests.csproj | |
| 1.12 | Update.xml 1.0.12.9 vs 1.0.10.0 | C | Update.xml:3; wt-avalonia/Update.xml:3 | |
| 1.13 | avalonia README still says .NET Framework 4.7.2 | C | wt-avalonia/README.md:9 | |
| 1.14 | Both branches are quiet | C | 13 and 8 months since the last commit, as of 2026-11-03. The plan's "6 months stale" (implementation-plan.txt:74) is now 8 months | |
| 1.15 | `upstream/nexus-support` stats (465 files, ~26k insertions) | N | Branch not fetched here | |

### §2.1 Toolchain

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 2.1a | Toolchain table (VS 17.14.41, SDK 8.0.425, component state…) | N | Owner machine only | |
| 2.1b | `DivinityModManagerCore.csproj:20` references `LSLibNative.vcxproj` | C | Core.csproj:20 | |
| 2.1c | C++/CLI: granny2wrapper.cpp, lz4wrapper.cpp, `CLRSupport NetCore`, v143 | C | LSLibNative.vcxproj:19,36,139-140 | |
| 2.1d | `dotnet build` cannot build `.vcxproj` | N | No SDK in container (true in general) | |
| 2.1e | `LSLib.csproj:70-76` `PreBuildStoryGppgFiles`, no ContinueOnError | C | External/lslib/LSLib/LSLib.csproj:70-76 | |
| 2.1f | Generated `*.lex.cs`/`*.yy.cs` gitignored; `External/lslib/External/` ships only `.keep` | C | lslib/.gitignore:66-67; `git -C External/lslib ls-files External` = `External/.keep` | |
| 2.1g | The csproj is authoritative over the lslib README (`External\gplex\`) | C | LSLib.csproj:62-63 vs lslib/README.md:17-18 | Add: the csproj path is lower-case `..\external\gppg\binaries\` (LSLib.csproj:62-63), which only resolves on a case-insensitive file system. That matters for "Linux kept open". |
| 2.1h | MSBuildSDKsPath / workload resolver / DOTNET_HOST_PATH are dead ends | N | Owner machine experiment | |
| 2.1i | `-nodeReuse:false` | N | Machine behaviour | |

### §2.2 Build traps

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 2.2a | `GUI.csproj:25` OutputPath `$(SolutionDir)\bin\$(Configuration)\` | C | GUI.csproj:25 | |
| 2.2b | Core's ProjectReferences use `$(SolutionDir)External\...` | C | Core.csproj:20-22 (also GUI.csproj:44 → `$(SolutionDir)\src\Core\…`) | |
| 2.2c | Outside a .sln build output lands in `C:\bin\Debug\` | C | Follows from :25 with an empty SolutionDir (static) | |
| 2.2d | LSLibNative has only Debug/Editor Debug/Release x64 | C | LSLibNative.vcxproj:4,8,12 | |
| 2.2e | sln maps LSLibNative `Publish|x64`/`PublishTest|x64` → `Release|x64` | C | BG3ModManager.sln:135-140 | |
| 2.2f | That is why `Editor Debug` exists at all | N | Inference. Note the sln maps CrossSpeak `Editor Debug` → `Release|Any CPU` (sln:169-172) and LSLibNative → `Editor Debug|x64` (sln:129-132) | |
| 2.2g | `GUI.csproj:82` original `rd /s /q $(TargetDir)_Lib` | C | GUI.csproj:82 | |
| 2.2h | `$(TargetDir)` is empty because the property group is above the SDK targets import | C | GUI.csproj:81-83 sits in the project body; TargetDir comes from the common targets imported after it (static MSBuild evaluation order) | |
| 2.2i | `Microsoft.Common.CurrentVersion.targets:1426` uses `WorkingDirectory="$(OutDir)"`; marker-file experiment | N | No SDK here | |
| 2.2j | Fix on `fix/publish-prebuild-quoting` `5f8a80a9`, exact text | C | `git diff fc35358 origin/fix/publish-prebuild-quoting` (GUI.csproj:82), commit 5f8a80a945 | |
| 2.2k | CrossSpeak `GeneratePackageOnBuild=True`; nuspec hardcodes `bin\Debug\netstandard2.0`; sln maps Publish → `Release|Any CPU` | C | CrossSpeak.csproj:5,8; CrossSpeak.nuspec:31-32; sln:173-176 | |
| 2.2l | Nothing in the solution consumes the CrossSpeak package | C | Core.csproj:22 is a ProjectReference | |
| 2.2m | First build fails: generated parsers miss the evaluation-time `**/*.cs` glob | C (static) | LSLib.csproj:70-76 runs `Exec` in a target and adds no `Compile` item. The error text needs a run (N) | |
| 2.2n | Execs guarded by `!Exists(...)` | C | LSLib.csproj:71-75 | |
| 2.2o | `BG3ModManager.sln:102-104` Toolbox `Release|x64` has ActiveCfg but no Build.0 | I | Only sln:104 (`Release|x64.ActiveCfg`) lacks a Build.0. sln:102-103 (`Release|Any CPU`) does build Toolbox | "`BG3ModManager.sln:104` has `{2F2EF4FE…}.Release|x64.ActiveCfg` with no `.Build.0`; `Release|Any CPU` (:102-103) still builds it." |
| 2.2p | Toolbox builds in Debug, Publish, PublishTest | C | sln:86-101 (also Editor Debug) | |
| 2.2q | GUI shells out to `Tools\Toolbox.exe` | C | MWVM:424 | |
| 2.2r | Publish ships unoptimized GUI IL; Core/Toolbox/LSLib set Optimize unconditionally | C | GUI.csproj:38-41 (Debug only); Core.csproj:13; Toolbox.csproj:14; LSLib.csproj:16. The SDK default rule itself is N | |
| 2.2s | `DebugType=none` in Core `:12`, Toolbox `:13`, LSLib → no symbols outside Debug | I | Core.csproj:12, Toolbox.csproj:13 and LSLib.csproj:15 are right, but GUI.csproj never sets DebugType outside Debug, so `BG3ModManager.dll` gets a portable PDB in Release/Publish. Publish then deletes it (GUI.csproj:94,98) | "…no symbols for Core/Toolbox/LSLib outside Debug; the GUI gets a PDB in Release and Publish, but Publish's `SetupLibFolder` deletes it." |

### §2.3 Commands / §2.4 _Lib / §2.5 Version

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 2.3a | `dotnet restore` works; msbuild command lines | N | Needs Windows toolchain | |
| 2.3b | Comment "Release zip — … fix the PreBuildEvent first" | **S** | Contradicts §2.2 in the same file (the PreBuildEvent works, CLAUDE.md:107-120) | See R2 |
| 2.3c | PublishTest = Publish minus the zip | C | GUI.csproj:92 (both) vs :104 (Publish only) | |
| 2.4a | `SetupLibFolder` (`GUI.csproj:92-102`) moves every DLL/XML except BG3ModManager.dll/WinRT.Runtime.dll, deletes PDBs, removes ~30 culture folders + runtimes/_Logs/Data | C | GUI.csproj:94-101 (28 culture dirs + 3; also moves `runtimes\win-x64\native\WebView2Loader.dll`) | |
| 2.4b | Resolver in `Program.cs:13-29`, installed before WPF | C | Program.cs:13-29,40-41 | |
| 2.4c | `GUI.csproj:10` StartupObject | C | GUI.csproj:10 | |
| 2.4d | Managed only, `Assembly.LoadFile` | C | Program.cs:25 | |
| 2.4e | Native screen-reader DLLs `LoadLibrary`'d from `_Lib` (`ScreenReaderService.cs:49-70`) | C | ScreenReaderService.cs:49-70 | Add: `_Lib` exists in **every** configuration, because the `CrossSpeakLibraryFiles` target (GUI.csproj:85-90) copies those native DLLs there after every build |
| 2.4f | `RunBuildRelease` runs `python BuildRelease.py <ver>`; two zips; `*.zip` ignored | C | GUI.csproj:104-109; BuildRelease.py:22-23,59-60; .gitignore:338 | |
| 2.4g | `_Latest.zip` is the name Update.xml points at | C | Update.xml:4 | |
| 2.5a | Version bump = GUI.csproj:16, :17, app.manifest:3, Update.xml:3 | C | `git grep 1.0.12.9` returns exactly those four lines | |
| 2.5b | AutoUpdater compares Update.xml against the assembly version | C | MWVM:4907-4912 (`AutoUpdater.InstalledVersion = Assembly.GetExecutingAssembly().GetName().Version`) | |

### §3 Architecture

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 3.1 | GUI → Core → lslib/CrossSpeak; Toolbox references nothing | C | GUI.csproj:44; Core.csproj:20-22; Toolbox.csproj has no ProjectReference | |
| 3.2 | TFMs / Toolbox console → `bin/<Config>/Tools/` | C | Core.csproj:3-4; GUI.csproj:5; Toolbox.csproj:3-5 | |
| 3.3 | Toolbox verb string at `MainWindowViewModel.cs:~436` | C | MWVM:436 `UpdateScriptExtender -u "<DWrite>" -b "<exeDir>"`; Toolbox/Args/MainArgs.cs:10-11, ScriptExtenderUpdaterArgs.cs:7-11 | Add the invocation guards: runs only if the `ScriptExtender` feature is on, Toolbox.exe and DWrite.dll exist, and `ExtenderUpdaterSettings.UpdaterVersion >= 4`; it is killed after 120 s (MWVM:420,426,447-450) |
| 3.4 | Separate process because it LoadLibrary's DWrite.dll | C | Toolbox/ScriptExtender/Updater.cs:82; NativeMethods.cs:7-8 | |
| 3.5 | Only a `[Toolbox]` log line shows failure | C | MWVM:413-416,456 | |
| 3.6 | `Program.cs:38` Main; `args` unused | C | Program.cs:38-53 | |
| 3.7 | `App` ctor `:25` registers services, configures AutoUpdater | C | App.xaml.cs:25-57 | |
| 3.8 | `OnStartup` `:59` fades splash, shows MainWindow | C | App.xaml.cs:59-78 | |
| 3.9 | `MainWindow` ctor `:367` sequence | C | MainWindow.xaml.cs:367-411 | |
| 3.10 | `OnViewActivated` `:4105`: InitSettingsBindings → LoadSettings → LoadKeybindings → SaveSettings → RefreshAsync on taskpool | C | MWVM:4111,4126-4128,4145-4147 | |
| 3.11 | MainWindow.xaml 60 lines, MainViewControl.xaml 524 | C | `wc -l` | |
| 3.12 | No single-instance guard | C | No Mutex/instance check in src (only a commented `GetProcessesByName`, MWVM:5425) | |
| 3.13 | Two copies write `settings.json` "on a 250 ms debounce" | I | `QueueSave` (MWVM:1273-1278, 250 ms) is called only from window position/size handlers (MainWindow.xaml.cs:196,249). The other 19 `SaveSettings()` calls write immediately | "Two copies both write `Data/settings.json` — most saves are immediate `SaveSettings()` calls; only window-geometry saves go through the 250 ms `QueueSave` — last writer wins silently." |
| 3.14 | `Services.cs` ~30-line static facade over `Locator` | C | src/Core/Services.cs (32 lines) | |
| 3.15 | Registration sites: App.xaml.cs ×5, MWVM ctor `:4893`, SettingsWindow.Init | C | App.xaml.cs:27-50; MWVM:4893; SettingsWindow.xaml.cs:294 | |
| 3.16 | Later registrations return null earlier; consumers null-check | C | MWVM:798,819; Converters/ModExistsConverter.cs:14 | |
| 3.17 | Interfaces in `DivinityModManager`, impls in `…AppServices`, same file; only block-scoped namespaces | C | src/Core/AppServices/*.cs (3 files, the only non-file-scoped namespaces) | |
| 3.18 | MWVM is 5448 lines; almost no `#region` | C | `wc -l`; regions only at :257, :4965 | |
| 3.19 | Nav row "49–256 … `SourceCache` at `:80`" | I | `mods` SourceCache is at MWVM:82 | `:82` |
| 3.20 | Nav rows 329–760, 859–1111, 1112–1315, 1316–1491, 1609–1834, 2340–2557, 2558–3040, 4105–4166, 4178–4316, 4891–5448 | C | MWVM:418/563 (SE), 859, 1112/1245/1273, 1316, 1609/1727/1755, 2340, 2597/2841/2897, 4105, 4178/4199, 4891 | |
| 3.21 | RefreshAsync has 10 progress steps | C | MWVM:2344 `taskStepAmount = 1.0 / 10` | |
| 3.22 | Every ReactiveCommand, every hotkey action, every DynamicData filter and the modsettings watcher live in the ctor | I | Commands: yes (no `ReactiveCommand.Create` before :4891). Hotkey actions: 5 of 30 `.AddAction` calls are in `InitSettingsBindings` (e.g. MWVM:881). Watcher: MWVM:5427 | "…every ReactiveCommand, most hotkey actions (a few, such as OpenLogsFolder, are bound in `InitSettingsBindings`), every DynamicData filter…" |

### §4 ReactiveUI / MVVM house rules

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 4.1 | Core and GUI both have `FodyWeavers.xml`; Core is the heavier user | C | src/Core/FodyWeavers.xml, src/GUI/FodyWeavers.xml; `[Reactive]` 195 Core / 78 GUI; `[ObservableAsProperty]` 34 Core / **0** GUI | |
| 4.2 | Pinned ReactiveUI.Fody 19.5.41 + ReactiveUI 20.1.63 | C | GUI.csproj:59-61; Core.csproj:31-32 | |
| 4.3 | `ReactiveExtensions.cs:17-36` helpers: `ToUIProperty` defers, `ToUIPropertyImmediate` doesn't, both on MainThreadScheduler | C | ReactiveExtensions.cs:17-20,33-36 | |
| 4.4 | "a deferred OAPH has no value until something subscribes" | I | With `deferSubscription: true` the OAPH subscribes to its source on the first `.Value` read and returns `initialValue` until the source emits through MainThreadScheduler. Both helpers call `ToPropertyEx` (:19,:35), so they work **only** on Fody-woven `[ObservableAsProperty]` properties | "A deferred OAPH doesn't subscribe to its source until the first `.Value` read, and returns `initialValue` until then. Both helpers wrap `ToPropertyEx`, so they need a Fody-woven `[ObservableAsProperty] T Prop { get; }`; they can't back a hand-written `_field` OAPH." |
| 4.5 | MWVM predates the helpers and uses raw `_field` + `.ToProperty`; new code should use the helpers | I | MWVM: 21 `.ToProperty(`, 0 helpers. But **all** of GUI uses raw ToProperty (45 sites, 0 `[ObservableAsProperty]`), and the helpers appear in only 2 Core files (DivinityModData.cs, DivinityModManagerSettings.cs); Core has 26 raw sites | State the real split: helpers in Core models only; GUI is uniformly raw `ToProperty`. Match the file you're in |
| 4.6 | `ReactiveCommand.Create` ×51, `CreateFromTask` ×2, `CreateFromObservable` ×1, no `CreateRunInBackground` | C | repo-wide grep: 51/2/1/0 | |
| 4.7 | Hand-off via `ScheduleAsync` "(22 sites)" | **W** | 16 in MWVM (15 Taskpool + MainThreadScheduler at :2594), 1 in SettingsWindowViewModel.cs:230 = 17 | See R3 |
| 4.8 | `canExecute` is always a named `IObservable<bool>` from `WhenAnyValue`; `outputScheduler: RxApp.MainThreadScheduler` passed explicitly on UI-touching commands | **W** | No canExecute: MWVM:4977,4997,5106,5369. Inline: BaseProgressViewModel.cs:63, DeleteFilesViewData.cs:145, BaseHistoryViewModel.cs:92-100. Only one named `outputScheduler:` (MWVM:4926) and 5 positional MainThreadScheduler args in the repo | See R4 |
| 4.9 | `BaseProgressViewModel` → override `Task<bool> Run(CancellationToken)` | C | BaseProgressViewModel.cs:43,59 | |
| 4.10 | Zero `.ThrownExceptions` subscriptions | C | grep = 0 | |
| 4.11 | `RxExceptionHandler` at `App.xaml.cs:54` (non-DEBUG), overwritten at `MWVM:4903` | C | App.xaml.cs:51-56; MWVM:4902-4903 | |
| 4.12 | Throwing command shows the user nothing — `OnNext` only logs | C | MainWindowExceptionHandler.cs:14-19 (its `OnError`, :21-35, does show an alert) | |
| 4.13 | Views built at `:5239-5246` with the quoted lines | C | MWVM:5239-5244 | |
| 4.14 | `modsConnection.Publish()` at `:5240` is dead | C | MWVM:5240 (result discarded) | |
| 4.15 | ActiveMods/InactiveMods are plain `ObservableCollectionExtended` rebuilt at `:2141-2144` | C | MWVM:161-165,2141-2144 | |
| 4.16 | Search sets `Visibility` per item; the container hides itself; `IsSelected` refuses `true` when not Visible | C | MWVM:4199+ (e.g. relative lines 79-84); Themes/MainResourceDictionary.xaml:369; Core/Models/DivinityModData.cs:185-196 | |
| 4.17 | Not a single `Dispatcher.Invoke` in `src/` | C | grep `Dispatcher.` in src = 0 | |
| 4.18 | Fody `[Reactive]` setter raises PropertyChanged on the calling thread | C | Fody weaving semantics (`RaiseAndSetIfChanged`) | |
| 4.19 | fc35358 wrapped `LoadSettings()` in `Observable.Start(…, MainThreadScheduler)` in RefreshAsync | C | `git show fc35358` (MWVM:2362-2368) | |
| 4.20 | "22 `ScheduleAsync` sites in MainWindowViewModel" (repeat) | **W** | 16 (see 4.7) | See R3 |
| 4.21 | `[DataMember]` = persisted + dirty flag through `InitSubscriptions()` | C | DivinityModManagerSettings.cs:16,180-191 (the dirty flag is set only while `SettingsWindowIsOpen`, :190) | |
| 4.22 | `[Reactive]` is the allow-list for `SetFrom<T, ReactiveAttribute>` | C | MWVM:1127; Core/Extensions/ModelExtensions.cs:44-57 | |
| 4.23 | …(implied for all settings) | I | `ExtenderSettings`/`ExtenderUpdaterSettings` are restored through the **untyped** `SetFrom<T>` (MWVM:1128-1129; ModelExtensions.cs:29-42), which copies every non-null TypeDescriptor property whether or not it has `[Reactive]` | Add: "For `ExtenderSettings`/`ExtenderUpdaterSettings` the untyped `SetFrom<T>` copies every non-null property; `[Reactive]` is not a filter there." |
| 4.24 | `[SettingsEntry]` → Preferences generated by reflection | C | SettingsWindow.xaml.cs:101-107,329-331 (excludes `HideFromUI`; DebugMode/LogEnabled checkboxes are hand-placed in XAML, SettingsWindow.xaml.cs:336-337) | |
| 4.25 | `SetFrom` skips null values | C | ModelExtensions.cs:35,51 | |
| 4.26 | Never reassign ExtenderSettings/ExtenderUpdaterSettings/Window/Confirmations; bug `7b57e5a` | C | `git show 7b57e5a` "Fix settings getting replaced with new instances" | See Gap 1: Window/Confirmations are never restored at all |
| 4.27 | `[IgnoreSetFrom]` exists but nothing reads it | C | Only DivinityModManagerSettings.cs:90-91 and Attributes/IgnoreSetFromAttribute.cs:8 | |
| 4.28 | The real exclusion is omitting `[Reactive]` | C | ModelExtensions.cs:47 | |
| 4.29 | Menu bar generated by reflection from `[MenuSettings]` Hotkeys in `AppKeys.cs` | C | MainViewControl.xaml.cs:73-78; AppKeys.cs:241 | |
| 4.30 | Every reactive view declares a non-generic shim | C | 10 shims, e.g. ModUpdatesLayout.xaml.cs:14, SettingsWindow.xaml.cs:26 | |
| 4.31 | House style `d(binding)`, not `.DisposeWith(d)` | C | 76 `d(` vs 3 `DisposeWith`, all 3 in MWVM | |
| 4.32 | Windows derive from `HideWindowBase<T>` and cancel Closing / `Hide()` | I | 5 windows do (HideWindowBase.cs:42,60-63). `MainWindow` derives directly from `AdonisWindow` (MainWindow.xaml.cs:26) and really closes | "Secondary windows derive from `HideWindowBase<T>`…; `MainWindow` does not." |
| 4.33 | `SettingsWindow.xaml` carries **eight** `clr-namespace:…;assembly=DivinityModManagerCore` declarations | **W** | 4 (SettingsWindow.xaml:10,12,13,16). 24 across 10 XAML files; Themes/MainResourceDictionary.xaml has 7 | See R5 |

### §5 Code style

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 5.1 | `.editorconfig` has only `[*.cs]` and `[*.{cs,vb}]` | C | .editorconfig:5,234 | |
| 5.2 | Hard tabs, size 4 | C | .editorconfig:9-11; 179/185 .cs tab-indented, 0 space-indented | |
| 5.3 | CRLF | I | .editorconfig:14 says crlf, but the **committed blobs** are LF: `git ls-files --eol '*.cs'` = 168 `i/lf`, 6 `i/crlf`, 11 `i/mixed`; XAML 21 lf / 5 crlf. CRLF appears only through Git for Windows `core.autocrlf`. `.gitattributes` has no `text=` rule | "CRLF in the Windows working tree (via `core.autocrlf=true`); the repo stores LF for most files. Don't commit CRLF into the index — on Linux or with autocrlf off you'll see LF." |
| 5.4 | `insert_final_newline = false` | C | .editorconfig:15 | |
| 5.5 | Most files have a UTF-8 BOM | C | 205/208 `src` .cs+.xaml files | |
| 5.6 | Allman braces | C | .editorconfig:152 | |
| 5.7 | File-scoped namespaces; usings outside | C | .editorconfig:114,135 (both only `:silent`) | |
| 5.8 | `System.*` usings last | C | .editorconfig:22 | |
| 5.9 | `var` everywhere | C | .editorconfig:80-82 | |
| 5.10 | 45 surviving `if(` | C | grep `\bif\(` = 45 | |
| 5.11 | Private fields mix `_x` and `x` | C | 202 underscore vs 58 bare | |
| 5.12 | `String.` and `string.` both used heavily | C | 191 / 70 | |
| 5.13 | Zero primary constructors despite the suggestion | C | 0 matches; .editorconfig:116 | |
| 5.14 | Post-port types are unprefixed (ProcessHelper, ModRegistryService, ModuleShortDesc, ConfirmationSettings, TempFile) | C | Core/Util/ProcessHelper.cs, Core/AppServices/ModRegistryService.cs, Core/Models/ModuleShortDesc.cs, Core/Models/App/ConfirmationSettings.cs, Core/Util/TempFile.cs | |
| 5.15 | Commit `c2079ac5` renamed `DivinityModDependencyData` → `ModuleShortDesc` | N | Commit absent from shallow clone. Consistent: 0 refs to the old name | |
| 5.16 | Nullable/ImplicitUsings: Core off/off, GUI off/off, Toolbox enable/enable | C | Neither property in GUI/Core csproj; Toolbox.csproj:6-7 | |
| 5.17 | "Do not write `string?` in Core or GUI — it yields CS8632" | I | CS8632 is a warning, not an error, and Core already has 5 (Core/Util/WebHelper.cs:11-16) | "…it yields warning CS8632 (Core already has five, in WebHelper.cs); don't add more." |
| 5.18 | `RxCommandUnit` alias | C | Core/Usings.cs, GUI/Usings.cs | |
| 5.19 | Core aliases bare `[JsonIgnore]`/`[JsonConverter]` to Newtonsoft while importing System.Text.Json globally; GUI has no aliases | C | Core/Usings.cs (last 2 lines, plus `global using System.Text.Json(.Serialization)`). Note MWVM:39 imports `System.Text.Json.Serialization` locally | |
| 5.20 | Persistence is Newtonsoft everywhere | C | No `JsonSerializer.*` call in src | |
| 5.21 | DynamicData not globally imported | C | Neither Usings.cs; 16 files `using DynamicData;` | |
| 5.22 | Five `Directory.build.props`, only obj redirection | C | src/{GUI,Core,Toolbox}, lslib/{LSLib,LSLibNative} | |
| 5.23 | `DivinityApp.cs:119` Log = `Trace.WriteLine`, caller info filled in | C | DivinityApp.cs:119-122 | |
| 5.24 | Logging off by default; listener only on `ToggleLogging(true)` | C | MainWindow.xaml.cs:71-94 | |
| 5.25 | Enable via `LogEnabled`, **`DebugModeEnabled`**, or the `debug` file | **W** | `ToggleLogging` checks `ViewModel.DebugMode` (MainWindow.xaml.cs:73,88), a plain VM property (MWVM:325) set only when the `debug` file enabled logging (MainWindow.xaml.cs:380-383,440-443). `Settings.DebugModeEnabled` drives only developer UI (DivinityModManagerSettings.cs:215; MWVM:4949) | See R6 |
| 5.26 | Logs in `<exe>/_Logs/`; LogTraceListener scrubs three env paths | C | MainWindow.xaml.cs:372; Util/LogTraceListener.cs:19-21 | |
| 5.27 | `ShowAlert` already logs, scrubs paths, marshals to the UI thread | C | MWVM:4317-4341 (`SettingsWindowViewModel.ShowAlert`, :120-122, logs but does **not** scrub paths) | |
| 5.28 | timeout 0 = until replaced | C | Controls/AlertBar.xaml.cs:157,275-276 | |
| 5.29 | From Core: `DivinityApp.Commands.ViewModel.ShowAlert(...)` | C | Core/Util/DivinityGlobalCommands.cs:13; Core/ViewModels/IDivinityAppViewModel.cs:21 | |

### §6 BG3 domain

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 6.1 | modsettings path / `.pak` identification / three identity fields | C | Domain + DivinityModDataLoader.cs (Folder-based override detection) | |
| 6.2 | Override paks without meta.lsx get `UUID = <full pak path>`; `HasInvalidUUID` exists | C | DivinityModDataLoader.cs:620; DivinityModData.cs:168 | |
| 6.3 | `DivinityApp.cs:44-47` campaign UUIDs; everything else in IgnoredMods.json | C | DivinityApp.cs:44-47 | |
| 6.4 | Add new Larian modules to **both** `Mods` and `IgnoreDependencies`, or get false "missing dependency" reports | **W** | `IgnoreModDependency` = `IgnoredDependencyMods.Contains(uuid) \|\| IgnoreMod(uuid)` (DivinityModDataLoader.cs:47-50), and `IgnoreMod` looks up `DivinityApp.IgnoredMods` (the `Mods` array, :42-45). Every missing-dep check uses it (MWVM:765,1527,2735). Engine/Game/MainUI/CrossplayUI are already only in `Mods` (16 of the 20 are in both) | See R7 |
| 6.5 | A new campaign needs MAIN_CAMPAIGN_UUID + `ModType="Adventure"` special case + export fallback chain | C | DivinityModDataLoader.cs:239-241; MWVM:2907,5171-5176 | |
| 6.6 | Version packing `(Major<<55)+(Minor<<47)+(Revision<<31)+Build`, ~3.6e16 | C | Core/Models/DivinityModVersion2.cs:31,47-49; Gustav = 36029301684370529 | |
| 6.7 | Entries replaced only when the incoming version is strictly higher | C | MWVM:4799 (`existing.Version < mod.Version`) | |
| 6.8 | Runtime reads `<exe>/Resources`; editing source needs a rebuild | C | MWVM:4693; GUI.csproj:71-73 (`CopyToOutputDirectory Always`) | |
| 6.9 | Loader is 1826 lines; entry points named | C | DivinityModDataLoader.cs:397,745,831,1431,1471,1560,1783 | |
| 6.10 | modsettings.lsx read with `ByteSwapGuids=false`, everything else true | C | DivinityModDataLoader.cs:35-37,840 | |
| 6.11 | `XML_MODULE_SHORT_DESC` order Folder{0} MD5{1} Name{2} PublishHandle{5} UUID{3} Version64{4} | C | DivinityApp.cs:33; args match at DivinityModDataLoader.cs:1482 | |
| 6.12 | Force-loaded mods not draggable unless `IsForceLoadedMergedMod`/`ForceAllowInLoadOrder` | C | DivinityModData.cs:381-384 | |
| 6.13 | `Models/Conflicts/*` unreferenced | C | 0 external refs to DivinityConflictEntryData/Group | |
| 6.14 | `DivinityModVersion` (v1), `DivinityGameMasterCampaign` dead | C | Each referenced only in its own file | |
| 6.15 | `VerticalModLayout`, `LeaderLibSettingsWindow` never instantiated | C | Only self-references | |
| 6.16 | Mod Updates view dead: `ModUpdatesViewData.Mods` never populated; refresh call commented out in RefreshAsync | C | MWVM:2547; ModUpdatesViewData.cs:31 (only `Clear()`). `RefreshModUpdatesCommand` (MWVM:4997-5001) can still run the background refresh | |
| 6.17 | `GetAppDirectory()` dead statement at `DivinityApp.cs:132`; every settings/log/Orders path flows through it | I | The discarded `Path.GetFullPath(...)` is at DivinityApp.cs:**135**. Exceptions: Program.cs:40 (`_Lib`), MainWindow.xaml.cs:380 (`debug` file), MWVM:2608 use `AppDomain.CurrentDomain.BaseDirectory` directly | "`DivinityApp.cs:135`… Almost every path (30 call sites) flows through it; `_Lib`, the `debug` marker and one export fallback (MWVM:2608) use `AppDomain.CurrentDomain.BaseDirectory` directly." |

### §7 Integrations

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 7.1 | AppFeatures.json = `{ScriptExtender:true, NexusMods:false, Workshop:false}`; `"Workshop"` never queried | C | Resources/AppFeatures.json; `FeatureEnabled(` callers only use ScriptExtender/NexusMods (MWVM:420,1142,1481,2535,2781) | |
| 7.2 | Nexus code ships but is disabled | I | The feature flag is off, but a non-empty `NexusModsAPIKey` still calls `NexusModsDataLoader.Init` whatever the flag says (MWVM:1056-1066) | Add that caveat |
| 7.3 | API key `HideFromUI`, plaintext in `Data/settings.json`, set by hand | C | DivinityModManagerSettings.cs:29-31 | |
| 7.4 | Release path deletes `Data/` twice | C | GUI.csproj:101; BuildRelease.py:55 | |
| 7.5 | GitHub updates: `GithubData` initialiser commented at `DivinityModData.cs:393`; `ModUpdateHandler.cs:112` unguarded; cache handler `return false;` | C | DivinityModData.cs:238,393; ModUpdateHandler.cs:108-113; Cache/GithubModsCacheHandler.cs:28-31 | |
| 7.6 | Steam workshop dead; `Gameloop.Vdf` live for library discovery | C | Core/Util/DivinityRegistryHelper.cs:1-2,170 | |
| 7.7 | Script Extender: DWrite.dll = updater; BG3ScriptExtender.dll in LocalAppData | C | DivinityApp.cs:56-59 | |
| 7.8 | Writes ScriptExtenderSettings.json / UpdaterConfig.json to game bin; non-defaults only by default | C | MWVM:841-846; SettingsWindowViewModel.cs:273-296; `ExportDefaultExtenderSettings` has no DefaultValue (false), ScriptExtenderSettings.cs:18-21 | |
| 7.9 | `WebHelper` swallows **every** exception | I | Only `DownloadFileAsStreamAsync`/`DownloadUrlAsStringAsync` swallow (WebHelper.cs:18-44, cancellation included). The `GetAsync`/`PostAsync` wrappers (:11-16) let exceptions through | "`WebHelper.Download*` swallow every exception (including cancellation)…" |

### §8 Fork practice

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 8.1 | `DivinityApp.cs:22` URL_UPDATE → LaughingLeader | C | DivinityApp.cs:22 | |
| 8.2 | Repoint `:19-30` (URL_UPDATE, URL_CHANGELOG_RAW live) + Update.xml; GITHUB_* never referenced | C | AppUpdateWindowViewModel.cs:58,113; 0 refs to GITHUB_USER/REPO/RELEASE_ASSET | |
| 8.3 | `CheckForUpdates=false` is enough for personal use | C | Only automatic path is MWVM:2541-2544 (manual F7 at :5316-5324 remains) | |
| 8.4 | README "only official source", ko-fi, MIT © 2019 LaughingLeader | C | README.md:6,63; LICENSE:3 | |
| 8.5 | 21 files `merge=ours` incl. the seven named | C | .gitattributes (21 lines; patterns are basenames, so they match at any depth) | |
| 8.6 | `ours` is not a built-in driver; inert without config | C | git docs (built-ins: text, binary, union); `git config --get merge.ours.driver` empty here | |
| 8.7 | Submodules are LaughingLeader forks | C | .gitmodules | |
| 8.8 | `.github/` only ISSUE_TEMPLATE; no workflows on either upstream branch | C | `ls -R .github` on master and wt-avalonia | |
| 8.9 | CI recipe (checkout@v4, setup-msbuild@v2, setup-dotnet@v4) | N | Versions not checkable offline. Newer majors (e.g. checkout v5) existed by 2026; pin whatever is current | |
| 8.10 | CI obstacles: tools not checked in, build twice/Debug first, no tests | C | Consistent with 2.1f, 2.2k, 2.2m | |
| 8.11 | Submodules public, no credentials needed | N | | |

### §9 Gotcha index

| # | Claim | Class | Evidence | Correction |
|---|---|---|---|---|
| 9.1 | 12 rows that restate §2/§4/§6/§8 claims judged C above | C | as above | |
| 9.2 | "Script Extender update does nothing → Release\|x64 or GUI-only" | I | Also: `UpdaterVersion < 4`, DWrite.dll missing, feature flag off, 120 s kill (MWVM:420-450) | Add "or `ExtenderUpdaterSettings.UpdaterVersion < 4` / no DWrite.dll (MWVM:426)" |
| 9.3 | "Mass missing dependency → new module missing from `IgnoreDependencies`" | **W**-derived (counted under 6.4) | DivinityModDataLoader.cs:47-50 | Row text in R7 |
| 9.4 | "Command throws, user sees nothing" row | C | 4.10-4.12 | |

(9.1 counts as one claim; 9.3 is not counted twice.)

Related staleness outside CLAUDE.md, not counted above:
- plan-overview.txt:248-249 "Two commits sit on fix/publish-prebuild-quoting, unpushed." Now pushed (`git ls-remote origin` → 5f8a80a).
- implementation-plan.txt:1065 "Nothing is pushed." `perf/list-render` is on origin (9f2b163).
- plan-overview "Branches: master · fork/main · fix/*". `fork/main` does not exist on origin.

The 3rd Stale item is **S3**: header, CLAUDE.md:3-4, "verified against the tree at fc35358 (2026-09-18/20)". Sections §2.1/§2.2 were re-verified on 2026-09-23 against branches beyond fc35358, so the header date range is out of date. Use: "Facts were verified against `fc35358` (2026-09-18/23); §2.1–2.2 also cover `fix/publish-prebuild-quoting`. Audited again 2026-11-03 (AUDIT-CLAUDE)."

### Exact replacement text for Wrong / Stale items

**R1 (1.4, replaces CLAUDE.md:24-25):**
> **`master` carries no fork commits** — it equals `upstream/master` at `fc35358`. Fork work lives on branches pushed to `origin`: `fix/publish-prebuild-quoting` (`f6633a8`, `5f8a80a9`), `perf/list-render` (13 commits on `fc35358`, including `TEMP:` instrumentation commits that must be dropped before any upstream PR), and the orphan `notes` branch (`9906953`: this file and the plan). `fork/main` has not been pushed. List them with `git branch -r` and `git log --oneline master..origin/<branch>`. Everything below describes upstream's code as inherited unless it names a branch.

**R2 (2.3b, replaces the comment on CLAUDE.md:198):**
> `# Release zip — REQUIRES MSBuild AND python on PATH; on a clean checkout build Debug once first (CrossSpeak pack trap, §2.2)`

**R3 (4.7 and 4.20, replaces "(22 sites)" on CLAUDE.md:337 and the sentence on CLAUDE.md:387):**
> …hands off via `RxApp.TaskpoolScheduler.ScheduleAsync(...)` (15 sites in `MainWindowViewModel`, plus one `MainThreadScheduler.ScheduleAsync` at `:2594` and one in `SettingsWindowViewModel.cs:230`).
>
> There are 15 `TaskpoolScheduler.ScheduleAsync` sites in `MainWindowViewModel` where the same hazard lurks.

**R4 (4.8, replaces CLAUDE.md:338-339):**
> `canExecute`, when present, is usually a named `IObservable<bool>` built with `WhenAnyValue`, but many commands have none (`RefreshCommand` `:4977`, `SaveSettingsSilentlyCommand` `:5369`) and some pass inline observables (`BaseProgressViewModel.cs:63`, `BaseHistoryViewModel.cs:92`). An explicit output scheduler is rare — one named `outputScheduler:` (`:4926`) and five positional `RxApp.MainThreadScheduler` arguments in the whole repo — so pass `RxApp.MainThreadScheduler` yourself whenever a command's subscribers touch the UI.

**R5 (4.33, replaces CLAUDE.md:425-427):**
> **Moving a Core type between namespaces breaks XAML silently.** `src/GUI` has 24 `clr-namespace:...;assembly=DivinityModManagerCore` declarations across 10 XAML files — 7 in `Themes/MainResourceDictionary.xaml`, 4 in `SettingsWindow.xaml`. Grep `src/GUI/**/*.xaml` after any namespace change.

**R6 (5.25, replaces "Enable via … next to the exe" on CLAUDE.md:486-487):**
> Enable it with the `LogEnabled` setting, or by dropping an extension-less file named `debug` next to the exe. The file also sets the VM-only `MainWindowViewModel.DebugMode` (`:325`), which keeps logging on even when `LogEnabled` is false. The `DebugModeEnabled` setting does **not** enable logging; it only shows developer UI. Any unhandled exception or `DisplayError` also switches logging on (`MainWindow.xaml.cs:99,130,154`).

**R7 (6.4, replaces step 2 on CLAUDE.md:528-529, and the §9 row on :715):**
> 2. Add any new Larian module to the `Mods` array. `DivinityModDataLoader.IgnoreModDependency` (`:47-50`) already treats every `Mods` UUID as a satisfied dependency, so `IgnoreDependencies` is only needed for UUIDs that are *not* in `Mods`. Upstream lists 16 of its 20 modules in both (Engine, Game, MainUI and CrossplayUI are only in `Mods`); copying that convention is harmless and keeps diffs against upstream small.
>
> `| Mass "missing dependency" after a patch | new Larian module missing from IgnoredMods.json `Mods` (§6) |`

## Gaps

These are facts a future agent working on the plan's stages would need and CLAUDE.md doesn't state. They are ranked by how much trouble their absence would cause.

1. **`Settings.Window` and `Settings.Confirmations` are never restored from `settings.json`.** Both are `[DataMember]` without `[Reactive]` (DivinityModManagerSettings.cs:112,122). `LoadSettings` copies only `[Reactive]` properties (MWVM:1127) and has no separate restore for them, unlike the two extender objects (:1128-1129). So "Save Window Location" saves (MainWindow.xaml.cs:179-196) and then applies in-memory defaults (MWVM:4132), and `DisableAdminModeWarning` (MWVM:4150-4160) is forgotten on every restart. Static evidence only; confirm with a Windows run. It is a ready-made upstreamable `fix/*`, and anyone adding a nested settings object will repeat the bug.
2. **CLAUDE.md is not in any code branch.** It is on the orphan `notes` branch. Agents launched in a `fix/*`/`perf/*` worktree won't auto-load it, and committing it there would contaminate an upstream PR. Say how to get it (a separate worktree or an uncommitted copy) and that it must never land on `fix/*`/`perf/*`/`chore/*`.
3. **State of `perf/list-render`.** 13 commits, 9 `TEMP:`. `PerfProbe.cs`, `PerfScrollDriver.cs` and five `Probe*` controls are instrumentation. An empty `perfscroll` file next to the exe makes the app scroll itself and exit (plan-overview "What to verify"). The keepers are `2858a23`, `7fa8347`, `396d7b2` and `b123aff`, and `309b81e` is entangled with TEMP logging. Without this an agent will upstream probes or lose the real fixes.
4. **Repo containers are shallow and have no `upstream` remote.** The `.git/shallow` clone (61 commits) and `git remote -v` showing only origin break the §1 command, the §8.3 sync recipe and the commit SHAs cited in §5 (`c2079ac5` is absent). Add `git remote add upstream …; git fetch --unshallow`.
5. **Line endings in the index are LF.** 168/185 `.cs` files are `i/lf` and `.gitattributes` has no `text` rule, so commits made from Linux or with autocrlf off stay LF, and CRLF commits create noisy diffs against upstream. (The perf branch's "UTF-8 BOM + CRLF" commit `e38c87b` actually stored LF: `git ls-files --eol` shows PerfProbe.cs as `i/lf`.)
6. **The OAPH helpers need Fody `[ObservableAsProperty]`.** `ToUIProperty*` call `ToPropertyEx` (ReactiveExtensions.cs:19,35). GUI has zero `[ObservableAsProperty]` properties and 45 raw `ToProperty` calls. Following "new code should use the helpers" in a GUI `_field`-style VM won't compile as intended. This also ties the code to Fody, which Avalonia replaced with source generators (relevant to "Linux kept open").
7. **The Stage 5 filter mechanics in code terms.** The 500 ms `Throttle` is at MWVM:5250,5253. `ModListDropHandler.UnfilteredInsertIndex` (ModListDropHandler.cs:23,57,133) depends on visibility-filtering. Virtualization is `Recycling` (HorizontalModLayout.xaml:119-120,223-224). The plan has this; CLAUDE.md, the "repo reference", doesn't.
8. **The grouping landmine.** `Themes/MainResourceDictionary.xaml:460` `IsGrouping=True` → `CanContentScroll=False`, which kills virtualization. Categories must be flat divider rows. It's a repo fact, but it's only in the plan.
9. **How settings are serialized.** `settings.json` is written with `DefaultValueHandling.IgnoreAndPopulate` (MWVM:1235), so a value equal to its `[DefaultValue]` is omitted and re-populated on load. The constructor calls `SetToDefault()` (DivinityModManagerSettings.cs:237). §7 says this only for the extender files. It matters for Stage 1 (new fork settings) and for any settings fixture.
10. **Most settings writes are immediate.** Of 19 `SaveSettings()` calls, only window geometry goes through `QueueSave`. `QueueSave` returns early while `!IsInitialized && IsRefreshing` (MWVM:1276). This matters for single-instance or sync work (Stage 9) and for perf traces of startup, since `OnViewActivated` saves before refresh (MWVM:4128).
11. **Script Extender updater preconditions.** Feature flag, DWrite.dll present, `UpdaterVersion >= 4`, 120 s kill (MWVM:420-450). The current text ("change the verb and it breaks") hides these other silent no-op paths.
12. **`_Lib` exists in Debug too.** `CrossSpeakLibraryFiles` (GUI.csproj:85-90) copies native screen-reader DLLs into `_Lib` in every configuration, and `DisableCrossSpeakLib=True` (GUI.csproj:34) suppresses CrossSpeak's own copy. §2.4 implies `_Lib` is Publish-only. Relevant to Stage 6's screen-reader checks.
13. **Upstream-PR hygiene the plan relies on.** Upstreamable branches must avoid `merge=ours`-listed files only for driver reasons, but MWVM, GUI.csproj and DivinityApp.cs are on that list, and most Stage 2-5 fixes touch MWVM (implementation-plan.txt §6). Upstream PRs should also not touch `IgnoredMods.json`, `Update.xml` or the version files, or they collide with upstream's own "Update base mod versions" commits.
14. **Exceptions from `ScheduleAsync` bodies don't reach the ReactiveCommand handler at all.** The 15 taskpool hand-offs run outside the command pipeline, so a throw there escapes both `ThrownExceptions` and `RxApp.DefaultExceptionHandler`. Either wrap them or route them to `ShowAlert`. I didn't test what Rx does with them at runtime (Not checkable here); flag it before Stages 2-4 add work there.
15. **Linux build blockers.** These go beyond "WPF is Windows-only": the case-sensitive `..\external\gppg` path (LSLib.csproj:62-63), backslash paths in csproj/BuildRelease, `rd`/cmd in PreBuildEvent, `net8.0-windows*` TFMs on Core (not just GUI; Core.csproj:3-4 `UseWPF`), and `Visibility` in Core models (DivinityModManagerSettings.cs:57). For "Linux kept open", note which layers are already toolkit-bound.

## Owner intentions gleaned (answers the relayed user question)

From implementation-plan.txt §1-3, the decision table and plan-overview.txt:
- Build a fork mainly for yourself and friends, but publish it. That means repointing the updater and adding a fork notice while keeping the MIT/LaughingLeader/Norbyte credits.
- Four workstreams, in your order: bugfixes (zip export corruption, archive import losses), then performance (startup load, scrolling, filtering), then the UI refresh, then features. The features are multi-order export, categories/dividers and overrides folded into the load order. A downloader, friend order-sync and native-mod management are parked until each gets its own spec.
- Stay on WPF `master`, don't adopt `upstream/avalonia`. Windows now, Linux/Steam Deck kept open ("nice, not a driver"), and choose toolkit-neutral options when they cost nothing.
- Give back upstream only as bugfixes and perf, on `fix/*`/`perf/*`/`chore/*` branches cut from clean `master`. Fork-only work goes on `fork/main` (`feat/*`, `ui/*`). Merge with `--no-ff`, one branch per issue, and merge (not rebase) when syncing upstream.
- Categories are flat divider rows, never WPF grouping.
- Take ideas from the BG3ModManager-Redux fork but no code, even though its licence would allow it.
- Measure before optimizing. Debug baseline: 1,121 mods / 935 active, ~15.2 s cold start, 9.27 s of it loading, 478 MB, 1.7-3.8 s per filter keystroke. Wrong hypotheses get recorded as rejected.
- You want fuzzy search, but only after the per-keystroke cost is fixed.
- You want to check visible changes yourself and to decide when to push. Keep the plan and the board updated together. Never touch your real game install or `modsettings.lsx`.
