# LIB-C: Mod library, per-order deployment and version pinning — data model, identity and lifecycle

Angle C: **the model.** How the app goes from "one `DivinityModData` per UUID, read from the Mods folder" to "a library of content-addressed versions grouped by mod, with per-order version pins, deployed per order". This angle owns identity, the store, the pin format, the version-swap lifecycle, migration, deletion, profiles, patches, and how the other stages fit. The *file-system mechanics* of deployment (hardlink probe, journal, rollback, FileId checks) are specified in detail by the sibling designs `docs/review/stages/alternatives/LIB-A-hardlink-sync.md` (direct hardlink sync of `Mods`) and `docs/review/stages/alternatives/LIB-B-junction-swap.md` (junction swap). This document reuses that engine and only restates it where the model changes its inputs (§6).

Status: design only. Nothing is built. This container cannot build WPF or run the game. Code claims cite `path:line` at `master` = upstream `fc35358`. Claims about BG3 itself are tagged **[unverified]** with a test id (G-n, §15), **[src-web]** with a source (§19), or **[inference]**.

---

## 0. Summary

- **Identity is two-level.** A *mod* is a `ModKey`: the meta.lsx UUID, or `ovr:<hash16>` for an override pak with no meta.lsx (whose in-app UUID is today a full file path, `DivinityModDataLoader.cs:613-621`). A *version* is a `ContentId`: the full SHA-256 of the pak bytes (multi-part paks hash their parts). Version64 is shown to humans and used for ordering, never for identity, because authors often ship new bytes under an old Version64. The hash is computed **once**, during ingest, while the bytes are being copied or read anyway. It is cached by path + size + mtime (the Stage 4 key) until the file is in the library, and from then on it lives in the version's sidecar.
- **The store stays one row per UUID.** `SourceCache<DivinityModData,string> mods` (`MainWindowViewModel.cs:80`) keeps its UUID key. Each key now holds the **effective version for the selected order**. Switching orders swaps the `DivinityModData` instances of only the UUIDs whose pins differ. The app already does exactly this swap when a mod is re-imported (`AddImportedMod`, `MainWindowViewModel.cs:3160-3227`). Every existing `mods.Lookup(uuid)` keeps working, and `BuildOutputList` (`DivinityModDataLoader.cs:1431-1469`) writes the pinned version's Version64 and dependencies into `modsettings.lsx` with no change to the function.
- **Pins live on the order, not on the row.** `UpdateOrderFromActiveMods` (`MainWindowViewModel.cs:4880-4888`) clears and rebuilds `Order` from `ActiveMods` 250 ms after *any* active-list change (`:5407`). A pin stored only on `DivinityLoadOrderEntry` would therefore be wiped by the first drag. The in-memory authority is `DivinityLoadOrder.Pins` (ModKey → `VersionPin`). It is written to JSON as an optional `"Pin"` on each `Order` entry, plus an optional top-level `"Pins"` for keys that have no entry: auto-added dependencies and overrides. Older builds ignore both.
- **The library owns the bytes. `Mods` becomes a deployment target.** Library location and the hardlink mechanics follow LIB-A (`%LOCALAPPDATA%\BG3ModManager\Library`, same volume as `Mods`). Migration **adopts by hardlink**: each pak gains a second name in the library, and `Mods` is not touched until the first deploy preview is confirmed. Nothing is moved, so there is no window in which a pak exists in only one half-written place.
- **Lifecycle verbs are distinct and named in the UI.** *Remove from order* changes no files. *Undeploy* unlinks a name from `Mods`. *Delete version* moves the library copy to the library trash. *Clean up unused versions* is an explicit, previewed garbage collection. Nothing deletes bytes on its own, and every bytes-level removal goes to a reversible trash first.
- **Fork-only.** It changes what the `Mods` folder *means*. One prerequisite is upstreamable on its own: temp-file-plus-rename for the three pak writers (§13, PR 0).
- **Biggest risk: the model and the disk disagree.** The model may believe a version is deployed when it isn't, or that a library file is immutable when something has rewritten it through a hardlink (the write-through hazard, LIB-A §0). The defences are: FileId is the authority (not the manifest), the read-only attribute is set, every writer uses temp-plus-rename, "Verify library" re-hashes, and the game-running guard. The second risk is undocumented game behaviour (G1-G9), so the feature ships behind `LibraryEnabled`, default off.

---

## 1. Requirements, and what this angle owns

| # | Owner requirement | Model consequence | Section |
|---|---|---|---|
| R1 | Mods not in the order are absent from `Mods`, so overrides can't load | Per-order *deployment set*, including override membership per order | §6, §10.2 |
| R2 | Order A on v1.5 of a mod, order B on v1.4 | Several versions per mod; per-order pins | §3, §4, §5 |
| R3 | Never lose user data | Bytes are removed only by explicit verbs, through a trash; FileId is the authority | §8, §11 |
| R4 | Errors visible (logging is off by default) | Every library and deploy failure goes through `ShowAlert` plus a modal list | §6.4 |
| R5 | Workable at 1,097 paks / 27 GB | Hash once; Stage 4 cache hits; swap only changed rows | §12 |
| R6 | Windows now, Linux kept open | All logic in Core behind an `IFileOps` seam (LIB-A §2.1) | §2 |

---

## 2. Code facts that shape the model (new findings in **bold**)

| # | Fact | Evidence | Consequence |
|---|---|---|---|
| F1 | **`Order` is rebuilt from `ActiveMods` on every change**: `Order.Clear()` then `AddRange(ActiveMods, true)` → `mod.ToOrderEntry()` (UUID and Name only) | `MainWindowViewModel.cs:4880-4888`, scheduled 250 ms after any `ActiveMods` change `:5401-5408`; `DivinityModData.cs:299-306` | Entry-level pins are erased by the first drag. Pins must be order-level state (§5.2). S6-A plans to replace this projection, and pins must survive that too. |
| F2 | **Order selection skips `LoadModOrder` when the UUID sequence matches** | `MainWindowViewModel.cs:5196-5217` (`OrderEquals`, `DivinityLoadOrder.cs:295-302`) | Two orders with the same mods but different pins would not swap versions. The check must include pins (§5.5). |
| F3 | **`Clone()` is shallow**: `Order = this.Order.ToList()` shares entry objects | `DivinityLoadOrder.cs:304-312` | Cloning must deep-copy entries *and* copy `Pins` (LIB-A §4.1 also noted the shallow copy). |
| F4 | **Refresh restores only entries**: `lastActiveOrder = SelectedModOrder.Order.ToList()` then `SetOrder(lastActiveOrder)` | `MainWindowViewModel.cs:2347-2352`, `:2456-2461` | Unsaved pins would be lost on F5. Capture and restore them (§5.5). |
| F5 | The "Current" order is rebuilt from `SelectedModOrder.Order` after export, and from `modsettings.lsx` at startup | `MainWindowViewModel.cs:2964-2968`; `BuildModOrderList` `:1755-1800` | modsettings.lsx carries no content hash, so Current's versions must come from **what is deployed** (the manifest plus FileIds), not from library defaults (§5.6). |
| F6 | Duplicate UUIDs: the loader keeps a strictly greater Version64 (`>`); ties are decided by `ConcurrentBag` order, i.e. effectively at random; losers go to the delete dialog | `DivinityModDataLoader.cs:787-806`; `MainWindowViewModel.cs:1695-1708` → `DeleteMods(..., true, ...)` `:4359-4371`; `SetLoadedMods` repeats `>` at `:1512-1518` | In library mode duplicates become versions, and "latest" needs a deterministic tie-break (§3.4). The duplicate-delete prompt is suppressed. |
| F7 | All three pak writers truncate in place: `File.Create` | `DivinityFileUtils.cs:364` (used by `AddModFromFile` `MainWindowViewModel.cs:1845`), `:3302` (compressed), `:3429` (archive) | Overwriting a deployed pak would rewrite the library version through the hardlink. Ingest must write to staging, never to `Mods` (§7.1); upstreamable PR 0. |
| F8 | A no-meta override gets `UUID = pakPath`; `HasMetadata` is set only by `ParseMetaFile` | `DivinityModDataLoader.cs:613-621`, `:224` | ModKey for these is derived from the file name, never the path (§3.1). The in-memory UUID stays the `Mods` path for compatibility (LIB-A §4.4). |
| F9 | **"Delete Mod" recycles `FilePath`** | `DeleteFilesViewData.cs:87` → `RecycleBinHelper.DeleteFile` (`RecycleBinHelper.cs:45-59`) | In library mode that removes only a link, and the next deploy brings the mod back. Deletion must be routed through the library (§8). |
| F10 | Every `*.json` under the Orders folder is loaded as an order, recursively | `DivinityModDataLoader.cs:1147-1157` | No library JSON may live under Orders. |
| F11 | `AddImportedMod` already swaps one UUID's `DivinityModData` in place: `ActiveMods.Replace`, `order.Update`, `mods.AddOrUpdate` | `MainWindowViewModel.cs:3172-3227` | Template for the version swap (§5.4). |
| F12 | Order JSON is written by `FileMode.Create` (truncate, then write) | `DivinityModDataLoader.cs:1098-1113` | A crash mid-save loses the order, and its pins with it. Write temp then rename (part of PR 4). |
| F13 | No single-instance guard, so two app copies can run at once | CLAUDE.md §3 | Library mutations take a cross-process lock (§11, S8). |

---

## 3. Identity

### 3.1 `ModKey`: which mod

