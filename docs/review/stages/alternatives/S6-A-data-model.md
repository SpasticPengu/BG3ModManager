# S6-A — Stage 6.2 (overrides folded into the order) and 6.3 (categories/dividers): data model first

Angle A, data model and file format. Written 2026-11-03 against `master` = upstream `fc35358`, plus
`wt-perf` (`origin/perf/list-render`, head `9f2b163`) and `wt-avalonia` (`upstream/avalonia`, head `a9b61fb`).
No reports were in `scratchpad/reports/` when I started. By the end, `AUDIT-PLAN.md` and `AUDIT-CLAUDE.md` had
landed, then `AVA.md`. Their relevant findings are folded in at §1 item 11, §2.2, §3, §6, §11 and §14. No VM2, S6, LOAD or UI
reports existed yet.

Labels used: **[E]** is evidence, with a `path:line` cite. **[I]** is an inference from code. **[GAME]** is a claim
about how BG3 itself behaves. These can't be checked here, so each one names the in-game test that would
settle it (§14).

---

## 0. Headline

**Keep `DivinityLoadOrder.Order` mods-only, exactly as it is today. Put dividers *and* folded-in overrides in
a new, optional sibling member, `"Layout"`, in the order JSON. Each layout row is anchored to the UUID of the
`Order` entry it sits before.** In memory there is still one flat `ActiveMods` collection holding mods,
`CategoryDivider` rows and pinned override rows. `UpdateOrderFromActiveMods` projects it into `Order`
(the rows that get written) plus `Layout` (everything else). Older builds and upstream deserialise the same
`Order` they always have and ignore `Layout`. Nothing they compute changes, so they can't show a
missing-mod warning, write an unwanted `ForceAllowInLoadOrder`, or put a bad `<ModuleShortDesc>` into
`modsettings.lsx`. The plan's literal wording ("a divider entry in `DivinityLoadOrder.Order`", plan
Stage 6.3) **should be changed.** §2 lists seven code paths, in upstream and in the fork itself, that
misbehave if a non-mod entry ever lands in `Order`.

For 6.2, folding overrides in **does not change what the game sees**. A pinned override is shown and
ordered in the active list, persisted only in `Layout`, and never written to `modsettings.lsx`, which is
also true today. An override with `ForceAllowInLoadOrder` is written at its row position, also as today.
For any order without categories, the `modsettings.lsx` bytes must be identical before and after the change.
That invariant is the main test.

---

## 1. The owner's intentions I have gleaned

You asked for this list in the relayed request. Sources: CLAUDE.md, the plan, the plan overview, and the
computed task.

1. **Branch discipline.** `master` = upstream, kept clean. Upstreamable fixes are cut from `master`.
   Fork-only features live on `fork/main`. 6.1 render perf goes first and goes upstream. 6.2 and 6.3 are
   fork-only (`feat/overrides-in-order` → `feat/categories`). (plan §3, Stage 6 header.)
2. **Categories are flat rows in one collection, never WPF grouping.** Virtualization must survive at
   935 active rows (plan Stage 6 "landmine", R2).
3. **The game must never see a divider.** Every export must still produce a `modsettings.lsx` the game
   accepts, proven by booting the game and not just by looking at the list (plan 6.2, 6.3, R7, §15).
4. **Overrides become a normal category** in the active list, as in the plan's mock-up ("▶ Overrides 2
   collapsed"). The separate Overrides panel goes away.
5. **Orders are shared with friends and published.** The order JSON must round-trip through archive
   export/import (Stages 2–3), multi-order export (Stage 8) and later syncing (Stage 9), without confusing
   older builds or upstream.
6. **Don't box in Stage 5.** The filter may move from per-item `Visibility` stamping to collection
   filtering.
7. **Evidence over argument.** Measure before you change anything. Every claim gets a cite, and the plan
   records several hypotheses that were disproved.
8. **Ideas only from Redux/Avalonia, no code.**
9. **Windows now, Linux/Avalonia not ruled out.** So logic goes into Core and view models, not code-behind
   (plan Stage 7 Avalonia note).
10. **User-visible failures go through `ShowAlert`.** House rules: no `string?` in Core/GUI; tabs, CRLF,
    BOM, Allman, `var`; new types unprefixed; mutate settings in place.
11. [I] **You probably use placeholder "divider mods" today.** The plan mentions "divider entries made of `=`
    characters" in your 900-mod order (plan Stage 0 notes, ~L404-407). Note that AUDIT-PLAN claim 14 refutes the
    plan's link from those names to the 58 `MeasureText` exceptions: `=` is mapped in every UI font. The
    entries themselves still appear to exist. Real categories would make those placeholder paks
    unnecessary. §11 describes an optional conversion.

---

## 2. What the code does today (evidence)

### 2.1 Order JSON read and write

- **Shape.** `DivinityLoadOrder` is `[DataContract]` and only `Order` is a `[DataMember]`
  (`src/Core/Models/DivinityLoadOrder.cs:23-47`). `Name`, `FilePath` and the flags are not serialised.
  The file is therefore `{"Order":[{"UUID":…,"Name":…}, …]}`, and `Missing` isn't written
  (`DivinityLoadOrder.cs:7-15`).
- **Write.** `JsonConvert.SerializeObject(order, Indented)` (`src/Core/Util/DivinityModDataLoader.cs:1085,1103`).
  The archive export uses the same call (`src/GUI/ViewModels/MainWindowViewModel.cs:3581`).
- **Read.** Every path goes through `DivinityJsonUtils.SafeDeserialize<DivinityLoadOrder>` or
  `TrySafeDeserializeFromPath`: the loader at `DivinityModDataLoader.cs:1129,1167,1194,1216` and archive
  import at `MainWindowViewModel.cs:3375,3511`. The settings object sets only an `Error` handler
  (`src/Core/Util/DivinityJsonUtils.cs:11-18`). Newtonsoft's default `MissingMemberHandling.Ignore` therefore
  applies: **an unknown top-level member is silently ignored by every existing build** [E]. When such a build
  saves, `DataContract` emits only `Order`, so **the unknown member is silently dropped** [E]. That is
  information lost, but nothing is corrupted.

### 2.2 What an *unknown entry inside `Order`* does to an older build or upstream

Suppose a divider were written as `{"UUID":"category:…","Name":"Cosmetics"}` inside `Order`:

| # | Site | Effect |
|---|---|---|
| 1 | `MainWindowViewModel.cs:2724-2746` `DisplayMissingMods` | `TryGetMod` fails and `IgnoreMod` is false, so **every divider appears in the "Missing Mods in Load Order" message box** on every load |
| 2 | `MainWindowViewModel.cs:4677-4679` (remove missing mods) | `Order.RemoveAll(x => !ModExists(x.UUID))` **deletes the dividers** |
| 3 | `MainWindowViewModel.cs:2689-2690` Save As | Keeps only entries whose UUID is in `Mods`, so **dividers are stripped** |
| 4 | `MainWindowViewModel.cs:2318, 2972` | `SelectedProfile.ActiveMods` is rebuilt from `Order.Select(x => x.UUID)`, which **puts dividers into the in-memory profile mod list** |
| 5 | `MainWindowViewModel.cs:3572` archive export | Harmless: the UUID matches no pak |
| 6 | `DivinityModDataLoader.cs:1431-1468` `BuildOutputList` | The lookup fails and it logs `[*ERROR*] Missing mod` per divider per export. It isn't written (good), but it's noisy |
| 7 | `MainWindowViewModel.cs:4886-4887` `UpdateOrderFromActiveMods` | Rebuilds `Order` from `ActiveMods`, so in an older build the dividers vanish on the next edit |

AUDIT-PLAN claim 32 reaches the same conclusion independently: `BuildOutputList` strips non-resolving UUIDs, but the
zip export, the JSON writer, Save As and older builds do not.
Avalonia upstream has the same problem in a different form: it reads legacy `Order` entries without
`Type` as mods (`wt-avalonia/src/Core/Json/IModOrderEntryConverter.cs:31-35`).
**Conclusion [E]: a non-mod entry in `Order` causes a visible regression in every reader that exists today.
A sibling member costs none of them anything.**

### 2.3 Overrides (force-loaded mods) today

- There are three kinds, set by the loader (`DivinityModDataLoader.cs:560-640`):
  1. **No `meta.lsx`, overrides base dirs.** `UUID = <full pak path>`, `Folder` = the first overridden
     builtin folder, `ModType = "File Override"`, `IsForceLoaded = true`, `HasMetadata = false`
     (`:604-639`).
  2. **Has `meta.lsx`, overrides base dirs, no files of its own.** `IsForceLoaded = true`,
     `IsForceLoadedMergedMod = false` (`:600-603, 639`).
  3. **Has `meta.lsx`, overrides base dirs, and has its own mod folder.** `IsForceLoadedMergedMod = true`.
     Treated as a normal mod.
