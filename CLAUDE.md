# CLAUDE.md — BG3 Mod Manager (SpasticPengu fork)

Context for AI agents working in this repo. Facts here were verified against the tree at
commit `fc35358` (2026-09-18/20). Where something is inferred rather than observed, it says so.

> **The fork's roadmap lives in `implementation-plan.html` (detailed) and `plan-overview.html`
> (quick-view board).** Update both together on any status change — a stale board is worse than
> none. Governing rule: anything that might go upstream (`fix/*`, `perf/*`) is branched from a
> clean `master` and never built on top of fork-only work.

---

## 1. What this is, and the fork situation

A Windows desktop mod manager for Baldur's Gate 3. **.NET 8, WPF, ReactiveUI/MVVM, x64-only.**
Originally forked (by upstream) from a Divinity: Original Sin 2 mod manager — which is why
most types are prefixed `Divinity` and both main projects use `RootNamespace=DivinityModManager`.

```
origin    https://github.com/SpasticPengu/BG3ModManager.git   (this fork)
upstream  https://github.com/LaughingLeader/BG3ModManager.git
```

**This fork currently has zero custom commits.** `git rev-list --left-right --count upstream/master...master`
→ `0  0`. Everything below describes upstream's code as inherited.

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
which wrongly says gplex goes in `External\gplex\`.

**Do not set `MSBuildSDKsPath`, `MSBuildEnableWorkloadResolver` or `DOTNET_HOST_PATH`.** They were
tried as workarounds before the SDK component was installed and are mutually exclusive dead ends:
`MSBuildSDKsPath` fixes the native project but breaks the C# ones; disabling the workload resolver
fixes those but then `GUI.csproj` fails `NETSDK1112`. With the component installed, builds are
clean with all three unset.

Also available: .NET SDK 10.0.400, Python 3.14, git 2.55. `gh` is authenticated as `SpasticPengu`.

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

**`Release|x64` silently does not build Toolbox.** `BG3ModManager.sln:102-104` has
`{2F2EF4FE…}.Release|x64.ActiveCfg` but **no matching `.Build.0`**. Since the GUI shells out to
`Tools\Toolbox.exe` at runtime, a Release build yields an app whose Script Extender updater
silently does nothing. Toolbox does build in Debug, Publish and PublishTest.

**`Publish` ships unoptimized IL.** The SDK only defaults `Optimize=true` when
`Configuration=='Release'`; `GUI.csproj` sets `Optimize` only for `Debug`. Core/Toolbox/LSLib escape
this by setting `<Optimize>True</Optimize>` unconditionally — only `BG3ModManager.dll` is affected.

**`DebugType=none`** in Core (`:12`), Toolbox (`:13`) and LSLib means **no symbols, no line numbers
in anything but `Debug`.** Reproducing a user stack trace requires a Debug build.

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
# Release zip — REQUIRES MSBuild AND python on PATH; fix the PreBuildEvent first
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
(`src/Core/AppServices/ScreenReaderService.cs:49-70`).

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
  GUI breaks silently** — only a `[Toolbox]` line in the log shows it.

### Startup order

1. `src/GUI/Program.cs:38` `Main` — installs the `_Lib` resolver, shows the splash. **`args` is
   accepted and never used; there is no CLI.**
2. `App` ctor (`App.xaml.cs:25`) — registers first services, configures AutoUpdater.
3. `App.OnStartup` (`:59`) — fades splash, `new MainWindow().Show()`.
4. `MainWindow` ctor (`MainWindow.xaml.cs:367`) — computes `_Logs`, attaches exception handlers,
   builds `MainWindowViewModel`, creates `MainViewControl` **in code** and adds it to `MainGrid`,
   constructs Settings/Update windows.
5. `MainWindowViewModel.OnViewActivated` (`:4105`) — the real init: `InitSettingsBindings()` →
   `LoadSettings()` → `Keys.LoadKeybindings()` → `SaveSettings()` → `RefreshAsync` on the taskpool.

**`MainWindow.xaml` is only 60 lines** — the real UI is `MainViewControl.xaml` (524 lines).

**No single-instance guard.** Two copies both write `Data/settings.json` on a 250 ms debounce;
last writer wins silently.

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
| 49–256 | property/collection declarations; `SourceCache<DivinityModData,string> mods` at `:80`, keyed by UUID |
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

**Practical rule: to find behaviour, grep the constructor first.** Every `ReactiveCommand`, every
hotkey action, every DynamicData filter, and the `modsettings.lsx` watcher live there.

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
`Filter`/`AutoRefresh` predicates) — a deferred OAPH has no value until something subscribes.

`MainWindowViewModel` predates these helpers and uses raw `_field` + `.ToProperty(...)`. New code
should use the helpers.

### Commands

`ReactiveCommand.Create` ×51, `CreateFromTask` ×2, `CreateFromObservable` ×1.
**`CreateRunInBackground` is not used.** Long work is *not* done inside the command — the body is
synchronous and hands off via `RxApp.TaskpoolScheduler.ScheduleAsync(...)` (22 sites).
`canExecute` is always a named `IObservable<bool>` from `WhenAnyValue`, and
`outputScheduler: RxApp.MainThreadScheduler` is passed explicitly on UI-touching commands.

Cancellable progress: derive from `BaseProgressViewModel` and override `Task<bool> Run(CancellationToken)`.

**There are zero `.ThrownExceptions` subscriptions in the repo.** Everything funnels into
`RxApp.DefaultExceptionHandler`, which is assigned twice — `RxExceptionHandler` in `App.xaml.cs:54`
(non-DEBUG only), then **overwritten** by `MainWindowExceptionHandler` in `MainWindowViewModel.cs:4903`.
So `RxExceptionHandler` is effectively dead once the VM exists, and
**a throwing ReactiveCommand shows the user nothing** — `OnNext` only logs. Add an explicit
`ShowAlert` in the catch if the user needs to know.

### DynamicData 9.x

One source of truth: `SourceCache<DivinityModData, string> mods` keyed by UUID
(`MainWindowViewModel.cs:80`). Views are built at `:5239-5246`:

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

### Threading — the live bug class

**There is not a single `Dispatcher.Invoke` in `src/`.** Use `RxApp.MainThreadScheduler`.

A Fody-woven `[Reactive]` setter raises `PropertyChanged` **synchronously on the calling thread**.
So anything under `RxApp.TaskpoolScheduler.ScheduleAsync` that writes bound state must be wrapped:

```csharp
await Observable.Start(() => { LoadSettings(); }, RxApp.MainThreadScheduler);
```

That is exactly what commit `fc35358` ("Load settings on the main UI thread") added to `RefreshAsync`.
There are 22 `ScheduleAsync` sites in `MainWindowViewModel` where the same hazard lurks.

### Settings — three attributes, each doing something different

A setting on `DivinityModManagerSettings` needs **all three** or it half-works:

| Attribute | Without it |
|---|---|
| `[DataMember]` | not persisted, and never marks the Save button dirty (`InitSubscriptions()` reflects over `[DataMember]`) |
| `[Reactive]` | **never restored on load** — `SetFrom<T, ReactiveAttribute>` uses `[Reactive]` as the allow-list |
| `[SettingsEntry(name, tooltip)]` | never appears in Preferences (the window is generated by reflection, not XAML) |

`SetFrom` also **skips null values**, so a property saved as `null` silently keeps its in-memory default.

**Never reassign `Settings.ExtenderSettings` / `ExtenderUpdaterSettings` / `Window` / `Confirmations`** —
mutate in place. Dozens of subscriptions capture the original instance; swapping it dead-ends the
whole Script Extender settings UI. That was bug `7b57e5a`. `[IgnoreSetFrom]` exists but
**nothing reads it** — the real exclusion is done by omitting `[Reactive]`.

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

Windows derive from `HideWindowBase<T>` and **cancel Closing / `Hide()` instead**. They are
constructed once and live for the process — don't expect ctor/dispose logic to re-run.

**Moving a Core type between namespaces breaks XAML silently.** `SettingsWindow.xaml` carries eight
`clr-namespace:...;assembly=DivinityModManagerCore` declarations. Grep `src/GUI/**/*.xaml` after any
namespace change.

---

## 5. Code style

`.editorconfig` has only `[*.cs]` and `[*.{cs,vb}]` sections — **XAML, JSON and csproj are
unconfigured; match the surrounding file.**

- **Hard tabs**, size 4. **CRLF.** `insert_final_newline = false`. Most files have a UTF-8 **BOM** —
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

**Do not write `string?` in Core or GUI** — it yields CS8632. Only Toolbox is annotated.

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
attaches one. Enable via the `LogEnabled` setting, `DebugModeEnabled`, or **dropping an
extension-less file named `debug` next to the exe**. Logs land in `<exe>/_Logs/`, and
`LogTraceListener` scrubs `%LOCALAPPDATA%`/`%APPDATA%`/`%USERPROFILE%` — don't re-implement redaction.

```csharp
ShowAlert(message, AlertType.Info, timeoutSeconds);   // timeout 0 = until replaced
```

`ShowAlert` **already logs, already scrubs paths, and already marshals to the UI thread** — call it
from any thread and don't add a redundant `DivinityApp.Log`. When the settings window is open, use
`SettingsWindowViewModel.ShowAlert` instead. From Core, go via
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
`UUID = <full pak path>`. That's why `HasInvalidUUID` exists; never assume `Guid.Parse` succeeds.

### Base-game knowledge lives in JSON, not code

`src/Core/DivinityApp.cs:44-47` hardcodes only the campaign UUIDs
(`MAIN_CAMPAIGN_UUID = GUSTAVX_UUID`, Patch 8+). Everything else is
**`src/GUI/Resources/IgnoredMods.json`**.

**When the game patches, this is what needs updating** (what upstream's "Update base mod versions"
commits do):

1. Bump the packed `Version` int of each changed Larian module.
2. Add any new Larian module to **both** the `Mods` array **and** `IgnoreDependencies` — forgetting
   the second produces a wave of false "missing dependency" reports.
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

### Known dead code — don't go looking for logic that isn't there

- `src/Core/Models/Conflicts/*` — **entirely unreferenced.** There is no file-level conflict analysis.
  "Conflicts" in the UI is only what a mod declares in its own `meta.lsx`.
- `DivinityModVersion` (v1, superseded by `DivinityModVersion2`), `DivinityGameMasterCampaign` (a DOS2 feature).
- `src/GUI/Views/VerticalModLayout.xaml(.cs)` and `LeaderLibSettingsWindow.xaml(.cs)` — never instantiated.
- The **"Mod Updates" view is dead UI** — `ModUpdatesViewData.Mods` is never populated and the
  `RefreshAllModUpdatesBackground()` call in `RefreshAsync` is commented out.
- `DivinityApp.GetAppDirectory()` has a **dead statement** (`DivinityApp.cs:132`): it computes the
  process-path directory (comment says it's for Proton) and discards it, always returning
  `AppContext.BaseDirectory`. Every settings/log/Orders path flows through this.

---

## 7. Integrations

`src/GUI/Resources/AppFeatures.json` is the master switch:
`{"ScriptExtender": true, "NexusMods": false, "Workshop": false}`.
**Nexus Mods code ships but is disabled**; `"Workshop"` is never even queried.

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

`WebHelper` **swallows every exception** and returns `Stream.Null` / `""` — silent network failure
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
| Script Extender update does nothing | built `Release\|x64` (Toolbox skipped) or GUI-only (§2.2) |
| New setting doesn't persist / doesn't restore / doesn't show | missing one of `[DataMember]` / `[Reactive]` / `[SettingsEntry]` (§4) |
| Extender settings UI stops updating | reassigned `Settings.ExtenderSettings` instead of mutating (§4) |
| UI doesn't refresh when a property changes | `Filter` without `AutoRefresh`, or `ToUIProperty` where `ToUIPropertyImmediate` was needed (§4) |
| Cross-thread / stale binding after a refresh | wrote `[Reactive]` state off the UI thread (§4) |
| Command throws, user sees nothing | no `ThrownExceptions`; handler only logs; logging is off by default (§4, §5) |
| UUIDs match nothing | wrong `ByteSwapGuids` setting (§6) |
| Mod won't appear in the load order | it's force-loaded/override, by design (§6) |
| Mass "missing dependency" after a patch | new Larian module missing from `IgnoreDependencies` (§6) |
| Edited `Resources/*.json`, nothing changed | runtime reads `<exe>/Resources`, needs a rebuild (§6) |
| Works in Debug, fails in Publish | the `_Lib` flattening + `AssemblyResolve` hook (§2.4) |
| Fork build updates itself to upstream | `URL_UPDATE` still points at LaughingLeader (§8.1) |