```
ModKey = mod.HasMetadata ? mod.UUID                      // whatever meta.lsx says, GUID or not
                         : "ovr:" + Hex(SHA256(lower(fileNameWithoutPartSuffix)))[0..16]
```

- With a meta.lsx, the UUID *is* the mod. It is compared ordinally, as the `SourceCache` already does (`MainWindowViewModel.cs:80`). It does not have to parse as a GUID (CLAUDE.md §6).
- Without a meta.lsx (F8), the file name is the only stable thing. The derivation is the same shape S8 already reserved (`ovr:<hash16>`, `docs/review/stages/S8.md` §3.3). S8 should adopt this exact formula so bundle keys and library keys agree. A full path never goes into a key, so neither the Windows user name nor a machine-specific path can leak into an order or bundle.
- A no-meta override that the user renames becomes a new ModKey. The Library window offers "Treat as a version of…" (writes `AliasOf` into the mod sidecar) [owner Q7].
- An author who changes the UUID between releases has produced a different mod. S9's same-UUID guard handles the download side (`docs/review/stages/S9.md` §2.5).

### 3.2 `ContentId`: which version

```
single file : ContentId = SHA-256(pak bytes)
multi-part  : ContentId = SHA-256( for each part in order Foo.pak, Foo_1.pak, …:  lower(name) "\0" size "\0" sha256hex "\n" )
```

- It is computed **during ingest**, in the same pass that copies the bytes into the library staging area (§7.1), using `IncrementalHash.CreateHash(HashAlgorithmName.SHA256)`. Archive imports and downloads therefore never read a pak twice. Only *adoption* of files already in `Mods` needs a dedicated read pass, and that happens once, at migration.
- Displayed as `sha256:` plus the first 8 hex digits (`9e8f7a6b`). Stored and compared as all 64.
- Two files with the same bytes are **one version**, even under different file names. The extra names are recorded in `KnownNames` and deployed under the primary name.
- The same UUID and Version64 with different bytes gives **two versions**. The UI labels them `1.2.0.0 · 9e8f7a6b` and `1.2.0.0 · 0bad1dea`. This is the case the owner's pinning exists for, and the one today's loader resolves at random (F6).
- MD5 is not used. The `MD5` attribute in modsettings.lsx stays empty (`DivinityModDataLoader.cs:1481-1482`).

### 3.3 Hash cache: where the hash lives before a file is in the library

`Library\hashcache.json` maps `(full path, length, LastWriteTimeUtc ticks)` to `sha256`. On Windows it also stores the 128-bit FileId. Rules:

- The key and the validation rules are Stage 4's (`docs/review/stages/alternatives/S4-B-pipeline.md` §2.4), including the racy-clean rule: an entry whose mtime is within 2 s of the time it was hashed is re-hashed. Stats come from `FileInfo`, not directory enumeration, which can be stale for hardlinked files (S4-B §2.6). The type is shared (`FileStamp`, from Stage 4's PR). The *file* stays separate because Stage 4 is upstreamable and this cache is fork-only.
- The cache is used for paks in `Mods` that are not yet library-managed (migration; foreign files dropped by other tools), and for import sources the user re-imports.
- Library files never need it. Their hash is in `version.json` and they are immutable (read-only attribute, §11 S2). "Verify library" re-hashes them on demand and compares.
- Migration is resumable because of this cache: cancel at 60 %, restart, and the first 60 % are cache hits.

### 3.4 "Latest" and the default version

Per ModKey, the **default version** is the version used by any order that does not pin one:

1. the version the user marked *default* in the Library window (`mod.json` → `Preferred`), else
2. the highest Version64, ties broken by **the newest `IngestedUtc`**, then by the `ContentId` string (so the result is total and deterministic).

Using ingest time rather than pak mtime is deliberate. Archive tools and mod.io may preserve or reset mtime [unverified], while ingest time is what "the newest thing I installed" means to the user. One consequence must be documented: importing an *older* build on purpose doesn't make it the default unless its Version64 is higher or the user marks it.

---

## 4. The library: store and on-disk layout

### 4.1 Location

As LIB-A §3.1: `%LOCALAPPDATA%\BG3ModManager\Library` by default. That is the same volume as `Mods`, outside the Larian tree (which users are told to delete when troubleshooting) and outside the Orders folder (F10). The same-volume check is by volume serial and a hardlink probe, never by comparing path strings. LIB-B's junction mode would let the library sit on another drive. The model in this document is the same in both modes.

### 4.2 Tree

```
%LOCALAPPDATA%\BG3ModManager\Library\
	library.json                          index cache (rebuildable from sidecars, see 4.4)
	hashcache.json                        3.3
	manifest.json                         what is deployed now: advisory, FileId is the authority (LIB-A §3.4)
	.lock                                 cross-process mutation lock (S8)
	inventory\
		2026-11-03T14-20-11Z.tsv          migration receipt: name, size, mtime, sha256 of every pak in Mods
		restore-mods.cmd                  app-free way back (§9.3)
	staging\                              ingest lands here, then one rename into mods\
	trash\                                deleted versions, restorable until emptied (§8)
		2026-11-04T09-00-00Z_CombatExtender_c1f3a9e2_9e8f7a6b\
	journal\                              in-flight deploy (LIB-A §5.5)
	mods\
		CombatExtender_c1f3a9e2\                   <SafeFolder ≤ 40>_<sha256(ModKey)[0..8]>
			mod.json                               ModKey, display name, Preferred, AliasOf, notes
			9e8f7a6b1c2d\                          ContentId[0..12]  (dedupe = directory exists + full hash equal)
				CombatExtender.pak                 read-only; same FileId as Mods\CombatExtender.pak while deployed
				version.json
			a1b2c3d4e5f6\
				CombatExtender.pak
				version.json
		HugeArmorPack_5d77c0aa\
			0bad1dea0001\
				HugeArmorPack.pak
				HugeArmorPack_1.pak                multi-part: one version, n files, deployed together
				version.json
		_ovr_NoCameraClamp_e019b2c4\               no-meta override (ModKey "ovr:…")
			77aa11bb2233\
				NoCameraClamp.pak
				version.json
```

The ContentId is the directory name, rather than LIB-A's `<Version>_<quick fingerprint>`. The hash is always known at ingest here (§3.2), so the name is the identity. Deduplication is a directory lookup followed by a full-hash compare against `version.json`. Twelve hex digits (48 bits) are a *name*, not proof: equality is always decided on all 64.

### 4.3 `version.json`

```json
{
	"Schema": 1,
	"ModKey": "c1f3a9e2-5f0b-4b1e-9a0e-2f1c3d4e5f60",
	"ContentId": "9e8f7a6b1c2d3e4f…(64 hex)",
	"UUID": "c1f3a9e2-5f0b-4b1e-9a0e-2f1c3d4e5f60",
	"Folder": "CombatExtender",
	"Name": "Combat Extender",
	"Author": "someone",
	"Version64": 36169534507319296,
	"Version": "1.5.0.0",
	"Files": [
		{ "Name": "CombatExtender.pak", "Size": 48211968, "Sha256": "9e8f7a6b…", "SourceMtimeUtc": "2026-10-02T09:12:44Z" }
	],
	"KnownNames": [ "CombatExtender.pak", "CombatExtender (1).pak" ],
	"IngestedUtc": "2026-11-03T14:20:11Z",
	"Source": { "Kind": "Adopted", "OriginalPath": "Mods\\CombatExtender.pak", "Archive": "", "Url": "" },
	"Label": "",
	"Scan": { "LoaderSchema": 1, "IgnoredModsHash": "5e0c…", "IsForceLoaded": false, "IsForceLoadedMergedMod": false, "HasMetadata": true }
}
```

- `Source.Kind` is one of `Adopted | Imported | Archive | Bundle | Downloaded | Relocated`. `OriginalPath` is stored *relative to the Larian folder*, so it contains no user name.
- `Scan` is a small classification cache. It is valid only while `LoaderSchema` and `IgnoredModsHash` match. Override classification depends on `IgnoredMods.json`, which changes with game patches (CLAUDE.md §6; `DivinityModDataLoader.cs:745-747`). Full metadata (meta.lsx text, dependencies) is not duplicated here. It comes from the Stage 4 cache, keyed by the library path, which never changes (§10.1).
- `Label` is free text ("pre-Patch 8", "co-op with Sam").
- Written as temp-plus-rename. The index is a cache of these files: if `library.json` is lost, it is rebuilt by walking `mods\*\*\version.json` [inference: under 1 s for about 1,200 small files].

### 4.4 Core types

`src/Core/Library/`, namespace `DivinityModManager.Library`, file-scoped, unprefixed. The types are toolkit-neutral (no `System.Windows`), so an Avalonia port keeps all of them.

```csharp
namespace DivinityModManager.Library;

public readonly record struct ContentId(string Sha256)
{
	public string Short => Sha256.Length >= 8 ? Sha256[..8] : Sha256;
	public string DirName => Sha256[..12];
	public bool IsEmpty => String.IsNullOrEmpty(Sha256);
	public override string ToString() => $"sha256:{Short}";
}

public sealed class ModVersion
{
	public string ModKey { get; init; }
	public ContentId Id { get; init; }
	public string UUID { get; init; }
	public string Name { get; init; }
	public ulong Version64 { get; init; }
	public string VersionText { get; init; }
	public IReadOnlyList<ModVersionFile> Files { get; init; }
	public DateTime IngestedUtc { get; init; }
	public string Directory { get; init; }			// absolute library dir of this version
	public string PrimaryFile => Path.Combine(Directory, Files[0].Name);
	public long TotalSize => Files.Sum(x => x.Size);
	public string Label { get; set; }
}

public sealed class LibraryMod
{
	public string ModKey { get; init; }
	public string Directory { get; init; }
	public ContentId Preferred { get; set; }
	public List<ModVersion> Versions { get; } = [];

	public ModVersion Default
	{
		get
		{
			if (!Preferred.IsEmpty)
			{
				var p = Versions.FirstOrDefault(x => x.Id == Preferred);
				if (p != null) return p;
			}
			return Versions
				.OrderByDescending(x => x.Version64)
				.ThenByDescending(x => x.IngestedUtc)
				.ThenBy(x => x.Id.Sha256, StringComparer.Ordinal)
				.FirstOrDefault();
		}
	}
}
```

