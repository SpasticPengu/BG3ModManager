# CLAUDE.md — BG3 Mod Manager (SpasticPengu fork)

Context for AI agents working in this repo. Facts here were verified against the tree at
commit `fc35358` (2026-09-18/23); §2.1–2.2 also cover `fix/publish-prebuild-quoting`. Re-audited
against `fc35358` on 2026-11-03 (docs/review/docs-corrections.md). Where something is inferred rather than observed, it says so.

> **The fork's roadmap lives in `implementation-plan.html` (detailed) and `plan-overview.html`
> (quick-view board).** Update both together on any status change — a stale board is worse than
> none. Governing rule: anything that might go upstream (`fix/*`, `perf/*`) is branched from a
> clean `master` and never built on top of fork-only work.
>
> **These three files live on the orphan `notes` branch** (`origin/notes`, `9906953`), not in any code
> branch. Read them from a separate worktree or keep an uncommitted copy; never commit them to a
> `fix/*`, `perf/*` or `chore/*` branch, or they land in an upstream PR.

---

## 1. What this is, and the fork situation

A Windows desktop mod manager for Baldur's Gate 3. **.NET 8, WPF, ReactiveUI/MVVM, x64-only.**
Originally forked (by upstream) from a Divinity: Original Sin 2 mod manager — which is why
most types are prefixed `Divinity` and both main projects use `RootNamespace=DivinityModManager`.

```
origin    https://github.com/SpasticPengu/BG3ModManager.git   (this fork)
upstream  https://github.com/LaughingLeader/BG3ModManager.git
```

**`master` carries no fork commits** — it equals `upstream/master` at `fc35358`. Fork work lives on branches
pushed to `origin` (as of 2026-11-03): `fix/publish-prebuild-quoting` (`f6633a8`, `5f8a80a9`), `perf/list-render`
(13 commits on `fc35358`: five fixes, seven `TEMP:` instrumentation commits and `e38c87b`, which only re-encodes
the TEMP `PerfProbe.cs`), and the orphan `notes` branch (`9906953`: this file and the plan). `fork/main` has not
been pushed. List them with `git branch -r` and `git log --oneline master..origin/<branch>`. Everything below
describes upstream's code as inherited unless it names a branch.

`perf/list-render` keepers: `b123aff`; `309b81e` + `396d7b2` (ship together; `309b81e` sits on TEMP logging from
`789ec87` and needs a hand-resolved version for a PR); `2858a23`, then `7fa8347` (it needs `2858a23` first). Everything
else there is instrumentation that must be dropped before any upstream PR. An empty `perfscroll` file next to the exe
makes that build scroll itself and exit.

Cloud/agent containers may be shallow and lack the `upstream` remote. Run
`git remote add upstream https://github.com/LaughingLeader/BG3ModManager.git && git fetch --unshallow upstream`
before any upstream comparison.

### Upstream is split across two branches, and we forked the legacy one

| | `upstream/master` (we are here) | `upstream/avalonia` |
|---|---|---|
| Last commit | 2025-10-06 | 2026-03-07 |
| Ahead of master | — | **944 commits** |
| UI | WPF (Windows-only) | Avalonia 12.0.0-preview1 (cross-platform) |
| Reactive | ReactiveUI 20 + Fody weaving | ReactiveUI 23 + source generators |
| Packages | per-csproj | central `Directory.Packages.props` |
| Tests | none | `src/Tests` exists |
| `Update.xml` version | 1.0.12.9 | 1.0.10.0 (lower ⇒ never released) |

`avalonia` is an **unfinished, stalled rewrite**, not a shipped successor — its README still
says .NET Framework 4.7.2 and its release version is behind master's. `master` is still what
ships. Both branches are quiet, so this fork is not chasing a fast-moving target — but also
should not expect a stream of upstream fixes.

There is also `upstream/nexus-support`: **not a cherry-pickable feature branch.** It is a full
re-architecture (465 files, ~26k insertions; adds `src/Services`, `src/Desktop-WPF`, `src/Tests`,
`nxm://` protocol models).

---

## 2. Building — read this before trying

### 2.1 Toolchain state (verified 2026-09-23 by building)

**The machine now builds.** Everything below was previously listed as a blocker and is resolved.
Debug and Publish both produce working output; `LSLibNative.dll` links; both parser generators run.

| Requirement | State |
|---|---|
| VS Build Tools 2022 + Desktop development with C++ | installed — 17.14.41, MSVC v143 `14.44.35207` |
| C++/CLI support (`/clr:netcore`) | installed, `LSLibNative.dll` builds |
| Windows SDK | installed — `10.0.26100.0` |
| .NET 8 SDK / Desktop Runtime | `8.0.425` / `Microsoft.WindowsDesktop.App 8.0.31` |
| `Microsoft.NetCore.Component.SDK` (in Build Tools) | installed — **was the one real gap** |
| GPLex 1.2.2 + GPPG 1.5.2 | present in `External/lslib/External/gppg/binaries/`, byte-identical to the official distros |

`src/Core/DivinityModManagerCore.csproj:20` still `ProjectReference`s
`External/lslib/LSLibNative/LSLibNative.vcxproj` — a **C++/CLI** project
(`granny2wrapper.cpp`, `lz4wrapper.cpp`, `<CLRSupport>NetCore`, toolset v143).
**`dotnet build` cannot build `.vcxproj` at all**, so full MSBuild remains mandatory.

`LSLib.csproj:70-76` runs GPLex/GPPG in a `PreBuildStoryGppgFiles` target with no
`ContinueOnError`. The generated `*.lex.cs` / `*.yy.cs` are gitignored and
`External/lslib/External/` ships with only a `.keep`, so a **fresh clone must supply both exes**
in `External/lslib/External/gppg/binaries/` — the csproj is authoritative over lslib's README,
which wrongly says gplex goes in `External\gplex\`. The csproj spells the path in lower case
(`..\external\gppg\binaries\`, `LSLib.csproj:62-63`), which resolves only on a case-insensitive file system.

**Do not set `MSBuildSDKsPath`, `MSBuildEnableWorkloadResolver` or `DOTNET_HOST_PATH`.** They were
tried as workarounds before the SDK component was installed and are mutually exclusive dead ends:
`MSBuildSDKsPath` fixes the native project but breaks the C# ones; disabling the workload resolver
fixes those but then `GUI.csproj` fails `NETSDK1112`. With the component installed, builds are
clean with all three unset.

Also available: .NET SDK 10.0.400, Python 3.14, git 2.55. `gh` is authenticated as `SpasticPengu`.

**.NET 8 reaches end of support on 2026-11-10.** Nothing breaks that day, but the runtime stops getting security
patches (all six projects target `net8.0*`; README.md:11 pins runtime 8.0.15). Moving to `net10.0` needs MSBuild/VS 18.0
or later: SDK 10.0.400 lists VS 18.0 as its minimum, so these Build Tools 17.14 cannot officially target it. Whether
net10 C++/CLI also needs the v145 toolset is unconfirmed. Install Build Tools 18 side by side before retargeting.

Use `-nodeReuse:false` on MSBuild — leftover worker nodes linger ~15 min and will block a
Visual Studio Installer run with `VSProcessesRunning`.

### 2.2 Verified build traps

**`$(SolutionDir)` is load-bearing — build the `.sln`, never a `.csproj` directly.**
`GUI.csproj:25` sets `OutputPath` to `$(SolutionDir)\bin\$(Configuration)\`, and Core's
ProjectReferences are `$(SolutionDir)External\...`. Outside a `.sln` build `SolutionDir` is empty,
so output collapses to `C:\bin\Debug\` and the lslib references resolve to nothing.
Workaround if you must: `-p:SolutionDir="<repo root>\"` (trailing backslash required).

