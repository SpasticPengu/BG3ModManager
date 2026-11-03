# BLD: build system, dependencies, runtime support, CI, repo hygiene

Reviewer: BLD agent, 2026-11-03. Read-only review of `/home/user/BG3ModManager` at `master` = `fc35358`, submodules `External/lslib` @ `2b88c0f` and `External/CrossSpeak` @ `58b7178`, plus `origin/fix/publish-prebuild-quoting`, `origin/perf/list-render` and the `upstream/avalonia` worktree. Nothing was built or run. Web facts were checked against Microsoft Learn, the GitHub runner-images readme, NuGet nuspecs and OSV. Anything I could not confirm is marked **(inferred)**.

## Map

| Project | TFM | Kind | Built by sln in | Notes |
|---|---|---|---|---|
| `src/GUI/GUI.csproj` | `net8.0-windows10.0.22621.0` | WPF WinExe, x64 | all configs | `OutputPath=$(SolutionDir)\bin\$(Configuration)\` (`:25`); Publish targets `:81-109` |
| `src/Core/DivinityModManagerCore.csproj` | `net8.0-windows`, `UseWPF` | library, x64 | all | refs LSLibNative.vcxproj, LSLib, CrossSpeak (`:20-22`) |
| `src/Toolbox/Toolbox.csproj` | `net8.0-windows` | console, x64 | all except Release\|x64 (`sln:104`) | only dependency is PowerArgs 4.0.3 |
| `External/lslib/LSLib/LSLib.csproj` | `net8.0` | library | all | GPLex/GPPG `Exec`s (`:59-76`), refs LSLibNative |
| `External/lslib/LSLibNative/LSLibNative.vcxproj` | `net8.0`, `CLRSupport=NetCore`, v143 | C++/CLI DLL | all (Publish → Release\|x64) | **Debug uses debug CRT** (`:35`) |
| `External/lslib/LSLibStats/LSLibStats.csproj` | `net8.0` | library | **all, but nothing references it** | 10 more GPLex/GPPG outputs |
| `External/CrossSpeak/CrossSpeak/CrossSpeak.csproj` | `netstandard2.0` | library (LGPL-2.1) | all (non-Debug → Release) | `GeneratePackageOnBuild=True` + Debug-only nuspec |

Release path: `Publish|x64` → GUI `PreBuildEvent` (`:82`) → build → `CrossSpeakLibraryFiles` + `SetupLibFolder` (`:85-102`) → `RunBuildRelease` → `python BuildRelease.py <ver>` → `BG3ModManager_v<ver>.zip` + `BG3ModManager_Latest.zip`.

Git facts: 210 tracked text files are LF in the index, 12 CRLF, 11 mixed (`git ls-files --eol`). `.gitattributes` holds only the 21 `merge=ours` lines. No `.github/workflows`, no `global.json`, no `Directory.Packages.props`. `upstream/avalonia` already targets `net10.0`, uses central package management, ReactiveUI 23.1.1 + ReactiveUI.SourceGenerators 2.6.1, and replaces lslib with **LSLibNet** (managed, no C++/CLI).

### CLAUDE.md sections 2 and 8.4 checked against the files

All six measured traps match the files. Minor deltas only:

- CrossSpeak pack path: confirmed, `CrossSpeak.nuspec:31-32`; the sln maps non-Debug configs to `Release|Any CPU` (`sln:169-184`).
- LSLib generated-parser glob: confirmed, `LSLib.csproj:70-76`. The GPLex/GPPG paths sit at `:62-63`, not `:67-68` as `implementation-plan` §0.1 says. **CLAUDE.md misses that `LSLibStats.csproj` has the same trap ×10** (BLD-05).
- Toolbox missing from `Release|x64`: confirmed, `sln:104` has `ActiveCfg` and no `Build.0`.
- Publish unoptimised: confirmed. `GUI.csproj:38-41` sets `Optimize` only for Debug.
- `DebugType=none`: confirmed, Core `:12`, Toolbox `:13`, LSLib `:15`, LSLibNative `:24`.
- `SolutionDir` dependence: confirmed. GUI also uses `$(SolutionDir)\src\Core\...` with a doubled backslash (`:44`). The Toolbox `OutputPath` has no doubled backslash (`Toolbox.csproj:5`).
- Section 8.4: no workflows exist; confirmed. The 21 `merge=ours` lines: confirmed.
- Section 5 "CRLF": true **only for a Windows working tree with `core.autocrlf=true`**. The repository stores LF (BLD-07).

## Findings

Severity counts: **High 2 · Medium 6 · Low 8 · Info 4** (20 findings).

### [High] BLD-01: The whole solution reaches end of support in 7 days (.NET 8 EOL, 2026-11-10)

- **Location:** `src/GUI/GUI.csproj:5`, `src/Core/DivinityModManagerCore.csproj:3`, `src/Toolbox/Toolbox.csproj:4`, `External/lslib/LSLib/LSLib.csproj:3`, `External/lslib/LSLibNative/LSLibNative.vcxproj:26`, `External/lslib/LSLibStats/LSLibStats.csproj:3`; `README.md:11`.
- **What:** every runtime-bearing project is `net8.0*` and framework-dependent (no `SelfContained`/`RollForward` anywhere in `src/**/*.csproj`). After 2026-11-10 Microsoft ships no more security fixes for .NET 8 (or .NET 9). Microsoft's replacement is .NET 10 LTS, supported to November 2028 (Microsoft devblog; endoflife sites give 2028-11-14). The README pins users to a specific old patch: `runtime-desktop-8.0.15-windows-x64-installer` (April 2025).
- **Failure scenario:** a published fork build keeps running, but on an unpatched runtime. New users following README step 2 install 8.0.15, which is already missing about 18 months of patches. Once the fork moves to net10, any user who has only the .NET 8 Desktop Runtime and accepts the AutoUpdater prompt (`MainWindowViewModel.cs:4912`) is left with an app that will not start until they install .NET 10. The host shows its "install .NET" dialog.
- **Evidence:** the TFMs above. [Microsoft: .NET 8 and 9 end of support on Nov 10 2026](https://www.elevenforum.com/t/net-8-and-net-9-will-reach-end-of-support-on-november-10-2026.47839/); [InfoWorld](https://www.infoworld.com/article/4191704/end-of-support-looms-for-net-8-and-net-9.html).
- **Fix:** follow the migration plan below. The short-term README fix is an evergreen link: `https://dotnet.microsoft.com/download/dotnet/10.0` after the move, `/8.0` until then.
- **Confidence:** High for the dates and TFMs. The user-facing update failure mode is inferred from standard host behaviour.

### [High] BLD-02: The owner's toolchain cannot officially target net10, and the C++/CLI project raises the bar further