`ModLibrary` (registered with Splat next to `IModRegistryService`, `MainWindowViewModel.cs:4893`; consumers null-check it, which is house practice) exposes:

```csharp
public interface IModLibrary
{
	bool IsEnabled { get; }
	IReadOnlyDictionary<string, LibraryMod> Mods { get; }			// by ModKey, ordinal
	bool TryGetVersion(ContentId id, out ModVersion version);
	Task<IngestResult> IngestFileAsync(string sourcePath, IngestSource source, CancellationToken token);
	Task<IngestResult> IngestStreamAsync(Stream source, string fileName, IngestSource source, CancellationToken token);
	Task<IngestResult> AdoptAsync(string modsPakPath, CancellationToken token);	// hardlink, no copy
	Task<DivinityModData> MaterializeAsync(ModVersion version, CancellationToken token);	// parse once, LRU-cached
	Task<TrashResult> TrashVersionAsync(ModVersion version, CancellationToken token);
	Task<GcPlan> PlanCleanupAsync(IEnumerable<DivinityLoadOrder> knownOrders, GcPolicy policy);
	Task<VerifyReport> VerifyAsync(IProgress<double> progress, CancellationToken token);
}
```

---

## 5. Data model changes: app side

### 5.1 `DivinityModData` additions (`src/Core/Models/DivinityModData.cs`)

```csharp
	/// <summary>Library identity. Empty when the library is disabled.</summary>
	[Reactive] public string ModKey { get; set; }
	/// <summary>The library version this instance was materialised from. Null when the library is disabled.</summary>
	[Reactive] public ModVersion LibraryVersion { get; set; }
	[Reactive] public int VersionCount { get; set; }
	/// <summary>The selected order pins this mod (true) or follows the default version (false).</summary>
	[Reactive] public bool IsVersionPinned { get; set; }
	/// <summary>Pinned, and the default version is a different one.</summary>
	[Reactive] public bool HasOtherDefaultVersion { get; set; }
	/// <summary>Pinned version is not in the library. The row shows a warning; deploy blocks.</summary>
	[Reactive] public bool IsPinUnresolved { get; set; }

	[ObservableAsProperty] public string VersionBadgeText { get; }	// "1.4.2.0 · 9e8f7a6b" when VersionCount > 1, else ""
```

The badge goes through `ToUIProperty` (CLAUDE.md §4). No filter predicate reads it.

`FilePath` semantics in library mode: it points at the **library file** of that version. The library file always exists and is immutable, so "Open in File Explorer", extract, zip export and Stage 8 bundles read correct bytes whether or not the version is deployed. The one exception is a no-meta override, whose `UUID` must stay `Path.Combine(AppDataModsPath, deployName)`, the same string as today (F8), so existing orders and `ForceAllowInLoadOrder` state keep matching (LIB-A §4.4). `ModFileDeletionData.FromMod` (`src/Core/Models/ModFileDeletionData.cs`) is not used in library mode (§8).

### 5.2 `VersionPin` and `DivinityLoadOrder.Pins` (`src/Core/Models/DivinityLoadOrder.cs`)

```csharp
[DataContract]
public class VersionPin
{
	/// <summary>Full SHA-256 ContentId. Authoritative when present.</summary>
	[DataMember(EmitDefaultValue = false)] public string Sha256 { get; set; }
	/// <summary>Display only, and the loose fallback when Sha256 is absent (hand-written or foreign orders).</summary>
	[DataMember(EmitDefaultValue = false)] public string Version { get; set; }
	[DataMember(EmitDefaultValue = false)] public ulong Version64 { get; set; }

	public VersionPin Clone() => new() { Sha256 = Sha256, Version = Version, Version64 = Version64 };
}

[DataContract]
public class DivinityLoadOrderEntry
{
	[DataMember] public string UUID { get; set; }
	[DataMember] public string Name { get; set; }
	/// <summary>Serialisation carrier only. The authority is DivinityLoadOrder.Pins (F1).</summary>
	[DataMember(EmitDefaultValue = false)] public VersionPin Pin { get; set; }
	public bool Missing { get; set; }

	//Unknown fields from newer fork builds survive a re-save by this build
	[JsonExtensionData] private IDictionary<string, Newtonsoft.Json.Linq.JToken> _extra;

	public DivinityLoadOrderEntry Clone()
	{
		return new DivinityLoadOrderEntry() { Name = this.Name, UUID = this.UUID, Missing = this.Missing, Pin = this.Pin?.Clone() };
	}
}
```

In `DivinityLoadOrder`:

```csharp
	/// <summary>ModKey -> pin. Survives every rebuild of Order (UpdateOrderFromActiveMods, S6-A projection).</summary>
	public Dictionary<string, VersionPin> Pins { get; private set; } = new(StringComparer.Ordinal);

	/// <summary>Pins whose ModKey has no Order entry: auto-added dependencies, overrides. Serialisation carrier only.</summary>
	[DataMember(Name = "Pins", EmitDefaultValue = false)] private Dictionary<string, VersionPin> _extraPins;

	[OnDeserialized]
	private void OnDeserializedPins(StreamingContext context)
	{
		Pins = new(StringComparer.Ordinal);
		if (_extraPins != null)
		{
			foreach (var kv in _extraPins) Pins[kv.Key] = kv.Value;
		}
		foreach (var e in Order)
		{
			if (e?.Pin != null) Pins[e.UUID] = e.Pin;
		}
		_extraPins = null;
	}

	[OnSerializing]
	private void OnSerializingPins(StreamingContext context)
	{
		var onEntries = new HashSet<string>(StringComparer.Ordinal);
		foreach (var e in Order)
		{
			e.Pin = Pins.TryGetValue(e.UUID, out var p) ? p : null;
			if (e.Pin != null) onEntries.Add(e.UUID);
		}
		var rest = Pins.Where(kv => !onEntries.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
		_extraPins = rest.Count > 0 ? rest : null;
	}

	[OnSerialized]
	private void OnSerializedPins(StreamingContext context)
	{
		_extraPins = null;
	}

	public bool PinsEqual(DivinityLoadOrder other) => Pins.Count == other.Pins.Count
		&& Pins.All(kv => other.Pins.TryGetValue(kv.Key, out var o) && o.Sha256 == kv.Value.Sha256);
```

Newtonsoft honours `System.Runtime.Serialization` `[OnSerializing]`/`[OnDeserialized]` callbacks and private `[DataMember]` fields on `[DataContract]` types. **[verify]** that this holds with the Core `JsonExtensionData` alias (CLAUDE.md §5, `src/Core/Usings.cs`). A unit test settles it (T-U3).

Rules:
- **Keyed by ModKey.** For a mod with meta.lsx, ModKey equals UUID, which equals the entry's `UUID`. A no-meta override's key (`ovr:…`) never equals its path-UUID, so its pin always goes in top-level `"Pins"` and never into an entry.
- **Pins for mods that leave the order are kept in memory for the session** and dropped at save. Dragging a mod out and back in therefore doesn't lose its pin, and the file doesn't accumulate dead pins. Exception: pins for overrides and auto-dependencies are saved if the mod is in this order's deployment set (§6.1).
- `Clone()` deep-copies entries and `Pins` (fixes F3). `SetOrder(DivinityLoadOrder)` copies `Pins`. `SetOrder(IEnumerable<entry>)` leaves `Pins` alone.

### 5.3 Order JSON, with an example and the compatibility rules

```json
{
	"Order": [
		{ "UUID": "c1f3a9e2-5f0b-4b1e-9a0e-2f1c3d4e5f60", "Name": "Combat Extender",
		  "Pin": { "Sha256": "a1b2c3d4e5f6…(64)", "Version": "1.4.2.0", "Version64": 36169534507319296 } },
		{ "UUID": "77e0c0de-0000-4000-8000-000000000001", "Name": "Better Hair" }
	],
	"Pins": {
		"ovr:3f9a1c0b7d2e4a55": { "Sha256": "77aa11bb2233…", "Version": "0.0.0.0" },
		"d2b5c0a1-…-ImpUI":     { "Sha256": "5150…", "Version": "3.1.0.0" }
	},
	"Layout": { "Version": 1, "Rows": [ { "Kind": "Override", "Pak": "NoCameraClamp.pak", "Before": null, "At": 2 } ] }
}
```

