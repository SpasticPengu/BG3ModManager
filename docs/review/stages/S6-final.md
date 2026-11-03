# S6-final: Stage 6.2 (overrides in the order) and 6.3 (categories/dividers), judged and synthesized

Judge/synthesis pass, 2026-11-03. Inputs: `docs/review/stages/alternatives/S6-A-data-model.md`, `docs/review/stages/alternatives/S6-B-list-ux.md` and
`docs/review/stages/alternatives/S6-C-game-risk.md` (all three present). Also the plan's Stage 6 and R1-R8, CLAUDE.md §4-§6, and the
reports that existed: `AUDIT-PLAN.md`, `AUDIT-CLAUDE.md`, `AVA.md`, `BLD.md`. **`VM2.md` and `S6.md` had not
landed when I started, and still hadn't when I re-checked the folder before finishing.** Re-check §11 (drag/drop) against VM2
and §8 (rendering/selection) against S6 when they arrive. I didn't build or run anything, and I didn't touch the repo.

Labels: **[E]** means I re-opened the cited `path:line` (master `fc35358` unless prefixed `wt-perf:`/`wt-ava:`).
**[I]** is an inference. **[GAME]** is a claim about BG3 itself that can't be checked here. Each one names its test in §15.

---

## Scores (1 = poor, 5 = best; "Effort" 5 = least effort / least risk to land)

