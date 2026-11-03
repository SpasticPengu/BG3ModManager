# LIB-B: Mod library, per-order deployment slots, and a junction swap

Scope: the owner's "mod hardlinking/versioning" request. Angle B: each load order owns a folder of
hardlinks into a central library, and the game's `Mods` folder becomes a directory junction to the
active order's folder. Switching orders then retargets one junction.

Status: design only. Nothing here was built or run. This container cannot build WPF or run BG3.
Code claims cite `path:line` against `master` @ `fc35358`. Claims about BG3 itself carry one of
these labels: **[src-web]** (a source is cited in §19), **[unverified]** (my belief, with the in-game
test that settles it in §16), **[inference]**.

---

## 0. TL;DR

1. **What it is.** A *library* (`<Root>\Library\<uuid>\<variant>\<file>.pak`, read-only attribute)
   holds every version of every mod exactly once. Each order has a *slot*
   (`<Root>\Deploy\<slotKey>\`), a flat folder of hardlinks to the variants that order uses.
   `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\Mods` becomes a junction to the active slot.
   Exporting an order to the game does three things: it syncs that order's slot (diff, add or remove
   links), retargets the junction, then writes `modsettings.lsx`. Mods that are not in the order are
   not in the folder the game reads, so overrides can't leak. Pins let order A use v1.4 and order B
   use v1.5 of the same UUID.
2. **Why the junction.** Every hardlink goes between `Library\` and `Deploy\`, which sit under one
   root. That makes the hardlink same-volume rule hold wherever the root lives. Only the junction
   crosses volumes, and junctions may span local volumes **[src-web: MS hard links and junctions]**.
   So the 27 GB library can live on D: while `%LOCALAPPDATA%` is on C:. Other benefits: the swap is
   one metadata operation, and a file another program drops into `Mods` lands in the *active
   order's* slot, so it can be attributed to that order.
3. **Honest verdict (§12).** At 935 mods the speed gain over direct hardlink sync is milliseconds
   against roughly a second, so speed doesn't matter. Junction swap wins on library placement and
   per-order capture. Direct sync wins on "the game and every other tool see a plain folder" and
   on reversibility: delete the library and the plain `Mods` still holds working links. **My
   recommendation:** build one engine whose primitive is "make folder F match plan P by hardlinks".
   Ship *direct sync* (F = `Mods`) first. Add *junction swap* (F = slot, then retarget) as an opt-in
   mode, gated on in-game tests G-1 to G-5. The junction mode costs about one extra PR on top of the
   shared engine (§17).
4. **Biggest risk (both modes):** **write-through.** A hardlink is the same file, so any program
   that truncates and rewrites a pak in place silently changes it in the library and in every
   order. The manager does this itself today. All three pak writers use `File.Create`, which
   truncates in place (`DivinityFileUtils.cs:364`, `MainWindowViewModel.cs:3302`, `:3429`). These
   must change to write-temp-then-rename *before* any links exist (PR0, upstreamable). Library files
   then also get the read-only attribute, which is per-file and shared by all links, so other
   programs' in-place writes fail loudly. **Biggest junction-specific risk:** the game, its in-game
   mod.io manager, or another tool mishandling a reparse-point `Mods`, or replacing it with a plain
   empty folder. Also, deleting the library root leaves `Mods` dangling. All of this is
   [unverified] and settled by G-1 to G-5.
5. **Fork-only.** It changes what `Mods` *is*, adds a data model upstream doesn't have, and depends
   on Stage 6.2 semantics. Only PR0 (atomic pak writes) and a small `JunctionPoint` error-check fix
   are upstreamable.

---

## 1. Ground truth: what the code does today

| Fact | Where |
|---|---|
| Mods path = `<AppDataGameFolder>\Mods`. Created if `!Directory.Exists` | `MainWindowViewModel.cs:1354-1369` |
| User paks are loaded from that path **recursively** (`RecursiveOptions`), except paths containing `Baldur's Gate 3\Data`, `\bin`, `Localization` | `DivinityModDataLoader.cs:745-768`, `:459-464`, `DivinityFileUtils.cs:12-17` |
| Multi-part paks (`Foo_1.pak` next to `Foo.pak`) are skipped as partials via a **static, never-cleared** `_AllPaksNames` | `DivinityModDataLoader.cs:429-446` |
| Same-UUID paks are treated as duplicates. The highest `VersionInt` wins and the rest are sent to `DeleteMods` → the deletion dialog, at startup | `DivinityModDataLoader.cs:789-806`, `MainWindowViewModel.cs:1696-1704` |
| The store keeps one `DivinityModData` per UUID | `MainWindowViewModel.cs:80` |
| A pak without `meta.lsx` that overrides a base folder gets `UUID = pakPath` (absolute), `IsForceLoaded = true` | `DivinityModDataLoader.cs:607-622`, `:632-642` |
| Order entries serialise `UUID` and `Name` only. `Clone()` copies three fields | `DivinityLoadOrder.cs:7-21` |
| `DivinityLoadOrder` has no stable id; it's identified by `Name`/`FilePath` | `DivinityLoadOrder.cs:24-44` |
| Order JSON goes through Newtonsoft via `SafeDeserialize` (unknown members ignored by Newtonsoft's default) | `DivinityJsonUtils.cs:29-37`, `DivinityModDataLoader.cs:1186-1206` |
| Loading an order flips `IsActive`/`Index` per UUID, then rebuilds Active/Inactive from `addonMods` | `MainWindowViewModel.cs:2093-2157` |
| Export-to-game = `BuildOutputList` (adds deps if `AutoAddDependenciesWhenExporting`) → `ExportModSettingsToFileAsync` → backup "LastExported" | `MainWindowViewModel.cs:2897-2934`, `DivinityModDataLoader.cs:1431-1469` |
| `modsettings.lsx` writes `mod.Version.VersionInt` and `mod.MD5` (meta's MD5 attribute; no file hash exists anywhere) | `DivinityModDataLoader.cs:1471-1487` |
| Pak writers truncate in place: `CopyFileAsync` → `File.Create`; compressed import → `File.Create`; archive import → `File.Create` | `DivinityFileUtils.cs:359-373`, `MainWindowViewModel.cs:3302`, `:3429` |
| Imports target `PathwayData.AppDataModsPath` directly | `MainWindowViewModel.cs:1840`, `:3233`, `:3394` |
| Deletion = recycle bin via VB `FileSystem.DeleteFile` | `RecycleBinHelper.cs:45-60`, `DeleteFilesViewData.cs:87` |
| Junction helper exists (create/delete/exists/target). Its only use is resolving junctioned game paths | `JunctionPoint.cs:100-260`, `DivinityRegistryHelper.cs:52-75` |
| `JunctionPoint.OpenReparsePoint` treats any non-zero `GetLastWin32Error()` after `CreateFile` as failure instead of checking `IsInvalid` (fragile; fix in PR1) | `JunctionPoint.cs:290-299` |
| `JunctionPoint.Create(overwrite:true)` on an existing directory issues `FSCTL_SET_REPARSE_POINT` on it. On a non-empty real directory NTFS refuses **[inference: ERROR_DIR_NOT_EMPTY]**, which works as a free safety net | `JunctionPoint.cs:100-151` |
| File-watcher service exists and is used for `modsettings.lsx` | `FileWatcherService.cs`, `MainWindowViewModel.cs:5410-5446` |
| A "game is running" check exists only as a comment | `MainWindowViewModel.cs:5424-5425` |
| No single-instance guard. Two copies can run at once | CLAUDE.md §3 |
| Upstream avalonia has a move-based cousin: on export, `OrganizeMods` *moves* inactive user paks to a sibling `Mods_Disabled` folder. No links, no versions | wt-avalonia `src/GUI/ModManager/ViewModels/Main/ModOrderViewModel.cs:763-800`, `src/Core/Models/PathwayData.cs:32,65`, `src/GUI/ModManager/Services/ModImportService.cs:63` |

BG3 facts this design leans on:

- A pak in `Mods` that isn't listed in `modsettings.lsx` is "considered disabled" but "may still
  override" vanilla files. Mod authors advise deleting rather than delisting **[src-web: Nexus App
  BG3 notes]**. This is the owner's premise, independently sourced.
- The in-game mod.io manager downloads, toggles and removes mods. As of Patch 7 it has no
  load-order management **[src-web: Nexus App BG3 notes]**. Its downloads are reported to land in
  the same `Mods` folder **[src-web~: community wiki, via S9.md §3.2; unverified → G-4]**.
- Subfolders in `Mods` make the game reset `modsettings.lsx` **[README.md:21; unverified → G-8]**.
- People do junction BG3's AppData folders manually to move them off C:, which suggests the game
  follows a junction **[src-web~: gameplay.tips guide, found via search, page now 404;
  unverified → G-1]**.

---

## 2. Concepts

| Term | Meaning |
|---|---|
| **Library** | Every pak the manager owns, one immutable copy per *variant*, never loaded by the game directly |
| **Variant** | One concrete build of one mod: (UUID, `Version64`, content). Multi-part paks form one variant |
| **VariantId** | `<Version64>` normally, or `<Version64>-<sha256[0..8]>` when two different builds claim the same `Version64` (common: authors forget to bump) |
| **modKey** | `<uuid>@<VariantId>`. Same shape as S8's bundle key (`docs/review/stages/S8.md` §3.3), so Stage 8/9 manifests carry variants unchanged |
| **Pin** | Optional on an order entry. It names a VariantId. Without a pin the entry *floats* to the latest variant |
| **Slot** | A flat folder of hardlinks (`Deploy\<slotKey>\`). Its contents are exactly the deployment plan of one order |
| **Plan** | The list of (file name → library file) a slot must contain, computed from the export list |
| **Active slot** | The slot `Mods` currently points to |
| **Foreign file** | A file in the active slot that isn't a hardlink of a library file the manifest knows about |

---

## 3. On-disk layout

Default root: `%LOCALAPPDATA%\BG3MM-Fork\`. It is configurable, and must be a local fixed NTFS
volume (`DivinityFileUtils.GetPathDriveType`, `:39`) that isn't under OneDrive. The root sits
*outside* `Larian Studios\Baldur's Gate 3\`, because "delete the Larian folder" is common
troubleshooting advice and would take the library with it **[inference]**.

```
%LOCALAPPDATA%\BG3MM-Fork\                       <Root>
  README - DO NOT DELETE.txt                     what this is, how to undo, real size on disk
  Restore-Mods-Folder.cmd                        app-free undo (§9.3)
  state.json                                     mode, active slot, Mods path, migration record
  library.json                                   index cache (rebuildable from disk)
  journal.json                                   present only while a multi-step op runs
  .lock                                          held FileShare.None during deploy (2 instances)
  Library\
    3f2a9c1e-…-uuid\
      36310272004161536\                         Version64 of 1.4.0.0
        PartyLimitBegone.pak                     [ReadOnly]
      36451009492516864\                         1.5.0.0
        PartyLimitBegone.pak                     [ReadOnly]
    9b1d…-uuid\
      36028797018963968\
        HugeTextures.pak  HugeTextures_1.pak  HugeTextures_2.pak    multi-part = one variant
    _override\
      ovr-5e0c2a71b93d44f1\                      meta-less override; key = ovr:<hash16(file name)>
        0\  GustavTextureFix.pak
    _incoming\                                   import staging: *.part then rename
    _quarantine\                                 foreign files that failed to parse
  Deploy\
    ord-7d1c4b0e\                                slot of order "Tactician run" (order.Id)
      PartyLimitBegone.pak     ← hardlink of Library\3f2a…\36310272004161536\PartyLimitBegone.pak
      …                                          935 links, flat, no subfolders
    ord-7d1c4b0e.slot.json                       slot manifest (beside, never inside, the slot)
    ord-a90e33f2\                                "Honour mode": uses 1.5.0.0 of the same mod
    prof-public\                                 the "Current" order of profile Public
    _all-latest\                                 today's behaviour: every latest variant
    _migration-snapshot\                         exact image of the pre-migration Mods folder

%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\
  Mods            ══junction══►  %LOCALAPPDATA%\BG3MM-Fork\Deploy\ord-7d1c4b0e
  Mods.pre-library-2026-11-03\                   original folder after migration (links only)
  PlayerProfiles\…
```

Rules:
- **Nothing but paks inside a slot.** The manifest lives beside the slot, so the game never sees a
  non-pak file the manager put there (whether it would mind is G-8, but there's no need to find out).
- **A slot is flat** and uses the variant's original file names. The game identifies mods by meta,
  not by file name **[unverified → G-7]**. Even so, names are never rewritten: multi-part sets rely
  on exact names (`DivinityModDataLoader.cs:430`). If two different UUIDs collide on one file name
  in one slot, deployment is refused and the user is told which ones.
- **slotKey:** `ord-<order.Id[0..8]>` for saved orders. Saved orders get a new `Id` (§5). For the
  "Current" (`IsModSettings`) order of profile P it is `prof-<sanitised profile name>`.

---

## 4. Architecture

Everything that touches the file system lives in **Core** (`src/Core/Library/`, namespace
`DivinityModManager.Library`, unprefixed type names), with no WPF types, so a later Avalonia port
reuses it unchanged. The GUI gets a thin wiring layer in `MainWindowViewModel` plus a wizard view.

```
Core/Library/
  ILinkProvider.cs            platform seam: hardlink, file identity, directory link (junction/symlink)
  WindowsLinkProvider.cs      CreateHardLinkW, GetFileInformationByHandle, JunctionPoint
  UnixLinkProvider.cs         link(2), stat(2), Directory.CreateSymbolicLink + rename(2)   (later)
  FileIdentity.cs             readonly record struct (VolumeSerial, FileIndex, LinkCount, Size, LastWriteUtc)
  LibraryIndex.cs             in-memory index: modKey → LibraryVariant; FileIdentity → modKey
  LibraryVariant.cs           UUID, VariantId, Version64, Files (rel paths), Size, Sha256 (lazy), Identity
  LibraryScanner.cs           walks Library\, parses each variant once (Stage 4 cache), builds index
  LibraryIngest.cs            staged file → variant folder (dedupe by size+sha256), read-only, index update
  DeploymentPlan.cs           computes Plan from export list + the order's override rows (S6-A Layout) + pins
  SlotSync.cs                 makes a folder match a Plan, by links only (shared with direct-sync mode)
  ModsFolderInspector.cs      classifies Mods: Missing / Plain / OurLink(target) / ForeignLink / Dangling
  DeploymentService.cs        orchestrates: lock, journal, adopt, sync, swap, verify, recover, undeploy
  DeploymentState.cs          state.json / journal.json DTOs (Newtonsoft, [DataMember])
  SafeFileOps.cs              SafeUnlink, WriteViaTemp, SetReadOnly; the only place that deletes
```

Registered through Splat in the `MainWindowViewModel` ctor, next to `IModRegistryService`
(CLAUDE.md §3): `Services.Register<IDeploymentService>(new DeploymentService(new WindowsLinkProvider()))`.

### 4.1 ILinkProvider

```csharp
namespace DivinityModManager.Library;

public interface ILinkProvider
{
	/// <summary>Creates linkPath as a new name for existingPath. Throws IOException on failure (cross-volume, 1023-link cap, exists).</summary>
	void CreateHardLink(string linkPath, string existingPath);

	/// <summary>Opens without following a final reparse point; returns false if the path does not exist.</summary>
	bool TryGetIdentity(string path, out FileIdentity identity);

	bool AreOnSameVolume(string pathA, string pathB);

	/// <summary>Junction on Windows, symlink on Unix. Fails if linkPath exists and is not an empty directory or a link.</summary>
	void CreateDirectoryLink(string linkPath, string targetDir);

	/// <summary>Atomically points an existing directory link at newTarget. Windows: FSCTL_SET_REPARSE_POINT on the existing junction. Unix: symlink to temp name + rename(2).</summary>
	void RetargetDirectoryLink(string linkPath, string newTargetDir);

	/// <summary>Returns the link target, or "" if the path is a real directory or missing.</summary>
	string GetDirectoryLinkTarget(string path);

	/// <summary>Removes the link itself, never its target's contents.</summary>
	void RemoveDirectoryLink(string linkPath);
}
```

`FileIdentity.LinkCount` is the key safety datum (§8). On Windows it comes from
`BY_HANDLE_FILE_INFORMATION.nNumberOfLinks`, read with a handle opened by
`CreateFile(…, FILE_READ_ATTRIBUTES, FILE_SHARE_READ|WRITE|DELETE, …, FILE_FLAG_BACKUP_SEMANTICS |
FILE_FLAG_OPEN_REPARSE_POINT)`, so it works while the game holds the pak open.

---

## 5. Data model and JSON

### 5.1 Order entry: optional pin and deploy-only flag

```csharp
[DataContract]
public class DivinityLoadOrderEntry
{
	[DataMember]
	public string UUID { get; set; }

	[DataMember]
	public string Name { get; set; }

	/// <summary>Null = float to the latest library variant.</summary>
	[DataMember(EmitDefaultValue = false)]
	public ModPin Pin { get; set; }

	public bool Missing { get; set; }

	public DivinityLoadOrderEntry Clone()
	{
		return new DivinityLoadOrderEntry() { Name = this.Name, UUID = this.UUID, Missing = this.Missing, Pin = this.Pin?.Clone() };
	}
}

[DataContract]
public class ModPin
{
	[DataMember] public string Variant { get; set; }          // VariantId, e.g. "36310272004161536"
	[DataMember] public string Version { get; set; }          // display only, "1.4.0.0"
	[DataMember(EmitDefaultValue = false)] public long Size { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Sha256 { get; set; }   // filled when known; S8/S9 sync key

	public ModPin Clone() => (ModPin)MemberwiseClone();
}
```

`DivinityLoadOrder` gains `[DataMember(EmitDefaultValue = false)] public string Id { get; set; }`,
a GUID "N" string assigned on first save. `Clone()` (`DivinityLoadOrder.cs:304-312`) must copy
`Id` *only* when cloning for the same order. "Save as" (`MainWindowViewModel.cs` save path ~:2597)
must clear it, or two orders share a slot.

Example `Orders/Tactician run.json`:

```json
{
	"Id": "7d1c4b0e5f2a4c0b9d3e8a1f2b6c7d90",
	"Order": [
		{ "UUID": "3f2a9c1e-…", "Name": "Party Limit Begone",
		  "Pin": { "Variant": "36310272004161536", "Version": "1.4.0.0", "Size": 482133 } },
		{ "UUID": "a11e…", "Name": "Tactician Enhanced" }
	],
	"Layout": { "Version": 1, "Rows": [
		{ "Kind": "Override", "Pak": "GustavTextureFix.pak", "Before": "a11e…",
		  "Pin": { "Variant": "0", "Size": 91230 } }
	] }
}
```

**Overrides are not order entries.** `docs/review/stages/alternatives/S6-A-data-model.md` §0 decides that `Order` stays
mods-only and that folded-in overrides (and 6.3 dividers) live in an optional sibling `Layout`, with
rows anchored to an `Order` UUID and no-meta overrides matched by `Pak` file name, never by their
path UUID. It lists seven code paths that misbehave on a non-mod `Order` entry. This design follows
it. **An override is deployed by an order if and only if that order has a `Layout` override row for
it.** The row may carry the same `Pin` object (S6-A rows keep unknown fields via
`[JsonExtensionData]`, so a 6.2-only build preserves it). If LIB ships before 6.2, PR3 implements
only the override-row subset of S6-A's `Layout`, in S6-A's exact shape.

### 5.2 Compatibility rules

- **Old file → new build:** no `Id`, `Pin` or `Layout`. Everything floats, `Id` is assigned on
  the next save. Behaviour is identical to today except deployment (which is opt-in, §9).
- **New file → upstream/old build:** Newtonsoft ignores unknown members (`Id`, `Pin`, `Layout`), so
  the same `Order` loads and the same `modsettings.lsx` is produced (S6-A §4.3). If upstream
  re-saves the file it drops pins and override rows. Document this. Deployment itself is invisible
  to an old build: it just sees whatever `Mods` holds.
- **S8 alignment:** S8 says pins live in the bundle manifest so that "a plain order file never
  changes meaning" (`docs/review/stages/S8.md` §3.2). That holds for upstream readers, which ignore `Pin`.
  For this fork the pin *is* order data (the owner wants order A to own v1.4). The S8 writer derives
  `pins` from `entry.Pin` (floating → the variant deployed at export time). No schema change on
  either side: modKey has the same shape.
- **Meta-less overrides (`UUID = absolute pak path`, `DivinityModDataLoader.cs:620`):** in library
  mode the *library key* is `ovr:<hash16(lower(file name))>@0`, the same idea as S8's `ovr:` key and
  its P1 privacy fix. It is never written into `Order` (S6-A). The in-memory `UUID` becomes the
  library path of the variant, so it stays unique and `HasInvalidUUID` (`DivinityModData.cs:367-374`)
  still flags it. Migration converts any existing `Order` entry whose UUID is a path under the old
  `Mods` folder (a `ForceAllowInLoadOrder` override) into an S6-A override row matched by `Pak`.
- **Unresolvable pin** (variant not in library): the entry is treated like a missing mod, with the
  text "pinned 1.4.0.0 not in library". **Never silently fall back to latest.** Offer
  "Use latest (1.5.0.0)" / "Locate file…".

### 5.3 Library side DTOs

```json
// library.json (cache; "Rebuild library index" regenerates it from Library\)
{ "Schema": 1, "Variants": [
	{ "Key": "3f2a…@36310272004161536", "UUID": "3f2a…", "Variant": "36310272004161536",
	  "Version64": 36310272004161536, "Files": ["3f2a…/36310272004161536/PartyLimitBegone.pak"],
	  "Size": 482133, "LastWriteUtc": "2026-05-01T10:12:00Z", "FileIndex": "0x000a00000002f1c3",
	  "Sha256": null, "AddedUtc": "2026-11-03T15:00:00Z", "Source": "migration" } ] }

// Deploy\ord-7d1c4b0e.slot.json
{ "Schema": 1, "SlotKey": "ord-7d1c4b0e", "OrderId": "7d1c…", "OrderName": "Tactician run",
  "BuiltUtc": "…", "Entries": [ { "File": "PartyLimitBegone.pak", "Key": "3f2a…@36310272004161536" } ] }

// state.json
{ "Schema": 1, "Mode": "JunctionSwap", "ModsPath": "C:\\Users\\…\\Mods", "ActiveSlot": "ord-7d1c4b0e",
  "Migration": { "Utc": "…", "BackupDir": "…\\Mods.pre-library-2026-11-03", "BackupDeleted": false } }
```

### 5.4 Settings

These go in `DivinityModManagerSettings.cs`, with all three attributes per CLAUDE.md §4:
- `ModLibraryRoot` (string, `[SettingsEntry(HideFromUI = true)]`). The wizard sets it, never a text box.
- `DeploymentMode` enum `{ Off, DirectSync, JunctionSwap }` (`HideFromUI = true`). It changes only
  through the wizard or Undeploy, because flipping it is a file-system migration, not a preference.
- `AdoptForeignPaks` enum `{ Ask, KeepInSlot, LibraryOnly }`, default `Ask` (visible).
- `ProtectLibraryFilesReadOnly` bool, default `true` (visible, with a warning tooltip).

### 5.5 In-memory model

`DivinityModData` stays one-per-UUID in the store. It gains:
- `IReadOnlyList<DivinityModData> Variants` (all parsed variants; usually one), and
- `string VariantId` / `string ModKey` (which variant this instance is).

The store entry for a UUID is *swapped* to the variant the selected order wants (§6.4). Version,
dependencies, `FilePath`, `Files` and `LastModified` then all naturally describe the deployed build.
That also fixes the version written to `modsettings.lsx` (`DivinityModDataLoader.cs:1482`) and the
archive export source path (`MainWindowViewModel.cs:3598`) without touching either.

---

## 6. Algorithms

### 6.1 Deploy (on "Export order to game")

Entry point: `ExportLoadOrderAsync`, right after `finalOrder` is computed (`MainWindowViewModel.cs:2931`)
and **before** `modsettings.lsx` is written (`:2932`). Either the game sees the new mods and the new
order, or it sees neither.

```
Deploy(order, finalOrder, token)
 0. Guards
    - bg3.exe / bg3_dx11.exe running → refuse ("close the game first"). Process.GetProcessesByName.
    - Acquire <Root>\.lock (FileShare.None). Fails → "another manager instance is deploying".
    - Recover any leftover journal.json first (§6.5).
 1. Inspect Mods (ModsFolderInspector)
    Missing        → fine (will create link)
    OurLink(slotX) → fine
    Plain, empty   → fine (will replace with link)
    Plain, non-empty → Adopt everything in it (§6.3), then it must be empty, else abort
    ForeignLink(t) → abort: "Mods points somewhere this manager didn't put it (t)". Never touch it.
    Dangling(t)    → treat as OurLink if t is under <Root>\Deploy, else abort
 2. Adopt foreign files in the CURRENT active slot (§6.3). Must happen before the swap, or files
    the in-game manager wrote while slot X was active get stranded in slot X unannounced.
 3. Plan = DeploymentPlan.Build(finalOrder ∪ order's Layout override rows, pins, index)
    - each entry → modKey (pin or latest) → library files (all parts)
    - skip: base-game/Larian mods, editor projects (IsEditorMod: they live in Data\Mods), dividers
    - collisions on file name between different modKeys → abort with the list
    - unresolved entries → abort, unless they're already flagged Missing (the user saw them)
 4. journal.json = { op:"deploy", slot, fromSlot, plannedEntries, step:"sync" }
 5. SlotSync(slotDir, plan)  (idempotent; also the direct-sync primitive)
    a. enumerate slotDir → name → FileIdentity
    b. for each name not in plan, or whose identity ≠ planned library identity:
         SafeUnlink(name)   (§8 I1: only if LinkCount ≥ 2 AND identity ∈ index; else quarantine-move)
    c. for each plan entry missing: CreateHardLink(slotDir\name, libraryPath)
    d. re-enumerate, verify name set == plan and every identity matches → else abort (slot left consistent-but-wrong; next deploy fixes it)
    e. write <slot>.slot.json
 6. journal.step = "swap"
    Mods missing/empty-plain → CreateDirectoryLink(Mods, slotDir)
    OurLink                  → RetargetDirectoryLink(Mods, slotDir)
    verify GetDirectoryLinkTarget(Mods) == slotDir, and Directory.EnumerateFiles(Mods) count == plan count
 7. state.ActiveSlot = slot; delete journal.json; release lock
 8. continue the existing export: modsettings.lsx, LastExported backup, etc.
Failure at any step: ShowAlert(Danger) with step + path, modsettings.lsx NOT written, journal kept for
recovery, Mods left pointing at the previous (complete) slot.
```

Step 6 detail: on Windows the existing `JunctionPoint.Create(Mods, slot, overwrite:true)` already
calls `FSCTL_SET_REPARSE_POINT` on the existing directory (`JunctionPoint.cs:117-145`). That
replaces a same-tag reparse point in place **[inference; test W-3]**. If W-3 shows it doesn't, the
fallback is `RemoveDirectoryLink` then `CreateDirectoryLink`: a window of a few milliseconds where
`Mods` doesn't exist, covered by the journal.

### 6.2 Switch without exporting

Selecting an order in the dropdown (`MainWindowViewModel.cs:5197-5205` → `LoadModOrder`) doesn't
deploy. Deployment is tied to "Export to game", the same moment `modsettings.lsx` changes today.
A status chip in the order bar shows "Deployed: Tactician run" vs "Selected: Honour mode (not
deployed)". An optional setting `DeployOnSelect` can come later.

### 6.3 Adopt foreign files

Triggers:
- (a) startup/refresh, at the start of `RefreshAsync` (`MainWindowViewModel.cs:2371`, before `LoadModsAsync`)
- (b) step 2 of every deploy
- (c) a `FileSystemWatcher` on `Mods` for `*.pak` created/renamed, debounced 3 s, with a non-modal
  alert "New mod found in Mods: X. Adopt / Ignore". Watching a junction path watches the target
  directory **[inference; test W-7]**.

```
Adopt(file in active slot)
 1. TryGetIdentity: if identity ∈ index → it's ours (maybe renamed) → done.
 2. Wait until stable: size unchanged for 2 s and opening with FileShare.None succeeds (writer done).
 3. Parse (LoadModDataFromPakAsync). Fails → move to Library\_quarantine\ and alert (never delete).
 4. modKey: same UUID+Version64 exists?
      sizes equal and SHA-256 equal → duplicate bytes: replace the foreign name with a link to the
                                       library file (delete is safe: identical content still exists)
      else                         → new VariantId "<v64>-<sha8>"
 5. New variant: File.Move(file, Library\…\<variant>\<name>) (same volume as the slot, so a rename),
    SetReadOnly, CreateHardLink(slot\name ← library). Multi-part: move all parts together.
 6. Order membership per AdoptForeignPaks:
      KeepInSlot  → add to the order as an S6-A override row if force-loaded, else as an inactive mod kept in
                    the slot (game behaviour unchanged)
      LibraryOnly → unlink from the slot on the next deploy (the game stops seeing it)
      Ask         → alert with both buttons; until answered the file stays deployed
```

Other drift, reported but not "fixed" without consent:
- **Link missing from the active slot** (something deleted it, e.g. an in-game uninstall
  **[unverified → G-4]**): "N mods were removed from the Mods folder by another program. Remove
  them from the order / Re-deploy them".
- **Library file changed in place** (identity same, size/mtime differ from `library.json`): this
  is write-through. Danger alert: "X was modified in place by another program; every order using
  it now has the modified file". Re-parse. If its Version64 changed, re-file it as the new variant
  (rename the folder). The old bytes are gone; read-only (§8 I3) exists to prevent this.
- **Mods replaced by a plain folder** (a tool deleted and recreated it): inspector reports `Plain`.
  Adopt its contents, then on the next deploy re-link. Until then the game sees whatever that folder
  holds.

### 6.4 Applying pins in the UI

At the top of `LoadModOrder` (`MainWindowViewModel.cs:2099`), before the activation loop:

```csharp
private void ApplyOrderVariants(DivinityLoadOrder order)
{
	if (Settings.DeploymentMode == DeploymentMode.Off) return;
	var wanted = order.Order.Where(x => x.Pin != null).ToDictionary(x => x.UUID, x => x.Pin.Variant);
	mods.Edit(cache =>
	{
		foreach (var mod in cache.Items.Where(x => x.Variants.Count > 1).ToList())
		{
			var target = wanted.TryGetValue(mod.UUID, out var variantId)
				? mod.Variants.FirstOrDefault(x => x.VariantId == variantId)
				: mod.Variants[0]; // Variants sorted newest first
			if (target != null && !ReferenceEquals(target, mod))
			{
				target.Variants = mod.Variants;
				cache.AddOrUpdate(target);
			}
		}
	});
}
```

This runs on the main thread (the `LoadModOrder` callers already are). `addonMods` rebinds
synchronously, and `ActiveMods`/`InactiveMods` are rebuilt from `addonMods` at `:2141-2144`, so
they pick up the swapped instances. Only UUIDs that have more than one variant are touched.

Pin UI: right-click → "Version ▸ Latest (float) / 1.5.0.0 / ● 1.4.0.0 (pinned)". The version
column shows a pin glyph. "Pin all to current versions" and "Unpin all" go in the order menu.
Changing a pin re-runs `ApplyOrderVariants` and marks the order dirty.

### 6.5 Crash recovery (`journal.json`)

| journal.step | State on disk | Recovery |
|---|---|---|
| `sync` | target slot partially synced; Mods → previous slot (complete) | redo SlotSync (idempotent) or leave it. The game is unaffected |
| `swap` | Mods → old or new slot, or missing (fallback path only) | if Mods missing → CreateDirectoryLink(Mods, journal.slot); verify; finish |
| `migrate:*` | see §9 | per-step table in §9.2 |
| `undeploy:*` | see §9.3 | per-step |

---

## 7. What can break, and how each case is detected

| # | Event | Effect with junction swap | Detection / response |
|---|---|---|---|
| B1 | In-game mod.io manager downloads a mod **[unverified → G-4]** | lands in the active slot (per-order) | watcher + startup adopt (§6.3) |
| B2 | Game or mod.io uninstalls a mod | removes a link in the slot; the library copy survives | "link missing" drift report |
| B3 | Game or mod.io **updates a pak in place** **[unverified → G-4]** | write-through into the library if not read-only; with read-only the update fails | read-only (I3) blocks it. Tampering check catches the rest. G-4 decides whether read-only breaks mod.io updates (then: `ProtectLibraryFilesReadOnly=false` for mod.io-sourced variants and adopt on change) |
| B4 | A tool runs `Directory.Delete(Mods, true)` | .NET doesn't recurse through reparse points, so the junction is removed and the slot is untouched **[src-web: .NET Directory.Delete docs]** | inspector: Missing → next deploy re-links. Library intact |
| B5 | A tool deletes Mods *contents* (`del Mods\*.pak`, Explorer select-all-delete) | deletes the links in the active slot only | drift report. Library intact. Re-deploy restores |
| B6 | A tool or game recreates `Mods` as a plain folder | the game sees that folder, not the slot | inspector `Plain` → adopt, then re-junction on deploy (only if empty after adopt) |
| B7 | Game patch migrates the Mods folder (Patch 7 moved it from Documents **[src-web~: Nexus forum thread via search]**) | unknown. It might copy through the junction (fine) or trip on it | inspector on every start. Worst case: undeploy, let it migrate, redeploy |
| B8 | Library root deleted or drive missing | `Mods` dangles, so the game sees no mods **[unverified: maybe resets modsettings → G-3]** | inspector `Dangling` → Danger alert at startup. `README - DO NOT DELETE.txt`. Library restricted to fixed drives |
| B9 | Antivirus / Controlled Folder Access | could block link creation under AppData **[unverified]**. Defender scans per file, so links aren't scanned twice **[inference]** | every link op checks results; first failure aborts the deploy with the path and Win32 error |
| B10 | Cloud sync | `%LOCALAPPDATA%` isn't in OneDrive's known-folder set **[inference]**. A root inside OneDrive breaks links and placeholders | refuse roots under `%OneDrive%`/`%OneDriveConsumer%`/`%OneDriveCommercial%` or with `RECALL_ON_DATA_ACCESS`/`OFFLINE` attributes |
| B11 | Backup tools (File History, Backblaze…) | slots can be backed up N times (each link looks like a file). Junctions are often skipped | README note. Suggest excluding `Deploy\` |
| B12 | Explorer "Properties → Size" on the root | shows N × 27 GB | UI shows real usage (sum of unique identities). README explains it |
| B13 | "No subfolders in Mods" (README.md:21) | slot is flat, `Mods` itself is a junction | G-8 confirms the game doesn't treat a junctioned `Mods` like a subfolder |
| B14 | Two manager instances | interleaved syncs | `.lock` with FileShare.None |
| B15 | Game running during deploy | a swap would succeed *under* the running game | process guard. Deploy refused |
| B16 | Vortex or another hardlink manager | deploys into the active slot and purges by deleting | out of scope, as the community already advises not to mix managers. Detect `__folder_managed_by_vortex` / `vortex.deployment*.json` in Mods and refuse |
| B17 | Our own `SetGamePathways` | `if (!Directory.Exists(Mods)) CreateDirectory(Mods)` (`:1365-1369`) with a dangling junction → `Directory.Exists` may be false while the name exists, so `CreateDirectory` throws inside the method's catch-all (`:1487`) and the *rest of pathway setup is skipped* **[inference; test W-6]** | replace with the inspector (§11 H1) |
| B18 | Our own loader | the recursive scan of Mods would also list paks under a nested link | library mode doesn't scan Mods for mods at all (§11 H2). .NET enumeration doesn't recurse into reparse-point subdirectories **[inference; test W-8]** |
| B19 | Our own deleter | recycling a link only removes a name. "Delete mod" on a slot path wouldn't free anything | redirect deletion (§11 H6) |
| B20 | Manager uninstalled (portable: folder deleted) | `Mods` stays a junction to the last slot. The game keeps working | README + `Restore-Mods-Folder.cmd` (§9.3) |
| B21 | 1023-link cap per file **[src-web: CreateHardLinkW]** | library + one link per slot that uses it | refuse more than 1000 slots per variant (practically never) |
| B22 | Profiles share Mods | deploying order A for profile P1 changes what P2 sees. P2's `modsettings` may list now-absent mods, and the game may drop them **[unverified → G-3]** | per-profile "last deployed slot" in state.json. Warn when exporting to P1 if P2's modsettings references mods not in the new slot. P2's order also survives as JSON |

---

## 8. Safety guarantees and how each is enforced

| ID | Guarantee | Enforcement |
|---|---|---|
| I1 | **The manager never removes the last name of a file.** | All unlinks go through `SafeFileOps.SafeUnlink(path)`: open the identity, require `LinkCount ≥ 2` **and** identity ∈ `LibraryIndex`, and require the library path still to resolve to the same identity. Otherwise *move* to `_quarantine` instead. A grep-able rule: no `File.Delete` in `Core/Library` outside `SafeFileOps`. Unit-tested with a fake provider |
| I2 | **Library deletions are recoverable.** | "Delete version from library" first SafeUnlinks every slot link, *then* recycles the library file. The recycle bin then holds the last name (`RecycleBinHelper.cs:45`). Always behind the existing confirmation dialog |
| I3 | **No in-place writes to shared bytes.** | (a) PR0: every pak writer uses write-to-`.part`-then-`File.Move(overwrite:true)`. A rename replaces the directory entry and never writes through a link. (b) The library file gets `FileAttributes.ReadOnly`. The attribute belongs to the file, so every link inherits it **[src-web: MS hard links]**, and a truncating `File.Create` from anyone fails with access denied. (c) Tampering check on every scan |
| I4 | **The game never sees a half-built order.** | Sync the target slot completely (and verify), *then* one swap. `modsettings.lsx` is written only after a verified swap |
| I5 | **Every failure is visible.** | Each step returns a `DeployResult` with step, path, Win32 error. The GUI calls `ShowAlert(..., AlertType.Danger, 0)` (CLAUDE.md §5; logging is off by default) |
| I6 | **Crash-safe.** | `journal.json` written before each multi-step op, steps idempotent, recovery on next start and before the next deploy |
| I7 | **Never touch a link we didn't create.** | The inspector only retargets or removes `Mods` if its target is under `<Root>\Deploy\` |
| I8 | **Reversible without the app.** | `Restore-Mods-Folder.cmd` plus the in-app Undeploy (§9.3) |
| I9 | **No destructive surprises at startup.** | The startup duplicate → `DeleteMods` path (`MainWindowViewModel.cs:1696-1704`) is bypassed for library variants. Multiple versions are a feature, not duplicates |

PR0's writer, in house style (Core, upstreamable):

```csharp
public static async Task<bool> CopyFileAsync(string copyFromPath, string copyToPath, CancellationToken token)
{
	var tempPath = copyToPath + ".part";
	try
	{
		using (var sourceFile = new FileStream(copyFromPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
		using (var outputFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128000, true))
		{
			await sourceFile.CopyToAsync(outputFile, 128000, token);
		}
		// Rename replaces the directory entry; it never writes through an existing hardlink.
		File.Move(tempPath, copyToPath, true);
		return true;
	}
	catch (Exception ex)
	{
		DivinityApp.Log($"Error copying file: {ex}");
		TryDelete(tempPath);
	}
	return false;
}
```

Same pattern for `MainWindowViewModel.cs:3302` and `:3429`. Upstream also gains "no truncated pak
left behind on a failed import". This is a fix in its own right, independent of this feature.

---

## 9. Migration and the way back

### 9.1 Preconditions (wizard page 1, read-only checks)

- Game not running. No other manager instance.
- `Mods` is a plain folder with no subfolders containing paks (README.md:21; also `LoadModPackageDataAsync`
  recurses, so they'd otherwise vanish from view). List them and ask the user to resolve.
- Root volume: local fixed NTFS, not OneDrive, free space shown. Same volume as Mods → "instant,
  no extra space". Different volume → "copies 27 GB, needs 27 GB free, about N minutes" (measured
  at run time from the first 1 GB).
- Show the counts: paks, multi-part sets, overrides, duplicates (same UUID, several versions:
  *these become variants*, not deletions).

### 9.2 Steps (all journalled; the original data is never moved until a full verified copy exists)

| Step | Action | Undo if interrupted |
|---|---|---|
| M1 | Scan Mods with the existing loader, but keep *all* duplicates (bypass `:789-806` grouping) | nothing written |
| M2 | For each variant: `CreateHardLink(Library\…\<file>, Mods\<file>)` (same volume) or copy via `.part` + rename (cross-volume). Set read-only | delete `Library\` entries whose identity has LinkCount ≥ 2 (same volume), or the copies (cross-volume). Mods untouched |
| M3 | Build `Deploy\_migration-snapshot\` = link of every file in Mods (paks *and* any loose non-pak files), so the game sees exactly what it saw before | delete the snapshot slot (SafeUnlink) |
| M4 | Move `Order` entries whose `UUID = <old Mods path>` into S6-A override rows matched by `Pak` (back up each order file to `Orders\_premigration\` first) | restore from backup |
| M5 | Ask: "Add the N override paks that are loaded today to all K saved orders as override rows? (keeps today's behaviour) / to none / let me choose" | order backups |
| M6 | `Directory.Move(Mods, Mods.pre-library-<date>)` (same parent: a rename) | rename back |
| M7 | `CreateDirectoryLink(Mods, Deploy\_migration-snapshot)`; verify count and identities | remove link, rename M6 back |
| M8 | state.json `Mode = JunctionSwap`, `ActiveSlot = _migration-snapshot`. Tell the user to run the game once (G-1) before deleting anything | — |
| M9 | Later, explicit button "Remove pre-library backup": SafeUnlink every file in the backup (each has LinkCount ≥ 2 by construction). Cross-volume case: ordinary recycle, since the library holds full copies | — |

Same-volume migration does no data copying: 1,097 `CreateHardLink` calls plus about 2,200 for the
snapshot, then two directory renames. Estimate: seconds **[inference; measure W-P1]**.
Cross-volume: bound by disk speed (27 GB at 200-500 MB/s ≈ 1-2.5 min SSD; HDD ≈ 3-5+ min).

### 9.3 Undeploy (in-app) and the app-free script

In-app "Stop using library deployment":
1. Choose what the plain folder should hold: *the active order's slot* (default), *all latest
   variants* (today's behaviour), or *the pre-library backup* if still present.
2. Build `Mods.restoring\` (sibling) by hardlinking the chosen set. Cross-volume root → copy.
3. `RemoveDirectoryLink(Mods)` (`JunctionPoint.Delete`, `:162-205`: removes the reparse point,
   then the empty directory. The slot is untouched).
4. `Directory.Move(Mods.restoring, Mods)`.
5. Mode = Off. The library and slots stay where they are. A separate "Delete library…" button lists
   which files would lose their last name (none of the ones in the new `Mods`, which holds links
   to them).

`Restore-Mods-Folder.cmd` (written to the root at migration, refreshed on every deploy with the
real paths, plain cmd, no admin):

```bat
@echo off
rem Restores a plain Mods folder from the currently deployed order. Does not delete the library.
set "MODS=C:\Users\me\AppData\Local\Larian Studios\Baldur's Gate 3\Mods"
set "SLOT=C:\Users\me\AppData\Local\BG3MM-Fork\Deploy\ord-7d1c4b0e"
rmdir "%MODS%" || (echo Mods is not a junction or is in use & exit /b 1)
mkdir "%MODS%"
for %%F in ("%SLOT%\*.pak") do mklink /H "%MODS%\%%~nxF" "%%F" >nul || copy /Y "%%F" "%MODS%\" >nul
echo Done. Mods is a normal folder again.
```

`rmdir` without `/s` on a junction removes only the junction **[inference: standard cmd behaviour;
W-9]**. `mklink /H` needs no admin. If the root is on another volume, the `copy` fallback runs.

---

## 10. Interplay with other stages

- **Stage 4 (startup cache keyed by path + size + mtime).** Ideal fit: library paths are stable and
  their bytes immutable (read-only), so hits approach 100%. The cache key must include the VariantId
  folder (it does, via the path). Deploy slots are **never** parsed, only enumerated for identities.
  The library holds more paks than Mods did (all versions), so a cold start without the cache gets
  slower in proportion to the extra variants. Stage 4 should land first, or together.
  `_AllPaksNames` (`DivinityModDataLoader.cs:429`) must become per-folder for the library scanner,
  or a part file in one variant folder could mask another. **Hardlink caveat for the S4 key**
  (`docs/review/stages/alternatives/S4-A-minimal.md` §0: relative path + length + last-write UTC + header/tail sentinel):
  directory-entry size and attributes are refreshed only at the link a change was made through
  **[src-web: MS hard links]**. A write-through via a slot link can leave the library path's
  enumerated length and mtime stale. In library mode the scanner must stat library files through a
  handle (`TryGetIdentity`, which also yields the FileIndex), not from `FindFirstFile` data. S4's
  sentinel is the second line of defence. `docs/review/stages/alternatives/LIB-A-hardlink-sync.md` makes the same point.
- **Stage 6.2 (overrides folded into the order).** This feature *needs* 6.2's semantics: in library
  mode an override is loaded only if its order deploys it. S6-A's `Layout` override rows are that
  per-order list: they're already never written to `modsettings.lsx`, so `BuildOutputList` needs no
  change. The deploy plan reads them. Without 6.2: the current `ForceLoadedMods`
  panel (`MainWindowViewModel.cs:5244-5246`) gets a checkbox per row, "deploy with this order".
- **Stage 6.3 (dividers).** Under S6-A, dividers are `Layout` rows of `Kind: "Category"`, never
  `Order` entries. `DeploymentPlan` reads only `Kind: "Override"` rows and passes everything else
  through. Collapsing a category has no effect on deployment.
- **Stage 8 (multi-order export).** modKey = `<uuid>@<VariantId>` is S8's key. Pak paths in the
  bundle are `paks/<uuid>/<version64>/…` (S8 §3.1). The same-Version64-different-bytes variant needs
  S8 to allow `<version64>-<sha8>` as the folder, a one-line note for S8. With pins, two orders can
  carry two variants of one UUID, which is exactly the case S8 §4.2 defers to LIB. Import into a
  library-mode install ingests every variant. Import into legacy mode keeps the highest and warns.
- **Stage 9 (downloader, sync).** Downloads stage into `Library\_incoming\` and become a *new
  variant*. An "update" never overwrites; it re-pins or floats. Revert = re-pin to the older
  variant, which makes S9's `Mods_Old_ModManager` backup idea (`docs/review/stages/S9.md` §2.5) unnecessary
  in library mode. Friend sync sends an order + pins (Sha256 filled at publish time). Missing
  variants are resolved per §5.2.

---

## 11. Hook points (path:line) with sketches

| ID | Where | Change |
|---|---|---|
| H1 | `MainWindowViewModel.cs:1354-1369` `SetGamePathways` | Replace `if (!Directory.Exists(modPakFolder)) CreateDirectory` with inspector logic. Create only when `Missing` *and* mode is Off. Dangling/foreign → alert, don't create |
| H2 | `MainWindowViewModel.cs:1660-1667` `LoadModsAsync` | Library mode: `LibraryScanner.LoadAsync(root, token)` instead of `LoadModPackageDataAsync(AppDataModsPath)`. Keep the Data-folder duplicate alert (`:1670-1690`). Skip the `DeleteMods(Duplicates)` call (`:1696-1704`) for library variants |
| H3 | `MainWindowViewModel.cs:2371` `RefreshAsync` | before "Loading mods…": `await DeploymentService.RecoverAndAdoptAsync(token)` |
| H4 | `MainWindowViewModel.cs:2099` `LoadModOrder` | `ApplyOrderVariants(order)` (§6.4). At `:2118` an unresolved pin → `Missing` |
| H5 | `MainWindowViewModel.cs:2931-2932` `ExportLoadOrderAsync` | deploy between `BuildOutputList` and `ExportModSettingsToFileAsync`. Override rows come from the order's `Layout`; `BuildOutputList` is unchanged |
| H6 | `MainWindowViewModel.cs:4359-4371` `DeleteMods`, `DeleteFilesViewData.cs:87` | library mode: the dialog offers "Remove from this order" / "Delete this version" / "Delete all versions". File ops go through `SafeFileOps` (I1, I2) |
| H7 | `MainWindowViewModel.cs:1840`, `:3233`, `:3394` imports | `outputDirectory = Library\_incoming` in library mode, then `LibraryIngest.IngestAsync`. The new variant floats in the *selected* order, so the user sees it immediately. It is deployed on next export |
| H8 | `DivinityFileUtils.cs:364`, `MainWindowViewModel.cs:3302`, `:3429` | PR0 temp-then-rename (§8) |
| H9 | `MainWindowViewModel.cs:5410-5416` ctor | second watcher: `fwService.WatchDirectory(PathwayData.AppDataModsPath, "*.pak")` → adopt (§6.3c) |
| H10 | `MainWindowViewModel.cs:3572-3573` archive export | uses `mod.FilePath`, which is now the deployed variant's library path after H4. Add multi-part files (S8 E7) |
| H11 | `DivinityModDataLoader.cs:607-622` | library mode: `UUID = pakPath` now resolves to the library path. The library key is `ovr:<hash16(file name)>@0`. Orders match by `Pak` (S6-A) |
| H12 | `DivinityLoadOrder.cs:8-21`, `:304-312` | `Pin`, `Id`, `Clone`, and the override-row subset of S6-A `Layout` if 6.2 hasn't landed (§5.1) |
| H13 | `MainWindowViewModel.cs:5424-5425` | revive the game-running check as `ProcessHelper.IsGameRunning(Settings)` and use it in deploy, migrate and undeploy |

H5 sketch:

```csharp
var finalOrder = DivinityModDataLoader.BuildOutputList(SelectedModOrder.Order, mods.Items, Settings.AutoAddDependenciesWhenExporting, outputAdventureMod);

if (Settings.DeploymentMode != DeploymentMode.Off)
{
	var deployer = Services.Get<IDeploymentService>();
	var slotKey = DeploymentService.GetSlotKey(SelectedModOrder, SelectedProfile);
	var deployResult = await deployer.DeployAsync(slotKey, SelectedModOrder, finalOrder, MainProgressToken?.Token ?? CancellationToken.None);
	if (!deployResult.Success)
	{
		// I4/I5: the game keeps the previous complete deployment and the previous modsettings.lsx.
		ShowAlert($"Deployment failed at '{deployResult.Step}': {deployResult.Message}", AlertType.Danger, 0);
		return false;
	}
}

var result = await DivinityModDataLoader.ExportModSettingsToFileAsync(SelectedProfile.Folder, finalOrder);
```

`ExportLoadOrderAsync` runs on the task pool (`:2839-2846`). `ShowAlert` marshals itself
(CLAUDE.md §5). Any `[Reactive]` write (e.g. a `DeployedSlotName` chip) goes through
`Observable.Start(…, RxApp.MainThreadScheduler)`.

`SlotSync` core (Core, toolkit-neutral):

```csharp
public sealed class SlotSync
{
	private readonly ILinkProvider _links;
	private readonly LibraryIndex _index;

	public SlotSync(ILinkProvider links, LibraryIndex index)
	{
		_links = links;
		_index = index;
	}

	public SyncResult Sync(string slotDir, DeploymentPlan plan, CancellationToken token)
	{
		var result = new SyncResult();
		Directory.CreateDirectory(slotDir);
		var present = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in Directory.EnumerateFiles(slotDir))
		{
			if (_links.TryGetIdentity(path, out var id)) present[Path.GetFileName(path)] = id;
		}

		foreach (var kvp in present)
		{
			token.ThrowIfCancellationRequested();
			if (!plan.TryGet(kvp.Key, out var wanted) || !wanted.Identity.SameFile(kvp.Value))
			{
				var path = Path.Combine(slotDir, kvp.Key);
				if (!SafeFileOps.TrySafeUnlink(_links, _index, path, out var reason))
				{
					result.Fail("unlink", path, reason);
					return result;
				}
				result.Removed++;
			}
		}

		foreach (var entry in plan.Entries)
		{
			token.ThrowIfCancellationRequested();
			var linkPath = Path.Combine(slotDir, entry.FileName);
			if (present.TryGetValue(entry.FileName, out var id) && entry.Identity.SameFile(id)) continue;
			try
			{
				_links.CreateHardLink(linkPath, entry.LibraryPath);
				result.Added++;
			}
			catch (IOException ex)
			{
				result.Fail("link", linkPath, ex.Message);
				return result;
			}
		}

		return result.Verify(slotDir, plan, _links);
	}
}
```

Windows hardlink P/Invoke (the only new native call besides the existing junction code):

```csharp
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

public void CreateHardLink(string linkPath, string existingPath)
{
	if (!CreateHardLinkW(@"\\?\" + Path.GetFullPath(linkPath), @"\\?\" + Path.GetFullPath(existingPath), IntPtr.Zero))
	{
		throw new IOException($"CreateHardLink failed ({Marshal.GetLastWin32Error()}): '{linkPath}' -> '{existingPath}'");
	}
}
```

---

## 12. Junction swap vs direct hardlink sync

Direct sync (angle A): `Mods` stays a plain folder, and switching unlinks what isn't in the order
and links what is.

| Criterion | B: junction swap | A: direct hardlink sync |
|---|---|---|
| Switch cost (935-mod order, typical diff of ~50-200 files) | slot sync (only when the order changed since its last deploy) + one FSCTL ≈ ms to ~0.3 s | ~50-200 link/unlink ≈ 0.05-0.3 s; full rebuild ~935 ≈ 0.3-1 s **[inference; W-P1]** |
| What dominates either way | `modsettings.lsx` write, UI refresh, the user | same |
| Library volume | anywhere local, since links stay inside the root | must share Mods' volume (C:), else copies (27 GB) |
| What the game, mod.io and other tools see | a reparse point **[unverified → G-1, G-4, G-8]** | a plain folder, the shape they were built for |
| Files written into Mods by others | land in the active order's slot, so they can be attributed per order | land in Mods, global; attribution by time only |
| Atomicity | the order changes in one operation, and a crash leaves old or new | a crash mid-sync leaves a mixed folder; the journal resumes it |
| Game running during switch | the swap *succeeds* under the game, so a guard is mandatory | unlinking paks the game holds open fails with sharing violations **[unverified → G-5]**: partial, so a guard is mandatory |
| `Directory.Delete(Mods, true)` by a tool | removes only the junction; slot intact | removes links; library intact |
| Library root deleted | `Mods` dangles, so no mods load | `Mods` still has full links, so the active order keeps working |
| Manager deleted (portable) | junction remains; works until the root is touched | nothing unusual remains |
| Explorer size confusion | root shows N × 27 GB | Mods + library show 2 × 27 GB |
| Backup-tool duplication | N slots | Mods only |
| Code size | A's engine + inspector, journal swap state, dangling handling, restore script (+1 PR) | engine + journal |
| Linux | symlink + `rename(2)` swap is clean and atomic | links inside the Proton prefix need the library on the prefix's filesystem |

Verdict: B isn't meaningfully faster at this scale. Its real advantages are library placement and
per-order capture of foreign files. Its real costs are the unverified game and tool interplay with
a reparse-point `Mods`, and the dangling-root failure. A is the safer default. Both share 90% of
the code (library, pins, ingest, adoption, `SlotSync`, `SafeFileOps`). **Build A's engine, keep the
slot abstraction (A is "slot = Mods"), and ship B as `DeploymentMode.JunctionSwap` behind G-1 to
G-5.** If the owner keeps the library on C: (the likely case, since Mods is already there), A
gives everything requested.

---

## 13. Linux (Proton) equivalent

- Mods path: `<steamapps>/compatdata/1086940/pfx/drive_c/users/steamuser/AppData/Local/Larian Studios/Baldur's Gate 3/Mods` **[inference: standard Proton layout; BG3 app id 1086940 is in the Steam URL found above]**.
- `Mods` → a symlinked directory to `~/.local/share/bg3mm-fork/Deploy/<slot>`. Symlinks need no
  privilege. Wine presents a Unix symlinked directory as an ordinary directory **[unverified → G-9]**.
- Atomic swap: `symlink(slot, "Mods.tmp")` then `rename("Mods.tmp", "Mods")`. rename(2) atomically
  replaces a symlink. It can't replace a non-empty real directory, which is the same safety net as
  NTFS.
- Hardlinks: `link(2)` via `[DllImport("libc", SetLastError = true)]`. Identity: `stat(2)`
  `st_dev`/`st_ino`/`st_nlink`. Same-filesystem rule only between Library and Deploy, so it always
  holds within the root.
- Prefix deletion (`rm -rf compatdata/1086940`): GNU `rm -r` doesn't follow symlinks, so only the
  link goes **[inference]**. Keeping the root outside `compatdata` keeps the library safe from
  Steam's "delete compat data".
- The read-only bit is `chmod a-w`. Unix `unlink` of a link needs write permission on the
  *directory*, not the file, so read-only files don't block our own `SafeUnlink`.
- The manager is WPF and doesn't run on Linux today. `UnixLinkProvider` is specified so Core stays
  portable. Build it when a Linux shell exists.

---

## 14. UI touch points

1. **Wizard** "Set up mod library" (Tools menu, a `Hotkey` with `[MenuSettings]` in
   `src/GUI/ViewModels/AppKeys.cs`, CLAUDE.md §4). Pages: checks (§9.1) → root and mode (Direct /
   Junction, with the §12 table in plain words) → overrides question (M5) → run with progress →
   "Launch the game once to confirm".
2. **Order bar chip:** "Deployed: Tactician run · 935 mods" / "Selected order not deployed". Click
   to deploy (same as Export).
3. **Version column:** pin glyph. Tooltip lists variants. Context menu "Version ▸ …" (§6.4). Order
   menu: "Pin all to current", "Unpin all".
4. **Overrides panel** (`ForceLoadedMods`): per-row "Deploy with this order" until 6.2 lands.
5. **Library view** (new tab or window): variants per mod, which orders pin or use each one, real
   disk usage, "Delete version", "Rebuild index", "Remove pre-library backup", "Stop using library".
6. **Alerts:** adoption prompt, drift report, tampering, dangling `Mods`. All via `ShowAlert`, with
   modal confirmation only for destructive choices.
7. Any new window must be added to `MainWindow.UpdateColorTheme` (plan Stage 7 note).

---

## 15. Performance at 1,097 paks / 27 GB / 935 in order

| Operation | Cost | Basis |
|---|---|---|
| Same-volume migration | ~3,300 link ops + 2 renames: seconds | **[inference; W-P1]** |
| Cross-volume migration | 27 GB copy: ~1-2.5 min SSD, more on HDD | disk bandwidth |
| First slot build (935 links) | ~0.1-0.5 s | **[inference; W-P1]** |
| Re-deploy unchanged order | enumerate + 935 identity opens ≈ 50-100 ms, 0 changes, 1 swap | **[inference]**. Optimisable with one `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)` pass per directory |
| Switch between two pre-built slots | ~ms | one FSCTL |
| Startup adopt check | enumerate active slot + identities ≈ 50-100 ms | as above |
| Startup mod parsing | library scan replaces Mods scan; more paks if many variants. Stage 4 cache makes it ≈ index load | Stage 0 baseline 9.3 s for mods |
| SHA-256 | only on ingest collisions (same UUID+Version64), adoption of duplicates, or S9 publish; never the 27 GB up front | design choice |
| Disk | library 27 GB once. Each link costs one MFT name record plus a directory index entry, so roughly 1 KB × links, under 10 MB for 10 slots **[inference]** | — |
| Memory | index ~1.1k entries + per-variant `DivinityModData`. Variants beyond the latest are parsed but not bound to the UI | — |

---

## 16. Open questions and in-game tests

Owner questions:
- **Q1** Library on C: (same volume as Mods), or on another drive? This decides A vs B more than anything else.
- **Q2** Do both profiles get used with *different* orders? If yes, B22 matters and per-profile warnings must ship in v1.
- **Q3** Default for paks that appear in Mods (in-game downloads): keep deployed, or library-only?
- **Q4** Is "deploy only on Export to game" right, or should selecting an order deploy it?
- **Q5** Keep `ProtectLibraryFilesReadOnly` on even if it breaks in-game mod.io updates (G-4)?
- **Q6** Should migration add today's overrides to every saved order (keeps behaviour) or to none (the clean slate you asked for)?

In-game / Windows tests that settle the [unverified] items (run on the owner's PC, with a backup of
`Mods` and `PlayerProfiles` first):

| ID | Test | Settles |
|---|---|---|
| G-1 | Make `Mods` a junction (`mklink /J`) to a folder holding 3 known paks, export an order using them, launch, check they're active in game | the game follows a junctioned Mods |
| G-2 | Put an override pak (no meta) in Mods but not in modsettings. Launch and confirm its effect. Remove it via deployment and confirm the effect is gone | the owner's premise and the fix |
| G-3 | Export modsettings listing a mod whose pak is absent. Launch. Diff modsettings afterwards | whether the game rewrites/drops entries (B8, B22) |
| G-4 | With Mods junctioned: subscribe, update and uninstall a mod in the in-game mod.io manager. Watch with Process Monitor (filter path contains `\Mods\`): file names, create vs in-place write, deletes, whether it touches the junction itself | B1-B3, read-only impact |
| G-5 | While the game runs, check open handles on paks (Process Explorer / `handle.exe Mods`). Try `del` on a pak link and a junction retarget | game holds paks open; guard necessity |
| G-6 | Pin an older variant whose Version64 is lower than another variant in the library. Export, launch, check the in-game version shown | the game reads the deployed pak's meta; Version64 in modsettings matches |
| G-7 | Rename a pak file (same content), launch | file name irrelevant to loading |
| G-8 | Put a non-pak file (`.txt`) into Mods. Separately, confirm the junctioned Mods doesn't trigger the "subfolder" reset | B13, slot rules |
| G-9 | Proton: symlinked Mods dir in the prefix, launch | Linux equivalent |
| W-3 | `JunctionPoint.Create(existing junction, newTarget, true)` while a file inside the old target is open | atomic in-place retarget |
| W-6 | Dangling junction: `Directory.Exists`, `Directory.CreateDirectory`, `DirectoryInfo.LinkTarget` results | B17 |
| W-7 | `FileSystemWatcher` on a junction path sees files created via the target path and via the junction | adoption trigger |
| W-8 | `Directory.EnumerateFiles(root, RecurseSubdirectories)` with a junction inside root | B18 |
| W-9 | `rmdir` on a junction (cmd), Explorer delete, `Directory.Delete(j, true)`: target intact | restore script, B4 |
| W-10 | Read-only hardlinked file: `File.Create` on the link fails. `File.Move(tmp, link, true)` replaces only the link. Recycle a link: other link keeps data | I3, I2 |
| W-P1 | Time 935 CreateHardLink + 935 DeleteFile in one dir on the owner's SSD. Time migration on a copy of the real Mods | §15 numbers |

---

## 17. Branch and PR breakdown

Fork-only `feat/*` off `fork/main`, except PR0 and PR1a.

| PR | Branch | Content | Est. |
|---|---|---|---|
| PR0 | `fix/atomic-pak-writes` (from master, **upstreamable**) | temp-then-rename for the three pak writers; delete `.part` on failure | 0.5 d |
| PR1a | `fix/junction-handle-check` (**upstreamable**) | `JunctionPoint.cs:297`: check `handle.IsInvalid` | 0.1 d |
| PR1 | `feat/lib-core` | `ILinkProvider`, `WindowsLinkProvider`, `FileIdentity`, `SafeFileOps`, `ModsFolderInspector`. New `tests/Core.Tests` (xUnit, net8.0-windows; pure-logic tests also run on Linux with a fake provider). Note: no test project exists today (CLAUDE.md §8.4) | 2 d |
| PR2 | `feat/lib-index` | `LibraryScanner`, `LibraryIndex`, `LibraryIngest`, `ovr:` library keys, per-folder partial-pak grouping, H2/H7/H11 behind `DeploymentMode != Off` | 3 d |
| PR3 | `feat/lib-pins` | `Pin`/`Id` JSON, S6-A override rows (if 6.2 not landed), `ApplyOrderVariants`, version context menu, missing-pin handling (H4, H12) | 2.5 d |
| PR4 | `feat/lib-deploy-direct` | `DeploymentPlan`, `SlotSync`, journal, `DeploymentService` direct mode, H1/H3/H5/H13, game-running and lock guards | 3 d |
| PR5 | `feat/lib-migrate` | wizard, M1-M9, undeploy, restore script, README writer | 2.5 d |
| PR6 | `feat/lib-adopt` | adoption, drift and tampering reports, watcher (H9), deletion redirect (H6) | 2 d |
| PR7 | `feat/lib-junction-swap` | junction mode: slots per order, retarget, dangling handling, per-profile warnings | 2 d |
| PR8 | `feat/lib-ui` | library view, chips, disk-usage display | 2 d |

Total ≈ 20 days of focused work. PR7 is the only part that is specifically angle B. Order: PR0 →
PR1 → PR2 → PR3 → PR4 → PR5 → PR6, then PR7 once G-1 to G-5 pass. Stage 4 ideally before PR2.
Stage 6.2 before or with PR4.

---

## 18. Test plan

Unit (Core.Tests, fake `ILinkProvider` with an in-memory inode table):
- SlotSync: empty→plan, plan→plan (0 ops), plan A→plan B diff counts, foreign file in slot is quarantined not deleted, LinkCount==1 file never unlinked (I1), file-name collision aborts.
- DeploymentPlan: pins resolve, floating resolves to newest, unresolved pin → error, multi-part sets kept whole, dividers and editor mods skipped, deps from `BuildOutputList` included, `Layout` override rows included in the plan and absent from the modsettings list (S6-A byte-identity invariant: no override rows → identical `modsettings.lsx` to master).
- JSON: old order loads (no Pin), new order round-trips, `EmitDefaultValue=false` keeps unpinned entries byte-identical to today's shape, `Clone` copies pins, "save as" clears `Id`.
- Journal recovery: a simulated crash after each step resumes to a consistent state.
- VariantId: same Version64 with different bytes → `-sha8` suffix; same bytes → dedupe.

Windows-only (manual or scripted on the owner's PC, temp folders only): W-3, W-6 to W-10, W-P1
(§16), plus:
- Migration on a *copy* of the real Mods (robocopy to a test AppData override via
  `DocumentsFolderPathOverride`, `DivinityModManagerSettings.cs:75-77`), then verify: identical file
  set seen through the junction, every original file has LinkCount 3, undeploy restores a plain
  folder byte-identical (hash list before/after).
- Cross-volume root on a second drive: copy path, free-space check.
- Kill the process (Task Manager) mid-sync and mid-swap; restart; recovery.
- Two instances deploying at once: the second gets the lock error.

In-game: G-1 to G-9. Plus the R7-style check from the plan: export a categorised, pinned order with
overrides, boot, and confirm the in-game mod list and the override's effect.

Regression: with `DeploymentMode = Off`, the whole app behaves exactly as master (diff the mod list
snapshot per plan R1).

---

## 19. Sources

- Microsoft, *Hard links and junctions*: hardlinks are same-volume, files only; changes through any link are visible through all; attributes are per-file; junctions can span local volumes, not network. https://learn.microsoft.com/windows/win32/fileio/hard-links-and-junctions
- Microsoft, *CreateHardLinkW*: NTFS only, 1023 links per file, ReFS not supported, security descriptor shared. https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw
- Microsoft, *Directory.Delete*: doesn't recurse through reparse points; mount points are unmounted (search summary). https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.delete ; implementation discussion https://github.com/dotnet/corefx/issues/24250
- Nexus Mods App developer notes, BG3: unlisted paks "considered disabled" but "may still override"; in-game mod.io manager downloads/toggles/removes; no load-order management at Patch 7. https://nexus-mods.github.io/NexusMods.App/developers/games/0003-BaldursGate3
- Community reports (search results, not authoritative): manual `mklink /J` of BG3 AppData folders (gameplay.tips guide, now 404); Patch 7 moved the Mods folder (https://forums.nexusmods.com/topic/11090483-bg3-mod-manager-and-patch-7-workaround); symlink deployment issues with another manager (https://forums.nexusmods.com/topic/13484716-symlink-deployment-run-as-administrator-only-available-deployment-method-2).
- In-repo: README.md:21 (no subfolders); `docs/review/stages/S8.md` §§0, 2.3, 3.3, 4.2 (modKey, E7 multi-part, P1 override UUID privacy); `docs/review/stages/S9.md` §§2.5, 3.2 (download staging, mod.io facts).
- Not found or not confirmed: where the in-game manager writes and whether it overwrites in place; whether BG3 holds paks open; how the game treats a junctioned or symlinked Mods; whether the game rewrites modsettings on missing paks. All are routed to G-tests.