(`Layout` is S6-A's member; it decides *which* overrides belong to the order, §10.2. `Pins` decides *which version*.)

| Reader | Behaviour |
|---|---|
| Upstream master, or a fork build before this feature | Ignores `Pin` and `Pins` (Newtonsoft default `MissingMemberHandling.Ignore`; `SafeDeserialize` swallows errors, `DivinityJsonUtils.cs:30-38`). The order loads as "latest". **Re-saving drops pins.** Mitigation: add `[JsonExtensionData]` to `DivinityLoadOrderEntry` in the *first* fork PR that touches the order format (S6-A's), so every later fork build round-trips them. Upstream can't be helped. |
| Upstream Avalonia | Migrates legacy `Order` to `Entries`. `Pins` lands in `Extras` [inference from S6-A §3]. The fork must never write `Entries`. |
| This build, library **disabled** | Reads and keeps pins (pass-through), but applies none: there is one version per UUID. Turning the library off and on loses nothing. |
| This build, library enabled, old file | No pins means every mod follows the default version. Exactly today's meaning. |
| Pin resolution | 1) `Sha256` matches a library version → resolved. 2) no `Sha256`, exactly one version with that `Version64` → resolved *loose* (shown with a dotted pin glyph; "Make exact" rewrites `Sha256`). 3) otherwise **unresolved**: the row shows a warning, and deploy blocks with "Use default for these N" / "Import file…" / "Cancel". **There is never a silent fallback to latest**, because that is precisely what a pin exists to prevent. |
| Stage 6.3 dividers | Not mods, no ModKey, never pinned. |

### 5.4 The version swap (effective version per order)

The selected order decides each row's effective version. When an order loads, the library computes the swaps for the UUIDs whose effective `ContentId` differs from the one currently in `mods`. At the owner's scale that is usually zero to a few dozen.

```csharp
// src/Core/Library/EffectiveVersionResolver.cs
public sealed record VersionSwap(DivinityModData Current, ModVersion Next);

public static class EffectiveVersionResolver
{
	public static List<VersionSwap> Plan(DivinityLoadOrder order, IEnumerable<DivinityModData> current, IModLibrary library, out List<string> unresolved)
	{
		var swaps = new List<VersionSwap>();
		unresolved = [];
		foreach (var mod in current)
		{
			if (String.IsNullOrEmpty(mod.ModKey) || !library.Mods.TryGetValue(mod.ModKey, out var lm)) continue;
			var want = lm.Default;
			if (order.Pins.TryGetValue(mod.ModKey, out var pin))
			{
				var resolved = PinResolver.Resolve(pin, lm);
				if (resolved == null)
				{
					unresolved.Add(mod.ModKey);
					continue;		// keep what is loaded; IsPinUnresolved is set by the caller
				}
				want = resolved;
			}
			if (want != null && mod.LibraryVersion?.Id != want.Id)
			{
				swaps.Add(new VersionSwap(mod, want));
			}
		}
		return swaps;
	}
}
```

Hook in `LoadModOrder` (`MainWindowViewModel.cs:2093`): insert the swap *before* the loop at `:2111`. Then `ActiveMods`/`InactiveMods` are rebuilt from `addonMods` as today (`:2141-2144`), and pick up the new instances.

```csharp
	public bool LoadModOrder(DivinityLoadOrder order)
	{
		if (order == null) return false;

		IsLoadingOrder = true;

		var library = Services.Get<IModLibrary>();
		if (library?.IsEnabled == true)
		{
			ApplyEffectiveVersions(order, library);
		}

		var loadFrom = order.Order;
		// ... unchanged ...
	}

	private void ApplyEffectiveVersions(DivinityLoadOrder order, IModLibrary library)
	{
		var swaps = EffectiveVersionResolver.Plan(order, mods.Items.Where(x => x.IsUserMod), library, out var unresolved);
		if (swaps.Count > 0)
		{
			//Materialisation is a cache hit for anything seen this session; cold ones parse in a few ms each.
			var next = swaps.Select(s => (s.Current, Data: library.MaterializeAsync(s.Next, CancellationToken.None).GetAwaiter().GetResult())).ToList();
			mods.Edit(u =>
			{
				foreach (var (cur, data) in next)
				{
					if (data == null) continue;
					data.CopyRowStateFrom(cur);
					u.AddOrUpdate(data);
				}
			});
			RefreshMissingDependencies(next.Select(x => x.Data));
		}
		var unresolvedSet = unresolved.ToHashSet(StringComparer.Ordinal);
		foreach (var m in mods.Items.Where(x => !String.IsNullOrEmpty(x.ModKey)))
		{
			var pinned = order.Pins.ContainsKey(m.ModKey);
			m.IsVersionPinned = pinned;
			m.IsPinUnresolved = unresolvedSet.Contains(m.ModKey);
			m.HasOtherDefaultVersion = pinned && library.Mods.TryGetValue(m.ModKey, out var lm) && lm.Default?.Id != m.LibraryVersion?.Id;
		}
		if (unresolved.Count > 0)
		{
			ShowAlert($"{unresolved.Count} pinned mod version(s) are not in the library - see the highlighted rows", AlertType.Warning, 0);
		}
	}
```

The blocking `GetResult()` on the UI thread is acceptable *only* because materialisation is normally a cache hit. In the real code, `LoadModOrder` should preload asynchronously: the order-selection handler (`:5196`) awaits `library.PreloadAsync(order)` on the taskpool, then calls `LoadModOrder` on the main thread. That keeps the UI thread free of pak I/O (CLAUDE.md §4 threading).

`CopyRowStateFrom` (new, `DivinityModData`) carries the per-UUID state that is not version metadata: `NexusModsEnabled`, `NexusModsData`, `WorkshopData`, `HasColorblindSupport`, `DeveloperMode`, `HasColorOverride`, `SelectedColor`, `ListColor`, `ForceAllowInLoadOrder`, `Visibility`, `CurrentExtenderVersion`. It does **not** carry `Version`, `Dependencies`, `FilePath`, `IsForceLoaded*`, `ScriptExtenderData` or `Files`. Those belong to the version, and they are why the swap exists. `IsActive`, `Index` and `IsSelected` are reset by `LoadModOrder` anyway (`:2101-2131`). `RefreshMissingDependencies` is the per-mod part of `SetLoadedMods` (`:1524-1530`), extracted.

### 5.5 Keeping pins alive through the existing lifecycle

| Site | Change |
|---|---|
| `UpdateOrderFromActiveMods` (`:4880-4888`), and S6-A's replacement | No change needed, because `Pins` is not touched. Add a unit test that a rebuild preserves `Pins` (T-U2). |
| Order selection (`:5202`) | `if (!SelectedModOrder.OrderEquals(...) \|\| !SelectedModOrder.PinsEqual(_loadedOrderPins))` → `LoadModOrder`. `_loadedOrderPins` is a snapshot taken in `LoadModOrder` (fixes F2). |
| `RefreshAsync` (`:2347-2352`, `:2456-2461`) | Capture `lastPins = new Dictionary<…>(SelectedModOrder.Pins)` alongside `lastActiveOrder`. Restore both onto the re-selected order (fixes F4). |
| `Clone()` (`:304-312`), `SetOrder(DivinityLoadOrder)` (`DivinityLoadOrder.cs:289-293`) | Deep copy, as in §5.2 (fixes F3). |
| `AddNewModOrder` (`:2055-2061`) | A new order starts with `Pins` copied from the order it was created from: "New order from current" keeps versions. |
| Export, Current order update (`:2964-2968`) | `SetOrder(SelectedModOrder.Order)` **and** `SetPins(SelectedModOrder.Pins)`, then mark Current as *observed* (§5.6). |
| Order save (`DivinityModDataLoader.cs:1098-1113`) | Write `*.json.tmp`, `Flush(true)`, then `File.Move(tmp, path, overwrite: true)` (fixes F12; pins are now worth protecting). |
| `AddImportedMod` (`:3160-3227`) | Library mode: the imported pak is a new *version*. The row swaps to it only if the selected order follows the default *and* the new version became the default (§3.4). Otherwise the row stays and gets the "other default" badge. |

### 5.6 The Current order and what is really deployed

"Current" is read from the profile's `modsettings.lsx`, which records UUID and Version64 but no hash (`DivinityApp.cs:33`). Its pins therefore come from the disk. On load, the library reads `manifest.json` and confirms each managed `Mods` name by FileId (LIB-A §5.2). Every deployed version becomes an *observed pin* on the Current order. The UI thus shows what the game will actually load, not the library default. If the manifest is missing or a FileId doesn't match (a foreign file), the foreign file is ingested (§7.3) and *its* ContentId becomes the observed pin. Saving Current under a new name ("Save As") turns observed pins into ordinary pins, which is the natural "freeze what I'm playing" action.

---

## 6. Deployment from the model's side

The engine (classification by FileId, the pure planner, preview, journal, unlink-before-link, rollback, game-process guard) is LIB-A §5, adopted unchanged. LIB-B adds the junction mode as another target. Here are the inputs the model supplies and the steps it owns.

### 6.1 Desired set D for an order O and profile P

1. `BuildOutputList(O.Order, mods.Items, Settings.AutoAddDependenciesWhenExporting, adventure)` (`DivinityModDataLoader.cs:1431`). Because of §5.4, `mods.Items` already holds O's effective versions, so the dependencies are those of the pinned versions. Auto-added dependencies that are not in `O.Order` resolve through `O.Pins` too, else the default.
2. Drop base-game and editor mods (`IgnoredMods`, `IsEditorMod`). They don't live in `Mods`.
3. Add the overrides that belong to O: S6-A `Layout` rows of `Kind: "Override"` (§10.2). If the order has no `Layout`, it is a *legacy order* and the preview asks once (LIB-A §5.3). The answer is written as Layout rows.
4. Resolve every member to a `ModVersion`. D is `deployName → (ModVersion, file)` for every file, including multi-part parts. `deployName` is `version.Files[i].Name`. When two different ModKeys want the same name, the preview offers to deploy the second as `<base>__<cid8>.pak` (parts renamed consistently). This is a **blocking** choice until made. Whether the game cares about pak file names at all is [unverified, G10].

