# AVA: Is upstream's Avalonia rewrite a credible path for "Linux kept open"? (static assessment)

Scope: static reading of `upstream/avalonia` at `a9b61fb` (worktree `scratchpad/wt-avalonia`, submodules fetched as source with `--depth 1`) compared with WPF `master` at `fc35358`. Nothing was built, restored or run. All paths below are relative to the avalonia worktree unless prefixed `master:`. Anything I could not confirm by reading is labelled **inferred**.

**Bottom line:** the branch solves the hardest Linux blocker. LSLib is fully managed, there is no C++/CLI and no `-windows` TFM, and it has real Linux and Proton code paths. But it is an unreleased rewrite on a preview UI stack. It has not moved in 8 months, its release/Publish pipeline is broken, and it has almost no tests. It carries most of the export/import defects the fork plans to fix (E1, E3, E5, E6, I2, I3, I4, I5) and adds new ones. It is a quarry for ideas and code, not a base to adopt. The plan's choice of WPF master stands. The cheapest way to keep Linux open is to make master's Core build with plain `dotnet build` (option b), which also helps Windows.

---

## Owner intentions gleaned (as asked in the relayed request)

From `CLAUDE.md`, `implementation-plan.txt` and `plan-overview.txt`:

1. **Who it's for:** a fork for yourself and friends, published publicly. It has four workstreams: bugfixes (zip export corruption, archive import losses), performance (startup, scrolling, filtering), UI/UX refresh, and features (multi-order export, categories/dividers, overrides folded into the order; later a downloader, order sync with friends, native-mod management, all parked until each has a spec).
2. **Platform:** stay on WPF `master`. Windows now, Linux kept open, Steam Deck nice-to-have. Prefer toolkit-neutral choices where they are free, and keep behaviour in view models and Core rather than code-behind so a later port is possible.
3. **Upstream:** only bugfixes and perf go upstream, on `fix/*`, `perf/*` or `chore/*` branches cut from clean `master`. Fork-only work goes on `fork/main` (`feat/*`, `ui/*`). Merge with `--no-ff`, merge rather than rebase when syncing, never enable the `merge.ours` driver.
4. **Method:** measure first, with the Stage 0 baseline (1,121 mods, 935 active, ~15.2 s cold start of which 9.27 s is mod loading, 478 MB). Correctness outranks speed: snapshot and diff the mod list around perf changes (R1). Verify exports by booting the game.
5. **Errors visible:** every failure surfaces through `ShowAlert`, not just the log (R8).
6. **Reuse:** a stricter stance than MIT requires. Take only general ideas from Redux, never code.
7. **Safety before sharing:** repoint the updater (Stage 1) before any public build.
8. **House rules:** keep the ReactiveUI.Fody 19.5.41 + ReactiveUI 20 pin; tabs, CRLF, BOM; no `Divinity` prefix on new types.
9. **Specific product wishes:** fuzzy search (after the per-keystroke cost is fixed), categories as flat divider rows rather than WPF grouping, overrides as a normal category, and multi-order zip export deduplicated by UUID with an explicit version-conflict rule.

---

## Linux buildability (static)

### What the branch targets

| Item | Evidence | Linux implication |
|---|---|---|
| TFM | `net10.0` everywhere: `src/Directory.build.props:3`, `src/Core/Core.csproj:3`, `src/GUI/ModManager/ModManager.csproj:3`, `src/GUI/Desktop-Avalonia/Desktop-Avalonia.csproj:3`, `src/Services/Services.csproj:3`, `src/Tests/Tests.csproj:3`, `src/Toolbox/Toolbox.csproj:3`. SourceGenerator is `netstandard2.0`. | No `-windows` TFM and no `UseWPF`, so the reference assemblies are the same on every OS. If it compiles on Windows, the C# compiles on Linux; what can differ is MSBuild and tooling behaviour. Needs the .NET 10 SDK. |
| RIDs | `Desktop-Avalonia.csproj:19` `<RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers>`; the per-OS `RuntimeIdentifier` block is commented out (`:32-38`). | Restore pulls packs for both RIDs. |
| Solution | `BG3ModManager.sln` lists only `.csproj` projects: no `.vcxproj` and no C++. | Unlike master, `dotnet build` can in principle build the whole solution. |
| LSLib | `Core.csproj:21-22` → `External/LSLibNet/src/LSLib` and `LSLibStats`. LSLibNet README: "the native part replaced with C# libraries". Compression uses `K4os.Compression.LZ4`, `ZstdSharp.Port`, `DotFastLZ`, `System.IO.Compression` (`External/LSLibNet/Directory.Packages.props`). The old native calls are commented out (`LSLib/LS/Pak/PackageReader.cs:127`, `LSLib/VirtualTextures/Compression.cs:55,149`). `LSGranny` (still needs LSLibNative) is not referenced. | **The Linux blocker on master is gone.** On master, `External/lslib/LSLib/LSLib.csproj:44` references `LSLibNative.vcxproj`, and `LS/Compression.cs:98,200` and `LS/PackageReader.cs:213` call `Native.LZ4FrameCompressor`. |
| Central packages | `Directory.Packages.props`: Avalonia `12.0.0-preview1`, ReactiveUI 23.1.1 + SourceGenerators 2.6.1, `ReactiveUI.Avalonia 11.4.3`, `Xaml.Behaviors.Avalonia 11.3.9.5`, `Material.Icons.Avalonia 3.0.0-avalonia12`, xunit.v3 3.2.2. `External/Directory.Packages.props` turns CPM off for submodules; SukiUI and LSLibNet each carry their own. | Preview Avalonia mixed with 11.x-era add-ons. Binary compatibility can't be judged statically (**inferred risk**). |
| Windows-only packages | None. `Microsoft.Win32.Registry` and `Microsoft.VisualBasic` come from the shared framework and throw at runtime off Windows. | Compiles on Linux; behaviour is guarded at runtime (see the Map). |
| Native deps | Avalonia/Skia natives via `SukiUI.csproj:24-25` (`SkiaSharp`, `SkiaSharp.NativeAssets.Linux`). CrossSpeak ships `lib/linux/libspeechdwrapper.so` plus Windows DLLs (`External/CrossSpeak/CrossSpeak/CrossSpeak.csproj:15-21`). | Runtime needs X11/XWayland, fontconfig and the Skia/HarfBuzz natives (**inferred**, standard for Avalonia). |
| Conditional code | `OperatingSystem.IsWindows/IsLinux` branches in `Services/Services/RegistryService.cs:197-204`, `PathwaysService.cs:111,127`, `BackgroundCommandService.cs:53-59`, `ScreenReaderService.cs:17-25`, `Core/Util/ProcessHelper.cs:62-114`, `FileUtils.cs:461-524`, `StringUtils.cs:58-64`, `MainWindowViewModel.cs:338,433,452,1419`. | It was written with Linux in mind. The author clearly tried it on Linux (Proton paths, `xdg-open`, SE detection by byte scan). |

### What `dotnet build` on Linux would most likely hit, in order (all **inferred**, none run)