- `CanAddToLoadOrder = modType != "Adventure" && !isLarianMod && (!isForceLoaded || isMergedMod) || forceAllowInLoadOrder`
  (`src/Core/Models/DivinityModData.cs:381-384, 530-531`).
- The Overrides panel shows `IsForceLoaded && !IsForceLoadedMergedMod && !ForceAllowInLoadOrder`
  (`MainWindowViewModel.cs:5244-5246`). It sits under the active list
  (`src/GUI/Views/HorizontalModLayout.xaml:127-141`), and its visibility is driven by `HasForceLoadedMods`
  (`HorizontalModLayout.xaml.cs:530-531, 550-571`).
- The toggle only exists for kind 2: `ToggleForceAllowInLoadOrderVisibility` needs
  `IsForceLoaded && HasMetadata && !IsForceLoadedMergedMod` (`DivinityModData.cs:419-421`). It calls
  `AddActiveMod`/`RemoveActiveMod` (`src/Core/Util/DivinityGlobalCommands.cs:111-124`).
- **`ForceAllowInLoadOrder` has no storage of its own. It is persisted by "being in `Order`".**
  `LoadModOrder` sets it to true for any force-loaded mod whose UUID appears in the order
  (`MainWindowViewModel.cs:2126-2129`), and nothing else sets it except the toggle [E]. That one fact
  settles 6.2: **an override must not go into `Order` unless it is meant to be written.** If it did, every
  older build would turn on `ForceAllowInLoadOrder` and start writing it to `modsettings.lsx`.
- [GAME] Kinds 1 and 2 are loaded by the engine regardless of `modsettings.lsx`. Evidence: the doc comment
  "This data is always loaded regardless if the mod is enabled or not" (`src/Core/Models/DivinityBaseModData.cs:52`)
  and the existence of the Overrides panel. Not verifiable here, see test G2.

### 2.4 The export path

`ExportLoadOrderAsync` (`MainWindowViewModel.cs:2897-3009`) runs `UpdateOrderFromActiveMods()`, then
`BuildOutputList(SelectedModOrder.Order, mods.Items, …, adventure)` (`:2931`), then
`ExportModSettingsToFileAsync` → `GenerateModSettingsFile`, a `String.Format` per mod
(`DivinityModDataLoader.cs:1471-1487`, placeholder order per CLAUDE.md §6). `BuildOutputList` writes the
adventure first, then each non-missing `Order` entry found in `allMods`, adding dependencies when enabled
(`:1431-1468`). It does **not** check `CanAddToLoadOrder`. **`Order` is the whole contract with the game.**
That is why `Order` must stay "exactly the rows to write".

---

## 3. Decision: sidecar `Layout`, anchored by UUID

Three options were considered.

| Option | Older builds / upstream | Fork code churn | Verdict |
|---|---|---|---|
| A. Divider entries inline in `Order` (plan wording) | Missing-mod popup, dividers deleted or stripped, profile list polluted (§2.2) | Every `Order` consumer needs a filter (≈25 sites) | **Rejected** |
| B. A separate `.layout` file next to each order | Untouched, and it survives an older build re-saving | Archive export/import, Stage 8 and rename/delete must all carry a second file. `FindLoadOrderFilesInDirectoryAsync` loads *every* `*.json` recursively as an order (`DivinityModDataLoader.cs:1155-1157`), so it could never be `.json` | Rejected for now. It is the fallback if R-A (§12) bites |
| C. **Optional `Layout` member in the same JSON** | Ignored on read. Dropped on re-save (no corruption) | `Order` consumers are untouched. Only the projection and load points change | **Chosen** |

Avalonia also shows what the hierarchy costs. AVA.md (its 6.2/6.3 rows and its archive-export section)
found that the rewrite's zip export builds its pak list from top-level `Entries` only, so **every mod
inside a container is left out of the zip**, and force-loaded mods are no longer exported at all. Keeping
`Order` flat and mods-only means every existing `Order` consumer (zip, Stage 8, Save As) stays correct
without any change.

Avalonia check: upstream's new format uses a top-level key **`Entries`**, with nested
`{"Type":"Container","Children":[…]}` (`wt-avalonia/src/Core/Models/Mod/Order/ModOrder.cs:23`,
`ModOrderContainer.cs:8-25`). It migrates a legacy `Order` through `[JsonExtensionData] Extras`
(`ModOrder.cs:321-333`). `Add` de-duplicates top-level ids only (`ModOrder.cs:127-150`). **The fork must
never write a key named `Entries`.** [I] If a file had both `Entries` (with nested children) and `Order`,
Avalonia would append every mod a second time at the top level. Any other unknown key, including
`Layout`, lands in Avalonia's `Extras`. [I] Whether Avalonia writes `Extras` back is unverified
(System.Text.Json extension data on a private property). Either way it reads the mods correctly.

---

## 4. File format

### 4.1 Shape

```jsonc
{
  "Order": [                                   // unchanged: exactly the modsettings.lsx entries (minus adventure/deps)
    { "UUID": "2bd9bdbe-…-a1", "Name": "Combat Extender" },
    { "UUID": "5f1c3a8e-…-b2", "Name": "Party Limit Begone" },
    { "UUID": "9a77e0c1-…-c3", "Name": "Tactician Enhanced" },
    { "UUID": "e3d4a5b6-…-d4", "Name": "Better Hair" }
  ],
  "Layout": {                                  // optional; omitted entirely when there is nothing but mods
    "Version": 1,
    "Rows": [
      { "Kind": "Category", "Id": "6f1c0d2e-…", "Name": "Gameplay Overhauls", "Before": "2bd9bdbe-…-a1", "At": 0 },
      { "Kind": "Category", "Id": "91ab44f0-…", "Name": "Overrides", "Role": "Overrides", "Collapsed": true,
        "Before": "e3d4a5b6-…-d4", "At": 3 },
      { "Kind": "Override", "Pak": "NoCameraLimit.pak", "Name": "NoCameraLimit", "Before": "e3d4a5b6-…-d4", "At": 3 },
      { "Kind": "Override", "UUID": "c0ffee00-…-e5", "Pak": "UnlockLevelCurve.pak", "Name": "Unlock Level Curve",
        "Before": "e3d4a5b6-…-d4", "At": 3 },
      { "Kind": "Category", "Id": "c0de1234-…", "Name": "Cosmetics", "Before": "e3d4a5b6-…-d4", "At": 3 }
    ]
  }
}
```

This is the plan's mock-up. In the list it renders as:

`▼ Gameplay Overhauls 3` · 0 Combat Extender · 1 Party Limit Begone · 2 Tactician Enhanced · `▶ Overrides 2 collapsed` · `▼ Cosmetics 1` · 3 Better Hair

(The `#` column shows `Index`, which is 0-based today, `DivinityModData.cs:388`, `MainWindowViewModel.cs:2120-2131`.)

### 4.2 Rules

- **Anchor.** A layout row sits immediately before the `Order` entry whose UUID is `Before`. If `Before` is
  omitted or null, it sits at the end. Rows that share an anchor keep their order in `Rows`.
- **`At`** is the index into `Order` of the anchor at save time (`Order.Count` for end of list). It is the
  fallback when `Before` is no longer in `Order`, for example after an older build or external tool removed
  that mod. During resolution the anchor index is kept non-decreasing through `Rows`, so categories can't
  cross each other.
- **Anchors are computed over `Order` including entries marked `Missing`.** A missing mod isn't shown, so a
  row anchored before it lands before the next present one.
- **`Override` rows identify the pak by file name (`Pak`).** They carry `UUID` only when the mod has a
  `meta.lsx` and the UUID parses as a GUID. A no-meta override's UUID is the **full local path**
  (`DivinityModDataLoader.cs:625`). That path is meaningless on a friend's machine and leaks the Windows
  user name (`C:\Users\<name>\AppData\…`). **It must never be serialised.** Matching on load is by `UUID`
  if present and found among force-loaded mods, otherwise by `Pak` (case-insensitive).
- **`Role: "Overrides"`** marks the category that newly found or imported overrides are appended to. There
  is at most one. If none exists and there are unplaced overrides, one is created at the end.
- **`Collapsed`** is persisted (§10). It is emitted only when true.
- **Versioning.** `Version` starts at 1. Readers **keep rows of unknown `Kind`** (passthrough) and write them
  back at their anchors. Each row and the `Layout` object carry `[Newtonsoft.Json.JsonExtensionData]`, so
  unknown fields survive too. This keeps a 6.2-only fork build from deleting 6.3 categories, and keeps a 6.3
  build from deleting whatever comes later. **This has to be in the first PR that writes `Layout`.**