**`LSLibNative.vcxproj` has no `Publish` configuration** — only `Debug|x64`, `Editor Debug|x64`,
`Release|x64`. `BG3ModManager.sln` remaps `Publish|x64` and `PublishTest|x64` → `Release|x64` for it.
This is the other reason to build the solution. (It is also why the odd `Editor Debug` solution
configuration exists at all.)

**Don't build `Publish|Any CPU` or `PublishTest|Any CPU`.** Those solution platforms build LSLib, LSLibNative and
LSLibStats in **Debug** (`BG3ModManager.sln:113-118,133-138,153-158`), so a release can ship a debug-CRT native DLL.

**The `Publish` pre-build step works — by accident.** `GUI.csproj:82` originally read:

```xml
<PreBuildEvent>rd /s /q $(TargetDir)_Lib</PreBuildEvent>
```

An earlier revision of this file claimed this fails with `MSB3073` (exit 2 / exit 3) on a path
containing a space. **That was wrong, and so was a later correction claiming it silently leaves
`_Lib` stale.** Measured from a real build log, both on this spaced path:

1. **`$(TargetDir)` expands to nothing.** The property group sits above the implicit
   `Microsoft.NET.Sdk` targets import, and `TargetDir` is not defined until that import is
   evaluated. The log shows the command reaching cmd as a bare relative `rd /s /q _Lib`.
   No path is interpolated, so the space in `...\Git Repositories\...` is irrelevant.
2. **`Microsoft.Common.CurrentVersion.targets:1426`** runs it as
   `<Exec WorkingDirectory="$(OutDir)" Command="$(PreBuildEvent)" />`, so the relative `_Lib`
   resolves against `bin\<Configuration>\` — the correct folder.

Two mistakes that cancel out. Verified by planting marker files: one in `bin\Publish\_Lib` is
deleted; one in `src\GUI\_Lib` is untouched. Every case exits 0.

The only real defect is cosmetic — on the first `Publish` after a clean, `_Lib` is absent and `rd`
writes to stderr while cmd still exits 0. Fixed on `fix/publish-prebuild-quoting` (`5f8a80a9`) with
`$(OutputPath)`, which **is** defined at that point because `GUI.csproj:25` sets it above the
property group:

```xml
<PreBuildEvent>if exist "$(OutputPath)_Lib" rd /s /q "$(OutputPath)_Lib"</PreBuildEvent>
```

Worth keeping because the original is only correct by coincidence: anything that makes `TargetDir`
resolve turns it into an unquoted absolute path, and the space-splitting bug becomes real.

**⚠ A clean checkout cannot build `Publish`, `PublishTest`, `Release` or `Editor Debug`.**
`External/CrossSpeak/CrossSpeak/CrossSpeak.csproj` sets `GeneratePackageOnBuild=True` with a
hand-written `CrossSpeak.nuspec` whose file list hardcodes the **Debug** output
(`<file src="bin\Debug\netstandard2.0\CrossSpeak.dll" .../>`, Release line commented out). The
`.sln` maps `Publish|x64` → `Release|Any CPU` for it, so the build writes `bin\Release\...` and
pack then looks in `bin\Debug\...`:

```
NuGet.Build.Tasks.Pack.targets(221,5): error : Could not find a part of the path
  '...\External\CrossSpeak\CrossSpeak\bin\Debug\netstandard2.0'.
