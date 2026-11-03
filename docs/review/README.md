# BG3 Mod Manager fork: codebase review (2026-11-03)

This is a read-only, multi-agent review of `master` (= upstream `fc35358`), your `perf/list-render` and `fix/publish-prebuild-quoting` branches, and upstream `avalonia`. It was checked against your `CLAUDE.md` and `implementation-plan.html` (on the `notes` branch). Nothing in the repo was built or run, because the WPF app needs Windows. Every claim cites `path:line` and is marked Confirmed, Plausible or Refuted.

**Status: incomplete, because the token budget ran out.**
- **Verified:** design defects (`verified/HARV-*`), the docs audit's missed defects (`verified/AUDIT-PLAN`), the build review (`verified/BLD`), and 17 contested claims (`verified/CLAIMS-1..3`).
- **Written but not verified:** the area reviews `areas/LOAD`, `VM1`, `VM2`, `UI`, `SVC`. Treat them as leads.
- **Missing:** the Stage 6.1 branch review, the Stage 2–3 fix spec, the decisions list and roadmap v2. `extra/claude-md-edits.json` is a partial set of CLAUDE.md corrections; the ones that validated are applied in `claude-md/CLAUDE.md.diff`.

## Verified findings to act on first (all Confirmed unless noted)
1. **Settings regression (upstream `7b57e5a`).** `Settings.Window` and `Settings.Confirmations` lack `[Reactive]`, so `SetFrom` skips them. `SaveSettings` then runs straight after `LoadSettings` and overwrites the saved values with defaults on every launch (CLAIM-10). The fix is upstreamable.
2. **Order import can corrupt files.** Importing an order from an archive exported from "Current" can make a later Save write JSON into `modsettings.lsx`, or overwrite a saved order (HARV-31/32, AUDIT-PLAN-01).
3. **Path UUIDs reach `modsettings.lsx`.** An order JSON that contains force-loaded mods, once re-imported, writes an override pak's file path as a UUID. The next read-back throws and the order resets (CLAIM-15, HARV-52). Fix: a `fix/modsettings-write-guard` (stages/S6-final.md).
4. **Imports overwrite paks in place** at `MainWindowViewModel.cs:1845, 3302, 3429` (HARV-61). Fix: write to a temp file, then rename. This is a prerequisite for hardlink deployment.
5. **Archives of 2 GiB or more can't be imported** (HARV-27). **`.bz2/.xz/.zst` imports write 0-byte paks** (AUDIT-PLAN). **Cancelling an export corrupts the zip** (AUDIT-PLAN).
6. **Updater:** a failed check reports "up to date", and a cancelled or failed download still closes the app (CLAIM-16). The fork also checks upstream's feed (already in CLAUDE.md §8.1).
7. **Startup:** all mod loading runs serially on the UI thread. The first await on `Observable.Start(MainThreadScheduler)` captures the dispatcher, and there is no `ConfigureAwait` (CLAIM-07). Moving that work off the UI thread is Stage 4's first step.
8. **.NET 8 support ends 2026-11-10.** Targeting .NET 10 needs VS 18 (2026) Build Tools alongside 2022 (BLD-01/02, CLAIM-17).

## Corrections to your plan (verified)
- **Stage 5:** steps 1 and 2 save nothing. The setters already skip equal values, and the search allocates nothing per mod (CLAIM-03/04). The filter loop itself costs milliseconds. The measured 1.2–3.3 s is plausibly UI Automation, idle sampling, Debug JIT and container realization (CLAIM-05, Plausible). Start with a control run that has filtering disabled (stages/S5-final.md).
- **Stage 3 I1:** SharpCompress fills the buffer, so a short read isn't the cause. A likelier cause is zip entries with data descriptors reporting `Size=0`, which makes the order JSON be skipped silently (CLAIM-01).
- **Stage 4:** there's no MD5 hashing anywhere. Equal-version duplicate winners are effectively random.
- **E6** is moot (CLAIM-02).
- **MeasureText:** `=` doesn't throw; the misses come from non-Latin or emoji characters (CLAIM-06, Plausible).
- **CLAUDE.md:** 144 of 182 claims are correct.
  - `DebugModeEnabled` doesn't enable logging.
  - `IgnoreDependencies` isn't needed for new Larian modules.
  - There are 16 `ScheduleAsync` sites, not 22.
  - 168 of 185 `.cs` files are stored as LF, not CRLF.

  See `areas/AUDIT-CLAUDE.md` and `verified/CLAIMS-*`.
- **Filtered drops are safe today.** gong's index equals the model index under visibility filtering (CLAIM-14, Refuted). They only need fixing if you switch to collection-view filtering.

## Stage designs (judged panels; each cites code; claims are marked inferred where unconfirmed)
| File | What |
|---|---|
| stages/S1.md | Fork identity: update URLs, date-based versions, `Update.xml` checksum, release CI on `windows-2022`, pre-release checklist |
| stages/S4-final.md | Startup: get loading off the UI thread, then bounded parallelism, then a facts cache (path + size + mtime + header). Override and dedupe decisions are re-made every load (R1-safe). Estimated 1.5–5 s cold, 0.4–1.2 s warm, against 9.27 s |
| stages/S5-final.md | Filter: probe first, then index fixes, a collection-view filter and a 150 ms throttle. Fork-only fuzzy search (prototype in `prototypes/fuzzy-search/`, about 0.5 ms per keystroke over 1,121 mods) |
| stages/S6-final.md | 6.2/6.3: flat `ActiveRows`, a versioned `Layout` member in the order JSON, overrides block, the write-guard PR first |
| stages/LIB-final.md | **Stage L (your hardlink/versioning request):** a library of every version keyed by SHA-256, direct hardlink sync into Mods on export, per-order `Pins` map and `Deploy.Overrides`. Fork-only; goes after 6.2, before 8. The junction-swap mode comes later. In-game tests G1–G9 |
| stages/S7.md, S8.md, S9.md | UI refresh within AdonisUI; multi-order bundle format and friend sync; Nexus/mod.io/native-mod spec skeletons (about 12 Nexus-path bugs to fix first) |
| areas/AVA.md | Upstream avalonia: managed LSLib and Linux paths, but stalled; it carries most of E1–E6/I1–I5. Recommendation: stay on WPF, test under Proton, later a cross-platform Core plus a CLI |
| areas/BLD.md | Dependency audit, the .NET 10 migration plan, CI YAML, line-ending policy, `Publish|Any CPU` building lslib in Debug |

## Next local steps (suggested)
1. Fixes 1–6 above as small upstreamable `fix/*` PRs: settings restore, write guard, temp-then-rename, update errors, archive import/export.
2. Run the S4 P1 timing and the S5 Step-0 control run on Windows before any perf changes.
3. Re-run verification on `areas/LOAD`, `VM1`, `VM2`, `UI` and `SVC`. The scripts are reproducible from the workflow prompts. Then finish the Stage 6.1 branch review.

Notes:
- Agents downloaded some library sources (SharpCompress, gong, AdonisUI, Autoupdater.NET) to read them, plus the .NET 8 SDK to run the fuzzy prototype. Nothing from the repo was executed.
- Building the avalonia branch on Linux was blocked by the permission classifier and not attempted.