- **Byte-identity.** `Layout` is `[DataMember(EmitDefaultValue = false)]` and is null when there is nothing
  but mods, so orders with no dividers or overrides serialise exactly as today.
- **Reserved keys.** Never emit `Entries` (Avalonia, see §3).

### 4.3 Compatibility matrix

| Reader | Reads a fork file with `Layout` | Writes it back | modsettings.lsx |
|---|---|---|---|
| Upstream master release / pre-6.2 fork build | `Order` only. No popup, no `ForceAllow` flips (§2.1-2.3) | `Layout` dropped silently (R-A) | Identical to the fork's (same `Order`) |
| 6.2 fork build (overrides only) | Override rows applied. Category rows passed through untouched | Preserved | Same |
| 6.3 fork build | Everything | Everything | Same |
| Upstream Avalonia | Legacy `Order` migrated to `Entries`, `Layout` goes to `Extras` [I] | Writes `Entries`. `Layout` kept only if Avalonia writes `Extras` back (unverified) | Its own exporter |
| `.txt` / `.tsv` import (`DivinityModDataLoader.cs:1240-1325`) | No layout. Unaffected | n/a | n/a |
| "Export order as list" JSON (`List<DivinitySerializedModData>`, `MainWindowViewModel.cs:3802-3813`, re-imported by `DivinityModDataLoader.cs:1222`) | Must contain **mods only**. Filter (§8, F-11) | n/a | n/a |

---

## 5. 6.2: what "folding overrides into the order" means

### 5.1 Model

| Kind (§2.3) | Shown in | In `Order`? | In `Layout`? | Written to modsettings.lsx? | `Index` |
|---|---|---|---|---|---|
| 1 no-meta override | Active list, pinned row | **Never** | `Override` row (by `Pak`) | **Never** (same as today) | −1 |
| 2 meta override, `ForceAllowInLoadOrder = false` | Active list, pinned row | No | `Override` row | No (same as today) | −1 |
| 2 meta override, `ForceAllowInLoadOrder = true` | Active list, normal row | **Yes** | No | **Yes**, at its row position among written rows (same as today) | contiguous |
| 3 merged override | Active or inactive, normal | as any mod | No | as any mod (unchanged) | contiguous |

"Pinned" means it can't be deactivated, because the game always loads it [GAME]. It can be dragged freely
inside the active list (between categories and around mods), but **its position has no effect on the
game.** [GAME] The relative priority of overrides is decided by the engine, not by `modsettings.lsx`
(test G2). The row shows a lock glyph and a blank `#`.

**Index numbering counts only written rows.** This is a deliberate difference from the plan's mock-up,
which numbers past the overrides ("6. Better Hair" after "2 collapsed"). If overrides took numbers, `#`
would stop matching `modsettings.lsx` positions and the copy-order-to-clipboard output
(`MainWindowViewModel.cs:5131-5143`). **Owner decision Q1.**

### 5.2 Semantics changes the owner must accept (Q2, Q3)

- **`ForceAllowInLoadOrder` becomes per-order (Q2).** Today it stays true after switching to an order
  that doesn't contain the mod. The mod then falls into `InactiveMods`, because `CanAddToLoadOrder` is now
  true (`MainWindowViewModel.cs:2101-2144`). In fold mode that would make the same pak appear pinned in one
  order and inactive in another. Proposal: at the top of `LoadModOrder`, reset it to false for
  `IsForceLoaded && !IsForceLoadedMergedMod`, then let the existing `:2126-2129` turn it back on for mods in
  `Order`. This matches how it is already persisted.
- **Overrides can't be dropped on the inactive list (Q3).** To stop writing a kind-2 override, the user
  clears `ForceAllowInLoadOrder` from the context menu (`MainResourceDictionary.xaml` ToggleForceAllow
  item, ~L284). The row stays in place as pinned.

### 5.3 modsettings.lsx rules (6.2 and 6.3 combined)

1. The written set is still `BuildOutputList(Order, …)`: adventure first, then `Order` in sequence, plus
   auto-dependencies. **`BuildOutputList` and `GenerateModSettingsFile` are unchanged except for the guards
   in 2-3.**
2. `Order` is only ever produced by `UpdateOrderFromActiveMods` from rows where `IsLoadOrderEntry`
   (= `!IsDivider && CanAddToLoadOrder`). Dividers and pinned overrides can't get in.
3. Guards (defence in depth for R7). `GenerateModSettingsFile` skips `IsDivider`
   (`DivinityModDataLoader.cs:1476-1484`). `BuildOutputList` skips entries whose UUID has the divider
   prefix (`:1446`). Both are inert for every order that exists today.
4. Invariant **INV-1**: for any order, the `modsettings.lsx` exported by a 6.3 build is byte-identical to the
   one exported after deleting every divider and leaving all overrides pinned. Invariant **INV-2**: for an
   order with no `Layout`, a 6.3 build writes the same `modsettings.lsx` as `master`.
5. Optional and separately upstreamable (`fix/no-path-uuid-in-modsettings`, cut from `master`): skip any mod
   with `IsForceLoaded && !HasMetadata` in `BuildOutputList`. Its UUID is a file path in a `type="guid"`
   attribute (`DivinityApp.cs:33`). [I] It is reachable today only by a hand-edited or foreign order JSON
   (`LoadModOrder` flips `ForceAllow` on anything in `Order`). Keep it out of the feature PRs.

---

## 6. In-memory model

**Invariant INV-3:** `MainWindowViewModel.ActiveMods` is the *row list*. It holds load-order mods,
`CategoryDivider` rows and pinned overrides, in display order. `DivinityLoadOrder.Order` = the rows where
`IsLoadOrderEntry`, in that sequence. `DivinityLoadOrder.Layout` = everything else, anchored. A
`CategoryDivider` is **never** added to the `mods` `SourceCache` (`MainWindowViewModel.cs:80`), so `Mods`,
`addonMods`, the selection counters (`:5261-5264`), `TryGetMod`, `ModIsActive`, AutoSize (`HorizontalModLayout.xaml.cs:806-834`)
and the delete flow never see one.

Grouping note: AUDIT-PLAN claim 17 shows the `IsGrouping` landmine could be removed
(`VirtualizingPanel.IsVirtualizingWhenGrouping`). The owner's flat-row decision still stands. It is also what makes
the drag/drop index rules in §9 simple.

Why a `DivinityModData` subclass and not a new interface: `ActiveMods` is
`ObservableCollectionExtended<DivinityModData>` (`MainWindowViewModel.cs:162`). It is used about 120 times in
`MainWindowViewModel`, `HorizontalModLayout.xaml.cs` and the drag/drop handlers, and the selection fixes in
wt-perf (`309b81e`, `396d7b2`) are keyed on `DivinityModData.UUID`. Changing the element type would touch
all of them and turn every upstream merge into a conflict. The subclass keeps those sites compiling. The
risk that dividers leak into mod-only code paths is dealt with by an explicit audit (§8) and two predicates.

### 6.1 Core: `DivinityModData` additions (`src/Core/Models/DivinityModData.cs`)

```csharp
	// next to IsActive (~L181)
	/// <summary>True for category header rows in the active list. Never true for anything in the mods cache.</summary>
	public virtual bool IsDivider => false;

	/// <summary>Always loaded by the game, shown in the active list, never written to modsettings.lsx.</summary>
	public bool IsPinnedOverride => !IsDivider && IsForceLoaded && !IsForceLoadedMergedMod && !ForceAllowInLoadOrder;

	/// <summary>Rows that become DivinityLoadOrder.Order entries, and therefore modsettings.lsx entries.</summary>
	public bool IsLoadOrderEntry => !IsDivider && CanAddToLoadOrder;

	/// <summary>Hidden because its category is collapsed. Separate from Visibility, which the filter owns.</summary>
	[Reactive] public bool IsCollapsedByCategory { get; set; }
```

`IsSelected` (`:185-196`). A collapsed row must not become selected, by the same rule as a filtered row:

```csharp
			if (value && (Visibility != Visibility.Visible || IsCollapsedByCategory))
			{
				value = false;
			}
```

`CanAllowInLoadOrderCheck` (`:381-384`). Exclude dividers so `CanAddToLoadOrder` and `HasInvalidUUID`
(`:367-373, 446-448`) stay false. Without this the row would get the red invalid-UUID colours (`:483-497`):

```csharp
	private static bool CanAllowInLoadOrderCheck(string modType, bool isLarianMod, bool isForceLoaded, bool isMergedMod, bool forceAllowInLoadOrder)
	{
		if (modType == CategoryDivider.MOD_TYPE) return false;
		return modType != "Adventure" && !isLarianMod && (!isForceLoaded || isMergedMod) || forceAllowInLoadOrder;
	}
```