```

**Build `Debug` once first** and every later configuration succeeds — which is why no one upstream
hits it. Fix belongs in the CrossSpeak submodule (drop `GeneratePackageOnBuild`; nothing in this
solution consumes the package).

**⚠ The first build on a clean checkout always fails; build twice.** `LSLib.csproj` generates
`Goal.lex.cs`, `Goal.yy.cs`, `StoryHeader.lex.cs` and `StoryHeader.yy.cs` in a build target, but
the SDK's default `**/*.cs` glob is expanded at project **evaluation**, before any target runs. So
the files exist on disk but are absent from `@(Compile)` for the build that created them:

```
GoalParser.cs(79,29): error CS0534: 'GoalScanner' does not implement inherited abstract member
HeaderParser.cs(24,25): error CS0246: The type or namespace name 'HeaderScanner' could not be found
```

The second build sees them; the `Exec`s are guarded by `Condition="!Exists(...)"` so nothing is
regenerated. Fix belongs in the lslib submodule.

`External/lslib/LSLibStats/LSLibStats.csproj` has the same trap for ten more generated files, and the `.sln` builds
it in every configuration although nothing references it (`BG3ModManager.sln:22,146-164`). A fix limited to
`LSLib.csproj` still leaves the first build failing.

**`Release|x64` silently does not build Toolbox.** `BG3ModManager.sln:104` (`Release|Any CPU`, `:102-103`, does build it) has
`{2F2EF4FE…}.Release|x64.ActiveCfg` but **no matching `.Build.0`**. Since the GUI shells out to
`Tools\Toolbox.exe` at runtime, a Release build yields an app whose Script Extender updater
silently does nothing. Toolbox does build in Debug, Publish and PublishTest.

**`Publish` ships unoptimized IL.** The SDK only defaults `Optimize=true` when
`Configuration=='Release'`; `GUI.csproj` sets `Optimize` only for `Debug`. Core/Toolbox/LSLib escape
this by setting `<Optimize>True</Optimize>` unconditionally — only `BG3ModManager.dll` is affected.

**`DebugType=none`** in Core (`:12`), Toolbox (`:13`), LSLib (`:15`) and LSLibNative (`:24`) means **no symbols, no line numbers
for those projects in anything but `Debug`.** The GUI never sets `DebugType`, so `BG3ModManager.dll` gets a portable PDB
in Release and Publish, but Publish's `SetupLibFolder` deletes it (`GUI.csproj:94,98`). Reproducing a user stack trace requires a Debug build.

### 2.3 Commands

```bash
# Fresh clone (both submodules are LaughingLeader forks, not Norbyte's lslib)
git clone --recurse-submodules https://github.com/SpasticPengu/BG3ModManager.git
```

```bash
# Existing clone: initialise / refresh submodules
git submodule update --init --recursive
```

```powershell
# Restore works with dotnet (restore does not load the C++ targets)
dotnet restore BG3ModManager.sln
```

```powershell
# Build — REQUIRES full MSBuild (C++/CLI in the reference chain)
msbuild BG3ModManager.sln -t:Build -p:Configuration=Debug -p:Platform=x64 -restore -m
```

```powershell
# Release zip — REQUIRES MSBuild AND python on PATH; on a clean checkout build Debug once first (CrossSpeak pack trap, §2.2)
msbuild BG3ModManager.sln -t:Build -p:Configuration=Publish -p:Platform=x64 -restore -m
```

`PublishTest` is identical to `Publish` but skips the zip step.

### 2.4 The `_Lib` layout (why Debug and Publish differ)

`Publish`/`PublishTest` run `SetupLibFolder` (`GUI.csproj:92-102`), which moves **every** DLL/XML
into `_Lib\` except `BG3ModManager.dll` and `WinRT.Runtime.dll`, deletes PDBs, and removes ~30
satellite-culture folders plus `runtimes`, `_Logs`, `Data`.

The app survives this via a hand-rolled resolver in `src/GUI/Program.cs:13-29` —
`AppDomain.CurrentDomain.AssemblyResolve` pointed at `_Lib`, installed before WPF starts
(`GUI.csproj:10` sets `<StartupObject>DivinityModManager.Program</StartupObject>`).

Consequences: `_Lib` only works for **managed** assemblies; it uses `Assembly.LoadFile` (no
dependency resolution); and anything loading assemblies by path or reflection **can pass in Debug
and fail in Publish**. Native screen-reader DLLs get an explicit `LoadLibrary` from `_Lib`
(`src/Core/AppServices/ScreenReaderService.cs:49-70`). `_Lib` exists in **every**
configuration, not only Publish: the `CrossSpeakLibraryFiles` target (`GUI.csproj:85-90`) copies those native DLLs
there after every build.

`RunBuildRelease` then runs `python BuildRelease.py <version>` from the repo root, producing
`BG3ModManager_v<ver>.zip` + `BG3ModManager_Latest.zip` (both gitignored via `*.zip`).
`BG3ModManager_Latest.zip` is exactly the filename `Update.xml` points at.

### 2.5 Version bump = 4 files, always together

`src/GUI/GUI.csproj:16` (`AssemblyVersion`), `:17` (`FileVersion`),
`src/GUI/app.manifest:3`, `Update.xml:3`.

AutoUpdater compares `Update.xml`'s version against the assembly version — a mismatch means either
no update prompt or an infinite one.

---

## 3. Architecture map

```
GUI.csproj  (BG3ModManager.exe)  ──>  Core (DivinityModManagerCore.csproj)  ──>  External/lslib, External/CrossSpeak
Toolbox.csproj  — references NOTHING in-repo; built only by the .sln; launched as a child process
```

- **Core** `net8.0-windows`, `UseWPF=true` — models, loaders, util. Uses WPF types (`Visibility`).
- **GUI** `net8.0-windows10.0.22621.0` — every concrete view model lives here.
- **Toolbox** `net8.0-windows`, console, → `bin/<Config>/Tools/`. One command, invoked by string:
  `Toolbox.exe UpdateScriptExtender -u "<DWrite.dll>" -b "<game bin dir>"`
  (`MainWindowViewModel.cs:~436`). It exists as a separate process because it `LoadLibrary`s and
  P/Invokes DWrite.dll, which the GUI must never load itself. **Change its PowerArgs verb and the
  GUI breaks silently** — only a `[Toolbox]` line in the log shows it. It also runs only when the `ScriptExtender`
  feature is on, `Tools\Toolbox.exe` and `DWrite.dll` exist and `ExtenderUpdaterSettings.UpdaterVersion >= 4`, and it
  is killed after 120 s (`MainWindowViewModel.cs:420,426,447-450`).

### Startup order

1. `src/GUI/Program.cs:38` `Main` — installs the `_Lib` resolver, shows the splash. **`args` is
   accepted and never used; there is no CLI.**
2. `App` ctor (`App.xaml.cs:25`) — registers first services, configures AutoUpdater.
3. `App.OnStartup` (`:59`) — fades splash, `new MainWindow().Show()`.
4. `MainWindow` ctor (`MainWindow.xaml.cs:367`) — computes `_Logs`, attaches exception handlers,
   builds `MainWindowViewModel`, creates `MainViewControl` **in code** and adds it to `MainGrid`,
   constructs Settings/Update windows.
5. `MainWindowViewModel.OnViewActivated` (`:4105`) — the real init: `InitSettingsBindings()` →
   `LoadSettings()` → `Keys.LoadKeybindings()` → `SaveSettings()` → `RefreshAsync`, started on the taskpool (`:4143`).
   **It does not stay there.** Its first await is an `Observable.Start(..., RxApp.MainThreadScheduler)` (through
   `SetMainProgressTextAsync`, `:2232-2238`, or `LoadSettings`), whose awaiter resumes inline on the Dispatcher. From then
   on `LoadModsAsync` and `LoadModPackageDataAsync` run on the **UI thread**, one pak at a time: each per-pak task does a
   synchronous `pr.Read` before its first await (`DivinityModDataLoader.cs:691-693,779-784`), so `Task.WhenAll` adds no
   parallelism and `ConfigureAwait(false)` alone does not fix it. (Static analysis; a Windows run that logs thread IDs
   would confirm it.)

**`MainWindow.xaml` is only 60 lines** — the real UI is `MainViewControl.xaml` (524 lines).

**No single-instance guard.** Two copies both write `Data/settings.json`; last writer wins silently. Most
saves are immediate `SaveSettings()` calls; only window-geometry saves go through the 250 ms `QueueSave`
(`MainWindowViewModel.cs:1273-1278`, called from `MainWindow.xaml.cs:196,249`), which returns early while
`!IsInitialized && IsRefreshing`.

### Service location — Splat, not DI

`src/Core/Services.cs` is a ~30-line static facade over `Locator`. No constructor injection;
everything is pull-style `Services.Get<T>()`.

Registered across three call sites: `IFileWatcherService`, `IScreenReaderService`, `HttpClient`,
`AppUpdateWindowViewModel`, `ICreatesObservableForProperty` (all in `App.xaml.cs`);
`IModRegistryService` (in `MainWindowViewModel`'s ctor, `:4893`); `SettingsWindowViewModel`
(in `SettingsWindow.Init`). **Registration order matters** — `Services.Get<T>()` for the last two
returns null earlier in startup, so consumers null-check.

Service interfaces are declared in namespace `DivinityModManager` while implementations are in
`DivinityModManager.AppServices` — **in the same file** under `src/Core/AppServices/`. These are the
only block-scoped namespaces in the repo.

### Navigating `MainWindowViewModel.cs` (5448 lines)

Almost no `#region` markers. Navigate by line range:

| Lines | Contents |
|---|---|
| 49–256 | property/collection declarations; `SourceCache<DivinityModData,string> mods` at `:82`, keyed by UUID |
| 329–760 | Script Extender: download DWrite.dll, run Toolbox, version checks |
| 859–1111 | `InitSettingsBindings()` |
| 1112–1315 | `LoadSettings` / `SaveSettings` / `QueueSave` |
| 1316–1491 | `SetGamePathways()`, game/AppData discovery |
| 1609–1834 | `LoadModsAsync`, `LoadProfilesAsync`, `BuildModOrderList` |
| 2340–2557 | `RefreshAsync` — *the* data pipeline, 10 progress steps |
| 2558–3040 | load-order save/load/export to game |
| 4105–4166 | `OnViewActivated` |
| 4178–4316 | filter parsing (`@author foo` syntax) |
| **4891–5448** | **the constructor — roughly half the app's behaviour** |

**Practical rule: to find behaviour, grep the constructor first.** Every `ReactiveCommand`, most
hotkey actions (5 of the 30 `.AddAction` calls, such as OpenLogsFolder, are in `InitSettingsBindings`, e.g. `:881`), every DynamicData filter, and the `modsettings.lsx` watcher live there.

---

## 4. ReactiveUI / MVVM house rules

Getting these wrong produces code that **compiles but silently doesn't update the UI**.

### Properties

Both Core and GUI have `FodyWeavers.xml`, so **`[Reactive]` works in both.** Core is the heavier
user. Attributes go on the same line as the property:

```csharp
[ObservableAsProperty] public bool HasDependencies { get; }
[DataMember, Reactive] public string GameDataPath { get; set; }
```

Note the pinned combination **ReactiveUI.Fody 19.5.41 + ReactiveUI 20.1.63** — Fody weaving was
dropped upstream after v19. Don't "fix" the version mismatch; it will break every `[Reactive]`.

### The house OAPH helper

`src/Core/Extensions/ReactiveExtensions.cs:17-36` — prefer these over raw `ToProperty`:

- `ToUIProperty(...)` — `deferSubscription: true`, `RxApp.MainThreadScheduler`
- `ToUIPropertyImmediate(...)` — `deferSubscription: false`

**Use `ToUIPropertyImmediate` whenever non-UI logic reads the property** (notably DynamicData
`Filter`/`AutoRefresh` predicates) — a deferred OAPH doesn't subscribe to its source until the first `.Value` read,
and returns `initialValue` until then. Both helpers wrap `ToPropertyEx` (`:19`, `:35`), so they need a Fody-woven
`[ObservableAsProperty] T Prop { get; }`; they can't back a hand-written `_field` OAPH.

In practice the helpers are used only in two Core models (`DivinityModData.cs`, `DivinityModManagerSettings.cs`).
All of GUI uses raw `_field` + `.ToProperty(...)` (45 sites, zero `[ObservableAsProperty]`), and Core has 26 raw
sites. Match the file you're in: helpers on a Fody `[ObservableAsProperty]` model, raw `ToProperty` in a
`_field`-style view model.

### Commands

`ReactiveCommand.Create` ×51, `CreateFromTask` ×2, `CreateFromObservable` ×1.
**`CreateRunInBackground` is not used.** Long work is *not* done inside the command — the body is
synchronous and hands off via `RxApp.TaskpoolScheduler.ScheduleAsync(...)` (15 sites in
`MainWindowViewModel`, plus one `MainThreadScheduler.ScheduleAsync` at `:2594` and one in `SettingsWindowViewModel.cs:230`).
`canExecute`, when present, is usually a named `IObservable<bool>` built with `WhenAnyValue`, but many commands have
none (`RefreshCommand` `:4977`, `SaveSettingsSilentlyCommand` `:5369`) and some pass inline observables
(`BaseProgressViewModel.cs:63`, `BaseHistoryViewModel.cs:92`). An explicit output scheduler is rare — one named
`outputScheduler:` (`:4926`) and five positional `RxApp.MainThreadScheduler` arguments in the whole repo — so pass
`RxApp.MainThreadScheduler` yourself whenever a command's subscribers touch the UI.

Cancellable progress: derive from `BaseProgressViewModel` and override `Task<bool> Run(CancellationToken)`.

**There are zero `.ThrownExceptions` subscriptions in the repo.** Everything funnels into
`RxApp.DefaultExceptionHandler`, which is assigned twice — `RxExceptionHandler` in `App.xaml.cs:54`
(non-DEBUG only), then **overwritten** by `MainWindowExceptionHandler` in `MainWindowViewModel.cs:4903`.
So `RxExceptionHandler` is effectively dead once the VM exists, and
**a throwing ReactiveCommand shows the user nothing** — `OnNext` only logs. Add an explicit
`ShowAlert` in the catch if the user needs to know.

### DynamicData 9.x

One source of truth: `SourceCache<DivinityModData, string> mods` keyed by UUID
(`MainWindowViewModel.cs:82`). Views are built at `:5239-5246`:

```csharp
var modsConnection = mods.Connect();
modsConnection.Filter(x => x.IsUserMod).Bind(out _userMods).Subscribe();
modsConnection.AutoRefresh(x => x.CanAddToLoadOrder).Filter(x => x.CanAddToLoadOrder).Bind(out addonMods).Subscribe();
```

To add a filtered view: declare `ReadOnlyObservableCollection<DivinityModData>`, add a
`modsConnection.AutoRefresh(...).Filter(...).Bind(out …).Subscribe()` line in the ctor, and
`OneWayBind` it in the view.

- **A plain `Filter` never re-evaluates.** Any predicate over a mutable property needs
  `.AutoRefresh(x => x.Prop)` upstream.
- `modsConnection.Publish()` at `:5240` is **dead code** — the connectable is discarded and never
  connected. Don't copy it thinking it multicasts.
- **`ActiveMods` / `InactiveMods` are NOT DynamicData-bound.** They are plain
  `ObservableCollectionExtended` rebuilt imperatively at `:2141-2144`. The search box **does not
  filter them** — `OnFilterTextChanged` sets `DivinityModData.Visibility` per item and the item
  container hides itself. That is why `IsSelected`'s setter refuses `true` when
  `Visibility != Visible`, and why programmatic multi-select on a filtered list appears to no-op.
- **`SelectedModOrder.Order` is rebuilt from `ActiveMods`.** Every `ActiveMods` `CollectionChanged` (Move included)
  schedules `UpdateOrderFromActiveMods` 250 ms later (`:5401-5408`). It clears `Order` and re-adds entries through
  `ToOrderEntry`, which copies only UUID and Name (`:4880-4888`; `DivinityModData.cs:299-306`). Export and save call it
  too. A new per-entry field on `DivinityLoadOrderEntry` is wiped unless it is carried over by UUID; prefer an
  order-level map keyed by UUID.
- **Drag-drop indices.** With visibility filtering, gong's `UnfilteredInsertIndex` equals the model index, so drops
  into a filtered list land correctly. A column-header sort adds `SortDescriptions` to the default view
  (`HorizontalModLayout.xaml.cs:752-769`), and a drop into a sorted active list then inserts at the sorted-view index.
- **Never enable WPF grouping on the mod lists.** `Themes/MainResourceDictionary.xaml:460-461` sets
  `CanContentScroll=False` when `IsGrouping=True`, which turns virtualization off; `CanContentScroll=True` comes only
  from AdonisUI's ListView style. Virtualization is `Recycling` (`HorizontalModLayout.xaml:119-120,223-224`) and
  `VirtualizingPanel.ScrollUnit` is set nowhere. Categories are flat divider rows.

### Threading — the live bug class

**There is not a single `Dispatcher.Invoke` in `src/`.** Use `RxApp.MainThreadScheduler`.

A Fody-woven `[Reactive]` setter raises `PropertyChanged` **synchronously on the calling thread**.
So anything under `RxApp.TaskpoolScheduler.ScheduleAsync` that writes bound state must be wrapped:

```csharp
await Observable.Start(() => { LoadSettings(); }, RxApp.MainThreadScheduler);
```

That is exactly what commit `fc35358` ("Load settings on the main UI thread") added to `RefreshAsync`.
There are 15 `TaskpoolScheduler.ScheduleAsync` sites in `MainWindowViewModel` where the same hazard lurks.
Side effect of the fix: after `RefreshAsync`'s first `Observable.Start(..., MainThreadScheduler)` the rest of it runs on
the UI thread (§3, Startup order). **Rx drops any exception thrown inside these `ScheduleAsync` bodies** (only `OperationCanceledException` is handled),
so it reaches neither `ThrownExceptions`, `RxApp.DefaultExceptionHandler` nor `DispatcherUnhandledException`, and the
repo has no `TaskScheduler.UnobservedTaskException` handler. `RefreshAsync` (`:2340-2557`) has no try/finally, so a throw
there leaves the progress overlay up and `IsRefreshing=true` until restart. Wrap new background work in try/catch/finally
and route failures to `ShowAlert`.

### Settings — three attributes, each doing something different

A setting on `DivinityModManagerSettings` needs **all three** or it half-works:

| Attribute | Without it |
|---|---|
| `[DataMember]` | not persisted, and never marks the Save button dirty (`InitSubscriptions()` reflects over `[DataMember]`) |
| `[Reactive]` | **never restored on load** — `SetFrom<T, ReactiveAttribute>` uses `[Reactive]` as the allow-list |
| `[SettingsEntry(name, tooltip)]` | never appears in Preferences (the window is generated by reflection, not XAML) |

`SetFrom` also **skips null values**, so a property saved as `null` silently keeps its in-memory default.
`settings.json` is written with `DefaultValueHandling.IgnoreAndPopulate` (`MainWindowViewModel.cs:1235`), so a value
equal to its `[DefaultValue]` is omitted from the file and re-populated on load.

`ExtenderSettings`/`ExtenderUpdaterSettings` are restored through the **untyped** `SetFrom<T>`
(`MainWindowViewModel.cs:1128-1129`; `ModelExtensions.cs:29-42`), which copies every non-null property whether or not
it has `[Reactive]`; boxed `bool`/`DateTime` values are never null. For the same reason never `SetFrom` a whole
`DivinityLoadOrder`: it overwrites `IsModSettings` and keeps `FilePath`, which is how importing an archive's
"Current" order can make a later Save write JSON into `modsettings.lsx` (`MainWindowViewModel.cs:3110-3127`).

**Never reassign `Settings.ExtenderSettings` / `ExtenderUpdaterSettings` / `Window` / `Confirmations`** —
mutate in place. Dozens of subscriptions capture the original instance; swapping it dead-ends the
whole Script Extender settings UI. That was the bug `7b57e5a` fixed, and the same commit
left `Window`/`Confirmations` unrestored (see below). `[IgnoreSetFrom]` exists but
**nothing reads it** — the real exclusion is done by omitting `[Reactive]`.

**`Settings.Window` and `Settings.Confirmations` are never restored from disk.** Both are `[DataMember]` without
`[Reactive]` (`DivinityModManagerSettings.cs:112,122`), `SetFrom<T, ReactiveAttribute>` skips them, and nothing else
copies them (`MainWindowViewModel.cs:1127-1129`). `OnViewActivated` then calls `SaveSettings()` straight after
`LoadSettings()` (`:4126-4133`), writing the defaults over the saved values. So "Save Window Location" never applies
(`ApplyWindowPosition` gets -1 values, `MainWindow.xaml.cs:205-235`) and `DisableAdminModeWarning` is forgotten on every
restart. Fix by copying the fields into the existing instances (never reassign them; `WindowSettings` is a POCO, so copy
its six fields by hand). Also note `ApplyWindowPosition` treats any negative X or Y as unset (`MainWindow.xaml.cs:231-232`),
so a window on a monitor left of or above the primary still loses its position after that fix. Any new nested settings
object needs its own restore line or it repeats this bug.

The Preferences window and the top menu bar are **both generated by reflection**. Adding a menu item
means adding a `Hotkey` property with `[MenuSettings]` to `src/GUI/ViewModels/AppKeys.cs`, not
editing `MainViewControl.xaml`.

### Views

XAML cannot name a generic base, so **every reactive view declares a non-generic shim** in the same
`.xaml.cs`, above the partial class:

```csharp
public class ModUpdatesLayoutBase : ReactiveUserControl<ModUpdatesViewData> { }
```

Wiring goes in `this.WhenActivated(d => { ... })` in the constructor. House style is **`d(binding)`
as a call, not `.DisposeWith(d)`** — both compile; mixing makes diffs noisy.

Secondary windows derive from `HideWindowBase<T>` and **cancel Closing / `Hide()` instead**; `MainWindow`
derives directly from `AdonisWindow` (`MainWindow.xaml.cs:26`) and really closes. They are
constructed once and live for the process — don't expect ctor/dispose logic to re-run.

**Moving a Core type between namespaces breaks XAML silently.** `src/GUI` has 24
`clr-namespace:...;assembly=DivinityModManagerCore` declarations across 10 XAML files — 7 in
`Themes/MainResourceDictionary.xaml`, 4 in `SettingsWindow.xaml`. Grep `src/GUI/**/*.xaml` after any
namespace change.

---

## 5. Code style

`.editorconfig` has only `[*.cs]` and `[*.{cs,vb}]` sections — **XAML, JSON and csproj are
unconfigured; match the surrounding file.**

- **Hard tabs**, size 4. **Line endings: keep each file's existing endings.** The index stores LF for most files
  (`git ls-files --eol 'src/*.cs'`: 168 LF, 6 CRLF, 11 mixed; the `.sln`, the GUI and Core csprojs and three XAML files are CRLF) and `.gitattributes` has
  no `text`/`eol` rule. A Windows working tree shows CRLF only through `core.autocrlf=true`. `.editorconfig:15,251`
  says `crlf`, so don't let an editor or `dotnet format` rewrite a whole LF file, and don't add a `* text=auto`
  renormalisation on the fork (master tracks upstream). `insert_final_newline = false`. Most files have a UTF-8 **BOM** —
  preserve it when rewriting a file whole.
- **Allman braces** (`csharp_new_line_before_open_brace = all`).
- **File-scoped namespaces**; usings outside the namespace.
- **`System.*` usings go LAST** (`dotnet_sort_system_directives_first = false`), groups separated by
  blank lines.
- `var` preferred everywhere (all three `csharp_style_var_*` are `true`).

Known divergences from the config — **match the file you're editing**: 45 surviving `if(` without the
space; private fields mix `_underscore` and bare `camelCase`; `String.` and `string.` both used
heavily; zero primary constructors despite the suggestion.

### Naming legacy

The `Divinity` prefix is **legacy and NOT the convention for new types.** Types added since the .NET 8
port are unprefixed (`ProcessHelper`, `ModRegistryService`, `ModuleShortDesc`, `ConfirmationSettings`,
`TempFile`, …), and commit `c2079ac5` explicitly renamed `DivinityModDependencyData` → `ModuleShortDesc`.
**New types: no prefix. Existing ones: leave alone.**

### Nullable / usings

| | `<Nullable>` | `<ImplicitUsings>` | globals |
|---|---|---|---|
| Core | **off** | **off** | `src/Core/Usings.cs` |
| GUI | **off** | **off** | `src/GUI/Usings.cs` |
| Toolbox | `enable` | `enable` | none |

**Do not write `string?` in Core or GUI** — it yields warning CS8632 (Core already has a few, in
`WebHelper.cs:11-16` and `DivinityJsonUtils.cs:29`); don't add more. Only Toolbox is annotated.

Because ImplicitUsings is off, all ambient usings come from the hand-written `Usings.cs` files.
Two things to know:

- **`RxCommandUnit`** is the house alias for `ReactiveCommand<Unit, Unit>`.
- **In Core, bare `[JsonIgnore]` / `[JsonConverter]` mean *Newtonsoft's*** — aliased in
  `src/Core/Usings.cs` — even though `System.Text.Json` is also globally imported. GUI has no such
  aliases. Persistence is Newtonsoft everywhere; writing `System.Text.Json` attributes in Core
  compiles and is ignored at runtime.
- **`DynamicData` is not globally imported anywhere** — add `using DynamicData;` per file.

The five `Directory.build.props` carry **no shared settings** — only `obj/` redirection to
`<repo>/obj/<Name>/`. A cross-project property change must be made in all three csprojs.

### Logging and alerts

```csharp
DivinityApp.Log($"message");   // pass ONLY the message — caller info is compiler-filled
```

`src/Core/DivinityApp.cs:119` — it is `Trace.WriteLine`, no framework, no severity.
**Logging is OFF by default** — `Trace` has no listener until `MainWindow.ToggleLogging(true)`
attaches one. Enable it with the `LogEnabled` setting (hidden from Preferences), or by **dropping an
extension-less file named `debug` next to the exe**. The file also sets the VM-only `MainWindowViewModel.DebugMode`
(`:325`), which keeps logging on even when `LogEnabled` is false. The `DebugModeEnabled` setting ("Mod Developer Mode")
does **not** enable logging; it only shows developer UI. Any unhandled exception or `DisplayError` also switches logging
on (`MainWindow.xaml.cs:99,130,154`). A `debug` file left in `bin\Publish` ships in the release zip and turns logging on
for every user. Logs land in `<exe>/_Logs/`, and
`LogTraceListener` scrubs `%LOCALAPPDATA%`/`%APPDATA%`/`%USERPROFILE%` — don't re-implement redaction.

```csharp
ShowAlert(message, AlertType.Info, timeoutSeconds);   // timeout 0 = until replaced
```

`ShowAlert` **already logs, already scrubs paths, and already marshals to the UI thread** — call it
from any thread and don't add a redundant `DivinityApp.Log`. When the settings window is open, use
`SettingsWindowViewModel.ShowAlert` instead (it logs but does **not** scrub paths, `SettingsWindowViewModel.cs:120-122`). From Core, go via
`DivinityApp.Commands.ViewModel.ShowAlert(...)`.

---

## 6. The BG3 domain

### Vocabulary

- **`.pak`** — LSPK archive. A mod is identified *inside* the pak by its `Mods/<Folder>` directory,
  not by the pak filename.
- **`meta.lsx`** — the mod manifest (`ModuleInfo` node: UUID, Name, Folder, Version64, Tags…).
- **`modsettings.lsx`** — the per-profile active mod list under
  `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\PlayerProfiles\<Profile>\`. **Exporting a load
  order *is* rewriting this file.**
- **Three identity fields, do not confuse them:** `UUID` is the primary key everywhere;
  `Folder` is the on-disk module dir and is what base-game-override detection keys on;
  `Name` is display only.

**A mod's UUID is not guaranteed to be a GUID** — override paks with no `meta.lsx` get
`UUID = <full pak path>`. Never assume `Guid.Parse` succeeds.
`HasInvalidUUID` does **not** catch these: `CheckForInvalidUUID` returns false unless `CanAddToLoadOrder`
(`DivinityModData.cs:367-374`), and meta-less overrides can't be added. It flags orderable mods whose meta UUID is not a
GUID, and such a mod is still exported. Master can also write an override's path into `modsettings.lsx`: the JSON text
export includes `ForceLoadedMods`, re-importing it makes `LoadModOrder` set `ForceAllowInLoadOrder`, and
`GenerateModSettingsFile` writes the path into a `type="guid"` attribute (`DivinityModDataLoader.cs:1471-1483`). On
read-back the GUID parse throws, the error is swallowed (`:838-846`) and the order comes back empty. Guard every
write with `Guid.TryParse`.

### Base-game knowledge lives in JSON, not code

`src/Core/DivinityApp.cs:44-47` hardcodes only the campaign UUIDs
(`MAIN_CAMPAIGN_UUID = GUSTAVX_UUID`, Patch 8+). Everything else is
**`src/GUI/Resources/IgnoredMods.json`**.

**When the game patches, this is what needs updating** (what upstream's "Update base mod versions"
commits do):

1. Bump the packed `Version` int of each changed Larian module.
2. Add any new Larian module, with its UUID, to the `Mods` array. `DivinityModDataLoader.IgnoreModDependency`
   (`:47-50`) already treats every `Mods` UUID as a satisfied dependency, and Larian paks found at load are added to
   `IgnoredMods` automatically (`MainWindowViewModel.cs:1501-1509`). `IgnoreDependencies` is needed only for a UUID that
   should count as installed without being in `Mods`. Upstream lists 16 of its 20 modules in both (Engine, Game, MainUI
   and CrossplayUI are only in `Mods`); copying that convention is harmless and keeps diffs against upstream small.
3. If Larian ships a new campaign module, change `MAIN_CAMPAIGN_UUID` plus the `ModType="Adventure"`
   special-case and the export fallback chain.

Version ints are packed: `(Major<<55) + (Minor<<47) + (Revision<<31) + Build` — hence the ~3.6e16
values. These are **load-bearing, not documentation**: entries are only replaced when the incoming
version is strictly higher.

**Runtime reads `Resources/*.json` from `<exe dir>/Resources`, not from `src/GUI/Resources`.**
Editing the source file without rebuilding changes nothing.

### Loader

`src/Core/Util/DivinityModDataLoader.cs` (1826 lines) is the heart. Key entry points:
`LoadBuiltinModsAsync` (LSLib VFS over the game Data dir), `LoadEditorProjectsAsync` (loose mods),
`LoadModPackageDataAsync` (paks in AppData), `LoadModSettingsFileAsync`, `BuildOutputList`,
`GenerateModSettingsFile`, `GetLoadOrderFromSave`.

**GUID byte-swap trap:** `modsettings.lsx` is read with `ByteSwapGuids = false` while **every other**
Larian resource uses `true`. Using the wrong settings object yields byte-reordered UUID strings that
silently match nothing.

**`modsettings.lsx` is written by `String.Format`, not LSLib.** In
`DivinityApp.XML_MODULE_SHORT_DESC` the placeholder order is Folder`{0}`, MD5`{1}`, Name`{2}`,
PublishHandle`{5}`, UUID`{3}`, Version64`{4}` — **`{5}` physically appears before `{3}`.** Adding or
reordering an attribute means matching the argument list exactly.

**Force-loaded (override) mods are deliberately not draggable** into the load order unless
`IsForceLoadedMergedMod` or the user sets `ForceAllowInLoadOrder`. Bugs reported as "my mod won't
appear in the list" are usually this, not a loading failure.

**Duplicate paks with equal versions:** the winner is the last pak to finish loading (a single-thread `ConcurrentBag`
enumerates in reverse insertion order). That is the alphabetically last path when reads complete synchronously, and
timing-dependent when LZ4 entries are involved. The loser is offered for deletion.

### Known dead code — don't go looking for logic that isn't there

- `src/Core/Models/Conflicts/*` — **entirely unreferenced.** There is no file-level conflict analysis.
  "Conflicts" in the UI is only what a mod declares in its own `meta.lsx`.
- `DivinityModVersion` (v1, superseded by `DivinityModVersion2`), `DivinityGameMasterCampaign` (a DOS2 feature).
- `src/GUI/Views/VerticalModLayout.xaml(.cs)` and `LeaderLibSettingsWindow.xaml(.cs)` — never instantiated.
- The **"Mod Updates" view is dead UI** — `ModUpdatesViewData.Mods` is never populated and the
  `RefreshAllModUpdatesBackground()` call in `RefreshAsync` is commented out.
- `DivinityApp.GetAppDirectory()` has a **dead statement** (`DivinityApp.cs:135`): it computes the
  process-path directory (comment says it's for Proton) and discards it, always returning
  `AppContext.BaseDirectory`. Almost every settings/log/Orders path (30 call sites) flows through this; `_Lib`
  (`Program.cs:40`), the `debug` marker (`MainWindow.xaml.cs:380`) and one export fallback (`MainWindowViewModel.cs:2608`)
  use `AppDomain.CurrentDomain.BaseDirectory` directly.

---

## 7. Integrations

`src/GUI/Resources/AppFeatures.json` is the master switch:
`{"ScriptExtender": true, "NexusMods": false, "Workshop": false}`.
**Nexus Mods code ships but is disabled** — not completely: a non-empty `NexusModsAPIKey` still calls
`NexusModsDataLoader.Init` whatever the flag says (`MainWindowViewModel.cs:1056-1066`). `"Workshop"` is never even queried.

- **Nexus Mods** — API key is `[SettingsEntry(..., HideFromUI = true)]`, so there is **no UI field**;
  it is stored **plaintext** in `<exe>/Data/settings.json` and set by hand-editing. The release path
  deletes `Data/` twice (the `SetupLibFolder` target and `BuildRelease.py`) — **don't remove either
  step, and never commit a settings.json fixture.**
- **GitHub mod updates** — a stub that will NRE if enabled: `GithubData` is always null
  (its initialiser is commented out at `DivinityModData.cs:393`) while `ModUpdateHandler.cs:112`
  dereferences it unguarded. The cache handler is `return false;`.
- **Steam** — workshop support is dead, but `Gameloop.Vdf` is live and load-bearing for locating
  the game across Steam libraries.
- **Script Extender** — the one integration that works. **Two different DLLs:** `DWrite.dll` next to
  `bg3.exe` is the *updater*; the extender itself is
  `%LOCALAPPDATA%\BG3ScriptExtender\BG3ScriptExtender.dll`. The manager writes
  `ScriptExtenderSettings.json` and `ScriptExtenderUpdaterConfig.json` into the game's bin folder;
  by default **only non-default values are serialized**, so a setting equal to its `[DefaultValue]`
  silently disappears from the file the extender reads.

`WebHelper.DownloadFileAsStreamAsync` / `DownloadUrlAsStringAsync` **swallow every exception** (cancellation included)
and return `Stream.Null` / `""` (`WebHelper.cs:18-44`); the `GetAsync`/`PostAsync` wrappers (`:11-16`) let exceptions through — silent network failure
looks identical to "nothing to update".

---

## 8. Fork practice

### 8.1 Before distributing any build — the self-update hazard

`src/Core/DivinityApp.cs:22`:

```csharp
public const string URL_UPDATE = @"https://raw.githubusercontent.com/LaughingLeader/BG3ModManager/master/Update.xml";
```

**Unchanged, a fork build offers upstream's release to its own users and overwrites itself.**
To repoint a fork, change `DivinityApp.cs:19-30` (`URL_UPDATE` and `URL_CHANGELOG_RAW` are the live
ones) **and** the root `Update.xml`. Note `GITHUB_USER` / `GITHUB_REPO` / `GITHUB_RELEASE_ASSET` at
`:28-30` are **declared but never referenced** — changing them alone does nothing.

For personal use, setting `Settings.CheckForUpdates = false` is enough. The README's
"only official source" wording and the ko-fi donation links would also need revisiting before any
public distribution. Licence is **MIT, © 2019 LaughingLeader** — keep the copyright notice.

**The update window has two confirmed bugs** (`src/GUI/ViewModels/AppUpdateWindowViewModel.cs`). A failed check
(network or feed error) reports "up to date", because the handler never reads `args.Error`. Cancelling or failing the
download still calls `Environment.Exit(0)` (`:80-86`), which skips the close handlers. AutoUpdater 1.9.2 also checks no
signature, and a non-zip download URL is started directly. Fix these before relying on the updater for a fork release.

### 8.2 ⚠ `.gitattributes` `merge=ours` — a latent trap

21 fork-critical files are marked `merge=ours`, including `DivinityApp.cs`, `MainWindowViewModel.cs`,
`DivinityModDataLoader.cs`, `IgnoredMods.json`, `AppFeatures.json`, `GUI.csproj`, `Update.xml`.

**Verified by experiment:** `ours` is **not a built-in git merge driver** (only `text`, `binary`,
`union` are). With no config, git silently ignores it and these files merge/conflict normally —
which is the current, safe state.

But if you run `git config merge.ours.driver true`:

```
Auto-merging DivinityApp.cs          ← no conflict
FORK-CHANGE                          ← upstream's change silently discarded
```

**Do not enable that driver in this fork.** Those 21 files are precisely the ones that most need
upstream's changes — `IgnoredMods.json` is what upstream's "Update base mod versions" commits touch
after every game patch. A conflict you resolve by hand is the *desirable* outcome; a silent drop is not.

(The attribute list makes sense for upstream merging its own branches into master. It does not
transfer to a downstream fork.)

### 8.3 Staying in sync with upstream

```bash
git fetch upstream
git log --oneline master..upstream/master     # what's new upstream
```

**Recommendation: merge, don't rebase.** Rebasing rewrites your commits, which breaks anything you've
already pushed to `origin` and makes the fork's history diverge from what GitHub shows. Merging keeps
your work identifiable and plays well with the per-issue-branch + `--no-ff` workflow.

```bash
git checkout -b sync/upstream-YYYY-MM-DD
git merge upstream/master        # expect conflicts in the merge=ours files — resolve by hand
```

Submodules are pinned per-commit; after a merge that bumps a pointer:

```bash
git submodule update --recursive
```

Note the submodules point at **LaughingLeader's forks** of lslib and CrossSpeak, not Norbyte's
upstream lslib. A Norbyte lslib fix (common after a BG3 patch changes pak/LSF formats) reaches this
fork only after LaughingLeader merges it *and* bumps the pointer here.

### 8.4 GitHub Actions — there are none

`.github/` contains only `ISSUE_TEMPLATE/bug-report.yaml` and `config.yml`. **No workflows exist on
either upstream branch.**

A workflow is a YAML file in `.github/workflows/` that GitHub runs on a hosted VM when an event fires
(push, PR, tag). For this repo a CI build would need:

- `runs-on: windows-latest`
- `actions/checkout@v4` with `submodules: recursive`
- `microsoft/setup-msbuild@v2` — **required**, because `dotnet build` cannot build the C++/CLI project
- `actions/setup-dotnet@v4` for the .NET 8 SDK
- `nuget restore` / `dotnet restore BG3ModManager.sln`
- `msbuild BG3ModManager.sln -p:Configuration=Debug -p:Platform=x64`

**Real obstacles before CI can work**, in order:

1. The **GPLex/GPPG** tools are not checked in and not downloadable without a step that fetches them.
2. **Every job must build twice**, or build `Debug` first — a clean runner hits both §2.2 traps:
   LSLib's generated parsers are invisible to the build that creates them, and CrossSpeak's pack
   step fails until a Debug build has created `CrossSpeak/bin/Debug/netstandard2.0`. The
   `PreBuildEvent` is *not* an obstacle; it works on a clean runner.
3. **No test project exists**, so CI can only prove "it compiles".
4. Submodules are public, so `submodules: recursive` works without extra credentials.
5. **`windows-latest` is now the Server 2025 / VS 2026 image.** It has MSVC 14.44 but not the
   `VC.14.44.17.14.CLI.Support` component, so a v143 C++/CLI build there may fail [inferred, not run]. Pin
   `windows-2022` for the .NET 8 build, or settle it with one test run. Pin action major versions to whatever is
   current when the workflow is written.
6. `LSLibStats` adds ten more generated parsers to the first-build trap (§2.2).

Value order for setting this fork up properly: **(1)** fix the two clean-checkout traps in §2.2 so
a fresh clone builds in one pass, **(2)** a Debug-build CI workflow as a regression net,
**(3)** repoint the update URL before sharing any build.

---

## 9. Quick gotcha index

| Symptom | Cause |
|---|---|
| `Publish`/`Release` fails in `NuGet...Pack.targets`, missing `bin\Debug\netstandard2.0` | CrossSpeak packs from a hardcoded Debug path; build `Debug` once first (§2.2) |
| First build of a clean clone fails `CS0534`/`CS0246` | LSLib's generated parsers miss the `**/*.cs` glob; build twice (§2.2) |
| Build output lands in `C:\bin\Debug\` | built a `.csproj`, not the `.sln` (§2.2) |
| Script Extender update does nothing | built `Release\|x64` (Toolbox skipped) or GUI-only (§2.2); or `UpdaterVersion < 4`, no `DWrite.dll`, the `ScriptExtender` feature off, or the 120 s kill (§3) |
| New setting doesn't persist / doesn't restore / doesn't show | missing one of `[DataMember]` / `[Reactive]` / `[SettingsEntry]` (§4) |
| Extender settings UI stops updating | reassigned `Settings.ExtenderSettings` instead of mutating (§4) |
| UI doesn't refresh when a property changes | `Filter` without `AutoRefresh`, or `ToUIProperty` where `ToUIPropertyImmediate` was needed (§4) |
| Cross-thread / stale binding after a refresh | wrote `[Reactive]` state off the UI thread (§4) |
| Command throws, user sees nothing | no `ThrownExceptions`; handler only logs; logging is off by default (§4, §5) |
| UUIDs match nothing | wrong `ByteSwapGuids` setting (§6) |
| Mod won't appear in the load order | it's force-loaded/override, by design (§6) |
| Mass "missing dependency" after a patch | new Larian module missing from IgnoredMods.json `Mods` (§6) |
| Edited `Resources/*.json`, nothing changed | runtime reads `<exe>/Resources`, needs a rebuild (§6) |
| Works in Debug, fails in Publish | the `_Lib` flattening + `AssemblyResolve` hook (§2.4) |
| Fork build updates itself to upstream | `URL_UPDATE` still points at LaughingLeader (§8.1) |
| Window position / admin-warning opt-out forgotten on restart | `Settings.Window`/`Confirmations` are never restored (§4) |
| `modsettings.lsx` comes back empty after a save | a non-GUID UUID (override pak path) was written (§6) |
| Saved an order and `modsettings.lsx` now holds JSON | an archive import ran `SetFrom` over the Current order (§4) |
