# LIB-final: mod library, per-order deployment and version pinning

This document judges and combines three designs: `docs/review/stages/alternatives/LIB-A-hardlink-sync.md`, `docs/review/stages/alternatives/LIB-B-junction-swap.md` and `docs/review/stages/alternatives/LIB-C-model-lifecycle.md`. All three were present and complete. It was written 2026-11-03 against `master` = upstream `fc35358`. Status: design only. Nothing has been built or run, because this container cannot build WPF or run BG3.

Labels used throughout:
- **[code]** means I opened the cited `path:line` myself.
- **[src-web]** means the claim has a cited source (§16).
- **[unverified, Gn]** is a claim about BG3 or the in-game mod.io manager, together with the in-game test that settles it (§11).
- **[inference]** is my own reasoning.

Inputs: `docs/review/stages/S6-final.md` appeared while I was writing, and §13.3 follows it. `docs/review/stages/S4-final.md`, `docs/review/areas/LOAD.md`, `docs/review/areas/VM2.md` and `docs/review/areas/SVC.md` still do not exist (I re-checked at the end). For Stage 4 I used `S4-A-minimal.md`, `S4-B-pipeline.md` and `S4-C-architecture.md`, and I also used the S6 angle designs, `S8.md` and `S9.md`. §13 states each dependency so that it holds whichever S4 variant wins.

---

## Scorecard

Scores run from 1 (poor) to 5 (best) and judge each design *as written*. The reasons are in §1.

