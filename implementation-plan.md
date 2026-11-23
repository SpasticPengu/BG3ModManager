# BG3 Mod Manager fork: implementation plan

The plan for the SpasticPengu fork of BG3 Mod Manager. Work that could also help the original project
("upstream") comes before work only this fork wants, so each fix can be offered to the original author
without dragging fork-only changes along with it.

- **Based on:** the original project's `master` branch (a Windows app built with WPF on .NET 8)
- **Fork work so far:** 22 commits on 9 branches; `master` itself is untouched
- **Status:** Stage F under way (7 of 11 fixes done, not pushed); smoother scrolling (6.1) is paused
- **Written** 21 September 2026, **updated** 23 November 2026

<a id="resume"></a>

## Resume here (updated 23 November 2026)

Read this first. It says where things stand and what comes next, in plain terms. The sections further
down keep the technical detail for whoever does the work.

### Where things stand

- **Building the app works** (Stage 0, done in September). Two build problems that only hit a fresh
  download of the code are written up but not fixed.
- **Smoother scrolling is half done** (Stage 6.1). Drawing a mod's name is now about 10 times cheaper,
  and each screen redraw while scrolling about a quarter cheaper. The work is on its own branch, `perf/list-render`, paused since
  23 September. Some temporary measuring code has to come out before it can be offered to the original
  author.