### 6.2 Core: `CategoryDivider` (new file, `src/Core/Models/CategoryDivider.cs`, BOM, CRLF, tabs)

```csharp
namespace DivinityModManager.Models;

/// <summary>
/// A category header row in the active list. Lives only in MainWindowViewModel.ActiveMods:
/// never in the mods SourceCache, never in DivinityLoadOrder.Order, never in modsettings.lsx.
/// </summary>
public class CategoryDivider : DivinityModData
{
	public const string MOD_TYPE = "Category Divider";
	public const string UUID_PREFIX = "category:";
	public const string ROLE_OVERRIDES = "Overrides";

	public static bool IsDividerUUID(string uuid) => uuid != null && uuid.StartsWith(UUID_PREFIX, StringComparison.Ordinal);

	public string CategoryId { get; }
	public string Role { get; set; }

	[Reactive] public bool IsCollapsed { get; set; }
	[Reactive] public int MemberCount { get; set; }
	[Reactive] public bool IsEditingName { get; set; }

	public override bool IsDivider => true;

	public override string GetHelpText()
	{
		var state = IsCollapsed ? "collapsed" : "expanded";
		return $"Category {Name}, {MemberCount} mods, {state}";
	}

	public OrderLayoutRow ToLayoutRow()
	{
		return new OrderLayoutRow
		{
			Kind = OrderLayoutRow.KIND_CATEGORY,
			Id = CategoryId,
			Name = Name,
			Role = Role,
			Collapsed = IsCollapsed
		};
	}

	public CategoryDivider(string id, string name) : base()
	{
		CategoryId = !String.IsNullOrWhiteSpace(id) ? id : Guid.NewGuid().ToString();
		UUID = UUID_PREFIX + CategoryId;
		Name = name;
		ModType = MOD_TYPE;
		IsActive = true;
	}
}
```

`IsActive = true` makes the existing `CanDrag` subscription (`DivinityModData.cs:465-480`) report true.
`Visibility` defaults to `Visible` (enum value 0). AVA.md recommends adding "no new `System.Windows` types to
Core". The format types (§6.3) and the builder logic are WPF-free. `CategoryDivider` only inherits the WPF
dependency `DivinityModData` already has, and adds no `System.Windows` usage of its own.

### 6.3 Core: the format types (new file, `src/Core/Models/OrderLayout.cs`)

```csharp
using Newtonsoft.Json.Linq;

using System.Runtime.Serialization;

namespace DivinityModManager.Models;

[DataContract]
public class OrderLayoutRow
{
	public const string KIND_CATEGORY = "Category";
	public const string KIND_OVERRIDE = "Override";

	[DataMember] public string Kind { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Id { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Name { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Role { get; set; }
	[DataMember(EmitDefaultValue = false)] public bool Collapsed { get; set; }
	[DataMember(EmitDefaultValue = false)] public string UUID { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Pak { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Before { get; set; }
	[DataMember] public int At { get; set; }

	// Fully qualified on purpose: Core globally imports System.Text.Json.Serialization (src/Core/Usings.cs),
	// whose JsonExtensionData would compile and be ignored by Newtonsoft (CLAUDE.md §5).
	[Newtonsoft.Json.JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

	public bool IsKnownKind => Kind == KIND_CATEGORY || Kind == KIND_OVERRIDE;

	public OrderLayoutRow Clone() => (OrderLayoutRow)MemberwiseClone();
}

[DataContract]
public class OrderLayout
{
	public const int CURRENT_VERSION = 1;

	[DataMember] public int Version { get; set; } = CURRENT_VERSION;
	[DataMember] public List<OrderLayoutRow> Rows { get; set; } = [];
	[Newtonsoft.Json.JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

	public OrderLayout Clone()
	{
		return new OrderLayout
		{
			Version = Version,
			Rows = Rows?.Select(x => x.Clone()).ToList() ?? [],
			Extra = Extra
		};
	}
}
```

[I] Newtonsoft honours `JsonExtensionData` on `[DataContract]` types. Test U1 confirms it with a
round-trip.

### 6.4 Core: `DivinityLoadOrder` (`src/Core/Models/DivinityLoadOrder.cs`)

```csharp
	[DataMember]
	public List<DivinityLoadOrderEntry> Order { get; set; } = new List<DivinityLoadOrderEntry>();

	/// <summary>Fork-only: dividers and pinned overrides, anchored to Order entries. Null when there are none,
	/// so plain orders serialize byte-for-byte as before. Older builds ignore it on read and drop it on save.</summary>
	[DataMember(EmitDefaultValue = false)]
	public OrderLayout Layout { get; set; }
```

- `SetOrder(DivinityLoadOrder nextOrder)` (`:289-293`): add `Layout = nextOrder.Layout?.Clone();`.
  `BackupCurrentLoadOrderAsync` (`MainWindowViewModel.cs:2855`) and the restore path (`:2311-2312`) use it,
  so LastExported keeps the layout.
- `SetOrder(IEnumerable<DivinityLoadOrderEntry>)` (`:283-287`): still entries only, with a doc comment
  saying so.
- `Clone()` (`:304-312`): add `Layout = this.Layout?.Clone()`.

### 6.5 Core: `OrderLayoutBuilder` (new, `src/Core/Util/OrderLayoutBuilder.cs`, pure and unit-testable)

```csharp
using DivinityModManager.Models;

namespace DivinityModManager.Util;

public static class OrderLayoutBuilder
{
	/// <summary>Project the row list into a Layout. Returns null when the rows are all load-order entries.</summary>
	public static OrderLayout FromRows(IEnumerable<DivinityModData> rows, IEnumerable<OrderLayoutRow> passthrough)
	{
		var layout = new OrderLayout();
		var pending = new List<OrderLayoutRow>();
		var orderIndex = 0;
		var orderUUIDs = new Dictionary<string, int>();

		foreach (var row in rows)
		{
			if (row.IsLoadOrderEntry)
			{
				foreach (var p in pending)
				{
					p.Before = row.UUID;
					p.At = orderIndex;
				}
				layout.Rows.AddRange(pending);
				pending.Clear();
				orderUUIDs[row.UUID] = orderIndex;
				orderIndex++;
			}
			else if (row is CategoryDivider divider)
			{
				pending.Add(divider.ToLayoutRow());
			}
			else if (row.IsPinnedOverride)
			{
				pending.Add(ToOverrideRow(row));
			}
		}
		foreach (var p in pending)
		{
			p.Before = null;
			p.At = orderIndex;
		}
		layout.Rows.AddRange(pending);

		if (passthrough != null)
		{
			foreach (var p in passthrough)
			{
				var copy = p.Clone();
				copy.At = copy.Before != null && orderUUIDs.TryGetValue(copy.Before, out var at) ? at : Math.Min(copy.At, orderIndex);
				layout.Rows.Add(copy);
			}
		}

		return layout.Rows.Count > 0 ? layout : null;
	}

	public static OrderLayoutRow ToOverrideRow(DivinityModData mod)
	{
		var row = new OrderLayoutRow
		{
			Kind = OrderLayoutRow.KIND_OVERRIDE,
			Name = mod.Name,
			Pak = !String.IsNullOrEmpty(mod.FilePath) ? Path.GetFileName(mod.FilePath) : null
		};
		// Never serialize a no-meta override's UUID: it is the full local pak path (DivinityModDataLoader.cs:625).
		if (mod.HasMetadata && Guid.TryParse(mod.UUID, out _))
		{
			row.UUID = mod.UUID;
		}
		return row;
	}

	/// <summary>
	/// Build the active row list from an order. orderedMods are the present load-order mods in Order sequence;
	/// overrides are the pinned overrides currently installed. Unknown-kind rows are returned via passthrough.
	/// </summary>
	public static List<DivinityModData> BuildRows(DivinityLoadOrder order, IList<DivinityModData> orderedMods,
		IEnumerable<DivinityModData> overrides, bool createOverridesCategory, out List<OrderLayoutRow> passthrough)
	{
		passthrough = [];
		var rows = new List<DivinityModData>(orderedMods.Count + 32);
		var orderIndexOf = new Dictionary<string, int>();
		for (var i = 0; i < order.Order.Count; i++)
		{
			var uuid = order.Order[i].UUID;
			if (!String.IsNullOrEmpty(uuid)) orderIndexOf.TryAdd(uuid, i);
		}

		var overridePool = overrides.ToList();
		var placedOverrides = new HashSet<DivinityModData>();
		var anchored = new List<(int At, DivinityModData Row)>();
		var lastAt = 0;
		CategoryDivider overridesCategory = null;

		foreach (var r in order.Layout?.Rows ?? [])
		{
			if (r == null) continue;
			var at = r.Before != null && orderIndexOf.TryGetValue(r.Before, out var idx) ? idx : Math.Clamp(r.At, 0, order.Order.Count);
			at = Math.Max(at, lastAt);
			lastAt = at;

			if (r.Kind == OrderLayoutRow.KIND_CATEGORY)
			{
				var divider = new CategoryDivider(r.Id, r.Name) { Role = r.Role, IsCollapsed = r.Collapsed };
				if (divider.Role == CategoryDivider.ROLE_OVERRIDES)
				{
					if (overridesCategory != null) divider.Role = null;
					else overridesCategory = divider;
				}
				anchored.Add((at, divider));
			}
			else if (r.Kind == OrderLayoutRow.KIND_OVERRIDE)
			{
				var mod = FindOverride(overridePool, r);
				if (mod != null && placedOverrides.Add(mod)) anchored.Add((at, mod));
			}
			else
			{
				passthrough.Add(r.Clone());
			}
		}

		// Merge: present mods carry their Order index; layout rows go before the first mod with index >= At.
		var a = 0;
		foreach (var mod in orderedMods)
		{
			var modAt = orderIndexOf.TryGetValue(mod.UUID, out var i) ? i : int.MaxValue;
			while (a < anchored.Count && anchored[a].At <= modAt)
			{
				rows.Add(anchored[a].Row);
				a++;
			}
			rows.Add(mod);
		}
		for (; a < anchored.Count; a++) rows.Add(anchored[a].Row);

		// Newly installed / never placed overrides: end of the Overrides category, or a new one at the end.
		var unplaced = overridePool.Where(x => !placedOverrides.Contains(x)).OrderBy(x => x.Name).ToList();
		if (unplaced.Count > 0)
		{
			if (overridesCategory == null && createOverridesCategory)
			{
				overridesCategory = new CategoryDivider(null, "Overrides") { Role = CategoryDivider.ROLE_OVERRIDES };
				rows.Add(overridesCategory);
			}
			var insertAt = overridesCategory != null ? BlockEnd(rows, rows.IndexOf(overridesCategory)) : rows.Count;
			rows.InsertRange(insertAt, unplaced);
		}
		return rows;
	}

	public static int BlockEnd(IList<DivinityModData> rows, int dividerIndex)
	{
		for (var i = dividerIndex + 1; i < rows.Count; i++)
		{
			if (rows[i].IsDivider) return i;
		}
		return rows.Count;
	}

	private static DivinityModData FindOverride(List<DivinityModData> pool, OrderLayoutRow r)
	{
		if (!String.IsNullOrEmpty(r.UUID))
		{
			var byId = pool.FirstOrDefault(x => x.UUID == r.UUID);
			if (byId != null) return byId;
		}
		if (!String.IsNullOrEmpty(r.Pak))
		{
			return pool.FirstOrDefault(x => !String.IsNullOrEmpty(x.FilePath)
				&& Path.GetFileName(x.FilePath).Equals(r.Pak, StringComparison.OrdinalIgnoreCase));
		}
		return null;
	}
}
```