| | Data safety | Correctness (only the order's mods; right version per order) | Switch speed @ 1,097 paks | Robustness (game / mod.io / user drops) | Effort & blast radius | Linux-open | **Total /30** |
|---|---|---|---|---|---|---|---|
| **A** hardlink sync into `Mods` | 4 | **2** | 4 | 4 | 3 | 4 | 21 |
| **B** per-order slots + `Mods` junction | 3 | **2** | 5 | 3 | 2 | 4 | 19 |
| **C** model, identity, lifecycle (reuses A's engine) | 5 | 4 | 4 | 4 | 3 | 4 | **24** |
| **Final** (this document) | 5 | 5 | 4 | 4 | 3 (phased) | 4 | 25 |

**Verdict.** Build on **C's model**: content-hash identity, order-level pins, effective-version swap, library trash. Use **A's engine** with direct hardlink sync into a plain `Mods` folder as the only v1 deployment target. Keep **B's junction swap** behind a pluggable target interface as an optional later mode; it is not in the v1 PR series. Simplify C's pin serialisation to a single top-level `"Pins"` map (§5.2).

There are two findings the owner should know about before anything else:
1. **A's and B's version pins would be wiped by the app itself.** Both store the pin on `DivinityLoadOrderEntry`. But `UpdateOrderFromActiveMods` rebuilds `Order` from scratch with `Order.Clear()` followed by `AddRange(ActiveMods, true)` (`MainWindowViewModel.cs:4880-4888`), using `ToOrderEntry()`, which copies only UUID and Name (`DivinityModData.cs:299-306`). That rebuild runs 250 ms after *any* active-list change (`:5401-5408`) and as the first statement of every export (`:2901`). So a pin set in the UI is gone before the deploy reads it [code]. C found this (its F1). It is the main reason C scores higher.
2. **The owner's premise is independently sourced.** "A pak in `Mods` that isn't listed in `modsettings.lsx` is considered disabled but may still override vanilla files" comes from the Nexus Mods App's BG3 developer notes [src-web]. Removing a pak from the folder is therefore the only reliable way to stop it loading [unverified, G4 confirms on the owner's machine].

---

## 1. Judging the three designs

### 1.1 Code claims spot-checked

| Claim (design) | Verdict | Evidence |
|---|---|---|
| Three pak writers truncate in place with `File.Create` (A, B, C) | **Holds** | `DivinityFileUtils.cs:364` (used by `AddModFromFile`, `MainWindowViewModel.cs:1840-1845`), `MainWindowViewModel.cs:3302`, `:3429` |
| Mods scan is recursive (A §3.1) | **Holds** | `DivinityModDataLoader.cs:756-761`, `DivinityFileUtils.cs:12-17` (`RecurseSubdirectories = true`) |
| A no-meta override gets `UUID = pakPath` (all) | **Holds** | `DivinityModDataLoader.cs:612-620` |
| Duplicates: strict `>` on Version64, ties fall to `ConcurrentBag` order, losers go to the delete dialog (A §6, C F6) | **Holds** | `DivinityModDataLoader.cs:787-806`; `MainWindowViewModel.cs:1694-1703` → `DeleteMods(..., true, ...)` `:4359` |
| `Clone()` is shallow (A, C) | **Holds** | `DivinityLoadOrder.cs:304-312` (`Order = this.Order.ToList()`) |
| `Order` is rebuilt from `ActiveMods` (C F1) | **Holds; decisive** | `:4880-4888`, `:5401-5408`, `:2901` |
| Pins can live on the entry and `Update` won't clear them (A §4.1, B §5.1) | **Rejected** | Same evidence. `Update` (`DivinityLoadOrder.cs:236-253`) is beside the point because the entries are replaced wholesale |
| Order selection skips `LoadModOrder` when the UUID sequences match (C F2) | **Holds** | `MainWindowViewModel.cs:5202` (`OrderEquals`, `DivinityLoadOrder.cs:295-302`). Two orders that differ only in pins would not swap versions. A and B miss this |
| Refresh restores entries only (C F4) | **Holds** | `:2347-2352` captures `Order.ToList()`; `:2458-2461` `SetOrder(lastActiveOrder)` |
| Order save truncates in place (C F12) | **Holds** | `DivinityModDataLoader.cs:1105` `FileMode.Create` |
| Every `*.json` under Orders is an order, recursively (C F10) | **Holds** | `DivinityModDataLoader.cs:1153-1157` |
| `AddImportedMod` already swaps one UUID's instance (C F11) | **Holds** | `MainWindowViewModel.cs:3172-3227` (`ActiveMods.Replace`, `order.Update`, `mods.AddOrUpdate`) |
| Export sets the game's selected profile (C §6.3) | **Holds** | `:2955` |
| Archive export adds *all* overrides (A, C) | **Holds** | `:3572-3573` |
| `ForceLoadedMods` filter (all) | **Holds** | `:5244-5246`. It excludes `ForceAllowInLoadOrder` mods |
| `JunctionPoint.OpenReparsePoint` checks `GetLastWin32Error()` instead of `IsInvalid` (B) | **Holds** | `JunctionPoint.cs:291-298` |
| `JunctionPoint.Create(overwrite:true)` issues `FSCTL_SET_REPARSE_POINT` on the existing directory (B) | **Holds** for the code. Whether this retargets atomically is **[inference]**, test W-J1 | `JunctionPoint.cs:100-151` |
| "Game running" check exists only as a comment (A, B) | **Holds** | `:5424-5425` |
| `SetGamePathways` creates `Mods` if missing (B B17) | **Holds** | `:1365-1369`. Whether `Directory.Exists` is false on a dangling junction is B's **[inference]**. I expect it is *true*, because attributes come from the reparse point itself. Test W-J2 |
| `Mods_Old_ModManager` is created by unused code (B, S9) | **Holds** | only `ModUpdatesViewData.cs:195`, which is dead UI per CLAUDE.md §6 |
| `modsettings.lsx` `MD5` "stays empty" (C §3.2) | **Corrected** | It writes whatever `meta.lsx` declared (`DivinityModDataLoader.cs:1482`, `mod.MD5 ?? ""`; read at `:231`). No *file* hash exists anywhere, which is B's wording and is right |
| `Data`-path `DeleteFilesViewData` recycles `FilePath` (C F9) | **Holds**, with a note | `DeleteFilesViewData.cs:87`. In `DEBUG` builds nothing is deleted at all (`#if DEBUG` at `:84`), so test deletion only in Release/Publish (plan R5) |
| Upstream avalonia has a move-based cousin (B) | **Holds** | wt-avalonia `ModOrderViewModel.cs:763-800`: on export (`:816`), inactive user paks are *moved* to a sibling `Mods_Disabled`, and only if that folder exists (`:769`). A no-meta override counts as inactive if it sits in `Mods_Disabled` (`:1481-1490`). `ModDataLoader.cs:1699` scans both folders. No versions, links or library |

A side finding, not part of this feature: `FindLoadOrderFilesInDirectoryAsync` sets `order.Name` *before* its null check (`DivinityModDataLoader.cs:1167-1168`). It belongs in a later upstreamable null-guard fix.

### 1.2 Per-design reasoning

**A, hardlink sync.**
- Strengths. It has the best engine of the three:
  - FileId, not the manifest, decides what counts as "ours";
  - the unlink precondition (`identity == library && linkCount ≥ 2`) is enforced in the executor;
  - a pure planner;
  - a write-ahead journal;
  - `modsettings.lsx` is written after the links, and the links are rolled back if that write fails;
  - adoption by hardlink, so migration copies nothing;
  - a correct and complete list of write-through hazards (§8.1).
- Weaknesses:
  - Entry-level pins are lost (above), and A misses F2 and F4.
  - It keeps the *latest* `DivinityModData` in the UI and resolves pins only at export. The version, dependencies and `FilePath` shown in the list are therefore wrong for pinned rows, and archive export would zip the wrong bytes unless every consumer goes through the resolver.
  - The variant id `<Version>_<quick fingerprint>` is a second identity system next to the background SHA-256, and pins carry three alternative keys.
  - The fallback chain (Move, then symlink, then copy) multiplies the safety states. Move mode in particular makes the library incomplete while a variant is deployed.
  - Scores: correctness 2 (R1 right, R2 broken), safety 4.

**B, junction swap.**
- Strengths:
  - Switching is the fastest: a pre-built slot plus one retarget.
  - The library can sit on any local volume.
  - Files dropped into `Mods` are attributed to the active order.
  - It provides an app-free restore script.
  - Its own §12 honestly concludes that A should be the default.
- Weaknesses:
  - The same pin loss as A.
  - `VariantId = <Version64>`, or `<Version64>-<sha8>` only *after* a collision. The first build's id therefore never says which bytes it is, which is weak for Stage 8/9 sync.
  - Migration renames the real `Mods` to `Mods.pre-library-*` and replaces it with a reparse point. How the game, the in-game mod.io manager, Patch-era folder migrations and third-party tools treat a junctioned `Mods` is all unverified.
  - If the library root is deleted, the game sees *no* mods (dangling junction). Under A the game still sees a full `Mods` of real links.
  - Quarantine-moving foreign files out of the slot is a move of user data.
  - At this scale the speed gain is milliseconds against about a second (B §12).
  - Scores: safety 3, robustness 3, blast radius 2.

**C, model and lifecycle.**
- Strengths:
  - It is the only design that traces the order lifecycle (F1 to F5, F12) and fixes it.
  - Two-level identity: `ModKey` (meta UUID or `ovr:<hash16(file name)>`) and `ContentId` (full SHA-256, computed once during ingest).
  - The effective-version swap means `BuildOutputList`, archive export and the UI all see the deployed build with no change to those functions.
  - "Current" derives its versions from what is really deployed (observed pins).
  - Explicit lifecycle verbs, a library trash, a GC rule that never touches pinned, labelled or deployed versions, and an inventory receipt plus `restore-mods.cmd`.
  - It flags the Stage 4 key and the S8 `modKey` asks early.
- Weaknesses:
  - Pins are split between an entry carrier and a top-level map, kept in sync by `[OnSerializing]`/`[OnDeserialized]` callbacks. C marks these **[verify]** itself, and they add machinery the problem doesn't need (§5.2).
  - It blocks with `GetResult()` on the UI thread in `ApplyEffectiveVersions`, although it notes the async preload fix.
  - It relies on A for every file-system detail.
  - Score: correctness 4.

---

## 2. What the final design is

| Decision | Choice | From |
|---|---|---|
| Deployment mechanism | **Pluggable `IDeploymentTarget`.** v1 ships only `DirectHardlinkTarget`: `Mods` stays a plain folder, and only the order's files are hardlinks there. `JunctionSlotTarget` (B) is an optional later PR. No Move, symlink or copy fallback in v1: when hardlinks are impossible, the feature says why and stays off | A engine, B as option |
| Library location | `%LOCALAPPDATA%\BG3ModManager\Library`, on the same volume as the *final* `Mods` path (junction-resolved) | A §3.1, C §4.1 |
| Identity | `ModKey` (which mod) + `ContentId` = full SHA-256 (which build). Version64 is for display and ordering only | C §3 |
| Pins | Order-level `DivinityLoadOrder.Pins` (ModKey → pin), serialised as **one top-level `"Pins"` map**. Strict resolution, never a silent fallback | C §5.2, simplified |
| Override membership per order | A new optional top-level `"Deploy": { "Overrides": [ { "Key", "Name" } ] }` keyed by ModKey (never a path). The three LIB designs proposed S6-A `Layout` override rows, but `S6-final.md` §5.2 persists **no** override membership and rejects position rows (§2.2). Membership is a fact about deployment, not layout, so it gets its own member (§13.3) | new; replaces A §12, B §5.1, C §10.2 |
| UI model | One row per UUID. The row holds the **effective version for the selected order**, swapped in `LoadModOrder` after an async preload | B §6.4, C §5.4 |
| Trigger | Deploy only on **Export Order to Game** (and Save on Current, which calls it: `:2624-2629`). Order selection never touches the disk | all three |
| Deletion | A library trash (same-volume rename), then the Recycle Bin. Never a permanent delete without a second confirmation | C §8 |
| Scope | Fork-only (§14), with three upstreamable prerequisites | all three |

---

## 3. Deployment target interface and the v1 target

All of this is Core, `src/Core/Library/`, namespace `DivinityModManager.Library`, file-scoped, unprefixed types. It uses no WPF types, so an Avalonia port keeps it.

```csharp
namespace DivinityModManager.Library;

public interface IDeploymentTarget
{
	string Kind { get; }                                  // "DirectHardlink" | "JunctionSlot"
	TargetCapability Probe(string modsPath, string libraryPath);
	ModsSnapshot Snapshot(CancellationToken token);       // one enumeration + FileIdentity per top-level *.pak
	DeployResult Apply(DeploymentPlan plan, DeploymentJournal journal, CancellationToken token);
	DeployResult Undeploy(UndeployRequest request, DeploymentJournal journal, CancellationToken token);
}
```

- **`IFileOps`** is the file-system seam (A §2.1): `GetIdentity` (128-bit `FILE_ID_INFO` plus volume serial), `GetLinkCount`, `CreateHardLink` (`CreateHardLinkW` with the `\\?\` prefix), `DeleteName`, `SetReadOnly`, `Move`, `SnapshotDirectory`.
  - `WindowsFileOps` is the v1 implementation. `UnixFileOps` comes later: `link(2)`, `stat` `st_dev`/`st_ino`/`st_nlink`, `rename(2)`.
  - The planner is pure and the executor sees only `IFileOps`, which allows fault-injection tests to run on Linux.
- **The probe is empirical, never a path comparison:**
  1. compare `VolumeSerialNumber` of handles on both directories (after `GetFinalPathNameByHandleW`);
  2. create `Library\.probe`, then `CreateHardLink(Mods\.bg3mm-probe.tmp, …)`, then delete both;
  3. if `Mods` is itself a junction to another drive (setups that `DivinityRegistryHelper.cs:52-75` already resolves for game paths), the default library goes to `<that volume>\BG3ModManager-Library`.
- **Why not `Mods_Disabled` or anything else inside or beside the Larian folder:**
  - Upstream avalonia scans `Mods_Disabled` as user mods (`ModDataLoader.cs:1699` there), so every stored version would appear as a duplicate if the owner ever ran that build.
  - "Delete the Larian folder" is common troubleshooting advice [src-web, A §3.1].
  - The app's own folder is portable and often on another drive (`README.md:13`).
  - Orders must not contain library JSON (F10).

---

## 4. Library layout and identity

### 4.1 Tree (C §4.2, adopted)

```
%LOCALAPPDATA%\BG3ModManager\Library\
	README - what this is.txt            real size on disk, how to undo, never delete while Mods holds links
	library.json                          index cache; rebuildable from version.json files
	hashcache.json                        (path, size, mtime, FileId) -> sha256, for files not yet in the library
	manifest.json                         advisory: what is deployed now (FileId is the authority)
	.lock                                 FileShare.None while mutating (no single-instance guard: CLAUDE.md §3)
	inventory\<ts>.tsv, <ts>\PlayerProfiles\...\modsettings.lsx, restore-mods.cmd
	journal\                              in-flight deploy only
	staging\                              ingest lands here, then one rename into mods\
	trash\<ts>_<keyDir>_<cid8>\           deleted versions, restorable
	mods\
		CombatExtender_c1f3a9e2\          <SafeFolder ≤ 40>_<sha256(ModKey)[0..8]>
			mod.json                      ModKey, display name, Preferred, AliasOf, notes
			9e8f7a6b1c2d\                 ContentId[0..12]; equality is always decided on all 64 hex digits
				CombatExtender.pak        read-only attribute; same FileId as Mods\CombatExtender.pak while deployed
				version.json
		_ovr_NoCameraClamp_e019b2c4\77aa11bb2233\NoCameraClamp.pak
```

### 4.2 Identity rules

- `ModKey = HasMetadata ? UUID : "ovr:" + hex(SHA256(lower(file name without part suffix)))[0..16]`.
  - It is never a path, so no Windows user name leaks into orders or bundles. This is S8's P1 fix, and **S8 must adopt this exact formula**.
  - The in-memory `UUID` of a no-meta override stays `Path.Combine(AppDataModsPath, deployName)`, the same string as today (`DivinityModDataLoader.cs:620`), so `ForceAllowInLoadOrder` state and existing orders keep matching.
- `ContentId` is the SHA-256 of the pak. For a multi-part pak it is the SHA-256 over `name\0size\0sha256\n` per part, in part order (C §3.2).
  - It is computed in the same pass that copies bytes during ingest (`IncrementalHash`). Only migration needs a separate read pass, once.
- **Hash versus Version64.** Version64 can't be the identity. Authors ship new bytes under an unchanged Version64, and today's loader then picks a winner effectively at random (`:787-806`). This is the case that pins exist for.
  - A's quick fingerprint is dropped: one identity system, not two.
- **Default version** per ModKey (C §3.4): `Preferred` if the user set one, else the highest Version64, then the newest `IngestedUtc`, then the ContentId string. This order is total and deterministic.

### 4.3 What a full hash costs at migration

27 GB at disk speed is about 15-25 s on NVMe, about 1 min on a SATA SSD and 3-4 min on an HDD [inference, T-P1]. It is resumable through `hashcache.json`, cancellable, and shows progress. That cost is paid once. Every later import hashes while it copies.

---

## 5. Data model changes

### 5.1 `DivinityModData` (`src/Core/Models/DivinityModData.cs`)

The new fields are `[Reactive] string ModKey`, `ModVersion LibraryVersion`, `int VersionCount`, `bool IsVersionPinned`, `bool IsPinUnresolved` and `bool HasOtherDefaultVersion`, plus an OAPH `VersionBadgeText` built through `ToUIProperty` (CLAUDE.md §4). No filter predicate reads them, so no `AutoRefresh` is needed.

In library mode `FilePath` is the library file of the effective version, which always exists and is immutable. That makes "Open in Explorer", extract, zip export (`:3598`) and Stage 8 correct whether or not the version is deployed.

A new `CopyRowStateFrom(DivinityModData)` carries the non-version row state across a swap: `NexusModsEnabled`, `NexusModsData`, `WorkshopData`, colours, `ForceAllowInLoadOrder`, `Visibility`, `CurrentExtenderVersion`. The list is C §5.4's, and it must be re-audited against the 6.1 branch (`wt-perf`) before L6.

### 5.2 Pins: order-level, one top-level map

This replaces A §4.1, B §5.1 and C §5.2's entry carrier.

```csharp
[DataContract]
public class VersionPin
{
	[DataMember(EmitDefaultValue = false)] public string Sha256 { get; set; }     // authoritative
	[DataMember(EmitDefaultValue = false)] public string Version { get; set; }    // display, and the loose fallback
	[DataMember(EmitDefaultValue = false)] public ulong Version64 { get; set; }

	public VersionPin Clone() => new() { Sha256 = Sha256, Version = Version, Version64 = Version64 };
}

// in DivinityLoadOrder
[DataMember(Name = "Pins", EmitDefaultValue = false)]
public Dictionary<string, VersionPin> Pins { get; set; }                         // ModKey -> pin; null when none

[JsonExtensionData] private IDictionary<string, JToken> _extra;                  // newer fork fields survive a re-save
```

Why a single top-level map:
- `Order` stays exactly today's `{UUID, Name}` shape, so `UpdateOrderFromActiveMods` and any S6 projection can rebuild it freely without touching pins. This fixes F1 by construction.
- No serialisation callbacks are needed, which removes C's [verify] item.
- No-meta overrides and auto-added dependencies, which have no `Order` entry, need no special case.
- It is the same shape S8's manifest `pins` should carry.

`EmitDefaultValue = false` plus a null map keeps unpinned orders **byte-identical** to today. That is unit test T-U3.

Lifecycle fixes, all required for R2 (C §5.5):

| Site | Change |
|---|---|
| `DivinityLoadOrder.Clone()` `:304-312` | deep-copy entries and `Pins` (F3) |
| `SetOrder(DivinityLoadOrder)` `:289-293` | also copy `Pins`. `SetOrder(IEnumerable<entry>)` leaves `Pins` alone |
| Order selection `:5202` | `if (!OrderEquals(...) \|\| !PinsEqual(_loadedPins))` → `LoadModOrder` (F2) |
| `RefreshAsync` `:2347-2352`, `:2458-2461` | capture and restore `Pins` next to `lastActiveOrder` (F4) |
| `AddNewModOrder` `:2055-2061` | copy `Pins` from the order it was created from |
| Export, "Current" update `:2964-2968` | `SetOrder(...)` plus observed pins (§5.4) |
| Order save `DivinityModDataLoader.cs:1098-1113` | write a temp file, `Flush(true)`, then `File.Move(tmp, path, true)` (F12). This is upstreamable (U2) |
| Pins of mods removed from the order | kept in memory for the session, pruned at save, except for keys in this order's deploy set (overrides, auto-dependencies) |

**Resolution** (C §5.3):
1. `Sha256` matches a library version: resolved.
2. Else, if exactly one version has that `Version64`: resolved *loose* (dotted glyph; "Make exact" fills in `Sha256`).
3. Otherwise **unresolved**. The row warns and the deploy blocks with "Use default for these N", "Import file…" or "Cancel". There is never a silent fallback to latest.

### 5.3 Effective-version swap

B §6.4 and C §5.4 agree on this mechanism.
- The order-selection handler (`:5196`) first awaits `library.PreloadAsync(order)` on the taskpool, which materialises any non-default version that is needed.
- `LoadModOrder` (`:2093`) then calls `ApplyEffectiveVersions` on the main thread before its activation loop at `:2111`. It makes one `mods.Edit` holding only the UUIDs whose effective `ContentId` differs, typically 0-30.
- `ActiveMods`/`InactiveMods` are rebuilt from `addonMods` at `:2141-2144` and pick up the new instances. `addonMods` is bound without `ObserveOn` (`:5243`), so it updates synchronously [code].
- There is no blocking `GetResult()` on the UI thread. On a cache miss in `LoadModOrder`, the swap is skipped, an alert is raised, and a re-load is scheduled after the preload.

### 5.4 "Current" and observed pins

`modsettings.lsx` records UUID and Version64 but no hash (`DivinityApp.cs:33`).
- At startup the library reads `manifest.json` and confirms each managed `Mods` name by FileId. The deployed versions become *observed pins* on the Current order, so Current shows what the game will actually load.
- A foreign or drifted file is ingested first, and its ContentId becomes the observed pin.
- "Save As" from Current turns observed pins into real ones. That is the natural "freeze what I'm playing" action.

### 5.5 Order JSON example and compatibility

```json
{
	"Order": [
		{ "UUID": "c1f3a9e2-5f0b-4b1e-9a0e-2f1c3d4e5f60", "Name": "Combat Extender" },
		{ "UUID": "77e0c0de-0000-4000-8000-000000000001", "Name": "Better Hair" }
	],
	"Pins": {
		"c1f3a9e2-5f0b-4b1e-9a0e-2f1c3d4e5f60": { "Sha256": "a1b2…(64)", "Version": "1.4.2.0", "Version64": 36169534507319296 },
		"ovr:3f9a1c0b7d2e4a55": { "Sha256": "77aa…(64)" }
	},
	"Deploy": {
		"Overrides": [
			{ "Key": "ovr:3f9a1c0b7d2e4a55", "Name": "NoCameraClamp" },
			{ "Key": "e5c9f00d-1111-4222-8333-444455556666", "Name": "Unlock Level Curve" }
		]
	},
	"Layout": { "Version": 1, "Rows": [ { "Kind": "Category", "Id": "6f0c…", "Name": "Gameplay", "Before": "c1f3…", "Position": 0 } ] }
}
```

`Deploy` is `[DataMember(EmitDefaultValue = false)]` and null for legacy orders. The class carries `[Newtonsoft.Json.JsonExtensionData]`, matching S6-final's `OrderLayout` discipline. `Deploy == null` means "membership unknown", which is the legacy-order case in §7.1. An empty list means "no overrides". `Name` is for humans and diagnostics only; matching is by `Key`. `Layout` is S6-final's shape and is unchanged by this feature.

| Reader | Behaviour |
|---|---|
| Upstream master, or a fork build before this feature | Ignores `Pins`, `Deploy` and `Layout` (Newtonsoft's default `MissingMemberHandling.Ignore`; `DivinityJsonUtils.cs:30-38`). The order loads as "latest". **If it re-saves, pins are dropped.** Documented; unavoidable for upstream. `[JsonExtensionData]` on `DivinityLoadOrder` in the first Stage L PR that touches the format (L4) protects every later fork build |
| Upstream avalonia | Migrates `Order` to `Entries` and keeps unknown members in `Extras` [inference, S6-A §3, S6-final §11]. The fork never writes `Entries` |
| This build, library off | Reads and keeps pins (pass-through), applies none |
| This build, library on, old file | No pins: every mod follows its default version. That is today's meaning |

### 5.6 Settings (`DivinityModManagerSettings`: `[DataMember]`, `[Reactive]` and `[SettingsEntry]`, CLAUDE.md §4)

| Setting | Default | Visible |
|---|---|---|
| `LibraryEnabled` | false | read-only in Preferences; changed only by the wizard or by Undeploy |
| `LibraryPath` | "" (automatic) | hidden (`HideFromUI`); set by the wizard |
| `DeployPreview` | `WhenRemovingOrAdopting` | yes (`Always`/`WhenRemovingOrAdopting`/`Never`) |
| `ProtectLibraryReadOnly` | true, revisited after G3/G7 | yes, with a warning tooltip |
| `DeployScope` | `ActiveOrder` | yes (`ActiveOrder`/`UnionOfProfiles`, §7.5) |
| `FreezeOrdersOnNewVersion` | false | yes (Phase 4, with S9) |

---

## 6. Ingest: the single entry point for new bytes (C §7)

| Today | Library mode |
|---|---|
| `AddModFromFile` copies into `Mods` (`:1840-1845`) | `library.IngestFileAsync(path)` |
| `ImportCompressedFileAsync` `File.Create` into `Mods` (`:3302`) | `IngestStreamAsync(decompressed)` |
| `ImportArchiveAsync` `File.Create` into `Mods` (`:3429`) | `IngestStreamAsync(entry)` |
| Stage 8 bundle import | `IngestStreamAsync`, skipped when the manifest's sha256 is already present. The manifest's hash is trusted only for *skipping*, never for deleting |
| Stage 9 downloader | `Data/Downloads/…/*.part` (S9 §2.5), then `IngestFileAsync`. **Never** writes into `Mods` |
| The user, the in-game mod.io manager [unverified, G7], or Vortex drop a pak into `Mods` | detected as Foreign at the next refresh or deploy, then `AdoptAsync` |

`IngestStreamAsync`:
1. Write to `staging\<guid>\<name>.tmp` with WriteThrough, hashing while copying, then `Flush(true)` and rename.
2. Parse with `LoadModDataFromPakAsync` (`DivinityModDataLoader.cs:731`). If parsing fails, keep the file in `staging\failed\` and raise `ShowAlert(Danger)`.
3. If the ContentId exists (directory present *and* all 64 hex digits equal), delete *our own* staging copy and add the name to `KnownNames`.
4. Otherwise `Directory.Move` the staging folder to `mods\<keyDir>\<cid12>` (one rename on the same volume), write `version.json` (temp + rename) and set read-only.

After the ingest the caller runs today's `AddImportedMod`. The row swaps to the new version only if the selected order follows the default and the new version *became* the default.

`AdoptAsync` (a file already in `Mods`) also follows C §7.3:
- Look up the hash in the hash cache, or hash the file with a read-only open (`FileShare.Read|Delete`).
- If the hash is already in the library, the file is a byte-identical *copy*. The plan marks it "replace with link", and the deploy unlinks it only after a fresh full-hash compare.
- Otherwise `CreateHardLink(staging\…, modsPath)`, verify the identity and a link count of at least 2, parse, then rename into `mods\`. `Mods` is never opened for writing, moved or renamed.

---

## 7. Apply and undeploy

### 7.1 Desired set D for order O and profile P

1. `BuildOutputList(O.Order, mods.Items, Settings.AutoAddDependenciesWhenExporting, adventure)` (`DivinityModDataLoader.cs:1431-1469`). Because of §5.3, `mods.Items` already holds O's effective versions, so the auto-dependencies are those of the pinned builds. Dependencies not in `O.Order` resolve through `O.Pins`, else the default.
2. Drop base-game mods (`IgnoredMods`) and editor mods (`IsEditorMod`, which live in `Data\Mods`).
3. Add O's overrides: every `Deploy.Overrides[i].Key` that resolves to a library ModKey whose version is force-loaded and not merged (`IsForceLoaded && !IsForceLoadedMergedMod`; merged overrides are ordinary order entries). For a **legacy order** (`Deploy == null`), the preview asks once; the default is "keep the overrides that are deployed now" (A §5.3). The answer is written back into `Deploy`. A K2 override with `ForceAllowInLoadOrder` is already in `Order` (S6-final §9.1), so it is deployed as an ordinary entry.
4. Resolve every member to a `ModVersion`. D maps `deployName → (version, file)` for every file, including every part of a multi-part pak.
   - Two different ModKeys that want the same file name: **blocking**, with both listed.
   - C's rename-on-conflict is deferred until G10 shows that file names don't matter.

### 7.2 Classify every top-level `*.pak` in the snapshot (A §5.2)

| Class | Test | Action |
|---|---|---|
| Managed | FileId equals a library file's FileId | none yet |
| Foreign | no FileId match, and the name isn't in the manifest | **ingest first** (adopt) |
| Drifted | the manifest lists the name, but the FileId differs (replaced by mod.io, Explorer, etc.) | ingest first, as a new version |
| Tampered | FileId matches, but size or mtime *read through a handle* differs from `version.json` | Danger alert: written through in place. Re-hash. Never unlink as "ours" until verified |

Subfolders and non-pak files are recorded and **never touched**. Subfolders raise a warning (`README.md:21`; G11).

### 7.3 Plan (pure function)

```
for each managed name n in S:
	if n not in D                         -> Unlink(n)
	else if FileId(n) != FileId(D[n])     -> Unlink(n), Link(n <- D[n])   (version swap, same name)
for each n in D not in S                  -> Link(n <- D[n])
op order: Ingest*, Unlink*, Link*         (unlink before link, so swaps under the same name work)
```

Each issue below is either blocking or non-blocking:
- **Blocking:** unresolved pin; name collision; a member with no library version; ingest of a foreign file failed. In that last case the file stays in place, and the user must accept "leave X in Mods" because it may be an override that defeats R1.
- **Non-blocking:** an override present now but not in D (shown in red; this is the owner's case); another profile's `modsettings.lsx` references mods that will be removed; subfolders present.

### 7.4 Apply (`DeploymentExecutor`)

```
0  GameProcessGuard: bg3.exe / bg3_dx11.exe (same names for GOG and Steam) -> refuse. Take Library\.lock.
   Mods path == manifest.ModsPath, else refuse and offer to redeploy into the new path (patch moved it).
1  Re-snapshot. If it differs from the plan's snapshot -> re-plan; re-show the preview if the diff changed.
2  Back up every profile's modsettings.lsx to inventory\<ts>\ (G8 insurance).
3  Write journal\<ts>.json (ops + preconditions), WriteThrough + Flush(true).
4  For each op: check precondition -> perform -> verify postcondition by FileId -> append "done".
	Unlink precondition (executor-level assert, not planner logic):
		FileId(Mods\x) == FileId(libraryFile) && GetLinkCount(libraryFile) >= 2
	Read-only: DeleteFile fails on a read-only file (DeleteFileW docs [src-web]), so: clear RO,
	delete the Mods name, set RO again through the LIBRARY name (attributes are per file and the
	directory entry of the deleted link is stale; MS hard-links docs [src-web]).
5  Write modsettings.lsx exactly as today (ExportModSettingsToFileAsync, :2932). On failure -> roll back links.
6  Write manifest.json (tmp + rename): ModsPath, Profile, order name/file, per name: ModKey, ContentId, FileId.
7  Delete the journal, release the lock, update Current with observed pins,
   ShowAlert(Success, "Deployed 'X': +31 -188 ~4 switched, 702 unchanged").
Any failure: roll back done ops in reverse (Link -> delete new name; Unlink -> CreateHardLink from library,
always possible because of the precondition). Ingest is not rolled back: it only added library names.
ShowAlert(Danger, 0) plus a modal naming the file and the Win32 error. modsettings.lsx is untouched.
Crash: the next RefreshAsync (:2340) finds the journal. Recovery decides done/not-done from FileIds
(the "done" marks are only hints) and offers roll back (default) or roll forward.
```

`ExportLoadOrderAsync` hook (`MainWindowViewModel.cs:2930-2932`): compute `finalOrder` as today. In library mode, plan, then show the preview (marshalled to the UI thread), then apply, with the `modsettings.lsx` write passed in as the step 5 callback. Without the library, behaviour is unchanged. The preview's **"Export without deploying"** button keeps today's behaviour as an escape hatch, and the status chip then shows "Mods not matching order". Progress goes through `MainProgress*` wrapped in `RxApp.MainThreadScheduler`, because Fody setters raise on the calling thread (CLAUDE.md §4). Every catch calls `ShowAlert`, because the repo has no `ThrownExceptions` subscriptions (plan R8). The `modsettings.lsx` watcher is paused during apply, as at `:2629`.

### 7.5 Profiles share one `Mods` folder

Deployment is global; `modsettings.lsx` is per profile.
- The export already makes the exported profile the game's selected profile (`:2955`).
- The preview warns: "Profile 'X' lists N mods that will not be in Mods."
- Whether the game rewrites that profile's `modsettings.lsx` when the player switches profiles in game is [unverified, G8]. The step 2 backup and the saved order JSON make any rewrite recoverable.
- `DeployScope = UnionOfProfiles` deploys the union of each profile's last exported order. It trades R1 isolation for safe in-game profile switching (owner Q3).

### 7.6 Undeploy and the way back

| Action | Effect | Data risk |
|---|---|---|
| **Restore full Mods folder** | Deploys the default version of every ModKey plus every override: today's state minus extra versions. The option "all versions under their original names" restores the inventory name-for-name, duplicates included | none |
| **Disable library** | Delete the manifest and stop deploying. `Mods` keeps what is deployed, as ordinary files (a hardlink is a full peer name). Orders keep their pins | none |
| **Remove library from disk** | Per file: a link count of at least 2 means the data is also in `Mods`, so deleting the library name loses nothing. A link count of 1 means the file is moved to `…\Mods_Library_Export\` or the trash. Never a permanent delete | none |
| **App broken or uninstalled** | `Mods` still works (real names). `inventory\restore-mods.cmd` holds one `mklink /H` per default version, `if not exist`-guarded and add-only. It needs no elevation [src-web: CreateHardLink needs only NTFS and the same volume] | none |

---

## 8. Robustness to other actors

| Actor / event | Behaviour |
|---|---|
| The in-game mod.io manager **installs** a pak into `Mods` [unverified, G7] | Foreign, so it is ingested at the next refresh or deploy. A `*.pak` watcher on `Mods` (`FileWatcherService.cs:104`) shows a badge only and never acts on its own |
| mod.io **updates** a pak | If it replaces the file (new FileId): Drifted, ingested as a new version, and the old version survives. If it rewrites **in place**: with RO on, the update fails (record what the game does). With RO off, it is Tampered: a Danger alert, re-hash, and the old bytes are gone. That is why RO defaults on and why G7 blocks default-on |
| mod.io **uninstalls** | One `Mods` name disappears; the library copy survives. Drift report: "N mods were removed from Mods by another program: re-deploy / remove from order" |
| The user deletes from `Mods` in Explorer | Same as an uninstall. RO makes Explorer prompt first |
| The user copies a pak into `Mods` | Foreign, ingested |
| A tool recreates `Mods` | The folder is plain either way under the direct target. The next deploy re-links |
| A patch moves `Mods` (Patch 7 did, from Documents to AppData [src-web, single forum report]) | The manifest's `ModsPath` no longer matches, so deploy refuses and offers to redeploy into the new path. The library doesn't move |
| The game writes into `Mods` | [unverified, G6]. Expected: nothing |
| A bad pak in `Mods` makes the game reset the whole order | Community-reported [src-web, Reddit Patch 7 troubleshooting; unverified, G12]. This feature *reduces* that risk, because undeployed paks are absent |
| `ModCrashSanityCheck` | The game creates a folder by this name beside `Mods`. The manager already deletes it before export (`:2876-2895`) and the library never touches it |
| Vortex in the same folder | Detect `vortex.deployment*.json` / `__folder_managed_by_vortex` in `Mods` and refuse (B B16) |
| Two manager instances | `.lock` |
| Game running | The guard refuses before plan and before apply. Sharing violations roll back |

---

## 9. UI

| Where | What |
|---|---|
| Mod row, Version column (`HorizontalModLayout.xaml`) | `VersionBadgeText` (`1.4.2.0 · a1b2c3d4` when `VersionCount > 1`) and a glyph: pin, dotted pin (loose), up-arrow (another default exists), warning (unresolved). Use the 6.1 cached-text element, not a new `TextBlock`, because the Version column is one of the four columns S6.1 is fixing (plan overview) |
| Mod context menu (`MainResourceDictionary.xaml:264-360`) | **Version ▸**: one checkable item per version (`1.5.0.0 · 9e8f7a6b · 48 MB · 2026-10-02 · default`), then `Follow default (unpin)`, `Pin to this version`, `Update pin to latest`, `Label…`, `Delete this version…`. Commands live in `DivinityGlobalCommands` |
| Menus (`AppKeys.cs`, `[MenuSettings]`, generated by reflection) | Order: `Pin all to current versions`, `Update all pins to latest…`, `Unpin all`, `Freeze all orders`. Tools: `Mod Library…`, `Set up mod library…`, `Undeploy (restore full Mods folder)…`. File: `Preview deployment…` |
| Overrides (S6-final's computed Overrides block, §9.1) | In library mode the block still lists **every** library override, as S6-final computes it. Each member gains a "deployed with this order" check glyph bound to `SelectedModOrder.Deploy.Overrides`. Unchecked members render dimmed, and the header tooltip changes from "Always loaded by the game" to "Only checked overrides are placed in the Mods folder". No drag gesture is involved, so S6-final's "no modsettings change from a drag" rule holds. Until 6.2 lands, the same toggle goes on the existing `ForceLoadedMods` pane |
| Status bar / order bar chip | "Deployed: Tactician (Public) · 935 mods · 3 pinned", or "Selected order not deployed". `StatusBarRightText` is at `:4875` |
| `Keys.LaunchGame` (`:897`) | Warn, without blocking, if the selected order isn't the deployed one or the snapshot shows drift |
| Deploy preview dialog | §7.3 counts; red REMOVE (overrides) lines; SWITCH lines ("Combat Extender 1.5.0.0 → 1.4.2.0 (pinned in 'Tactician')"); ADOPT lines; buttons "Deploy & Export", "Export without deploying", "Cancel" |
| Library window (`HideWindowBase<T>`, plus a line in `MainWindow.UpdateColorTheme`; both themes key-for-key) | Tree ModKey → versions, with columns Version, ContentId, size, ingested, label, used-by orders, deployed. Actions: default, label, trash, restore, clean up unused, verify (re-hash), rebuild index, true disk usage (Explorer shows `Mods` and `Library` both at full size) |
| Delete Mod (`DeleteMods` `:4359`) | In library mode, routed to the library dialog: "Remove from this order" / "Delete this version" / "Delete all versions". Every option lists the orders that pin the version first. Bytes go to the library trash (C §8) |

---

## 10. Migration (enable wizard, C §9.1 + A §7.1)

| Step | What happens | `Mods` changed? |
|---|---|---|
| 1 Preconditions | Game not running; lock; probe (same volume + hardlink); NTFS; free-space note ("extra disk now: 0 B; old versions you keep will use space") | no |
| 2 Receipts | Copy every profile's `modsettings.lsx`; write `inventory\<ts>.tsv` (name, size, mtime, FileId) | no |
| 3 Hash | Full SHA-256 of every pak and part; resumable, cancellable | no (read-only opens) |
| 4 Adopt | `AdoptAsync` per pak group. Duplicates become versions, and the startup delete-duplicates dialog (`:1694-1703`) is suppressed from now on | **no**: only library names are added |
| 5 Verify | Same names, sizes and FileIds as step 2; every pak has a library twin with link count 2. Shows "1,097 files in Mods unchanged · 1,097 adopted · N mods with several versions" | no |
| 6 Overrides question | "Add the N overrides loaded today to all K saved orders (keeps today's behaviour) / to none / let me choose." Writes `Deploy.Overrides`, backing each order up to `Orders\_premigration\` first. Existing `Order` entries whose UUID is a path under `Mods` (a forced K1, S6-C §2.4) are removed from `Order` and added to `Deploy.Overrides` by ModKey | no |
| 7 Manifest, `restore-mods.cmd` | `Deployed = "(unmanaged: everything)"` | no |
| 8 First deploy | Optional. The full preview shows what leaves `Mods`, overrides in red | yes, only after confirmation |

The wizard first runs on a **copy** of the real `Mods` folder, through `DocumentsFolderPathOverride` (`MainWindowViewModel.cs:1159`, `:1282`), then on the real one (T-A1).

---

## 11. Verification protocol

### 11.1 In-game (owner; Windows; Publish build per plan R5; back up `Mods` and `PlayerProfiles` first)

These gates must pass before `LibraryEnabled` can be offered outside the owner's machine. They merge A's G1-G10, B's G-1 to G-9 and C's P-OVR/G9-G11.

| # | Claim | Procedure | Pass | Gate for |
|---|---|---|---|---|
| G1 | The game loads hardlinked paks normally | Deploy an order (Phase 1), launch, load a save that uses several mods | mods active, no "missing mods" | Phase 1 |
| G4 | **The owner's premise.** An override in `Mods` but not in modsettings loads; absent from `Mods`, it doesn't | **P-OVR canary:** a tiny no-meta pak overriding one obviously visible base-game asset (build it with `DivinityFileUtils.CreatePackageAsync` `:132`, or use a visible override the owner already has). P1 library off, canary in `Mods`, not listed → visible. P2 deploy order A without it, `dir Mods` shows it absent → effect gone. P3 order B with the row → visible. P4 back to A, launch, quit, relaunch → absent both times | as described | Phase 1 |
| G3 | The game opens paks read-only and accepts the RO attribute | G1 with RO on, plus Process Monitor (`bg3.exe`, path contains `\Mods\`): record Desired Access | loads; no write access | RO default |
| G5 | Does the game keep paks open, and with what sharing? | `handle.exe -p bg3.exe Mods` at the main menu and in game; then try `del` on a deployed link | recorded; decides how strict the guard must be | guard |
| G6 | The game writes nothing into `Mods` | Before/after snapshot around a full session (names, FileId, size, mtime; "Verify library") | no change | Phase 1 |
| G7 | The in-game mod.io manager: install path, file names, whether an update replaces or rewrites in place, uninstall | Install, update and uninstall one mod.io mod with the library on; Process Monitor; then Verify library | library hashes unchanged; drifted file ingested as a version | default-on, if the owner uses mod.io |
| G8 | With two profiles, does switching to a profile whose list names undeployed mods make the game rewrite its `modsettings.lsx`? | Deploy A for Public; switch to profile 2 in game; diff its file against the backup | recorded; decides `DeployScope` advice | Q3 |
| G9 | The version swap is real | Order A pins X v1, order B follows v2. Export A, launch, check the in-game version (mod UI or SE console `Ext.Mod.GetMod("<uuid>").Info.ModVersion`), then B. **G9b:** same Version64, different bytes; tell them apart by a visible content difference | right build each time; modsettings Version64 matches | Phase 2 |
| G10 | Pak file names don't matter to the game | Deploy a pak under a different name | loads | only if rename-on-conflict is ever wanted |
| G11 | A subfolder in `Mods` resets modsettings (`README.md:21`) | optional | recorded | none (the library is outside `Mods` either way) |
| G12 | One unloadable pak makes the game clear the order (community report) | optional: a deliberately broken pak in `Mods` | recorded | documentation |
| G-J1..3 | Only for the optional junction mode: the game follows a junctioned `Mods`; mod.io into a junctioned `Mods`; a dangling junction at launch | B's G-1, G-4 and B8 | — | L9 only |

### 11.2 Windows integration (`src/Tests/Core.Tests`, `[Trait("os","windows")]`, NTFS temp dirs)

`src/Tests/Core.Tests` is the first test project in the repo (CLAUDE.md §8.4).
- W1: identity and link count; RO is shared across links; deleting an RO link fails until RO is cleared.
- W2: `File.Create` on a link truncates the shared data (proves the hazard). `File.Move(tmp, dest, true)` replaces only the name.
- W3: `MoveFileEx(REPLACE_EXISTING)` onto a **read-only** destination fails [inference], so library mode must never move onto a deployed name. This is why ingest goes to staging.
- W4: delete one link while another process holds the other name open, with and without `FileShare.Delete`.
- W5: the probe on NTFS, exFAT, ReFS/Dev Drive and a second NTFS volume.
- W6: paths over 260 characters through `\\?\`.
- W7: Recycle one link of a two-link file (data survives); Recycle an RO file.
- W8: `Mods` junctioned to D: → library defaults to D:.
- W9: kill the process mid-apply → journal recovery yields the before state or the after state, never a mix.
- W10: two instances → the lock refuses the second.

### 11.3 Unit tests (platform-neutral, fake `IFileOps` with an in-memory inode table)

- Planner tables: empty→full; full→empty; swap with the same file name; multi-part; foreign/drifted/tampered; collision; unresolved pin; legacy order; divider rows; no-meta overrides.
- Executor fault injection at op *k* for every *k*: the final state equals the initial one, and S1 never fires with a link count below 2.
- JSON: no-pin orders are byte-identical to today (T-U3); pins round-trip; an upstream-shape reader loads the file; `Clone`/`SetOrder` deep-copy.
- An `Order` rebuild (`Clear` + `AddRange`) preserves `Pins` (the F1 regression test).
- Default-version ordering is deterministic under shuffling.
- GC exclusions hold.

### 11.4 Performance (owner's PC; all estimates [inference])

| Operation | Estimate | Test |
|---|---|---|
| Snapshot of `Mods` (1 enumeration + 1,097 handle opens) | 30-150 ms; bulk `FileIdExtdDirectoryInfo` if over 200 ms | T-P1 |
| Typical switch (≈200 unlink + 200 link) | 0.1-0.5 s | T-P2 |
| Full deploy from empty (~1,000 links) | 0.2-1.2 s | T-P2 |
| Migration: hash 27 GB, then 1,097 adopts | ~15 s to ~4 min (disk-bound), then 2-10 s | T-P3 |
| Startup in library mode | today's per-pak path over ~1,100 default versions, i.e. ≈ today's 9.3 s cold. With the Stage 4 cache keyed by library path, a warm start has ~100 % hits (library files are immutable) | T-P4, with the R1 snapshot diff clean |
| Order switch with k swapped rows | k × (AddOrUpdate + CopyRowStateFrom); cold materialise 5-30 ms per pak | T-P5 |

---

## 12. Safety rules (consolidated, each enforced in code, not by convention)

| # | Rule | Enforced by |
|---|---|---|
| S1 | Never remove the last name of any pak | Executor assert: FileId match and link count ≥ 2. A byte-identical copy is unlinked only after a fresh full-hash compare. No `File.Delete` in `Core/Library` outside `SafeFileOps` (grep-able rule) |
| S2 | Unknown files are owned before anything is unlinked | Ingest-first (§7.3). On failure the file stays in place |
| S3 | No in-place writes to library bytes | U1 temp+rename on every manager writer; ingest to staging; RO attribute; tamper check by handle stats; "Verify library" |
| S4 | `Mods` and `modsettings.lsx` change together or not at all | Ordered apply, journal, rollback, `modsettings.lsx` written last |
| S5 | No file changes under a running game | Guard before plan and before apply |
| S6 | Pins never silently change meaning | Strict resolution; a version is never trashed without listing the orders that pin it (searching every `Orders\**\*.json`, backups, unsaved orders and Current's observed pins) |
| S7 | Every deletion is reversible at least once | Library trash, then the Recycle Bin. A permanent delete needs a second confirmation |
| S8 | Every failure is visible | Result objects, plus `ShowAlert(Danger, 0)` and a modal listing names. Nothing in `Core/Library` swallows exceptions. The success summary is alerted too |
| S9 | Losing `library.json`, `manifest.json` or a sidecar loses nothing | Rebuild from `version.json` files; FileId re-derives "managed"; a pak without a sidecar is re-parsed |
| S10 | There is always a way back | Inventory receipt, `restore-mods.cmd`, Restore full Mods, Export without deploying |
| S11 | Non-pak files and subfolders in `Mods` are never touched | Top-level `*.pak` snapshot only |
| S12 | Off means off | With `LibraryEnabled = false` the app behaves exactly as `master` (plan R1 snapshot diff) |

---

## 13. Where it goes in the stage plan, and what it changes

### 13.1 Placement

Add a **new Stage L, "Mod library and per-order deployment" (fork-only, `feat/lib-*`)**, scheduled **after Stage 6.2 and before Stage 8**. Phase 1 can start before 6.2 if the owner wants R1 sooner, because the toggle then goes on the old pane. Stage 7 can run before or after it; the library window is just one more themed window. I keep the existing numbers so that S8.md, S9.md and the plan's references stay valid.

Its dependencies:

| Dependency | Why | Hard or soft |
|---|---|---|
| U1 temp+rename for the pak writers (Stage 2/3 family) | Prevents write-through and truncated paks on cancel | **hard**, before any link exists |
| Stage 3 `fix/archive-import` | Edits the same functions (`:3391-3524`, `:3230-3330`). Land U1 inside it or right before it | coordinate |
| Stage 2 export restructure (S8 §5) | Library-sourced bytes for zip export | soft (needed for Phase 4) |
| Stage 6.2 (`S6-final.md` A1-A3) | Hosts the deploy toggle in the Overrides block. Membership itself lives in Stage L's `Deploy` member, so 6.2 is **not** a hard dependency: before 6.2, the toggle goes on today's `ForceLoadedMods` pane (`HorizontalModLayout.xaml:103-141`) | soft |
| S6-final PR-0 `fix/modsettings-write-guard` | Never writes a K1 path UUID into modsettings | **hard**, before L4 |
| Stage 4 cache | Fast library-mode startup; full-path key | soft (without it, cold-start cost ≈ today's) |
| Stage 6.1 cached text | Version-column badge | soft |
| Stage 1 | No public build before the updater points at the fork (plan R4) | for distribution only |

### 13.2 Stage 4 (S4-A / S4-B): asks

1. **Key the cache by full path, or keep one section per root.** S4-A keys `Path` relative to the Mods root under a single `RootHash` (S4-A §2.3), which can't hold library paths. S4-B (§2.4) and S4-C (§0, key "FilePath exactly as enumerated") already key full paths. This costs nothing upstream if decided before Stage 4 merges.
2. **Read stats from `FileInfo`/a handle, never from directory enumeration.** For hardlinked files the directory entry's size and mtime can be stale [src-web: MS hard links]. S4-B §2.6 already does this; S4-A should adopt it.
3. **Make partial-pak detection per directory.** `_AllPaksNames` is static, never cleared and global (`DivinityModDataLoader.cs:429`, `:758`). A `Foo_1.pak` in one library version folder could be masked by a `Foo.pak` elsewhere. This is also an upstreamable correctness fix on its own.
4. **Add `ModKey` and `ContentId` columns to the R1 snapshot harness**, so library mode can be shown to pick the same winner as the plain loader for every non-duplicate UUID.
5. **Share the `FileStamp` type** with the library's `hashcache.json`, but keep it in a separate file, because Stage 4 is upstreamable and the hash cache is fork-only.

### 13.3 Stage 6.2 (`S6-final.md`): what this changes and what it leaves alone

`S6-final.md` is now the 6.2/6.3 decision. Its row model is `ActiveRows` with a derived, mods-only `ActiveMods`. Its `Layout` envelope holds Category rows only, and it states plainly: "**No override membership is persisted.** The Overrides block's members are always computed from the installed `ForceLoadedMods`" (S6-final §5.2). It also rejects A-style override position rows (§2.2).

- **Nothing in S6-final needs to be reversed.** Its reasons for not persisting override *positions* still hold: the game ignores position [GAME G3/G4], and path UUIDs must stay out of shared JSON. Stage L persists something different, override **membership**. It does so in its own member, `DivinityLoadOrder.Deploy.Overrides`, keyed by ModKey, which is never a path. No `Layout` row kind is added, and S6-final's `OrderRowLayout.Project` and its INV-1/INV-2 byte-identity invariants are untouched.
- **What changes in the block, library mode only.** The Overrides block is still computed from every installed override. In library mode "installed" means "in the library", not "in `Mods`", because L3 loads from the library. Each member gets the deploy toggle (§9), and the header tooltip changes. Library off: exactly S6-final.
- **S6-final §9.1 statement "Always loaded by the game; position has no effect"** stays true with the library off. With it on, it becomes "loaded only if checked for this order".
- **S6-final's per-order `ForceAllowInLoadOrder` reset (its Q2)** is consistent with this design and should be kept. An allowed K2 is an ordinary `Order` entry and is deployed as one.
- **S6-final PR-0, `fix/modsettings-write-guard`** (never write a non-GUID UUID; read back after write), is complementary and ships first. Stage L's deploy relies on it: a K1 path-UUID must never be written even though its pak is now deployed.
- **`[JsonExtensionData]`.** S6-final puts it on `OrderLayout` and its rows. Stage L adds it on `DivinityLoadOrder` itself (for `Pins` and `Deploy`) in L4, the first Stage L PR that writes the order format, so a later fork build never drops either member.
- **Terminology.** S6-final's Overrides block uses "pinned last" only for its *position*. Version pins are a separate concept. The UI strings should say "kept at the end" for the block and "pinned to version" for pins.
- **Current's layout store.** S6-final §9.2 keeps a store at `Data/order-layouts.json`. Current's override membership needs no store, because it is *observed* from the manifest (§5.4).

### 13.4 Stage 8 (S8): changes

- **The bundle key becomes `<ModKey>@<ContentId[0..12]>`**, not `uuid@version64`. Version64 is not unique, which is this feature's premise. Pak paths become `paks/<keyDir>/<cid12>/<file>`. `pins` carry the full sha256, in the same `{Sha256, Version, Version64}` shape as the order's top-level `Pins`. The `ovr:` formula is §4.2's. All of this is a string change now and a schema break after Stage 8 ships.
- **S8 §4.2 "two versions can't happen in a local export" stops being true in library mode.** It is normal there, and the bundle carries every pinned version (the union over *resolved versions*, not UUIDs). The writer's "one modKey per UUID" assert applies only with the library off.
- **S8 §3.2 "pins live in the manifest, not the order":** fork orders now carry `Pins`. The S8 writer copies them, and resolves floating entries to the version deployed or exported. Upstream readers ignore both, so a plain order still means the same thing to them.
- **Overrides become per order** (from `Deploy.Overrides`), not bundle-level (`:3573`). This agrees with S6-final §11, which already dedupes overrides by pak name or hash, never by UUID. The `IncludeOverrides` switch stays.
- **Bytes are read from library paths**, so a version needn't be deployed to be exported.
- **Import in library mode:** each pak goes through `IngestStreamAsync` (skipped if the sha256 is present), and each order imports with its pins. Without the library, import is unchanged (S8 §7).

### 13.5 Stage 9 (S9): changes

- **Downloader output goes to `Data/Downloads/...part`, then `IngestFileAsync`, and never into `Mods`** (S9 §2.5 already says "never write straight into Mods").
- **S9 §2.5's "move the old pak to `Mods_Old_ModManager`, then revert" is superseded in library mode.** An update *adds* a version. Unpinned orders follow it, pinned orders don't, and "revert" means re-pin. The backup flow remains only for library-off mode.
- **mod.io:** S9's read-only first stance stands. G7 decides whether mod.io-installed paks can carry the RO attribute.
- **Friend sync:** "Co-op ready" = `TryGetVersion(pin.Sha256)` for every pin. Show "same 1.4.2.0, different file" explicitly. Whether BG3 multiplayer refuses mismatched builds is [unverified] (C G11).
- **`FreezeOrdersOnNewVersion`** ships with the downloader.

### 13.6 Stages 1, 2, 3, 7: notes

- **Stage 1:** nothing changes, but no public build with Stage L should ship before Stage 1.
- **Stages 2 and 3:** U1 is in their family; coordinate as above.
- **Stage 7:** a new window needs a line in `MainWindow.UpdateColorTheme`, and both themes must stay key-for-key.

---

## 14. Fork-only, and the upstreamable pieces

**Fork-only.** The feature changes what the manager does to the user's `Mods` folder, adds persistent state outside the app folder, changes the order JSON, and depends on fork-only Stage 6.2 semantics. Upstream's live line is avalonia, which solves only R1, by moving inactive paks to `Mods_Disabled` (`ModOrderViewModel.cs:763-800`), with no versions and no links. A WPF-master PR would have no future upstream. The owner's rule (bugfixes and perf upstream only) agrees.

The three upstreamable prerequisites are each cut from a clean `master`:

| PR | Branch | Content | Effort |
|---|---|---|---|
| U1 | `fix/pak-write-temp-rename` | `CopyFileAsync` (`DivinityFileUtils.cs:359-373`), `:3302`, `:3429` (and dead `ModUpdatesViewData.cs:180,205`) write `<dest>.tmp-<guid>` then `File.Move(tmp, dest, true)`, deleting the temp in `finally`. This also fixes "cancel leaves a truncated pak in Mods". The `.tmp-*` suffix isn't `.pak`, so the loader ignores it (`:761`) | 0.5 d |
| U2 | `fix/order-save-temp-rename` | `ExportLoadOrderToFileAsync` (`DivinityModDataLoader.cs:1098-1113`): temp, `Flush(true)`, rename. A crash mid-save today loses the order | 0.25 d |
| U3 | `perf/startup-load` asks (§13.2 items 1-3) | full-path key, `FileInfo` stats, per-directory partial detection: part of Stage 4 | in Stage 4 |
| (U4) | `fix/junction-handle-check` | `JunctionPoint.cs:297`: check `IsInvalid`. Only needed if L9 is ever built | 0.1 d |

---

## 15. PR series for Stage L (`feat/lib-*` on `fork/main`, each merged `--no-ff`)

Everything is feature-flagged by `LibraryEnabled` (default off). Phase 1 delivers the owner's first ask, **R1: only the order's mods are in `Mods`**. Phase 2 delivers **R2: the right version per order**.

| Phase | PR | Branch | Content | Depends on | Effort |
|---|---|---|---|---|---|
| 0 | U1, U2 | (above) | upstreamable prerequisites | — | 0.75 d |
| 1 | L1 | `feat/lib-fileops` | `IFileOps`, `WindowsFileOps`, `FileIdentity`, probe, `GameProcessGuard` (revives `:5424-5425`), `.lock`; the new `src/Tests/Core.Tests` (xUnit, `net8.0-windows`; pure tests also run on Linux) | U1 | 2-3 d |
| 1 | L2 | `feat/lib-store` | `ModKey`, `ContentId`, `ModVersion`, `LibraryMod`, `ModLibrary` (layout, sidecars, index rebuild, ingest, adopt, trash), `HashCache` | L1 | 4 d |
| 1 | L3 | `feat/lib-load` | `LoadModsAsync` (`:1660-1667`) loads the **library**, not `Mods`. This is essential: after a deploy, `Mods` holds only the order, so scanning it would make every undeployed mod vanish from the inactive list. Duplicates become versions (suppress `:1694-1703`); the three import paths go to ingest; `FilePath` points at the library; overrides listed from the library | L2 | 3 d |
| 1 | L4 | `feat/lib-deploy` | Pure planner, executor, journal, recovery hook in `RefreshAsync` (`:2340`), manifest, preview dialog, `ExportLoadOrderAsync` hook (`:2930-2932`), the `Deploy` member plus `[JsonExtensionData]` on `DivinityLoadOrder`, the deploy toggle (S6-final Overrides block, or the old pane), drift badge watcher, `DeleteMods` routing | L3, S6-final PR-0 | 5-6 d |
| 1 | L5 | `feat/lib-migrate` | Wizard (§10), receipts, hash and adopt with resume, verify, `restore-mods.cmd`, Restore/Disable/Remove (§7.6), settings | L4 | 3 d |
| — | gate | | **G1, G4 (P-OVR), G6 pass; G3/G5 recorded.** The owner uses Phase 1 day to day | | |
| 2 | L6 | `feat/lib-pins` | `VersionPin`, top-level `Pins`, the lifecycle fixes (§5.2), `PinResolver`, effective-version swap with async preload, `CopyRowStateFrom`, Current observed pins | L5 | 4 d |
| 2 | L7 | `feat/lib-version-ui` | Version badge (6.1 cached text), Version ▸ menu, order-level pin commands, SWITCH lines in the preview | L6 | 2-3 d |
| — | gate | | **G9/G9b pass** | | |
| 3 | L8 | `feat/lib-window` | Library window: labels, default, trash/restore, GC (§12 S6), verify, true disk usage | L7 | 3-4 d |
| 3 | L9 (optional) | `feat/lib-junction-target` | `JunctionSlotTarget` (B), only if the owner wants the library off C:; gated by G-J1..3 and W-J1..2 | L8, U4 | 2-3 d |
| 4 | — | with S8/S9 | bundle key `ModKey@cid12`, bundle import to the library, downloader ingest, `FreezeOrdersOnNewVersion` | L8, S8 | in S8/S9 |

Phases 0-3 come to about 30-35 working days without L9. That is in line with the three designs' 19-26 d each, which each left out parts the others had.

---

## 16. Open questions for the owner

1. **Deploy trigger.** Only on Export to Game (recommended: `Mods` and `modsettings.lsx` then always change together), or also when an order is selected?
2. **Do you use the in-game mod.io manager?** If yes, G7 blocks default-on and decides the read-only default.
3. **Two profiles.** Is strict "one deployed order at a time" acceptable (recommended; with modsettings backups and a warning), or do you want `UnionOfProfiles`?
4. **Migration and overrides.** Add today's overrides to every saved order (recommended: nothing changes until you remove them per order), or to none?
5. **New versions.** Should unpinned orders follow the new default (recommended), or should saved orders freeze automatically?
6. **"Delete Mod…"** in library mode: all versions to the library trash (recommended), or only the effective one?
7. **Disk.** Is the library on C: (the same drive as `Mods`) fine? Adopted paks cost 0 B; each *extra* version you keep costs its size. If not, L9 (junction mode) is the route.
8. **Order of work.** Phase 1 (R1) before Stage 6.2, with the toggle on the old Overrides pane, or after 6.2, with the toggle in the new block (recommended unless R1 is urgent)?
9. **Legacy-order default.** For an order you haven't touched since migration, is "keep the overrides that are deployed now" the right default in the preview?
10. **GC.** Minimum age 14 days, with labelled versions exempt?
11. **The "Export without deploying" button.** Keep it as an escape hatch (recommended), or hide it after G1/G4 pass?

---

## 17. Sources

- Microsoft, *CreateHardLinkW*: NTFS only, files only, same volume, at most 1023 links; ReFS not supported. https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw
- Microsoft, *Hard links and junctions*: attributes are per file; a directory entry's size and attributes are refreshed only at the link used; junctions may span local volumes. https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions
- Microsoft, *DeleteFileW*: deleting a read-only file fails until the attribute is removed. https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-deletefilew
- Nexus Mods App, BG3 developer notes: unlisted paks are "considered disabled" but may still override vanilla files; the in-game mod.io manager downloads, toggles and removes mods. https://nexus-mods.github.io/NexusMods.App/developers/games/0003-BaldursGate3
- Community, unverified: subfolders in `Mods` reset `modsettings.lsx` (https://wiki.bg3.community/Tutorials/Mod-Use/BG3-Mod-Types-and-how-to-install-them); "the game looks at ALL the mods in your Mods folder; if it does not like any of them, it resets the load order" (https://cal1.lr.ggtyler.dev/r/BaldursGate3/comments/1fkq8cq/patch_7_mod_troubleshooting); Patch 7 moved the Mods folder (https://forums.nexusmods.com/topic/11090483-bg3-mod-manager-and-patch-7-workaround); Vortex hardlink deployment needs staging on the game's drive and NTFS (https://forums.nexusmods.com/topic/13187550-symlink-deployment-run-as-administrator-only-available-deployment-method).
- Not found by search, so routed to tests: where the in-game mod.io manager writes and whether it overwrites in place (G7); whether BG3 holds paks open (G5); whether the game follows a junctioned `Mods` (G-J1); whether it rewrites another profile's modsettings (G8).
- In-repo: the three LIB designs; `S4-A-minimal.md` §2.3; `S4-B-pipeline.md` §2.4, §2.6; `S6-A-data-model.md` §0, §4, §5; `S6-C-game-risk.md` §0, §3, §7.1; `S6-final.md` §2.2, §5.2, §9, §11, §12; `S4-C-architecture.md` §0; `S8.md` §0, §3, §4.2, §8.3; `S9.md` §2.5, §3.2; wt-avalonia `ModOrderViewModel.cs:763-816, 1481-1490`, `ModDataLoader.cs:1699`.