### 6.2 Steps (one Export to Game)

```
 0  Guard: game not running (GameProcessGuard, LIB-A §5.6); library lock acquired (S8); Mods path == manifest.ModsPath
 1  Preload: materialise any un-materialised effective version (async, taskpool)
 2  Unresolved pins?  -> blocking preview issue
 3  Snapshot Mods (one enumeration + FileId per *.pak); classify Managed / Foreign / Drifted (LIB-A §5.2)
 4  Ingest every Foreign or Drifted pak FIRST (adopt by hardlink, §7.3). Nothing is ever unlinked that has no verified library twin
 5  Plan: unlink (managed, not in D), swap (name in D but wrong FileId), link (in D, absent); unlink before link
 6  Preview (LIB-A §5.4), with version-specific lines:  "SWITCH Combat Extender 1.5.0.0 -> 1.4.2.0 (pinned in 'Tactician')"
                                                     "REMOVE override NoCameraClamp.pak (not in this order)"
 7  Journal, apply, verify each op by FileId (LIB-A §5.5)
 8  Write modsettings.lsx exactly as today (ExportModSettingsToFileAsync, MainWindowViewModel.cs:2932). Version64 comes from the
    effective DivinityModData, so it matches the deployed bytes
 9  Write manifest.json: { ModsPath, Profile, Order name, Order file, per name: ModKey + ContentId + FileId }
10  Current order := O with observed pins (§5.6); ShowAlert(success with counts)
```

### 6.3 Profiles sharing one `Mods` folder

`Mods` is per machine, `modsettings.lsx` is per profile, and the owner has two profiles. Deployment is therefore **global**. Exporting order B for profile 2 removes order A's mods from `Mods`, even though profile Public's `modsettings.lsx` still lists them.