Notes. `orderedMods` arrive sorted by `Index`, which `LoadModOrder` assigns in `Order` sequence
(`MainWindowViewModel.cs:2108-2135`), so the merge needs one pass. A present mod without an `Order` index
can't happen in practice; one gets `int.MaxValue` and flushes every remaining anchor ahead of it, which is
harmless. Unresolved `Override` rows (pak not installed) are dropped and logged. Same for missing mods: as
today, `UpdateOrderFromActiveMods` discards `Order` entries that aren't shown.

---

## 7. View-model hook points (GUI)

All in `src/GUI/ViewModels/MainWindowViewModel.cs` unless stated. Each label (F-n) is cited from §8 and §13.

**F-1 `RenumberActiveMods()`.** New. It replaces every "Index = position in ActiveMods". O(n), and assigns
only on change, so 935 rows don't raise 935 notifications:

```csharp
	public void RenumberActiveMods()
	{
		var index = 0;
		foreach (var row in ActiveMods)
		{
			if (row.IsLoadOrderEntry)
			{
				if (row.Index != index) row.Index = index;
				index++;
			}
			else if (row.Index != -1)
			{
				row.Index = -1;
			}
		}
	}
```

Callers: the drop handler (replaces `ModListDropHandler.cs:218-221`, which is also O(n²) via `IndexOf`),
`AddActiveMod` (replaces `:4847`), `LoadModOrder`, the ForceAllow toggle, and every 6.3 command.

**F-2 `UpdateOrderFromActiveMods` (`:4880-4889`).** This is the single place where rows become order data:

```csharp
	private List<OrderLayoutRow> _layoutPassthrough = [];

	public void UpdateOrderFromActiveMods()
	{
		_updateOrderTask?.Dispose();

		if (SelectedModOrder != null)
		{
			SelectedModOrder.Order.Clear();
			SelectedModOrder.AddRange(ActiveMods.Where(x => x.IsLoadOrderEntry), true);
			SelectedModOrder.Layout = OrderLayoutBuilder.FromRows(ActiveMods, _layoutPassthrough);
		}
	}
```

**F-3 `LoadModOrder` (`:2093-2158`).**
- Top of the method (Q2): reset `ForceAllowInLoadOrder = false` for every `mods.Items` entry with
  `IsForceLoaded && !IsForceLoadedMergedMod`, before the loop at `:2108`.
- Replace `:2141-2142`. Read overrides from `mods.Items`, **not** `ForceLoadedMods`: that collection is bound
  through `ObserveOn(RxApp.MainThreadScheduler)` (`:5245-5246`) and doesn't yet reflect the `ForceAllow`
  flags just set at `:2126-2129` [E]:

```csharp
		var orderedMods = addonMods.Where(x => x.CanAddToLoadOrder && x.IsActive).OrderBy(x => x.Index).ToList();
		var pinned = mods.Items.Where(x => x.IsPinnedOverride);
		var rows = OrderLayoutBuilder.BuildRows(order, orderedMods, pinned, CategoriesEnabled, out _layoutPassthrough);
		foreach (var row in rows)
		{
			if (!row.IsLoadOrderEntry) row.IsActive = true;
		}

		ActiveMods.Clear();
		ActiveMods.AddRange(rows);
		RenumberActiveMods();
```

Then, after the two `OnFilterTextChanged` calls (`:2146-2147`), call `UpdateCategoryRows(ActiveModFilterText)`
(F-9). `CategoriesEnabled` is a const `true` on `feat/categories`. On `feat/overrides-in-order` it is the only
switch, and it still creates the single read-only "Overrides" divider (§13).

**F-4 `AddActiveMod` / `RemoveActiveMod` (`:4841-4870`).** `AddActiveMod` appends (it lands in the last
category) and calls `RenumberActiveMods()` instead of `Index = Count - 1`. `RemoveActiveMod`: when
`mod.IsForceLoaded && !mod.IsForceLoadedMergedMod`, set `ForceAllowInLoadOrder = false`, keep the row,
renumber, and return. A pinned override never leaves the active list.

**F-5 ForceAllow toggle (`src/Core/Util/DivinityGlobalCommands.cs:111-124`).** Route through a new
`IDivinityAppViewModel.OnForceAllowInLoadOrderToggled(DivinityModData)` (`src/Core/ViewModels/IDivinityAppViewModel.cs`):

```csharp
	public void OnForceAllowInLoadOrderToggled(DivinityModData mod)
	{
		if (ActiveMods.Contains(mod))
		{
			mod.IsActive = true;
			RenumberActiveMods();
			UpdateOrderFromActiveMods();
		}
		else if (mod.ForceAllowInLoadOrder)
		{
			AddActiveMod(mod);
		}
		else
		{
			RemoveActiveMod(mod);
		}
	}
```

**F-6 Current order keeps a layout.** `BuildModOrderList` (`:1755-1834`). Look up `lastExported` (`:1796`,
which uses the literal `"LastExported"`; use `DivinityApp.PATH_LAST_EXPORTED_NAME`) *before*
`nextOrders.Add(currentOrder)` (`:1794`), and set `currentOrder.Layout = lastExported?.Layout?.Clone();`.
Anchors are UUIDs, so if the game or another tool rewrote `modsettings.lsx` the categories stay attached to
their first mods.
- `ExportLoadOrderAsync` `:2967`: change `.SetOrder(SelectedModOrder.Order)` to `.SetOrder(SelectedModOrder)`,
  so Current inherits the layout.
- `RefreshAsync` `:2346-2351, 2458-2461`: also snapshot `SelectedModOrder.Layout?.Clone()`. After
  `BuildModOrderList(0, lastOrderName)`, if `lastOrderName` was Current, reapply it and reload. Otherwise
  unsaved layout edits on Current are lost on F5 (R-I).