- **Location:** owner machine (CLAUDE.md §2.1: VS Build Tools 2022 17.14.41, MSVC v143, SDK 8.0.425 + 10.0.400); `LSLibNative.vcxproj:36,42,48` (`PlatformToolset v143`).
- **What:** Microsoft's SDK/VS table says targeting `net10.0` "is officially supported in Visual Studio 18.0+ only". With SDK 10.0.1xx, VS 17.14 can target at most `net9.0`, and newer targets produce a build warning. **SDK 10.0.200 and later, including the owner's 10.0.400, list 18.0 as their minimum Visual Studio version**, and an SDK "won't load in a version of Visual Studio that's older than that minimum version". The C++/CLI side follows the same coupling: .NET 8 C++/CLI needed MSVC 14.38 / VS 17.8 (dotnet/runtime#94723), so net10 C++/CLI almost certainly needs the VS 2026 v145 toolset, MSVC 14.50 **(inferred: no Microsoft doc found that states it)**.
- **Failure scenario:** the owner changes TFMs to `net10.0-windows` and builds with the 17.14 MSBuild. The result is either NETSDK1233/NETSDK1209 warnings or errors, or an SDK-resolution error because 10.0.400 refuses to load. If it builds at all, the toolchain is unsupported. LSLibNative retargeted to net10 under v143 is unlikely to build at all.
- **Evidence:** [learn.microsoft.com: .NET SDK, MSBuild, and Visual Studio versioning](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs) (table rows 10.0.100 → min VS 17.14, max TFM in 17.14 = net9.0; 10.0.200–10.0.400 → min VS 18.0); [MSVC v145 / VS 2026 18.0](https://learn.microsoft.com/en-us/cpp/overview/what-s-new-for-msvc).
- **Fix:** install **Visual Studio Build Tools 2026 (18.x)** side by side with 2022. Select the components "Desktop development with C++", "C++/CLI support for v145 build tools", the .NET SDK component (`Microsoft.NetCore.Component.SDK`) and the Windows 11 SDK. Also add "MSVC v143 build tools" plus its C++/CLI component if v143 should stay buildable. Then build with the 18.x `msbuild.exe` and `-nodeReuse:false`. Keep 2022 for baseline A/B runs.
- **Confidence:** High for the C# rules (Microsoft table). Medium for the C++/CLI toolset requirement (inferred).

### [Medium] BLD-03: No `global.json`, so the SDK is chosen by whatever happens to be installed

- **Location:** repo root (absent). Owner: SDK 8.0.425 and 10.0.400. `windows-latest` runner (VS 18.11) has SDKs 8.0.133/206/319/427, 9.0.x and 10.0.114/204/303/403.
- **What:** `msbuild` resolves the highest installed SDK unless `global.json` pins one. The owner's builds, CI and VS can therefore use different SDK majors. With SDK 10, the .NET 10 behaviour changes (NuGetAudit `all`, pruning, NU1510) appear only once TFMs move. SDK-level changes such as the terminal logger and new analyzers appear immediately.
- **Failure scenario:** CI compiles with 10.0.403 and the owner with 8.0.425 (or 10.0.400; see "Needs a Windows run"). An analyzer or SDK behaviour difference then shows up only on one side, and builds stop being reproducible.
- **Evidence:** no `global.json` (`find` over the repo); [runner image readme](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-VS2026-Readme.md) line 446.
- **Fix:** add `global.json` now with `{"sdk":{"version":"8.0.425","rollForward":"latestPatch","allowPrerelease":false}}`. The runner's 8.0.427 satisfies it. Bump to `10.0.100`/`latestFeature` in the net10 branch. This is a `chore/` change and upstreamable.
- **Confidence:** High.

### [Medium] BLD-04: `Publish|Any CPU` / `PublishTest|Any CPU` builds LSLib, LSLibNative and LSLibStats in **Debug**, so a release can ship a debug-CRT native DLL

- **Location:** `BG3ModManager.sln:113-114, 117-118` (LSLib → `Debug|Any CPU`), `:133-134, 137-138` (LSLibNative → `Debug|x64`), `:153-154, 157-158` (LSLibStats → Debug); `LSLibNative.vcxproj:35` (`UseDebugLibraries=true` for Debug).
- **What:** the solution offers "Any CPU" platforms. For Publish and PublishTest they map GUI, Core and Toolbox to the x64 Publish configs, but the lslib projects to **Debug**. MSBuild and VS fall back to "Any CPU" when no platform is given **(inferred from MSBuild's default solution-platform rule)**.
- **Failure scenario:** someone runs `msbuild BG3ModManager.sln -p:Configuration=Publish`, forgetting `-p:Platform=x64`, or picks Publish in VS with "Any CPU" showing. `RunBuildRelease` still produces `BG3ModManager_Latest.zip`, but `_Lib\LSLibNative.dll` is a `/MDd` build that imports `VCRUNTIME140D.dll`/`ucrtbased.dll`, which end users do not have. The DLL fails to load the first time a chunked-LZ4 path runs: `LSFReader` (`LSLib/LS/Resources/LSF/LSFReader.cs:312`) or `PackageReader.cs:213`, as in save-game order import. The rest of the app keeps working, so the zip passes a casual test. LSLib also ships unoptimised with `DEBUG` defined.
- **Evidence:** sln lines above. Debug-CRT imports are standard for `/MDd` **(inferred, check with `dumpbin /dependents`)**.
- **Fix:** delete the four `Any CPU` solution platforms, or map every `*|Any CPU` row to the same project configs as `*|x64`. A one-file `chore/` change in this repo, upstreamable. CI must always pass `-p:Platform=x64`.
- **Confidence:** High for the mapping, Medium for the runtime consequence.

### [Medium] BLD-05: LSLibStats is built in every configuration, referenced by nothing, and carries a second copy of the first-build parser trap

- **Location:** `BG3ModManager.sln:24, 145-164`; `External/lslib/LSLibStats/LSLibStats.csproj:8-31`.
- **What:** LSLibStats is never referenced by GUI, Core or LSLib (`grep -rn LSLibStats src` finds nothing). It still has `Build.0` in all ten configurations, so every build runs 10 more GPLex/GPPG invocations (Stat, Functor, Lua, Requirement, RollConditions). Its hand-written parsers (`Stats/File/StatParser.cs`, `Stats/Functor/*Parser.cs`) depend on the generated scanners, so it fails the same "glob expanded before generation" way on a clean clone. CLAUDE.md §2.2 and plan §0.2a describe the trap for LSLib only, so a fix limited to `LSLib.csproj` will still leave the first build failing.
- **Failure scenario:** the owner lands `fix/lslib-generated-parser-glob` in the lslib submodule for the four LSLib files. A fresh clone still fails its first build, now in LSLibStats, and the fix looks broken.
- **Evidence:** sln lines; `LSLibStats.csproj` `PreBuildStatsGppgFiles`; `grep` for usages.
- **Fix:** remove the LSLibStats `Build.0` lines, or the whole project, from `BG3ModManager.sln` (superproject, `chore/`, upstreamable). This also shortens every build. If it must stay, include its 10 outputs in the glob fix.
- **Confidence:** High that it is unused and built. Medium that its first build fails (the same mechanism, not observed).

### [Medium] BLD-06: LSLibNative (C++/CLI) is the one hard blocker for `dotnet build`, a cheap CI, Linux, and dropping the VC++ redistributable, and upstream already replaced it

- **Location:** `src/Core/DivinityModManagerCore.csproj:20`; `LSLib.csproj:44`; native call sites `LSLib/LS/Compression.cs:98,200`, `LS/PackageReader.cs:213` (LZ4 frame), `VirtualTextures/Compression.cs:54,146`, `VirtualTextures/VirtualTexture.cs:354` (FastLZ), `Granny/GR2/Reader.cs:256-409` (Granny); `README.md:11` (C++ redist prerequisite).
- **What:** the only things the mod manager needs from LSLibNative are LZ4 frame compression and decompression on the LS paths. Non-chunked LZ4 already goes through managed `lz4net`. C++/CLI cannot be built by `dotnet build`, cannot exist on Linux, ties the build to a specific VS/MSVC version (BLD-02), and forces users to install the VC++ redist. `upstream/avalonia` uses `External/LSLibNet` (LaughingLeader): `net10.0`, `K4os.Compression.LZ4(.Streams) 1.3.8`, `DotFastLZ.Compression`, and `YaccLexTools 1.2.3` for parser generation with correct `Compile` items. That also fixes the GPLex/GPPG download and the glob trap.
- **Failure scenario:** every future platform step pays this cost. Today it is the net10 toolset jump; next it is CI (C++ workload and C++/CLI component on the runner); later any Linux/Deck work, where no UI toolkit change helps while Core references a `.vcxproj`.
- **Evidence:** files above; `wt-avalonia/External/LSLibNet/src/LSLib/LSLib.csproj:26-34`, `Directory.Packages.props`.
- **Fix:** do not swap in LSLibNet wholesale. Its layout differs (no `LS/PackageReader.cs`; `LS/Pak/` instead), so it is not a drop-in replacement. Instead, in the lslib submodule fork, replace `Native.LZ4FrameCompressor` with K4os LZ4 frame streams. Exclude `Granny/` and `VirtualTextures/` from the build, or use DotFastLZ for them, and drop the `LSLibNative` ProjectReference. Measure first: the 9.27 s mod-load baseline and a byte-for-byte mod-list diff (R1). This is a separate repo PR, so it is optional for the net10 deadline (see plan, option B).
- **Confidence:** High for the facts. Medium for the effort and performance neutrality (inferred).

### [Medium] BLD-07: Line endings: the repo stores LF, CLAUDE.md says "CRLF", and there are no text/eol rules

- **Location:** `.gitattributes` (21 `merge=ours` lines only); `.editorconfig:15,251` (`end_of_line = crlf`); CLAUDE.md §5.
- **What:** in `src`, 188 of 208 `.cs`/`.xaml` blobs are LF, 9 are CRLF and 11 are mixed: `DivinityModDataLoader.cs` (520 CRLF / 1306 LF), `DivinityBaseModData.cs` 29/150, `DivinityProfileActiveModData.cs` 11/50, `Program.cs` 18/36, `DivinitySaveTools.cs` 110/26, `ModExistsConverter.cs`, `ModIsActiveConverter.cs`, `DivinityProfileData.cs`, `PropertyConverters.cs`, `ModSettingsParseResults.cs`, `ModuleShortDesc.cs`. CRLF-in-index files: `BG3ModManager.sln`, both main csprojs, `ModRegistryService.cs`, `ReactiveExtensions.cs`, `DivinityFileUtils.cs`, `TempFile.cs`, `AppCommands.cs`, `ModToDisplayNameConverter.cs`, `MainResourceDictionary.xaml`, `MainViewControl.xaml`, `MainWindow.xaml`. The owner sees CRLF because Git for Windows defaults to `core.autocrlf=true`. His perf-branch commits are LF in the index (verified on all 17 changed files of `origin/perf/list-render`), so his setup is fine. The trap is for anyone without autocrlf: Linux agents, cloud sessions, CI scripts.
- **Failure scenario:** an agent on Linux follows CLAUDE.md "CRLF" literally and rewrites `MainWindowViewModel.cs` with CRLF. The commit converts all 5,448 lines, so the diff against upstream is the whole file, and the "minimal upstreamable PR" rule fails.
- **Evidence:** `git ls-files --eol`. A scratch-repo test showed that adding `* text=auto` leaves existing CRLF/mixed blobs untouched (`git status` clean), normalises new files to LF, and that only `git add --renormalize` would rewrite the 23 files.
- **Fix:** add `* text=auto` at the top of `.gitattributes` and keep the `merge=ours` lines. Add binary markers (`*.png *.ico *.dll *.zip binary`). Do **not** renormalise upstream-shared files on the fork: every later upstream edit to those 23 files would conflict. Offer renormalisation upstream as a `chore/` if at all. Update CLAUDE.md §5 to "LF in the repository, CRLF in a Windows working tree; never commit CRLF; leave mixed files' existing lines alone".
- **Confidence:** High.

### [Medium] BLD-08: The release zip is built from an uncleaned `bin\Publish` with error-swallowing deletes and no check of its contents

- **Location:** `BuildRelease.py:33-41, 55-60`; `GUI.csproj:92-102` (only `_Lib` is cleaned, by the pre-build); `GUI.csproj:71-76`.
- **What:** `zipdir("bin/Publish")` zips everything present. Only `Data`, `Orders` and `_Logs` are removed, by `SilentRemove`, which catches every `Exception`, and `shutil.rmtree(ignore_errors=True)` hides partial failures. Nothing removes stale root-level files from earlier builds, an extension-less `debug` marker file (which enables logging for every user; CLAUDE.md §5), or other test markers. The `perf/list-render` branch already uses a `perfscroll` marker next to the exe. Nothing checks that `Tools\Toolbox.exe` is present.
- **Failure scenario:** (a) The owner drops `debug` into `bin\Publish` to diagnose a Publish-only failure (R5 says to test every stage in Publish). The next release zip turns logging on for all users. (b) Stages 4–6 drop a dependency or a resources file. The old copy outside `_Lib` stays in `bin\Publish` and ships forever. (c) An antivirus lock makes `rmtree` fail silently, and `Data\settings.json` ships with the plaintext Nexus API key (CLAUDE.md §7). This is unlikely because `SetupLibFolder` also deletes `Data`, but nothing would report it.
- **Evidence:** script lines above. 44 icon PNGs are both `Resource` and `CopyToOutputDirectory` (`GUI.csproj:74-76`) yet are loaded through `pack://` URIs (`MainResourceDictionary.xaml:45`), so the loose copies in the zip are dead weight.
- **Fix:** build releases only from a clean checkout (the CI job below). In `BuildRelease.py`, use an allowlist or denylist plus a post-zip assertion (no `Data/`, `_Logs/`, `Orders/`, `*.pdb`, `debug`, `perfscroll`; must contain `BG3ModManager.exe`, `_Lib/`, `Tools/Toolbox.exe`), and fail loudly. Drop `CopyToOutputDirectory` from the icon `Resource` items after checking nothing reads them from disk.
- **Confidence:** High for the mechanism. The scenarios are plausible, not observed.

### [Low] BLD-09: Review of `origin/fix/publish-prebuild-quoting`: the one-liner is right for every solution build that exists today, but fragile, and it should be squashed before a PR

- **Location:** `f6633a8`, then `5f8a80a` → `GUI.csproj:82`: `<PreBuildEvent>if exist "$(OutputPath)_Lib" rd /s /q "$(OutputPath)_Lib"</PreBuildEvent>`.
- **What:**
  - **Correct cases:**
    - `Publish` and `PublishTest` built from the sln. `OutputPath` is defined at `:25`, above `:81-83`, so it expands at evaluation to `C:\…\BG3ModManager\\bin\Publish\`.
    - The doubled backslash. Win32 path canonicalisation (used by `if exist` and `rd`) collapses repeated separators that are not at the start, so it is harmless. It even rescues `-p:SolutionDir=C:\repo` passed without the trailing slash.
    - `SolutionDir` empty (a csproj build). The path becomes `\bin\Publish\_Lib`, the same drive-root folder the build itself writes to, so it stays consistent.
    - Spaces in the path. They are quoted.
    - The guard. It silences the first-run stderr.
  - **Diverging cases:**
    1. **A RuntimeIdentifier.** `Microsoft.NET.RuntimeIdentifierInference.targets` appends `win-x64\` to `OutputPath` *after* the project body was evaluated. The pre-build then cleans `bin\Publish\_Lib` while `SetupLibFolder` (execution-time `$(OutputPath)`) fills `bin\Publish\win-x64\_Lib`. This is plausible in Stage 4 if ReadyToRun is tried, since R2R needs a RID **(inferred)**.
    2. **An `-p:OutDir=` override**, common in CI. Output goes to OutDir, the cleanup goes to `OutputPath`. All the custom targets share this bug.
    3. **A relative `OutputPath`.** It resolves against `Exec WorkingDirectory=$(OutDir)`, which is wrong.
    4. **`cmd`-only syntax.** On a non-Windows shell `if exist` is a syntax error, against the "toolkit-neutral where free" rule.
    5. **`rd`'s exit code.** `rd` does not reliably set ERRORLEVEL when files inside are locked, so a locked `_Lib` is silently half-deleted.
  - **History:** commit `f6633a8`'s message documents the refuted diagnosis ("exit 2 / exit 3", "first Publish build always failed").
- **Failure scenario:** case 1 or 2 above leads to stale `_Lib` contents in the release zip. An upstream reviewer reads `f6633a8` and gets a wrong bug report.
- **Evidence:** `git show f6633a8 5f8a80a`; `GUI.csproj:25, 81-109`.
- **Fix:** squash into a single commit whose message is `5f8a80a`'s. Better still, use the cross-platform MSBuild form with no shell or quoting, resolved at execution time:

  ```xml
  <Target Name="CleanLibFolder" BeforeTargets="PreBuildEvent" Condition="'$(Configuration)' == 'Publish' or '$(Configuration)' == 'PublishTest'">
  	<RemoveDir Directories="$(OutDir)_Lib" />
  </Target>
  ```

  `RemoveDir` succeeds silently when the folder is absent and *fails the build* when it cannot delete, which is the right behaviour for a release. A follow-up `chore/` could switch `CrossSpeakLibraryFiles` and `SetupLibFolder` from `$(OutputPath)` to `$(OutDir)` too. Either version is a tiny upstream PR. The `RemoveDir` one is 4 lines.
- **Confidence:** High.

### [Low] BLD-10: Unused or redundant package references

- **Location:** `DivinityModManagerCore.csproj:26, 28-30, 34-35`; `GUI.csproj:56, 64`; `LSLib.csproj:54-55`.
- **What:**

  | Package | Where | Usage found | Verdict |
  |---|---|---|---|
  | `Microsoft.CSharp 4.7.0` | Core | 1 `dynamic` (`DivinityJsonUtils.cs:23`); the binder is in the net8 shared framework | redundant; NU1510 on net10 **(inferred: it is in the prune list)** |
  | `System.Net.Http 4.3.4`, `System.Text.RegularExpressions 4.3.1` | Core and LSLib | `HttpClient`/`Regex` come from the framework | not used at runtime. Probably here to lift the vulnerable 4.3.0 copies pulled in by `Gameloop.Vdf 0.6.2` and `lz4net` (both `netstandard1.0` → `NETStandard.Library 1.6.1`) **(inferred)**. Keep on net8; delete on net10, where pruning removes the whole tree and NU1510 flags them |
  | `ReactiveProperty 9.7.0` | Core | exactly one `.AddTo(Disposables)` (`BaseHistoryViewModel.cs:3,89`) | replace with `.DisposeWith(...)` and drop it (also drops `ReactiveProperty.Core` and `System.ComponentModel.Annotations`) |
  | `ReactiveHistory 0.10.7` | Core | `BaseHistoryViewModel` is used by `MainWindowViewModel`, but the only snapshot is `MainWindowViewModel.cs:2067`, and `UndoCommand`/`RedoCommand` are bound nowhere | dead feature. Pulls the legacy `System.Reactive.Core/Linq/Interfaces 5.0.0` facades. Removal touches a base class, so it is a fork-side cleanup |
  | `Splat.Drawing 15.3.1` | GUI | 0 uses of `IBitmap`/`BitmapLoader` | remove |
  | `NexusModsNET 1.0.5` | GUI | 0 uses in GUI (4 in Core) | redundant: it flows transitively from Core |
  | `DotNetProjects.Extended.Wpf.Toolkit` | GUI | about 20 `Xceed.Wpf.Toolkit.MessageBox.Show`, `IntegerUpDown` (`SettingsWindow.xaml.cs:247`), theme keys | used; keep |
  | `WpfScreenHelper 2.1.1` | GUI | `MainWindow.xaml.cs:22,187,213-217` (window placement) | used; keep |
  | `Gameloop.Vdf 0.6.2` | Core | used; `netstandard1.0`, last release 2019 | keep. It is the source of the legacy `NETStandard.Library 1.6.1` tree |

  Version mismatches between projects (all resolve upward at runtime, so harmless): `DynamicData` is 9.1.2 in GUI and 9.0.4 in Core (via ReactiveUI 20.1.63); `Splat` is 15.3.1 in GUI and 15.1.1 in Core; Fody: BLD-12.
- **Failure scenario:** none today beyond restore and output bloat. On net10 they become NU1510 warnings.
- **Evidence:** greps as cited; NuGet nuspecs (`api.nuget.org/v3-flatcontainer`).
- **Fix:** one `chore/prune-packages` branch from master: drop Splat.Drawing, the GUI NexusModsNET reference, Microsoft.CSharp and ReactiveProperty (with its one-line change). Leave the 4.3.x pins until net10.
- **Confidence:** High for usage counts, Medium for why the pins exist.

### [Low] BLD-11: Known-vulnerable packages in the graph (none reachable today; net10 restore will report them)

- **Location:** direct `SharpCompress 0.39.0` (`GUI.csproj:62`); transitive `System.Drawing.Common 4.6.0` (via `AdonisUI 1.17.1`), `System.Private.Uri 4.3.0` and `System.Net.Http`/`System.Text.RegularExpressions 4.3.0` (via `NETStandard.Library 1.6.1`).
- **What:** OSV query results:
  - SharpCompress 0.39.0: **GHSA-6c8g-7p36-r338 / CVE-2026-44788**, path traversal in `IArchive.WriteToDirectory()` directory entries, fixed in 0.48.0. The app never calls `WriteToDirectory`. Import extracts only `.pak` entries by `Path.GetFileName(file.Key)` (`MainWindowViewModel.cs:3415-3426`), so the advisory is not reachable.
  - System.Drawing.Common 4.6.0: GHSA-rxg9-xrhp-64gj (critical).
  - System.Private.Uri 4.3.0: GHSA-5f2m-466j-3848, GHSA-x5qj-9vmx-7g6g, GHSA-xhfc-gr8f-ffwc.
  - System.Text.RegularExpressions 4.3.0: GHSA-cmhx-cq75-c4mj.
  - System.Net.Http 4.3.0: GHSA-7jgj-8wvc-jh57.

  On net8 the framework copies of those assemblies win conflict resolution, so the package binaries are not loaded **(inferred)**. On net10, `NuGetAuditMode` defaults to `all` and reports transitive advisories as NU1901–NU1904 warnings. Pruning should drop the framework-overlapping ones; whether System.Drawing.Common is pruned for WindowsDesktop is **(inferred)**.
- **Failure scenario:** the net10 branch shows a list of audit warnings. Someone adds `TreatWarningsAsErrors` and the build breaks; or the warnings get ignored wholesale and a real one is missed later.
- **Evidence:** OSV `querybatch`; [.NET 10: dotnet restore audits transitive packages](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/nugetaudit-transitive-packages).
- **Fix:** bump SharpCompress to ≥0.48 in its own branch (API changes possible between 0.39 and 0.48/1.0; covers Stage 2/3 zip work anyway). After net10, run `dotnet list package --vulnerable --include-transitive`. Suppress only verified-unreachable advisories with `<NuGetAuditSuppress Include="…"/>`.
- **Confidence:** High for the advisories, Medium for reachability.

### [Low] BLD-12: Core weaves with Fody 6.8.0, GUI with 6.9.2

- **Location:** `GUI.csproj:53` (`Fody 6.9.2 PrivateAssets=all`); `DivinityModManagerCore.csproj` has no Fody reference. `ReactiveUI.Fody 19.5.41`'s nuspec depends on `Fody >= 6.8.0`, and NuGet picks the lowest applicable version.
- **What:** the two projects use different weaver hosts. Latest Fody is 6.9.3.
- **Failure scenario:** a Fody/MSBuild incompatibility under the 18.x MSBuild or SDK 10 hits only Core. That would mean silently unwoven `[Reactive]` properties in Core, the heavier user (195 of 273).
- **Evidence:** nuspec dependency groups; csprojs.
- **Fix:** add `<PackageReference Include="Fody" Version="6.9.3" PrivateAssets="all" />` to Core and bump GUI to match. Leave the ReactiveUI.Fody 19.5.41 / ReactiveUI 20.1.63 pin alone (CLAUDE.md §4).
- **Confidence:** High for the version resolution, Low for real-world impact.

### [Low] BLD-13: Native libraries are loaded with ANSI `LoadLibraryA`, and failures are invisible

- **Location:** `src/Toolbox/NativeMethods.cs:7-8`, used by `ScriptExtender/Updater.cs:82`; `src/Core/Util/NativeLibraryHelper.cs:6-7`, used by `AppServices/ScreenReaderService.cs:61`.
- **What:** the path is marshalled as `LPStr` in the system ANSI code page. In Toolbox, `LoadLibrary` lacks `SetLastError=true`, so `ThrowExceptionForLastWin32Error` cannot report the real error. `ScreenReaderService` ignores the `0` return, and its `catch` never fires.
- **Failure scenario:** a game path such as `D:\Игры\Baldurs Gate 3\bin\DWrite.dll`, or a portable install under a non-ASCII user profile. Characters outside the code page become `?`, `LoadLibraryA` fails, and the Script Extender update or the screen-reader setup silently does nothing.
- **Evidence:** files above.
- **Fix:** use `NativeLibrary.TryLoad(path, out var h)` (managed, Unicode), or `LoadLibraryW` with `StringMarshalling.Utf16` and `SetLastError = true`, and log failures. A small upstreamable `fix/`.
- **Confidence:** High for the code, Medium for how often it happens.

### [Low] BLD-14: `BuildRelease.py` robustness

- **Location:** `BuildRelease.py`.
- **What:**
  - (a) **arcname.** `root.replace(src, "")` yields `"\\_Lib\\x.dll"` on Windows and `"/_Lib/x.dll"` on Linux. `zipfile` strips the leading separator and converts `\` to `/`. Simulated with `ntpath`/`posixpath`, the entries come out as `BG3ModManager.exe`, `_Lib/f.dll`, `Tools/f.dll` and `Resources/Icons/f.dll` on both. So the paths are correct, but only by `zipfile`'s normalisation, and the result is not robust if `src` ever becomes absolute or appears twice in a path.
  - (b) `time.sleep(3)` has no stated reason. MSBuild's `Move`/`Delete` are synchronous.
  - (c) `SilentRemove` swallows every exception (BLD-08).
  - (d) The `ZipFile` is not closed on exception, which leaves a partial `BG3ModManager_v….zip` behind.
  - (e) Missing argument → `BG3ModManager_vNone.zip`.
  - (f) `SilentCopyAndRemove` is dead code.
  - (g) `GUI.csproj:108` runs `python`. On a machine with only the `py` launcher, or the Microsoft Store alias, the Exec fails with code 9009 and a confusing message.
  - (h) The file is LF with no BOM, while the house style is tabs. Lines 26-31 use 4 spaces and the rest tabs.
- **Failure scenario:** a partial or misnamed zip, or a silent leftover (BLD-08).
- **Evidence:** the script; a local `ntpath` simulation (script in the scratchpad `bld/`).
- **Fix:** use `arcname=os.path.relpath(full, src)`, `with ZipFile(...)`, `sys.exit` when the version is missing, remove the sleep and the dead code, assert the contents (BLD-08), and use `sys.executable`-independent invocation (`py -3` fallback, or let CI provide `python`).
- **Confidence:** High.

### [Low] BLD-15: Version bump touches 4 files; it can be one

- **Location:** `GUI.csproj:16-17`, `app.manifest:3`, `Update.xml:3`.
- **What:** the owner keeps four copies of the version by hand. `RunBuildRelease` already passes the assembly version to `BuildRelease.py`.
- **Failure scenario:** a mismatch leads to an infinite or a missing update prompt (CLAUDE.md §2.5).
- **Fix:** replace the two properties with `<Version>1.0.12.9</Version>`. Have `BuildRelease.py` write `Update.xml`'s `<version>` (and optionally a `<checksum algorithm="SHA256">`, which AutoUpdater.NET supports) from the argument it receives. For a WPF exe the manifest `assemblyIdentity` version is informational **(inferred; verify)**, so freeze it at `1.0.0.0`. `Update.xml` is `merge=ours`-listed but has no driver, so upstream changes still surface.
- **Confidence:** Medium.

### [Low] BLD-16: Fork-facing metadata still points upstream

- **Location:**
  - `.github/ISSUE_TEMPLATE/config.yml:3-8`: LaughingLeader discussions.
  - `bug-report.yaml:23,31`: placeholders `1.0.9.10` and "Patch 3 Hotfix 2".
  - `README.md:11`: pinned 8.0.15 runtime.
  - `README.md:67-69`: "Building From Source" links Norbyte's lslib, while the submodule is LaughingLeader's fork. It mentions no submodules, MSBuild or GPLex/GPPG.
  - Licences: CrossSpeak is **LGPL-2.1** (`External/CrossSpeak/LICENSE.txt`), and the zip ships no third-party notices.
- **Failure scenario:** fork users file questions upstream. A public fork release ships an LGPL component without its notice.
- **Fix:** handle this in Stage 1 (`chore/fork-identity`, fork-only). Repoint or annotate the templates ("bugs that also occur in upstream belong upstream"), add a "Building" section that mirrors CLAUDE.md §2, and add `THIRD-PARTY-NOTICES.txt` to the zip.
- **Confidence:** High.

### [Info] BLD-17: Sizing the eventual ReactiveUI.Fody → ReactiveUI.SourceGenerators migration (not a recommendation to touch the pin now)

- **Counts (attribute parse over `src`):**

  | | `[Reactive]` | of which `private set` | `[ObservableAsProperty]` | classes to mark `partial` | `ToPropertyEx`/`ToUIProperty*` sites |
  |---|---|---|---|---|---|
  | Core | 195 (72 also `[DataMember]`) | 7 | 34 (30 in `DivinityModData.cs`) | ~22 | 33 house helpers + 5 raw `ToPropertyEx`, mostly Core |
  | GUI | 78 | 5 | 0 (46 hits are hand-written `ObservableAsPropertyHelper` fields, which need no change) | ~11 more (1 already partial) | — |
  | Toolbox | 0 | | 0 | | |

  No `[Reactive]` property has an initializer, so C# 13 partial properties are sufficient.
- **Mechanical changes:**
  1. Remove `Fody`, `ReactiveUI.Fody`, `FodyWeavers.xml/.xsd` (both projects) and `GUI.csproj:77` `<Resource Include="FodyWeavers.xml"/>`.
  2. Upgrade to ReactiveUI ≥ 24.4, which bundles SourceGenerators 4.0 per reactiveui.net. Alternatively, add standalone `ReactiveUI.SourceGenerators 2.x` on ReactiveUI 20, as `upstream/avalonia` does with ReactiveUI 23 **(inferred compatible)**.
  3. In both `Usings.cs`, replace `global using ReactiveUI.Fody.Helpers;` with `global using ReactiveUI.SourceGenerators;`. Because the using is global, convert project-at-a-time, not file-at-a-time.
  4. Mark the ~33 classes `partial`, including the outer class of any nested ones.
  5. Rewrite `{ get; set; }` → `public partial T X { get; set; }` (273 sites; regex-able). Keep the 12 `private set` modifiers.
  6. Rewrite the 34 OAPH declarations to `public partial T X { get; }`, and the 38 helper call sites to assign the generated `_xHelper` with `ToProperty(this, nameof(X), …)`. Rewrite `ToUIProperty`/`ToUIPropertyImmediate` (`ReactiveExtensions.cs:17-36`) to take or return the helper.
  7. **The hazard:** `MainWindowViewModel.cs:1127` restores settings with `SetFrom<DivinityModManagerSettings, ReactiveAttribute>`, which reflects `Attribute.IsDefined(prop, ReactiveAttribute)` (`ModelExtensions.cs:44-56`). The four objects that must never be reassigned (`ExtenderSettings`, `ExtenderUpdaterSettings`, `Window`, `Confirmations`) are excluded *only* because they lack `[Reactive]`. Three `[Reactive]` properties lack `[DataMember]` (`DisplayFileNames`, `DefaultExtenderLogDirectory`, `ExtenderLogDirectory`). If the generated attribute is not retained in metadata, nothing is restored. If it is swapped for a `[DataMember]` allow-list (what `upstream/avalonia` did: `SetFromDataMember`), the four objects get reassigned, which is bug `7b57e5a` again. So first make `SetFrom` honour the existing but unread `[IgnoreSetFrom]`, mark those four with it, then change the allow-list.
  8. C# 13 requires SDK ≥ 9.0.300, so this naturally comes after net10.
- **Effort:** about 1–2 days mechanical plus a full UI regression pass, done as a single `chore/` per project.
- **Evidence:** parse script output; [reactiveui.net migrating from Fody](https://reactiveui.net/documentation/source-generators/migrating-from-fody/); `wt-avalonia` uses `[Reactive] public partial …` (135 sites).
- **Confidence:** High for the counts, Medium for package-version details.

### [Info] BLD-18: Cheap Linux-readiness issues (each costs nothing to fix)

- `Directory.build.props` (all five) has a lowercase `b`. MSBuild looks for `Directory.Build.props`, so on a case-sensitive file system the `obj\` redirection is silently not applied.
- `LSLib.csproj:62-63` and `LSLibStats.csproj:11-12` reference `..\external\gppg` while the folder is `External`, which breaks on Linux.
- The GUI pre-build event uses `cmd` syntax (BLD-09).
- `GUI.csproj:108` calls `python`, which is often `python3` on Linux.
- **Fix:** rename to `Directory.Build.props` (`git mv`, a case-only rename on Windows), fix the casing, use `RemoveDir`. These are upstreamable `chore/` changes except the lslib ones (separate repo).
- **Confidence:** High.

### [Info] BLD-19: The GUI TFM's Windows-SDK version is forced by ReactiveUI.WPF, although the code uses no WinRT API

- **Location:** `GUI.csproj:5`; `GUI.csproj:95` excludes `WinRT.Runtime.dll` from `_Lib`.
- **What:** a grep finds no `Windows.*` WinRT namespace in `src`, but `ReactiveUI.WPF 20.1.63` ships only `net8.0-windows10.0.17763`/`19041` assets. So the TFM cannot drop to plain `net10.0-windows`. Keep `net10.0-windows10.0.22621.0` (or lower it to `10.0.17763.0`) when migrating. The CsWinRT projection DLL ships either way.
- **Confidence:** High.

### [Info] BLD-20: The ubuntu CI job can compile almost nothing useful today

- **What:** `dotnet build -p:EnableWindowsTargeting=true` can build **Toolbox** and **CrossSpeak** only:
  - `BG3ModManager.sln` contains `LSLibNative.vcxproj`, so `dotnet build` of the sln fails outright.
  - LSLib and Core reference it, and LSLib calls `LSLib.Native.*` types, so neither compiles without the C++/CLI assembly.
  - Without Core, GUI cannot compile either.
  - The GPLex/GPPG exes are .NET Framework Windows binaries, plus the path-case issues (BLD-18).

  After BLD-06 (managed LZ4, YaccLexTools) and a solution filter without the vcxproj, an ubuntu job could compile everything, WPF included, with `EnableWindowsTargeting`. That would be a fast compile gate and a Linux-readiness signal. It still could not *run* anything that touches WPF types; note that Core itself is `UseWPF`.
- **Recommendation:** skip ubuntu until BLD-06 lands. The snippet in the CI section is ready for then.
- **Confidence:** High.

## .NET 10 migration plan

### Decision summary

- **Option A (minimum, recommended for the deadline):** retarget only the projects in this repo (GUI, Core, Toolbox) to net10. Leave the lslib and CrossSpeak submodules on `net8.0`/`netstandard2.0`. A net10 process loads net8 libraries; library TFMs do not carry support status, the runtime does. The one unknown is whether the net8-built C++/CLI LSLibNative loads in a net10 process. ijwhost binds to the already-running runtime, which should be fine **(inferred; first item to verify)**. If it does not, LSLibNative must be retargeted, which means VS 2026 C++/CLI and a PR to LaughingLeader/lslib.
- **Option B (strategic, after A):** remove LSLibNative (BLD-06) in the lslib submodule fork. This removes the C++/CLI toolset coupling, the VC++ redist prerequisite and the CI C++ workload, and opens the way to `dotnet build` and Linux.
- **Branching:** `chore/net10` cut from `master` (upstreamable; upstream master is net8 and quiet, so expect it to live on `fork/main` until upstream takes it). Submodule changes go to LaughingLeader/lslib first. A superproject commit that points at a submodule commit existing only in a SpasticPengu fork breaks `--recurse-submodules` clones that use upstream's `.gitmodules`, so repointing `.gitmodules` is a `fork/main`-only change.

### Steps

1. **Toolchain (owner, before code):** install VS Build Tools 2026 (18.x) side by side. Components: Desktop development with C++, C++/CLI support for v145, MSVC v143 build tools plus C++/CLI support for v143 (to keep LSLibNative's `v143` buildable without editing the submodule), the .NET SDK component, and Windows 11 SDK 10.0.26100. Confirm with `"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\msbuild.exe" -version`. The path is (inferred); find the real one with `vswhere -latest -products * -find MSBuild\**\Bin\amd64\MSBuild.exe`.
2. **Pin the SDK:** add `global.json` with `"version": "10.0.100", "rollForward": "latestFeature"`. The owner's 10.0.400 qualifies.
3. **TFMs:**
   - `GUI.csproj:5` → `net10.0-windows10.0.22621.0`
   - `DivinityModManagerCore.csproj:3` → `net10.0-windows`
   - `Toolbox.csproj:4` → `net10.0-windows`
   - Leave `External/**` unchanged (option A).
4. **Packages:**
   - Delete `Microsoft.CSharp`, `System.Net.Http` and `System.Text.RegularExpressions` from Core (NU1510).
   - LSLib's copies stay because LSLib stays net8.
   - Do BLD-10's removals in the same branch or before it.
   - Pin Fody 6.9.3 in both projects (BLD-12).
   - Keep ReactiveUI 20.1.63 + ReactiveUI.Fody 19.5.41: the weaver rewrites IL and is TFM-agnostic, and its `net8.0` helper asset is selected for net10 **(inferred; verify)**.
5. **Package compatibility on net10:** none of these ships a net10 asset; all resolve to their highest compatible one.

   | Package | Asset picked on net10 | Assessment |
   |---|---|---|
   | AdonisUI / ClassicTheme 1.17.1 | `net5.0-windows7.0` | unmaintained since 2021; XAML-only themes, so the WPF 10 DynamicResource change is the risk (below) |
   | gong-wpf-dragdrop 4.0.0 | `net8.0-windows7.0` | in-process drag carries an object reference, no serialization |
   | Autoupdater.NET.Official 1.9.2 (1.9.3 exists) | `net8.0-windows7.0` | depends on WebView2 1.0.2592.51 |
   | Ookii.Dialogs.Wpf 5.0.1 | `net6.0-windows7.0` | |
   | DotNetProjects.Extended.Wpf.Toolkit 5.0.124 (5.0.129 exists) | `net8.0-windows7.0` | |
   | WpfScreenHelper 2.1.1 | `net8.0-windows7.0` | |
   | NexusModsNET 1.0.5 | `netstandard2.0` | |
   | PowerArgs 4.0.3 | `net6.0` | |
   | SharpCompress 0.39 | `net8.0` | |

   Expect no compile breaks from these **(inferred)**. Runtime risks are listed below.
6. **Runtime and API breaking changes, checked against this code:**
   - **BinaryFormatter removed (.NET 9):** no `BinaryFormatter`, `[Serializable]`, `ISerializable`, `SetData`/`GetData` or `IDataObject` use in `src`. Clipboard use is `Clipboard.SetText` only: `MainWindowViewModel.cs:5120,5144,5307`, `VersionGeneratorWindow.xaml.cs:27`, `DivinityGlobalCommands.cs:76`. Drag-drop is gong-wpf-dragdrop with in-process objects (`ModListDragHandler.cs:64-80`, a lazy `IEnumerable<DivinityModData>`) and file drops read via `DataObject.ContainsFileDropList` (`ModListDropHandler.cs:89,118`). WPF serializes custom drag data only when another process asks for the format, and `DivinityModData` was never serializable, so nothing changes **(inferred)**. Verify by dragging within and between lists and dropping files from Explorer.
   - **WPF 10, DynamicResource type mismatch now crashes:** every `Color="{DynamicResource …}"` in this repo points at a `Color` key (`Dark.xaml:10-22`, `Light.xaml:10-13`, `MainResourceDictionary.xaml:1337` → `adonisUI:Colors.Layer0BackgroundColor`), which is correct. AdonisUI and Xceed's own dictionaries cannot be checked without unpacking the packages, so smoke-test both themes in every window.
   - **WPF 10, empty `ColumnDefinitions`/`RowDefinitions` are disallowed:** none found (grep).
   - **C# 14 `field` keyword:** `field` appears only as a method parameter (`BaseHistoryObject.cs:17,41`) and a local (`EnumExtension.cs:51`), never inside a property accessor, so it is safe. C# 14 span overload resolution: no `.Reverse()` on arrays, and no `Contains` inside expression trees seen.
   - **.NET 9 CET enabled by default:** the Toolbox apphost becomes shadow-stack-enabled and `LoadLibrary`s third-party `DWrite.dll`. The GUI loads `Tolk.dll`, `nvdaControllerClient64.dll`, `SAAPI64.dll` and WebView2Loader. Test the Script Extender update on CET hardware (Intel 11th gen+ / Zen 3+). If it crashes, set `<CETCompat>false</CETCompat>` in Toolbox **(inferred risk)**.
   - **.NET 9 saturating float→int conversion, `ZipArchiveEntry` UTF-8 flag, `BinaryReader.ReadString` U+FFFD:** low exposure. The Script Extender download uses `ZipArchive` (`MainWindowViewModel.cs:356`) and import/export uses SharpCompress.
   - **.NET 10 `DllImportSearchPath.AssemblyDirectory` semantics:** not used in `src`.
   - **Obsolete-as-error:** no `TreatWarningsAsErrors` anywhere, and no obsolete crypto/WebClient APIs in `src` or `LSLib` (grep). Expect SYSLIB warnings only.
   - **NuGetAudit `all` and NU1510:** BLD-10, BLD-11.
7. **Build and publish:** Debug then Publish with the 18.x MSBuild (`-p:Platform=x64 -nodeReuse:false`). Check that `_Lib` still contains everything, that `Program.cs`'s `AssemblyResolve` still covers it (Program.cs:13-29), and that `WinRT.Runtime.dll` stays at root.
8. **User transition:**
   - Change `README.md:11` to the .NET 10 Desktop Runtime (`https://dotnet.microsoft.com/download/dotnet/10.0`; `winget install Microsoft.DotNet.DesktopRuntime.10` **(inferred ID)**).
   - In the fork's `Update.xml` changelog (after Stage 1 repoints it), state the new prerequisite.
   - Optionally ship one last net8 build with `<RollForward>Major</RollForward>` (it runs on .NET 10 when .NET 8 is absent) to bridge users. That build would hit the .NET 10 runtime changes (DynamicResource and others) without being recompiled, so test it too.
   - Self-contained publishing is **not** a drop-in alternative. `SetupLibFolder` would move `coreclr.dll`/`System.*.dll` into `_Lib`, and the host cannot start.
   - If LSLibNative is ever built with v145, users need a VC++ redist ≥ 14.50. The README's `aka.ms/vs/17/release/vc_redist.x64.exe` link should then point at the current permalink **(inferred)**. This is one more argument for option B.

### What users of a .NET 8 build face after EOL

The app keeps working, on a runtime that no longer gets security fixes. The manager parses untrusted `.pak`/zip/JSON content from mod sites and downloads over HTTP, so the runtime's patch level is not academic. Users who later install only .NET 10 cannot start a net8 build: there is no `RollForward`, and the default rolls only within major version 8. Microsoft also says the VS 2022 .NET 8/9 components will be marked out of support.

### Risks

- **M1:** net8 C++/CLI fails to load in a net10 process. Fallback: retarget LSLibNative (VS 2026 C++/CLI, lslib PR) or option B.
- **M2:** Fody or ReactiveUI.Fody does not weave under MSBuild 18. The symptom is bindings that compile but never update (CLAUDE.md §4). Detect it with an ILSpy check of one `set_X` and a UI smoke test.
- **M3:** AdonisUI/Xceed theme XAML trips the .NET 10 DynamicResource crash at startup or on theme toggle.
- **M4:** CET versus third-party native DLLs (Toolbox/DWrite, screen readers).
- **M5:** Startup or perf regressions confound Stage 4–6 measurements. Re-baseline on net10 before comparing.
- **M6:** Users stranded by an auto-update to a runtime they don't have.
- **M7:** Submodule pointer pushed to a commit unreachable from upstream's `.gitmodules`.
- **M8:** NuGet audit noise hides a real advisory.

### Verification

1. Debug and Publish build twice from a clean clone on VS 18 (after the CI fixes).
2. Run the app against the real 1,121-mod setup:
   - mod list snapshot diff against net8 (R1)
   - cold start, mod-load time and memory against 15.2 s / 9.27 s / 478 MB
3. Drag-drop within and between lists, and from Explorer.
4. Copy-to-clipboard actions.
5. Light/Dark toggle (Ctrl+L) with every window opened once.
6. Settings save, restart, and check every value restored. Specifically check that `ExtenderSettings` edits still persist (bug `7b57e5a`).
7. Script Extender update via Toolbox.
8. Screen reader.
9. Zip export/import.
10. Save-game order import, which exercises LSLibNative's chunked LZ4 (`LSFReader`).
11. AutoUpdater with a low `AssemblyVersion`.
12. Launch on a clean VM with only the .NET 10 Desktop Runtime; then a second VM with only .NET 8, to see the user-facing failure dialog.
13. `dotnet list package --vulnerable --include-transitive` output reviewed.

## CI workflow (YAML)

Draft for `.github/workflows/build.yml`. It is **not committed**. It handles every trap without relying on build order:

- GPLex/GPPG: downloaded from the official distros named in `External/lslib/README.md` and hash-pinned.
- Parser glob trap: the generating targets are invoked on their own before the real build, so a single build sees the generated files.
- CrossSpeak pack trap: `-p:GeneratePackageOnBuild=false` as a *global* property overrides `CrossSpeak.csproj:5`, so no Debug-first build is needed.
- Platform: always `-p:Platform=x64` (BLD-04).
- Build: from the `.sln`, with `-nodeReuse:false` and recursive submodules.
- Artifacts and binlogs are uploaded.

> **Why `-p:PlatformToolset=v145`:** `windows-latest` has been the VS 2026 image since June 2026. Its readme lists the v143 compiler (`VC.14.44.17.14.x86.x64`) but C++/CLI support only for the default v145 toolset (`VC.CLI.Support`), so building LSLibNative with v143 there is likely to fail **(inferred)**. Overriding to v145 on the command line leaves the submodule untouched. For exact parity with the owner's VS 2022 17.14 / v143 machine, switch to `runs-on: windows-2022` and delete the override.

```yaml
name: build

on:
  push:
    branches: [master, 'fork/**', 'fix/**', 'perf/**', 'chore/**', 'feat/**', 'ui/**']
  pull_request:
  workflow_dispatch:

permissions:
  contents: read

concurrency:
  group: build-${{ github.ref }}
  cancel-in-progress: true

env:
  DOTNET_NOLOGO: '1'
  DOTNET_CLI_TELEMETRY_OPTOUT: '1'
  NUGET_PACKAGES: ${{ github.workspace }}\.nuget\packages
  # Official distros referenced by External/lslib/README.md (checked reachable 2026-11-03:
  # gplex 922,093 bytes, S3 ETag a81af1230a68b9ebf0b761f5947faad0; gppg 682,509 bytes, ETag 11857a9375b2a7831f24e6021e75dc05).
  # Fill the SHA-256 values from the owner's verified copies:  Get-FileHash .\gplex-distro-1_2_2.zip
  GPLEX_URL: https://s3.eu-central-1.amazonaws.com/nb-stor/dos-legacy/ExportTool/gplex-distro-1_2_2.zip
  GPPG_URL: https://s3.eu-central-1.amazonaws.com/nb-stor/dos-legacy/ExportTool/gppg-distro-1_5_2.zip
  GPLEX_SHA256: 'FILL-ME'
  GPPG_SHA256: 'FILL-ME'
  # windows-latest = VS 2026 (18.x). See note above; remove for windows-2022.
  PLATFORM_TOOLSET: v145

jobs:
  windows:
    name: ${{ matrix.configuration }} (x64)
    runs-on: windows-latest
    timeout-minutes: 45
    strategy:
      fail-fast: false
      matrix:
        configuration: [Debug, Publish]

    steps:
      - name: Checkout (with submodules)
        uses: actions/checkout@v5
        with:
          submodules: recursive
          fetch-depth: 1
          persist-credentials: false

      - name: Set up .NET SDK
        uses: actions/setup-dotnet@v5
        with:
          # Replace with  global-json-file: global.json  once BLD-03 lands.
          dotnet-version: '8.0.4xx'

      - name: Set up MSBuild (full VS MSBuild; dotnet build cannot build the C++/CLI project)
        uses: microsoft/setup-msbuild@v2
        with:
          msbuild-architecture: x64

      - name: Set up Python (BuildRelease.py runs inside the Publish build)
        if: matrix.configuration == 'Publish'
        uses: actions/setup-python@v6
        with:
          python-version: '3.12'

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ${{ env.NUGET_PACKAGES }}
          key: nuget-${{ runner.os }}-${{ hashFiles('src/**/*.csproj', 'External/**/*.csproj', 'External/**/*.vcxproj') }}
          restore-keys: nuget-${{ runner.os }}-

      - name: Fetch GPLex 1.2.2 and GPPG 1.5.2 (hash-pinned)
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          $dest = Join-Path $env:GITHUB_WORKSPACE 'External/lslib/External/gppg/binaries'
          New-Item -ItemType Directory -Force -Path $dest | Out-Null
          $tmp = Join-Path $env:RUNNER_TEMP 'parsergen'
          New-Item -ItemType Directory -Force -Path $tmp | Out-Null
          $tools = @(
            @{ Url = $env:GPLEX_URL; Sha = $env:GPLEX_SHA256; Exe = 'gplex.exe' },
            @{ Url = $env:GPPG_URL;  Sha = $env:GPPG_SHA256;  Exe = 'gppg.exe' }
          )
          foreach ($t in $tools) {
            $zip = Join-Path $tmp (Split-Path $t.Url -Leaf)
            Invoke-WebRequest -Uri $t.Url -OutFile $zip
            $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
            if ($hash -ne $t.Sha) { throw "SHA-256 mismatch for $(Split-Path $zip -Leaf): $hash" }
            $out = Join-Path $tmp ([IO.Path]::GetFileNameWithoutExtension($zip))
            Expand-Archive -Path $zip -DestinationPath $out -Force
            $exe = Get-ChildItem -Path $out -Recurse -File | Where-Object Name -ieq $t.Exe | Select-Object -First 1
            if (-not $exe) { throw "$($t.Exe) not found inside $(Split-Path $zip -Leaf)" }
            # copy the exe and any sibling files it ships with
            Copy-Item -Path (Join-Path $exe.DirectoryName '*') -Destination $dest -Recurse -Force
          }
          Get-ChildItem $dest | Format-Table Name, Length

      - name: Pre-generate parser sources (LSLib + LSLibStats; avoids the first-build glob trap)
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          $sd = "$env:GITHUB_WORKSPACE\"
          msbuild External/lslib/LSLib/LSLib.csproj -t:PreBuildStoryGppgFiles "-p:SolutionDir=$sd" -nodeReuse:false -nologo -v:minimal
          if ($LASTEXITCODE) { exit $LASTEXITCODE }
          msbuild External/lslib/LSLibStats/LSLibStats.csproj -t:PreBuildStatsGppgFiles "-p:SolutionDir=$sd" -nodeReuse:false -nologo -v:minimal
          if ($LASTEXITCODE) { exit $LASTEXITCODE }
          $expected = @(
            'External/lslib/LSLib/LS/Story/GoalParser/Goal.lex.cs',
            'External/lslib/LSLib/LS/Story/GoalParser/Goal.yy.cs',
            'External/lslib/LSLib/LS/Story/HeaderParser/StoryHeader.lex.cs',
            'External/lslib/LSLib/LS/Story/HeaderParser/StoryHeader.yy.cs'
          )
          foreach ($f in $expected) { if (-not (Test-Path $f)) { throw "parser generation did not produce $f" } }

      - name: Build solution
        shell: pwsh
        run: |
          msbuild BG3ModManager.sln -restore -t:Build `
            -p:Configuration=${{ matrix.configuration }} -p:Platform=x64 `
            -p:GeneratePackageOnBuild=false `
            -p:PlatformToolset=$env:PLATFORM_TOOLSET `
            -m -nodeReuse:false -nologo -v:minimal `
            "-bl:$env:RUNNER_TEMP\build-${{ matrix.configuration }}.binlog"
          if ($LASTEXITCODE) { exit $LASTEXITCODE }

      - name: Verify output layout
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          $cfg = '${{ matrix.configuration }}'
          $bin = "bin/$cfg"
          foreach ($f in 'BG3ModManager.exe', 'BG3ModManager.dll', 'Tools/Toolbox.exe', 'Resources/IgnoredMods.json') {
            if (-not (Test-Path "$bin/$f")) { throw "missing $bin/$f" }
          }
          if ($cfg -eq 'Publish') {
            foreach ($f in '_Lib/DivinityModManagerCore.dll', '_Lib/LSLib.dll', '_Lib/LSLibNative.dll', '_Lib/CrossSpeak.dll') {
              if (-not (Test-Path "$bin/$f")) { throw "missing $bin/$f" }
            }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path 'BG3ModManager_Latest.zip'))
            try {
              $names = @($zip.Entries | ForEach-Object FullName)
              $bad = $names | Where-Object { $_ -match '^(Data|_Logs|Orders)/' -or $_ -match '\.pdb$' -or $_ -in @('debug', 'perfscroll') }
              if ($bad) { throw "release zip contains forbidden entries: $($bad -join ', ')" }
              foreach ($need in 'BG3ModManager.exe', 'Tools/Toolbox.exe') {
                if ($need -notin $names) { throw "release zip is missing $need" }
              }
              "zip OK: $($names.Count) entries"
            } finally { $zip.Dispose() }
          }

      - name: Upload release zip
        if: matrix.configuration == 'Publish'
        uses: actions/upload-artifact@v4
        with:
          name: BG3ModManager-Publish-${{ github.sha }}
          path: BG3ModManager_v*.zip
          if-no-files-found: error
          retention-days: 30

      - name: Upload Debug build (has symbols; use to reproduce user stack traces)
        if: matrix.configuration == 'Debug'
        uses: actions/upload-artifact@v4
        with:
          name: BG3ModManager-Debug-${{ github.sha }}
          path: bin/Debug/
          if-no-files-found: error
          retention-days: 7

      - name: Upload binlog
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: binlog-${{ matrix.configuration }}-${{ github.sha }}
          path: ${{ runner.temp }}/build-${{ matrix.configuration }}.binlog
          retention-days: 7
```

Notes:

- Action major versions (`checkout@v5`, `setup-dotnet@v5`, `setup-python@v6`, `cache@v4`, `upload-artifact@v4`, `setup-msbuild@v2`) were not confirmed against today's releases (GitHub API access was blocked in this session). Use the current majors and pin them by commit SHA.
- Binlogs record environment and properties. Do not upload them once secrets are added (e.g. a signing step).
- When the clean-checkout fixes land in the submodules, keep the pre-generate step and the `GeneratePackageOnBuild=false` override for one cycle, then delete them. CI then proves the fixes.
- `actions/checkout` on Windows inherits the image's `core.autocrlf` (the default is `true` in Git for Windows), which only affects the working tree.

**Optional ubuntu job (only after BLD-06, using a `.slnf` without the vcxproj):**

```yaml
  linux-compile:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
        with: { submodules: recursive, fetch-depth: 1, persist-credentials: false }
      - uses: actions/setup-dotnet@v5
        with: { global-json-file: global.json }
      - run: dotnet build BG3ModManager.Managed.slnf -c Debug -p:Platform=x64 -p:EnableWindowsTargeting=true -p:GeneratePackageOnBuild=false
```

Today, without BLD-06, the most it could compile is `src/Toolbox/Toolbox.csproj` and `CrossSpeak.csproj` (BLD-20). It is not worth the runner minutes yet.

## Needs a Windows run to settle

1. **Which SDK the owner's MSBuild 17.14 actually uses today.** Running `msbuild BG3ModManager.sln -getProperty:NETCoreSdkVersion` or checking a binlog will show it. Microsoft's table says 10.0.400 needs VS 18.0, yet builds succeed, so it is probably 8.0.425 or a 9.0.3xx from the Build Tools component. This settles BLD-03.
2. **`windows-latest` (VS 2026):** does `LSLibNative.vcxproj` build with v143 C++/CLI, or only with `-p:PlatformToolset=v145`? Does v145 `/clr:netcore` targeting `net8.0` build and run? First CI run.
3. **GPLex/GPPG distro zips:** SHA-256 of the owner's verified copies, the folder layout inside the zips, and whether the exes run on Windows Server 2025 without .NET Framework 3.5.
4. **A clean clone with only `-p:GeneratePackageOnBuild=false` plus the pre-generate step builds Publish in a single pass.** Also confirm that LSLibStats fails a clean first build like LSLib (BLD-05).
5. **`Publish|Any CPU` output:** check `dumpbin /dependents bin\Publish\_Lib\LSLibNative.dll` for `VCRUNTIME140D.dll`/`ucrtbased.dll` (BLD-04).
6. **net10 option A:** does the net8 C++/CLI `LSLibNative.dll` load in a net10 process? Save-game import exercises it.
7. **net10:**
   - Fody/ReactiveUI.Fody weaving: ILSpy on a `set_X`, plus a UI update.
   - AdonisUI/Xceed theme dictionaries under the .NET 10 DynamicResource change: both themes, all windows.
   - gong drag-drop and file drop.
   - CET with Toolbox → DWrite.dll on CET-capable hardware.
   - Screen-reader DLL loading.
8. **Re-baseline on net10** (cold start, mod load, memory) before any Stage 4–6 comparison.
9. **`app.manifest` `assemblyIdentity` version:** confirm that freezing it at `1.0.0.0` changes nothing observable (BLD-15).
10. **`RollForward=Major` bridge build:** a net8 build running on a machine with only .NET 10.
11. **The full NuGet audit list after a net10 restore** (`dotnet list package --vulnerable --include-transitive`), including whether System.Drawing.Common 4.6.0 is pruned.
12. **`LoadLibraryA` failure** with a non-ANSI game path, reproduced to confirm BLD-13's impact.