1. **`dotnet build BG3ModManager.sln -r linux-x64` stops immediately with NETSDK1134** (a solution build with a single RID is not supported). Build the solution without `-r`, or build the Desktop project with `-r`.
2. **Building a project directly (not the .sln) may fail SDK resolution with NETSDK1141.** `External/Avalonia.Controls.TreeDataGrid/global.json` pins `"version": "8.0.101", "rollForward": "latestFeature"`, while that project targets `net10.0` (`.../Avalonia.Controls.TreeDataGrid.csproj:3`). When no solution is in play, MSBuild's SDK resolver looks for global.json from the project's own directory (medium confidence). A `.sln` build at the repo root (no global.json there) avoids it. Applies equally on Windows.
3. **Lower-case `Directory.build.props` files are silently ignored on Linux.** Every one under `src/` (`src/Directory.build.props`, `src/Core/Directory.build.props`, `src/GUI/Directory.build.props`, …) and `External/LSLibNet/Directory.build.props` is named with a lower-case `b`. MSBuild only auto-imports `Directory.Build.props`, so on a case-sensitive filesystem none are imported. Lost on Linux: `Nullable=enable` (Core, ModManager, Desktop-Avalonia, Locale and Tests don't set it themselves), `PlatformTarget x64`, `Configurations`, `DebugType/Optimize`, and the `obj\<Name>\` redirection. Expect a flood of CS8632 warnings and a build that differs from Windows. `.editorconfig:234` makes CS8618 an *error*, but only under nullable, so the Linux build is more lenient than Windows. Not build-breaking, but a silent divergence (High confidence; this is how MSBuild's `GetDirectoryNameOfFileAbove` behaves on case-sensitive filesystems).
4. **The parser generators run at build time.** `LSLib.csproj` and `LSLibStats.csproj` use the `YaccLexTools` 1.2.3 package (`YaccFile`/`LexFile` items), and the generated `*.yy.cs`/`*.lex.cs` files are not committed (`git ls-files` shows none). nuget.org lists `YaccLexTools.Gplex` 1.2.3.1 assets for net462/netstandard2.0/net6/8/9 but not net10. Whether its targets launch the tool as `dotnet gplex.dll` (works if a net8/9 runtime is present or rolls forward) or as a .NET Framework `.exe` (fails on Linux) is **unverified**. This is the most likely *first real compile-stage failure*. Check it by reading `~/.nuget/packages/yacclextools*/**/build/*.targets` after a restore.
5. **CrossSpeak packs on every build** (`GeneratePackageOnBuild=True`, hand-written nuspec with `<file src="bin\Debug\netstandard2.0\CrossSpeak.dll">`, same submodule commit `58b7178` as master). Debug works; any non-Debug configuration hits the same 0.2b trap documented for master. The backslash `src` paths in the nuspec are probably normalised by NuGet on Linux (**inferred**, low risk). SukiUI also packs on build (`SukiUI.csproj:14`), which is harmless.
6. **Publish/PublishTest are Windows-only and broken by design** (see AVA-06). `Desktop-Avalonia.csproj:51` `<PreBuildEvent>rd /s /q $(TargetDir)_Lib</PreBuildEvent>` gives `rd: not found` → MSB3073 on Linux. `:78` runs `python BuildRelease.py` (many distros only have `python3`). `:61-72` moves all DLLs into `_Lib`, but nothing resolves them at startup.

### What `dotnet test src/Tests` would most likely hit

- **Build:** Tests → Services → Core → LSLibNet/CrossSpeak/NexusModsNET/modio/Locale. No Avalonia project is in the closure, so items 1–2 above don't apply but items 3–5 do. `Tests.csproj:8` declares only `Debug`, which is fine for `dotnet test`.
- **No UI tests:** no `Avalonia.Headless` package anywhere. The tests are UI-free.
- **Run: expect 0 of 5 test cases to pass** (High confidence from reading):
  - `NexusModsTests` (3 cases) calls `Assert.False(String.IsNullOrEmpty(apiKey))` in its **constructor** (`src/Tests/NexusModsTests.cs:30`). Without `NEXUSMODS_API_KEY` and network access, every case fails rather than skips.
  - `ImportTests` (2 cases, 42 KB and 500 MB) never registers `IFileSystemService`. `FileUtils`/`ImportUtils` capture `_fs` once in their static constructors (`Core/Util/FileUtils.cs:18-22`, `Core/Util/ImportUtils.cs:66-70`), so `_fs` is null. `CreatePackageAsync` throws a null reference inside its try and returns false, and the test fails at "Failed to create pak". It would pass only if `NexusModsTests` had run first in the same process *with* a key.
  - On Linux, `ImportTests` also builds paths like `Path.Combine(pakDataPath, @"Mods\Test\meta.lsx")` (`src/Tests/ImportTests.cs:30-31`). That creates a single file literally named `Mods\Test\meta.lsx`, so the pak would lack `Mods/Test/meta.lsx` and the import would find no mod.
  - `Dispose` calls non-recursive `Directory.Delete(testDir)` inside an empty catch, so test artefacts pile up, including a 500 MB sparse file.

---

## Map of the avalonia branch

### Project structure