- The manager already sets the exported profile as the game's selected profile (`ExportedSelectedProfile`, `MainWindowViewModel.cs:2955`), so the next launch uses the profile that matches `Mods`.
- The preview warns when another profile's `modsettings.lsx` references a mod that is about to be undeployed ("Profile 'Public' lists 23 mods that will not be in Mods").
- Whether the game rewrites another profile's `modsettings.lsx` when the player switches to it in-game and mods are missing is [unverified, G8]. If it does, that profile's list would silently lose entries. The order JSON in the manager is unaffected, so the loss is recoverable by re-exporting.
- Setting `DeployScope`: `ActiveOrder` (default; the owner's R1) or `UnionOfProfiles`, which deploys the union of every profile's exported order and trades override isolation for safe profile switching inside the game [owner Q3].

### 6.4 Failure handling (model-specific additions to LIB-A §5.6)

| Failure | Response |
|---|---|
| Pin unresolved | Blocks before any file op. Modal lists the mods with "Use default", "Import file…" (ingests, then re-resolves) and "Cancel". |
| Materialisation fails (corrupt library pak) | Blocks. `ShowAlert(Danger)` names the version; "Verify library" is offered; the version is marked `Suspect` in the index. |
| Ingest of a foreign file fails (locked, unreadable) | That file is **left in place and not unlinked**. Deploy continues for the rest only if the user accepts "Leave unknown file X in Mods" (it may be an override, which defeats R1, and the preview says so). |
| Manifest says deployed, FileId says otherwise | FileId wins. The file is treated as Foreign and ingested. The manifest is advisory (LIB-A §3.4). |
| Library lock held by another instance | Refuse with "Another BG3 Mod Manager window is changing the library". |

---

## 7. Ingest: the single entry point for new bytes

### 7.1 Write paths that change

| Today | Library mode |
|---|---|
| `AddModFromFile` copies the `.pak` into `Mods` (`MainWindowViewModel.cs:1841-1845`, `CopyFileAsync` → `File.Create`, `DivinityFileUtils.cs:359-372`) | `library.IngestFileAsync(filePath, IngestSource.File(filePath))` |
| `ImportArchiveAsync` writes each pak entry to `Mods\<name>` with `File.Create` (`:3420-3433`) | `library.IngestStreamAsync(entryStream, name, IngestSource.Archive(archiveName))` |
| `ImportCompressedFileAsync` (`:3302`) | same, from the decompressed stream |
| Stage 8 bundle import | `IngestStreamAsync`. Skips extraction entirely if the manifest's `sha256` is already in the library. The manifest's hash is trusted *only* for skipping, never for deleting anything. |
| Stage 9 downloader | Downloads to `Data/Downloads/…/*.part` (S9 §2.5), then `IngestFileAsync`. **Never** writes into `Mods`. |
| Something else drops a pak into `Mods` (the user, Vortex, the in-game mod.io manager [unverified G7]) | Detected as Foreign at the next refresh or deploy → `AdoptAsync` |

After any ingest, the caller does today's UI work: `AddImportedMod`, select the new mod, and so on.

### 7.2 `IngestStreamAsync`, step by step

```
1  stage = Library\staging\<guid>\ ; write <fileName>.tmp with FileOptions.WriteThrough, hashing while copying (IncrementalHash)
2  Flush(true); rename .tmp -> fileName                        (multi-part: repeat per part, then hash the part list, §3.2)
3  parse: LoadModDataFromPakAsync(stagedPath, builtinMods)     (DivinityModDataLoader.cs:731) -> UUID, HasMetadata, flags
   parse failure -> keep in staging\failed\ (never delete user bytes), ShowAlert(Danger) naming the file
4  key = ModKey(mod, fileName); cid = ContentId(...)
5  if library has cid (dir exists AND version.json.ContentId == cid, all 64 hex):
       delete the staging copy (it is our own copy of bytes we already hold); add fileName to KnownNames; result = AlreadyPresent
   else:
       Directory.Move(stage, Library\mods\<keyDir>\<cid12>)      (same volume: one rename)
       write version.json (tmp + rename); set FILE_ATTRIBUTE_READONLY on every pak
       update library.json; result = NewVersion (IsNewDefault = Default(key).Id == cid)
6  return IngestResult { Version, Result, PreviousDefault }
```

The library never opens an existing library pak for writing. That is safety invariant S2 (§11).

### 7.3 `AdoptAsync`: a file already in `Mods`

```
1  stamp = FileInfo(modsPath); sha = hashcache[stamp] ?? hash the file (read-only open, FileShare.Read|Delete)
2  if library has sha: the Mods file is a byte-identical *copy* (different FileId).
       Record it in the plan as "replace with link"; the deploy unlinks it only after confirming identity by full hash again.
   else:
       CreateHardLink(staging\<guid>\<name>, modsPath)     (no bytes copied; Mods untouched)
       verify GetIdentity(staged) == GetIdentity(modsPath) and link count >= 2
       parse, ModKey, then rename staging dir into mods\<keyDir>\<cid12>; version.json; read-only attribute
3  Source.Kind = Adopted, OriginalPath = relative path
```

### 7.4 Duplicate UUIDs become versions

When the library is enabled, `LoadModsAsync` no longer opens the delete-duplicates dialog (`MainWindowViewModel.cs:1695-1708`). At migration, every duplicate pak is adopted as its own version of the same ModKey, or merged if the bytes are identical. The alert reads: "3 mods have more than one version. Choose per order from the Version menu". `DivinityModDataLoader.LoadModPackageDataAsync` keeps its winner rule (`:787-806`) for library-off mode. The library path doesn't call it (§10.1).

---

## 8. Deletion semantics

| User verb (UI text) | Effect on the model | Effect on disk | Reversible? |
|---|---|---|---|
| **Remove from order** (drag to inactive, today's behaviour) | Entry leaves `Order`; the pin stays in memory until save (§5.2) | None until the next deploy, which unlinks that name from `Mods` | Yes: drag back |
| **Undeploy now** (Library window, or the preview's "Remove" line) | None | Unlink the `Mods` name. Library name remains | Yes: redeploy, about 1 ms |
| **Delete this version** (Version menu, Library window) | The version disappears from `LibraryMod.Versions`. Orders pinning it become *unresolved*; the dialog lists them first ("Pinned by: Tactician, Co-op") and offers "Repin them to v1.5" | 1) Unlink from `Mods` if deployed (game must be closed). 2) `Directory.Move` the version dir to `Library\trash\<ts>_<keyDir>_<cid8>\`, a same-volume rename | Yes, until the trash is emptied: "Restore" in the Library window |
| **Delete mod** (today's "Delete Mod…", `MainResourceDictionary.xaml:319`) | All versions, as above. The mod leaves `mods` (`RemoveDeletedMods`, `MainWindowViewModel.cs:4377-4393`) | Every version → library trash | Yes, until emptied |
| **Empty library trash** | None | `RecycleBinHelper.DeleteFile` per file (Recycle Bin, `RecycleBinHelper.cs:45-59`). If a file is larger than the bin can hold, Windows decides what happens [unverified]: the dialog warns first and needs a second confirmation for "permanently delete" | Recycle Bin, if it fits |
| **Clean up unused versions** (GC) | Shows candidates; nothing automatic | Selected candidates → library trash | Yes |

GC candidate rule. *All* conditions must hold:

```
!IsDefault(v)
&& !DeployedNow(v)                                      // FileId check against Mods
&& !PinnedByAnyKnownOrder(v)                            // every *.json under Orders (incl. backups),
                                                        // the in-memory unsaved orders, and Current's observed pins
&& String.IsNullOrEmpty(v.Label)                        // labelled = deliberately kept
&& v.IngestedUtc < now - policy.MinAge (default 14 days)
```

The dialog says plainly that pins inside exported zips, Stage 8 bundles and friends' copies can't be seen. A version you trash may be one a friend's order expects.

In library mode, `DeleteMods` (`MainWindowViewModel.cs:4359-4371`) routes to the library dialog instead of `DeleteFilesViewData`, whose recycle of `FilePath` (`DeleteFilesViewData.cs:87`) would remove only a link (F9). With the library off, nothing changes.

The trash is our own folder, not the Recycle Bin, for three reasons: a same-volume rename is instant even for a 4 GB pak; it never hits Recycle Bin size limits; and "Restore" puts a version back with its `version.json` intact.

---

## 9. Migration from today's plain `Mods` folder, and the way back

### 9.1 Enable (wizard, `Tools → Mod Library…`)

| Step | What happens | Mods changed? | If interrupted |
|---|---|---|---|
| 1 Preconditions | Game not running; same-volume + hardlink probe (LIB-A §3.1); free-space check (adoption needs metadata only; trash and new versions need room later); no other instance (lock) | No | Nothing to undo |
| 2 Receipts | Copy every profile's `modsettings.lsx` to `Library\inventory\<ts>\PlayerProfiles\…`. Write `inventory\<ts>.tsv` (name, size, mtime; hashes are filled in step 3) | No | Re-run |
| 3 Hash | Full SHA-256 of every pak and part. Cancellable, resumable through the hash cache, progress bar. 27 GB: about 15 s on NVMe up to about 3 min on an HDD (§12) | No (read-only opens) | Resume where it stopped |
| 4 Adopt | `AdoptAsync` for each pak group (§7.3). Duplicates become versions | **No.** Only extra names are created, in the library | Staging holds only extra names. Cleaned on the next start |
| 5 Verify | Re-enumerate `Mods`: same names, sizes, FileIds as step 2; every pak has a library twin with the same FileId; every library pak's hash matches `version.json` (cheap: from step 3). Show "1,097 files in Mods unchanged · 1,097 adopted · 3 mods with several versions" | No | Re-run |
| 6 Manifest | `Deployed = "(unmanaged: everything)"`. Generate `restore-mods.cmd` (§9.3) | No | — |
| 7 First deploy | Optional; the user picks an order. The full preview (§6.2) shows what leaves `Mods`, overrides in red | Yes, after confirmation | Journal recovery (LIB-A §5.6) |

Why *adopt by hardlink* rather than *move into the library, then link back*. Both are same-volume, metadata-only operations. A move, though, leaves a window, between the move and the link back, where `Mods` lacks the file. If the app dies in that window, the game sees missing mods, and the user sees an empty folder and assumes the worst. Adoption has no such window, and step 5's "Mods unchanged" check can be shown truthfully. *Move* is used only by the fallback modes with no hardlink support (FAT32/exFAT), as in LIB-A §7.3 step 2. There the wizard says plainly that deleting from `Mods` in Explorer then deletes the only copy.

### 9.2 The way back (in the app)

| Action | Result |
|---|---|
| **Restore full Mods folder** | Deploys the default version of every ModKey plus every override, which is today's state minus extra versions. With "Include all versions under their original names", every version is deployed under its `KnownNames[0]`, so `Mods` returns name-for-name to the inventory, duplicates included |
| **Disable library** | Deletes `manifest.json` and stops deploying. Whatever is in `Mods` stays, as ordinary files (a hardlink is a full peer name). Orders keep their pins (§5.3: library-off reads them through) |
| **Remove library from disk** | Per library file: link count ≥ 2 means the bytes are also in `Mods`, so deleting the library name loses nothing. Link count 1 means *move* into `Mods\..\Mods_Library_Export\` or send to the trash, never a permanent delete |

### 9.3 The way back without the app

`Library\inventory\restore-mods.cmd` is regenerated after every library change. It contains one `mklink /H "<Mods>\<name>" "<library file>"` per default version plus overrides, guarded by `if not exist`. `mklink /H` needs no elevation on NTFS [src-web: CreateHardLink needs no special privilege; only same volume and NTFS, §19]. If the manager is broken or uninstalled, double-clicking the script repopulates `Mods`. It only adds names and never deletes. A `restore-mods.sh` sibling (using `ln`) is written for the Linux/Proton path when that mode exists.

---

## 10. Interplay with other stages

### 10.1 Stage 4 (startup cache)

- **Library load replaces the `Mods` scan in library mode.** `LoadModsAsync` (`MainWindowViewModel.cs:1659-1666`) calls `library.LoadAsync()` instead of `LoadModPackageDataAsync(AppDataModsPath)`. That method reads `library.json`, takes the **default and Current-observed** version per ModKey, and parses each through the Stage 4 per-pak path (`LoadModDataFromPakToBagAsync`, `DivinityModDataLoader.cs:696`). Older versions are parsed lazily by `MaterializeAsync` (LRU, 64 entries). Library paks are immutable, so Stage 4 cache entries keyed by library path **always hit** after the first launch.
- **Requirement on Stage 4.** S4-A keys entries by path *relative to the Mods root*, with a `RootHash` header (`docs/review/stages/alternatives/S4-A-minimal.md` §2.3). That can't hold library paths. Ask Stage 4 to key by full path (S4-B already does, §2.4), or to keep one cache section per root. The change is small, and costs nothing upstream if it is made before Stage 4 merges.
- Stage 4's R1 snapshot harness (`docs/review/stages/alternatives/S4-A-minimal.md` §3.1) gains two columns, `ModKey` and `ContentId`, so it can show that library mode chooses the same winner as the plain loader for every UUID without duplicates.
- The hash cache reuses Stage 4's `FileStamp` and racy-clean rule (§3.3).

### 10.2 Stage 6.2 (overrides folded into the order): does deployment supersede it?

**No. They complement each other, and 6.2 should land first.**

- 6.2 (S6-A §5) gives every order a persisted list of override rows (`Layout` rows, `Kind: "Override"`, matched by `Pak` name or GUID). Without the library, those rows are *locked*: shown and orderable, but the game loads the pak regardless [unverified G4; consistent with Nexus Mods App's BG3 notes, §19]. The rows are never written to `modsettings.lsx`, for no-meta overrides as today.
- With the library on, the same rows become **the order's override deployment membership** (§6.1 step 3). An override row in the order means deployed; no row means undeployed. That gives R1 its teeth, and it removes S6-A's "can't be deactivated" restriction: in library mode an override row *can* be dragged to the inactive list, and the next deploy unlinks it. S6-A's lock glyph becomes "deployed with this order".
- `modsettings.lsx` content is unchanged by either. S6-A's byte-identity invariants INV-1/INV-2 still hold, because deployment changes `Mods`, not `modsettings.lsx`.
- Terminology clash: S6-A calls the locked override row "pinned". In this feature "pin" means *version pin*. Rename S6-A's term to **locked** before either ships.
- If LIB ever has to land before 6.2: write the same `Layout` override rows (S6-A §4.1 shape, with `[JsonExtensionData]`), so 6.2 inherits them without a migration.
- The 6.2 designs disagree on persistence. S6-A persists override rows in `Layout`. `docs/review/stages/alternatives/S6-C-game-risk.md` (§0 and ~L410) keeps the overrides block pinned at the end of the list and **persists nothing** for 6.2. If the owner picks S6-C's variant, LIB's PR 6 must add the per-order override membership itself, as S6-A-shaped `Layout` rows of `Kind: "Override"`. Without library mode, nothing in S6-C changes. So: choose S6-A's persistence, or accept that extra work in LIB [owner Q10].

### 10.3 Stage 6.3 (categories)

Dividers are layout rows, not mods. They have no ModKey, no pin and no deployment. The version badge is a cell in the mod row template, so it doesn't change the flat-collection design.

### 10.4 Stage 8 (multi-order export and import)

- In library mode, "two orders reference different versions of the same mod" stops being impossible (`docs/review/stages/S8.md` §4.2) and becomes normal. The bundle should key mods by **`<ModKey>@<ContentId short 12>`**, not `uuid@version64`. Version64 is not unique, which is the starting premise of this feature. Pak paths become `paks/<keyDir>/<cid12>/<file>`. `pins` carry the full `sha256`. Ask S8 to make this its v1 key: it is one string change before anything ships, and a schema break after.
- The export set is the union of each selected order's *resolved* versions. Bytes are read from library paths (`FilePath`, §5.1), so a version needn't be deployed to be exported.
- Import into the library: each pak → `IngestStreamAsync`, skipping any whose `sha256` is already present. Each order → pins straight from the manifest. Current named `<Profile>_Current` (S8 §3.4) imports as an ordinary order with pins. Without the library (or on upstream), the bundle still imports as plain paks (S8 §7.1).

### 10.5 Stage 9 (downloader and friend sync)

- Downloads become library versions (§7.1). "Update available" is a property of a ModKey. Updating never touches a pinned order: it adds a version, and unpinned orders follow it. Setting `FreezeOrdersOnNewVersion` (default off) pins every saved order that contains the mod to the *previous* default at ingest time, the "don't break my running campaign" switch [owner Q4].
- Friend sync pins by hash (S8 §8.3): "Co-op ready" means `library.TryGetVersion(pin.Sha256)` for every pin. Version64 is shown for humans ("you have 1.4.2.0 · a1b2c3d4, they have 1.4.2.0 · 9e8f7a6b": same number, different files). Whether BG3 multiplayer itself refuses mismatched mod versions is [unverified, G11].

### 10.6 Game patches

- `IgnoredMods.json` changes after a patch (CLAUDE.md §6). `Scan.IgnoredModsHash` (§4.3) invalidates override classification for the affected versions only.
- A patch that breaks mods is exactly what pins are for. A one-click **"Freeze all orders"** (pin every unpinned entry in every saved order to its current version) before updating mods, plus a version `Label` ("pre-Patch 9"), keeps the last working set restorable.
- If a patch moves the `Mods` folder (one forum report says Patch 7 moved it from Documents to AppData [src-web, §19, single-user report]), `manifest.ModsPath` no longer matches `PathwayData.AppDataModsPath` (`MainWindowViewModel.cs:1352-1357`). Deploy refuses and offers to deploy into the new path. The library itself doesn't move.

### 10.7 Upstream Avalonia

`wt-avalonia` has no library, deployment or link concept. The only link code is `JunctionPoint` used for registry path resolution (`wt-avalonia/src/Services/Services/RegistryService.cs:32-34`). There is nothing to borrow and nothing to stay compatible with beyond the JSON rule "never write `Entries`".

---

## 11. Safety guarantees and how each is enforced

| # | Guarantee | Enforcement |
|---|---|---|
| S1 | A deploy never destroys bytes | Unlink only a `Mods` name whose FileId equals a library file's FileId **and** whose library link count is ≥ 2 (LIB-A §5.5). A byte-identical copy is replaced only after a fresh full-hash compare |
| S2 | Library versions are immutable | Read-only attribute on every pak. The library opens nothing for write after ingest. PR 0 moves every manager pak writer to temp-plus-rename. "Verify library" re-hashes |
| S3 | Unknown files are never removed before they are owned | Ingest Foreign/Drifted files *first* (§6.2 step 4). On failure they stay put (§6.4) |
| S4 | Every user-initiated deletion is reversible at least once | Library trash (same-volume rename) → Recycle Bin. Permanent delete needs a second confirmation |
| S5 | No file changes under a running game | `GameProcessGuard` before plan and again before apply (LIB-A §5.6) |
| S6 | Crash-safe | Write-ahead journal plus FileId-based recovery (LIB-A §5.6). All JSON is written temp-plus-rename |
| S7 | Pins never silently change meaning | Strict resolution; unresolved blocks deploy (§5.3). A version is never trashed without listing the orders that pin it |
| S8 | One writer | `Library\.lock` opened `FileShare.None` for the duration of any mutation. Reads are lock-free |
| S9 | Errors are visible | Every library operation returns a result object. Failures go to `ShowAlert` plus a modal listing names (CLAUDE.md §5). Nothing in `src/Core/Library` swallows exceptions |
| S10 | There is always a way back | Inventory receipt, `restore-mods.cmd`, "Restore full Mods folder", "Export without deploying" |

---

## 12. Performance at 1,097 paks / 27 GB / 935 in the order

All numbers are **[inference]** until measured. Each line names its measurement (T-P).

| Operation | When | Estimate | Basis |
|---|---|---|---|
| Hash 27 GB (migration) | once | NVMe ~15-25 s, SATA SSD ~55-70 s, HDD ~3-4 min | Disk-bound. SHA-256 with SHA-NI runs at roughly 1.5-2 GB/s per core, so 2-4 worker threads keep the disk busy. Whether Defender scans on these reads is unknown (T-P1) |
| Hash on import | per import | ~0 extra | Hashed while copying (§7.2) |
| Adopt 1,097 paks | once | ~2-10 s | One `CreateHardLink` plus sidecar write per pak, both metadata-only (T-P2) |
| Library load | each start | index ~1 MB JSON in ~50 ms; then the Stage 4 hit path for ~1,100 default versions | Library paths never change, so Stage 4 hit rate is about 100 % |
| Order switch, no pin differences | each switch | today's cost plus `PinsEqual` | Dictionary compare |
| Order switch, k swapped rows | each switch | k × (AddOrUpdate + `CopyRowStateFrom`), plus a cold materialise of 5-30 ms per pak on first use | Swaps are typically 0-30; batched in one `mods.Edit` |
| Deploy, typical switch | each export | ~1 s for ~200 link operations | LIB-A §10 |
| Memory | resident | +~1-2 MB index; DivinityModData only for effective versions plus an LRU of 64 | Today's 478 MB baseline (plan §4, Stage 0) |
| Extra disk | ongoing | 0 B for adopted versions; each *extra* version costs its own size | Hardlinks share data |

---

## 13. Branch and PR breakdown

**Fork-only** (`feat/*` on `fork/main`, `--no-ff`). It changes what the `Mods` folder means, depends on 6.2 and the Stage 8 format, and upstream is moving to Avalonia, which has no equivalent. The only upstreamable piece is PR 0.

| PR | Branch | Content | Depends on | Effort |
|---|---|---|---|---|
| 0 | `fix/pak-write-temp-rename` (from `master`, **upstreamable**) | `CopyFileAsync` and the two archive writers write `*.tmp` then `File.Move(overwrite)`. Partial files on cancel and in-place truncation both go away | — | 0.5 d |
| 1 | `feat/order-json-passthrough` | `[JsonExtensionData]` on `DivinityLoadOrderEntry`; temp-plus-rename order save (F12); deep `Clone` (F3). Ideally folded into S6-A's first PR | — | 0.5 d |
| 2 | `feat/library-core` | `ContentId`, `ModKey`, `ModVersion`, `LibraryMod`, `ModLibrary` (layout, sidecars, index rebuild, lock, trash), `HashCache`, `IngestStreamAsync/FileAsync`, `AdoptAsync`, `IFileOps` (LIB-A §2.1). Core unit tests | 0 | 4 d |
| 3 | `feat/library-migration` | Wizard, receipts, hash and adopt with resume, verify, `restore-mods.cmd`, settings (`LibraryEnabled`, `LibraryPath`, three attributes each, CLAUDE.md §4) | 2 | 3 d |
| 4 | `feat/version-pins` | `VersionPin`, `Pins` plus JSON callbacks, `PinResolver`, `EffectiveVersionResolver`, the `LoadModOrder` swap, `CopyRowStateFrom`, selection/refresh/export hooks (§5.5), Current observed pins | 1, 2 | 4 d |
| 5 | `feat/library-load` | `LoadModsAsync` switch to `library.LoadAsync`, duplicates as versions, ingest routing in the three import paths and `AddImportedMod` | 2, 4, Stage 4 (full-path key) | 3 d |
| 6 | `feat/deploy` | Planner/executor/journal from LIB-A §5 plus the model inputs (§6), preview UI, override membership from 6.2 `Layout` | 5, 6.2 | 5-6 d |
| 7 | `feat/version-ui` | Version column badge, Version submenu, order-level pin commands, Library window (versions, sizes, used-by, labels, default, trash, GC, verify) | 4, 6 | 4 d |
| 8 | later, with S8/S9 | Bundle key `ModKey@cid12`, bundle import into library, downloader ingest, `FreezeOrdersOnNewVersion` | 7, S8 | with S8/S9 |

About 24-26 working days for PRs 0-7. Everything stays behind `LibraryEnabled` (default off) until G1-G9 pass on the owner's machine in a Publish build (plan R5).

---

## 14. UI touch points

| Where | What | Hook |
|---|---|---|
| Mod row, Version column (`HorizontalModLayout.xaml`, the column after `#` at ~L848) | `VersionBadgeText` plus a glyph: pin (pinned), dotted pin (loose pin), up-arrow ("a different default exists"), warning (unresolved). Tooltip: ContentId short, size, ingest date, label | `ToUIProperty` on `DivinityModData` |
| Mod context menu (`MainResourceDictionary.xaml:264-360`, next to the ForceAllow item `:287`) | **Version ▸**: one checkable item per version (`1.5.0.0 · 9e8f7a6b · 48 MB · 2026-10-02 · default`), then `Follow default (unpin)`, `Pin to this version`, `Update pin to latest`, `Label this version…`, `Delete this version…` | New `ReactiveCommand<(DivinityModData, ModVersion), Unit>` entries in `DivinityGlobalCommands` (`src/Core/Util/DivinityGlobalCommands.cs:31,165` pattern), `canExecute` from `WhenAnyValue` (CLAUDE.md §4) |
| Top menu, Order | `Pin all to current versions`, `Update all pins to latest…` (preview of changes), `Unpin all`, `Freeze all orders` | `Hotkey` properties with `[MenuSettings]` in `src/GUI/ViewModels/AppKeys.cs` (menus are generated by reflection, CLAUDE.md §4) |
| Top menu, Tools | `Mod Library…` (window: tree ModKey → versions; columns Version, ContentId, Size, Ingested, Label, Used by orders, Deployed; actions default/label/trash/restore/cleanup/verify) | New `LibraryWindow` deriving `HideWindowBase<T>`; add a line to `MainWindow.UpdateColorTheme` (plan Stage 7 note) |
| Export to Game | The deploy preview dialog (§6.2) with "Deploy & Export", "Export without deploying" and "Cancel" | `ExportLoadOrderAsync` (`MainWindowViewModel.cs:2897`) |
| Status bar | "Deployed: Tactician (Public) · 935 mods · 3 pinned" | `StatusBarRightText` exists (`:4875`) |

"Update pin to latest" sets the pin to `Default`. It is not "unpin": the order stays frozen, just at the newer version. That distinction is the point of the command.

---

## 15. In-game verification (owner, Windows, Publish build)

Each test settles a claim marked [unverified]. G1-G9 match LIB-A §13.3 where they overlap. G10 and G11 are new.

**Protocol P-OVR: proving that an undeployed override is not loaded (R1, the owner's core claim).** Use a **canary override**: a tiny no-meta pak that overrides one obviously visible base-game asset, for example one main-menu string or GUI file. Build it with the manager's own packer (`DivinityFileUtils.CreatePackageAsync`, `DivinityFileUtils.cs:132`) or LSLib. The owner chooses the exact file from the modding wiki. A visible override already in the owner's collection works too.

| Step | Setup | Expected | Settles |
|---|---|---|---|
| P1 | Library off. Canary in `Mods`, **not** in `modsettings.lsx` | Effect visible | G4a: an override in `Mods` loads without an entry (owner premise; Nexus Mods App notes say "disabled" paks with vanilla overrides may still override [src-web]) |
| P2 | Library on. Order A without the canary → Deploy. `dir Mods` shows no canary | Effect absent | G4b: absence from `Mods` is sufficient |
| P3 | Order B with the canary row → Deploy | Effect visible | Deployment restores it; the hardlinked pak loads (G1) |
| P4 | Back to A, launch, quit, relaunch | Absent both times | No game-side pak cache [unverified] |
| P5 | During P2 and P3: Process Monitor, filter `bg3.exe`, Path contains `\Mods\` | P2: no access to the canary name at all. P3: a read-only open | G3 (no write access requested), G5 (share mode) |

**Version tests.**

| # | Test | Expected |
|---|---|---|
| G9 | Order A pins X at v1 (ContentId a), order B follows v2 (ContentId b). Export A, launch, check the version (mod UI, or SE console `Ext.Mod.GetMod("<uuid>").Info.ModVersion`), then B | The right version each time. `modsettings.lsx` Version64 matches |
| G9b | Same Version64, different bytes (two builds of one release) | Game loads the deployed bytes. Distinguish them by a content change visible in game |
| G6 | Before/after snapshot of `Mods` around a full play session ("Verify library" plus FileIds) | The game writes nothing into `Mods` |
| G7 | In-game mod.io manager: install, update, uninstall one mod with the library on | Record where it writes and whether an update *replaces* (new FileId, ingested as a version) or *rewrites in place* (write-through: the read-only attribute should make it fail; record how the game reacts) |
| G8 | Two profiles: deploy order A for Public; in game, switch to profile 2, whose list references undeployed mods | Record whether the game rewrites that profile's `modsettings.lsx` |
| G10 | Deploy a pak under a different file name (`Foo__9e8f7a6b.pak`) | Loads normally, so file names don't matter. If it doesn't, the rename-on-conflict option (§6.1 step 4) is dropped |
| G11 | Co-op host and client with different ContentIds of one mod | Record whether the game refuses or warns (informs S9 "Co-op ready") |

---

## 16. Test plan

**Unit (Core.Tests; platform-neutral except where marked):**
- T-U1 `ModKey`: meta UUID passes through, non-GUID UUID kept, no-meta → `ovr:` from the file name, part suffix stripped, never contains a path separator.
- T-U2 `UpdateOrderFromActiveMods`-equivalent rebuild (`Order.Clear` + `AddRange`) leaves `Pins` intact. Drag out and back in within a session keeps the pin.
- T-U3 JSON: round-trip with entry pins plus top-level `Pins`; a no-pin order serialises **byte-identical** to today; an upstream-shape reader (a copy of the old `DivinityLoadOrderEntry`) loads it; a pre-feature fork build with `[JsonExtensionData]` round-trips pins; callbacks fire under `SafeDeserialize`.
- T-U4 `PinResolver`: exact, loose (single Version64 match), ambiguous loose → unresolved, missing → unresolved, never latest.
- T-U5 Default version ordering: Preferred > Version64 > IngestedUtc > ContentId; deterministic under shuffling (fixes F6).
- T-U6 `EffectiveVersionResolver.Plan`: no-op when nothing differs; swaps only changed keys; unresolved reported.
- T-U7 Ingest dedupe: same bytes under two names → one version with two `KnownNames`; same UUID+Version64 with different bytes → two versions.
- T-U8 GC rule: each exclusion (default, deployed, pinned in a backup order, labelled, too young) keeps the version.
- T-U9 `Clone`/`SetOrder` copy pins deeply (F3).

**Windows integration (NTFS temp dirs, `[Trait("os","windows")]`, fault-injected `IFileOps`):**
- T-W1 Adopt keeps `Mods` byte- and FileId-identical; link count 2; read-only set.
- T-W2 Ingest staged rename is atomic; kill the process mid-copy → only staging debris, cleaned at start.
- T-W3 Trash and restore a deployed version: unlink first, then rename; restore brings back `version.json`.
- T-W4 Two instances: the second's mutation is refused by the lock.
- T-W5 `restore-mods.cmd` repopulates an emptied `Mods` with no elevation.
- T-W6 Write-through canary: open a deployed pak for write through `Mods` → `UnauthorizedAccessException` because of the read-only attribute.
- T-W7 Recycle Bin behaviour for a link whose other name still exists (data survives).

**App-level (owner, Publish build):**
- T-A1 Migration on a *copy* of the real Mods folder first (a second Windows user, or `appDataGameFolderOverride`, `MainWindowViewModel.cs:1330-1342`), then on the real one. Compare against the inventory receipt.
- T-A2 Switch between two orders differing only by pins: versions swap (F2 regression).
- T-A3 F5 with unsaved pin changes keeps them (F4).
- T-A4 Export, then switch to Current: Current shows the deployed versions, not defaults (§5.6).
- T-A5 Stage 4 snapshot harness: library-off vs library-on with no pins → identical UUID/Version/flags for all non-duplicate mods.

**Performance (T-P):** T-P1 hash time and Defender CPU during migration; T-P2 adopt time; T-P3 cold and warm start with the library vs the Stage 0 baseline (~15 s, 9.3 s loading mods); T-P4 order switch with 0 / 30 / 200 swaps; T-P5 deploy of a 200-link switch.

---

## 17. Open questions only the owner (or a game test) can answer

1. **Auto-deploy on order selection** or only on Export to Game? This design deploys on export, so `Mods` and `modsettings.lsx` always change together (as LIB-A Q1).
2. Does the owner use the **in-game mod.io manager**? If yes, G7 blocks default-on.
3. `DeployScope` default: strict per order (R1) or union across the two profiles (§6.3)?
4. When a mod updates, should saved orders **follow latest** (today's meaning, the default here) or be **frozen** at the old version automatically (`FreezeOrdersOnNewVersion`)?
5. GC minimum age (14 days proposed), and should labelled versions be exempt (proposed yes)?
6. Should "Delete Mod…" in library mode delete *all* versions (proposed) or only the effective one?
7. Renamed no-meta overrides: merge by hand ("Treat as a version of…") or auto-merge by matching overridden file lists [inference: risky]?
8. Is a 1-3 minute one-time hash acceptable during migration, or should adoption run first, with hashing in the background (that would mean a temporary quick-fingerprint id, as LIB-A does)?
9. G4/G8/G10 outcomes (§15) decide whether R1 is fully achievable and whether conflict renaming stays.
10. Stage 6.2 persistence: S6-A (override rows persisted per order, which this design reuses as deployment membership) or S6-C (nothing persisted, so LIB adds it)? See §10.2.

---

## 18. Biggest risks

1. **Model/disk divergence and write-through.** Mitigated by S1-S3 and S6, by FileId as the authority, and by "Verify library". G6/G7 are the tests that could still break it.
2. **Pins lost by an older build re-saving an order** (upstream, or a pre-PR-1 fork build). Mitigated by `[JsonExtensionData]` from PR 1, and by documentation. Unavoidable for upstream.
3. **The UI-thread swap cost on cold materialisation.** Mitigated by async preload (§5.4) and the LRU. Measure with T-P4.
4. **Coupling with 6.2 and Stage 8 formats.** Three small asks of other stages: Stage 4 full-path keys (§10.1), 6.2 "locked" terminology and override rows as membership (§10.2), and the S8 bundle key `ModKey@cid12` (§10.4). All three are cheap now and costly after those stages ship.

---

## 19. Sources

- Microsoft, CreateHardLinkW: NTFS only, files only, same volume, at most 1023 links per file. https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw (also the CreateHardLinkA page: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinka)
- Nexus Mods App developer notes for BG3: paks in Mods but absent from modsettings.lsx are treated as disabled, but those containing vanilla overrides may still override. https://nexus-mods.github.io/NexusMods.App/developers/games/0003-BaldursGate3
- Nexus forums, subfolders in Mods reset modsettings.lsx (user report): https://forums.nexusmods.com/topic/13496718-bg3-why-does-vortex-add-subfolders-when-it-causes-mods-to-not-work
- BG3 Community Wiki, mod types and install locations: https://wiki.bg3.community/Tutorials/Mod-Use/BG3-Mod-Types-and-how-to-install-them ; Patch 7 troubleshooting: https://wiki.bg3.community/en/Tutorials/patch7-troubleshooting
- Nexus forums, Patch 7 Mods folder move (single user report): https://forums.nexusmods.com/topic/11090483-bg3-mod-manager-and-patch-7-workaround
- Sibling designs relied on: `docs/review/stages/alternatives/LIB-A-hardlink-sync.md` (engine, FileId, journal, fallback chain), `docs/review/stages/alternatives/LIB-B-junction-swap.md` (junction mode), `docs/review/stages/alternatives/S4-A-minimal.md`, `docs/review/stages/alternatives/S4-B-pipeline.md` (cache keys, FileInfo stats), `docs/review/stages/alternatives/S6-A-data-model.md` (`Layout`), `docs/review/stages/S8.md` (bundle manifest), `docs/review/stages/S9.md` (download staging).
