# LIB-A: Mod library, per-order hardlink deployment and version pinning

Angle A: **hardlink sync into the real Mods folder.** Status: design only. Nothing here is built. Written 2026-11-03 against `master` = upstream `fc35358`.

Every claim about how BG3 itself behaves is marked **[unverified]** and points to a numbered in-game test (G1 to G10, §13.3). Code claims cite `path:line` at `fc35358`.

---

## 0. Summary

- The manager gets a **library** at `%LOCALAPPDATA%\BG3ModManager\Library`. It is outside the Larian folder but, because both live under `%LOCALAPPDATA%`, on the same volume as `Mods`. The library keeps **every version ("variant") of every pak**, one folder per variant, with a JSON sidecar.
- The real `Mods` folder becomes a **deployment target**. On *Export Order to Game* the manager works out the exact set of files the order needs: its entries, the dependencies it auto-adds, the adventure mod, and the overrides you chose for that order. Each entry resolves to a pinned variant or to the latest one. The manager then **diffs** that set against `Mods`: it creates the missing hardlinks, swaps links whose version is wrong, and removes links that should not be there. Every step is journaled and rolled back on failure. `modsettings.lsx` is written only after the links succeed.
- **Hardlinks make the Mods folder disposable.** Every pak in `Mods` is a second name for a file the library also names. Removing a link from `Mods` deletes no data, and putting it back takes about a millisecond. The game sees an ordinary file (expected; G1).
- **Migration copies nothing.** "Adopting" today's 1,097 paks means creating 1,097 *extra* names in the library with `CreateHardLink`. No byte is copied or moved and `Mods` is not modified. The first deploy that follows then shows a preview of what would be removed.
- **Identity comes from the file system, not from bookkeeping.** A file in `Mods` counts as "ours" only if its NTFS file ID equals a library object's file ID. The manifest is advisory. Losing it only means files get re-adopted. It never causes a deletion.
- **This is fork-only** (§11). The one piece worth sending upstream is "write imports to a temp file, then rename". It is needed for safety here and also fixes partial-file-on-cancel bugs (§8, PR 0).
- **The biggest risk is the hardlink write-through hazard.** Anything that opens a deployed pak *for writing in place* rewrites the shared data, which corrupts that variant for every order. That includes three of the manager's own import paths today (§8.1), the game, or the in-game mod.io manager (G6, G7). Mitigations are in §9: S3, the read-only attribute, and temp-plus-rename everywhere.

---

## 1. What the owner asked for, restated as requirements

| # | Requirement | Where it is met |
|---|---|---|
| R1 | When I switch load orders, any mod not in the order is physically absent from `Mods`, so override paks (which load whenever they are present, per the owner [unverified, G4]) cannot load. | Deploy diff, §5 |
| R2 | Order A can use mod X v1.4 while order B uses v1.5. | Variants (§3), pins (§6) |
| R3 | Never lose user data. | Safety invariants, §9 |
| R4 | Errors are visible. Logging is off by default. | `ShowAlert` plus a modal list on every failure path, §5.6 |
| R5 | Measure first, and stay usable at 1,097 paks / 27 GB. | §10 |
| R6 | Keep a Linux/Proton path open. | `IFileOps` seam with a Unix implementation later, §2 |

Non-goals for v1: managing loose editor projects in `Data\Mods`, managing the game's `Data` paks, and deduplicating identical content automatically during a deploy.

---

## 2. Architecture

All logic lives in **Core**, in a new folder `src/Core/Library/`. The namespace is `DivinityModManager.Library`, file-scoped, and new types carry no prefix. GUI adds only view models and views. Nothing in Core references WPF types, so an Avalonia port keeps all of it.

```
src/Core/Library/
	FileIdentity.cs           readonly record struct (VolumeSerial, FileId128)
	IFileOps.cs               the seam for every file-system effect (fault injection in tests)
	WindowsFileOps.cs         P/Invoke: CreateHardLinkW, GetFileInformationByHandleEx, GetVolumeInformationByHandleW,
	                          GetFinalPathNameByHandleW, CreateSymbolicLinkW; plus File.Move/File.Copy wrappers
	LinkCapability.cs         probe: HardLink | Move | SymbolicLink | Copy, with the reason
	ModVariant.cs             one immutable version of one mod (1..n files: multi-part paks)
	LibraryMod.cs             UUID -> variants
	ModLibrary.cs             store: layout, sidecars, index, adopt, ingest, lookup, rebuild
	ModPin.cs                 pin model + resolver (Sha256 > Variant id > Version)
	DeploySpec.cs             per-order overrides membership (until Stage 6.2 folds them in)
	DeploymentManifest.cs     advisory record of what we linked, with kind and identity
	ModsSnapshot.cs           one-pass scan of Mods: name, size, mtime, attrs, FileIdentity
	DeploymentPlanner.cs      PURE function: (desired, snapshot, library, capability) -> plan
	DeploymentPlan.cs         ops + issues + counts (also the dry-run preview model)
	DeploymentExecutor.cs     journal, apply, verify, rollback, recover
	DeploymentJournal.cs      write-ahead journal on disk
	GameProcessGuard.cs       is bg3.exe / bg3_dx11.exe running
src/GUI/ViewModels/Library/
	DeployPreviewViewModel.cs, LibraryWindowViewModel.cs, EnableLibraryWizardViewModel.cs
src/GUI/Views/Library/
	DeployPreviewView.xaml(.cs), LibraryWindow.xaml(.cs), EnableLibraryWizard.xaml(.cs)
```

Services: register `ModLibrary` with `Services.Register` in the `MainWindowViewModel` ctor next to `IModRegistryService` (`Services.RegisterSingleton<IModRegistryService>` at `MainWindowViewModel.cs:4893`). Consumers null-check it, which is house practice. `MainWindowViewModel` stays the orchestrator because it owns `mods`, `SelectedModOrder` and `SelectedProfile`. Core does the work.

### 2.1 The `IFileOps` seam

```csharp
namespace DivinityModManager.Library;

public interface IFileOps
{
	FileIdentity GetIdentity(string path);
	uint GetLinkCount(string path);
	VolumeInfo GetVolume(string directory);
	void CreateHardLink(string newPath, string existingPath);
	void CreateSymbolicLink(string newPath, string targetPath);
	void Move(string from, string to);
	void CopyVerified(string from, string to, CancellationToken token);
	void DeleteLink(string path);
	void SetReadOnly(string path, bool readOnly);
	IEnumerable<ModsEntry> SnapshotDirectory(string directory);
}
```

`WindowsFileOps` is the only implementation in v1. A later `UnixFileOps` (`link(2)`, `stat` `st_dev`/`st_ino`/`st_nlink`, `symlink(2)`) slots in for Linux/Proton without the planner or executor changing. Core today targets `net8.0-windows` (CLAUDE.md §3), so the Unix implementation waits for whichever project first targets plain `net8.0`.

```csharp
internal static partial class Win32
{
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

	[DllImport("kernel32.dll", SetLastError = true)]
	internal static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FILE_ID_INFO info, int size);

	internal const int FileStandardInfo = 1;
	internal const int FileIdInfo = 18;

	[StructLayout(LayoutKind.Sequential)]
	internal struct FILE_ID_INFO
	{
		public ulong VolumeSerialNumber;
		public ulong FileIdLow;
		public ulong FileIdHigh;
	}
}

public sealed class WindowsFileOps : IFileOps
{
	private static string Long(string path) => path.StartsWith(@"\\?\") ? path : @"\\?\" + Path.GetFullPath(path);

	public void CreateHardLink(string newPath, string existingPath)
	{
		if (!Win32.CreateHardLinkW(Long(newPath), Long(existingPath), IntPtr.Zero))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateHardLink '{newPath}' -> '{existingPath}'");
		}
	}

	public FileIdentity GetIdentity(string path)
	{
		// FileAccess.None is not offered by File.OpenHandle; Read is fine (no data is read).
		using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (!Win32.GetFileInformationByHandleEx(h, Win32.FileIdInfo, out var info, Marshal.SizeOf<Win32.FILE_ID_INFO>()))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		return new FileIdentity(info.VolumeSerialNumber, new UInt128(info.FileIdHigh, info.FileIdLow));
	}
	// ...
}
```