**F-7 Save As (`:2689-2690`).** Copy `tempOrder.Layout = SelectedModOrder.Layout?.Clone();`.

**F-8 Order equality (`:5203`).** Change to `SelectedModOrder.OrderEquals(ActiveMods.Where(x => x.IsLoadOrderEntry).Select(x => x.UUID))`.
Otherwise any divider makes every order switch reload twice.

**F-9 Filter (`OnFilterTextChanged`, `:4199-4314`).** At the top, `var items = modDataList.Where(x => !x.IsDivider);`,
and use `items` in the three loops (`:4205, :4264, :4289`). At the end, when `modDataList == ActiveMods`,
call `UpdateCategoryRows(searchText)`. This one function is what a future collection filter would use as
its predicate, so Stage 5 isn't boxed in:

```csharp
	/// <summary>Recompute member counts, collapse flags and divider visibility. O(n).</summary>
	public void UpdateCategoryRows(string filterText)
	{
		var isFiltering = !String.IsNullOrWhiteSpace(filterText);
		CategoryDivider current = null;
		var members = 0;
		var visibleMembers = 0;

		void Close()
		{
			if (current == null) return;
			if (current.MemberCount != members) current.MemberCount = members;
			var vis = !isFiltering || visibleMembers > 0 ? Visibility.Visible : Visibility.Collapsed;
			if (current.Visibility != vis) current.Visibility = vis;
		}

		foreach (var row in ActiveMods)
		{
			if (row is CategoryDivider divider)
			{
				Close();
				current = divider;
				members = 0;
				visibleMembers = 0;
				continue;
			}
			members++;
			if (row.Visibility == Visibility.Visible) visibleMembers++;
			//While filtering, matches inside collapsed categories are shown
			var hide = current != null && current.IsCollapsed && !isFiltering;
			if (row.IsCollapsedByCategory != hide) row.IsCollapsedByCategory = hide;
			if (hide && row.IsSelected) row.IsSelected = false;
		}
		Close();
	}
```

**F-10 Counts that must mean "mods".**
- `:2551` `if (ActiveMods.Count == 0)` → `!ActiveMods.Any(x => x.IsLoadOrderEntry)`. Without this, anyone
  with an override installed never gets `CheckForEmptyOrderAsync`, the "reset externally, restore?" prompt
  (`:2290-2333`).
- `:5429` `var activeCount = ActiveMods.Count` → `ActiveMods.Count(x => x.IsLoadOrderEntry)`. Otherwise
  there are false "reset externally" alerts.
- `:5353-5356` screen-reader summary → count and list load-order entries.
- `:5010` `anyActiveObservable` → cosmetic. Leave it, or switch it to an `ActiveModCount` property.

**F-11 Exports that must not see rows.**
- Text/TSV/JSON list export `:3802-3803`: use
  `var exportMods = ActiveMods.Where(x => !x.IsDivider && !x.IsPinnedOverride).ToList(); exportMods.AddRange(ForceLoadedMods.OrderBy(x => x.Name));`.
  Without the filter, pinned overrides appear twice and dividers go into an importable JSON (§4.3).
  Optional: in `.txt` only, emit `## {Name}` lines for dividers.
- Clipboard `:5131-5143`: divider → `## {Name}`; pinned override → `-. {DisplayName} (override)`; others
  unchanged.

**F-12 Imported override (`AddImportedMod`, `:3164-3170`).** After `mods.AddOrUpdate(mod)`, insert it at
`BlockEnd` of the Overrides-role category (or at the end), set `IsActive = true`, renumber.

**F-13 Loader guards (`src/Core/Util/DivinityModDataLoader.cs`).** In `BuildOutputList` `:1446`:
`order.Where(x => !x.Missing && !CategoryDivider.IsDividerUUID(x.UUID))`. In `GenerateModSettingsFile`
`:1478`: `if (!mod.IsDivider && !String.IsNullOrWhiteSpace(mod.UUID))`.

---

## 8. Audit: every consumer of `ActiveMods` and `Order`, with a verdict

| Site | Verdict |
|---|---|
| `Order` consumers, §2.2 rows 1-7, plus `:2350, 2493, 3380, 3516, 3572, 3877, 3943, 4386, 4677` | **No change**: `Order` stays mods-only |
| `ModListDropHandler.cs:116, 150-214, 218-221, 223-234` | F-1 plus §9 rules |
| `ModListDragHandler.cs:70-79` | §9 (also `.ToList()` the lazy `Where`, which today is evaluated at drop time) |
| `HorizontalModLayout.xaml.cs:185-210 SelectMods` (wt-perf `GetListViewFor`) | Route by `IsActive` only. Remove the force-loaded branch (pinned rows have `IsActive = true`) |
| `HorizontalModLayout.xaml.cs:212-241 UpdateIsSelected` (wt-perf reconcile on UUID) | Works as is: divider UUIDs are unique (`category:<guid>`) |
| `HorizontalModLayout.xaml.cs:295-400 MoveSelectedMods` (Enter / Alt+arrow) | Exclude `IsDivider` and `IsPinnedOverride` from the moved set. If nothing is left, `ShowAlert` and return |
| `HorizontalModLayout.xaml.cs:708-786 ListView_Click / Sort` | On the active list, if any row is not a load-order entry, `ShowAlert("Sorting is disabled while the order has categories or overrides", Info, 10)` and return. A view sort would scatter dividers |
| `HorizontalModLayout.xaml.cs:789-860 AutoSize` | Unaffected (iterates `Mods`) |
| `HorizontalModLayout.xaml:127-141`, `.xaml.cs:530-531, 550-571` (Overrides panel) | Keep the control (it is referenced by `DeselectAll`, the drop handler and the delete hotkey `:5056`). Set `Visibility="Collapsed"` and the row height to 0, and delete the `HasForceLoadedMods` bindings. Smallest diff against upstream |
| `MainWindowViewModel.cs:5040-5075` delete hotkey | Safe: `CanDelete` is false for dividers (`FilePath` null, `DivinityModData.cs:526-528`) |
| `AppKeys.cs:72-76` MoveToTop/MoveToBottom | [E] No action is wired anywhere (grep), so nothing to change. If it gets wired later, it must call F-1 |

---

## 9. Drag, drop and selection rules (6.3)

Coordinates are Gong's `UnfilteredInsertIndex` into `ActiveMods`, which includes collapsed rows. That is
why collapse is done with container visibility and never by removing rows. **Indices underneath never
shift.**

- **D1. Dragging a divider moves its whole category**: the divider plus every row up to the next divider,
  collapsed members included. Implement in `Drop` when the target is active:
  `data = _viewModel.ExpandCategoryBlocks(data)`, which preserves `ActiveMods` order.
- **D2. Categories don't nest.** When the moved set contains a divider, snap `insertIndex` (after source
  removal) forward to the next divider index or `Count`. That single rule also covers the uncategorised
  head of the list.
- **D3. Dropping inside a collapsed category** (`insertIndex` strictly between its divider and its block
  end) snaps to the block end, so the mods land at the end of that category.
- **D4. Inactive list as target.** Dividers and pinned overrides are removed from the moved set and stay
  where they are. Block expansion doesn't apply, so only the explicitly selected mods move. If nothing
  remains, set `Effects = None` in `DragOver` and show an alert on drop.