| Design | Game safety | Compat (old builds, upstream, saved orders, archives) | Virtualization / perf | UX | Effort | Fit with Stages 5 & 8 | Total |
|---|---|---|---|---|---|---|---|
| **S6-A** data model (`Layout` sidecar member, divider = `DivinityModData` subclass in `ActiveMods`) | 4 | **5** | 4 | 4 | 2 | 4 | 23 |
| **S6-B** list UX (`ActiveRows` + derived `ActiveMods`, `Dividers` array, style selector) | 4 | 4 | 4 | **5** | 3 | 3 | 23 |
| **S6-C** game risk (write guard + read-back, B's row model, minimal 6.2) | **5** | 4 | 4 | 3 | **4** | 4 | 24 |
| **Final (this doc)** | 5 | 5 | 4 (gated on P1) | 4-5 | 3 | 5 | — |

Why the scores came out this way:
- **A** has the best file format: a versioned envelope, passthrough of unknown kinds, `JsonExtensionData`, a reserved `Entries` key,
  and no path UUIDs in shared JSON. But it puts dividers and pinned overrides inside `ActiveMods`, so every one of
  its ~70 consumers (42 in MWVM alone, by grep) has to be audited now, and every future upstream merge again. Its own
  `CanAllowInLoadOrderCheck` tweak (§6.1) shows how easy that is to get wrong: unpatched, a divider computes
  `CanAddToLoadOrder = true` (DivinityModData.cs:381-384 [E]). Per-override `Pak` placement is also a lot of
  machinery for a position that has no effect in game [GAME G3/G4].
- **B** has the best interaction detail: keyboard, F2, Delete, screen reader, the `ItemContainerStyle`-beats-selector
  gotcha, and the stale local automation Name on recycled containers. Its model (`ActiveMods` stays mods-only) is the
  safer one. It loses points because collapse reuses the filter-owned `Visibility` stamp, which couples it to Stage 5
  internals. It also has an unversioned top-level `Dividers` array, and drag-out of K2 changes modsettings.lsx through a drag gesture.
- **C** adds the only upstreamable safety layer (write guard, read-back, K1 entry guard), the failure catalogue and a
  proper in-game protocol. Its 6.2 is deliberately minimal, and its UX is the thinnest: dragging an expanded divider
  moves only the header, which silently re-homes members.

**The final design takes:** B/C's row model and rendering, A's file-format discipline, A's separate collapse flag,
C's write guard, invariants and test protocol, and B's interaction details.

---

## 1. Your intentions as I read them (you asked for this list)

1. `master` mirrors upstream. Upstreamable `fix/*` and `perf/*` branches are cut from `master` and go first. 6.1 (render perf) lands before 6.2/6.3.
   6.2 and 6.3 are fork-only (`feat/overrides-in-order` → `feat/categories` on `fork/main`).
2. Categories are flat rows in one collection, never WPF grouping. Virtualization must survive at 935 active rows,
   on top of 6.1's `CachedTextBlock` and the selection fixes.
3. The game is the authority. modsettings.lsx must always be accepted, which is proven by exporting and booting the game. The game
   never sees a divider (R7).
4. Overrides become "a normal category" in the active list (mock-up: "▶ Overrides 2 collapsed"), so the separate pane goes away.
5. Collapse is view-only. Indices underneath never shift. Drag across dividers keeps `Index` contiguous.
6. Orders are shared and published (zip export/import, Stage 8 multi-order, Stage 9 sync), so the order JSON is a
   public contract. Older builds and upstream must not be confused by it.
7. Don't box in Stage 5: per-item `Visibility` stamping now, collection filtering possibly later.
8. Evidence over argument: cite, measure, label inferences, don't re-argue disproved hypotheses.
9. Redux/Avalonia supply ideas only, no code. Logic goes in Core/view models, not code-behind (keeps a future port cheap).
10. Visible failures go through `ShowAlert`. House style: no `string?`, tabs/CRLF/BOM/Allman/`var`, unprefixed types, settings mutated in place.
11. You already use `=====` placeholder "divider" mods in your real order (plan Stage 0 notes). Native categories
    should offer a way off them, but not by surprise.
12. Accessibility matters: "still reads mod names" is on your verify list.

---

## 2. Spot-check of the code claims the designs rely on

### 2.1 Verified (load-bearing for the final design)

| Claim | Evidence |
|---|---|
| Order JSON is `[DataContract]` with only `Order` as a `[DataMember]`. `Name`, `FilePath` and `Missing` are not serialised | DivinityLoadOrder.cs:7-15, 23-47 [E] |
| `SetOrder(IEnumerable)` / `SetOrder(DivinityLoadOrder)` / `Clone()` copy only `Order` | DivinityLoadOrder.cs:283-312 [E] |
| Deserialisation settings set only an `Error` handler. So by Newtonsoft's default `MissingMemberHandling.Ignore` [library default, I], an unknown top-level member is ignored on read and dropped on re-save | DivinityJsonUtils.cs:12-19, 59-73 [E] |
| `LoadModOrder` sets `ForceAllowInLoadOrder = true` for **any** `IsForceLoaded` mod found in `Order`, K1 included | MWVM:2125-2129 [E] |
| `BuildOutputList` writes the campaign first, then every resolvable non-missing `Order` entry. It doesn't check `CanAddToLoadOrder` | DMDL:1431-1468 [E] |
| `GenerateModSettingsFile` skips only blank UUIDs. The UUID attribute is `type="guid"` | DMDL:1471-1487; DivinityApp.cs:33 [E] |
| A meta-less override's UUID is its full pak path, and its Folder is a builtin folder | DMDL:612-621 [E] (A cites :625, C cites :620. :620 is correct) |
| LSLib parses a `guid` attribute with `new Guid(str)`, which throws on a path. `LoadModSettingsFileAsync` catches everything and returns an empty list | NodeAttribute.cs:440-447; DMDL:838-845 [E]. C's `LSXReader.cs:169` cite didn't resolve at that path. The throw itself is verified |
| Text export "JSON" serialises `ActiveMods` **plus `ForceLoadedMods`** | MWVM:3802-3803 [E] |
| An array JSON falls through to the `List<DivinitySerializedModData>` branch on "Load order from file" | DMDL:1215-1236 [E]. Whether deserialising an array as `DivinityLoadOrder` returns null under the handled error is [I] (C's T-B1 settles it) |
| An unknown `Order` entry becomes a missing-mod popup, is deleted by Clear Missing, and is stripped by Save As | MWVM:2724-2746, 4677, 2689-2690 [E] |
| The zip export takes paks from `Order` plus non-merged `ForceLoadedMods` and serialises `SelectedModOrder` whole | MWVM:3572-3581 [E] |
| Export of order X copies **only `Order`** into Current | MWVM:2967 [E] |
| LastExported is one file, `Orders/LastExported.json`, **not per profile**, written with `SetOrder(DivinityLoadOrder)` | MWVM:2850-2873 [E] |
| The Orders dir is scanned recursively for `*.json` (only `settings.json` is excluded) | DMDL:1147-1159 [E] |
| Reset detectors count `ActiveMods` | MWVM:2551, 5429 [E] |
| `ActiveMods` CollectionChanged (Add/Remove/Reset) clears `HasExported` and schedules `UpdateOrderFromActiveMods` in 250 ms | MWVM:5400-5407 [E] |
| Direct `ActiveMods` mutation sites | MWVM:2141-2142, 3183, 3198, 4392, 4845, 4856 (grep) [E] |
| The drop handler renumbers by `IndexOf` (O(n²)). Drag data is a lazy `Where` over `IsSelected && Visible` | ModListDropHandler.cs:218-221; ModListDragHandler.cs:70-79 [E] |
| Alt+Up/Down needs `ItemsSource is ObservableCollectionExtended<DivinityModData>`, uses `list.Move`, and never writes `Index` | ModListView.cs:228-300 [E] |
| `ItemContainerStyle` is set at HorizontalModLayout.xaml:116 (active), :138 (pane), :220 (inactive), in code at .xaml.cs:109, and in the style setter at MainResourceDictionary.xaml:**443** (B/C say 444) | [E] |
| Mod rows **don't** bind `IsSelected` (commented out). Selection is synced in code | MainResourceDictionary.xaml:368 [E] |
| `ScreenReaderHelperBehavior` sets Name/HelpText as **local** values and only when non-empty, so a recycled container can keep a stale name | ScreenReaderHelperBehavior.cs:100-128 [E] |
| wt-perf reconciles selection over a `DivinityModData` list from `SelectedItems.OfType<DivinityModData>()`. Its fallback uses `Cast<DivinityModData>()` | wt-perf:HorizontalModLayout.xaml.cs:213-255 [E] |
| `Index` is displayed raw (0-based). The mock-up's 1-based numbering is cosmetic | MainResourceDictionary.xaml:852 [E] |
| The IsGrouping landmine | MainResourceDictionary.xaml:460-461 [E]. AUDIT-PLAN 17: `CanContentScroll=True` comes only from AdonisUI's style |
| Avalonia uses the top-level `Entries` key and migrates legacy `Order` via a private `[JsonExtensionData] Extras` | wt-ava:src/Core/Models/Mod/Order/ModOrder.cs:23, 321-333 [E]. Whether STJ honours a *private* extension-data property is [I] |
| Core globally imports `System.Text.Json.Serialization`, so an unqualified `[JsonExtensionData]` would bind to the STJ attribute, which Newtonsoft ignores | src/Core/Usings.cs [E] (A's catch) |

### 2.2 Rejected or corrected

| Design | Claim | Verdict |
|---|---|---|
| B §2, §4.3 | "`Order` is re-sorted by `mod.Index` 25 ms after any Index change (MWVM:5256-5259)" | **Doubtful [I].** The subscription is `ActiveMods.WhenAnyPropertyChanged(nameof(DivinityModData.Index))`. That observes the *collection's* own PropertyChanged, which never raises "Index". Treat it as inert. `Order` follows `ActiveMods` through the 250 ms CollectionChanged rebuild. Nothing in the final design depends on the sort |
| C §8.5 | The divider style binds `IsSelected` TwoWay | **Rejected.** Mod rows deliberately don't bind it (above), and a TwoWay binding on recycled containers fights the SelectedItems reconcile. Dividers join the reconcile instead (§8.3) |
| B §9.2 D7 | "Silently drop dividers from mixed data" when dropping on the inactive list | **Incomplete [I].** `ModListDropHandler.DragOver` calls `base.DragOver` (ModListDropHandler.cs:88). Gong's `DefaultDropHandler` type-compat check would refuse the *whole* mixed drop, because `CategoryDivider` isn't a `DivinityModData`. Override `CanAcceptData`/`DragOver` explicitly |
| A §7 F-6 | Current inherits its layout from LastExported | **Partly rejected.** LastExported is a single file for all profiles (MWVM:2851 [E]), so profile 2's Current would get profile 1's categories through `At` fallbacks. Use a profile-keyed store, with LastExported only as a fallback when its `Order` equals Current's (§6.4) |
| A §5.1, §6.5 | Persist each pinned override's position by `Pak` | **Rejected for v1.** The game ignores it [GAME G3/G4]. It adds pak-name matching, a second override taxonomy in JSON, and per-order positions for a global thing |
| AUDIT-PLAN line 236 | Dividers inline in `Order` with a non-GUID scheme, skipped at each consumer | **Rejected.** All three designs and §2.1 show every existing reader misbehaves on them (popup, deletion, Save As strip) |
| A §9 | "Gong doesn't start a drag from a `ButtonBase`" | Unverified [I]. Set `dd:DragDrop.DragSourceIgnore="True"` on the chevron regardless (B) |
| C §2.4 | Master can write a pak path as a GUID | Steps 1, 3 and 4 verified. Step 2 is [I]. A hand-edited, friend's or Avalonia-written order with a K1 UUID reaches step 3 directly, so the guard is justified either way |

---

## 3. Claims about the game (need in-game tests, §15)

| ID | Claim | Who relies on it | Test |
|---|---|---|---|
| G1 | The order of `Mods/ModuleShortDesc` children *is* the load order | all | T-G1 |
| G2 | The campaign entry must be present (and first) or the main menu fails (README.md:23) | all | T-G1 |
| G3 | Every pak in Mods is mounted. Builtin-folder overrides (K1/K2) apply whether or not they're listed | 6.2 semantics, "no index for overrides" | T-G3 |
| G4 | Precedence between two overrides of the same file is not controlled by modsettings.lsx | Overrides block sorted by name, not draggable | T-G4 |
| G5 | An unresolvable entry (unknown GUID, path UUID) makes the game drop it **or** clear the whole order (README.md:24) | Severity of any leak, so the write guard | T-G5a/b |
| G6 | Subfolders in Mods reset modsettings.lsx (README.md:21) | Constraint: never implement categories as folders | — |
| G7 | The game rewrites modsettings.lsx on launch. The bytes change, but the UUID sequence should not | Comparison method in the protocol | T-G1 step 7 |
| G8 | A save made with mod X active warns when X is no longer active | Placeholder-pak conversion | T-M2 |
| G9 | Placeholder `=====` paks are effectively empty and removing them changes nothing in game | Placeholder conversion | T-M2 |
| G10 | A K2 written by "Allow in load order" is accepted at its row position | 6.2 keeps the toggle | T-G2 |

---

## 4. Final design: invariants

- **INV-1 (byte identity).** `GenerateModSettingsFile(BuildOutputList(Order, …))` depends only on `Order`, the mod set,
  the campaign and the deps setting. Dividers, collapse, filter text and the Overrides block cannot change a byte. For
  an order with no K1 entries, the output is byte-identical to `fc35358`'s.
- **INV-2.** `DivinityLoadOrder.Order` holds only mod entries. `ActiveMods` holds only *exportable* `DivinityModData`:
  K0 ordinary, K3 merged, and K2 with `ForceAllowInLoadOrder`. That is upstream's existing meaning. **Every existing
  reader of `ActiveMods`/`Order` stays correct without edits.**
- **INV-3.** No `ModuleShortDesc` is written unless `Guid.TryParse(UUID)` succeeds and the mod is not K1. Anything skipped gets a `ShowAlert`.
- **INV-4.** The campaign stays first (unchanged code path).
- **INV-5.** Nothing is created inside the game's Mods folder (G6).
- **INV-6.** Every modsettings.lsx write is read back with the manager's own LSLib settings, and the UUID sequence must match.
- **INV-7.** `Index` = 0..n-1 contiguous over `ActiveMods` in row order, and −1 on every other row. One function assigns it.

---

## 5. Model (Core)

### 5.1 Row types

- **`CategoryDivider : ReactiveObject, ISelectable`** (new, `src/Core/Models/CategoryDivider.cs`). **Not** a `DivinityModData`
  subclass (B/C). The compiler then points at every site that must handle it, and the ~70 `ActiveMods` consumers never see one.
  Members: `Id`, `Kind` (`User` | `Overrides`), `[Reactive] Name`, `IsCollapsed`, `IsEditing`, `MemberCount`,
  `VisibleMemberCount`, `Visibility`, `Color` (optional, unused in v1). It also has `IsSelected` with the same "refuse true while
  hidden" guard as DivinityModData.cs:185-196, `CanDrag => Kind == User` (v1), an `AutomationName` OAPH ("Category X, N mods,
  collapsed"), and `ToLayoutRow()`. A single type with a `Kind` covers both the user dividers and the Overrides header (B). C's separate
  `OverridesHeaderRow` isn't needed.
- **`DivinityModData`** gains one Core property: `[Reactive] public bool IsCollapsedByCategory { get; set; }` (A). The
  `IsSelected` guard becomes `value && (Visibility != Visibility.Visible || IsCollapsedByCategory)`. **Collapse doesn't touch
  `Visibility`, so Stage 5 keeps sole ownership of it.**

### 5.2 Persisted shape (A's envelope, trimmed)

`src/Core/Models/OrderLayout.cs`:

```csharp
[DataContract]
public class OrderLayoutRow
{
	public const string KIND_CATEGORY = "Category";
	public const string ROLE_OVERRIDES = "Overrides";

	[DataMember] public string Kind { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Id { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Name { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Role { get; set; }
	/// <summary>UUID of the first Order entry after this row; null = end of the order.</summary>
	[DataMember(EmitDefaultValue = false)] public string Before { get; set; }
	/// <summary>Number of Order entries before this row at save time; fallback when Before no longer resolves.</summary>
	[DataMember] public int Position { get; set; }
	[DataMember(EmitDefaultValue = false)] public bool Collapsed { get; set; }

	// Fully qualified: Core globally imports System.Text.Json.Serialization (src/Core/Usings.cs).
	[Newtonsoft.Json.JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
}

[DataContract]
public class OrderLayout
{
	public const int CURRENT_VERSION = 1;
	[DataMember] public int Version { get; set; } = CURRENT_VERSION;
	[DataMember] public List<OrderLayoutRow> Rows { get; set; } = [];
	[Newtonsoft.Json.JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
}
```

`DivinityLoadOrder` gains `[DataMember(EmitDefaultValue = false)] public OrderLayout Layout { get; set; }`. It stays
**null** whenever there is nothing to persist, which gives byte-identical JSON for every existing order. `SetOrder(DivinityLoadOrder)` and
`Clone()` deep-copy it. `SetOrder(IEnumerable<DivinityLoadOrderEntry>)` leaves it alone (doc comment says so).

Example (the plan's mock-up, with Overrides moved between categories and collapsed. The Role row only exists once the user has moved or
collapsed Overrides):

```json
{
  "Order": [
    { "UUID": "0b6b9c5e-…", "Name": "Combat Extender" },
    { "UUID": "7c1d2e3f-…", "Name": "Party Limit Begone" },
    { "UUID": "a1b2c3d4-…", "Name": "Tactician Enhanced" },
    { "UUID": "d4e5f6a7-…", "Name": "Better Hair" }
  ],
  "Layout": {
    "Version": 1,
    "Rows": [
      { "Kind": "Category", "Id": "6f0c2a8e-…", "Name": "Gameplay Overhauls", "Before": "0b6b9c5e-…", "Position": 0 },
      { "Kind": "Category", "Id": "overrides", "Role": "Overrides", "Name": "Overrides", "Before": "d4e5f6a7-…", "Position": 3, "Collapsed": true },
      { "Kind": "Category", "Id": "9e1d3b7c-…", "Name": "Cosmetics", "Before": "d4e5f6a7-…", "Position": 3 }
    ]
  }
}
```

Format rules:
- **Anchor.** A row sits immediately before the `Order` entry whose UUID is `Before`. Rows sharing an anchor keep their
  `Rows` order. If `Before` doesn't resolve, use `Clamp(Position, 0, Order.Count)`, kept non-decreasing through `Rows` so categories can't
  cross. Anchors are computed over `Order` **including `Missing` entries**, so a row anchored before a missing mod lands before the next present one.
- **No override membership is persisted.** The Overrides block's members are always computed from the installed
  `ForceLoadedMods` (MWVM:5244-5246). That means a pak path UUID can never get into shared JSON (A's R-G, S8 P1).
- **Passthrough.** Rows of unknown `Kind`, and unknown fields (`Extra`), are kept and written back at their anchors. This ships in the
  **first** PR that reads `Layout` (A's R-K).
- **Reserved.** Never emit a top-level `Entries` key, because Avalonia would read it as its own format (wt-ava ModOrder.cs:23).
- **Version.** Readers accept `Version <= CURRENT_VERSION` fully. For a higher version they still apply known
  `Category` rows and pass the rest through, so it degrades gracefully.

Why a `Layout` object rather than B/C's flat `Dividers`: the cost is the same, but there is one top-level key to reserve, a version
number, and room for Stage 8/9 additions (colour, notes) without adding more top-level members that older builds would drop one by one.
Why not A's `Override` rows: see §2.2.

### 5.3 Pure layout functions (`src/Core/Util/OrderRowLayout.cs`, static, unit-testable)

- `BuildRows(order, orderedActiveMods, overrides, createDivider)` → `List<ISelectable>`: merges anchored `Category`
  rows into the mod sequence in one pass (A's merge, with B's `HashSet` instead of `Contains`). It then places the Overrides
  header and its members, sorted by `Name`, at the Role row's anchor, or **at the end** if there is no Role row. It returns the
  passthrough rows through an out parameter.
- `Project(rows, activeModsOut, out OrderLayout layout, passthrough)`: **the only writer of `ActiveMods` content and
  `Index` for the active side** (C's `Project` with A's `FromRows`):

```csharp
public static OrderLayout Project(IList<ISelectable> rows, List<DivinityModData> modsOut, IList<OrderLayoutRow> passthrough)
{
	modsOut.Clear();
	var result = new List<OrderLayoutRow>();
	var pending = new List<OrderLayoutRow>();
	var index = 0;
	foreach (var row in rows)
	{
		if (row is CategoryDivider divider)
		{
			pending.Add(divider.ToLayoutRow());
		}
		else if (row is DivinityModData mod)
		{
			if (mod.CanAddToLoadOrder)
			{
				foreach (var p in pending) { p.Before = mod.UUID; p.Position = index; }
				result.AddRange(pending);
				pending.Clear();
				if (mod.Index != index) mod.Index = index;
				modsOut.Add(mod);
				index++;
			}
			else if (mod.Index != -1)
			{
				mod.Index = -1;	//Overrides-block members
			}
		}
	}
	foreach (var p in pending) { p.Before = null; p.Position = index; }
	result.AddRange(pending);
	//passthrough rows re-anchored by Before if it still resolves, else clamped Position
	...
	return HasPersistableRows(result, passthrough) ? new OrderLayout { Rows = result } : null;
}
```

`HasPersistableRows` is false when the only row is a default Overrides header: not moved (still at the end), not collapsed,
and not renamed. That keeps untouched orders at `Layout == null` (INV-1 for JSON).

---

## 6. modsettings.lsx rules

1. **The writer's input is unchanged.** `BuildOutputList(SelectedModOrder.Order, mods.Items, …)` (MWVM:2931) and the
   LastExported restore (MWVM:2326). `Order` is produced only by `UpdateOrderFromActiveMods` from the derived `ActiveMods`
   (INV-2), so dividers and Overrides-block members can't get in structurally.
2. **PR-0 write guard (upstreamable, C §6).** It adds `ModSettingsWriteGuard.CanWrite`: skip a blank UUID, skip K1 (`IsForceLoaded &&
   !IsForceLoadedMergedMod && !HasMetadata`), and skip a UUID that fails `Guid.TryParse`. Every skip is collected and surfaced with
   `ShowAlert(Warning)` naming the mods. `ExportModSettingsToFileAsync` then reads the file back (INV-6). A mismatch is
   `AlertType.Danger`, the export counts as failed, and `HasExported` stays false. It also changes
   `LoadModOrder` to force-allow only `IsForceLoaded && HasMetadata` (closes the K1 entry path at MWVM:2126-2129).
   For valid orders the bytes are identical.
3. **Fork defence in depth (in `feat/overrides-in-order`).** `BuildOutputList` also skips `IsForceLoaded && !IsForceLoadedMergedMod
   && !ForceAllowInLoadOrder` (B §5.2) and logs it. No current path reaches it.
4. **The campaign is first** (unchanged, DMDL:1436-1445).
5. **The game never receives** divider names/ids, the Overrides header, K1 UUIDs, `Index` values or collapse state.
6. **Behaviour change accepted (A's Q2): `ForceAllowInLoadOrder` becomes per-order.** At the top of `LoadModOrder`, reset it
   to false for every `IsForceLoaded && !IsForceLoadedMergedMod` mod. The existing loop then re-enables it for K2 mods present in
   `Order`. This matches how it's already persisted ("being in `Order`" is its only storage, [E] grep: it is set only at MWVM:2128 and
   in DivinityGlobalCommands.cs:115). Without the reset, an allowed K2 would show in the Overrides block in one order and
   in the inactive list in another, depending on history. Write it in the release notes.

---

## 7. View-model wiring (GUI)

- **`ActiveOrderRows`** (new, `src/GUI/ViewModels/ActiveOrderRows.cs`) owns `Items` (`ObservableCollectionExtended<ISelectable>`),
  the passthrough rows, the per-divider collapse subscriptions and `CommitStructureChange()`. `MainWindowViewModel.ActiveRows`
  exposes it. The active `ListView.ItemsSource` becomes `ActiveRows.Items` (HorizontalModLayout.xaml.cs:526).
- **`ActiveMods` stays, keeping its type and meaning, and is derived.** `CommitStructureChange()` runs `Project` and then `_activeMods.Load(mods)` **only if
  the sequence changed** (a collapse or a divider rename never resets it, so `HasExported` isn't cleared at MWVM:5402). After that
  it calls `UpdateCategoryRows()`. **Divider-only edits must call `UpdateOrderFromActiveMods()` explicitly**, because `ActiveMods` didn't
  change, so the 250 ms hook at MWVM:5400-5407 won't fire. `UpdateOrderFromActiveMods` gains
  `SelectedModOrder.Layout = ActiveRows.Layout;`.
- **Reroute the six direct mutation sites** through `ActiveRows`: MWVM:2141-2142 (`LoadModOrder` → `ActiveRows.Load`), 3183
  and 3198 (import replace), 4392 (delete), 4845 (`AddActiveMod`: insert at the end of the last user category, i.e. before
  the Overrides header when it's pinned last), and 4856 (`RemoveActiveMod`). Add a grep gate in review: no
  `ActiveMods.(Add|Remove|Insert|Move|Clear|Replace|RemoveMany|AddRange|Load)` outside `ActiveOrderRows`. Add a debug-only assert
  in `CommitStructureChange` that `ActiveMods` equals `Items` filtered to exportable mods.
- **Overrides block contents.** Subscribe to the existing `_forceLoadedMods` binding (MWVM:5244-5246). On change, rebuild
  only the block range. **Keep `ForceLoadedMods`**: the zip export (MWVM:3573) and the text export (MWVM:3803) need it (C F9; avoid
  Avalonia's lost-overrides bug, AVA.md:232). Overrides-block members keep `IsActive = false` and `Index = -1`.
- **K2 toggle** (DivinityGlobalCommands.cs:111-124) is unchanged in behaviour. Allowing moves the row from the block to the end of
  the ordered rows (today's `AddActiveMod` semantics). Disallowing returns it to the block.
- **Counts that already mean "mods"** need no change because `ActiveMods` is mods-only: MWVM:2551, 5429 and `ActiveModsFilterResultText`
  (MWVM:5269), `SpeakActiveModOrder` (B's optional "sections" variant reads `ActiveRows`), and the `OrderEquals` check at
  MWVM:5203. (A needed F-8/F-10 patches for each. This model doesn't.)

---

## 8. List rendering, selection, screen reader

### 8.1 Rendering (B §7, verified gotchas)

- Divider rows get their own container style, chosen by a type-keyed `ModRowStyleSelector : StyleSelector` (new,
  `src/GUI/Controls`). That style is a full-width `ListViewItem` template with **no `GridViewRowPresenter`**: a chevron `ToggleButton` with
  `dd:DragDrop.DragSourceIgnore="True"` and `Focusable="False"`, the name `TextBlock`, an inline-rename `TextBox`, and the count text. It
  uses AdonisUI brushes only, so there are no new keys in Light.xaml/Dark.xaml. Check `Layer2BackgroundBrush`, `Layer2BorderBrush` and
  `AccentHighlightBrush` exist in AdonisUI 1.17.1.
- **The selector is ignored while `ItemContainerStyle` is non-null** (B, WPF source). On the **active list only**: remove the
  attribute at HorizontalModLayout.xaml:116, set a local `null` in `SetupListView` (.xaml.cs:109) for the active list, and
  leave MainResourceDictionary.xaml:443 for the inactive list (the local null wins).
- The selector calls `container.ClearValue(AutomationProperties.NameProperty/HelpTextProperty)` before returning the
  divider style, so a recycled mod container doesn't keep a stale name (ScreenReaderHelperBehavior.cs:111, 124 [E]). The divider style
  binds `AutomationProperties.Name` to `AutomationName` and does **not** set `ScreenReaderHelperBehavior.Automatic`.
- **No `IsSelected` binding on divider containers** (§2.2). There is no `CachedTextBlock` for divider names either: they are a few percent of rows.
  C's reuse is optional.
- The `#` cell (MainResourceDictionary.xaml:852): add an `OrderIndexTextConverter` (−1 → "", or a lock glyph for override
  members). If 6.1 has turned that cell into a `CachedTextBlock`, apply the converter there.
- **Keep virtualization unconditional.** `IsVirtualizing="True"` and `VirtualizationMode="Recycling"` (HorizontalModLayout.xaml:119-120)
  stay. No `GroupDescriptions`, ever. Pin `CanContentScroll="True"` on `ListScrollViewer` (MainResourceDictionary.xaml:448,
  AUDIT-PLAN 17) so a theme change can't silently turn it off.
- **Known cost:** one recycle queue, so a style swap whenever a recycled container changes row type [E from WPF source per B].
  [I] It's expected to be at most one or two swaps per frame at ~20 dividers. **Gate P1** (§15). If the swaps show up, fall back to B's single-style variant
  (one container style with a `DataTrigger` switching a `ContentPresenter` between the row presenter and the divider visual).

### 8.2 Collapse (view-only)

- The rule (one predicate, owned by `ActiveOrderRows.UpdateCategoryRows(filterActive)`, O(n), writes only on change):
  - Mod row: hidden if `Visibility != Visible` (the filter's flag) **or** (`IsCollapsedByCategory` **and** no filter text).
  - Divider: visible if there's no filter, or if `VisibleMemberCount > 0`.
  - **The filter wins.** While filtering, matches inside collapsed categories show, and collapse flags are kept for when the filter clears.
- Mechanism v1: a style trigger in `DivinityModDataListItem` (MainResourceDictionary.xaml ~L380-401),
  `<DataTrigger Binding="{Binding IsCollapsedByCategory}" Value="True"><Setter Property="Visibility" Value="Collapsed"/>`.
  A trigger setter beats the style's `Visibility` binding setter (L369), so the filter binding is untouched (A §10.1).
  `UpdateCategoryRows` clears `IsCollapsedByCategory` while filtering.
- **Stage 5 neutrality.** If Stage 5 moves to collection filtering (S5-B), the same predicate becomes the view filter over
  `ActiveRows.Items`, written against `ISelectable`/`CategoryDivider`. S5-A §5.3 already sketches it, but its "dividers must be items of
  `ObservableCollectionExtended<DivinityModData>`" assumption must be updated to `ActiveRows`. That also fixes R-D below: collapsed
  rows then have no container at all.
- **Persistence.** `Collapsed` goes in the layout row. A toggle **doesn't** mark the order dirty and doesn't clear `HasExported`. It's written
  at the next save or export, and for Current by the debounced layout store (§9.2).
- Toggling: Space or numpad +/− on a focused divider (**not** Left/Right, which are global MoveFocus hotkeys, AppKeys.cs:64-67 per B),
  or a click on the chevron. Hidden children are deselected and the reduced selection is pushed to the ListView with wt-perf's
  `SetSelection`. If the screen reader is on, speak "Collapsed X, N mods hidden".

### 8.3 Selection (built on wt-perf `309b81e`/`396d7b2`)

- `UpdateIsSelected` reconciles `ActiveRows.Items` from `SelectedItems.OfType<ISelectable>()`. Mods are keyed by `UUID` as today,
  dividers by reference. The fallback branch's `Cast<DivinityModData>()` becomes `OfType<…>`, otherwise the first divider click throws
  `InvalidCastException`.
- `UpdateViewSelection(ViewModel.ActiveMods, …)` (HorizontalModLayout.xaml.cs:263) → `ActiveRows.Items`.
  `GetListViewFor`/`SelectMods` route K1/K2 to `ActiveModsListView`. The pane's branch goes away.
- Collapsed and filtered rows can't become selected (the guard in §5.1). Shift-range across a collapsed block puts hidden items in
  `SelectedItems`, and the model refuses them. Accept that and note it (it already happens today with an active filter).
- `MoveSelectedMods` (HorizontalModLayout.xaml.cs:295-400) uses `ActiveRows.Items` for index lookups and the `ManualDropInfo`
  collections, and excludes dividers and override members. If nothing is left, `ShowAlert` and return.
- Delete key with dividers selected: remove only the dividers (mods stay and join the previous category), `ShowAlert(Info)`, and
  never reach the file-delete path (MWVM:5044-5080).

---

## 9. Behaviour of the Overrides block and "Current"

### 9.1 Overrides (6.2)

| Kind (C §2.3) | Shown | Draggable | `Index` | Written |
|---|---|---|---|---|
| K1 no meta.lsx | Overrides block | no | − | never (now enforced by PR-0) |
| K2 meta, not allowed | Overrides block | no (v1) | − | no |
| K2 meta, allowed | normal row at its position | yes | contiguous | yes (as today) [GAME G10] |
| K3 merged | normal mod | yes | contiguous | as any mod |

- v1: the block is pinned at the **end**, members are sorted by `Name`, and the header tooltip reads "Always loaded by the game; position has no
  effect" [GAME G3]. No numbers, against the mock-up (owner Q1; all three designs agree).
- PR B5 (optional): the Overrides header becomes movable as a block (Role row anchor). A non-override drop inside the block snaps
  to just before its header. This gives the mock-up's placement at no game cost.
- Later and optional: K2 drag-out maps to the toggle (B §6.2). This is deferred because it makes a modsettings.lsx change from a drag gesture.

### 9.2 Where Current's layout lives

- **`Data/order-layouts.json`** (`DivinityApp.GetAppDirectory("Data", …)`, next to settings.json at MWVM:1115). It is **not under
  Orders**, which is scanned recursively for `*.json` (DMDL:1147-1159), so neither this build nor an older one would show a phantom order.
  Shape: `Dictionary<string, OrderLayout>` keyed by `SelectedProfile.UUID` (DivinityProfileData.cs:17).
- Written after a successful export, and debounced (1 s) on layout edits while Current is selected. Read in `BuildModOrderList`
  after Current is filled (~MWVM:1790). If the store has no entry for the profile, use `LastExported.Layout` **only if**
  `LastExported.OrderEquals(current UUIDs)` (the A idea, with the multi-profile flaw from §2.2 fixed).
- MWVM:2967 `SetOrder(SelectedModOrder.Order)` → `SetOrder(SelectedModOrder)`, so exporting X gives Current X's layout.
  `BackupCurrentLoadOrderAsync` already uses `SetOrder(DivinityLoadOrder)`, so LastExported carries `Layout` for free. Save As
  (MWVM:2689-2690) copies `Layout` (anchors to mods filtered out by Save As fall back to `Position`).
- `RefreshAsync`: snapshot `SelectedModOrder.Layout` and re-apply it when the refreshed selection is Current (A's R-I).
- Read failure: log it and continue without the layout. Write failure: `ShowAlert(Warning)`. Losing it only ever loses Current's categories, never the order.

---

## 10. Drag/drop rules (6.3)

Coordinates are gong's `UnfilteredInsertIndex` into `ActiveRows.Items`. That index counts collapsed rows, which is why
collapse never removes rows in v1.

| # | Gesture | Rule |
|---|---|---|
| D1 | Mods across a divider | Allowed. `Project` renumbers. Moving across a boundary without changing the mod sequence changes **no** `Index` |
| D2 | Drag a divider (expanded or collapsed) | Moves the **whole block**: the header plus every row to the next divider, collapsed members included (`ExpandCategoryBlocks` in `StartDrag`, `.ToList()`'d so it isn't evaluated lazily at drop time). A mixed selection gives the union in row order. (C's "header only" is rejected: it silently re-homes members) |
| D3 | A block lands strictly inside a category | Snap to the **nearest** category boundary: divider indices, the Overrides header index, or `Count`. Ties go later. Index 0 is a boundary only if row 0 is a divider, so a block can't capture the leading uncategorised mods (B) |
| D4 | Insert index inside a **collapsed** category's hidden range | Snap to that block's end, so the mods are appended to the category. It **stays collapsed**. Select the header and announce "Moved N mods into X". (B's auto-expand is the alternative. Owner Q4) |
| D5 | Drop on the lower half or centre of a divider | Insert at divider+1, the top of that category. This works for empty categories too |
| D6 | Any non-override drop at or after the Overrides header (pinned last) | Clamp to the header index, i.e. the end of the last user category |
| D7 | Target = inactive list | Strip dividers and override members from the data. If nothing is left, `Effects = None`. **Override `DragOver`/`CanAcceptData`** so gong's type check doesn't refuse the whole mixed drop (§2.2) |
| D8 | Override members, the Overrides header | Not draggable (`CanDrag` false). Drops onto the block follow D6 |
| D9 | Column-header sort on the active list | Disabled while any user divider exists. Show `ShowAlert("Column sorting is off while categories exist; use 'Sort category by name'", Info, 10)` (HorizontalModLayout.xaml.cs:708-786). "Sort category by name" really reorders that range and so changes indices |
| D10 | Alt+Up/Down (ModListView.cs:228-300) | Generalise the guard to `IList` over `ActiveRows.Items`. A mod hops one row and may cross a divider (that changes its category, not its `Index`). A selected divider moves its whole block past the adjacent block. A mod hops over a collapsed block as one step. Then call `CommitStructureChange()`, which also fixes master's missing `Index` write |
| D11 | After every drop | `CommitStructureChange()` replaces ModListDropHandler.cs:218-221. The reselect loop (:223-234) runs over `DivinityModData` only. `FilterActiveRows` replaces the `OnFilterTextChanged(…, ActiveMods)` call at :250-254. Remove the `ForceLoadedModsView` refresh at :242 |

Feedback: when dragging a block, use `DropTargetAdorners.Highlight` on the snapped boundary row and set `DestinationText`
("Move category before X").

---

## 11. Migration and compatibility

| Scenario | Result |
|---|---|
| Existing order JSON | Loads as today, with the auto Overrides block at the end. Re-saves byte-identically (`Layout` null) until the user adds a category or moves/collapses Overrides |
| Fork file in an upstream/older WPF build | Same `Order`, same mods, no missing-mod popup, no `ForceAllow` flips, same modsettings.lsx. **Re-saving there drops `Layout`** (release note) |
| Fork file in upstream Avalonia | `Order` migrated to `Entries`. `Layout` goes to `Extras` or is ignored [I, T-C2]. Never emit `Entries` |
| An order with a K1 UUID (hand-edit, friend, Avalonia) | After PR-0 the mod stays in the Overrides block, isn't written, and gets one `ShowAlert(Info)` |
| `.txt`/`.tsv`/list-JSON export and import | Unchanged in v1: mods plus overrides, no dividers (C §8.8). Optional later: `## Category` lines in `.txt` (the import regex `\((\S+\.pak)\)` at DMDL:1242 ignores them) |
| Zip export/import (Stages 2-3) | `Layout` rides in the order JSON. **Prerequisite: the AUDIT-PLAN-01 fix must copy with `SetOrder(DivinityLoadOrder)`**, not the entries-only overload, or imported categories vanish |
| Stage 8 multi-order | Each order's JSON carries its own `Layout`, so nothing needs merging. The pak union is ∪`Order` + overrides, with overrides deduplicated by pak file name/hash, never by UUID. S8's `IsDivider(entry)` predicate becomes unnecessary because dividers never enter `Order` |
| Stage 9 sync | Diff `Order` (mods) and `Layout.Rows` (by `Id`) separately. UUID anchors make "keep my extras" merges stable |
| Your `=====` placeholder paks | **No automatic conversion.** An opt-in "Replace with category" action, offered after T-M2: insert a divider at the row, move the placeholder mod to inactive, and confirm with "This mod will no longer be loaded; saves made with it may warn it is missing" [GAME G8/G9]. Detection is a heuristic (name made mostly of `=-_#~*`, few files) and the UI labels it as one |

---

## 12. PR series

```
master
 ├─ fix/modsettings-write-guard        PR-0  upstreamable: guard + read-back + ShowAlert + LoadModOrder K1 fix   (first)
 ├─ fix/modsettings-partial-reset-report PR-0b optional upstreamable: watcher reports entries the game removed (C §6.4)
 ├─ perf/drop-renumber                 optional upstreamable: O(n) renumber (ModListDropHandler.cs:218-221)
 ├─ fix/alt-move-index                 optional upstreamable: Alt+Up/Down writes Index (confirm the stale "#" in app first)
 └─ perf/list-render (6.1)             upstreamable, TEMP commits dropped, 309b81e hand-resolved (AUDIT-PLAN 30-31)
fork/main  ← merge the above --no-ff
 └─ feat/overrides-in-order (6.2)
      A1 Core: CategoryDivider, OrderLayout/OrderLayoutRow (+passthrough, JsonExtensionData), DivinityLoadOrder.Layout
         (+SetOrder/Clone), OrderRowLayout (BuildRows/Project/snap rules), IsCollapsedByCategory, src/Tests/Core.Tests (xunit,
         net8.0-windows; there is no test project today). No UI change.
      A2 VM: ActiveOrderRows, ActiveRows as ItemsSource, ActiveMods derived, six mutation sites rerouted, drag/drop/Alt-move/
         selection/MoveSelectedMods/Delete ported, ForceAllow per-order reset, fork BuildOutputList guard.
      A3 View: ModRowStyleSelector + divider style (Overrides header), ItemContainerStyle removed on the active list,
         CanContentScroll pinned, Index converter, Overrides pane retired (ForceLoadedMods kept).
      Gate: INV-1 golden diff, T-U*, T-C4, P1, in-game steps 1-7, T-G3.
 └─ feat/categories (6.3, on top of 6.2)
      B1 Layout persistence: read/write in LoadModOrder/UpdateOrderFromActiveMods, order-layouts.json for Current,
         MWVM:2967 SetOrder(SelectedModOrder), Save As/LastExported/Refresh carry-through.
      B2 User dividers: insert (AppKeys hotkey with [MenuSettings("Edit", …)]), F2 rename, delete-keep-mods, context menu,
         screen-reader names and announcements.
      B3 Collapse: predicate, trigger, filter interplay, persistence. Gate P1 with 3 collapsed categories (~200 hidden rows).
      B4 Block drag D1-D11, sort disable, Alt-move blocks.
      B5 optional: movable Overrides block (Role anchor).
      B6 optional: "Replace with category" for placeholder paks, only after T-M2.
      Gate: full §15, T-C1-T-C3.
```

PR-0/0b/perf/fix are cut from `master` and contain nothing fork-only (R3). New files are added with BOM and CRLF, following the
owner's `core.autocrlf` (BLD.md).

---

## 13. Risks

| # | Risk | Mitigation |
|---|---|---|
| R7 / C1 | A divider or K1 leaks into modsettings.lsx, and the game clears the whole order [GAME G5] | Structural (INV-2), PR-0 guard + read-back, fork guard, golden diff, in-game protocol |
| F4 | `Project` bug silently reorders exported mods | One function, a property test (random permutations → `ActiveMods` = exportable rows in sequence), golden bytes |
| B1 | A missed mutation path desyncs `ActiveRows`/`ActiveMods` | Reroute the six sites, a grep gate, a debug assert |
| R-D / B2 | Collapsed blocks are realized as zero-height containers. Style swap on recycle | P1 gate against 6.1 numbers (≤10% frame-time regression, owner Q5). Fallbacks: the single-style variant, and collapse via collection filter (Stage 5 mechanism) |
| R-A | An older/upstream build re-saves and drops `Layout` | Release note. (Optional later: a fork-side cache restoring a known order's layout. It must not be `.json` under Orders) |
| R-K | A future fork build drops newer row kinds | Passthrough + `JsonExtensionData` from A1 |
| C5 | The `Data/` store is lost (portable re-extract) | Same exposure as settings.json. LastExported fallback when orders match |
| D7 | Gong refuses mixed drops to inactive | Explicit `DragOver` override (§10) |
| C4 | Users read the Overrides block's position as meaningful | No `Index`, a tooltip. T-G4 may change this |
| J-1 | `ForceAllow` reset surprises existing users | Release note. T-G2 step |
| M-1 | Heavy MWVM churn means upstream merge conflicts (~2093-2153, 4841-4889) | Row logic in two new files. Hooks stay small |

---

## 14. Open questions for you

- **Q1.** Overrides block: unnumbered and pinned at the end in v1, movable in B5 (recommended), or numbered as in the mock-up (not recommended:
  `#` would stop matching modsettings.lsx positions)?
- **Q2.** OK to make `ForceAllowInLoadOrder` per-order (§6 item 6)?
- **Q3.** PR-0: skip-and-alert on a non-GUID UUID (recommended), or warn-but-write until T-G5 has run?
- **Q4.** A drop into a collapsed category: stay collapsed and announce (recommended), or auto-expand (B)?
- **Q5.** Perf budget for P1: ≤10% over 6.1 frame times?
- **Q6.** Offer "Replace with category" for the `=====` paks at all, and only after T-M2?
- **Q7.** May PR-0 add `src/Tests` to a master-based branch (upstream has none)?

---

## 15. Verification protocol

### 15.1 Unit / golden (Windows, `src/Tests/Core.Tests`)

| ID | Test |
|---|---|
| T-U1 | **Golden bytes.** Fixture mods (3× K0, K3, K2 allowed and not, K1, campaign). `GenerateModSettingsFile(BuildOutputList(Order))` for (a) no layout, (b) categories + collapse, (c) the Overrides block moved. All three are byte-equal to a golden file produced once from `fc35358` code |
| T-U2 | Guard: a K1 path UUID, `"not-a-guid"` and an empty UUID are skipped and reported. An allowed K2 is written |
| T-U3 | Campaign first in every T-U1 variant, including a divider or Overrides block at row 0 |
| T-U4 | `Project` property test: random permutations of mods, dividers and override members → `ActiveMods` = exportable rows in sequence |
| T-U5 | INV-7 after random drags and Alt-moves. Crossing a boundary without a sequence change leaves every `Index` unchanged |
| T-U6 | `Layout` round-trip including an unknown `Kind` row and an unknown field (proves `[Newtonsoft.Json.JsonExtensionData]` on `[DataContract]`). An order without `Layout` serialises byte-identically to master. A fork file deserialised by a copy of master's class gives the same `Order` and no error |
| T-U7 | Anchors: `Before` missing → `Position`; null → end; shared anchor keeps row order; anchor before a `Missing` entry |
| T-U8 | Snap rules D3-D7, including index 0 with leading uncategorised mods, an empty category and a collapsed target |
| T-U9 | Collapse/expand any category: `ActiveMods` sequence and every `Index` unchanged. With the filter on, collapse is suspended and empty dividers are hidden |
| T-U10 | Read-back (INV-6): written UUID sequence == `LoadModSettingsFileAsync` sequence (ignored mods removed on both sides). A hand-made path UUID makes the read fail, which documents the hazard |

### 15.2 In-app (Debug **and** Publish, R5)

- **T-B1.** On `fc35358`: Export Order to Text as `.json`, then Load Mod Order From File, then Export to Game. Record whether a pak path ends up in
  modsettings.lsx. On PR-0 it must not, and the override stays in Overrides.
- **T-C1.** Open a categorised fork order in an unmodified upstream release: no missing-mod popup, and Export to Game writes the same UUID
  sequence as the fork. **T-C2:** the same file in an Avalonia build, if one runs. **T-C3:** after using categories on Current, no
  phantom order appears in either build's dropdown. **T-C4:** a zip of a categorised order contains every K1 pak and `Layout`,
  and re-import restores the categories (once the AUDIT-PLAN-01 fix lands).
- **P1.** wt-perf `PerfScrollDriver`/`PerfProbe`, 935 rows: (a) 0 dividers, (b) 20 dividers, (c) 20 dividers with 3 collapsed (~200 hidden),
  thumb-drag and wheel. Measure median frame time, the share of frames over 33 ms, and containers created (should stay ~106, i.e. virtualization alive) against 6.1.
- **UX.** Drag across a divider and check `#` is unchanged. Drag a collapsed category: all members move and the count is unchanged. Drop on a collapsed header. Filter
  with collapsed categories, then clear. Delete on a divider (no file dialog). Ctrl+A then Enter moves only mods. Theme toggle. With NVDA/Narrator,
  dividers read name, count and state, and a **mod row that recycled a divider container still reads the mod name**.

### 15.3 In-game (the gate for every feature PR)

**Setup, once.** Close the game. Back up `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\modsettings.lsx`
and a listing of `...\Mods`. Record the game version. Create a **test profile in game** for the destructive tests (README.md:50).
Fixture paks in Mods (no subfolders, G6): one K1 with a visible effect at the main menu or in the UI, one K2, one K3, one `=====` placeholder, plus
your 935 mods. Make a save with the normal order beforehand.

**For each build** (`fc35358` baseline, PR-0, 6.2, 6.3). Steps 3, 4 and 7 make up T-G1, and step 5 is T-G3:

| Step | Action | Pass |
|---|---|---|
| 1 | Load order X, Export to Game | Success alert. No skipped-entries alert, except the expected one on T-B1 fixtures |
| 2 | Copy modsettings.lsx to `X_<build>.lsx`, then `certutil -hashfile … SHA256` | **INV-1:** same hash across builds for X without categories. In 6.3, add 10 categories, collapse 3 and move Overrides: re-export and the **hash is unchanged**. `findstr` every divider name and Id in the lsx: zero hits |
| 3 | Launch the game and wait ~30 s at the main menu | No reset/disabled-mods prompt, and the main menu loads (G2) |
| 4 | In-game Mod Manager | Enabled count and order match `ActiveMods` (Overrides excluded) (G1). Overrides don't appear as modules |
| 5 | The K1 fixture's effect | Present, whatever the block's position (G3) |
| 6 | Load the pre-test save | Same missing-mod warning state as baseline (G8) |
| 7 | Quit. Copy modsettings.lsx to `X_<build>_after.lsx` | UUID sequence equals step 2's (the bytes may differ, G7). No "reset externally" alert, and none from PR-0b |

**One-off tests on the test profile only.** Restore the backup afterwards and don't rely on the LastExported restore while it's under test.

| ID | Action | Record |
|---|---|---|
| T-G2 | Allow the K2 fixture into the order mid-list, export, boot | It's listed at its row position and works (G10). Switch to an order without it: it's back in Overrides and absent from the lsx (§6 item 6) |
| T-G4 | Two K1 paks overriding the same visible file. Swap their file names (alphabetical order) | Does the winner change? If yes, precedence is by file name. Note it as a possible future feature, not part of 6.2 |
| T-G5a | Hand-add a `ModuleShortDesc` with a random valid GUID matching no mod | Dropped alone, or the whole list cleared (G5)? Does PR-0b report it? |
| T-G5b | Hand-add one with `UUID="C:\x.pak"` and `Folder="Gustav"` (the §2.4 shape) | Same question. The manager should alarm on read-back |
| T-M2 | Deactivate the placeholder pak, boot, load a save made with it active | Missing-mod warning or not (G8/G9). This decides B6 and its wording |

**Pass criteria.** PR-0: T-U2, T-U10, T-B1, steps 1-7 on baseline order. 6.2: T-U1/3/4/5/7, T-C4, P1(a), steps 1-7, T-G2,
T-G3. 6.3: everything above plus T-U6/8/9, T-C1-T-C3, P1(b, c), step 2's categorised hash check. Record the hashes and results in the
plan's Verification section.