Why these calls (sourced):
- `CreateHardLinkW` works only on NTFS, only for files, and only on one volume. It allows at most 1023 links per file. It is **not supported on ReFS** in Microsoft's Windows 8 table, so the capability probe must be empirical. Paths over MAX_PATH need the `\\?\` prefix. Source: [CreateHardLinkW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw).
- File attributes are per file and so shared by all links: "if you clear the read-only attribute flag on a particular hard link so you can delete that hard link … the other hard links display that the read-only attribute is still set, which isn't true". Directory-entry size and attributes are refreshed "only at the link through which the change was made". Source: [Hard links and junctions](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions). Consequence: **never trust `FindFirstFile` metadata for a linked file. Use a handle.** This also matters for Stage 4 (§12).
- `FileIdInfo` (128-bit) is used instead of `nFileIndexHigh/Low` so the identity is also valid on ReFS.

---

## 3. On-disk layout

### 3.1 Location rule

| Situation | Library default | Mode |
|---|---|---|
| `Mods` is a real folder under `%LOCALAPPDATA%` (the owner's case) | `%LOCALAPPDATA%\BG3ModManager\Library` | HardLink |
| `Mods` (or the Larian folder) is a junction/symlink to another drive, or `appDataGameFolderOverride` points elsewhere (`MainWindowViewModel.cs:1330-1342`) | `<volume root of the *final* Mods path>\BG3ModManager-Library` | HardLink |
| User picks a path on another volume | Refused with an offer to relocate (§7) | — |

"Same volume" is decided by comparing `FILE_ID_INFO.VolumeSerialNumber` of handles opened on both directories (with `GetFinalPathNameByHandleW` to resolve junctions). A real hardlink probe follows: create `Library\.probe`, then `CreateHardLink(Mods\.bg3mm-probe.tmp, Library\.probe)`, then delete both. The probe needs the game to be closed and lasts milliseconds. Path strings are never compared.

Why not inside `Mods`: `LoadModPackageDataAsync` enumerates `Mods` **recursively** (`DivinityModDataLoader.cs:756-762` with `RecursiveOptions`, `DivinityFileUtils.cs:12-17`), so the manager itself would load every library variant as a duplicate. The README also says subfolders in `Mods` make the game reset `modsettings.lsx` (`README.md:21`) [unverified, owner-reported].

Why not inside `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\`: common troubleshooting advice is to delete that tree, including the Mods folder and `modsettings.lsx` ([Pro Game Guides](https://progameguides.com/baldurs-gate/how-to-fix-bg3-mod-manager-not-working-in-patch-7/)). Larian or the launcher could also clean it [unverified]. The library must survive that. `%LOCALAPPDATA%` is also not OneDrive-synced and not covered by Controlled Folder Access by default.

Why not next to the exe: the manager is portable (`README.md:13`). Its folder (`DivinityApp.GetAppDirectory()`) is often on another drive.

### 3.2 Tree

```
%LOCALAPPDATA%\BG3ModManager\Library\
	library.json                         index cache (rebuildable from sidecars)
	library.json.bak
	manifest.json                        advisory: what is linked into Mods right now
	journal\
		2026-11-03T14-22-05Z_7f3a.json   in-flight deploy (deleted on commit)
	staging\                             imports land here, then are renamed into mods\
	mods\
		CombatExtender_c1f3a9e2\                     <SafeFolder ≤40 chars>_<sha1(UUID)[0..8]>
			1.4.2.0_a1b2c3d4\                        <Version>_<qfp[0..8]>  (opaque variant id)
				CombatExtender.pak                   same file (same FileId) as Mods\CombatExtender.pak when deployed
				variant.json
			1.5.0.0_9e8f7a6b\
				CombatExtender.pak
				variant.json
		HugeArmorPack_5d77c0aa\
			2.0.0.0_0bad1dea\
				HugeArmorPack.pak
				HugeArmorPack_1.pak                  multi-part paks: one variant, n files
				variant.json
		_override_NoCameraClamp_e019b2c4\            meta-less override (UUID was a path)
			0.0.0.0_77aa11bb\
				NoCameraClamp.pak
				variant.json
```

The folder name uses `sha1(UUID)` rather than the UUID itself. A mod's UUID is not guaranteed to be a GUID: meta-less override paks get `UUID = <full pak path>` (`DivinityModDataLoader.cs:612-621`), and that is not a valid folder name.

The variant id is `<Version>_<qfp8>`. `qfp` is a *quick fingerprint*: SHA-256 over `size ‖ first 64 KiB ‖ last 64 KiB`, which costs 128 KiB of reads per pak. A full SHA-256 is computed in the background and stored in the sidecar (§10). The quick fingerprint only *names* a variant. It is **never** used to decide that two files are equal before deleting anything (S1).

### 3.3 `variant.json` (sidecar, the source of truth)

```json
{
	"Schema": 1,
	"UUID": "c1f3a9e2-5f0b-4b1e-9a0e-2f1c3d4e5f60",
	"Folder": "CombatExtender",
	"Name": "Combat Extender",
	"Author": "someone",
	"Version64": 36169534507319296,
	"Version": "1.4.2.0",
	"Files": [
		{ "Name": "CombatExtender.pak", "Size": 48211968, "LastWriteUtc": "2026-06-14T09:12:44Z", "Sha256": "a1b2c3d4e5…" }
	],
	"QuickFingerprint": "a1b2c3d4…",
	"Source": { "Kind": "Adopted", "From": "Mods\\CombatExtender.pak", "At": "2026-11-03T14:20:11Z" },
	"Parsed": {
		"LoaderSchema": 1,
		"IgnoredModsHash": "5e0c…",
		"IsForceLoaded": false,
		"IsForceLoadedMergedMod": false,
		"ModType": "Add-on",
		"Dependencies": [ { "UUID": "…", "Name": "ImpUI", "Version64": 0 } ]
	},
	"Label": ""
}
```

`Parsed` is a metadata cache. It is valid only while `LoaderSchema` and `IgnoredModsHash` both match. Override classification depends on `IgnoredMods.json` (`DivinityModDataLoader.cs:747`, `builtinMods`), and that file changes after game patches (CLAUDE.md §6). The `Source.Kind` values are `Adopted`, `Imported`, `Downloaded` and `Relocated`. `Label` is a free-text user note, for example "pre-patch-8".

`library.json` is `{ "Schema": 1, "Variants": [ { "Dir": "...", "Id": "...", "UUID": "...", "FileIds": ["<vol>:<id>"] , ... } ] }`. It is a cache of all sidecars plus the last-seen `FileIdentity` of each library file. If it is missing or corrupt, it is rebuilt by walking `mods\*\*\variant.json`. That takes under 1 s for about 1,200 small files (§10).

### 3.4 `manifest.json` (advisory)

```json
{
	"Schema": 1,
	"ModsPath": "C:\\Users\\K\\AppData\\Local\\Larian Studios\\Baldur's Gate 3\\Mods",
	"Deployed": { "Order": "Tactician run", "Profile": "Public", "At": "2026-11-03T14:22:07Z" },
	"Links": [
		{ "File": "CombatExtender.pak", "Variant": "CombatExtender_c1f3a9e2/1.4.2.0_a1b2c3d4", "Kind": "HardLink", "Identity": "3a5f…:00000000000000000004000000012c7a" }
	]
}
```

The **authoritative** test for "managed" is `GetIdentity(Mods\x) == GetIdentity(libraryFile)`. The manifest only adds the link kind (needed for symlink and copy modes) and the "deployed order" label for the UI. Writes are atomic: write `manifest.json.tmp`, then `File.Move(tmp, manifest.json, overwrite: true)`.

---

## 4. Data model and JSON changes

### 4.1 Order entries gain an optional pin

`src/Core/Models/DivinityLoadOrder.cs:7-21`:

```csharp
[DataContract]
public class DivinityLoadOrderEntry
{
	[DataMember] public string UUID { get; set; }
	[DataMember] public string Name { get; set; }

	/// <summary>Null = follow the latest variant in the library (today's behaviour).</summary>
	[DataMember(EmitDefaultValue = false)] public ModPin Pin { get; set; }

	public bool Missing { get; set; }

	public DivinityLoadOrderEntry Clone()
	{
		return new DivinityLoadOrderEntry() { Name = this.Name, UUID = this.UUID, Missing = this.Missing, Pin = this.Pin?.Clone() };
	}
}

[DataContract]
public class ModPin
{
	[DataMember(EmitDefaultValue = false)] public string Variant { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Version { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Sha256 { get; set; }

	public ModPin Clone() => new() { Variant = Variant, Version = Version, Sha256 = Sha256 };
}
```

Also fix `DivinityLoadOrder.Update` (`:236-253`) so it does **not** clear `Pin`. It currently rewrites `UUID`/`Name`/`Missing` only, which is fine, but a pin-unaware future edit could drop it, so add a test. `DivinityLoadOrder.Clone()` (`:304-312`) copies `Order.ToList()`, which is a *shallow* copy: entries are shared, so editing a pin on a clone would edit the original. Change it to `Order.Select(x => x.Clone()).ToList()`.

### 4.2 Orders gain an optional deploy spec

```csharp
[DataContract]
public class DeploySpec
{
	/// <summary>Force-loaded/override mods deployed with this order. Same identification as S6-A Layout Override rows: Pak file name, plus UUID when it is a real GUID.</summary>
	[DataMember(EmitDefaultValue = false)] public List<DeployOverrideRef> Overrides { get; set; }
}

[DataContract]
public class DeployOverrideRef
{
	[DataMember] public string Pak { get; set; }
	[DataMember(EmitDefaultValue = false)] public string UUID { get; set; }
	[DataMember(EmitDefaultValue = false)] public ModPin Pin { get; set; }
}

// in DivinityLoadOrder
[DataMember(EmitDefaultValue = false)] public DeploySpec Deploy { get; set; }
```

### 4.3 Example order file (`Orders\Tactician run.json`)

Today an order file is only `{"Order":[{"UUID","Name"}]}`. `Name` and `FilePath` are not `[DataMember]` (`DivinityLoadOrder.cs:23-31`), and `Name` comes from the file name (`DivinityModDataLoader.cs:1168`). With this design:

```json
{
	"Order": [
		{ "UUID": "c1f3a9e2-5f0b-4b1e-9a0e-2f1c3d4e5f60", "Name": "Combat Extender",
		  "Pin": { "Variant": "1.4.2.0_a1b2c3d4", "Version": "1.4.2.0", "Sha256": "a1b2c3d4e5f6…" } },
		{ "UUID": "77e0c0de-0000-4000-8000-000000000001", "Name": "Better Hair" }
	],
	"Deploy": {
		"Overrides": [
			{ "Pak": "NoCameraClamp.pak" },
			{ "Pak": "UnlockLevelCurve.pak", "UUID": "e5c9f00d-1111-4222-8333-444455556666", "Pin": { "Version": "2.1.0.0" } }
		]
	}
}
```

### 4.4 Compatibility rules

| Reader | Behaviour |
|---|---|
| Upstream or older fork build reading a new file | Newtonsoft ignores unknown members (default `MissingMemberHandling.Ignore`, and `DivinityJsonUtils.SafeDeserialize` at `DivinityJsonUtils.cs:30-38` adds an error handler that swallows errors). Pins and Deploy are ignored, so the order loads as "latest". **If that build saves the order, pins are lost.** Document this. The library keeps `library.json` independent, so nothing else is lost. |
| New build reading an old file | `Pin == null` means latest. `Deploy == null` means a **legacy order**: overrides membership is unknown, so the deploy preview defaults to "keep the overrides currently in Mods" and asks once (§5.3). The answer is written into `Deploy`. |
| Pin resolution | 1) `Sha256` matches a variant, 2) else `Variant` id matches, 3) else exactly one variant with that `Version`. Otherwise **unresolved**. An unresolved pin is a *blocking* preview issue. There is no silent fallback to latest, because that is what the user pinned against. The user can choose "Use latest for these N" in the preview, which clears those pins. |
| `modsettings.lsx` | Unchanged format. `Version64` written per mod is the **resolved variant's** (§5.5). This is the only place the game sees a version. |
| Meta-less overrides | In-memory UUID stays `Path.Combine(AppDataModsPath, deployedFileName)`, **the same string as today** (`DivinityModDataLoader.cs:620`), even though the bytes live in the library. Existing orders that hold such a UUID through `ForceAllowInLoadOrder` keep matching. On disk, Deploy identifies overrides by `Pak` file name (portable, matches S6-A `Layout` rows) so orders can be shared (Stage 8/9). |
| Stage 6.3 dividers | A divider entry resolves to no mod, so the planner skips it the same way `BuildOutputList` will. `Pin` is never set on a divider. |

### 4.5 Settings (`DivinityModManagerSettings`, all three attributes, CLAUDE.md §4)

```csharp
[DefaultValue(false)]
[SettingsEntry("Mod Library", "Keep every mod version in a library and hardlink only the active order's mods into the Mods folder")]
[DataMember, Reactive] public bool LibraryEnabled { get; set; }

[DefaultValue("")]
[SettingsEntry("Mod Library Path", "Must be on the same drive as the Mods folder. Empty = automatic")]
[DataMember, Reactive] public string LibraryPath { get; set; }

[DefaultValue(DeployPreviewMode.WhenRemovingOrAdopting)]
[SettingsEntry("Deploy Preview", "When to show the list of Mods-folder changes before exporting")]
[DataMember, Reactive] public DeployPreviewMode DeployPreview { get; set; }

[DefaultValue(true)]
[SettingsEntry("Protect Library Files", "Mark library paks read-only so nothing can overwrite a version in place")]
[DataMember, Reactive] public bool ProtectLibraryReadOnly { get; set; }

[DefaultValue(false)]
[SettingsEntry("Pin Versions On Export", "When exporting, pin every unpinned mod in the order to the version being deployed")]
[DataMember, Reactive] public bool PinOnExport { get; set; }
```

Turning `LibraryEnabled` on goes through the wizard (§7), not a bare checkbox. The settings entry is read-only in Preferences, or opens the wizard.

---

## 5. The deployment algorithm

The trigger is **Export Order to Game** (`ExportLoadOrderAsync`, `MainWindowViewModel.cs:2897`), and therefore also Save on the "Current" order (`:2624-2629`). Selecting an order in the dropdown (`:5197-5220`) only loads it into the lists. The game sees nothing until export, so that is the moment the Mods folder must change too. This keeps "what the game sees" equal to `modsettings.lsx` plus `Mods`, written together. (Open question Q1: auto-deploy on selection.)

### 5.1 Inputs

1. **Desired set D.** Start from `BuildOutputList(order.Order, allMods, Settings.AutoAddDependenciesWhenExporting, adventure)` (`DivinityModDataLoader.cs:1431`), which already adds dependencies and the adventure. Drop base-game and editor mods (`IgnoredMods`, `IsEditorMod`): they are not in `Mods`. Add `order.Deploy.Overrides`. Resolve each to a `ModVariant` (pin, else latest). D maps `deployedFileName → (variant, file)`, including every part of a multi-part pak.
2. **Snapshot S** of `Mods`: one directory enumeration. Then a handle-based `FileIdentity` and link count for `*.pak` entries only. Bulk variant: `GetFileInformationByHandleEx(dir, FileIdExtdDirectoryInfo)` returns 128-bit ids for a whole directory in one call, which avoids 1,097 opens; it is a perf option, see §10. Subdirectories and non-pak files are recorded and **never touched**. Subdirectories raise a warning, per the README.
3. **Library index L** (in memory).
4. **Capability C** from the probe: HardLink | Move | SymbolicLink | Copy.

### 5.2 Classify every `*.pak` in S

| Class | Test | Action |
|---|---|---|
| Managed | `S.Identity == L.IdentityOf(variantFile)` for some library file | none yet |
| Managed-copy | Copy mode only: manifest says `Copy`, and size plus full SHA-256 equal the library file | none yet |
| Drifted | manifest lists the name but the identity differs. Someone replaced the file: mod.io update, an Explorer copy, a manual edit. | **Adopt** as a new variant |
| Unknown | not managed and not drifted | **Adopt** |

**Adopt** means: parse the pak (the loader already did, `LoadModDataFromPakAsync`, `DivinityModDataLoader.cs:731`), create its variant dir, then `CreateHardLink(libraryFile, ModsFile)`. That adds a *name* to the same data. The Mods file is not opened for writing, moved or renamed. Write the sidecar. After adoption the file is Managed. If a variant with the same `Sha256` already exists, the new one is marked `DuplicateOf` and stays as is. Reclaiming the space is a separate, explicit library action (§8.4), never part of a deploy.

### 5.3 Plan (pure, `DeploymentPlanner.Plan`)

```
for each name n in S (managed, after adoption):
	if n not in D                         -> Unlink(n)
	else if identity(n) != identity(D[n]) -> Unlink(n), Link(n <- D[n])      (version swap or name reuse)
	else                                  -> Unchanged
for each name n in D not in S             -> Link(n <- D[n])
```

Issues the plan can carry:

| Issue | Blocking? | Notes |
|---|---|---|
| unresolved pin | yes | |
| two desired variants want the same file name | yes | Two different mods ship `Foo.pak`. Today that is impossible in one folder too. |
| D contains a mod with no library variant | yes | Missing mod. Same message as `DisplayMissingMods`. |
| legacy order, overrides unknown | no | The preview asks. Default: keep currently-deployed overrides. |
| override(s) present now but not in D | no | Highlighted in red. This is the owner's case. |
| Mods has subfolders | no | Warning, untouched. |
| other profile's `modsettings.lsx` references mods that will be removed | no | Warning (G8, Q4). |

Operation order inside the plan is all `Adopt`, then all `Unlink`, then all `Link`. Unlink comes before link so that name reuse (a version swap with the same file name) works.

### 5.4 Preview (dry run)

`Plan` has no side effects. The only effect before confirmation is adoption, and adoption only *adds* library names. Even that is deferred: the preview lists "N unknown files will be adopted", and adoption runs as the executor's first phase. The preview dialog shows:

```
Deploy "Tactician run" to profile "Public"
	unchanged 702 · add 31 · remove 188 · switch version 4 · adopt 2
	REMOVE (overrides)  NoCameraClamp.pak, CheatTweaks.pak        <- red
	REMOVE              ...186 more (expand)
	SWITCH              Combat Extender 1.5.0.0 -> 1.4.2.0 (pinned)
	ADOPT (unknown)     SomethingNew.pak (UUID ..., v1.0.0.0)
	[ Deploy & Export ]  [ Export without deploying ]  [ Cancel ]
```

"Export without deploying" writes `modsettings.lsx` only, which is today's behaviour. It is the escape hatch. `DeployPreview` decides when the dialog appears: Always, WhenRemovingOrAdopting (default), or Never.

### 5.5 Apply (`DeploymentExecutor.ApplyAsync`)

```
0. Guard: GameProcessGuard.IsRunning() -> refuse (alert + modal). Re-check immediately before step 3.
1. Re-snapshot Mods; if it differs from the snapshot the plan was built on -> re-plan (cheap) and,
   if the diff changed materially, re-show the preview.
2. Write journal\<ts>.json with the full op list and each op's precondition
   (expected identities), FileOptions.WriteThrough + Flush(true).
3. For each op i (adopt, unlink, link):
	a. verify precondition (identity / absence) - if violated, stop -> rollback
	b. perform
	c. verify postcondition (identity of new link == library file; old name absent)
	d. append "done": i to the journal (small JSON-lines append + Flush(true))
4. Write modsettings.lsx exactly as today, with Version64/MD5/Name from the RESOLVED variant
   (BuildOutputList over resolved metadata, §8.2). On failure -> rollback all links.
5. Write manifest.json (tmp + rename). Delete the journal. ShowAlert(success with counts).
6. If PinOnExport: write pins for unpinned entries into the order and save the order JSON.
```

Unlink under `ProtectLibraryReadOnly`: the attribute is per file, so it is shared with the library name. The sequence is clear RO, `DeleteFile(Mods\x)`, then set RO again **through the library name**, because the attribute shown through the deleted link is stale (MS docs quoted in §2.1). A crash between those steps leaves RO cleared, which is harmless. Recovery re-applies RO.

The S1 precondition for Unlink is: `identity(Mods\x) == identity(libraryFile) && GetLinkCount(libraryFile) >= 2`. If the counts disagree, the op fails and rolls back. It never "deletes anyway".

### 5.6 Failure handling and rollback

| Failure | Response |
|---|---|
| Any op in step 3 throws (sharing violation, access denied, AV lock, disk full, path too long) | Roll back completed ops in reverse. Link becomes DeleteLink of the new name. Unlink becomes CreateHardLink again from the library (always possible: the library name exists by S1). Adopt is **not** rolled back: it only added library names and leaves them harmless. Then `ShowAlert(Danger)` plus a modal naming the file and the Win32 error. `modsettings.lsx` is untouched. |
| Rollback itself fails on some op | Keep the journal. The modal lists exactly which names are in which state. Startup recovery (below) offers "Retry rollback" or "Finish deploy". |
| modsettings write fails (step 4) | Roll back links. Same messages as today's "Is the file locked?" path (`MainWindowViewModel.cs:2984-2993`). |
| Process killed or power loss | Next start, `RefreshAsync` (`MainWindowViewModel.cs:2340`) finds a journal. Recovery re-snapshots and, for each op, decides *done / not done* from identities, not from the "done" marks (the marks are hints). It offers roll back (default) or roll forward, then deletes the journal. |
| Game started mid-deploy | Step 3a fails on a sharing violation, or the guard fires. Roll back. |

On Windows, deleting **any** name of a file that someone has open requires that opener to have passed `FILE_SHARE_DELETE`. Sharing is per file, not per link ([CreateHardLinkW remarks](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw)). Since Windows 10 1809/1909, `DeleteFileW` uses POSIX semantics on NTFS when it can: the name disappears at once while existing handles keep the data ([FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex); the version cut-over is community-reported, not documented). Whether bg3.exe opens paks with `FILE_SHARE_DELETE`, keeps them open for the whole session, or memory-maps them is **[unverified, G5]**. If it maps them, delete fails regardless. Either way, changing the mod set under a running game is undefined. Hence:

```csharp
public static class GameProcessGuard
{
	private static readonly string[] _names = ["bg3", "bg3_dx11"];

	public static bool IsRunning(out string which)
	{
		foreach (var n in _names)
		{
			var procs = Process.GetProcessesByName(n);
			try
			{
				if (procs.Length > 0)
				{
					which = n + ".exe";
					return true;
				}
			}
			finally
			{
				foreach (var p in procs) p.Dispose();
			}
		}
		which = "";
		return false;
	}
}
```

The same check already exists as a comment at `MainWindowViewModel.cs:5424-5425`, and upstream avalonia keeps it commented out too (`ModOrderViewModel.cs:2141` there). Under Proton the process appears as `bg3.exe` inside wine. On Linux the check must look at `/proc/*/cmdline` (future `UnixFileOps`).

---

## 6. Version pinning in practice

- **Variants appear by themselves.** Each import of a newer pak becomes a new variant (§8.1). Older variants stay until the user removes them. Several paks in `Mods` with the same UUID are today's "duplicates" (`DivinityModDataLoader.cs:788-803`, `MainWindowViewModel.cs:1673-1711`). On adoption they become variants, and the duplicate-delete dialog is not shown in library mode (§8.2).
- **Latest.** The highest `Version64`, then the newest `Source.At`. That tie-breaker matters: many BG3 mods ship updates without bumping `Version64`. Today's loader, with equal versions, keeps whichever pak the `ConcurrentBag` yields first (`DivinityModDataLoader.cs:790-801`, strict `>` at `:799`), so the current choice is effectively **nondeterministic**. Content-addressed variants make the choice deterministic and visible.
- **Pin UX.** Right-click a mod in the active list, then "Version ▸", which lists every variant (Version, label, date, size, "latest" mark), then "Unpin (follow latest)". The Version column shows a pin glyph and the pinned version. "Pin all to current" pins every entry to the variant now deployed, which freezes an order. `PinOnExport` does this automatically.
- **The UI keeps one row per UUID.** `SourceCache<DivinityModData,string>` stays keyed by UUID (`MainWindowViewModel.cs:80`) and holds the *latest* variant's `DivinityModData`. The order layer, not the store, knows about pins: `LoadModOrder` (`:2093-2152`) sets a new `[Reactive] string PinnedVersion` on each mod it activates and clears it on the others. Swapping `DivinityModData` instances per order would churn DynamicData and the lists (Stage 6.1 concerns).
- **Known v1 limitation.** Dependency warnings in the UI use the *latest* variant's `meta.lsx`. The export path uses the pinned variant's dependencies (§8.2). The UI shows a tooltip note when they differ.

---

## 7. Migration from today's plain Mods folder, and the way back

### 7.1 Enable (wizard)

1. Preconditions: game not running; `Mods` exists; capability probe. HardLink is expected for the owner (NTFS `C:`).
2. Choose the library path (default from §3.1) and show "extra disk used: 0 B now. Older versions you keep will use space."
3. **Adopt everything.** For each pak the loader already parsed this session, create the variant dir, `CreateHardLink`, write the sidecar, compute the quick fingerprint. `Mods` is left byte-for-byte and name-for-name identical. The wizard verifies that afterwards with a snapshot diff and shows "0 files changed in Mods".
4. Write the manifest. `Deployed.Order` is `"(unmanaged)"`.
5. Background job: full SHA-256 of every variant (cancellable, resumable, progress in the status bar, §10).
6. The next Export shows the preview, including any overrides that will now leave `Mods`. Nothing is removed before the user sees that list.

### 7.2 The way back

| Action | Effect | Data risk |
|---|---|---|
| **Disable library (keep Mods as is)** | Delete `manifest.json`. Stop deploying. `Mods` keeps whatever is deployed: those are ordinary files, because hardlinks are real directory entries. | none |
| **Restore full Mods folder** | Deploy the "everything" set: the latest variant of every UUID plus all overrides. This recreates the pre-library state minus duplicate versions. Then disable. | none |
| **Remove library from disk** | Per variant: if `GetLinkCount >= 2`, the data also lives in `Mods`, so deleting the library name loses nothing. If the count is 1 (only in the library), offer *move into Mods* (rename, same volume) or *Recycle Bin*. Never a permanent delete. | none, by construction |
| Manager deleted or uninstalled without any of the above | `Mods` still works: real files. The library stays in `%LOCALAPPDATA%\BG3ModManager`. | none |

### 7.3 Fallback chain when hardlinks are impossible

| Step | When | How it deploys | Trade-off |
|---|---|---|---|
| 1. HardLink | same volume, NTFS (probe passes) | `CreateHardLinkW` | best: library always complete, Mods disposable |
| 2. Move | same volume, hardlinks unsupported (exFAT/FAT32, ReFS without link support, Dev Drive [probe]) | `File.Move` library → Mods and back (O(1) rename) | The library is *incomplete* while a variant is deployed: deleting from Mods in Explorer then really deletes. The UI says so. |
| 3. Offer to relocate the library to the Mods volume | library on another volume | one-time progress job: copy, verify SHA-256, then Recycle the source **only after verify**. Free space is checked first. | 27 GB copy once (§10) |
| 4. SymbolicLink | user declines relocation, and `CreateSymbolicLink` with `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE` (0x2) succeeds (Developer Mode on Win10 build 14972+, or elevated) | `File.CreateSymbolicLink` (.NET 6+; if it does not pass the unprivileged flag, P/Invoke `CreateSymbolicLinkW` with 0x2) | Game must follow file symlinks **[unverified, G2]**. It almost certainly does, because `CreateFile` follows them unless `FILE_FLAG_OPEN_REPARSE_POINT` is passed. If the target drive is absent, the mod is missing and the game may reset modsettings [unverified, G8]. Background: [meziantou](https://meziantou.net/create-symbolic-links-in-dotnet.htm), [PowerShell #7967](https://github-redirect.dependabot.com/PowerShell/PowerShell/issues/7967). |
| 5. Copy | last resort | `CopyVerified` to `Mods\x.tmp`, verify hash, rename to `x`. Unlink a copy only after verifying size + SHA-256 equal the library file. | Slow, and the space is doubled |

Also considered (angle B territory, noted for comparison): make `Mods` itself a **junction** to `Library\deployments\<order>\`, a per-order folder of hardlinks. Switching orders then retargets one junction: O(1), cross-volume capable, no admin. It is rejected as the default for v1. It replaces the game's real `Mods` directory, game behaviour with a junctioned `Mods` is [unverified], and unknown files dropped by the game or mod.io land inside a per-order folder. The planner and executor here could drive it: it is just another `LinkCapability`.

Also rejected: renaming inactive paks in place to `*.pak.off`. It mutates user files and needs name-mangling for multiple versions, and whether the game ignores non-`.pak` files is [unverified].

---

## 8. Hook points in the existing code

### 8.1 Write paths that MUST change first (hardlink write-through)

`File.Create` / `FileMode.Create` on an existing path **truncates the existing file object**. When that path is a hardlink, the library variant and every other order using it are overwritten. Today's import paths write in place:

| Site | Code |
|---|---|
| `MainWindowViewModel.cs:1840-1845` → `DivinityFileUtils.CopyFileAsync` `DivinityFileUtils.cs:364` | `File.Create(copyToPath, …)` |
| `MainWindowViewModel.cs:3302` (ImportCompressedFileAsync) | `File.Create(outputFilePath, …)` into `AppDataModsPath` |
| `MainWindowViewModel.cs:3429` (ImportArchiveAsync) | `File.Create(outputFilePath, …)` into `AppDataModsPath` |
| `src/GUI/ViewModels/ModUpdatesViewData.cs:180,205` | `File.Copy(…, overwrite: true)` (dead UI per CLAUDE.md §6, still fix) |

**PR 0 (upstreamable, `fix/import-write-temp-rename` from master).** Write to `<dest>.tmp-<guid>` in the same folder, then `File.Move(tmp, dest, overwrite: true)`, and delete the temp in `finally`. `MoveFileEx(MOVEFILE_REPLACE_EXISTING)` replaces the *directory entry*: the old link goes and the other links' data is untouched. As a side effect this fixes "cancel leaves a truncated pak in Mods", which is in the same family as Stage 2's E1/E3. Coordinate with the Stage 3 `fix/archive-import` branch, which edits the same function. The `.tmp-…` name does not end in `.pak`, so the loader filter (`DivinityModDataLoader.cs:761`) ignores it.

```csharp
public static async Task<bool> CopyFileAsync(string copyFromPath, string copyToPath, CancellationToken token)
{
	var tempPath = copyToPath + ".tmp-" + Guid.NewGuid().ToString("N");
	try
	{
		using (var sourceFile = new FileStream(copyFromPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
		using (var outputFile = File.Create(tempPath, 128000, FileOptions.Asynchronous))
		{
			await sourceFile.CopyToAsync(outputFile, 128000, token);
		}
		File.Move(tempPath, copyToPath, true);
		return true;
	}
	catch (Exception ex)
	{
		DivinityApp.Log($"Error copying file: {ex}");
	}
	finally
	{
		try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
	}
	return false;
}
```

In library mode, imports instead go to `Library\staging\`. There they are parsed, renamed into a new variant dir (same volume, O(1)), deduplicated by SHA-256 against existing variants, and **then** linked into `Mods` only if the selected, deployed order wants the latest variant. Otherwise they stay library-only until the next deploy. `AddImportedMod(mod, toActiveList)` (`:1853`, `:3323`, `:3451`) is unchanged: the mod shows up in the lists either way.

### 8.2 Load and export

**`LoadModsAsync`** `MainWindowViewModel.cs:1660-1716`:

```csharp
if (Settings.LibraryEnabled && Library != null)
{
	await SetMainProgressTextAsync("Loading mod library...");
	// Reads sidecars (Parsed cache) for every variant; parses only variants whose cache is stale;
	// snapshots Mods and adopts unknown/drifted paks; returns latest-per-UUID + the variant table.
	modLoadingResults = await RunTask(Library.LoadAsync(PathwayData.AppDataModsPath, builtinMods, cancelTokenSource.Token), null);
}
else if (Directory.Exists(PathwayData.AppDataModsPath))
{
	// today's path, unchanged
}
```

In library mode `modLoadingResults.Duplicates` holds only *true* duplicates: the same UUID in two variants of identical SHA-256. Those never trigger the `DeleteMods(..., true, ...)` dialog at `:1709`. The library window offers "reclaim space" for them instead. Refactor `LoadModPackageDataAsync` (`DivinityModDataLoader.cs:745`) into `LoadModsFromPaksAsync(IEnumerable<string> pakPaths, …)` plus the enumeration, so the library can feed it variant files directly. The meta-less override UUID must be computed from the **deployed** name (`DivinityModDataLoader.cs:620`): pass an optional `Func<string,string> uuidPathFor`.

**`ExportLoadOrderAsync`** `MainWindowViewModel.cs:2930-2932`:

```csharp
string outputPath = Path.Combine(SelectedProfile.Folder, "modsettings.lsx");
var resolver = Settings.LibraryEnabled ? Library?.CreateResolver(SelectedModOrder) : null;
var finalOrder = DivinityModDataLoader.BuildOutputList(SelectedModOrder.Order, mods.Items, Settings.AutoAddDependenciesWhenExporting, outputAdventureMod, resolver);

if (resolver != null)
{
	var plan = Library.Plan(SelectedModOrder, finalOrder, resolver, SelectedProfile, Profiles);
	var decision = await ConfirmDeployAsync(plan);   // marshals to the UI thread; returns Deploy / ExportOnly / Cancel
	if (decision == DeployDecision.Cancel) return false;
	if (decision == DeployDecision.Deploy)
	{
		var deployResult = await Library.ApplyAsync(plan, () => DivinityModDataLoader.ExportModSettingsToFileAsync(SelectedProfile.Folder, finalOrder), MainProgressToken.Token);
		// ApplyAsync writes modsettings itself between links and manifest (§5.5 step 4) and rolls back on failure
		if (!deployResult.Success)
		{
			await Observable.Start(() => ShowDeployFailure(deployResult), RxApp.MainThreadScheduler);
			return false;
		}
		result = true;
	}
	else
	{
		result = await DivinityModDataLoader.ExportModSettingsToFileAsync(SelectedProfile.Folder, finalOrder);
	}
}
else
{
	result = await DivinityModDataLoader.ExportModSettingsToFileAsync(SelectedProfile.Folder, finalOrder);
}
```

`BuildOutputList` (`DivinityModDataLoader.cs:1431-1470`) gains an optional `IModVariantResolver resolver`. When it is present, `mData` is the resolved variant's `DivinityModData` snapshot, so `Version64`, `MD5`, `Name`, `Folder` and the dependency walk (`GetDependencyMods`) all use the pinned version. `GenerateModSettingsFile` (`:1472-1489`) is unchanged.

**Also:** `BackupCurrentLoadOrderAsync` (`:2850`) must save pins (it uses `SetOrder`, which copies entries, so pins carry over). Before each deploy, back up every profile's `modsettings.lsx` to `Library\backups\<ts>\<profile>.lsx` (Q4, G8).

### 8.3 Other touch points

| Where | Change |
|---|---|
| `SetGamePathways` `MainWindowViewModel.cs:1357` | after `AppDataModsPath` is set, `Library?.SetModsPath(...)` and re-probe the capability |
| `RefreshAsync` `:2340` (before step "load mods") | `Library.RecoverIfJournalPresentAsync()`, which prompts on the UI thread |
| `LoadModOrder` `:2116-2134` | set `mod.PinnedVersion = entry.Pin?.Version ?? ""` for active mods. Clear it in the deactivate loop `:2101-2105`. |
| `ExportLoadOrderToArchiveAsync` `:3572-3573` | in library mode, `modPaks` comes from the resolver (the pinned variant's library file path), and overrides come from `order.Deploy.Overrides`, not from all `ForceLoadedMods`. Zip names stay `Path.GetFileName`. For Stage 8 see §12. |
| `DeleteMods` `:4359` / `DeleteFilesViewData.cs:87` | in library mode "Delete" becomes "Remove from library": choose variants (default: all), refuse variants pinned by any saved order (listed), unlink from Mods, then Recycle the library name. `RecycleBinHelper.DeleteFile` on a read-only file needs RO cleared first [verify on Windows: T-W7]. |
| ctor `ForceLoadedMods` binding `:5244-5246` | each row gains a "Deploy with this order" toggle bound to `SelectedModOrder.Deploy.Overrides`. Stage 6.2 replaces this (§12). |
| `Keys.ExportOrderToGame` `:5163` | unchanged binding. `ExportLoadOrder` → `ExportLoadOrderAsync` carries the deploy. |
| `Keys.LaunchGame` `:897` | if library mode is on and a quick snapshot shows Mods drifted from the manifest, alert "Mods folder changed since last deploy (N unknown files)". It does not block. |
| `AppKeys.cs` | `[MenuSettings("File", "Preview Deployment...")] public Hotkey PreviewDeploy`; `[MenuSettings("Tools", "Mod Library...")] public Hotkey OpenModLibrary`; `[MenuSettings("Tools", "Undeploy (Restore Full Mods Folder)...")]`. The menu is generated by reflection (CLAUDE.md §4). |
| Mods watcher | `Services.Get<IFileWatcherService>().WatchDirectory(AppDataModsPath, "*.pak")` (`FileWatcherService.cs:104`). It only drives a "drifted" badge and never auto-acts. The watcher is paused during apply, like `_modSettingsWatcher.PauseWatcher` at `:2629`. |

Threading: `Plan`/`ApplyAsync` run on the taskpool. Progress goes through the existing `MainProgress*` properties, wrapped in `RxApp.MainThreadScheduler`, because Fody `[Reactive]` setters raise on the calling thread (CLAUDE.md §4). `ShowAlert` already marshals. Each command body calls `ShowAlert` explicitly in its catch, because the repo has no `ThrownExceptions` subscriptions.

### 8.4 Library window (Tools → Mod Library…)

Columns: mod, variants (count), which orders pin each variant, deployed? (✓ / kind), size, "only in library" bytes (variants with link count 1, which is the real extra disk use), labels. Actions: label a variant, remove unused variants (Recycle, refusing pinned ones), reclaim duplicates (same SHA-256: keep one, recycle the other's library name **only** after a full-hash compare, then relink Mods if needed), verify library (re-hash), rebuild index, relocate.

Explorer shows both `Mods` and `Library` at full size, and naive backup tools copy hardlinked data twice. The window states the true number.

---

## 9. Safety guarantees and their enforcement

| # | Guarantee | Enforced by |
|---|---|---|
| S1 | **Never remove the last name of any pak.** | `Unlink` precondition: `identity(Mods\x) == identity(lib)` and `GetLinkCount(lib) >= 2`. Copy mode: library copy SHA-256 verified equal. Move mode: an unlink is a move back, never a delete. These are executor-level asserts, not planner logic, so a planner bug cannot bypass them. Unit-tested with fault injection. |
| S2 | Unknown files are only ever adopted: one name added in the library, never modified, moved or deleted. | Adopt opens the Mods file for read only (parse) and calls `CreateHardLink(lib, mods)`. Drifted files are adopted as new variants, never "repaired". |
| S3 | No in-place writes to library data. | PR 0 temp+rename on every manager write path (§8.1), plus the `ProtectLibraryReadOnly` attribute, which turns an accidental in-place open into an `UnauthorizedAccessException` instead of silent corruption. The game opening paks with write access while RO is set is [unverified, G3]. If G3 fails, RO defaults to off and S3 relies on G6/G7 results. |
| S4 | The Mods folder and `modsettings.lsx` change together or not at all. | Journal, ordered apply, rollback; `modsettings.lsx` written after links, links rolled back if it fails. |
| S5 | No deploy while the game runs. | `GameProcessGuard` before plan, before apply and before step 3; sharing violations roll back. |
| S6 | Every failure is visible. | `ShowAlert(Danger, 0)` plus a modal with file names and Win32 messages. The journal stays for recovery. The deploy summary is always alerted, so success is visible too. |
| S7 | Non-pak files and subfolders in Mods are never touched. | The snapshot filters `*.pak` at the top level, the same as the game's expected layout. |
| S8 | Removing variants goes to the Recycle Bin, with a list, and never touches pinned or deployed variants. | Library window and `DeleteMods` changes. |
| S9 | Losing `library.json` or `manifest.json` loses nothing. | The index is rebuilt from sidecars. The manifest is advisory: identity comparison re-derives "managed", and re-adoption of a file that is already a library object is a no-op (same identity). |
| S10 | Losing a sidecar loses nothing. | A variant dir without a sidecar is re-parsed from its pak. A pak in `mods\` that is not in any dir is adopted as a variant. |
| S11 | Every op is reversible until commit. | All ops are link-level. The data always has a library name (S1). |

---

## 10. Performance at 1,097 paks / 27 GB

These are estimates to be measured (T-P1..P4). Hardlink operations are metadata-only, so their cost does not depend on pak size.

| Operation | Work | Estimate | Notes |
|---|---|---|---|
| Snapshot Mods | 1 enumeration + identity per pak | 30–150 ms (1,097 × ~30–100 µs handle open) | Bulk `FileIdExtdDirectoryInfo` cuts it to 1–2 syscalls. Use that if T-P1 shows > 200 ms. Defender usually skips opens that request no data access, but `File.OpenHandle(Read)` may be scanned; if so, P/Invoke `CreateFileW(FILE_READ_ATTRIBUTES)`. |
| Plan | in-memory set diff | < 5 ms | |
| Typical switch (≈200 unlink + 200 link) | 400 metadata ops + journal appends | 0.1–0.5 s | NTFS link/unlink ≈ 0.1–1 ms each (to measure) |
| Full deploy from empty Mods (935 + deps + overrides ≈ 1,000 links) | 1,000 `CreateHardLink` | 0.2–1.2 s | |
| Adoption at migration | 1,097 links + 1,097 sidecars + quick fingerprints (128 KiB each ≈ 140 MB read) | 2–6 s | sidecar writes dominate with AV on |
| Full SHA-256, background, once | 27 GB read | NVMe ≈ 10–20 s; SATA SSD ≈ 50–60 s; HDD ≈ 3–4 min | Runs once. Cached by `FileIdentity + size + mtime` in `library.json`. Parallelism 2–4 on SSD, 1 on HDD. Lowest priority, pausable. |
| Library load at startup (warm) | read about 1,200 sidecars (or one `library.json`) | 50–200 ms vs 9.27 s today for pak parsing (Stage 0.3) | Only valid if `Parsed` caches are current. This *is* a Stage 4 cache for library mode (§12). |
| Relocate library across volumes (fallback 3) | 27 GB copy + verify | 2–10 min | one-time, progress, resumable |

Memory: about 1,200 `ModVariant` records holding strings, a few MB. Parsed `Files` sets (`DivinityModData.cs:234`) are **not** stored in sidecars (too large). They are rebuilt only for variants the UI asks about, or kept only for the latest variant, as today.

---

## 11. Fork-only, and branch/PR breakdown

**Fork-only. The argument for:** it changes what the manager does to the user's Mods folder, adds persistent state outside the app folder, and changes the order JSON. Upstream is busy with the Avalonia rewrite, and `upstream/avalonia` has no library, deploy or link concept: a grep for hardlink/symlink/deploy finds only `JunctionPoint` used by `RegistryService.cs:32-34` there. A one-person upstream would be taking on support for a feature that can delete user-visible files. **The exception:** PR 0 is a generic robustness fix (temp+rename on import) and goes upstream from clean `master`.

| # | Branch (base) | Content | Effort |
|---|---|---|---|
| 0 | `fix/import-write-temp-rename` (master) | §8.1, 3 sites + `ModUpdatesViewData` | 0.5–1 d |
| 1 | `feat/lib-fileops` (fork/main) | `IFileOps`, `WindowsFileOps`, `FileIdentity`, `LinkCapability` probe, `GameProcessGuard`; new `tests/Core.Tests` (xUnit, `net8.0-windows`, the first test project in the repo, CLAUDE.md §8.4) | 2–3 d |
| 2 | `feat/lib-store` | `ModLibrary` layout, sidecars, index, adopt, staging ingest, fingerprints, background hashing, rebuild | 3–4 d |
| 3 | `feat/lib-deploy` | `DeploymentPlanner` (pure), `DeploymentExecutor`, journal, recovery, manifest | 4–5 d |
| 4 | `feat/lib-pins` | `ModPin`, `DeploySpec`, JSON compat, resolver, `BuildOutputList` resolver param, `Clone` fix | 2 d |
| 5 | `feat/lib-integration` | `LoadModsAsync`/`ExportLoadOrderAsync`/archive export/`DeleteMods`/imports-to-staging hooks, settings | 3–4 d |
| 6 | `feat/lib-ui` | enable wizard, deploy preview, pin context menu + Version column glyph, library window, menu keys; both themes key-for-key (CLAUDE.md Stage 7 note) | 4–6 d |

Total ≈ 19–25 working days. Merge order: 1 → 2 → 3 → 4 → 5 → 6, each `--no-ff` into `fork/main`. Feature-flagged by `LibraryEnabled` (default off) until G1–G8 pass.

---

## 12. Interplay with other stages

- **Stage 2 (zip export).** PR 0 is in the same family as E1/E3. `ExportLoadOrderToArchiveAsync` should already be restructured around `(orders, mod set)`. Library mode then supplies the mod set as *variants*.
- **Stage 3 (archive import).** Same functions as PR 0 (`ImportArchiveAsync` `:3391-3524`). Land PR 0 first or fold it into `fix/archive-import`.
- **Stage 4.** The S4-A design (`docs/review/stages/alternatives/S4-A-minimal.md`) uses `Data/modcache.json`, keyed by relative path + length + `LastWriteTimeUtc` + a 72-byte header sentinel. Three points:
	- (a) For hardlinked files, directory-entry size and mtime can be **stale** (MS docs, §2.1). An enumeration-sourced `FileInfo` can therefore disagree with the file. Library objects are immutable, so a stale value is still a *stable* value, and the header sentinel catches real content changes. No false cache hit is expected unless something writes through a link (G7).
	- (b) In library mode, scan the library rather than `Mods` and key on `variant id + LoaderSchema + IgnoredModsHash`. Variants never change, so the sidecar's `Parsed` block is that cache. S4-A's cache and this one should share one `PakScanResult` → `DivinityModData` builder (`BuildModDataFromScan` in S4-A).
	- (c) The R1 snapshot/diff discipline applies: compare library-mode loading with plain loading on the same folder.
- **Stage 6.2.** S6-A (`docs/review/stages/alternatives/S6-A-data-model.md`) keeps `Order` mods-only. It puts overrides in an optional `"Layout"` member as `{ "Kind": "Override", "Pak": "<file>.pak", "UUID"?: … }` rows, identifying a pak **by file name** for the same reason given here (no-meta UUIDs are local paths).
	- **Converge on that.** When S6-A lands, an order's override membership is its `Layout` Override rows, and the planner reads them. `Deploy.Overrides` is needed only if this feature ships first; in that case it uses the same `{Pak, UUID?}` identification and is folded into `Layout` on load.
	- **Semantic change to flag to S6.** S6-A calls these rows "pinned" because "the game always loads it [GAME]". In library mode that stops being true: an override row *not* in the order is not deployed. So removing an override from an order means something, and the lock glyph should become a normal "remove" affordance when `LibraryEnabled`.
	- **Terminology clash.** S6-A's "pinned override" (position-locked) vs this design's "version pin". Rename one before both ship. Suggestion: "locked override".
	- S6-A's G2 (override priority is decided by the engine) is orthogonal to deployment.
- **Stage 6.3 (dividers).** Dividers resolve to no variant and are skipped. They never get pins. The Version-column pin glyph is one more cell in the row; render it with the 6.1 cached-text approach, not a new `TextBlock`.
- **Stage 8 (multi-order export).** S8 (`docs/review/stages/S8.md`) uses this bundle layout:
	- `orders/<name>.json` in the upstream shape;
	- `paks/<uuid>/<version64>/<file>`;
	- a `bundle.manifest` with SHA-256 per mod and per-order version pins;
	- mods keyed by `modKey = uuid@version64`;
	- the v1 policy "one version per UUID per bundle".

  It was explicitly left open for this design, and pins fill that gap: the bundle union is over **variants**. One correction to feed back: **`Version64` is not a sufficient variant key.** Many mods ship updates without bumping it (§6), so two different variants can share `uuid@version64`. S8's `modKey` should become `uuid@version64@sha8`, or the `paks/` folder should use this design's variant id (`<Version>_<qfp8>`) and look variants up by the manifest SHA-256. Pins in the order JSON (`Pin.Sha256`) and S8's manifest pins should be the same data. A library-mode importer puts each bundle pak into `Library\staging\` and dedupes by SHA-256.
- **Stage 9 (downloader, sync).** `Pin { Sha256, Version }` plus a future `Source { Nexus file id | mod.io id }` is the sync payload. A friend's manager resolves by SHA-256. The downloader writes only into `Library\staging\`, never into `Mods`.

---

## 13. Test plan

### 13.1 Unit (Core.Tests, run on Windows; planner tests are platform-neutral)

- Planner table tests: empty→full, full→empty, swap version with the same file name, swap with a different file name, multi-part variants, unknown + drifted adoption, name collision (blocking), unresolved pin (blocking), legacy order with no `Deploy`, divider entries, meta-less override keys.
- Executor with a fault-injecting `IFileOps`: fail at op *k* for every *k* in a 50-op plan. The assertion is that the final Mods state, compared name by name and identity by identity, equals the initial state, and that S1 never fires a delete with link count < 2.
- Journal recovery: truncate the journal after each op; recovery yields either the before state or the after state, never a mix.
- JSON: an old order loads with null pins; pins round-trip; `EmitDefaultValue=false` keeps unpinned entries byte-identical to today's shape; `Clone()` deep-copies pins.

### 13.2 Windows-only integration (real NTFS temp dirs; `[Trait("os","windows")]`)

- T-W1: hardlink identity and link count; RO attribute shared across links; deleting an RO link fails until RO is cleared (confirms §5.5 sequence).
- T-W2: `File.Create` on a hardlink truncates the shared data (proves the §8.1 hazard). `File.Move(tmp, dest, true)` leaves the other link intact (proves the fix).
- T-W3: delete a link while another process holds the *other* name open with `FileShare.Read` (expect sharing violation) and with `FileShare.Read|Delete` (expect success, POSIX semantics on Win10 1909+).
- T-W4: capability probe on NTFS, exFAT USB stick (expect Move), ReFS/Dev Drive (record the result), a second NTFS volume (expect relocate offer).
- T-W5: symlink with Developer Mode off and on, unelevated.
- T-W6: long paths (> 260) through `\\?\` P/Invoke.
- T-W7: `RecycleBinHelper.DeleteFile` on an RO file and on one link of a two-link file (only that name goes to the bin; the data survives through the library name).
- T-W8: Mods junctioned to D: (DivinityRegistryHelper-style setups) → library default goes to D:.

### 13.3 In-game checks (owner; each settles an [unverified] claim)

| # | Question | Procedure | Pass |
|---|---|---|---|
| G1 | Does the game load hardlinked paks normally? | Enable library, deploy an order, launch, load a save that uses several mods | mods active; no "mods missing" |
| G2 | Does it follow file symlinks in Mods? | Symlink mode with Developer Mode, same as G1 | same |
| G3 | Does it load **read-only** paks? Does it ever open them for write? | G1 with RO on. Separately, Process Monitor filter `bg3.exe`, path contains `\Mods\`, record the Desired Access | loads; no write access requested |
| G4 | Owner's premise: an override pak present in Mods but absent from modsettings loads; absent from Mods it does not | Pick a visible override (UI or camera change). Test with it in Mods (not in modsettings), then removed by deploy | effect present, then gone |
| G5 | Does the game keep paks open, with what share mode, or map them? | `handle.exe -p bg3.exe Mods` at the main menu and in-game. Process Explorer → DLLs/Mapped Files | records the answer; decides how strict S5 must be |
| G6 | Does the game write anything into Mods (new files, modified paks, deletions) at launch, in play or at exit? | Before/after snapshot: names, `FileIdentity`, size, mtime, SHA-256 (manager's "Verify library") | no change (if it changes, S3 needs more) |
| G7 | In-game mod.io manager: where it installs, file names, whether an update overwrites in place (write-through!) or replaces the file | Install, update and uninstall one mod.io mod while library mode is on; then "Verify library" | library hashes unchanged; drifted file adopted as new variant |
| G8 | With two profiles, does switching to a profile whose modsettings references undeployed mods make the game rewrite that modsettings? | Deploy order A for Public, then in-game select profile 2 (order B) | record; decides Q4 handling (backup is in place either way) |
| G9 | Version swap is real | Order A pins X v1, order B uses X v2. Export each, launch; check the version in game (mod's own UI or SE console `Ext.Mod.GetMod("<uuid>").Info.ModVersion`) | correct version each time |
| G10 | Subfolder in Mods resets modsettings (README claim) | Only to justify "library outside Mods"; optional | record |

### 13.4 Performance (T-P)

- T-P1: snapshot time at 1,097 paks, cold and warm, with Defender on.
- T-P2: full deploy and typical-switch timings. Record `CreateHardLink`/`DeleteFile` per-op cost.
- T-P3: adoption time at migration. Full-hash throughput on the owner's disk.
- T-P4: startup with library mode vs today's 9.27 s mod-loading baseline (Stage 0.3), with the R1 mod-list diff clean.

---

## 14. Open questions only the owner (or a test) can answer

1. **Deploy trigger.** Deploy on *Export to game* only (recommended), or also when selecting an order? Selecting without exporting would desync Mods from `modsettings.lsx`.
2. **Legacy orders.** For an order with no `Deploy` block, which overrides go with it by default: the ones now in Mods (recommended, nothing changes until you choose), none, or all?
3. **Two profiles share one Mods folder.** Deploying for Public removes paks profile 2 needs. Is "one deployed order at a time" acceptable, given that the game might rewrite profile 2's modsettings on switch (G8)? The alternative is a "deploy union of both profiles' orders" mode, which defeats R1 for overrides.
4. **Read-only protection** on by default (pending G3)?
5. **Unknown files.** Adopt silently and list them in the preview (recommended), or ask per file?
6. **Delete** in library mode: remove all variants or only the selected one? Should it ever touch a pinned variant?
7. Does the owner use the **in-game mod.io manager**? If yes, G7 is a blocker before enabling by default.
8. **Pin on export** default: on (orders replay exactly what you last played) or off (orders follow updates)?
9. Disk budget for old variants: auto-prune unpinned old variants after N days, or keep forever?

---

## 15. Biggest risks

1. **Hardlink write-through.** Any in-place writer corrupts a variant for every order. The manager's own three import paths do this today (§8.1); the game or mod.io might (G6, G7). Mitigations: PR 0 lands first, RO attribute, drift detection by identity, and "Verify library" hashes.
2. **Undocumented game behaviour** (G4, G5, G8): override loading, open-file semantics, and profile modsettings resets. The feature stays behind `LibraryEnabled` (default off) until G1–G8 pass on the owner's machine.
3. **Two profiles, one folder** (Q3). Pre-deploy backups of every profile's `modsettings.lsx` limit the damage. They do not prevent the game resetting a profile.
4. **Variant identity when `Version64` is not bumped.** Solved by content fingerprints, at the cost of a one-time 27 GB hash in the background.

Sources used for Windows semantics: [CreateHardLinkW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw), [Hard links and junctions](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions), [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex), [OpenJDK on POSIX delete semantics](https://mail.openjdk.org/pipermail/jdk-dev/2019-May/002816.html), [meziantou: symlinks in .NET](https://meziantou.net/create-symbolic-links-in-dotnet.htm). BG3 folder location (community, unverified): [Nexus forum](https://forums.nexusmods.com/topic/11090483-bg3-mod-manager-and-patch-7-workaround), [Steam discussion](https://steamcommunity.com/app/1086940/discussions/2/4632610585628937686).