- **An automated code review** ran in the cloud on 3 November and was checked again on this PC on
  23 November. Most of it holds up, and it found about 20 bugs the plan didn't know about. Some of them
  can quietly lose a load order, crash the app or reset settings. They are collected in
  [Stage F](#stageF).
- **Stage F is under way: 7 of its 11 fixes are done** (23 November), each on its own branch. Fixes 1 to 5
  were checked by a small test program and in one real run of the app (all passed); an independent review
  then found a few flaws in them, now fixed and re-checked. They fix: the window position and the
  admin-warning choice being forgotten; Save or Export crashing when mods depend on each other in a loop;
  Save deleting the mods of an order that aren't installed; errors in background work leaving the app stuck
  on "Loading..."; and a single-letter shortcut crashing every launch. Two extra problems turned up and were
  fixed on the way: Export left out a dependency's own dependencies, and a restored window could land on an
  unplugged monitor. Your own data: none of your mods loop, 13 of your 16 saved orders hold mods that aren't
  installed (a Save would have deleted them), and 3 orders were missing one dependency each.
- **Fixes 6 and 7 are done too** (test program and independent review, not yet the real app). Export to game
  now keeps a copy of the game's previous mod list, never jumps to another saved order, never locks the app,
  and refuses to write a list the game couldn't read. The "use the last exported order?" question at startup
  can no longer empty that backup; on your orders, answering Yes would have overwritten your saved order
  "5 Player - Hard UW". Your own `modsettings.lsx.bak` (934 mods, 19 September) is safe: the app's new
  backup uses its own name, `modsettings.lsx.bg3mm.bak`.
- **Everything else hasn't started.** The review sketched designs for most stages, and a new
  [Stage L](#stageL) for the mod library you asked for: keep every version of each mod, and let each
  load order use its own versions.
- **.NET 8, the framework the app runs on, went out of support on 10 November.** The app still works,
  but Microsoft no longer fixes security problems in it. Moving to .NET 10 first needs newer Visual
  Studio build tools on this PC.

### What's next

1. **Fix the bugs that lose data or crash the app** ([Stage F](#stageF)). Each fix is small and gets its
   own branch, so it can be offered to the original author too. *Decided 23 November: this goes first,
   in the order of the Stage F checklist below.* **Next up: fix 8, never write a broken mod ID into the
   game's mod list** (`fix/modsettings-write-guard`); start at [Stage F progress](#stageFprogress), which
   says how the others were done and checked.
2. **Measure before speeding anything up.** Two test runs on this PC: one shows what really makes
   startup slow, the other what makes search slow. The review found that the plan's guesses for both
   were partly wrong.
3. **Finish smoother scrolling** (6.1) and offer it to the original author.
4. **Stage 1 before sharing a build with friends.** Until then, a build would "update" itself back to
   the original author's version.

### Decisions waiting on you

- **Stage L (mod library):** add it to the plan as designed? It would come after "overrides in the load
  order" (6.2) and before exporting several orders at once (Stage 8).
- **.NET 10:** when to move? It needs Visual Studio's 2026 build tools installed first.
- **Smaller choices the review left open:** the version-number scheme, automatic release builds and the
  donation links (Stage 1), whether fuzzy search reorders the active list (Stage 5), and the open
  questions on overrides and categories (Stage 6).
- **Where the review files live:** on their own branch (now), or next to this plan on the `notes` branch.
- **Stage F upstream:** *decided 23 November:* no fix is offered on its own. All of Stage F may be offered
  together once it is finished, and even that is still only a maybe: ask again at the end of Stage F.

### Housekeeping

- This plan, `CLAUDE.md` and `tasks/lessons.md` are kept out of the code history on purpose (listed in
  `.git/info/exclude`), so they can never slip into a fix sent to the original author. Copies are saved
  on the `notes` branch: update them there after each change.
- The earlier HTML versions of this plan (`implementation-plan.html`, `plan-overview.html`) are retired.
  Their history is on the `notes` branch.
- The review's line numbers refer to `master` at commit `fc35358`. The original project hasn't changed
  since (checked 23 November).

## Checklist

**Stage 0: build tools and first build**
- [x] Install the build tools (0.1)
- [x] Make the release build's cleanup step safe (0.2, branch `fix/publish-prebuild-quoting`)
- [ ] Offer 0.2 to the original author
- [ ] Fix the first build failing on a fresh download (0.2a, inside LSLib, the bundled library that reads game files)
- [ ] Fix release builds failing on a fresh download (0.2b, inside CrossSpeak, the bundled screen-reader library)
- [x] Measure the starting point: load time, search speed, scrolling (0.3)

**Stage 1: make the app know it's this fork**
- [ ] Point the updater and links at this fork (`chore/fork-identity`)
- [ ] Decide the review's Stage 1 choices (version numbers, release automation, donation links)

**Stage F: bugs that lose data or crash the app (started 23 November, in this order)**

The first fix is the smallest and is sure to affect you, so it also proves the routine (new branch, build,
check, commit) before the riskier ones. Crashes and lost load orders come next.
- [x] Remember window size, position and the admin-warning choice (`fix/settings-restore`, `f4d3d737`; checked
  without opening the app, one real launch still to do)
- [x] Save or Export crashes when mods depend on each other in a loop (`fix/dependency-cycle`, `9f854767`). Also
  fixed: Export silently left out a dependency's own dependencies. None of your mods loop; 3 of your saved orders
  were missing one such dependency each
- [x] Saving drops mods that aren't installed from the order (`fix/order-keep-missing`, `bb07d2bf`). 13 of your 16
  saved orders hold 1 to 16 such mods each; a Save would have deleted them
- [x] Show errors from background work instead of hanging on "Loading..." (`fix/background-errors`, `7f19c08c`)
- [x] Every launch crashes after a hotkey is set to a single letter (`fix/hotkey-validation`, `c8c21eed`)
- [x] One real launch of the app to confirm the five fixes above (23 November, all 17 checks passed), plus an
  independent review whose findings are fixed in follow-up commits (see [Stage F progress](#stageFprogress))
- [x] Export to game: keep a backup, don't switch orders by itself, don't lock up (`fix/export-to-game`, `1ff34c38`).
  Also: it refuses to write a list the game couldn't read, and saved orders can no longer be left half-written
- [x] The "use the last exported order?" prompt can wipe the backup (`fix/restore-prompt`, `da623aaf`). On your
  orders a Yes would have overwritten "5 Player - Hard UW"; one backup per profile is left for later (rarely matters)
- [ ] Never write a broken mod ID into the game's mod list file (`fix/modsettings-write-guard`)
- [ ] Updater: no "up to date" after a failed check, no closing on a cancelled download (`fix/updater-errors`)
- [ ] A booby-trapped mod can't write outside the Mods folder (`fix/path-containment`)
- [ ] Never offer the game's own files for deletion (`fix/duplicate-dialog-larian`)

**Stage 2: exporting a zip**
- [ ] Fix the export bugs as revised in Stage 2 (`fix/zip-export-corruption`)

**Stage 3: importing a zip**
- [ ] Safe mod-file writes: write to a temporary file, then swap it in; never replace a newer mod with an older one
- [ ] Order names: "Current" imports, imports with an existing order's name, duplicate names in one zip
- [ ] The other import bugs listed in Stage 3

**Stage 4: faster startup**
- [ ] Measure first: where the 9.3 seconds actually go
- [ ] Then the fixes, in the order Stage 4 lists

**Stage 5: faster search**
- [ ] A test run with filtering switched off, to compare against
- [ ] The fixes in the review's order, then fuzzy search (fork-only)

**Stage 6: the mod lists**
- [x] 6.1: draw mod names about 10 times cheaper (`2858a235`, `7fa83473`)
- [x] 6.1: selection fix (`309b81ef`, `396d7b2f`)
- [ ] 6.1: the same treatment for the other four text columns
- [ ] 6.1: scrolling that glides instead of jumping a row at a time (`ScrollUnit="Pixel"`)
- [ ] 6.1: remove the temporary measuring code, untangle the selection fix from it, offer to the original author
- [ ] 6.2: show overrides in the load order (fork-only)
- [ ] 6.3: categories and dividers (fork-only)

**Stage L: mod library (proposed)**
- [ ] Decide whether to add it
- [ ] Build it after 6.2

**Stages 7 to 9**
- [ ] 7: visual refresh of the rest of the app
- [ ] 8: export several load orders in one zip
- [ ] 9: downloader, syncing with friends, native mods (each needs its own spec first)

## Contents

1. [Goal and scope](#goal)
2. [Decisions taken](#decisions)
3. [Git strategy](#git)
4. [Codebase review (2026-11) and re-check](#review)
5. [Stage 0 — Toolchain & first build](#stage0)
6. [Stage 1 — Fork identity & safety](#stage1)
7. [Stage F: safety fixes from the review](#stageF)
8. [Stage 2 — Zip export corruption](#stage2)
9. [Stage 3 — Archive import losses](#stage3)
10. [Stage 4 — Perf A: startup load](#stage4)
11. [Stage 5 — Perf B: filter & search](#stage5)
12. [Stage 6 — The list workstream](#stage6)
13. [Stage L: mod library and per-order deployment](#stageL)
14. [Stage 7 — UI/UX refresh](#stage7)
15. [Stage 8 — Multi-order export](#stage8)
16. [Stage 9 — Ambitious set](#stage9)
17. [Risks](#risks)
18. [Verification](#verify)

<a id="goal"></a>

## 1. Goal and scope

A fork of LaughingLeader's BG3 Mod Manager, primarily for the owner and friends, but published publicly. Four workstreams, in the owner's words:

- **Bugfixes** — zip export is inconsistent/corrupting; importing order and mods from an archive loses mods and/or the order. Since 2026-11: also the crash and order-loss bugs the review found ([Stage F](#stageF)).
- **Performance** — slow initial load with many mods, slow scrolling, slow filtering.
- **UI** — dated appearance, UX improvements.
- **Features** — multi-order export, categories/dividers, overrides folded into the load order; later, a downloader, order syncing with friends, and native-mod management. Added 2026-11-03: a mod library that keeps every version, pins versions per order and deploys by hardlink ([Stage L](#stageL)).

> **All repo facts referenced here are in `CLAUDE.md` at the repo root.** This plan does not repeat them; it cites section numbers (§2.2, §4, …).

<a id="decisions"></a>

## 2. Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Base branch | **upstream/master** (WPF) | The circleainn/BG3ModManager-Redux fork proves a modern UI and every wanted feature is achievable on this exact stack (WPF, .NET 8). `upstream/avalonia` is 944 commits ahead but unfinished, unreleased, 6 months stale, and on `Avalonia 12.0.0-preview1`. |
| Platforms | Windows now, Linux kept open | Steam Deck support was rated "nice, not a driver". Stages note where a future Avalonia port would invalidate work; toolkit-neutral choices preferred where free. |
| Upstream contribution | Bugfixes and perf only | Drives the whole ordering — see §3. |
| Stage F upstreaming (2026-11-23) | Not fix by fix; maybe all of Stage F together at the end | Undecided whether to offer at all; the branches stay upstream-clean so the option stays open. Ask again when Stage F is finished. |
| Categories implementation | **Not** via WPF grouping | See the landmine in Stage 6. `CollectionViewSource` grouping would set `IsGrouping=True`, firing an existing template trigger that disables `CanContentScroll` and kills virtualization. |
| Reuse of Redux code | General ideas only | Redux is MIT and reuse with attribution would be permitted; the owner chose a stricter stance. Honoured throughout. |

<a id="git"></a>

## 3. Git strategy

> **The governing rule: anything you might upstream must never be built on top of anything you won't.**
>
> A zip fix sitting on top of a UI overhaul is no longer cherry-pickable. This is the entire reason bugfixes and performance come before features.

### Branch shape

```
master                     ← tracks upstream/master, never committed to directly
 ├── fix/*    ┐
 ├── perf/*   ├─ cut from a clean master, self-contained, upstream-targetable
 └── chore/*  ┘
fork/main                  ← the fork's own line; merges master + fork-only work
 ├── feat/*                ← fork-only features, may depend on each other
 └── ui/*
```

Merge to the parent with `--no-ff` (owner's standing preference), one branch per issue. Upstreamable branches are cut from `master`, *not* from `fork/main`, so a PR to LaughingLeader contains only the fix.

### Two traps, both already verified

> **Never run `git config merge.ours.driver true`.** `.gitattributes` marks 21 files `merge=ours`, including `IgnoredMods.json` — the file upstream patches after every BG3 update. With no driver configured the attribute is inert and those files conflict normally, which is what you want. Define the driver and upstream's changes are discarded silently, with no conflict. See CLAUDE.md §8.2.

> **Merge, don't rebase, when syncing upstream.** Rebasing rewrites commits you have already pushed to `origin`. See CLAUDE.md §8.3.

<a id="review"></a>

## 4. Codebase review (2026-11) and re-check

**In short:** A cloud tool reviewed the code on 3 November and it was re-checked on this PC on 23 November. Most of it holds up. This section says what to trust and where everything is.

On 2026-11-03 a cloud session ran a read-only, multi-agent review of `master`, `perf/list-render`, `fix/publish-prebuild-quoting` and upstream `avalonia`, checked against `CLAUDE.md` and this plan. It lives on branch `kk/laughing-pascal-710otn` under `docs/review/` (start at `README.md`). Its budget ran out part-way. On 2026-11-23 seven local agents re-checked it against `master`; the result is `docs/review/verified/LOCAL-RECHECK-2026-11-23.md` on the same branch. Neither pass built or ran the app: every finding is traced from code and library sources, not reproduced.

| Part | How far to trust it |
|---|---|
| `verified/*` ledgers | Re-checked: 57 ledger claims, plus this plan's own E1-E5 and I1-I5 and two local toolchain facts. Of those 69: 46 confirmed, 18 partly right (corrections recorded), 4 refuted, 1 needs a runtime test. |
| `areas/LOAD, SVC, VM1, VM2, UI` | Never verified by the cloud. The re-check verified their 12 High findings (11 confirmed, 1 partly right); they feed [Stage F](#stageF). Their ~80 Medium and Low findings are still leads; titles are in the re-check report. |
| `stages/*`, `areas/AVA.md`, `areas/BLD.md` | Designs judged by panels, not decisions. Each is linked from its stage below. |
| `claude-md/` | Proposed CLAUDE.md corrections; the ones that held up were applied on 2026-11-23. |
| Missing | A review of the 6.1 branch, a Stage 2-3 fix spec and a roadmap v2 (this update stands in for the last). |

### Design index

| File (under `docs/review/`) | What | Stage |
|---|---|---|
| `stages/S1.md` | Fork identity: update URLs, CalVer versions, an `Update.xml` checksum, release CI on `windows-2022`, a pre-release checklist; decisions in §8 | [1](#stage1) |
| `stages/S4-final.md` | Startup: measure, loading off the UI thread, bounded parallelism, a facts cache, an R1 snapshot harness | [4](#stage4) |
| `stages/S5-final.md` | Filter: a probe first, collection-view filtering, drops mapped by item, throttle last; fork-only fuzzy search (prototype in `prototypes/fuzzy-search/`) | [5](#stage5) |
| `stages/S6-final.md` | Overrides block and dividers: flat rows, a versioned `Layout` member in the order JSON, the write-guard PR first | [6.2, 6.3](#stage6) |
| `stages/LIB-final.md` | Mod library, version pins, hardlink deployment | [L](#stageL) |
| `stages/S7.md`, `S8.md`, `S9.md` | UI refresh within AdonisUI; multi-order bundles and friend sync; Nexus, mod.io and native-mod skeletons | [7](#stage7), [8](#stage8), [9](#stage9) |
| `areas/AVA.md` | Upstream avalonia: it carries most of E1-E6 and I1-I5 too. Advice: stay on WPF, test under Proton, later a cross-platform Core plus a CLI | [Decisions](#decisions) |
| `areas/BLD.md` | Dependency audit, the .NET 10 migration, CI YAML, line-ending policy | [0](#stage0) |

### What changed in this plan

- New [Stage F](#stageF): confirmed crash, order-loss, settings and security bugs; accepted 2026-11-23, before Stage 2.
- [Stage 0](#stage0): .NET 8 out of support, an SDK mismatch, three more build traps; the 58 `MeasureText` exceptions are not caused by `=` dividers.
- [Stage 2](#stage2): E1 to E4 narrowed or corrected, E6 dropped, four new export defects.
- [Stage 3](#stage3): I1 refuted; the real silent order loss is importing a "Current" order; five new import defects.
- [Stage 4](#stage4): loading runs on the UI thread; about 6.5 s of the 9.27 s is unattributed; no MD5 anywhere.
- [Stage 5](#stage5): steps 1 and 2 save nothing; filtered drops are safe, column-sorted drops are not.
- [Stage 6](#stage6): the 6.1 branch is pushed; 6.2 and 6.3 get the S6-final design.
- New [Stage L](#stageL); four new [risks](#risks); revised [verification](#verify).

<a id="stage0"></a>

## 5. Stage 0 — Toolchain & first build

**In short:** Getting the app to build on this PC. Done. Two problems that only affect a fresh download of the code are still open, and .NET 8 is now out of support.

> **Status 2026-09-23 — complete, ready to verify.** The toolchain is installed and proven. `Publish` builds twice in a row from a clean `bin/`. The app was built in Debug, launched against the real 1 097-pak mod folder, and the Stage 0.3 baseline is recorded below.
>
> **Read 0.2 before trusting anything previously written about the pre-build event.** That bug has now been mis-diagnosed twice — once in the original survey, once in the first correction. The measured mechanism is in 0.2 and it is not what either said.
>
> Two blockers were found that no earlier document mentions: **0.2b**, which stops a clean checkout from producing a release at all, and the **filter-latency finding** in 0.3, which changes the premise of Stage 5.

> **Update 2026-11-23 (review BLD-01 to BLD-08, re-checked on this machine).**
>
> - **.NET 8 and .NET 9 reached end of support on 2026-11-10.** Nothing breaks, but no more security fixes (the final .NET 8 patch is 8.0.34; this machine runs desktop runtime 8.0.31). Upstream `avalonia` already targets `net10.0`.
> - **This machine cannot target `net10.0` yet.** Its only Visual Studio instance is Build Tools 2022 17.14, and the 10.0.4xx SDK needs MSBuild 18 (Build Tools 2026 / VS 18). Installing that is step one of any .NET 10 move.
> - **Restore and build use different SDKs.** `dotnet restore` resolves 10.0.400; `msbuild` 17.14 cannot load it and falls back to 9.0.318. Both target net8, so nothing breaks, but analyzers and NuGet differ between the steps. A `global.json` would pin one SDK.
> - `Publish|Any CPU` and `PublishTest|Any CPU` build LSLib, LSLibNative and LSLibStats in **Debug**, so a release could ship a debug-CRT native DLL. Always pass `-p:Platform=x64` (BLD-04).
> - `LSLibStats` is built in every configuration, used by nothing, and carries a second copy of the 0.2a parser trap (BLD-05).
> - The release zip is built from an uncleaned `bin\Publish`, with error-swallowing deletes and no check of its contents (BLD-08).
> - `LSLibNative` (C++/CLI) is what blocks `dotnet build`, a cheap CI and Linux; upstream `avalonia` replaced it with managed code (BLD-06).

### 0.1 Install the toolchain `DONE`

| Component | State | Evidence |
|---|---|---|
| **VS Build Tools 2022** + Desktop development with C++ | installed | 17.14.41, MSVC v143 `14.44.35207`. |
| **C++/CLI support** (`/clr:netcore`) | installed & proven | `Microsoft.VisualStudio.Component.VC.CLI.Support` now registers, and `LSLibNative.dll` builds. |
| **Windows SDK** | installed | Windows 11 SDK `10.0.26100.0`. The earlier claim that `Windows Kits\10\Include` does not exist was wrong. |
| **.NET 8 SDK / Desktop Runtime** | installed | `8.0.425`; `Microsoft.WindowsDesktop.App 8.0.31`. |
| **.NET SDK component for Build Tools**<br>`Microsoft.NetCore.Component.SDK` | installed & proven | This was the one real gap. Verified functionally, not just by catalog: evaluating `Toolbox.csproj` with `-getProperty` resolves `Microsoft.NET.Sdk` under **both** amd64 and x86 MSBuild. The amd64 `SdkResolvers` folder is still empty and `updateDate` never moved — both are harmless. |
| **GPLex 1.2.2 + GPPG 1.5.2**<br>`External/lslib/External/gppg/binaries/` | installed correctly | Byte-identical to the official distributions, both execute, both ran inside a real build. Location matches `LSLib.csproj:67-68`, which is authoritative over lslib's README. |
| **`gh` CLI** | authenticated | `SpasticPengu`; scopes `gist, read:org, repo, workflow`. |

No environment-variable workarounds are needed or in use. `MSBuildSDKsPath`, `MSBuildEnableWorkloadResolver` and `DOTNET_HOST_PATH` are all unset, and builds are clean without them.

### 0.2 Fix the pre-build event `UPSTREAMABLE` `fix/publish-prebuild-quoting · 5f8a80a9`

> **Both earlier diagnoses were wrong.** The original survey said the build fails with `MSB3073` (exit 2 / exit 3). The first correction said the build succeeds but `_Lib` is silently left stale because the unquoted path splits on the space in `…\Git Repositories\…`. **Neither happens.** The pre-build event works correctly today, on this machine, space and all.

The mechanism, read out of a real build log rather than a harness:

```
<PreBuildEvent>rd /s /q $(TargetDir)_Lib</PreBuildEvent>      <!-- GUI.csproj:82 -->

MSBuild log:   8>PreBuildEvent:
                    rd /s /q _Lib
```

**Two mistakes that cancel out:**

1. `$(TargetDir)` expands to **nothing**. The property group sits above the implicit `Microsoft.NET.Sdk` targets import, and `TargetDir` is not defined until that import is evaluated. So no path is ever interpolated — which is why the space in the repo path is irrelevant.
2. The command is therefore the relative `rd /s /q _Lib`, and `Microsoft.Common.CurrentVersion.targets:1426` runs it as `<Exec WorkingDirectory="$(OutDir)" …/>`. `_Lib` resolves against `bin\Publish\` — exactly the right folder.

Confirmed by planting marker files in both candidate locations and building:

| Marker | After a Publish build with the original command |
|---|---|
| `bin\Publish\_Lib\…` | **deleted** — the command works |
| `src\GUI\_Lib\…` | untouched — the working directory is not the project dir |

The only genuine defect is cosmetic: on the first `Publish` after a clean, `_Lib` does not exist and `rd` writes "The system cannot find the file specified." to stderr. `cmd` still exits 0, so the build is unaffected.

> **Why change it at all, then?** Because it is correct only by accident. Anything that makes `TargetDir` resolve — moving the property group below the SDK import, or a future SDK defining it earlier — converts it into an *unquoted absolute path*, and the space-splitting bug the original survey predicted becomes real. The fix removes the accident and the stderr noise together.

**The committed fix** uses `$(OutputPath)`, which this project sets at `GUI.csproj:25` — above the property group, so it *is* defined at that point in evaluation:

```
<PreBuildEvent>if exist "$(OutputPath)_Lib" rd /s /q "$(OutputPath)_Lib"</PreBuildEvent>
```

which now evaluates to a real, quoted, absolute path:

```
if exist "C:\Users\…\Git Repositories\BG3ModManager\\bin\Publish\_Lib" rd /s /q "…"
```

**Proven end-to-end:**

| Check | Result |
|---|---|
| `Publish` from a clean `bin/`, run 1 | exit 0, 0 errors |
| … spurious stderr from `rd` | 0 lines (was 2) |
| `Publish` run 2, immediately after | exit 0, 0 errors |
| File planted in `_Lib` between the runs | **deleted** by run 2 |
| `_Lib` contents, run 1 vs run 2 | identical, 47 files |
| `BG3ModManager_Latest.zip` | contains the exe, not the planted file |

A more idiomatic fix would drop the shell entirely and use `<RemoveDir Directories="$(OutputPath)_Lib" />` in a target, which resolves at execution time and cannot be broken by evaluation order. Kept as a one-liner here to stay minimal for an upstream PR.

### 0.2a LSLib parser bootstrap `UPSTREAMABLE` `fix/lslib-generated-parser-glob (not cut)`

On a clean checkout the first build fails with `CS0534` / `CS0246`: `LSLib.csproj` generates `Goal.lex.cs`, `Goal.yy.cs`, `StoryHeader.lex.cs` and `StoryHeader.yy.cs` in a build target, but the default `**/*.cs` glob is expanded during project **evaluation**, before any target runs. Building twice works; the `Exec`s are guarded by `Condition="!Exists(…)"`, so nothing is regenerated.

**Not re-triggered in this run** — the generated files already existed from earlier attempts, so every build here was effectively a "second" build. The trap stands as previously observed, but still wants confirmation against a genuinely fresh clone before the branch is cut.

**Candidate fix:** add the four generated paths as explicit `<Compile Include>` items, or have the generating target add them to `@(Compile)`. It lives in the `lslib` submodule (LaughingLeader's fork), so it is a separate PR to a separate repo.

### 0.2b CrossSpeak blocks every non-Debug build `UPSTREAMABLE` `not yet cut · CrossSpeak submodule`

> **New, and the one that actually stopped the first build.** On a clean checkout, `Publish`, `PublishTest`, `Release` and `Editor Debug` all fail. Only `Debug` works.

```
NuGet.Build.Tasks.Pack.targets(221,5): error : Could not find a part of the path
  '…\External\CrossSpeak\CrossSpeak\bin\Debug\netstandard2.0'.
```

`CrossSpeak.csproj` sets `GeneratePackageOnBuild=True` and points at a hand-written `CrossSpeak.nuspec` whose file list hardcodes the **Debug** output — with the Release line sitting right below it, commented out:

```
<file src="bin\Debug\netstandard2.0\CrossSpeak.dll" target="lib\netstandard2.0" />
<!--<file src="bin\Release\netstandard2.0\CrossSpeak.dll" … />-->
```

The solution maps `Publish|x64` → `Release|Any CPU` for CrossSpeak, so the build writes `bin\Release\…` and pack then looks in `bin\Debug\…`, which on a fresh clone does not exist.

**Why nobody upstream hit it:** on a machine that has built `Debug` even once, `bin\Debug\netstandard2.0\` exists and pack silently succeeds — picking up a stale DLL. Confirmed here: the Publish build failed on a clean tree, a Debug build created that folder, and every Publish afterwards succeeded. Same family as 0.2a — works on the maintainer's machine, fails on a clean checkout or in CI.

**Workaround in use:** build `Debug` once before any `Publish`. **Candidate fix:** drop `GeneratePackageOnBuild` (nothing in this solution consumes the package — Core references the project directly), or make the nuspec configuration-agnostic via `$configuration$` and `NuspecProperties`. Lives in the CrossSpeak submodule, so it is a third separate repo.

### 0.3 Baseline the app `DONE`

Debug build, logging enabled via the extension-less `debug` file next to the exe, launched against the real mod folder. **These are Debug-build numbers** — unoptimized IL, so treat them as the control for relative improvement, not as release performance.

| Workload | Value |
|---|---|
| Paks in `%LOCALAPPDATA%\…\Mods` | 1 097 (27 GB) |
| Paks in the game `Data` folder | 48 (GOG install) |
| **Mods loaded** | **1 121** |
| Profiles | 2 (active: `Public`) |
| Entries in the active profile order | 935 |
| `modsettings.lsx` size | 548 KB |

**Cold start** (wall clock from process start, log markers timestamped by polling):

| Milestone | t | Δ |
|---|---|---|
| First window (splash) | 0.50 s | — |
| Log file created | 1.09 s | 0.59 s |
| `RefreshAsync` starts | 3.61 s | 2.52 s — init, settings, resources |
| Mods finished loading | 12.88 s | **9.27 s — the dominant cost** |
| Profiles loaded | 13.35 s | 0.47 s |
| Lists built, refresh done | 14.61 s | 1.26 s |
| UI responsive | **~15.2 s** | 0.63 s |

At that point: **478 MB** private / 559 MB working set, 24.2 s CPU, 59 threads, 1 055 handles. After interaction, 480 MB / 594 MB.

> **Filter latency — this changes Stage 5's premise.** The plan assumed the 500 ms throttle "alone may be what feels slow". It is not. Measured by driving the real search boxes through UI Automation and sampling CPU until the process went idle:
>
> | Action | Settled at | Work after the 500 ms throttle |
> |---|---|---|
> | Inactive list, broad match (`a`) | 3.77 s | **3.27 s** |
> | Inactive list, cleared | 1.87 s | 1.37 s |
> | Inactive list, no match (`zzzzzz`) | 1.74 s | 1.24 s |
> | Active list, broad match (`e`) | 1.89 s | 1.39 s |
> | Active list, cleared | 1.84 s | 1.34 s |
>
> Even a filter that matches *nothing* costs ~1.2 s of work. Removing or shortening the throttle would therefore win at most a fifth of the delay. The cost is in `OnFilterTextChanged` walking every mod and setting `Visibility` per item — which also means the virtualizing panel still has to realize and collapse containers rather than simply not having the items. Stage 5 should target that, not the throttle.

> **Scroll — reported by Konstantinos, 2026-09-23:** *"not stuttery but not as smooth as I'd like"*, on the **inactive** list. The lists expose no `ScrollPattern`, so this could not be automated; it is his judgement, and it is the Stage 6 target.
>
> **Two things make that verdict sharper than it looks.** First, the inactive list is the *small* one — roughly 186 rows against the active list's 935. The list most likely to feel bad has not been tried yet. Second, "not stuttery" rules out dropped frames, and virtualization is confirmed on (`IsVirtualizing="True"`, `VirtualizationMode="Recycling"`, `HorizontalModLayout.xaml:119,223`) — so this is not a throughput problem.
>
> **Likely cause, and it is one line per list.** `VirtualizingPanel.ScrollUnit` is **not set anywhere in the codebase** (grepped across all `.xaml` and `.cs`), so WPF's default `ScrollUnit="Item"` applies: the list scrolls a whole row at a time rather than by pixels. Smooth frames, but the content steps instead of gliding — which is exactly "not stuttery, not smooth".

> **58 handled exceptions during a single load.** `ElementHelper.MeasureText` throws `KeyNotFoundException` out of WPF's font cache 58 times while the mod list column autosizer measures mod names (`HorizontalModLayout.xaml.cs:846` and `:910`). They are caught and logged, so nothing breaks, but throwing 58 exceptions on the UI path is not free and the names that trigger it — divider entries made of `=` characters, and non-Latin titles — are ordinary in a 900-mod order. Candidate work for Stage 6.
>
> **Corrected 2026-11-23 (CLAIM-06):** `=` does not throw. The misses come from characters the font lacks (non-Latin scripts, emoji) in the longest names, which are the ones the autosizer measures.

<a id="stage1"></a>

## 6. Stage 1 — Fork identity & safety

**In short:** Make the app know it is this fork, so it never updates itself back to the original author's version. Needed before sharing a build.

### Repoint the updater `FORK-ONLY` `chore/fork-identity`

> Because you intend to publish, this is **required, not optional**. Unchanged, a fork build reads upstream's `Update.xml`, offers upstream's release to your users, and overwrites your install with LaughingLeader's.

- `src/Core/DivinityApp.cs:19-30` — `URL_UPDATE` and `URL_CHANGELOG_RAW` are the live ones. `GITHUB_USER`/`GITHUB_REPO`/`GITHUB_RELEASE_ASSET` are declared but never referenced — changing them alone does nothing.
- Root `Update.xml` — version and download URL.
- `README.md` — the "only official source" wording is upstream's and becomes false in a fork. Add a fork notice; keep the MIT notice and © LaughingLeader; keep credit to Norbyte for LSLib.
- Decide on the ko-fi links (upstream's donation buttons).

**Verify:** with a deliberately low `AssemblyVersion`, the updater offers *your* release, not upstream's.

> **Review design: `docs/review/stages/S1.md`.** CalVer `YYYY.M.D.N` versions with a bump script and a CI guard over the four version files, the update feed published as a release asset, an `Update.xml` checksum, a release workflow on `windows-2022` for `fork/main`, and a pre-release checklist. Its §8 lists the decisions it needs from you.
>
> **Confirmed updater bugs** (fixed in [Stage F](#stageF), upstreamable): a failed update check reports "up to date", and cancelling or failing the download closes the app through an unconditional `Environment.Exit(0)` (CLAIM-16, HARV-01, HARV-02). The feed is trusted with no checksum (HARV-04, low).

<a id="stageF"></a>

## 7. Stage F: safety fixes from the review

**In short:** Bugs the review found that can lose load orders, crash the app or reset settings. Each fix is small.

`UPSTREAMABLE` `one fix/* branch per row, each cut from clean master`

> **Accepted 2026-11-23; work order is the Stage F checklist at the top.** Every bug below is in upstream `master` today. Each was confirmed by reading the code, none has been reproduced, and most are a few lines to fix. Several lose load orders silently, which is why this stage goes before Stage 2. **Back up `modsettings.lsx` and the `Orders` folder before testing any of them**, and start each branch with a Windows repro or an instrumented PASS/FAIL check.

| Branch (proposed) | Bug, as a user sees it | Where (`master`) | Fix sketch |
|---|---|---|---|
| `fix/dependency-cycle` | Save or Export closes the app with no message when an active mod lists itself as a dependency, or two mods list each other (stack overflow). On by default through `AutoAddDependenciesWhenExporting`. LOAD-02, VM2-05. | `DivinityModDataLoader.cs:1400-1429`: `GetDependencyMods` recurses before marking a mod as added. | Mark visited before recursing. Worth a one-off scan of your 1,121 mods to see whether any trigger it. |
| `fix/hotkey-validation` | A hotkey rebound to a plain letter or digit works until restart, then every launch crashes ("The program will close") until `keybindings.json` is fixed by hand. UI-01. | `HotkeyEditorControl.xaml.cs:64-121` accepts it; `MainViewControl.xaml.cs:37` builds a `KeyBinding`, whose `KeyGesture` rejects it at the next start. | Reject invalid gestures in the editor; on load, skip and report them instead of throwing. |
| `fix/order-keep-missing` | Saving an order while some of its mods are missing deletes those mods from the saved order. VM2-01. | `MainWindowViewModel.cs:4880-4889` rebuilds `Order` from the loaded mods 250 ms after any list change (`:5400-5408`) and on every Save (`:2601`) and Export (`:2899`). | Keep missing entries in place when rebuilding. 6.2, 6.3 and Stage L need this too: any per-entry field is wiped the same way (CLAIM-09). |
| `fix/export-to-game` | Export overwrites `modsettings.lsx` with no backup, then overwrites the LastExported backup even when the write failed (VM2-04). On a fresh setup the first Save or Export of Current switches to another saved order and copies it into Current (VM2-02), and can leave the app locked until restart (VM2-03). | `DivinityModDataLoader.cs:1331-1364`; `MainWindowViewModel.cs:2841-2985`, `BackupCurrentLoadOrderAsync` at `:2850-2873`. | One rewrite: run on the UI thread, write through a temp file and rename while keeping a `.bak`, back up LastExported only after success, and never route the backup through `AddNewModOrder`. |
| `fix/restore-prompt` | After a game patch empties the list, answering Yes to "use the last exported order?" can empty the LastExported backup and write a `modsettings.lsx` holding only the campaign. VM1-01. All profiles also share one LastExported (HARV-54). | `MainWindowViewModel.cs:2291-2327` copies into the first saved order, which can be LastExported itself; `SetOrder` on itself clears it (`DivinityLoadOrder.cs:289-293`). | Restore into Current; never `SetOrder` an order into itself; one backup per profile. |
| `fix/modsettings-write-guard` | A meta-less override pak (its UUID is its file path) can be written into `modsettings.lsx`; the next read throws and the whole order comes back empty (CLAIM-15, HARV-52). One unparseable entry blanks the file the same way (HARV-53). | `BuildOutputList`, `GenerateModSettingsFile`, `LoadModSettingsFileAsync` in `DivinityModDataLoader.cs`. | Never write a non-GUID UUID; read back after writing; skip a bad entry on read instead of failing the file. This is S6-final's PR-0 and a hard prerequisite of Stage L. |
| `fix/settings-restore` | Window size and position, and the admin-warning opt-out, reset on every launch. CLAIM-10, caused by upstream `7b57e5a`. | `MainWindowViewModel.cs:1127` restores only `[Reactive]` properties; `Window` and `Confirmations` (`DivinityModManagerSettings.cs:112, :122`) have none, and `SaveSettings` then writes the defaults back. | Copy both objects' fields in place after `:1127`. Never reassign them (CLAUDE.md §4). |
| `fix/background-errors` | Any exception inside an `RxApp.TaskpoolScheduler.ScheduleAsync` body is swallowed; one in `RefreshAsync` leaves "Loading..." up with every button disabled until restart. VM1-02. | System.Reactive 6.0.1's `ScheduleAsync`; 15 sites in `MainWindowViewModel.cs`. | A small helper that catches, calls `ShowAlert`, and resets `IsRefreshing` / `IsLoadingOrder` in a `finally`. |
| `fix/updater-errors` | A failed update check says "up to date"; cancelling or failing the update download closes the app. CLAIM-16, HARV-01, HARV-02. | `AppUpdateWindowViewModel.cs`; `TryRunUpdate` calls `Environment.Exit(0)` whatever happened. | Report the failure; exit only after a successful download. |
| `fix/path-containment` | **Security, needs a crafted mod.** A compressed pak import (`.bz2/.xz/.zst`) names its output from the mod's own `meta.lsx` Folder, so it can truncate or replace `.pak` files outside Mods (SVC-01). Extract Selected Mods writes pak entry names with no containment check, so `..` escapes the chosen folder (VM2-06). | `MainWindowViewModel.cs:3294-3301`; extraction in `External/lslib/LSLib/LS/PackageCommon.cs:162` (submodule). | `Path.GetFileName` on the derived name; a containment check before each extracted write (the lslib half is a submodule PR). |
| `fix/duplicate-dialog-larian` | If a mod reuses a Larian module's UUID, the startup duplicate dialog can tick the game's own `Game.pak` or `Gustav.pak` for deletion; confirming recycles it where the folder is writable (usually Steam, usually not GOG under Program Files). LOAD-01: offering Data-folder copies is intended, offering Larian paks is the bug. | `MainWindowViewModel.cs:1694-1709`. | Exclude Larian and multi-module Data paks from that rule. |

<a id="stageFprogress"></a>

#### Stage F progress (23 November)

**In short:** seven fixes are done; the first five are also checked in the real app. Next:
`fix/modsettings-write-guard`.

| Branch | Commit | What changed | Checked by |
|---|---|---|---|
| `fix/settings-restore` | `f4d3d737` | `Window`/`Confirmations` copied in place after `SetFrom` (`WindowSettings.SetFrom`). Also `ApplyWindowPosition` keeps negative coordinates when the title bar is on a connected screen (else default position), and `UpdateWindowSettings` skips a minimized window (-32000). | 13 PASS: master loses both values, the fix keeps them across two launches, same instances, null objects in the file; the on-screen test. |
| `fix/dependency-cycle` | `9f854767` | `GetDependencyMods` marks a dependency before recursing. **New finding:** master also dropped dependencies of dependencies (every recursive result was already in `addedMods`, so the caller never added it), losing even active mods for an order M, D, E with M→D→E. Adventure added once. | 13 cases (master: 5 stack overflows, 2 drops). Scan of 1,132 installed mods: no self or loop dependencies; 45 mods have depth-2 dependencies; over the 16 saved orders the fix only adds 1 missing dependency in 3 orders. |
| `fix/order-keep-missing` | `bb07d2bf`, `78924a47` | `LoadModOrder` sets `entry.Missing`; `DivinityLoadOrder.UpdateFrom(activeMods, isInstalled)` (one linear pass, swapped in at the end) puts Missing entries back after the entry they followed, and clears the flag for a mod installed since. After Export, Current, the profile's list and LastExported mirror only non-Missing entries (what was written). Save As still drops missing mods on purpose; Clear Missing Mods still removes them; Export's missing-mods notice now lists them. | 32 PASS: 16 cases + 1,000-entry timing (0.45 ms; a first O(n²) draft took 19 ms) + the 13 saved orders holding missing mods, all kept in order. App run: a saved order kept its not-installed mod. |
| `fix/background-errors` | `7f19c08c`, `11016f41` | `ReactiveExtensions.ScheduleAsyncSafe`; every `ScheduleAsync` in `src` now goes through it (15 `RunInBackground` jobs, Save, the SE manifest check): log, `ShowAlert`, then cleanup of only what the job set (`ResetBusyState` for refreshes; `IsRefreshing` + overlay for ImportMods; `IsLoadingOrder` for Export to game and Save, since the first export builds LastExported through `AddNewModOrder` → `BuildModOrderList`; `OnMainProgressComplete` for zip, extract and the SE download, guarded there by `progressDone`). | 11 PASS (task pool, current-thread and event-loop schedulers): master's error reaches no handler (only `UnobservedTaskException` after a forced GC); errors before and after an await are reported, cancellation is not, the 2 s delay and disposal still work. |
| `fix/hotkey-validation` | `c8c21eed`, `2cc1c6c3` | `Hotkey.IsValidGesture` (asks `KeyGesture` itself); the editor refuses and says the modifier must still be held when the key is released; `LoadKeybindings` skips saved invalid ones and names them (`AppKeys` now sets `DisplayName` from `[MenuSettings]` at construction). | 17 PASS: the startup `KeyBinding` throws for a plain L; `IsValidGesture` agrees with it on 16 pairs. App run: started with a saved plain L, warned, dropped it (the run showed the then-blank name, fixed in `2cc1c6c3`). |
| `fix/export-to-game` | `1ff34c38` | `DivinityFileUtils.WriteFileSafelyAsync`: a uniquely named temp file next to the target, flushed, an optional check on it, the old file copied to a backup path, then one rename (a symbolic link is written through). `ExportModSettingsToFileAsync` uses it with backup `modsettings.lsx.bg3mm.bak` (the owner keeps a hand-made 934-mod `modsettings.lsx.bak` in `PlayerProfiles\Public`) and a read-back check: every non-ignored mod written must parse back with the app's own reader, so a path UUID (CLAIM-15) or a Version64 of 2^63 or more (LOAD-06) now fails the export and keeps the old file. `ExportLoadOrderToFileAsync` (Save of a saved order, LastExported) writes through it too: HARV-62 for those paths, Save As still writes in place. `ExportLoadOrder` runs on the UI thread. LastExported is replaced only after a successful write; a new entry goes in at index 1, where `BuildModOrderList` puts it, with the selection kept, never through `AddNewModOrder`. The failure message names the likely causes. | 25 PASS: master's in-place write, its empty file after a failed write, its unreadable file for a path UUID and for version 2^63, and its selection jump are reproduced; the fix keeps the target on a locked or read-only file and on a refused or throwing check, keeps `.bg3mm.bak`, leaves the user's own `.bak` alone, and survives 8 overlapping writes on one UI thread; `BackupCurrentLoadOrderAsync`, run on the real view model, inserts or updates LastExported with the selection kept. Not run: the symbolic-link case (needs Developer Mode). Review: 1 major (a "never back up an empty order" rule would make the startup prompt nag a user who exports an empty list on purpose; dropped), 1 minor (insert position), 2 nits (shared temp name, symlink); all fixed in the commit. |
| `fix/restore-prompt` | `da623aaf` | `DivinityLoadOrder.SetOrder` copies before clearing (alias-safe; the order overload delegates to the list one). `CheckForEmptyOrderAsync` writes into no saved order: the profile's mods are cleared and refilled from LastExported, `modsettings.lsx` is written from it, an alert says when that write fails (the game does not have the list yet), then `BuildModOrderList(0)` shows Current. HARV-54 (one LastExported for all profiles) is left for later: it needs a profile set without "Public", and its fix touches `BackupCurrentLoadOrderAsync`. | 9 PASS: master's restore reproduced emptying LastExported, and, on the owner's 16 saved orders, overwriting "5 Player - Hard UW" (73 mods) with LastExported's 29; the fixed `SetOrder` keeps itself, keeps its own list, applies a filter over it; an IL scan of the built method finds no `SetOrder` and a profile `Clear`. Not run: the prompt flow itself (modal dialog). Review: ship with nits (the alert wording, done). |

**How the checks run.** A throwaway console program references the built `bin\Debug` DLLs and logs PASS/FAIL;
one `<Name>Check.cs` per branch, built alone because each branch only has its own fix. It is kept outside the
repo in the session notes folder, `...\side-session-notes\local_16579313-3951-4f39-bb69-06578dd6828c\stageF-harness\`
(`run-check.ps1 <Name>` builds and runs it), with `splice.py` for byte-exact edits of mixed-ending files
(see `tasks/lessons.md`) and `sync-notes.sh` for the `notes` branch. `modsettings.lsx` was backed up to
`...\backups\2026-11-23\` in the same folder (hash-checked).

**Fixes 6 onward** were done in a second side session, in this checkout, one branch at a time. Their checks
(`ExportToGameCheck.cs`, `RestorePromptCheck.cs`, ...) are in the same `stageF-harness\harness` folder and run
against `bin\Debug` after `stageF-harness\build-debug.ps1` (a Debug build of the .sln that prints only errors).
`splice2.py` there handles snippets whose line endings change inside one function (`DivinityModDataLoader.cs`
has some). Before each fix, six read-only agents traced all six remaining bugs on master and designed the
fixes; after each commit, one independent read-only reviewer tried to break it, and its findings were fixed
in the same commit. `modsettings.lsx` and the 17 order files were backed up (hash-checked) to
`...\side-session-notes\local_e41f79c8-ab41-48e5-b759-9212d1b0b757\backups\2026-11-23-2035\`; no check writes
outside a temp folder.

**Real app run (23 November, with the owner's OK).** A throwaway worktree with all five fixes (before the
follow-ups) was built and launched with seeded settings, a plain-L shortcut and a test order holding a
not-installed mod; `stageF-harness-v2\stagef-apprun.ps1` drives it with UI Automation only (no mouse or
keyboard). 17 PASS: the window opened at the saved 300,120 1500x900; position and admin-warning choice survived
startup and close; the L shortcut was dropped with a warning; Save kept the missing mod in place;
`modsettings.lsx` and the game's Script Extender files were unchanged. The app answers no UI Automation while it
loads mods (loading runs on its UI thread, CLAIM-07), so the script finds the window through the process.

**Independent review (workflow, 5 reviewers + 2 skeptics).** 9 findings: 5 confirmed, 3 contested, 1 refuted;
all confirmed and contested ones are fixed in the follow-up commits above and were re-reviewed. Left on purpose:
ExportLoadOrderAsync still rebuilds the order on a pool thread while the 250 ms rebuild can run on the UI thread
(an old race, removed when `fix/export-to-game` moves export to the UI thread; until then fix 3's new list reads
in that path can throw in the same tiny window); and inside `SaveLoadOrderAsync` an Export failure is caught by
its own try, so it cannot clear `IsLoadingOrder` (also `fix/export-to-game`'s ground). Checks for the follow-ups:
`stageF-harness-v2` (`run-check.ps1 <Name> [-Bin <worktree>\bin\Debug]`, one obj folder per check).

**Facts for the next fixes.** The owner's real install is `Documents\BG3 Modding\BG3 Mod Manager`
(1.0.12.9, 16 saved orders, Save Window Location on, auto-add dependencies on). The active `Public` order
is now 26 entries (was 935 in September). Reading the 1,132 paks with the app's loader takes 66 to 79 s cold.

#### Lower priority, same family (confirmed)

- Drops into a column-sorted active list land at the sorted position, not the load-order one (HARV-71).
- Duplicates with equal versions get an arbitrary winner, and the losers are pre-ticked for deletion (HARV-63).
- Saved order JSON files are rewritten in place, so a crash mid-write empties them (HARV-62).
- Menu shortcut labels go stale after a rebind (HARV-08). Tab is captured by the `SwapListFocus` hotkey in the main window; Shift+Tab still works (HARV-10, needs a runtime test).
- The Script Extender updater writes `DWrite.dll` in place, so a cancel or a decompression failure can truncate it (HARV-47).
- The screen-reader probe runs 3-4 times per mod during load (CLAIM-08; cost unmeasured, probably small).

#### Unverified leads worth a look first

From the area reports' Medium findings, not yet checked: the `modsettings.lsx` watcher is disabled for good after the first Save of Current (SVC-04); saving Script Extender updater settings runs Toolbox on the UI thread for up to 120 s (SVC-08, VM1-03); a missing or unreadable `settings.json` makes startup overwrite `keybindings.json` with `{}` (VM1-05); Import Order from `.json` creates a nameless order (VM2-09). The full list is in the re-check report.

Import and zip-export bugs stay in [Stage 2](#stage2) and [Stage 3](#stage3).

<a id="stage2"></a>

## 8. Stage 2 — Zip export corruption

**In short:** Exporting a load order to a zip can produce a broken or incomplete zip. The review narrowed down when that happens and found a few more export bugs.

`UPSTREAMABLE` `fix/zip-export-corruption` — all in `src/GUI/ViewModels/MainWindowViewModel.cs`, i.e. `src/GUI`, so a future Avalonia port would carry the logic but not the file.

> **Review update 2026-11-23.** E1 to E6 were re-checked against the code:
>
> | Defect | Verdict | Correction |
> |---|---|---|
> | E1 | Real, narrower | Only *Export As* over an existing, larger zip hits it. A plain Export never does, so it explains "sometimes corrupt" only for that path. |
> | E2 | Real, overstated | The catch is at `:3679-3683`. "Very likely the real cause" of missing mods is unproven: scanned paks rarely fail to open. Other confirmed causes: split paks are never exported (HARV-25) and Stage 3's import losses. |
> | E3 | Real, different | A cancel between mods leaves a valid but incomplete zip and no message. A cancel during a write races the abandoned writer against `ZipWriter.Dispose` and corrupts the zip (AUDIT-PLAN-03). |
> | E4 | Real, smaller | Lines `:3670-3691`; it is one pool thread spinning. The proposed `Task.Run` fix alone does not stop a write already in flight: cancellation must wait for it (or abort it) before the `ZipWriter` is disposed, or the E3 race remains. |
> | E5 | Confirmed | As written. |
> | E6 | Moot | The infinite value is never used (CLAIM-02). Dropped. |
>
> **New export defects (confirmed):** the planned "collect failures and continue" fix would still ship a truncated pak when a read fails mid-copy, because SharpCompress finalises the partial entry as valid (AUDIT-PLAN-05); split multi-part paks (`Foo_1.pak`, ...) are never exported (HARV-25); the JSON text export writes the full Windows path, user name included, of meta-less override paks (HARV-26). **Seen, not verified:** `canStartExport` is always true, so a second export can start while one runs (`:5006`); there is no zip64, so a pak of 4 GiB or more breaks the archive.

```text
Collect mods  -->  File.OpenWrite  -->  write order.json  -->  WriteZipAsync  -->  "Exported!"
(order +           [E1: no                                     [E2: swallows       (always reports
 overrides)         truncate]                                   all errors]         success)

Why the zip is "sometimes" corrupt:
  E1: overwriting a LARGER existing zip leaves the old tail behind -> invalid archive.
  E3: cancelling mid-write returns from inside the using; the partial file is left on disk.
```

*Export path. Steps in [brackets] are defects located in the code, not hypotheses (see the review update
above for how E1 and E3 were narrowed).*

#### E1 — corrupt archive on overwrite (HIGH CONFIDENCE) (NARROWED 2026-11)

*Location:* `MainWindowViewModel.cs:3579`

```
using (var stream = File.OpenWrite(outputPath))
```

`File.OpenWrite` uses `FileMode.OpenOrCreate` — it **does not truncate**. Export over an existing zip that is larger than the new one and the old bytes remain past the end of the new archive. That is a corrupt zip, and it explains the "sometimes" perfectly: it depends entirely on the relative sizes.

**Fix:** `File.Create(outputPath)` (or `FileMode.Create`). Write to a `.tmp` beside the target and `File.Move(..., overwrite:true)` on success, so a failed export never replaces a good archive.

#### E2 — mods silently missing from the zip (HIGH CONFIDENCE) (REVISED 2026-11)

*Location:* `MainWindowViewModel.cs:3660-3670, inside WriteZipAsync`

```
catch (Exception)
{
    // ignored because an exception on a cancellation request
    // cannot be avoided if the stream gets disposed afterwards
}
```

Every failure is swallowed — a locked `.pak`, a missing file, an I/O error. The mod is omitted and the export still reports success. **This is very likely the real cause of the "import didn't get all my mods" complaint too**: the mods were never in the archive.

**Fix:** catch `OperationCanceledException` only; collect real failures into a result object and surface them via `ShowAlert` (CLAUDE.md §5 — note the log is off by default, so log-only reporting is invisible).

#### E3 — cancellation leaves a partial zip (HIGH CONFIDENCE) (REVISED 2026-11)

*Location:* `MainWindowViewModel.cs:3593`

`if (t.IsCancellationRequested) return false;` returns from inside the `using`. The stream disposes and a truncated zip is left on disk, never deleted. It also skips `Directory.Delete(tempDir)`, so temp folders accumulate.

**Fix:** the write-to-temp-then-move approach in E1 solves this too.

#### E4 — busy-wait during export (HIGH CONFIDENCE) (REVISED 2026-11)

*Location:* `MainWindowViewModel.cs:3675-3685`

```
var childTask = Task.Factory.StartNew(..., TaskCreationOptions.AttachedToParent);
var awaiter = childTask.GetAwaiter();
while (!awaiter.IsCompleted)
{
    await Task.Delay(0, token);
}
```

Two faults. `Task.Delay(0)` completes immediately, so this is a spin loop hammering the thread pool for the entire duration of the write. And `AttachedToParent` is a no-op here because `Task.Run` creates its task with `DenyChildAttach`.

**Fix:** `await Task.Run(() => writer.Write(entryName, source), token)`.

#### E5 — temp cleanup can throw (MEDIUM)

*Location:* `MainWindowViewModel.cs:3650`

`Directory.Delete(tempDir)` is non-recursive and sits outside the `try`. Any leftover file (e.g. `CreatePackageAsync` returned false, so the temp `.pak` was never deleted) makes it throw, unhandled.

**Fix:** `Directory.Delete(tempDir, true)` inside a `finally`.

#### E6 — division by zero (HIGH) (cosmetic) (MOOT 2026-11: DROPPED)

*Location:* `MainWindowViewModel.cs:3574`

`1d / modPaks.Count` is `Infinity` when the order is empty. Guard it.

#### Design note for Stage 8

The export writes one order JSON and then streams each mod, keyed by `Path.GetFileName(mod.FilePath)`. Multi-order export needs a set of mods unioned across orders and several order JSONs. Restructuring around `(IEnumerable<DivinityLoadOrder>, HashSet<mod>)` is a small change *if done while fixing these bugs*, and an awkward one later. Do the refactor here; ship the feature in Stage 8.

<a id="stage3"></a>

## 9. Stage 3 — Archive import losses

**In short:** Importing a zip can lose mods or the load order. The plan's main suspect was wrong: the real cause is importing an order named "Current".

`UPSTREAMABLE` `fix/archive-import` — `ImportArchiveAsync`, `MainWindowViewModel.cs:3390-3524`.

> **Review update 2026-11-23: I1 is refuted.** SharpCompress 0.39's zip entry streams (stored and deflate) fill the buffer in one read; a short read only happens on a corrupt entry. Keep a `ReadExactly` as hygiene, but it is not why orders vanish. The silent order losses that are real (confirmed):
>
> - **Importing an order named "Current"**, i.e. any archive exported while Current was selected. With a saved order selected, the import is discarded and the selected order becomes a copy of Current (`:3119-3125`); otherwise Current flips to a non-modsettings order, so a later Save writes JSON into `modsettings.lsx` and Export to Game throws (AUDIT-PLAN-01, HARV-31).
> - An imported order with the same name as a saved one overwrites that order's file on the next save, without asking (HARV-32).
> - Two order JSONs with the same base name in one archive: the second is lost (HARV-29).
> - Zips from third-party streaming writers report `Size=0` for entries written with a data descriptor, so the order JSON reads as empty (CLAIM-01). The manager's own exports are not affected.
>
> **Other import defects (confirmed):**
>
> - All three pak import paths truncate the destination in place (`MainWindowViewModel.cs:1845, 3302, 3429`), so a cancel or an error destroys the installed mod (HARV-61, AUDIT-PLAN-04). Fix: write to a temp file, then rename. Stage L depends on this.
> - `.bz2/.xz/.zst` imports write a 0-byte pak and report success, because the temp copy already drained the stream (AUDIT-PLAN-02). The same path has a security hole, fixed in [Stage F](#stageF) (SVC-01).
> - Archives of 2 GiB or more can't be imported: I3's throwaway read fails with `OutOfMemoryException` at `new byte[Length]` before the cast matters (HARV-27). The same two lines also sit in `ImportCompressedFileAsync` and `LoadResourceAsync`.
> - Importing a mod replaces the installed one with the same UUID even when the import is older (HARV-33).
> - I2 and I5 are confirmed as written. I4: the acceptance half is confirmed; the solid-archive slowness needs a timing test. Side note: `.tar.gz` can never match in `AddModFromFile`, because `Path.GetExtension` returns `.gz`.
>
> **Suggested split:** one branch for pak writes (temp then rename, with HARV-33's version check as the overwrite policy) and one for order naming (AUDIT-PLAN-01, HARV-31, HARV-32, HARV-29). Both are upstreamable.

#### I1 — the load order is silently lost (HIGH CONFIDENCE) (REFUTED 2026-11: SEE ABOVE)

*Location:* `MainWindowViewModel.cs:3457-3462`

```
int length = (int)file.Size;
var result = new byte[length];
await entryStream.ReadAsync(result, 0, length);   // return value discarded
string text = Encoding.UTF8.GetString(result);
```

A single `ReadAsync` is **not guaranteed to fill the buffer**, and on a decompression stream a short read is normal. The count is discarded, so `result` keeps trailing zero bytes → truncated JSON → `SafeDeserialize` returns `null` (it swallows errors by design, `DivinityJsonUtils.cs:29-37`) → `order == null` → the order is dropped with **no error shown and nothing logged**.

That is a complete causal chain for "sometimes it doesn't import the load order", and it is size-dependent, which is why it looks intermittent.

**Fix:** `await entryStream.ReadExactlyAsync(result, cts)`, or copy into a `MemoryStream` and decode that. Then make the deserialize failure loud.

#### I2 — `success` leaks across entries (HIGH CONFIDENCE)

*Location:* `MainWindowViewModel.cs:3395 (declaration), 3428-3444 (use)`

`bool success` is declared once for the whole archive and set `true` on the first successful extraction, but **never reset per entry**. If pak #1 extracts and pak #2 fails, `success` is still `true`, so the code proceeds to `LoadModDataFromPakAsync` on a truncated file. That returns null, and the mod vanishes without a visible error.

**Fix:** a per-entry local. Also use the loop variable rather than a shared flag for the "did anything at all succeed" decision further down.

#### I3 — whole archive read into a throwaway buffer (HIGH) (REVISED 2026-11)

*Location:* `MainWindowViewModel.cs:3411`

```
await fileStream.ReadAsync(new byte[fileStream.Length], 0, (int)fileStream.Length);
fileStream.Position = 0;
```

Allocates a byte array the size of the entire archive, reads into it, discards it, and rewinds. For a friend-sized modlist zip that is a multi-hundred-MB spike for no benefit, and `(int)` overflows above 2 GB. **Fix: delete these two lines.**

#### I4 — .7z and .rar are accepted but handled badly (MEDIUM)

*Location:* `MainWindowViewModel.cs:3048 (filter), 3066 (guard commented out)`

The dialog offers `*.7z, *.rar, *.zip`, and the guard that used to reject non-zip is commented out. `ArchiveFactory.Open` + `Entries` + `OpenEntryStream` on a *solid* 7z/rar re-decompresses from the start for each entry — pathologically slow, and a plausible "it just hangs / half the mods missing" report.

**Fix:** keep accepting them, but route non-zip through `ReaderFactory` forward-only extraction, or extract once to a temp dir. Decide by measurement on a real 7z.

#### I5 — same-named paks collide (MEDIUM)

*Location:* `MainWindowViewModel.cs:3422-3424`

Every `.pak` is flattened to `PathwayData.AppDataModsPath` by filename, so two entries in different archive folders with the same name silently overwrite. `File.Create` truncates, so the second wins.

> **Are the export and import bugs the same defect?** Partly, and it matters for testing. **E2** produces archives that are genuinely missing mods — importing one of those looks like an import bug but isn't. **I1/I2** are independent import defects that would lose data even from a perfect archive. So: fix export first, then re-test import *with a freshly exported archive* and with a known-good hand-made one, or you will not be able to tell which fix did what.

<a id="stage4"></a>

## 10. Stage 4 — Perf A: startup load

**In short:** Make startup faster. Measure first: most of the 9 seconds is not where the plan expected.

`UPSTREAMABLE` `perf/startup-load` — mostly `src/Core`, so this survives a future Avalonia port.

> **Measure first. This stage starts with instrumentation, not optimisation.** Your own Facet spec records that the obvious first fix was wrong and only measurement showed it. Same rule here.

> **Review update 2026-11-23** (design: `docs/review/stages/S4-final.md`).
>
> - **Loading runs on the UI thread** (CLAIM-07, confirmed by code reading). `RefreshAsync` starts on the taskpool, but its first await on `Observable.Start(..., RxApp.MainThreadScheduler)` resumes inline on the dispatcher (System.Reactive 6.0.1's awaiter), so the rest of it, pak loading included, runs there. The hop predates `fc35358`: `SetMainProgressTextAsync` already did it. Another ledger (AUDIT-PLAN-06) says the loop runs on the pool; a thread-id log settles it.
> - **The pak loop is not most of the 9.27 s.** 26 runs already in `bin/Debug/_Logs` show 1,098 paks loading in 2.46-3.00 s, editor projects in 0.03 s and base-mod parsing in 0.08-0.11 s. That last timer starts *after* the base-game VFS build, so about 6.5 s is unattributed, and the VFS build over the 48 Data paks (AUDIT-PLAN-08; it finds 18 base modules on this install) is the likeliest owner. Time it first in 4.1.
> - **Kill the MD5 candidate:** nothing hashes anything (HARV-63). A side effect: duplicates with equal versions get an arbitrary winner ([Stage F](#stageF), lower priority).
> - `DivinityModData.Files` keeps every entry path of every pak and nothing reads it (AUDIT-PLAN-07, memory). `LoadModPackageDataAsync`'s `dupes` is a lazy query re-run per mod: O(n²), but only milliseconds.
> - S4-final's order: a zero-code first measurement, then loading off the UI thread, then bounded parallelism, then a facts cache keyed by full path + size + mtime + header. It is R1-safe: override and dedupe decisions are re-made on every load. Its estimate (1.5-5 s cold, 0.4-1.2 s warm) predates the log finding above. Stage L asks that the cache key full paths and read file stats from the file, not the directory entry.

### 4.1 Instrument

Add stopwatch timings around each of `RefreshAsync`'s ten steps and around `LoadBuiltinModsAsync` / `LoadModPackageDataAsync`, logged via `DivinityApp.Log`. Capture with a real mod set and record N.

### 4.2 Candidates, to confirm or kill

| Candidate | Note |
|---|---|
| Per-pak full entry enumeration + regex | Override detection matches `^(Mods\|Public)/(.+?)/.+$` against **every file entry in every pak**. Likely the dominant cost at high mod counts. |
| MD5 hashing | If a hash is computed over whole pak files, that alone can dominate. **Killed 2026-11:** no hashing exists anywhere. |
| No metadata cache | There is no on-disk cache of parsed mod metadata. A cache keyed by *path + mtime + size* would skip nearly all work on subsequent launches — probably the single biggest win, and low risk because a stale entry is detectable. |
| Double enumeration of loose mods | Editor mods are discovered twice (LSLib VFS and `LoadEditorProjectsAsync`). |

> **Correctness outranks speed here.** Anything touching dedupe or override detection can change *which* mod wins or whether a mod is classed as force-loaded. Snapshot the full mod list (UUID, version, flags) before and after and diff it.

<a id="stage5"></a>

## 11. Stage 5 — Perf B: filter & search

**In short:** Make the search box faster, then add fuzzy search. The plan's first two steps would not help, so measure first.

`UPSTREAMABLE` `perf/filter` `+ fork-only: fuzzy matching`

> **Hypothesis tested and rejected.** Both earlier drafts assumed the `Throttle(500ms)` at `MainWindowViewModel.cs:5250,5253` was most of the felt slowness. **It is not.** Measured against the real 1 121-mod load (Debug build, UI Automation driving the real search boxes, CPU sampled to idle):
>
> | Action | Settled at | Work *after* the 500 ms throttle |
> |---|---|---|
> | Inactive list, broad match (`a`) | 3.77 s | **3.27 s** |
> | Inactive list, cleared | 1.87 s | 1.37 s |
> | Inactive list, **no match** (`zzzzzz`) | 1.74 s | 1.24 s |
> | Active list, broad match (`e`) | 1.89 s | 1.39 s |
> | Active list, cleared | 1.84 s | 1.34 s |
>
> The throttle is **at most a fifth** of the delay. Even a search matching *nothing* burns ~1.2 s. Cutting the throttle to 150 ms would save ~350 ms out of 1,700–3,800 — and would make things *worse* by firing that 1.2 s of work more often per keystroke. **Fix the per-keystroke cost first; retune the throttle last.**

> **Review update 2026-11-23** (design: `docs/review/stages/S5-final.md`; fuzzy prototype in `docs/review/prototypes/fuzzy-search/`).
>
> - **Steps 1 and 2 below save nothing** (confirmed): the `Visibility` and `IsSelected` setters already skip equal values, and the plain search allocates no per-mod strings (CLAIM-03, CLAIM-04).
> - **The 1.2-3.3 s is not in `OnFilterTextChanged`'s loop**, which costs milliseconds (CLAIM-05). Plausible owners: UI Automation (the measurement drove the boxes through UIA, and 6.1 found an outside UIA client walking the tree), idle sampling, Debug JIT and container realization. Start with S5-final's Step 0: a probe run with filtering disabled, as the control.
> - **Filtered drops are safe** (CLAIM-14 refuted): a drop lands next to the visible target row. So the "Step 5 is more invasive" box below overstates the drag-and-drop half; the selection half still holds. The real wrong-position bug is a drop into a *column-sorted* active list (HARV-71).
> - The `@desc` and `@file` filters would throw on a mod with no description or path; latent only, add the null guard in any rework (HARV-69).
> - S5-final's order: Step 0 probe; extract `ModFilterQuery`; count flips; filter through the collection view and drop the row `Visibility` binding; map drops by item (with the view step); O(n) renumber; the 150 ms throttle last. Fuzzy search (fork-only) sits on top; the prototype scores 1,121 mods in about 0.5 ms per keystroke.

### Where the time goes

`OnFilterTextChanged` (`:4199`) walks every mod and assigns `DivinityModData.Visibility`; each row then collapses itself. One keystroke = N property-change notifications + N container visibility changes + a full layout pass. Because the items stay in the collection, the virtualizing panel still has to realize and collapse containers instead of simply not having them — so virtualization (which *is* enabled, `Recycling` mode) buys much less than it should.

> **Requested feature: fuzzy matching.** Konstantinos wants search to be fuzzy, not just substring. This is a *behaviour* change on top of the perf work, and the two interact: a naive fuzzy scorer run over 1 121 names per keystroke would be far more expensive than today's `Contains`, so it cannot be bolted onto the current design. Doing the perf rework first is what makes fuzzy affordable.
>
> Design notes for when this is picked up: keep the existing `@author foo` field syntax (`:4178-4316`) working; score and *rank* rather than just include or exclude; precompute a lowercase match key per mod once at load rather than per keystroke; and decide explicitly whether fuzzy applies to the active list too, where order is meaningful and reordering by score would be wrong.

### Order of work, measured-cheapest first

1. **Only assign `Visibility` when it actually changes.** Compare before writing. On a no-match search this should collapse ~1.2 s toward nothing, and it is a few lines. Do this first and re-measure — it may be most of the win. *(2026-11: saves nothing; the setters already compare. See the review update.)*
2. **Hoist the per-keystroke allocations.** Cache a lowercase name/author key per mod at load time instead of rebuilding strings for 1 121 mods on every filter pass. *(2026-11: saves nothing; there are no per-mod allocations to hoist.)*
3. **Re-measure, then retune the throttle** — once a keystroke is cheap, 150 ms becomes safe and the UI feels live.
4. **Then add fuzzy matching** on the now-cheap path, with ranking.
5. **Only if still needed: move to real DynamicData filtering.** The principled fix, and the invasive one.

> **Step 5 is more invasive than it looks.** The visibility-stamping design is load-bearing: `ModListDropHandler` uses `dropInfo.UnfilteredInsertIndex` *precisely because* the lists are visibility-filtered rather than collection-filtered, and `DivinityModData.IsSelected` refuses to become true while `Visibility != Visible`. Converting to collection filtering means reworking drag-and-drop insert indices and selection. Treat it as its own pass, and only on evidence.

<a id="stage6"></a>

## 12. Stage 6 — The list workstream

**In short:** The two mod lists: smoother scrolling (half done), then overrides shown in the load order, then categories.

`perf/list-render → feat/overrides-in-order → feat/categories`

Three things that all rewrite the same surface. Doing them separately means rebuilding the mod list three times, so they share a stage — but they stay separate branches, because the first is upstreamable and the other two are not.

> **The landmine.** `MainResourceDictionary.xaml:461` contains `<Trigger Property="IsGrouping" Value="True">` → `CanContentScroll="False"`. That is stock WPF boilerplate and is inert today. But `CanContentScroll="False"` switches the ScrollViewer to pixel scrolling, which **disables UI virtualization**. Implement categories with `CollectionViewSource` grouping and you will silently destroy scrolling performance on exactly the lists you just optimised.
>
> **Therefore: categories are flat rows with divider items in one collection, not WPF grouping.**

### 6.1 Render performance `UPSTREAMABLE` `perf/list-render`

> **Symptom, in the owner's words (2026-09-23):** the inactive list is *"not stuttery but not as smooth as I'd like"*; the active list has *"lag/low framerate when grabbing the scroll bar and dragging it"*. The active list holds 935 rows, the inactive roughly 186.

**Everything below was measured, not reasoned.** A temporary harness (`src/GUI/Util/PerfProbe.cs`, enabled by an extension-less `perfprobe` file next to the exe, mirroring the `debug` convention) records frame pacing from `CompositionTarget.Rendering` plus call counts and wall time on individual paths, and dumps to the log every 5s and on exit. **Twelve hypotheses were tested across two sessions; the cause of the name-cell cost is identified and fixed** (the "Resolved 2026-09-23" note below). Do not re-litigate the rejected ones from first principles — the numbers are here.

| Hypothesis | Verdict | Evidence |
|---|---|---|
| Screen-reader reflection per row bind<br>(`ScreenReaderHelperBehavior`) | **NOT the cause** | Real but tiny: 3 835 calls costing **60 ms out of a 5 000 ms window** (1.2%) while half the frames in it were dropped. ~14 µs each. Still worth caching one day; it will not be felt. |
| Garbage collection | **NOT the cause** | gen2 collections **0** throughout, gen0 5–16 per window, heap flat around 390–425 MB. |
| Container churn / virtualization off | **NOT the cause** | Virtualization is on and recycling works: only **106** containers were ever created across a whole session. |
| Column autosize walking all mods<br>(`AutoSizeNameColumn_*`) | **NOT the cause** | Only runs on `OrderJustLoaded`, never during scroll. It *is* where the 58 `MeasureText` glyph failures at startup come from. |
| `Width="Auto"` on the Last Updated column | **Fixed, but changed nothing measurable** | Plausible feedback loop (realize rows → widest date changes → re-arrange all rows), and Auto on a fixed-format date column is pointless, so the fix was kept (`b123aff6`). Full-list Arrange stayed at 8–16 ms either side of it. **Claim no credit for this one.** |
| Custom accessibility peers walking the visual tree | **NOT the cause** | `AutomationInteropProvider.ClientsAreListening` is `True`, so a client really is attached — but `AutomationPeer.GetChildren` recorded **zero calls**. The subtree walks in `CachedAutomationPeer` / `ModEntryGridAutomationPeer` never run during scrolling. *Amended 2026-09-23 (session 2):* in 4 of ~25 scripted runs an outside client *did* walk the tree mid-run — 6 206 calls at 155 µs, once 9 847 at 536 µs (5.3 s). Not the name-cell cost, but a real intermittent one; see "Next" below. |
| **Session 2 (2026-09-23), scripted scroll, 25 rows/frame** |  |  |
| The shared `ModToolTip` instance | **NOT the cause** | Removing the ToolTip from every row: name Arrange 222 µs, frame 33.7 ms, against a baseline pair of 204/228 µs and 33.3/34.0 ms. WPF source agrees: `ToolTipService.ToolTipProperty` has no layout flags and no callback, and a closed tooltip keeps no link to any row. |
| `Tag="{Binding}"` (with the ToolTip) | **NOT the cause** | 218 µs, 33.6 ms. |
| Font fallback / unusual characters in names | **NOT the cause** | A 36-character ASCII string (the UUID) costs 246 µs — cost tracks *length*. Authors (~10 chars) 90 µs, names (avg 30.8) ~215. |
| `TextFormattingMode=Display` / `HorizontalAlignment=Left` | **NOT the cause** | 209 / 182 µs in the app; headless, Stretch, Left and Display all land at 72–88 µs Measure and 78–102 Arrange. |
| Off-screen row cache (`CacheLength` 0 / 10 items) | **NOT the cause** | Only 28 rows are realized (26 visible). 207 / 204 µs, no frame change. |
| Automation ancestor walk at the top of `UIElement.Arrange` | **NOT the cause** | ~1 µs in the in-app split of one Arrange (see the "Resolved 2026-09-23" note). |

> **Resolved 2026-09-23: what the 248 µs was, and the fix.** Committed `2858a235` + `7fa83473` (both upstreamable; no TEMP code in either).
>
> **First, the "50× slower than Measure" framing was a probe artifact.** When a recycled row gets a new mod, the name TextBlock's text changes while `ModEntryGrid` is still clean, so WPF's layout manager measures the TextBlock *directly* from its queue — `ModEntryGrid.MeasureOverride` never sees the text work. Its 7 µs is Grid bookkeeping. Measured directly, a plain Author TextBlock in the same row costs 90 µs to arrange. Nothing was special about the name cell except its length.
>
> **Ranked causes of the per-row cost** (in-app, 25 rows/frame):
>
> | # | Cause | Share of ~220 µs | Evidence | Status |
> |---|---|---|---|---|
> | 1 | **Redraw registers with an unfrozen, shared brush.** `DrawingContext.Close()` swaps the new drawing in; `RenderData.PropagateChangedHandler` registers it with every unfrozen Freezable it uses and unregisters the old one. The row text brush is an AdonisUI theme brush — unfrozen by design (its colour is a `DynamicResource`) and shared by the whole window, so each swap costs time proportional to its holders. Headless elements never pay it. | ~120 µs | In-app split of one Arrange: before override 1.0, clip/RenderOpen 1.5, OnRender 6.0, **Close 118–122 µs**; brush logs `IsFrozen=False`; drawing with a frozen snapshot → Close 4 µs. | **Fixed** `7fa83473` |
> | 2 | **Text re-shaped from scratch on every text change, twice.** TextBlock keeps nothing between strings: Measure shapes, then `OnRender` (run inside Arrange) shapes again. ~3–4 µs per character, per pass. | ~95 µs (+ ~70 unseen in Measure) | Headless over the 936 real names: TextBlock 72 + 80 µs; FormattedText 60 + 66; drawing cached glyph runs 0.1 + 0.8. WPF source: `TextBlock.OnRender` reformats every line; nothing caches glyph runs. | **Fixed** `2858a235` |
> | 3 | Grid, border and icon strip around the text | ~15 µs | Residual; icons alone 3–5 µs. | Left as is |
>
> **The fix: `src/GUI/Controls/CachedTextBlock.cs`**, used for `ModNameText` in `ModNameTemplate`. It shapes each distinct string once via `FormattedText`, caches the glyph runs (shared, keyed by text, typeface, size, DPI, formatting mode, direction and culture; 8 192-entry cap), redraws them on later appearances, and draws with a frozen snapshot of `Foreground`. It keeps a *weak* subscription to the brush so theme switches still recolour. It keeps TextBlock's hit-testing (tooltip over empty cell space), clipping and a Text automation peer. `ModEntryGridAutomationPeer` now looks it up by the new type.
>
> **One WPF quirk it works around:** `FormattedText` pixel-snaps each line with a *dynamic* guideline pair stored as (leading, offset). A `DrawingGroup` records the pair verbatim, but `PushGuidelineSet` reads a dynamic pair back as (leading, *driven coordinate*) and subtracts — a naive replay moves text a sub-pixel. The replay set is rebuilt through `GuidelineSet`'s internal dynamic constructor (reflection, once per new string, never per frame). If that constructor ever disappears, the element falls back to drawing `FormattedText` — slower, never wrong.
>
> **Fidelity, headless:** all 936 names plus empty, whitespace, line-break, tab, CJK, emoji, Hebrew and Arabic strings, each at plain / sub-pixel offset / 144 DPI / both — **3 784 renders, 0 pixel, size or hit-test differences, 0 fallbacks**. Plus six brush-mutation steps (recolour in place, swap brush, old brush changes after swap…), all pixel-identical to TextBlock. In the app, both elements resolve the same font, size, weight, foreground, formatting mode and language (`en-au`).
>
> **Before / after** — alternating runs of the build before either change and the final build, same scripted workload (935 rows, 3 round trips):
>
> | Metric | Before | After | Change |
> |---|---|---|---|
> | Name-cell Arrange per row, 25 rows/frame (3 pairs) | 220 / 217 / 219 µs | **23 / 20 / 22 µs** | **−90%** |
> | UI-thread layout+render per frame | 33.4 / 33.1 / 32.7 ms | 24.7 / 25.4 / 25.3 ms | −24% |
> | Median frame | 28 ms (35 fps) | 21 ms (48 fps) |  |
> | Frames over 33 ms | 36–40% | 19–21% | ~halved |
> | p99 frame | 88–97 ms | 65–66 ms |  |
> | Name-cell Arrange, 5 rows/frame | 220 µs | 20 µs | −91% |
> | Median frame, 5 rows/frame | 7.8 ms (129 fps) | 6.2 ms (160 fps) |  |
>
> The shaping cache alone (`2858a235`) took the row from ~222 to ~127 µs; the frozen brush (`7fa83473`) took it to ~21. The untouched Author TextBlock also got cheaper (88 → 71–79 µs): fewer holders of the shared brush.

> **Next — what the thumb drag still costs (~25 ms/frame at 25 rows/frame).** Ranked by expected payoff:
>
> - **The other four text cells** (Index, Version, Author, Last Updated) pay both costs above: Author measured at 71–90 µs Arrange plus an unseen Measure, per row, per cell. Converting them to `CachedTextBlock` is the same change again. Caveats: the Index cell's `Background="Transparent"` and Version's `TextAlignment="Left"` are redundant defaults to drop; **the active list's Author cell is wrapped by the TEMP `ProbeDecorator` from `ab73bf1f`** — drop the TEMP commits first or expect a conflict there.
> - **Virtualizing panel Measure: ~8.5 ms/frame** at 25 rows/frame (recycling, re-binding ~35 bindings per row, the other cells' measures). The largest single block left.
> - **Outside accessibility client walking the tree** (see the amended row above): intermittent, but up to 5 s of a 16 s run once. The custom peers' subtree walks (`CachedAutomationPeer.GetChildrenCore`) are what it pays for.
> - **Hand thumb-drags trigger a whole-list Arrange** (~50 per 5 s, never in the scripted runs). Inferred from source, not measured: WPF's layout queue holds 153 requests and, once ~145 are queued, escalates to marking every ancestor dirty.
> - `ScrollUnit="Pixel"` for the inactive list's "not smooth" complaint — still untried; see below.

> **How to measure (the rig, all TEMP).** Next to `bin\Debug\BG3ModManager.exe`: a `perfprobe` file enables PerfProbe, and its *contents* are variant flags (`step25`, `cache0`, `notooltip`, `notag`, `uuidtext`, `display`, `left`, `notext`; empty = baseline). An empty `perfscroll` file makes `PerfScrollDriver` wait for the list to load, scroll the active list 3 round trips (5 rows/frame by default), write one `[PerfProbe:scroll]` summary to `_Logs`, and close the app. About 20 s a run; `modsettings.lsx` stayed byte-identical through ~30 runs. Two identical baselines agree within ~10% per row and ~2% per frame. The variant switches target a `TextBlock` and are inert now the name is a `CachedTextBlock`. The name-element split needs a hand edit of the template (see `9f2b1630`'s message).

<details>
<summary>Session 1's working record, superseded by the "Resolved 2026-09-23" note above (kept for the history)</summary>

> **Where the time actually goes.** Arrange, not Measure, and it is concentrated in one place:
>
> | Path | Calls | Total | Each |
> |---|---|---|---|
> | `Arrange ActiveModsListView` | 252 | 4 010 ms | 15.9 ms |
> | `ModEntryGrid.Arrange` (one name cell) | 14 693 | 3 648 ms | **248 µs** |
> | ↳ icon strip Arrange (5 Images) | 7 114 | 36 ms | **5 µs** |
> | `ModEntryGrid.Measure` (same cell) | 20 578 | 142 ms | **7 µs** |
> | `ScreenReader reflection` | 14 729 | 205 ms | 14 µs |
>
> **Read those four numbers together.** A whole-list Arrange of ~16 ms is not one expensive operation — it is 30–60 realized rows at ~248 µs each, and `ModEntryGrid.Arrange` alone is 3.6 of the list's 4.0 seconds. The same element *measures* in 7 µs, so the Grid and its child count are not the problem. And the icon strip — five `Image` elements with trigger-driven styles, the obvious suspect — costs **5 µs, about 2%**.
>
> **So ~243 of the 248 µs belongs to the other half of the cell: the name `TextBlock`.** That is the open question, and it is where the next session should start.

> **What is odd about that, and worth checking first.** A single-line `TextBlock` arranging in 243 µs is roughly fifty times too slow. It is declared in `ModNameTemplate` (`MainResourceDictionary.xaml:706`) with `HorizontalAlignment="Stretch"`, `Tag="{Binding}"`, `Text="{Binding DisplayName}"`, `ToolTip="{StaticResource ModToolTip}"` and `ToolTipService.IsEnabled="{Binding HasToolTip}"`.
>
> Candidates, in the order a fresh pair of eyes should test them:
>
> - **The shared `ModToolTip` instance.** One `AutomationTooltip` (~40 elements, 29 bindings) is assigned as the `ToolTip` of every realized row's TextBlock at once. It is shared rather than per-row, which is good, but a `ToolTip` is a `FrameworkElement` and can only have one logical parent — so dozens of elements claiming the same instance may be re-parenting it during layout. **Prime suspect.**
> - **Text formatting per arrange.** No `TextWrapping` is set, so it should be one line, but glyph-run construction may be re-running because the row height is not pinned. Test by fixing row height and by setting `TextOptions.TextFormattingMode`.
> - **The `Tag="{Binding}"`**, which exists only so the shared tooltip can reach the mod via `PlacementTarget.Tag`.
>
> The same bisect trick that cleared the icons will settle this: a `ProbeTextBlock` timing its own `ArrangeOverride`, then removing the ToolTip from the template for one run to compare. Both are non-destructive.
>
> *Outcome (session 2): the tooltip, the Tag and text formatting mode were each tested and cleared; "text formatting per arrange" was right in spirit — see the "Resolved 2026-09-23" note.*

</details>

> **Do not confuse `ScrollUnit="Pixel"` with `CanContentScroll="False"`.** Both give pixel-smooth scrolling; only one is safe. `VirtualizingPanel.ScrollUnit="Pixel"` keeps the `VirtualizingStackPanel` in charge and **keeps virtualization**. `ScrollViewer.CanContentScroll="False"` hands scrolling to the `ScrollViewer` over the whole realized panel and **disables virtualization** — fatal on a 935-row list. Never set the second on these lists.
>
> `ScrollUnit` is still set nowhere in the repo, so both lists default to `Item` and step a whole row per notch. That is the likely cause of the *separate* "not smooth" complaint about the inactive list, and is still untried.

**Also found, not yet fixed:** `ElementHelper.MeasureText` (`:110-128`) has its `try/catch` *inside* the per-character loop, so a name with N unmappable glyphs throws N exceptions rather than one. 58 per startup on this mod set. Startup cost only, but a one-line fix — use `CharacterToGlyphMap.TryGetValue` instead of catching.

> **Branch state to inherit (updated 2026-09-23, session 2).** `perf/list-render` is cut from clean `master` and holds thirteen commits. **Five are real:** `b123aff6` (Last Updated column width, no measured benefit, safe to revert), `309b81ef` and `396d7b2f` (the two halves of the selection fix), `2858a235` (`CachedTextBlock`: cached shaping) and `7fa83473` (frozen-brush drawing). The rest are **marked `TEMP:` and must be dropped before any PR** — plus `e38c87b2`, which only re-encodes the TEMP `PerfProbe.cs`. They carry `PerfProbe.cs`, `PerfScrollDriver.cs`, `ProbeStackPanel.cs`, `ProbeDecorator.cs`, `ProbeVirtualizingStackPanel.cs`, `ProbeCachedTextBlock.cs`, `ProbeSegmentDecorator.cs`, the timing hooks in `ModListView` / `ModEntryGrid` / `CachedAutomationPeer` / `ScreenReaderHelperBehavior`, the `ItemsPanel` setter and Author-cell wrapper in `MainResourceDictionary.xaml`, the `MainWindow` hook, the selection logging in `UpdateIsSelected`, and a change swapping `MeasureText`'s exception logging for a counter. Keep the harness while the list work is open; drop it, and restore that logging, when it closes. Pushed to `origin` (by 2026-11-03) so the cloud review could read it.
>
> **Tested 2026-09-23 by cherry-picking the real commits onto `master`** in a throwaway worktree: `b123aff6`, `2858a235` and `7fa83473` apply cleanly, and the result has no probe references. **`309b81ef` conflicts** in `HorizontalModLayout.xaml.cs`: it was written on top of the TEMP selection logging from `789ec87c`, so it needs a hand-resolved version for any PR. (Not built on bare `master`; that needs the submodules set up in the worktree.)
>
> **Review note 2026-11-23.** The cloud review never got to this branch: its 6.1 branch review is missing, so nothing here has had a second pair of eyes. Three unverified leads from it touch files this branch changes: the custom automation peers strip UIA patterns and walk the whole visual tree per query (UI-07, which fits the outside-UIA-client cost above), `MainResourceDictionary.xaml` is parsed 15 times (UI-10), and the screen-reader DLL preload uses an ANSI path and ignores failure (UI-14).

> **Review design for 6.2 and 6.3: `docs/review/stages/S6-final.md`** (judged across three designs). Rows become one flat `ActiveRows` collection with `ActiveMods` derived from it; dividers persist in a versioned `Layout` member of the order JSON; override *positions* are not persisted, because the game ignores them; an Overrides block lists the installed force-loaded mods. PR-0 is the `modsettings.lsx` write guard ([Stage F](#stageF)). Two confirmed facts shape it: `Order` is rebuilt from `ActiveMods` after every change, so any per-entry field is wiped (CLAIM-09, Stage F's order-keep-missing fix), and a path UUID must never reach `modsettings.lsx` (CLAIM-15). Its open questions are in §14 and its in-game protocol in §15.

### 6.2 Overrides folded into the load order `FORK-ONLY`

Today force-loaded mods are deliberately excluded from the order unless `IsForceLoadedMergedMod` or the user sets `ForceAllowInLoadOrder` (CLAUDE.md §6). Folding them in is a *behaviour* change, not a UI one: the export path (`BuildOutputList`) must keep writing a `modsettings.lsx` the game accepts. Verify by exporting and booting the game, not just by looking at the list.

### 6.3 Categories / dividers `FORK-ONLY`

- Model: a divider entry in `DivinityLoadOrder.Order` — needs a shape that round-trips through the order JSON without confusing older builds or upstream.
- Dividers must be skipped when building `modsettings.lsx`; the game must never see them.
- Collapsing a category is a view concern; the order indices underneath must not shift.
- Drag-and-drop across a divider has to keep `mod.Index` contiguous — see the renumbering loop in `ModListDropHandler`.

```text
▼ Gameplay Overhauls   3
1. Combat Extender
2. Party Limit Begone
3. Tactician Enhanced
▶ Overrides   2 collapsed
▼ Cosmetics
6. Better Hair
```

*Dividers are ordinary rows in one flat collection — collapsible, draggable, and invisible to `modsettings.lsx`. Overrides become a normal category.*

<a id="stageL"></a>

## 13. Stage L: mod library and per-order deployment

**In short:** A library that keeps every version of every mod, so each load order can use its own versions.

`FORK-ONLY` `feat/lib-*`, design: `docs/review/stages/LIB-final.md` (judged from three designs; design only, nothing built)

> **Requested 2026-11-03, proposed here, not yet accepted.** Keep every version of every mod, let each order pin the versions it uses, and deploy an order by hardlinking exactly its paks into `Mods`.

- **Library:** every pak version stored once, keyed by its SHA-256 content hash, with a library trash for safe removal.
- **Pins:** one top-level `"Pins"` map per order, plus `Deploy.Overrides` for override membership. Not on `DivinityLoadOrderEntry`: that list is rebuilt after every change, so a pin stored there is wiped before deploy reads it (CLAIM-09). `[JsonExtensionData]` on `DivinityLoadOrder` so older fork builds keep both members.
- **Deploy:** on export, sync `Mods` to the order with hardlinks. The premise: a pak left in `Mods` but not listed in `modsettings.lsx` may still override vanilla files (from the Nexus Mods App's BG3 notes; in-game test G4 confirms it on your machine). A junction-swap mode can come later behind the same deployment-target interface.
- **Placement:** after 6.2, before Stage 8; Stage 7 can go either side. Hard prerequisites: temp-then-rename pak writes (Stage 3, HARV-61) and the `modsettings.lsx` write guard (Stage F). Soft: the Stage 4 cache (full-path keys, stats read from the file) and the Stage 2 export restructure.
- **Verification:** in-game tests G1-G9 (back up `Mods` and `PlayerProfiles` first), Windows integration tests on NTFS, and unit tests over a fake file-operations layer (LIB-final §11).

<a id="stage7"></a>

## 14. Stage 7 — UI/UX refresh

**In short:** A visual refresh of everything except the mod lists.

`FORK-ONLY` `ui/*`

Everything that isn't the mod list, after the list has settled. Working within AdonisUI rather than replacing it keeps the diff reviewable and the theme system intact.

- Brush and spacing pass in `Themes/Light.xaml` + `Dark.xaml` — **they must stay key-for-key identical**; a key defined in only one yields a null brush in the other theme (CLAUDE.md §4).
- Any new window needs a line added to `MainWindow.UpdateColorTheme`, or it is stuck on whatever theme was live when it was constructed.
- Settings are generated by reflection from `[SettingsEntry]`, not written in XAML — restyling them means changing the generator, not a form.

**Avalonia note:** this is the stage a future port would throw away entirely. If a port ever looks likely, keep behaviour in view models rather than code-behind.

**Review design:** `docs/review/stages/S7.md` (a refresh within AdonisUI). One unverified lead belongs here: the light theme is not applied to lazily created windows or popup message boxes (UI-06).

<a id="stage8"></a>

## 15. Stage 8 — Multi-order export

**In short:** Export several load orders into one zip.

`FORK-ONLY` `feat/multi-order-export`

Select several orders; produce one zip containing each order's JSON plus the union of their mods, deduplicated. Cheap *if* Stage 2 already restructured the export around a set of orders and a mod set; awkward otherwise. Dedupe by UUID, and decide explicitly what happens when two orders reference different *versions* of the same mod — that is a real case, and the naive union silently picks one.

**Review design:** `docs/review/stages/S8.md` (a multi-order bundle format, designed together with friend sync). With [Stage L](#stageL), version pins answer the "different versions" question: the bundle carries each order's pinned versions from the library.

<a id="stage9"></a>

## 16. Stage 9 — The ambitious set

**In short:** Bigger ideas (a downloader, syncing with friends, native mods). Each needs its own spec first.

`FORK-ONLY` — **each needs its own spec session before any code.** Listed here so the roadmap is honest about them, not because they are planned.

| Feature | What makes it hard |
|---|---|
| Update checker / downloader (Nexus + mod.io) | Nexus support exists but is disabled by a feature flag, the API key is stored in plaintext with no UI, and the GitHub update path will NRE if enabled (CLAUDE.md §7). The "Mod Updates" view is dead UI — nothing populates it. This is reviving three half-built systems, not adding one. |
| Order syncing with friends | Needs a transport and a hosting story, plus version pinning per mod. The multi-order export format from Stage 8 is the natural payload — design them together. |
| Native mod management | Touches the game's `bin` folder alongside the Script Extender's own `DWrite.dll` — the one integration that currently works. High blast radius. |
| Mod page dependency/version checks | Rides on the Nexus work above. |

**Review skeletons:** `docs/review/stages/S9.md` sketches Nexus, mod.io and native-mod management, and lists about 12 bugs on the Nexus code path to fix before reviving it. Note that a non-empty `NexusModsAPIKey` already creates the Nexus client today, whatever the feature flag says (CLAUDE.md §7).

<a id="risks"></a>

## 17. Risks

**In short:** What could go wrong, and how each risk is handled.

| # | Risk | Mitigation |
|---|---|---|
| R1 | A perf change alters which mod wins dedupe or override detection — silently wrong load orders. | Snapshot/diff the full mod list before and after every Stage 4 change. |
| R2 | Categories implemented via WPF grouping kill virtualization. | Flat rows with divider items. Called out in Stage 6. |
| R3 | A bugfix built on fork-only work becomes unupstreamable. | Branch discipline in §3 — `fix/*` and `perf/*` cut from `master`. |
| R4 | Fork build self-updates to upstream and overwrites itself. | Stage 1, before any public release. |
| R5 | Debug works, Publish breaks — the `_Lib` flattening plus the `AssemblyResolve` hook (CLAUDE.md §2.4). | Test every stage in a `Publish` build, not only Debug. |
| R6 | An upstream merge silently drops changes. | Never configure `merge.ours.driver`. |
| R7 | Divider entries confuse the game or older builds. | Dividers stripped in `BuildOutputList`; verify by booting the game. |
| R8 | Errors stay invisible because logging is off by default and `ThrownExceptions` is unsubscribed everywhere. | Every fix in Stages 2–3 surfaces failures through `ShowAlert`. Stage F's background-errors fix also stops `ScheduleAsync` from dropping exceptions (VM1-02). |
| R9 | Today's code loses load orders silently while you test: missing mods dropped on Save, export with no backup, the restore prompt, "Current" imports (Stage F, Stage 3). | Stage F first. Back up `modsettings.lsx` and `Orders` before every test session. |
| R10 | .NET 8 is out of support (since 2026-11-10), and this machine cannot build `net10.0`. | Install Build Tools 2026 (VS 18) side by side, pin the SDK with `global.json`, then retarget in its own branch. See Stage 0. |
| R11 | A crafted mod writes outside the Mods folder (SVC-01, VM2-06). | Path containment checks in Stage F. |
| R12 | The review's findings are code-traced, not reproduced; a fix can target a bug that behaves differently at runtime. | Start each fix branch with a Windows repro, or an instrumented check that logs PASS/FAIL. |

<a id="verify"></a>

## 18. Verification

**In short:** How we will know each stage works.

### Stage 0

- Debug build completes and the app launches against your real mod folder.
- `Publish` completes twice in a row and produces `BG3ModManager_Latest.zip`.
- Baseline numbers recorded (load time + mod count, scroll feel, filter latency).

### Stage F

- A test mod whose `meta.lsx` lists itself as a dependency: Save and Export skip the cycle and the app stays open.
- Bind a hotkey to a single letter: the editor rejects it, and a `keybindings.json` that already holds one loads with a warning instead of a crash.
- Load an order with a missing mod and Save: the missing entry is still in the saved JSON, in place.
- Empty `Orders` folder, then Save and Export Current: the selection stays on Current, the app stays unlocked, and a `modsettings.lsx` backup exists.
- Restart with Save Window Location on: the window comes back where it was.
- Each check ships as an instrumented dev check that logs PASS/FAIL, per the standing rule.

### Stage 2 — export

- Export an order, then use **Export As over the same filename** with fewer mods. The second zip opens cleanly. *This is the E1 regression test and it fails today; a plain Export never overwrites, so it can't reproduce E1.*
- Lock a `.pak` (open it in another program) and export — you get a visible warning naming the mod, not a silent success.
- Cancel an export midway — no partial zip is left behind, no `_Temp_*` folder remains.

### Stage 3 — import

- Round-trip: export an order, import it into a clean mods folder. Every mod and the order itself come back.
- Export while **Current** is selected, then import that archive with a saved order selected: the imported order appears and the saved order is unchanged (HARV-31). This replaces the refuted I1 large-JSON test.
- Cancel an import midway, and import a `.pak.zst`: the installed copy survives and no 0-byte pak is left (HARV-61, AUDIT-PLAN-02).
- Import an archive containing one deliberately corrupt `.pak` — the others still import and you are told which one failed.

### Stages 4–6

- Load time improved against the Stage 0 baseline, with the mod list diff clean.
- Typing in the search box feels immediate.
- Scrolling a full list is smooth, **and still smooth with categories in use**.
- Export a categorised order and boot the game — the load order applies correctly.

---

Detailed reference (the plan-overview.html quick view was retired 2026-11-06) · Repo facts: **CLAUDE.md** · Review: branch **kk/laughing-pascal-710otn**, **docs/review/**