- **D5. After every drop**, call `RenumberActiveMods()` (F-1), `UpdateCategoryRows(...)` (F-9) and
  `UpdateOrderFromActiveMods()` (the existing `:260`). Dropped dividers get `IsSelected = true` along with
  the mods (the reselect loop at `:223-234` walks `Mods`, which doesn't contain dividers).
- **S1. Selection** stays model-plus-`ListView.SelectedItems` as in wt-perf `309b81e`/`396d7b2`. Collapsed and
  filtered rows can't become selected (`IsSelected` guard, §6.1). Ctrl+A therefore selects the visible rows
  and the dividers, and dragging a selected collapsed divider carries its hidden members through D1.

```csharp
	// MainWindowViewModel
	public List<DivinityModData> ExpandCategoryBlocks(List<DivinityModData> dragged)
	{
		if (!dragged.Any(x => x.IsDivider)) return dragged;
		var picked = dragged.ToHashSet();
		var result = new List<DivinityModData>();
		var inPickedBlock = false;
		foreach (var row in ActiveMods)
		{
			if (row.IsDivider) inPickedBlock = picked.Contains(row);
			if (inPickedBlock || picked.Contains(row)) result.Add(row);
		}
		return result;
	}

	public int SnapActiveInsertIndex(int insertIndex, bool movingCategory)
	{
		insertIndex = Math.Clamp(insertIndex, 0, ActiveMods.Count);
		var snap = movingCategory;
		if (!snap)
		{
			for (var i = insertIndex - 1; i >= 0; i--)
			{
				if (ActiveMods[i] is CategoryDivider d)
				{
					//insertIndex > i here, i.e. inside the collapsed block, after its header
					snap = d.IsCollapsed;
					break;
				}
			}
		}
		if (!snap) return insertIndex;
		for (var i = insertIndex; i < ActiveMods.Count; i++)
		{
			if (ActiveMods[i].IsDivider) return i;
		}
		return ActiveMods.Count;
	}
```

[I] Gong doesn't start a drag from a `ButtonBase` (its default drag-source ignore), so the collapse toggle in
the divider template won't start one. Confirm in test M5.

---

## 10. Collapse

- **It's a view concern.** `CategoryDivider.IsCollapsed` drives `IsCollapsedByCategory` on members (F-9),
  and the container style hides them (§10.1). The rows stay in `ActiveMods`, so `Index`, `Order` and
  `modsettings.lsx` are unaffected (INV-1).
- **The filter wins.** While filter text is set, nothing is hidden by collapse, so matches inside collapsed
  categories are visible. A divider with no visible members is hidden while filtering.
- **Persistence.** `Layout.Rows[].Collapsed`, written whenever the order is saved or exported. Current gets
  it through LastExported (F-6). Toggling collapse doesn't write a file: a silent write would also save
  unsaved reorders. Trade-off: a collapse change not followed by a save is lost when the app closes.
  Alternative (Q5): a per-user `HashSet` of collapsed category ids in `settings.json`. Category ids are
  GUIDs, so that would work, but it adds a collection to settings, where there's no precedent for one
  (`DivinityModManagerSettings.InitSubscriptions` only watches property changes,
  `src/Core/Models/DivinityModManagerSettings.cs:180-191`).
- **Virtualization.** Collapsed containers still exist under the `VirtualizingStackPanel`, the same mechanism
  as today's filter. [I] Scrolling past a large collapsed block realizes and measures those zero-height
  containers. That is the same cost Stage 5 measured for filtering. Measure it (test P1). If it hurts, the
  later Stage 5 collection filter fixes both, using the predicate in F-9.

### 10.1 XAML (`src/GUI/Themes/MainResourceDictionary.xaml`)

Add the template before `DivinityModDataListItem` (~L255), and add the triggers at the end of its
`Style.Triggers` (~L380-401). Trigger setters beat the style's `Visibility` binding setter (`~L369`), so the
filter binding is left untouched:

```xml
	<ControlTemplate x:Key="CategoryDividerItemTemplate" TargetType="{x:Type ListViewItem}">
		<Border x:Name="Bd"
		    Padding="4,2"
		    Background="{DynamicResource {x:Static adonisUI:Brushes.Layer2BackgroundBrush}}"
		    BorderBrush="{DynamicResource {x:Static adonisUI:Brushes.AccentBrush}}"
		    BorderThickness="0,0,0,1"
		    SnapsToDevicePixels="True">
			<StackPanel Orientation="Horizontal">
				<ToggleButton Width="16"
				    Height="16"
				    Margin="0,0,6,0"
				    AutomationProperties.Name="Collapse category"
				    IsChecked="{Binding IsCollapsed, Mode=TwoWay}" />
				<TextBlock FontWeight="SemiBold" Text="{Binding Name}" />
				<TextBlock Margin="8,0,0,0" Foreground="{DynamicResource {x:Static adonisUI:Brushes.DisabledForegroundBrush}}" Text="{Binding MemberCount}" />
			</StackPanel>
		</Border>
		<ControlTemplate.Triggers>
			<Trigger Property="IsSelected" Value="True">
				<Setter TargetName="Bd" Property="Background" Value="{DynamicResource {x:Static adonisUI:Brushes.AccentHighlightBrush}}" />
			</Trigger>
		</ControlTemplate.Triggers>
	</ControlTemplate>
```

```xml
			<DataTrigger Binding="{Binding IsDivider, Mode=OneTime}" Value="True">
				<Setter Property="Template" Value="{StaticResource CategoryDividerItemTemplate}" />
				<Setter Property="ContextMenu" Value="{StaticResource CategoryDividerContextMenu}" />
			</DataTrigger>
			<DataTrigger Binding="{Binding IsCollapsedByCategory}" Value="True">
				<Setter Property="Visibility" Value="Collapsed" />
			</DataTrigger>
```

- `CategoryDividerContextMenu` offers Rename (toggles `IsEditingName`, which swaps the name `TextBlock` for
  a `TextBox`), Collapse/Expand, Delete category (members stay and join the previous category) and Insert
  category above. Each new command needs an `AppKeys` `Hotkey` with `[MenuSettings("Edit", …)]`
  (`src/GUI/ViewModels/AppKeys.cs:60-85`) so it shows up in the reflected menu (CLAUDE.md §4).
- [I] With `VirtualizationMode="Recycling"` (`HorizontalModLayout.xaml:119-120`), a container moving
  between mod and divider re-applies its `Template`. With a few dozen dividers in 935 rows this should be
  rare. Measure it with the wt-perf `PerfScrollDriver` (test P1). If template flips show up, switch to an
  `ItemContainerStyleSelector`.
- Pinned overrides keep the mod template. Add a `DataTrigger` on `Index` = −1 in the `#` cell (ModGridView
  ~L845-855) that shows a lock glyph instead of "-1".
- Screen reader: `CategoryDivider.GetHelpText()` provides "Category X, N mods, collapsed". wt-perf's
  `CachedAutomationPeer` path (`ScreenReaderHelperBehavior`) reads `HelpText` (CLAUDE.md §4 views; test M6).

---

## 11. Migration and compatibility story

- **Files need no migration.** Existing orders have no `Layout`. They load as today, plus the auto
  "Overrides" category at the end when any pinned override is installed. They write back byte-identical
  until the user adds a category or there are overrides to place.
- **Older builds and upstream** see `Order` only (§4.3). The one loss is that **re-saving in an older build
  drops `Layout`** (R-A). Write this in the fork's release notes.
- **Optional, `feat/categories` PR B3: "Convert placeholder divider mods to categories…"** [I] This assumes
  you use `=====`-style placeholder paks today (§1 item 11). Candidates are active mods whose `Name`
  matches `^\s*[=\-_~#*]{3,}` (or user-picked). A confirm dialog lists them. Each one becomes a
  `CategoryDivider` named from the stripped text, at the same position, and the placeholder mod moves to
  inactive, so it stops being written. [GAME] This assumes such paks are empty. Test G5 checks that
  removing one changes nothing in-game.
- **Avalonia, ideas only:** per-container colour/icon/border (`wt-avalonia/src/Core/Models/Mod/ModContainerSettings.cs`)
  would make a natural optional `Color` field on a `Category` row later. Avalonia also keeps containers in
  the inactive list (`ModOrderViewModel.cs:1465, 1827`), which is out of scope here. **Don't copy** its
  `loadOrderIndex++` for containers (`ModOrderViewModel.cs:1430-1434`): that gives non-contiguous mod
  indices, which is exactly what the owner ruled out.

### 11.1 Archive and multi-order export (Stages 2, 3, 8)

- **Archive export** serialises `SelectedModOrder` with `JsonConvert` (`:3580-3581`), so `Layout` rides along
  with no change. Import uses `SafeDeserialize<DivinityLoadOrder>` (`:3375, 3511`), so `Layout` arrives
  too. Override rows re-bind on the friend's machine by `Pak`. **Prerequisite: the fix for AUDIT-PLAN-01.**
  Importing an archive exported from "Current" runs `SetFrom` on a whole `DivinityLoadOrder`
  (MWVM:3110-3127). That flips the live Current order to `IsModSettings=false`, or overwrites a saved
  order. The proposed fix copies through `SetOrder(order)`. It must use the **`SetOrder(DivinityLoadOrder)`**
  overload, which with §6.4 carries `Layout`, and not the entries-only `IEnumerable` overload, or the
  categories are lost on import. AUDIT-PLAN claim 9 finds that I1 does not short-read for zip entries, so
  I1 isn't the blocker. AUDIT-PLAN-10 (any `*.json` in a zip becomes an order) is unaffected by `Layout`.
  `Layout` adds roughly 150 B per row, which is negligible next to roughly 85 KB for 935 entries.
- **Override paks in the zip:** keep the current rule of every installed non-merged override
  (`:3573`). They are global [GAME], so per-order placement is cosmetic.
- **Stage 8 multi-order:** each order JSON carries its own `Layout`, so there's nothing to merge. Build the
  pak union from ∪`Order` UUIDs plus overrides, **deduplicating overrides by pak file name and never by UUID**
  (a no-meta UUID is a local path). Category ids are per-order GUIDs, so two orders' "Cosmetics" never
  collide. The Stage 2 refactor to `(IEnumerable<DivinityLoadOrder>, HashSet<DivinityModData>)` should take
  the layout's override rows as an input to the pak set.

---

## 12. Risks

| # | Risk | Mitigation |
|---|---|---|
| R-A | An older or upstream build re-saves and silently drops `Layout` | Release note. Optional later: a fork-side cache `Data/OrderLayouts/<orderfile>.layout` (not `.json`, §3 option B) restored when a known order comes back without `Layout` |
| R-B | A divider leaks into a mod-only path (exports, counts, profile list) | INV-3, the §8 audit table, the two predicates, the F-13 guards, the INV-1 diff test |
| R-C | A pinned override is written by mistake | It can't be in `Order` (F-2). `LoadModOrder` never sees override rows in `Order`. INV-2 diff test |
| R-D | Collapsed blocks cost scroll time (zero-height containers) | P1. The Stage 5 collection filter reuses F-9 |
| R-E | A container template flip on recycle | P1. Fall back to an `ItemContainerStyleSelector` |
| R-F | Gong's insert index lands among hidden rows | D3 snapping. M3 test |
| R-G | A no-meta override's local path leaks into shared JSON | `ToOverrideRow` never writes it (§6.5). U4 test |
| R-H | Header sort scatters dividers | Disabled on the active list with layout rows (§8) |
| R-I | Unsaved layout edits on Current lost on F5 | F-6 refresh snapshot |
| R-J | `ForceAllow` per-order reset changes behaviour for existing ForceAllow users | Q2. Release note. G3 test |
| R-K | A 6.2-only build deletes 6.3 categories | Unknown-kind passthrough plus `JsonExtensionData`, shipped in PR A1 |
| R-L | `Entries` key collision with Avalonia | Reserved and never emitted (§4.2) |

---

## 13. Branches and PRs

`perf/list-render` (6.1, upstreamable) merges into `fork/main` first. `CachedTextBlock` and the selection
fixes are already in the row template and code-behind that this design edits
(`wt-perf … MainResourceDictionary.xaml:719`, `HorizontalModLayout.xaml.cs` `GetListViewFor`).

**`feat/overrides-in-order`** (from `fork/main`):
- **A1. Core format.** `OrderLayout`/`OrderLayoutRow` with *both* kinds and passthrough,
  `DivinityLoadOrder.Layout` and `SetOrder`/`Clone`, `OrderLayoutBuilder`, `CategoryDivider` (model only),
  the `DivinityModData` predicates and the `CanAllowInLoadOrderCheck` tweak, and the F-13 guards. No UI
  change.
- **A2. Fold-in.** F-1…F-8, F-10…F-12, the read-only "Overrides" divider row template, the hidden
  Overrides panel, `GetListViewFor` routing, D4 for overrides, and the `ForceAllow` toggle (F-5).
  Acceptance: INV-2 holds, and G1-G3 pass.

**`feat/categories`** (on top of `feat/overrides-in-order`):
- **B1. Dividers as user content.** Insert/rename/delete commands plus `AppKeys` entries, and the context
  menu.
- **B2. Collapse and drag.** `IsCollapsedByCategory`, F-9, D1-D3, header sort disabled, collapse
  persistence.
- **B3. Optional.** Placeholder-mod conversion (§11).

**Separate upstreamable `fix/*` from `master`, only if wanted:** the no-path-UUID guard (§5.3 item 5), and the
`ModListDropHandler.cs:218-221` O(n²) renumber, which also matters on upstream at 935 rows.

---

## 14. Test plan

None of this can be built or run in this container, so everything below is on Windows. There is no test
project in the solution (`BG3ModManager.sln`, three csprojs). U-tests need a small fork-only
`tests/Core.Tests` (xunit, `net8.0-windows`) or a throwaway console harness.

**Unit (Core, pure)**
- **U1.** Round-trip: deserialise the §4.1 example, serialise it, and check semantic equality, including an
  unknown row `{"Kind":"Future","X":1}` and an unknown field on a known row (proves `JsonExtensionData`
  works on `[DataContract]`).
- **U2.** An order without `Layout` serialises byte-identically to `master`'s `ExportLoadOrderToFile` output.
- **U3.** `BuildRows` → `FromRows` is the identity for: an empty layout; a category at the top; at the end;
  two adjacent categories (empty category); a `Before` anchor missing with an `At` fallback; an anchor
  before a `Missing` entry; overrides by `UUID`, by `Pak`, and unresolved; a duplicated Overrides role.
- **U4.** A no-meta override row has no `UUID` (no path in the output).
- **U5.** `FromRows` returns an `Order` sequence equal to the input mods with dividers and pinned rows removed
  (the INV-1 core).

**Manual (app)**
- **M1.** Open an order from `master` and save it unchanged: the file diff is empty (except the auto
  "Overrides" layout when overrides are installed).
- **M2.** Drag across dividers: `#` stays 0…n−1 contiguous over written rows, and pinned overrides show the
  lock.
- **M3.** Drop on and below a collapsed divider: the mods land at the end of that category. Drag a
  collapsed divider: the block moves with its hidden members and the count is unchanged.
- **M4.** Filter "hair" with Cosmetics collapsed: Better Hair is visible, and dividers with no matches are
  hidden. Clear the filter: Cosmetics is collapsed again.
- **M5.** Click the collapse toggle: it doesn't start a drag. Ctrl+A then Enter moves only mods, and the
  dividers and overrides stay.
- **M6.** With a screen reader, a divider reads "Category …, N mods, collapsed".
- **M7.** Archive round-trip: export a categorised order, then import it into a clean profile. Categories,
  collapse state and override placement return. Run it with both a saved order and "Current" as the source;
  the Current case needs the AUDIT-PLAN-01 fix.
- **M8.** Older build: open the same JSON in an upstream master release build. It loads with no
  missing-mods popup, the overrides aren't promoted, the exported `modsettings.lsx` equals the fork's
  (INV-1), and re-saving drops `Layout` (R-A, expected).
- **P1.** Perf, with the wt-perf harness (`PerfScrollDriver`, `PerfProbe`): the 935-row list with 15
  categories, 3 of them collapsed (≈200 hidden rows), against 6.1 numbers for thumb-drag frame time and
  stutter count. Accept a regression of 10% or less (Q4).

**In-game [GAME]**
- **G1. INV-1 by boot.** Export the real 935-mod order with categories and folded overrides. Diff the
  `modsettings.lsx` against the export of the same order with all dividers deleted: it must be identical.
  Boot BG3. The in-game mod list shows the same mods in the same order, with no entry named after a
  category. Load a save and confirm no "missing mods" prompt.
- **G2. Overrides are global.** Take a visibly-acting no-meta override (a camera or UI pak), pin it in the
  middle of a category, and export. It isn't in `modsettings.lsx`, and in game its effect is present. Move
  it and re-export: nothing changes in game. This confirms "always loaded, position is cosmetic".
- **G3. ForceAllow.** Toggle ForceAllow on a meta override (kind 2). It appears in `modsettings.lsx` at its
  row position (check `#`). The game boots and accepts it. Switch to an order without it: it reverts to
  pinned (Q2), and the exported file doesn't contain it.
- **G4. Layout-only orders.** An order whose rows are only the Overrides category exports a
  `modsettings.lsx` with just the adventure. The game boots.
- **G5. (B3 only)** A placeholder divider mod converted to a category: the game boots with no change in
  behaviour.

---

## 15. Open questions for the owner

- **Q1.** Should `#` count only written mods (proposed) or also pinned overrides (as the mock-up shows)?
- **Q2.** Should `ForceAllowInLoadOrder` be per-order (proposed, matching how it is already persisted), or
  stay sticky across order switches as today?
- **Q3.** Should dragging a pinned override to the inactive list be blocked with an alert (proposed), or
  quietly ignored?
- **Q4.** What perf budget is acceptable for collapse (proposed: 10% or less over 6.1 frame times)?
- **Q5.** Should collapse persist in the order JSON (proposed), or per-user in settings?
- **Q6.** Do you want the B3 placeholder-mod conversion? (Can you confirm your `=====` entries are empty
  paks?)
- **Q7.** Upstream's Avalonia rewrite drops the force-loaded exclusion entirely
  (`CanAddToLoadOrder = modType != "Adventure" && !isHidden`, per AVA.md row 6.2), so it presumably
  *writes* overrides to `modsettings.lsx`. [I] This is inferred from AVA.md, and I didn't trace its
  exporter. This design keeps today's rule instead: pinned overrides are not written. Confirm that is
  what you want. If you ever want the Avalonia behaviour, G2/G3 are the tests that would show whether the
  game cares.