| Project | Path | Size (static count) | Role |
|---|---|---|---|
| Core | `src/Core/Core.csproj` (`ModManager.Core`) | 194 .cs, ~13.8k lines | Models, loaders (`Util/ModDataLoader.cs` 1,757 lines), `Util/Pak/DirectoryPakParser.cs`, `ImportUtils`, `FileUtils`, service interfaces. References LSLibNet, NexusModsNET, modio.net and Locale (a GUI→Core layering inversion). |
| Services | `src/Services/Services.csproj` | 34 .cs, ~4.0k | Concrete services: `RegistryService` + `Reg/{Windows,Linux}RegistryHelper`, `PathwaysService`, `SettingsService`, `ModSettingsExportService`, `NexusModsService`, `ModioService`, `GithubService`, `ModUpdaterService`, `DownloadManagerService`, `ScreenReaderService`, `BackgroundCommandService` (named-pipe single-instance arg forwarding). DI is a Splat source generator (`SplatContainerExtensions.cs`). |
| ModManager (GUI lib) | `src/GUI/ModManager/ModManager.csproj` | 117 .cs + 50 .axaml, ~16.9k | All view models and views. `ModOrderViewModel.cs` (2,333 lines), `MainWindowViewModel.cs` (1,883), `MainCommandBarViewModel.cs` (986), `Services/ModImportService.cs` (725, holds import **and** archive export), `Views/Mods/ModListView.axaml(.cs)` (TreeDataGrid). |
| Desktop-Avalonia | `src/GUI/Desktop-Avalonia/` | 1 .cs (`Program.cs`, 75 lines) | Exe host (`BG3ModManager`, `AssemblyVersion 2.0.0.0`), single-instance check, `UsePlatformDetect()`. |
| Locale | `src/GUI/Locale/` | resx + 5k-line Designer.cs | Localisation (new; master has none). |
| SourceGenerator | `src/SourceGenerator/` | 8 .cs | Roslyn generators for settings views and hotkeys (replaces master's runtime reflection). |
| Tests | `src/Tests/` | 4 .cs, 267 lines | 2 test classes, 5 cases (above). |
| Toolbox | `src/Toolbox/` | 5 .cs | Script Extender updater; `kernel32` `LoadLibrary`, so Windows-only (`Toolbox/NativeMethods.cs:7-15`). |
| External | 8 submodules | | `LSLibNet`, `NexusModsNET`, `modio.net`, `CrossSpeak`, plus Avalonia-12 forks by LaughingLeader of `SukiUI`, `Avalonia.Labs`, `Avalonia.Controls.TreeDataGrid`, `Avalonia.MarkdownViewer` (each "Update to avalonia 12", 2026-03-03). |

### Activity and release state

- Tip `a9b61fb` dated 2026-03-07. `git ls-remote` against the upstream repo today (2026-11-03) still returns `a9b61fb` for `avalonia` and `fc35358` for `master`. **Neither branch has moved in about 8 months.**
- The last month of activity (2026-02-26 → 03-07) was the Avalonia 12 migration, a download manager and fixes. `Svg.Skia` was removed ("no Avalonia 12 support").
- `Update.xml` says `1.0.10.0`, while `Desktop-Avalonia.csproj:9` says `2.0.0.0`. The README still tells users to install .NET Framework 4.7.2. The "944 commits ahead" figure in CLAUDE.md could not be re-checked here: the local clone is shallow.

### What stays Windows-specific

| Area | Location | Behaviour off Windows |
|---|---|---|
| Registry | `Services/Services/Reg/WindowsRegistryHelper.cs` (Steam, GOG, uninstall keys, `nxm://`) | Not used. `LinuxRegistryHelper` uses XDG paths. `GetApplicationInstallPath` throws `NotImplementedException` (`Reg/LinuxRegistryHelper.cs:9-12`); its only caller, `DirectoryOpusService`, is Windows-guarded. NXM on Linux is a TODO (`RegistryService.cs:204`). |
| Recycle-bin deletes | `Core/Util/RecycleBinHelper.cs:47-62` calls `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., UIOption, RecycleOption)` | Throws on Linux (**inferred**, AVA-08), so every delete fails, logged only. |
| Junctions | `Core/Util/JunctionPoint.cs` (kernel32) via `RegistryService.GetTruePath` | Never called (only declared on `IRegistryService`), so harmless. |
| Native screen-reader DLLs | `Services/Services/ScreenReaderService.cs:60-80` → `NativeLibraryHelper.LoadLibrary` (kernel32) | Only runs if a file exists in `_Lib` (Publish), and the exception is caught. CrossSpeak picks `libspeechdwrapper.so` on Linux. |
| Script Extender updater | `MainWindowViewModel.cs:244-290` runs `Tools/Toolbox.exe` (`Core/DivinityApp.cs:230`) only if `UpdaterVersion >= 4` (`:255`). `UpdaterVersion` is set only in the Windows `FileVersionInfo` branch (`:338-355`) and defaults to `-1` (`Core/Models/Settings/ScriptExtenderUpdateConfig.cs:88`). | SE auto-update is skipped on Linux. Detection still works: a byte scan of `DWrite.dll` (`:379-402`) and version folder names (`:452-470`). |
| Window position restore | `MainWindowViewModel.cs:1419-1423` | Windows-only, with a TODO for Wayland/X11. |
| Process launch | `Core/Util/ProcessHelper.cs:62-114` | `xdg-open` on Linux, `open` on macOS. |
| Permissions | `Core/Util/FileUtils.cs:461-524` | ACLs on Windows, `UnixFileMode` on Linux. |
| Backslash literals | `Core/Util/ModDataLoader.cs:56` (save-path profile regex `PlayerProfiles\\(.*?)\\Savegames`), `:1276` (`@"Launcher\Settings"`), `Core/Util/RuntimeHelper.cs:5` (`C:\Program Files\dotnet...`, unused) | Save import loses the profile-name prefix; launcher preference updates are skipped on Linux. |
| Game launch | `MainWindowViewModel.cs:640-700`: Exe / Steam (`steam://run/1086940//…`) / Custom; default `LaunchGameType.Exe` (`Core/Models/Settings/ModManagerSettings.cs:62`) | On Linux the default tries to exec `bg3.exe` directly. The user must switch to Steam. |

### Finding BG3 data on Linux, Proton and Steam Deck

- `RegistryService.GetAppDataPath(steamAppId)` (`Services/Services/RegistryService.cs:159-170`) first tries `GetProtonDataPath` (`:55-68`): `<SteamRoot>/steamapps/compatdata/1086940/pfx/drive_c/users/steamuser/AppData/Local`, which is exactly the Deck layout. Failing that, it falls back to `$XDG_DATA_HOME` or `~/.local/share` (`Reg/LinuxRegistryHelper.cs:19-37`).
- `SteamRoot` is only `$XDG_DATA_HOME/Steam` or `~/.local/share/Steam` (`LinuxRegistryHelper.cs:39-60`). It does **not** check Flatpak Steam (`~/.var/app/com.valvesoftware.Steam/.local/share/Steam`) or other library folders. Compatdata lives in the library that holds the game, so a **Deck install on the SD card** (`/run/media/<card>/steamapps/compatdata/1086940/...`) is missed. The fallback then points at `~/.local/share/Larian Studios/Baldur's Gate 3`, which doesn't exist (BG3 has no native Linux build).
- The game install itself is found via `appmanifest_1086940.acf` plus `libraryfolders.vdf` (`RegistryService.cs:70-157`), so the SD-card *game* folder is found even though its *profile* folder is not.
- `PathwaysService.SetGamePathways` normalises `\` to `/` after joining the JSON's Windows-style relative paths (`Services/Services/PathwaysService.cs:63,87,102`; `GUI/ModManager/Resources/DefaultPathways.json`). It also looks for a non-existent native `bin/bg3` on Linux (`:111-121`), which is harmless. GOG on Linux (Heroic/Lutris) is not handled: the GOG branch is Windows-only (`:127`).

### Feature parity with WPF master (main flows)

| Flow | avalonia | Evidence |
|---|---|---|
| Load mods (paks, loose, builtin) | Yes, restructured | `Core/Util/Pak/DirectoryPakParser.cs:136-160` (all paks via `Task.WhenAll`) |
| Edit order (drag/drop rows) | Yes | `Views/Mods/ModListView.axaml:164` `AutoDragDropRows="True"`; `ModListView.axaml.cs:60-140` |
| Drag files in to import | Yes | `ModListView.axaml.cs:502-526` |
| Export to `modsettings.lsx` | Yes, flattens containers | `ModOrderViewModel.cs:810-880`; `ModDataLoader.cs:1358-1389`; `Services/Services/ModSettingsExportService.cs` (still `string.Format` templates) |
| Export order to zip | Yes, with defects | `GUI/ModManager/Services/ModImportService.cs:573-690` (AVA-12) |
| Import archive (zip/7z/rar/tar; new: .bz2/.xz/.zst) | Yes, with defects | `Core/Util/ImportUtils.cs:72-180, 183-320` (AVA-13) |
| Import from save (.lsv) | Yes | `ModOrderViewModel.cs:1630`; `ModDataLoader.cs:1459` |
| Profiles | Yes | `ModOrderViewModel.cs:60, 236-300` |
| Settings / keybindings | Yes, source-generated views | `src/SourceGenerator/Generators/SettingsViewGenerator.cs`; `Views/Generated/*.axaml` |
| Filters (`@author` syntax) | Yes, same design as master | `Core/Util/ModUtils.cs:182-277` |
| Script Extender | Yes on Windows; detection only on Linux | above |
| Categories | **New:** nested `ModContainer` | `Core/Models/Mod/ModContainer.cs`, `Models/Mod/Order/ModOrderContainer.cs` |
| Nexus / mod.io / GitHub updates | New and enabled (`Resources/AppFeatures.json`: all true), partly TODO | `Services/Services/ModUpdaterService.cs:125,206` |
| Pak explorer, stats validator, version generator | New windows | `Windows/*.axaml` |
| Old order files | Reads master's v1/v2 formats (`ModDataLoader.cs:1070-1093`). Master cannot read the new polymorphic `Entries` format (`Models/Mod/Order/ModOrder.cs:23`, `IModOrderEntry` with `Type`) (**inferred**). | |

### Visible TODOs and unfinished areas

26 `TODO`s and 4 `NotImplementedException`s in `src/`. The ones that matter:

- `ViewModels/ExportOrderToArchiveViewModel.cs:52-57`: `Run()` selects entries, does nothing and returns `true`. The richer export dialog is a stub.
- `ViewModels/Main/MainCommandBarViewModel.cs:534-535`: `ReloadModsCommand = ReactiveCommand.Create(() => { }, …)`, an empty command. `:287-291` keeps an `[Obsolete] NotImplemented()` helper.
- `MainWindowViewModel.cs:1398,1809,1812-1813`: commented-out hotkey actions (ExtractSelectedAdventure, CollectionDownloader).
- `ModOrderViewModel.cs:215`: "Update since ScreenReaderHelper used native dlls like Tolk". `:1179` "Save Order?". `:2275` `_ => throw new NotImplementedException()`.
- `DownloadActivityBarViewModel.cs:78`: queued downloads not shown (the download manager was started 2026-03-04/05).
- Row lifecycle: `ModListView.axaml.cs:617-632, 671-683` has the per-row disposal code commented out. `PrepareRow` (`:577-614`) rebinds and walks container children on every `RowPrepared`.
- Publish pipeline: no `_Lib` resolver (AVA-06).

### What the tests cover

Only pak creation plus a single-file import (`ImportTests`) and Nexus API fetch/download (`NexusModsTests`). Nothing covers order building, `modsettings.lsx` output, archive export, filters, containers, settings round-trip or path discovery. No UI or headless tests, no CI (`.github/` holds only issue templates).

---

## Findings

### [Info] AVA-01: The branch has stalled for 8 months and is unreleased
- **Location:** branch tip `a9b61fb`; `Update.xml:3`; `src/GUI/Desktop-Avalonia/Desktop-Avalonia.csproj:9`; `README.md` Setup step 2.
- **What:** the last commit is 2026-03-07, and the remote tip is unchanged as of 2026-11-03. The release manifest says 1.0.10.0, the assembly says 2.0.0.0, and the README describes .NET Framework 4.7.2.
- **Evidence:** `git log -1` in the worktree; `git ls-remote https://github.com/LaughingLeader/BG3ModManager.git refs/heads/avalonia` → `a9b61fb…`.
- **Implication:** confirms CLAUDE.md §1. No upstream activity to ride on; adopting it means owning it.
- **Confidence:** High.

### [Info] AVA-02: LSLib is fully managed in avalonia (the master Linux blocker is solved there)
- **Location:** `src/Core/Core.csproj:21-22`; `External/LSLibNet/README.md`; `External/LSLibNet/src/LSLib/LS/Compression.cs` (K4os LZ4, ZstdSharp, ZLibStream); `LS/Pak/PackageReader.cs:127-140` (`LZ4Frame.Decode` replacing `Native.LZ4FrameCompressor`).
- **What:** no C++/CLI, no `.vcxproj` in the solution. Granny (still native) lives in `LSGranny`, which nothing references.
- **Evidence:** a grep for `DllImport|LibraryImport|LSLibNative` in LSLibNet (excluding LSGranny) finds only commented-out lines. On master: `External/lslib/LSLib/LSLib.csproj:44`, `LS/Compression.cs:98,200`, `LS/PackageReader.cs:213`.
- **Implication:** LSLibNet is the reusable piece for option (b). Its public API mostly matches what master's Core uses (`VFS`, `ResourceUtils`, `PackageReader` moved to namespace `LSLib.LS.Pak`, `PackageWriterFactory`, `PackageBuildData`, `ResourceConversionParameters`). It targets `net10.0` only, so master would need net10 or a multi-target. It is a stripped fork, last commit 2025-12-14; Norbyte's format updates after a BG3 patch must be ported by hand (**inferred**).
- **Confidence:** High (managed); Medium (API fit).

### [Medium] AVA-03: Lower-case `Directory.build.props` files are not imported on Linux
- **Location:** `src/Directory.build.props`, `src/Core/Directory.build.props`, `src/GUI/Directory.build.props`, `src/GUI/ModManager/Directory.build.props`, `src/GUI/Desktop-Avalonia/Directory.build.props`, `src/Services/…`, `src/Tests/…`, `src/Toolbox/…`, `External/LSLibNet/Directory.build.props`.
- **What:** MSBuild auto-imports `Directory.Build.props` by exact name. On a case-sensitive filesystem these files are skipped, dropping `Nullable=enable`, `PlatformTarget`, `Configurations`, `DebugType`/`Optimize` and the `obj` redirect.
- **Evidence:** file names as listed. `Core.csproj`, `ModManager.csproj` and `Desktop-Avalonia.csproj` don't set `Nullable` themselves; `.editorconfig:234` sets CS8618 to `error`.
- **Implication:** a Linux build compiles a different configuration from Windows: thousands of CS8632 warnings and none of the CS8618 errors. Master has the same lower-case files (CLAUDE.md §5 "five Directory.build.props"), so this matters to option (b) too. A rename is a trivial, upstreamable `chore/*`.
- **Confidence:** High (inferred from MSBuild's lookup; not run).

### [Medium] AVA-04: Build-time parser generation via YaccLexTools is unverified on Linux
- **Location:** `External/LSLibNet/src/LSLib/LSLib.csproj` (`YaccFile`/`LexFile` items, `PackageReference Include="YaccLexTools"`), `External/LSLibNet/src/LSLibStats/LSLibStats.csproj`; `External/LSLibNet/Directory.Packages.props` (`YaccLexTools 1.2.3`).
- **What:** generated `*.yy.cs`/`*.lex.cs` are not committed. The tool assets per nuget.org: net462/netstandard2.0/net6/8/9 (no net10).
- **Evidence:** `git ls-files` in LSLibNet shows no generated parser files. The nuget.org package page was read; the package itself was not downloaded.
- **Implication:** probably the first compile-stage failure on a Linux box with only the .NET 10 runtime, or if the targets exec an `.exe`. Check this first.
- **Confidence:** Low–Medium (inferred).

### [Medium] AVA-05: Two SDK traps for command-line builds (NETSDK1134, NETSDK1141)
- **Location:** `External/Avalonia.Controls.TreeDataGrid/global.json` (`8.0.101`, `latestFeature`) vs `.../src/Avalonia.Controls.TreeDataGrid/Avalonia.Controls.TreeDataGrid.csproj:3` (`net10.0`).
- **What:** `-r` on a solution is rejected (NETSDK1134). Building `Desktop-Avalonia.csproj` directly makes the resolver find the submodule's global.json for that project and demand an 8.0 feature-band SDK, which then can't target net10 (NETSDK1141).
- **Evidence:** file contents as cited.
- **Implication:** verify with a `.sln` build without `-r` first. The portable output runs on Linux via `dotnet BG3ModManager.dll`.
- **Confidence:** Medium (inferred from SDK resolver behaviour).

### [High] AVA-06: The Publish/release pipeline is Windows-only and would produce an app that can't start
- **Location:** `src/GUI/Desktop-Avalonia/Desktop-Avalonia.csproj:49-79`; `src/GUI/Desktop-Avalonia/Program.cs` (whole file); `src/Core/DivinityApp.cs:230`.
- **What:** `rd /s /q $(TargetDir)_Lib` (the same evaluation-order accident as master's pre-0.2 code). `python BuildRelease.py`. `SetupLibFolder` moves every DLL except `BG3ModManager.dll` into `_Lib`, but unlike master (`master:src/GUI/Program.cs:13-29`) there is **no `AssemblyResolve` hook** and no probing path, so `ModManager.dll`, `ModManager.Core.dll` etc. can't be found at startup. `RemoveDir Directories="(OutputPath)_Logs;…"` is missing its `$` (`:71`). `GetToolboxPath()` hard-codes `Toolbox.exe`.
- **Evidence:** a grep for `AssemblyResolve|AssemblyLoadContext|ProbingPath` across `src/` finds nothing.
- **Implication:** there is no shippable build path on any OS, consistent with "never released". A Linux release would need a new packaging story (tarball, AppImage or Flatpak).
- **Confidence:** High for the missing resolver; Medium that start-up fails (not run).

### [High] AVA-07: The tests prove almost nothing and would all fail in a default run
- **Location:** `src/Tests/ImportTests.cs`, `src/Tests/NexusModsTests.cs`, `src/Tests/Tests.csproj`.
- **What:** 5 cases. Three need a Nexus API key and network and fail rather than skip. Two depend on a service-locator registration that no test performs. Windows path literals break them on Linux. No coverage of order export, `modsettings.lsx`, archive round-trip, filters or path discovery. No Avalonia.Headless.
- **Evidence:** `NexusModsTests.cs` constructor `Assert.False(String.IsNullOrEmpty(apiKey), …)`; `Core/Util/FileUtils.cs:18-22`; `ImportTests.cs` `@"Mods\Test\meta.lsx"`.
- **Implication:** "the avalonia branch has tests" (CLAUDE.md §1 table) is true in name only. It gives no safety net for adopting it. The pattern (a static `_fs` grabbed once from `AppLocator`) also makes Core untestable without the full container.
- **Confidence:** High.

### [High] AVA-08: Deleting files on Linux probably always fails, silently
- **Location:** `src/Core/Util/RecycleBinHelper.cs:47-62`; callers `ViewModels/Main/DeleteFilesViewModel.cs:76`, `ModOrderViewModel.cs:973`, `MainWindowViewModel.cs:1353`, `Core/Models/Updates/ModDownloadData.cs:43`, `Core/ModUpdater/Cache/IExternalModCacheDataExtensions.cs:72`.
- **What:** `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs|AllDialogs, RecycleOption.*)`. Any `UIOption` other than "no UI", or a recycle request, goes through the Windows shell path, which is not supported on Unix. The exception is caught and only logged.
- **Evidence:** code as cited. The runtime behaviour is **inferred** from .NET's VisualBasic implementation, not run.
- **Implication:** "Delete selected mods", deleting orders, and replacing downloaded files would quietly do nothing on Linux. On a Linux path this needs a `File.Delete`/freedesktop-trash fallback.
- **Confidence:** Medium.

### [Medium] AVA-09: Proton profile discovery misses SD-card libraries and Flatpak Steam
- **Location:** `Services/Services/RegistryService.cs:55-68, 159-170`; `Services/Services/Reg/LinuxRegistryHelper.cs:19-60`.
- **What:** compatdata is only looked up under the primary Steam root (`~/.local/share/Steam`). The game's library (from `libraryfolders.vdf`, already parsed at `:109-150`) is not used for compatdata, and no Flatpak path is checked.
- **Evidence:** code as cited.
- **Implication:** the standard Deck internal-SSD install works. An SD-card or Flatpak install falls back to a non-existent `~/.local/share/Larian Studios`. Settings allow an override (`DocumentsFolderPathOverride`, `PathwaysService.cs:23-26`). The fix is small and toolkit-neutral; the same logic could go into master's Core for option (b).
- **Confidence:** High (code); the Deck layout is per the task brief.

### [Medium] AVA-10: Linux defaults are wrong for launch and Script Extender
- **Location:** `Core/Models/Settings/ModManagerSettings.cs:62` (`[DefaultValue(LaunchGameType.Exe)]`); `MainWindowViewModel.cs:244-290, 338-355`; `Core/Models/Settings/ScriptExtenderUpdateConfig.cs:88`.
- **What:** the default "Exe" launch would exec `bg3.exe` directly on Linux; the Steam option (`steam://run/1086940//…`) exists but isn't the default there. SE auto-update via Toolbox is effectively disabled on Linux (`UpdaterVersion` stays `-1`; Toolbox is kernel32-based).
- **Evidence:** code as cited.
- **Implication:** usable on Linux after changing one setting. SE updates rely on the in-game DWrite updater under Proton (**inferred** to work).
- **Confidence:** High (code).

### [Low] AVA-11: The single-instance check can kill a `dotnet`-hosted launch
- **Location:** `src/GUI/Desktop-Avalonia/Program.cs:17-47`.
- **What:** `Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1` → exit. In non-DEBUG builds it exits whenever another process has the same name. Launched as `dotnet BG3ModManager.dll`, that name is `dotnet`, so a lingering MSBuild node or language server closes the app immediately with no message.
- **Evidence:** code as cited (`#if !DEBUG return true;`).
- **Implication:** launch through the apphost `./BG3ModManager` when testing Release. The named-pipe forwarding itself is cross-platform.
- **Confidence:** High (code); impact Low.

### [High] AVA-12: Archive export carries E1, E3, E5 and E6 and adds two new data-loss paths
- **Location:** `src/GUI/ModManager/Services/ModImportService.cs:573-690`.
- **What:**
  - E1 is still present: `_fs.File.OpenWrite(outputPath)` (`:602`), so no truncation.
  - E3 is still present: `if (token.IsCancellationRequested) return false;` inside the `using` (`:618`) leaves a partial zip and skips the temp cleanup.
  - E5 is present and worse: `_fs.Directory.Delete(tempDir)` (`:678`) is non-recursive and outside the try. `tempDir` is the shared `<app>/Temp` (`:580`, the same folder downloads use; `NexusModsTests` writes there too). It is no longer created up front (master `:3557` did), so it throws when no loose mod created it, or when anything else is in it.
  - E6 is still present: `100d / modPaks.Count` (`:597`).
  - E2 is half-fixed: an exception now aborts the export with an alert (`:661-668`). But a failed loose-mod package (`CreatePackageAsync` false, `:648`) and mods with an invalid `FilePath` (`:623-625`) are skipped silently.
  - E4 is solved differently: SharpCompress async writer (`:603`, `WriteAsync`), no spin loop.
  - **New, data loss:** `modPaks` is built from `selectedModOrder.Entries.Any(o => o.Id == x.UUID)` (`:595`), top-level entries only, so **every mod inside a container/category is left out of the zip**. `ModOrder.GetModIds()`/`GetFlattenedEntries()` exist (`Core/Models/Mod/Order/ModOrder.cs:86-125`) but aren't used.
  - **New, data loss:** master adds `ForceLoadedMods` to the zip (`master:src/GUI/ViewModels/MainWindowViewModel.cs:3573`); avalonia doesn't, so overrides not dragged into the order are no longer exported.
- **Evidence:** code as cited.
- **Implication:** the fork's Stage 2 fixes are needed on avalonia too. A `fix/zip-export-corruption` written against master would map almost line for line onto `ModImportService.cs`, and could be offered upstream for both branches.
- **Confidence:** High.

### [High] AVA-13: Archive import fixes I1, keeps I2, I4 and I5, and makes I3 worse
- **Location:** `src/Core/Util/ImportUtils.cs:72-180` (archives), `:183-320` (compressed single files).
- **What:**
  - I1 is fixed: JSON entries are read with `StreamReader.ReadToEnd` (`:131-136`). `SafeDeserialize` still returns null silently (`:167`), so a bad order is dropped with no alert.
  - I2 is still present: a single `success` (`:76`) is set true on the first good copy (`:107`) and never reset, so a failed later pak still goes to `LoadModDataFromPakAsync` (`:115`).
  - I3 is worse: `var buffer = new byte[fileStream.Length]; await fileStream.ReadExactlyAsync(...)` (`:86-87`, again at `:196-197`). The whole archive is now *fully* read into a throwaway array. Above ~2 GB the allocation throws and the whole import fails (caught, alert at `:153-158`); below that it is a 1:1 memory spike.
  - I4 is still present: `_archiveFormats` includes `.7z`/`.rar` (`:64`) and uses random-access `ArchiveFactory.OpenAsyncArchive` (`:90`).
  - I5 is still present: `GetFileName(file.Key)` flattening (`:98-99`).
- **Evidence:** code as cited.
- **Implication:** Stage 3 fixes apply to both branches.
- **Confidence:** High.

### [Medium] AVA-14: Filtering uses the same visibility-stamping design as master, so the Stage 5 cost is likely the same
- **Location:** `src/Core/Util/ModUtils.cs:182-277`; `src/GUI/ModManager/ViewModels/Mods/ModListViewModel.cs:257-263`; `src/GUI/ModManager/Views/Mods/ModListView.axaml.cs:579, 692-697`.
- **What:** each pass writes `IsHidden = false` to every mod, then `true` to every non-match (two notifications per hidden mod). It uses a culture-aware `IndexOf` over `DisplayName` with no precomputed key. Rows stay in the source and are hidden by binding `TreeDataGridRow.IsVisible`. The 500 ms throttle is the same. No fuzzy matching.
- **Evidence:** code as cited.
- **Implication:** the Stage 5 plan (write only on change, precomputed keys, then fuzzy) applies to both branches. Avalonia's per-row hide cost is unmeasured (**inferred** similar in shape).
- **Confidence:** High (design); Low (cost).

### [Medium] AVA-15: Unfinished UI and services visible in code
- **Location:** `ViewModels/ExportOrderToArchiveViewModel.cs:52-57` (no-op `Run`); `ViewModels/Main/MainCommandBarViewModel.cs:534-535` (empty Reload command); `MainWindowViewModel.cs:1398,1809,1812-1813`; `Services/Services/ModUpdaterService.cs:125,206`; `ViewModels/DownloadActivityBarViewModel.cs:78`; `Views/Mods/ModListView.axaml.cs:617-632,671-683`.
- **What:** the TODO list in the Map.
- **Evidence:** code as cited.
- **Implication:** adopting the branch means finishing someone else's half-built features (downloader, update queue, export dialog) before the fork's own work.
- **Confidence:** High.

### [Info] AVA-16: The preview UI stack is fragile
- **Location:** `Directory.Packages.props`; `External/SukiUI`, `External/Avalonia.Labs`, `External/Avalonia.Controls.TreeDataGrid`, `External/Avalonia.MarkdownViewer` (LaughingLeader forks, each re-pinned to Avalonia 12 on 2026-03-03).
- **What:** Avalonia `12.0.0-preview1` combined with packages versioned for Avalonia 11 (`ReactiveUI.Avalonia 11.4.3`, `Xaml.Behaviors.* 11.3.9.5`). Four UI dependencies are personal forks.
- **Evidence:** as cited. Whether Avalonia 12 GA has shipped since was not checked.
- **Implication:** any move to a released Avalonia means re-syncing four forks and re-validating TreeDataGrid behaviour (drag/drop, hierarchy), which is the core of the mod list.
- **Confidence:** High (facts); the size of the risk is inferred.

### [Low] AVA-17: Minor Linux path issues
- **Location:** `Core/Util/ModDataLoader.cs:56` (`PlayerProfiles\\…\\Savegames` regex), `:1276` (`@"Launcher\Settings"`).
- **What:** save-import order names lose the profile prefix, and launcher-preference updates are skipped on Linux.
- **Evidence:** code as cited.
- **Implication:** cosmetic and toolkit-neutral; the same patterns are in master (`master:src/Core/Util/DivinityModDataLoader.cs:39`).
- **Confidence:** High.

### [Info] AVA-18: Startup has no metadata cache
- **Location:** `Core/Util/Pak/DirectoryPakParser.cs:136-160`; `Services/SplatContainerExtensions.cs:21` (FusionCache is used only by `ControlFactoryService`).
- **What:** all paks are parsed concurrently via unbounded `Task.WhenAll` with no on-disk cache. The `^(Mods|Public|Generated)/(.+?)/.+$` per-entry regex remains (`ModDataLoader.cs:44`).
- **Evidence:** code as cited.
- **Implication:** Stage 4's main lever (a path+mtime+size cache) is not there. Its concurrency model differs from master's and is unmeasured.
- **Confidence:** High (code); perf unmeasured.

---

## Comparison with the fork's stages

| Stage | avalonia branch | Verdict | Evidence |
|---|---|---|---|
| **2 – E1** no truncate | `File.OpenWrite` | **Same defect** | `ModImportService.cs:602` |
| **2 – E2** swallowed per-mod errors | Whole export aborts with alert; failed loose-mod packaging and invalid paths are still skipped silently | **Partly solved** | `:623-625, 648, 661-668` |
| **2 – E3** partial zip on cancel | `return false` inside `using` | **Same defect** | `:618` |
| **2 – E4** busy-wait | Async writer | **Solved differently** | `:603` |
| **2 – E5** temp cleanup throws | Non-recursive, outside try, shared `Temp` dir no longer pre-created | **Same, worse** | `:580, 678` |
| **2 – E6** divide by zero | `100d / modPaks.Count` | **Same defect** | `:597` |
| **2 – new** | Container children and force-loaded mods omitted from the zip | **New defect** | `:595`; master `:3573` |
| **3 – I1** short read of order JSON | `StreamReader.ReadToEnd` (silent `SafeDeserialize` remains) | **Solved** (deserialize failure still silent) | `ImportUtils.cs:131-136, 167` |
| **3 – I2** `success` leaks across entries | Unchanged | **Same defect** | `:76, 107, 115` |
| **3 – I3** throwaway full read | Now `ReadExactlyAsync` into `byte[Length]` | **Same, worse** (fails above ~2 GB) | `:86-87, 196-197` |
| **3 – I4** 7z/rar random access | `ArchiveFactory.OpenAsyncArchive` | **Same defect** | `:64, 90` |
| **3 – I5** same-name collision | `GetFileName` flatten | **Same defect** | `:98-99` |
| **4** startup | Concurrent load, no cache | **Not solved**; different model, unmeasured | `DirectoryPakParser.cs:136-160` |
| **5** filter / fuzzy | Same stamping design, same throttle, no fuzzy | **Same defect** | `ModUtils.cs:182-277`; `ModListView.axaml.cs:579, 692-697` |
| **6.1** render | TreeDataGrid; `PrepareRow` rebinds per realized row and loops over container children; WPF `CachedTextBlock` work doesn't transfer | **Different toolkit; not comparable without measurement** | `ModListView.axaml.cs:577-614` |
| **6.2** overrides in order | The force-loaded exclusion is gone: `CanAddToLoadOrder = modType != "Adventure" && !isHidden` (oddly coupled to the filter's `IsHidden`); overrides get an "Override" label and colour | **Solved differently**, but archive export lost them (above) | `ModData.cs:373-376, 484`; `ModEntry.cs:292-306` |
| **6.3** categories | Nested `ModContainer` tree (hierarchical source, collapsible, per-container colours and icons), flattened for `modsettings.lsx` | **Solved differently** (tree, not the plan's flat dividers); export-to-zip bug shows the hierarchy's cost | `ModContainer.cs`; `ModOrderContainer.cs`; `ModDataLoader.cs:1373-1386` |
| **8** multi-order export | Not present; the export dialog VM is a stub | **Not solved** | `ExportOrderToArchiveViewModel.cs:52-57` |

Useful for the fork regardless of toolkit: the containers' order model (`IModOrderEntry` with a `Type` discriminator plus `ForEachNested`) is a worked example of a format change. Its export bug is exactly the risk R7 names ("the game or older builds must never see dividers"). The plan's flat-divider design avoids it by construction.

---

## Linux options and recommendation

| Option | What it takes | Effort | Risk | What breaks or stays broken |
|---|---|---|---|---|
| **(a) Keep WPF, run under Wine/Proton** | Owner-side: install .NET 8 Desktop Runtime and the VC++ 2015-22 runtime (for C++/CLI `LSLibNative`/ijwhost) into a prefix, ideally the BG3 prefix `compatdata/1086940`, then run `BG3ModManager.exe` there. Fork-side: document it; maybe fix `DivinityApp.GetAppDirectory()`'s dead Proton statement (CLAUDE.md §6). | **S** (days, mostly testing) | **Medium** (all **inferred**, unverified here) | WPF under Wine rendering, DPI and perf (AdonisUI, the virtualized list, `CachedTextBlock` reflection should be fine on real .NET). Steam/GOG registry detection fails in the BG3 prefix, so paths are set by hand (`Z:\home\…\steamapps\common\Baldurs Gate 3\Data`). `%LOCALAPPDATA%` resolves correctly *inside the BG3 prefix*, wrongly in any other. Screen readers: no. Script Extender Toolbox (Windows DLL) likely works. File watching on `Z:` is unreliable. Two Wine prefixes mean two copies of settings. |
| **(b) Make master's Core cross-platform + a CLI** | 1) Replace the 3 `Native.LZ4FrameCompressor` calls in `External/lslib/LSLib` (`LS/Compression.cs:98,200`, `LS/PackageReader.cs:213`) with K4os, as LSLibNet did, and drop `LSLibNative.vcxproj` from `LSLib.csproj:44` and `master:src/Core/DivinityModManagerCore.csproj:20`. Or swap in LSLibNet, which needs net10 or multi-targeting plus a `LSLib.LS.Pak` namespace change. 2) Retarget Core `net8.0-windows`+`UseWPF` → `net8.0`, moving the 11 files' WPF types out (`Visibility` in `DivinityModData`/`DivinityBaseModData`, `Key`/`ModifierKeys` in `Hotkey`/`KeyExtensions`/`MultiReactiveCommandHotkey`, `PropertyConverters`, `DivinityGlobalCommands`, `ISelectable`, `DivinityModUpdateData`, `DivinityModManagerSettings`, `BaseHistoryViewModel`). 3) Guard Registry, Junction and RecycleBin, and port avalonia's Proton/XDG discovery. 4) A small `feat/cli` (`export-to-game --profile --order`) over `BuildOutputList` + `GenerateModSettingsFile`. 5) Rename `Directory.build.props` → `Directory.Build.props`. 6) Replace master lslib's GPLex/GPPG `.exe` pre-build step (Windows .NET Framework tools; per `docs/review/areas/AUDIT-CLAUDE.md` row 2.1g its path is the lower-case `..\external\gppg\binaries\`, which won't resolve on a case-sensitive filesystem) with YaccLexTools, as LSLibNet does, or with committed generated parsers. This also fixes CLAUDE.md §2.2's "first build always fails". | **M** for 1+5+6 (upstreamable `chore/*`); **M–L** for 2–4 | **Medium**: R1 (managed LZ4 must decode identically; snapshot-diff the 1,121-mod list), and the `Visibility` removal collides with Stage 5's visibility-stamping design | Nothing for Windows users. Step 1 alone removes the C++/CLI toolchain requirement on Windows too (CLAUDE.md §2: "`dotnet build` cannot build `.vcxproj`"), which makes CI (§8.4) far easier and lets Core tests run on Linux. |
| **(c) Adopt or finish upstream avalonia** | Rebase all fork work onto a 944-commit, preview-stack rewrite; fix AVA-06/07/08/12/13; finish the downloader, export dialog and update queue; then redo Stages 4–8 there. Fody pin, house rules and the WPF list work (6.1) don't apply. | **XL** | **High**: preview Avalonia with 4 forked UI deps, no tests, no working release path, an upstream that has been silent for 8 months | Every completed WPF measurement and fix (6.1 `CachedTextBlock`, the 0.2 build fixes) is thrown away; the Stage 0 baseline doesn't transfer. |
| **(d) Port the fork's WPF UI to Avalonia later** | Needs (b) first. Then a new Avalonia head over master's Core, borrowing upstream pieces as *reference*: LSLibNet, `RegistryService`/`LinuxRegistryHelper`, `ProcessHelper` xdg-open, SE Linux detection, TreeDataGrid usage. | **L–XL** | **Medium–High**: Stage 7 (UI refresh) would be thrown away (the plan already says so); the list perf would need re-measuring | Defers Linux UI until it is actually a driver. |

### Recommended order (fits "upstreamable fixes and perf first, Windows now")

1. **Now, unchanged:** Stages 1–6 on WPF master as planned. Two cheap additions:
   - Write the Stage 2/3 fixes (E1, E3, E5, E6, I2, I3, I5, plus a loud deserialize failure) so they map onto avalonia's `ModImportService.cs:573-690` and `ImportUtils.cs:72-320`. The same defects exist there (AVA-12, AVA-13), so a parallel upstream PR costs little if upstream ever resumes.
   - Keep new logic toolkit-neutral and in Core: export/import result objects, filter match keys, the metadata cache, the divider model (and its export flattening, avoiding AVA-12's bug). Add **no new `System.Windows` types to Core**.
2. **Soon, upstreamable `chore/*`:** rename the lower-case `Directory.build.props` (AVA-03). Replace `LSLibNative`'s LZ4 with managed K4os, and the GPLex/GPPG `.exe` step with YaccLexTools, in the lslib submodule. That is a separate PR to LaughingLeader/lslib, with an R1-style snapshot diff. This pays off on Windows immediately (plain `dotnet build`, simpler CI) and removes the real Linux blocker.
3. **Interim Linux story, owner-verified:** option (a). One test on a Linux box or the Deck tells you whether WPF-under-Proton is good enough. If it is, Linux stays "open" at near-zero cost.
4. **After Stage 5 lands:** option (b) steps 2–4 (Core to plain `net8.0`, path discovery ported from avalonia's `RegistryService.cs:55-170` with the SD-card/Flatpak fix, a `feat/cli` for export-to-game). That gives Linux and Deck users the one operation that matters without any UI.
5. **Only if Linux becomes a driver:** option (d). **Only if upstream resumes avalonia** (watch `git ls-remote … refs/heads/avalonia`): reconsider (c).

---

## Commands to verify on Linux

For the owner or a later session **with permission to build** (this session did not run any of these). Run from a fresh directory.

```bash
# 0. Source + toolchain (needs .NET 10 SDK for avalonia; .NET 8 for master)
git clone --recurse-submodules -b avalonia https://github.com/LaughingLeader/BG3ModManager.git bg3mm-ava
cd bg3mm-ava
dotnet --list-sdks
dotnet --list-runtimes
ls src/Directory.build.props src/Directory.Build.props   # AVA-03: confirm only the lower-case one exists

# 1. Restore, then inspect how YaccLexTools runs gplex/gppg (AVA-04)
dotnet restore BG3ModManager.sln
find ~/.nuget/packages -ipath '*yacclextools*' -name '*.targets' -exec grep -nH -E 'Exec|Command|\.exe|dotnet' {} +

# 2. Solution build, Debug, no RID (avoids NETSDK1134 / NETSDK1141)
dotnet build BG3ModManager.sln -c Debug -v:m 2>&1 | tee build.log
grep -nE 'error|NETSDK|MSB3073|gplex|gppg|CS8632' build.log | head -50
grep -c 'CS8632' build.log                                    # AVA-03 symptom count

# 3. The two SDK traps (expected to fail; record the error codes)
dotnet build BG3ModManager.sln -c Debug -r linux-x64 2>&1 | grep -E 'NETSDK1134' || true
dotnet build src/GUI/Desktop-Avalonia/Desktop-Avalonia.csproj -c Debug -r linux-x64 2>&1 | grep -E 'NETSDK1141|error' | head || true

# 4. Tests (expect 0/5 to pass without NEXUSMODS_API_KEY; AVA-07)
dotnet test src/Tests/Tests.csproj -c Debug --logger "console;verbosity=detailed" 2>&1 | tee test.log
grep -nE 'Passed|Failed|Failed to create pak|NEXUSMODS_API_KEY' test.log | head

# 5. Run the app (use the apphost, not `dotnet …dll`, to dodge AVA-11)
APPDIR=$(dirname "$(find src/GUI/Desktop-Avalonia/bin -name BG3ModManager.dll | head -1)")
ls "$APPDIR"/runtimes/linux-x64/native/ 2>/dev/null           # Skia/HarfBuzz natives present?
"$APPDIR"/BG3ModManager                                       # or: dotnet "$APPDIR"/BG3ModManager.dll
ls "$APPDIR"/_Logs 2>/dev/null

# 6. Data discovery check (AVA-09): where are the Proton profiles really?
ls "$HOME/.local/share/Steam/steamapps/compatdata/1086940/pfx/drive_c/users/steamuser/AppData/Local/Larian Studios/Baldur's Gate 3/PlayerProfiles"
grep -E '"path"' "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"
ls -d /run/media/*/steamapps/compatdata/1086940 2>/dev/null   # SD-card install on Deck
ls -d "$HOME/.var/app/com.valvesoftware.Steam" 2>/dev/null     # Flatpak Steam

# 7. Master on Linux (expected to fail; confirms why option (b) step 1 matters)
git clone --recurse-submodules https://github.com/SpasticPengu/BG3ModManager.git bg3mm-master
cd bg3mm-master && dotnet build BG3ModManager.sln -c Debug 2>&1 | grep -E 'NETSDK1100|vcxproj|MSB4278|MSB4019|error' | head

# 8. Option (a) feasibility, owner-only (installs software into the BG3 Proton prefix)
#    protontricks 1086940 dotnetdesktop8 vcrun2022
#    protontricks-launch --appid 1086940 /path/to/BG3ModManager.exe
```

After step 2, check AVA-08 by hand: delete a throwaway mod through the UI on Linux and confirm the file is still on disk and a "Error deleting file" line appears in `_Logs`.
