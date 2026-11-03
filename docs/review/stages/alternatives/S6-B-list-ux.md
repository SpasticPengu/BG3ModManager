# Stage 6.2 + 6.3 design, angle B: list and interaction first

Author: design agent "S6-B-list-ux", 2026-11-03. Scope: Stage 6.2 (overrides folded into the order) and
6.3 (categories/dividers), designed from the list outward: rendering, collapse, drag/drop, keyboard,
selection, screen reader, active vs inactive list. The model, JSON and modsettings.lsx rules are included
because the list design depends on them.

Evidence rules. `path:line` is master @ fc35358 unless prefixed `wt-perf:` (origin/perf/list-render) or
`wt-ava:` (upstream/avalonia). "WPF source" means dotnet/wpf `main`, fetched for this design. **[inference]**
marks anything not read directly from code. **[game]** marks a claim about the game that can't be checked
in this container; each one names the in-game test that would settle it. Nothing here was built or run.

Reports (`scratchpad/reports/`): only AUDIT-PLAN.md and AUDIT-CLAUDE.md existed when I finished.
VM2, S6, LOAD and UI were still missing, so re-check this against VM2 (drop handler) and LOAD
(BuildOutputList) when they land. AUDIT-PLAN agrees with or adjusts this design in four places:
- **Claim 32.** `BuildOutputList` already drops UUIDs that match no mod. The *other* consumers of
  `Order` (zip export MWVM:3572, the JSON writer, `SaveLoadOrderAs` MWVM:2690, and older or upstream
  builds) are not covered. That is why this design keeps `Order` mods-only (§3.4) instead of
  filtering at each consumer.
- **Claim 34.** Number `Index` from a separate counter, not `IndexOf`. Adopted in §4.3.
- **Claim 17.** `CanContentScroll=True` on the list comes only from AdonisUI's
  `GridViewScrollViewerStyleKey` style. Pin `CanContentScroll="True"` on `ListScrollViewer`
  (MainResourceDictionary.xaml:448) in feat/overrides-in-order, so a theme change can't silently turn
  virtualization off under the new rows.
- **Claim 14.** `=` characters can't make `MeasureText` throw. The placeholder-divider *names* still
  exist in the real order (implementation-plan.txt:406-407), so §11 is unaffected; only the "58
  exceptions" attribution was wrong.

---

## 0. Headline

1. **The ListView shows a new row collection. `ActiveMods` stays mods-only and is derived from it.**
   `ActiveRows` (`ObservableCollectionExtended<ISelectable>`) is the active list's single flat
   `ItemsSource`: user mods, `ModDivider` rows and a pinned Overrides block. `ActiveMods` keeps its type
   and its meaning, "exportable mods in order". It is rebuilt from `ActiveRows` after every structural
   change. So the ~70 existing readers of `ActiveMods` and `Order`, including every export path, never
   see a divider or a pure override. No WPF grouping anywhere.
2. **Divider rows use a type-keyed `ItemContainerStyleSelector` with their own full-width
   `ListViewItem` template, not a `GridViewRowPresenter`.** That keeps virtualization and recycling.
   It costs a template swap whenever a recycled container switches between mod and divider, which is
   checked against WPF source and should be measured with PerfProbe. One gotcha must be fixed for the
   selector to work at all: `ItemContainerStyle` is set in three places today, and it beats the selector.
3. **The JSON stays upstream-shaped.** `Order` still holds only real mod entries. Dividers go in a new
   sibling array `Dividers`, anchored by the UUID of the next mod, with a position fallback. Older
   builds and upstream (WPF and Avalonia) ignore the array, so they load the same mods and write the
   same modsettings.lsx. They drop the dividers if they re-save the file, and nothing worse happens.
4. **Overrides do not go into `Order`.** That would break upstream: its `LoadModOrder` sets
   `ForceAllowInLoadOrder = true` on any force-loaded mod it finds in an order
   (MainWindowViewModel.cs:2126-2129), and that leads to an override's pak *path* being written as a
   module UUID. Overrides are shown as a pinned system category. Dragging an eligible override out of
   that category, or back in, is exactly the existing "Allow in load order" toggle. **For the same flags,
   modsettings.lsx is byte-identical before and after 6.2.**
5. **Collapse only changes visibility. Rows and `Index` never change.** It works through the same
   `Visibility` property the Stage 5 filter stamps today, and is suspended while a filter is active.
   That leaves Stage 5 free to keep stamping or to move to collection filtering.

---

## 1. Owner intentions I have gleaned (answering the relayed question)

From CLAUDE.md, the plan, the overview and the task text:

1. Stay on WPF master and keep it upstream-friendly. Fixes and perf go first, on branches cut from
   `master`. Fork-only features are layered on `fork/main` and never become a base for upstreamable work
   (plan §3 "governing rule").
2. 6.1 render perf comes before everything else in Stage 6. Categories must not undo it: virtualization
   at 935 active rows is non-negotiable (R2; the landmine at MainResourceDictionary.xaml:460-462).
3. Categories are flat rows in one collection, never `CollectionViewSource` grouping.
4. The game must never see a divider. modsettings.lsx must stay something the game accepts, and that is
   proven by booting the game, not by looking at the file (6.2, R7, §15).
5. Collapsing is a view concern. Load-order indices underneath must not shift (mock-up: 1, 2, 3, then
   "Overrides, 2 collapsed", then 6).
6. Overrides become "a normal category" in the same list. The separate Overrides pane is meant to go away.
7. Order JSON must round-trip without confusing older builds or upstream.
8. Don't block either future for the Stage 5 filter: per-item Visibility stamping, or collection filtering.
9. Evidence over argument: measure (PerfProbe), cite path:line, label inferences, and don't re-argue
   rejected hypotheses.
10. House rules: no `string?` in Core/GUI, tabs/CRLF/BOM/Allman/`var`, unprefixed new types, mutate
    settings in place, `ShowAlert` for user-visible errors.
11. Accessibility counts. "If you use a screen reader, check it still reads mod names" is in the
    owner's verify list.
12. Ideas only from Redux and Avalonia, no code reuse (plan §2).
13. Stage 8 multi-order export is coming, so the order format should carry categories cheaply.
14. You already use placeholder "divider" mods whose names are made of `=` characters (plan
    implementation-plan.txt:406-407). Native categories should give you a way off them.

---

## 2. What the code does today (the parts this design leans on)

| Fact | Where |
|---|---|
| `ActiveMods`/`InactiveMods` are plain `ObservableCollectionExtended<DivinityModData>`, rebuilt imperatively | MainWindowViewModel.cs:161-165, 2141-2144 |
| Active ListView `ItemsSource` = `ActiveMods` | HorizontalModLayout.xaml.cs:526 |
| Overrides live in a second ListView fed by a DynamicData filter `IsForceLoaded && !IsForceLoadedMergedMod && !ForceAllowInLoadOrder` | MainWindowViewModel.cs:5244-5246; HorizontalModLayout.xaml:127-141 |
| The text exporter already lists overrides **after** the order, **sorted by Name** | MainWindowViewModel.cs:3802-3803 |
| Loading an order flips `ForceAllowInLoadOrder = true` on every force-loaded mod in it | MainWindowViewModel.cs:2126-2129 |
| Overrides without meta.lsx get `UUID = <pak path>`, `ModType = "File Override"` | DivinityModDataLoader.cs:612-620 |
| `BuildOutputList` resolves each non-missing entry against all mods by UUID and writes whatever resolves | DivinityModDataLoader.cs:1431-1469 |
| `GenerateModSettingsFile` writes every mod with a non-blank UUID | DivinityModDataLoader.cs:1471-1487 |
| `UpdateOrderFromActiveMods` = `Order.Clear(); AddRange(ActiveMods, force)` | MainWindowViewModel.cs:4880-4889 |
| Any Add/Remove/Reset on `ActiveMods` schedules that rebuild after 250 ms and clears `HasExported` | MainWindowViewModel.cs:5400-5407 |
| `Order` is re-sorted by `mod.Index` 25 ms after any Index change | MainWindowViewModel.cs:5256-5259, 4577-4603 |
| Drop renumbers with `mod.Index = ActiveMods.IndexOf(mod)` (O(n²)) | ModListDropHandler.cs:218-221 |
| Drag data = `IsSelected && Visibility == Visible` (active); also `CanDrag` (inactive) | ModListDragHandler.cs:64-73 |
| Drop uses `UnfilteredInsertIndex`; filters drop data with `.OfType<DivinityModData>()` | ModListDropHandler.cs:133, 150 |
| Filter stamps `Visibility` per item; the container binds it | MainWindowViewModel.cs:4199-4311; MainResourceDictionary.xaml:369 |
| `IsSelected` refuses `true` while `Visibility != Visible` | DivinityModData.cs:185-196 |
| Alt+Arrow moves rows only if `ItemsSource is ObservableCollectionExtended<DivinityModData>`, and **never writes `Index`** | ModListView.cs:228-300 |
| Plain Left/Right are global hotkeys (MoveFocusLeft/Right) | AppKeys.cs:64-67; HorizontalModLayout.xaml.cs:572-618 |
| `ItemContainerStyle` is set in the style, in XAML and in code | MainResourceDictionary.xaml:444; HorizontalModLayout.xaml:116; HorizontalModLayout.xaml.cs:109 |
| Screen-reader names are set as a **local** value on DataContextChanged | ScreenReaderHelperBehavior.cs:90-128 |
| wt-perf reconciles selection from `ListView.SelectedItems.OfType<DivinityModData>()` and selects through `SetSelectedItems` | wt-perf:HorizontalModLayout.xaml.cs:185-245; wt-perf:ModListView.cs:202 |
| Order JSON loads in Newtonsoft with an error handler and no `MissingMemberHandling`, so unknown members are ignored | DivinityJsonUtils.cs:12-19 |
| Order files are discovered by recursing `*.json` under the Orders dir (only `settings.json` is excluded) | DivinityModDataLoader.cs:1147-1159 |
| Upstream Avalonia has nested `ModOrderContainer` entries in a new `Entries` array, and migrates a legacy `Order` array | wt-ava:src/Core/Models/Mod/Order/ModOrderContainer.cs; wt-ava:.../ModOrder.cs:324-333 |

WPF source facts (dotnet/wpf main):
- `ItemsControl.ApplyItemContainerStyle` uses `ItemContainerStyle` first, and only falls back to the
  selector when it is null. It re-runs on every `PrepareItemContainer`, including recycled containers,
  and replaces a style it set earlier (`IsStyleSetFromGenerator`).
- `ItemContainerGenerator` keeps **one** recycle queue (`_recyclableContainers`, a single `Queue<DependencyObject>`).
  It is not partitioned by style or item type. So a recycled mod container can be prepared for a divider,
  which swaps its style and template, and the reverse.

---

## 3. Model

### 3.1 Row model: `ModDivider` (new, Core)

`src/Core/Models/ModDivider.cs`. It implements `ISelectable` (ISelectable.cs:5-10: `IsSelected`,
`Visibility`, `CanDrag`), so gong and the existing selection code can treat it like any other row.

```csharp
using System.Windows;

namespace DivinityModManager.Models;

public enum DividerKind
{
	User,
	Overrides
}

public class ModDivider : ReactiveObject, ISelectable
{
	public const string OverridesId = "overrides";

	public string Id { get; }
	public DividerKind Kind { get; }

	[Reactive] public string Name { get; set; }
	[Reactive] public bool IsCollapsed { get; set; }
	[Reactive] public bool IsEditing { get; set; }
	[Reactive] public string Color { get; set; }
	[Reactive] public Visibility Visibility { get; set; }

	/// <summary>Maintained by ActiveOrderRows after each structural change or filter pass.</summary>
	[Reactive] public int ChildCount { get; set; }
	[Reactive] public int VisibleChildCount { get; set; }
	[Reactive] public bool IsFilterActive { get; set; }

	[ObservableAsProperty] public string CountText { get; }
	[ObservableAsProperty] public string AutomationName { get; }

	private bool _isSelected;
	public bool IsSelected
	{
		get => _isSelected;
		set
		{
			if (value && Visibility != Visibility.Visible)
			{
				value = false;
			}
			this.RaiseAndSetIfChanged(ref _isSelected, value);
		}
	}

	public bool CanDrag => Kind == DividerKind.User;
	public bool IsSystem => Kind != DividerKind.User;

	internal IDisposable CollapseSubscription { get; set; }

	public ModDivider(string id, string name, DividerKind kind = DividerKind.User)
	{
		Id = id;
		Name = name;
		Kind = kind;
		Visibility = Visibility.Visible;

		this.WhenAnyValue(x => x.ChildCount, x => x.VisibleChildCount, x => x.IsCollapsed, x => x.IsFilterActive)
			.Select(x => ToCountText(x.Item1, x.Item2, x.Item3, x.Item4))
			.ToUIPropertyImmediate(this, x => x.CountText, String.Empty);

		this.WhenAnyValue(x => x.Name, x => x.CountText, x => x.IsCollapsed)
			.Select(x => $"Category {x.Item1}, {x.Item2}, {(x.Item3 ? "collapsed" : "expanded")}")
			.ToUIPropertyImmediate(this, x => x.AutomationName, String.Empty);
	}

	private static string ToCountText(int total, int visible, bool collapsed, bool filterActive)
	{
		if (filterActive) return $"{visible} of {total}";
		if (collapsed) return $"{total} collapsed";
		return total.ToString();
	}

	public OrderDivider ToOrderDivider() => new() { Id = Id, Name = Name, Collapsed = IsCollapsed, Color = Color };
}
```

Notes:
- It is **not** a `DivinityModData` subclass. If it were, it would compile its way silently into every
  `ActiveMods`/`Mods` consumer. Its colour and `HasInvalidUUID` logic would also run, because a
  divider's Id is not a GUID (DivinityModData.cs:446-448, 483-511). Keeping it a separate type means
  every site that needs to know about dividers is one the compiler points at.
- It is never added to `mods` (the SourceCache), so `ActiveSelected`/`InactiveSelected`
  (MainWindowViewModel.cs:5261-5264) keep counting mods only.
- `Id` is `Guid.NewGuid().ToString()` for user dividers. It never collides with a mod UUID, and it
  doesn't resolve in `BuildOutputList` even if it leaked in (defence in depth, §5).

### 3.2 Persisted shape: `OrderDivider` (new, Core)

`src/Core/Models/OrderDivider.cs`:

```csharp
using System.Runtime.Serialization;

namespace DivinityModManager.Models;

[DataContract]
public class OrderDivider
{
	[DataMember] public string Id { get; set; }
	[DataMember] public string Name { get; set; }

	/// <summary>UUID of the first mod entry after this divider. Null/empty = end of the order.</summary>
	[DataMember(EmitDefaultValue = false)] public string Before { get; set; }

	/// <summary>Count of mod entries before this divider when saved. Fallback when Before no longer resolves.</summary>
	[DataMember] public int Position { get; set; }

	[DataMember(EmitDefaultValue = false)] public bool Collapsed { get; set; }
	[DataMember(EmitDefaultValue = false)] public string Color { get; set; }

	public OrderDivider Clone() => (OrderDivider)MemberwiseClone();
}
```

`DivinityLoadOrder` (DivinityLoadOrder.cs:26-318) gains one member, plus copy semantics:

```csharp
[DataMember] public List<OrderDivider> Dividers { get; set; }

// Newtonsoft's ShouldSerialize convention: files without categories stay byte-identical to today.
public bool ShouldSerializeDividers() => Dividers != null && Dividers.Count > 0;

public bool HasDividers => Dividers != null && Dividers.Count > 0;
```

- `SetOrder(DivinityLoadOrder)` (:289) and `Clone()` (:304) also copy
  `Dividers?.Select(x => x.Clone()).ToList()`. `SetOrder(IEnumerable<DivinityLoadOrderEntry>)` (:283)
  leaves `Dividers` alone, because it only knows about mod entries.
- `Order` is unchanged and keeps meaning "mod entries in load order". No `Kind` field is added to
  `DivinityLoadOrderEntry`.
- **To verify in a unit test:** that Newtonsoft honours `ShouldSerializeDividers` on a `[DataContract]`
  type. **[inference]** It should, because `DefaultContractResolver` picks the method up for any
  property. If it doesn't, use `EmitDefaultValue = false` and keep `Dividers` null when there are none.

### 3.3 JSON example

The mock-up's order (implementation-plan.txt:1097-1112) with one user category collapsed:

```json
{
  "Order": [
    { "UUID": "0b6b9c5e-1c1a-4f3e-9a2b-1d2e3f4a5b6c", "Name": "Combat Extender" },
    { "UUID": "7c1d2e3f-4a5b-4c6d-8e9f-0a1b2c3d4e5f", "Name": "Party Limit Begone" },
    { "UUID": "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d", "Name": "Tactician Enhanced" },
    { "UUID": "d4e5f6a7-b8c9-4d0e-9f1a-2b3c4d5e6f7a", "Name": "Better Hair" }
  ],
  "Dividers": [
    { "Id": "6f0c2a8e-5b1d-4c9e-8a7f-3d2c1b0a9e8f", "Name": "Gameplay Overhauls", "Before": "0b6b9c5e-1c1a-4f3e-9a2b-1d2e3f4a5b6c", "Position": 0 },
    { "Id": "9e1d3b7c-2a4f-4e6d-b8c0-1f2e3d4c5b6a", "Name": "Cosmetics", "Before": "d4e5f6a7-b8c9-4d0e-9f1a-2b3c4d5e6f7a", "Position": 3, "Collapsed": true }
  ]
}
```

The Overrides category is **not** in this file. It is computed from the mods folder and pinned at the
end of the list (§6). Its collapsed state is a per-user setting.

### 3.4 Why this shape, and the alternatives I rejected

| Option | Older WPF build / upstream master | Upstream Avalonia | Verdict |
|---|---|---|---|
| **A. Sibling `Dividers` array, anchored by UUID (chosen)** | Ignores the unknown member. Loads the same mods. Exports the same lsx. Re-saving drops the dividers silently, which is harmless. | Migrates `Order` into `Entries` (wt-ava ModOrder.cs:324-333) and ignores the extra member | Safe |
| B. Divider entries inside `Order` (`{"UUID":"divider:…","Name":"── X ──"}`) | `LoadModOrder` skips them, then `DisplayMissingMods` reports every divider as a **missing mod** (MainWindowViewModel.cs:2742-2745). "Clear missing mods" deletes them (:4677). | `IModOrderEntryConverter` sees an unknown type. Behaviour unknown. | Rejected (R7) |
| C. Divider anchored by an integer position only | Fine | Fine | Rejected as the primary anchor. `Order` is mutated outside the list (`ClearMissingMods` :4677, `RemoveDeletedMods` :4386), which shifts positions. Kept as the fallback. |
| D. Overrides as `Order` entries | **Dangerous.** `LoadModOrder` sets `ForceAllowInLoadOrder = true` (:2126-2129), so `CanAddToLoadOrder` becomes true and the override joins `ActiveMods`. `BuildOutputList` then resolves the UUID, which for a meta-less pak is the pak path (DivinityModDataLoader.cs:619), and `GenerateModSettingsFile` writes it (:1480-1482). | n/a | Rejected |

A note on mapping to Avalonia later: the flat sections map one-to-one onto Avalonia's single-level
`ModOrderContainer { Id, Name, Children }`. A converter is easy if the fork ever moves there.

### 3.5 Where the "Current" order's dividers live

"Current" is rebuilt from modsettings.lsx on every `BuildModOrderList` (MainWindowViewModel.cs:1766-1790),
and the game can't store dividers in that file. So "Current" needs a sidecar:

- File: `DivinityApp.GetAppDirectory("Data", "categories.json")`, next to `Data/settings.json`
  (:1115). It **must not** go under the Orders directory, because that directory is scanned recursively
  for `*.json` order files (DivinityModDataLoader.cs:1147-1159).
- Shape: `Dictionary<string, List<OrderDivider>>` keyed by `SelectedProfile.UUID`.
- Written: after a successful `ExportLoadOrderAsync` (:2945 branch), and whenever the "Current"
  order's dividers change (debounced with the existing 250 ms `UpdateOrderFromActiveMods`).
- Read: in `BuildModOrderList`, after `currentOrder` is filled (:1790), set
  `currentOrder.Dividers = CategoryStore.Get(profile.UUID)`.
- Because anchors are UUIDs, a modsettings.lsx rewritten by the game or another tool degrades
  gracefully. Dividers whose anchor is gone fall back to `Position`.
- Write failures are reported with `ShowAlert(..., AlertType.Warning)`, not swallowed.
- Also, at :2967, `ModOrderList.First(x => x.IsModSettings)?.SetOrder(SelectedModOrder.Order)` becomes
  `SetOrder(SelectedModOrder)`, so exporting order "X" also carries X's dividers into "Current".
  `BackupCurrentLoadOrderAsync` (:2850-2873) already calls `SetOrder(DivinityLoadOrder)`, so LastExported
  picks the dividers up for free once §3.2 lands.

---

## 4. The row collection: `ActiveRows`

### 4.1 Ownership

New `src/GUI/ViewModels/ActiveOrderRows.cs` holds the state and the Rx wiring. Pure layout functions go
in `src/Core/Util/OrderRowLayout.cs` (static, no schedulers) so they can be unit-tested.

```csharp
public class ActiveOrderRows
{
	public ObservableCollectionExtended<ISelectable> Items { get; } = new();

	// Pure helpers live in OrderRowLayout; this class owns state and Rx wiring only.
	public IEnumerable<DivinityModData> ModRows => Items.OfType<DivinityModData>();
	public int OverridesHeaderIndex { get; private set; } = -1;

	public void Load(DivinityLoadOrder order, IEnumerable<DivinityModData> orderedActiveMods, IEnumerable<DivinityModData> overrides);
	public void CommitStructureChange();                        // see 4.3
	public void RebuildOverridesBlock(IEnumerable<DivinityModData> overrides);
	public (int Start, int End) GetCategoryRange(ModDivider divider);  // [divider, next divider)
	public List<ISelectable> ExpandCategories(IEnumerable<ISelectable> data);
	public void ApplyCategoryVisibility(bool filterActive);
	public List<OrderDivider> ToOrderDividers();
	public ModDivider InsertDivider(int rowIndex, string name);
	public void RemoveDivider(ModDivider divider);              // mods stay where they are
}
```

`MainWindowViewModel` gets `public ActiveOrderRows ActiveRows { get; } = new();`. `ActiveMods` stays a
field-backed `ObservableCollectionExtended<DivinityModData>`, but **no code outside `ActiveOrderRows`
mutates it**. That rule replaces seven direct mutation sites: :2141-2142, :3185, :3198, :4392, :4845,
:4856.

### 4.2 Building rows (in `LoadModOrder`, MainWindowViewModel.cs:2093-2153)

The loop that sets `IsActive`/`Index` (:2109-2137) stays as it is. Replace :2141-2142 with:

```csharp
var orderedActive = addonMods.Where(x => x.CanAddToLoadOrder && x.IsActive).OrderBy(x => x.Index).ToList();
ActiveRows.Load(order, orderedActive, ForceLoadedMods);
```

`OrderRowLayout.BuildRows(order, orderedActive)`:

```csharp
public static List<ISelectable> BuildRows(DivinityLoadOrder order, IReadOnlyList<DivinityModData> orderedActive, Func<OrderDivider, ModDivider> create)
{
	var rows = new List<ISelectable>(orderedActive.Count + 32);
	var byUUID = orderedActive.ToDictionary(x => x.UUID);
	var pending = order.HasDividers ? order.Dividers.ToList() : [];
	var placed = new HashSet<string>();

	// Walk Order (not orderedActive) so a divider anchored to a missing mod still lands in the right place.
	foreach (var entry in order.Order)
	{
		foreach (var d in pending.Where(x => x.Before == entry.UUID && !placed.Contains(x.Id)).ToList())
		{
			rows.Add(create(d));
			placed.Add(d.Id);
		}
		if (byUUID.TryGetValue(entry.UUID, out var mod) && !rows.Contains(mod))
		{
			rows.Add(mod);
		}
	}
	// Unresolved anchors: fall back to Position (count of mods before the divider).
	foreach (var d in pending.Where(x => !placed.Contains(x.Id)).OrderBy(x => x.Position))
	{
		rows.Insert(RowIndexForModPosition(rows, d.Position), create(d));
	}
	return rows;
}
```

`!rows.Contains(mod)` is O(n²). Use a `HashSet<DivinityModData>` in the real code. It is written this
way here only to keep the sketch short.

Then `Load` appends the Overrides block (§6), assigns with `Items.Load(rows)` (one `Reset`), calls
`CommitStructureChange()` and `ApplyCategoryVisibility(...)`.

### 4.3 `CommitStructureChange()`: the one place that rebuilds `ActiveMods` and renumbers

It runs after every drop, Alt+Arrow move, divider insert/remove, Add/RemoveActiveMod, import replace
and delete.

```csharp
public void CommitStructureChange()
{
	var mods = new List<DivinityModData>(Items.Count);
	var index = 0;
	foreach (var row in Items)
	{
		if (row is DivinityModData mod)
		{
			if (mod.CanAddToLoadOrder)
			{
				if (mod.Index != index) mod.Index = index;
				index++;
				mods.Add(mod);
			}
			else if (mod.Index != -1)
			{
				mod.Index = -1;
			}
		}
	}
	if (!_activeMods.SequenceEqual(mods))
	{
		_activeMods.Load(mods);   // Reset -> existing :5400 handler -> UpdateOrderFromActiveMods in 250 ms
	}
	RefreshChildCounts();
}
```

- This replaces the O(n²) `IndexOf` renumbering at ModListDropHandler.cs:218-221.
- `Index` is contiguous over exportable mods only. Dividers and overrides never take a number. Moving a
  mod from the end of category A to the top of the next category B changes **no** index (that is a test
  case).
- `UpdateOrderFromActiveMods` (:4880-4889) is unchanged for `Order` and gains one line:
  `SelectedModOrder.Dividers = ActiveRows.ToOrderDividers();`, plus the sidecar write if
  `IsModSettings`.
- The Index-triggered `Order.Sort` (:5256-5259) keeps working, because `Order` still has only mods.
  It is O(n²·log n) because `SortModOrder` does `mods.Items.FirstOrDefault` per comparison (:4581-4582),
  so swapping it for a dictionary lookup is an easy perf follow-up. It is not required for this design.

`ToOrderDividers()` (pure, in `OrderRowLayout`):

```csharp
var result = new List<OrderDivider>();
var pending = new List<OrderDivider>();
var position = 0;
foreach (var row in rows)
{
	if (row is ModDivider d && d.Kind == DividerKind.User)
	{
		pending.Add(d.ToOrderDivider());
	}
	else if (row is DivinityModData m && m.CanAddToLoadOrder)
	{
		foreach (var p in pending) { p.Before = m.UUID; p.Position = position; }
		result.AddRange(pending);
		pending.Clear();
		position++;
	}
}
foreach (var p in pending) { p.Before = null; p.Position = position; }
result.AddRange(pending);
return result;
```

---

## 5. modsettings.lsx rules

1. **The export input is unchanged.** `BuildOutputList(SelectedModOrder.Order, ...)`
   (MainWindowViewModel.cs:2326, 2931) still gets `Order`, which by construction (§4.3) only holds
   `CanAddToLoadOrder` mods. Dividers aren't in it. Pure overrides aren't in it, because they fail
   `CanAddToLoadOrder` (DivinityModData.cs:381-384) unless the user allowed them.
2. **Defence in depth in `BuildOutputList`** (DivinityModDataLoader.cs:1446). This costs nothing and
   guards any future caller that passes an unfiltered list:
   ```csharp
   if (mData != null && (mData.IsForceLoaded && !mData.IsForceLoadedMergedMod && !mData.ForceAllowInLoadOrder))
   {
   	DivinityApp.Log($"[*WARN*] Skipping override '{mData.Name}' that is not allowed in the load order.");
   	continue;
   }
   ```
   Divider Ids never resolve in `allMods`, so even a leaked divider entry only produces the existing
   "Missing mod" log line (:1465). **This guard is a behaviour change only for a mod whose
   `ForceAllowInLoadOrder` is false but which is in the order anyway.** Today `LoadModOrder` makes that
   impossible (:2126-2129), so the expected output diff is none. Confirm with the golden-file test (§11).
3. **`GenerateModSettingsFile` (:1471) is unchanged.** It still uses `String.Format` with
   `XML_MODULE_SHORT_DESC`, and its argument order {0} Folder, {1} MD5, {2} Name, {3} UUID,
   {4} Version64, {5} PublishHandle is untouched.
4. **`SelectedProfile.ActiveMods` mirrors** at :2318, :2972 and :4679 read `Order`, so they need no change.
5. **The game never receives:** divider names or Ids, the Overrides header, pure-override UUIDs (pak
   paths), or any Index value. modsettings.lsx carries order by node sequence only.

**[game] G1:** for an order with no categories and the same override flags, the 6.2 build writes a
modsettings.lsx byte-identical to the 6.1 build's. This can be checked offline by diffing two exports.
Then boot the game and check that the in-game Mod Manager lists the same mods in the same order.

**[game] G2:** export a categorised order. Grep the lsx for every divider name and Id (expect zero hits),
then boot the game and confirm the order applies.

---

## 6. Stage 6.2: overrides as a system category

### 6.1 Placement and numbering

- The Overrides block is pinned at the **end** of `ActiveRows`: `[user rows…][Overrides header][overrides sorted by Name]`.
  This matches how the manager already presents them, below the active list (HorizontalModLayout.xaml:106-141),
  and how the text export orders them (MainWindowViewModel.cs:3802-3803).
- Overrides have **no index**. They show `Index = -1`, rendered blank (§7.4).
- **Owner decision D1:** the mock-up (implementation-plan.txt:1097-1109) draws Overrides in the middle
  with numbers 4-5, so that "Cosmetics" starts at 6. I recommend against that. A pure override has no
  position in modsettings.lsx, so giving it a number would make `#` stop meaning "position in the file
  the game reads". Placing the block between user categories is a small extension (store an anchor for
  the system divider in the settings) if you want it anyway. The numbering, I think, should stay
  mod-only either way.
- **[game] G4 [inference]:** I believe pure overrides (no meta.lsx, or overriding a base-game folder)
  are loaded by the game whatever modsettings.lsx says, and that their relative order isn't controlled
  by it. That is why the block is sorted by Name and not reorderable. To confirm: take two overrides
  that touch the same file, swap their positions in any UI, export, and boot. Expect the same winner.
  Renaming the pak files is the variable that might matter. If the test shows the position *does*
  matter, revisit this.

### 6.2 Membership: two kinds of override

| Kind | Test (existing properties) | In the Overrides block | Draggable | Drag into a user position | Exported |
|---|---|---|---|---|---|
| Pure override | `IsForceLoaded && !IsForceLoadedMergedMod && !HasMetadata`, or has meta but `HasInvalidUUID` | yes | **no** | refused | never |
| Eligible override (meta.lsx overriding a base folder, no own files) | `ToggleForceAllowInLoadOrderVisibility == Visible`, i.e. `IsForceLoaded && HasMetadata && !IsForceLoadedMergedMod` (DivinityModData.cs:419-421) | yes, while `!ForceAllowInLoadOrder` | **yes (new)** | = `AllowInLoadOrder(true)` (:320-324), mod becomes a normal ordered row | only after it is allowed |
| Merged override | `IsForceLoadedMergedMod` | no, it is an ordinary mod today and stays one | yes | normal | yes |

- The block's contents are `ForceLoadedMods` (MainWindowViewModel.cs:5244-5246). It already
  auto-refreshes on `ForceAllowInLoadOrder`. Subscribe to the existing
  `forceLoadedModsConnection` (:5376) and call `ActiveRows.RebuildOverridesBlock(ForceLoadedMods)`.
  That only replaces the tail range `[OverridesHeaderIndex, Count)`, so it is cheap.
- `CanDrag` in the ctor (DivinityModData.cs:465-481) changes, fork-only:
  `CanDrag = forceAllowInLoadOrder || isActive || !isForceLoaded || isForceLoadedMergedMod || (HasMetadata && !HasInvalidUUID);`.
  `HasMetadata` and `HasInvalidUUID` are added to the `WhenAnyValue` list.
- Dropping an allowed override back onto or below the Overrides header sets `AllowInLoadOrder(false)`.
  That is the same as today's context-menu toggle (DivinityGlobalCommands.cs:111-125) and is still
  offered there.
- The existing context-menu toggle keeps working. It calls `AddActiveMod`/`RemoveActiveMod`, which
  now route through `ActiveRows` (§4.1).

### 6.3 Retiring the pane

- HorizontalModLayout.xaml:106-141: remove `ActiveModListViewGridSplitter`, the "Overrides" TextBlock
  and `ForceLoadedModsListView`, and collapse the row definitions to one.
  HorizontalModLayout.xaml.cs:548-570 (row-height toggling), :508-513 (its SelectionChanged), :276-290 and :492
  (its ItemContainerStatusChanged) and the `ForceLoadedModsView` member of `IModViewLayout` go with them.
  `HasForceLoadedMods` (:5376-5377) now drives the header row's visibility.
- `Keys.DeleteSelectedMods` (MainWindowViewModel.cs:5054-5059) checks `ForceLoadedModsListView.IsKeyboardFocusWithin`. Fold
  that into the active-list branch, with `targetList = ActiveRows.ModRows`.
- `ModListDropHandler` (:241-242) refreshes `ForceLoadedModsView`. Drop that line.
- wt-perf `GetListViewFor` (wt-perf:HorizontalModLayout.xaml.cs:185-192) returns `ActiveModsListView`
  for overrides.
- Overrides now go through the active filter (§8.2). Today the pane is never filtered, because
  `OnFilterTextChanged` is only called for Active and Inactive (:5250-5254).
- The collapsed state lives in settings: `[DataMember, Reactive] public bool OverridesCategoryCollapsed { get; set; }`
  on `DivinityModManagerSettings` (DivinityModManagerSettings.cs, near :88). It has no
  `[SettingsEntry]` on purpose, because it is view state and not a preference. Mutate it in place.

---

## 7. Rendering divider rows (virtualization-safe)

### 7.1 Style selector (new, GUI)

`src/GUI/Controls/ModRowStyleSelector.cs`:

```csharp
using DivinityModManager.Models;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

public class ModRowStyleSelector : StyleSelector
{
	public Style ModStyle { get; set; }
	public Style DividerStyle { get; set; }

	public override Style SelectStyle(object item, DependencyObject container)
	{
		if (item is ModDivider)
		{
			// A recycled mod container keeps the Name/HelpText that ScreenReaderHelperBehavior set as a
			// LOCAL value (ScreenReaderHelperBehavior.cs:111,124); a local value beats the style binding.
			container.ClearValue(AutomationProperties.NameProperty);
			container.ClearValue(AutomationProperties.HelpTextProperty);
			return DividerStyle;
		}
		return ModStyle;
	}
}
```

### 7.2 Wiring, and the three places that block the selector

`ApplyItemContainerStyle` ignores the selector whenever `ItemContainerStyle` is non-null (WPF source).
For the **active** list only:

1. HorizontalModLayout.xaml:116: remove `ItemContainerStyle="{StaticResource ListViewItemMouseEvents}"`
   and add `ItemContainerStyleSelector="{StaticResource ActiveRowStyleSelector}"`.
2. HorizontalModLayout.xaml.cs:109: `SetupListView` sets `ItemContainerStyle` on both lists. Give it a
   parameter so the active list gets `listView.ItemContainerStyle = null;` (a local null, which beats
   the style setter below).
3. MainResourceDictionary.xaml:444: the `ModOrderListView` style setter. The local null from step 2
   overrides it, so leave it for the inactive list.

In `HorizontalModLayout.xaml` resources:

```xml
<c:ModRowStyleSelector x:Key="ActiveRowStyleSelector"
	DividerStyle="{StaticResource ModDividerListItem}"
	ModStyle="{StaticResource ListViewItemMouseEvents}" />
```

### 7.3 Divider container style (MainResourceDictionary.xaml, after `DivinityModDataListItem` at :255-403)

```xml
<Style x:Key="ModDividerListItem" BasedOn="{StaticResource {x:Type ListViewItem}}" TargetType="{x:Type ListViewItem}">
	<!-- Deliberately NO behavior:ScreenReaderHelperBehavior.Automatic: it would set a local Name once and go stale. -->
	<Setter Property="AutomationProperties.Name" Value="{Binding AutomationName}" />
	<Setter Property="Tag" Value="{Binding}" />
	<Setter Property="Visibility" Value="{Binding Visibility, FallbackValue='Visible'}" />
	<Setter Property="HorizontalContentAlignment" Value="Stretch" />
	<Setter Property="ContextMenu" Value="{StaticResource ModDividerContextMenu}" />
	<Setter Property="Template">
		<Setter.Value>
			<ControlTemplate TargetType="{x:Type ListViewItem}">
				<Border x:Name="Bd"
					Padding="2,3"
					Background="{DynamicResource {x:Static adonisUI:Brushes.Layer2BackgroundBrush}}"
					BorderBrush="{DynamicResource {x:Static adonisUI:Brushes.Layer2BorderBrush}}"
					BorderThickness="0,1,0,1"
					SnapsToDevicePixels="True">
					<Grid>
						<Grid.ColumnDefinitions>
							<ColumnDefinition Width="Auto" />
							<ColumnDefinition Width="Auto" />
							<ColumnDefinition Width="Auto" />
							<ColumnDefinition Width="*" />
						</Grid.ColumnDefinitions>
						<ToggleButton x:Name="Chevron"
							dd:DragDrop.DragSourceIgnore="True"
							AutomationProperties.Name="Collapse or expand category"
							Focusable="False"
							IsChecked="{Binding IsCollapsed, Mode=TwoWay}"
							Style="{StaticResource DividerChevronStyle}" />
						<TextBlock x:Name="DividerName" Grid.Column="1" Margin="4,0" FontWeight="SemiBold" Text="{Binding Name}" />
						<TextBox x:Name="DividerNameEditor" Grid.Column="1" MinWidth="160" Text="{Binding Name, UpdateSourceTrigger=Explicit}" Visibility="Collapsed" />
						<TextBlock Grid.Column="2" Foreground="{DynamicResource {x:Static adonisUI:Brushes.DisabledForegroundBrush}}" Text="{Binding CountText}" />
					</Grid>
				</Border>
				<ControlTemplate.Triggers>
					<Trigger Property="IsSelected" Value="True">
						<Setter TargetName="Bd" Property="Background" Value="{DynamicResource {x:Static adonisUI:Brushes.AccentHighlightBrush}}" />
					</Trigger>
					<DataTrigger Binding="{Binding IsEditing}" Value="True">
						<Setter TargetName="DividerName" Property="Visibility" Value="Collapsed" />
						<Setter TargetName="DividerNameEditor" Property="Visibility" Value="Visible" />
					</DataTrigger>
				</ControlTemplate.Triggers>
			</ControlTemplate>
		</Setter.Value>
	</Setter>
</Style>
```

- **Full width without spanning columns.** The template has no `GridViewRowPresenter`, so it ignores
  `GridView.Columns` and fills the arranged width of the `VirtualizingStackPanel`. That width is the
  widest row, i.e. the summed column widths. **[inference]** It scrolls horizontally with the content.
- `DividerChevronStyle` reuses the existing `ExpandDownImage`/`ExpandUpImage` bitmaps
  (MainResourceDictionary.xaml:57-60).
- **Brushes come from AdonisUI only**, so there are no new keys in Light.xaml/Dark.xaml and no
  key-for-key mismatch (plan Stage 7). **Check** that `Layer2BackgroundBrush`, `Layer2BorderBrush` and
  `AccentHighlightBrush` exist in the pinned AdonisUI version, and substitute the nearest keys if they
  don't.
- A divider's `Color` (optional, user-set) applies through `StringToSolidBrushConverter`, which already
  exists (:39), in a `DataTrigger` on `Color != null`.
- No `CachedTextBlock` is needed for dividers. They are about 2% of rows. The wt-perf name cell
  (wt-perf:MainResourceDictionary.xaml:719) is in `ModNameTemplate`, which a divider container never
  instantiates. So nothing binds `DisplayName` against a divider, and there are no binding errors.

### 7.4 Index cell for rows without an index

The active GridView's `#` cell binds `{Binding Index}` (MainResourceDictionary.xaml:852). Overrides now
sit in that list with `Index = -1`. Add `OrderIndexTextConverter` (-1 → "") and use
`Text="{Binding Index, Converter={StaticResource OrderIndexTextConverter}}"`. If 6.1 converts the
Index cell to `CachedTextBlock` first, apply the same converter there.

### 7.5 Virtualization and recycling cost

- Nothing changes on `ListView`, `ScrollViewer` or `VirtualizingStackPanel`. `IsVirtualizing="True"`
  and `VirtualizationMode="Recycling"` (HorizontalModLayout.xaml:119-120) stay. No `CollectionViewSource`
  grouping, so the `IsGrouping` trigger (:460-462) stays inert. Never set `CanContentScroll`.
- **Known cost (WPF source):** there is one recycle queue, so when a divider scrolls in, it may take a
  recycled mod container, and `ApplyItemContainerStyle` swaps that container's `Style`. That rebuilds the
  template, and the reverse happens when the divider leaves. With ~20 dividers among 935 rows, a
  25-rows-per-frame thumb drag realizes roughly 0.5 dividers per frame. **[inference]** I expect one or
  two template swaps per frame at most, each costing about a fresh mod row (well under 1 ms).
  **Measure** with the wt-perf PerfProbe rig (`perfscroll`) on the real order, with and without 20
  dividers, and compare frame work against the 6.1 numbers (24.7-25.4 ms/frame at 25 rows/frame,
  implementation-plan.txt:921-927).
- **Fallback if the swap shows up:** use a single container style for both row types and keep a
  `ContentPresenter` that switches between a `GridViewRowPresenter` and the divider visual with a
  `DataTrigger` on a cheap `IsDivider`-style flag. That avoids the style swap but still swaps
  sub-templates. Only take it on evidence.
- `ScrollUnit="Pixel"` (the inactive list's "not smooth" fix, still untried) is independent of this
  design and compatible with it.

---

## 8. Collapse, filter and visibility

### 8.1 The rule

The `Visibility` a row's container binds to is computed from two inputs:

```
filterActive = !String.IsNullOrWhiteSpace(ActiveModFilterText)
mod row:     Visible  iff  (filterActive ? MatchesFilter(mod) : !InCollapsedCategory(mod))
divider row: Visible  iff  (filterActive ? VisibleChildCount > 0 : true)
```

- **While a filter is active, collapse is suspended.** Matches inside collapsed categories show, and
  dividers with no matches hide. Collapse flags are kept and come back when the filter clears. That
  keeps the "N hidden" label (`TotalActiveModsHidden`) counting filter hits only.
- **Collapse never touches `ActiveRows` order, `Index`, `ActiveMods` or `Order`.** So "indices don't
  shift" holds by construction, and the visible `#` column shows gaps (1, 2, 3, 6), as in the mock-up.
- Hidden rows inherit the existing guarantees for free. `IsSelected` refuses `true`
  (DivinityModData.cs:190-193), and the drag handler skips them (ModListDragHandler.cs:66). That is the
  reason for reusing `Visibility` rather than adding a second container property.

### 8.2 Hook points (Stage-5-neutral)

- Add `FilterActiveRows()` to `MainWindowViewModel`:
  ```csharp
  private void FilterActiveRows()
  {
  	var filterActive = !String.IsNullOrWhiteSpace(ActiveModFilterText);
  	if (filterActive)
  	{
  		OnFilterTextChanged(ActiveModFilterText, ActiveRows.ModRows.ToList());
  	}
  	else
  	{
  		ActiveRows.ApplyCollapseOnly();   // stamps Visibility from collapse state; only writes on change
  		TotalActiveModsHidden = 0;
  	}
  	ActiveRows.ApplyCategoryVisibility(filterActive);
  }
  ```
  It replaces the three `OnFilterTextChanged(ActiveModFilterText, ActiveMods)` calls: :2146, :5251 and
  ModListDropHandler.cs:252. `OnFilterTextChanged`'s tail compares `modDataList == ActiveMods` (:4305),
  so pass a flag or set `TotalActiveModsHidden` in the wrapper. `ActiveModsFilterResultText` (:5269)
  divides by `ActiveMods.Count`. Use `ActiveRows.ModRows.Count()`, because overrides now count.
- **If Stage 5 keeps stamping:** its "only write when changed" optimisation applies unchanged, and
  `ApplyCollapseOnly` follows the same compare-before-write rule.
- **If Stage 5 moves to collection filtering:** the same rule becomes the predicate of a filtered view
  over `ActiveRows`. Nothing here depends on items staying in the collection, except gong's
  `UnfilteredInsertIndex`, and that already handles a filtered `ICollectionView`.

### 8.3 Toggling

`ActiveOrderRows` subscribes per divider when it creates the row:

```csharp
divider.CollapseSubscription = divider.WhenAnyValue(x => x.IsCollapsed).Skip(1)
	.ObserveOn(RxApp.MainThreadScheduler)
	.Subscribe(_ => OnCollapsedChanged(divider));
```

`OnCollapsedChanged`:
- If the filter is inactive, walk `GetCategoryRange(divider)` and set each child's `Visibility`.
- Clear `IsSelected` on hidden children, and push the reduced selection to the ListView with wt-perf's
  `ModListView.SetSelection` (wt-perf:ModListView.cs:202), so the view and the model agree.
- If the screen reader is active, speak "Collapsed {Name}, {n} mods hidden" or "Expanded {Name}".
- Do **not** mark the order dirty or clear `HasExported`. `Collapsed` is written at the next save as a
  UI hint. For the Overrides category, mutate `Settings.OverridesCategoryCollapsed` in place.

---

## 9. Drag and drop

### 9.1 What can be dragged

`ModListDragHandler.StartDrag` (ModListDragHandler.cs:58-87):

```csharp
if (dragInfo.SourceCollection == _viewModel.ActiveRows.Items)
{
	var selected = _viewModel.ActiveRows.Items.Where(x => x.IsSelected && x.Visibility == Visibility.Visible && x.CanDrag);
	dragInfo.Data = _viewModel.ActiveRows.ExpandCategories(selected);   // divider -> divider + ALL its children, even collapsed ones
}
```

- Selecting a divider and dragging it moves the **whole category**, header and children together.
  Children are included even when collapsed, which is how a collapsed category moves as one block.
- A mixed selection (a divider plus loose mods) becomes the union, de-duplicated and kept in row order.
- The Overrides header (`CanDrag == false`) and pure overrides (`CanDrag == false`) never start a drag.
  `CanStartDrag` (:96-114) already checks `ISelectable.CanDrag` for single items. Change the multi-item
  branch from `IEnumerable<DivinityModData>` to `IEnumerable<ISelectable>`.
- `DragDirectlySelectedOnly=True` (HorizontalModLayout.xaml:27) stays. `dd:DragDrop.DragSourceIgnore`
  on the chevron means clicking to collapse never starts a drag.

### 9.2 Where it lands

`ModListDropHandler.Drop` (ModListDropHandler.cs:105-261). Changes:

```csharp
var rows = _viewModel.ActiveRows;
bool isActive = dropInfo.TargetCollection == rows.Items;                 // was == ActiveMods (:116)
...
var data = ExtractData(dropInfo.Data).OfType<ISelectable>().ToList();    // was OfType<DivinityModData> (:150)
if (!isActive)
{
	// Dividers never leave the active list; overrides never go inactive.
	data = data.OfType<DivinityModData>().Where(x => x.CanAddToLoadOrder || x.ForceAllowInLoadOrder).Cast<ISelectable>().ToList();
}
else
{
	insertIndex = rows.AdjustInsertIndex(dropInfo, insertIndex, data);   // BEFORE the removal loop, which then decrements as today
}
// ... existing remove-from-source / insert loop (:152-207) unchanged, now over IList of ISelectable ...
if (isActive)
{
	rows.ApplyOverrideTransitions(data);   // eligible override above header -> AllowInLoadOrder(true); allowed mod at/below header -> false
}
rows.CommitStructureChange();           // replaces :218-221
// :223-234 (IsActive/IsSelected stamping) only for DivinityModData items
_viewModel.FilterActiveRows();          // replaces :250-254 for the active side
_viewModel.UpdateOrderFromActiveMods();
```

`AdjustInsertIndex` rules, in `OrderRowLayout` (pure and testable):

| # | Situation | Rule |
|---|---|---|
| D1 | Any insert at or after `OverridesHeaderIndex` by a non-override | Clamp to `OverridesHeaderIndex`, i.e. the end of the last user category |
| D2 | An eligible override dragged from the block to above the header | Allowed. It becomes an ordered mod at that position (§6.2) |
| D3 | Data contains a divider (a block drag) and the index is strictly inside a category | Snap to the **nearest category boundary**. Boundaries are divider indices and `OverridesHeaderIndex`/`Count`; ties go later. **Index 0 is not a boundary when `Rows[0]` is a mod**, because inserting a block there would capture the leading uncategorised mods |
| D4 | The resulting position is inside a collapsed category | Allowed, but **expand that category** after the drop so nothing lands out of sight. Speak/alert "Moved N mods to {Category}" |
| D5 | The target is the divider row, lower half or centre (gong `RelativeInsertPosition.AfterTargetItem`/`TargetItemCenter`) | Insert at divider+1, the top of that category. This also works for an empty category |
| D6 | A multi-select of mods from several categories | All land contiguously at the drop point in their current relative order, leaving their old categories (same as today) |
| D7 | The target is the inactive list and the data contains dividers | Refused in `DragOver`: set `dropInfo.Effects = DragDropEffects.None` when every item is a `ModDivider`, and silently drop dividers from mixed data. **[inference]** gong 4.0's `DefaultDropHandler` may already refuse because `ModDivider` isn't assignable to `DivinityModData`. Don't rely on that |

Feedback while dragging: in `DragOver` (:80-103), when the data contains a divider, use
`DropTargetAdorners.Highlight` on the snapped boundary row, and set `dropInfo.DestinationText` to
"Move category before {next}" (gong shows it with its default effect template). Plain mod drags keep the
insertion line.

### 9.3 `Index` contiguity proofs (unit tests on `OrderRowLayout`)

- Any sequence of drops leaves `Index` equal to `0..N-1` over `CanAddToLoadOrder` rows, in row order,
  and `-1` elsewhere.
- Moving mods across a divider boundary without changing the mod sequence leaves every `Index` unchanged.
- Moving a block A before block B gives, for each mod, the index `Order` would give after concatenating
  the sections in their new order.

### 9.4 Keyboard moves (ModListView.cs:219-321)

- Generalise the guard at :228 from `ItemsSource is ObservableCollectionExtended<DivinityModData>` to
  `ItemsSource is ObservableCollectionExtended<ISelectable> list`. The inactive list stays
  `<DivinityModData>`. Handle both by writing the move loop against `IList` and `Move`
  (`ObservableCollection<T>.Move`), with a small switch on the concrete type.
- Alt+Up/Down on a mod moves it one row and may cross a divider (that changes its category, not its
  Index). Alt+Up/Down on a selected divider moves the whole block past the adjacent block. Use
  `GetCategoryRange` and one `Move` per row, or a remove-then-insert of the range.
- After the loop, call `MainWindow.Self.ViewModel.ActiveRows.CommitStructureChange()`. ModListView
  already reaches the VM this way (wt-perf:ModListView.cs:168, 182).
- **Upstreamable finding [from source, not run]:** on master, Alt+Up/Down never writes `mod.Index`
  (ModListView.cs:228-300). `Order` follows, because the move fires `CollectionChanged` and so
  `UpdateOrderFromActiveMods` (:5400-5407), but the `#` column should go stale until the next drop or
  load. Confirm in the app, then fix it on `fix/alt-move-index` from `master` with the counter loop from §4.3.

---

## 10. Keyboard, selection, screen reader, list behaviour

### 10.1 Keyboard

| Key | On a divider row | Notes |
|---|---|---|
| Up/Down | moves focus through visible rows | Collapsed containers aren't focusable **[inference: KeyboardNavigation skips non-visible elements]** |
| **Space**, or numpad **+**/**−** | toggle / expand / collapse | Handled in `ModListView.OnKeyDown` before `base.OnKeyDown`. **Not Left/Right**, because those are the global MoveFocusLeft/Right hotkeys (AppKeys.cs:64-67) |
| **F2** | inline rename (`IsEditing = true`, focus `DividerNameEditor`); Enter commits, Esc cancels | Set `e.Handled` in the TextBox, because Enter is `Keys.Confirm`, which triggers MoveSelectedMods (HorizontalModLayout.xaml.cs:678-699) |
| Alt+Up/Down | move the whole category | §9.4 |
| Delete | **remove the divider(s) only; mods untouched** | In `Keys.DeleteSelectedMods` (MainWindowViewModel.cs:5044-5080): if the active list has focus and any selected row is a `ModDivider`, call `ActiveRows.RemoveDivider` for each, `ShowAlert("Removed N categories (mods kept)", Info, 10)`, and return. The file-deletion path never sees a selection containing a divider |
| Enter (Confirm) | no-op when only dividers are selected | `ActiveSelected` counts mods only, so the existing guard (HorizontalModLayout.xaml.cs:695) already skips it |
| Ctrl+A | selects all visible rows, dividers included | Dragging that moves everything (net no-op) |
| New hotkey `AddCategory` (suggest Ctrl+Shift+N, menu "Edit") | inserts a divider above the focused or first selected row, then F2 | Add it to `AppKeys.cs` with `[MenuSettings(...)]`. The menu is generated by reflection (CLAUDE.md §4) |

### 10.2 Selection (built on wt-perf's SelectedItems reconcile)

- wt-perf:HorizontalModLayout.xaml.cs:213-245 reconciles from
  `sourceView.SelectedItems.OfType<DivinityModData>()`. Extend it to `OfType<ISelectable>()`, keyed by
  reference rather than `UUID` for dividers. The master fallback branch (`e.AddedItems.Cast<DivinityModData>()`,
  HorizontalModLayout.xaml.cs:222-229) **would throw `InvalidCastException` on the first divider click**,
  so it must become `OfType<DivinityModData>()` plus a divider loop.
- `UpdateViewSelection(ViewModel.ActiveMods, ...)` (:263) becomes `ActiveRows.Items`. That method
  already takes `IEnumerable<ISelectable>`.
- `MoveSelectedMods` (HorizontalModLayout.xaml.cs:295-400) mixes index spaces. It reads
  `ViewModel.ActiveMods.IndexOf(selectedMod)` and writes it into `ActiveModsListView.SelectedIndex`.
  Use `ActiveRows.Items.IndexOf`. Its `ManualDropInfo` calls pass `ViewModel.ActiveMods` as target or
  source (:307, :359). Pass `ViewModel.ActiveRows.Items`.
- Shift-range selection across a collapsed category: WPF puts the hidden children into `SelectedItems`.
  The model refuses them (`IsSelected` guard), so operations driven by the model stay correct. The
  ListView's own count is inflated, which is the same as today with a filter active. It matters only for
  ModListView's "single selected item click deselects" check (wt-perf:ModListView.cs:164-192). Accept,
  and note it.
- Header-click sort (HorizontalModLayout.xaml.cs:707-776): sorting the active view while dividers exist
  would scatter the divider rows. **Owner decision D2.** I recommend that, when `ActiveRows` contains
  any user divider, a header click on the active list does nothing and shows
  `ShowAlert("Column sorting is off while categories exist; use 'Sort category by name' on a category", Info, 10)`.
  The context-menu action "Sort category by name" really reorders the mods in that range, and so changes
  indices.

### 10.3 Screen reader

- Divider rows announce "Category Gameplay Overhauls, 3, expanded" through the bound
  `AutomationProperties.Name` (§7.3). Name staleness on recycled containers is handled in the selector
  (§7.1).
- On toggle, call `Services.ScreenReader.Speak("Collapsed Gameplay Overhauls, 3 mods hidden")`, using
  the existing pattern at HorizontalModLayout.xaml.cs:312, 365.
- On a drop or Alt-move that changes a mod's category, speak "Moved 2 mods to Cosmetics".
- `SpeakActiveModOrder` (MainWindowViewModel.cs:5351-5362) speaks sections:
  "Gameplay Overhauls: A, B, C. Cosmetics: D…" plus "Overrides: N", built from `ActiveRows`.
- Optional phase 2: a `DividerItemAutomationPeer` exposing `ExpandCollapsePattern`. **[inference]**
  ListView item peers are created by the list's peer (`ModListViewAutomationPeer`, a custom
  `CachedAutomationPeer`), not by the container, so check how that peer builds its children before
  promising this.
- **Check:** the owner's screen-reader verify item ("still reads mod names") must also pass for a mod
  row that recycled a divider container. `Automatic` re-subscribes when the mod style re-applies
  (ScreenReaderHelperBehavior.cs:68-86).

### 10.4 Active vs inactive list

- **Dividers exist only in the active list.** The inactive list stays
  `ObservableCollectionExtended<DivinityModData>` with its fixed `ItemContainerStyle`. None of its code
  changes, beyond `MoveSelectedMods` passing `ActiveRows.Items` as the source.
- A mod moved to inactive loses its category. Moving it back puts it at the drop position, or for
  Enter-moves, at `ActiveModsListView.SelectedIndex`. If that index is inside a collapsed category,
  rule D4 expands it.
- Divider context menu "Deactivate all mods in category" moves the range to inactive through the
  existing `ManualDropInfo` path and keeps the now-empty divider.

### 10.5 Divider context menu (`ModDividerContextMenu`)

Rename (F2) · Collapse/Expand · Collapse all / Expand all · Add category above · Remove category (keep
mods) · Sort category by name · Select all mods in category · Deactivate all mods in category ·
Colour… (optional). The Overrides header gets only Collapse/Expand and Collapse all/Expand all.

---

## 11. Migration and compatibility

| Scenario | Result |
|---|---|
| Existing order JSON (no `Dividers`) | Loads exactly as today. It is re-saved byte-identical while no divider is added (ShouldSerialize, §3.2) |
| Fork order with `Dividers` opened in an **older fork build / upstream WPF** | Same mods, same order, same lsx. Dividers are dropped on that build's next save. No missing-mod warnings |
| Same file in **upstream Avalonia** | `Order` is migrated into `Entries` (wt-ava ModOrder.cs:324-333). `Dividers` is an ignored extra **[inference: extras are tolerated, since it reads `Order` via `TryGetExtraProperty`]** |
| Older-build-saved file reopened in the fork | No dividers. Nothing to repair |
| `Order` edited outside the fork (mods removed or reordered) | Dividers re-anchor by `Before` UUID. A lost anchor falls back to `Position`, clamped |
| "Current" (modsettings.lsx) rewritten by the game or another tool | The sidecar re-anchors the same way (§3.5) |
| Archive export/import (Stage 2/3) and Stage 8 multi-order export | The order JSON carries `Dividers` with no extra work. Dedupe works over mods, and dividers aren't mods |
| `.txt`/`.tsv` import (DivinityModDataLoader.cs:1241-1321) | Has no dividers. Optional: the `.txt` export writes `# Category: X` comment lines, which the import regex `\((\S+\.pak)\)` (:1242) ignores |

**Placeholder "divider mods"** (implementation-plan.txt:406-407 says the real order has names made of
`=` characters). Add an opt-in command, **"Convert placeholder dividers…"** (context menu on the active
list plus the File or Edit menu):
- Detect active mods whose `Name` matches `^\s*[=\-_~#*─-╿]{3,}` or ends with that run,
  **and** whose pak carries almost nothing (`Files.Count <= 2`, i.e. meta.lsx and maybe an icon).
  **[inference]** I believe these are empty paks. The preview dialog must show the file list so you
  can tell.
- In the preview, list each one with its proposed category name (symbols trimmed). On confirm, insert a
  `ModDivider` at its row and **move the placeholder mod to inactive**. That is a modsettings.lsx change.
- **[game] G5:** after converting, export and boot. Expect the same behaviour, and the placeholder
  modules gone from the in-game Mod Manager.
- Never automatic. It must be reversible by re-activating the mods.

---

## 12. Risks

| # | Risk | Mitigation |
|---|---|---|
| B1 | A mutation path is missed and mutates `ActiveMods` directly, so rows and mods desync | Make `_activeMods` private to `ActiveOrderRows`'s owner. Grep gate: no `ActiveMods.(Add|Remove|Insert|Move|Clear|Replace|RemoveMany|AddRange)` outside ActiveOrderRows (current sites listed in §4.1). Run a debug-only invariant check in `CommitStructureChange` |
| B2 | Style swap on recycled containers costs frame time | Measure (§7.5). Fallback design ready |
| B3 | A divider or pure override leaks into modsettings.lsx (R7) | Structural: `Order` is mods-only. Guard in `BuildOutputList` (§5.2). Golden diff tests G1/G2 |
| B4 | The master selection fallback casts to `DivinityModData` and throws | §10.2. Build feat branches on fork/main *after* perf/list-render merges |
| B5 | A stale screen-reader Name on a recycled container | `ClearValue` in the selector (§7.1). Manual check with NVDA/Narrator |
| B6 | A block drop captures neighbouring mods | Snapping rules D3/D1 and index-0 exclusion. Unit tests |
| B7 | A drop lands invisibly in a collapsed category | Rule D4 auto-expands, plus an announcement |
| B8 | Interaction with the Stage 5 rewrite | One rule (§8.1) with two documented implementations. Stage 5 owns `OnFilterTextChanged` internals, this design owns `FilterActiveRows` |
| B9 | `ShouldSerializeDividers` is ignored on a DataContract type | Unit test. Fallback is null plus `EmitDefaultValue=false` |
| B10 | The 6.2 `CanDrag` change makes some override draggable that shouldn't be | Only rows with `HasMetadata && !HasInvalidUUID`, the same set the existing toggle offers (DivinityModData.cs:419-421) |
| B11 | Sidecar `categories.json` lost or corrupt | Only the "Current" dividers are lost, never the order. `ShowAlert` on write failure. Ignore and log on parse failure |
| B12 | Header-sort UX regression | Owner decision D2 |

Upstream drift: everything here is fork-only and touches MainWindowViewModel heavily. Expect upstream
merge conflicts around :2093-2153 and :4841-4889. Keep the row logic in the two new files so conflicts
stay small (§3 of the plan).

---

## 13. Branch and PR breakdown

Prerequisite: `perf/list-render` (6.1) is cleaned (TEMP commits dropped, 309b81ef hand-resolved,
implementation-plan.txt:1052-1071) and merged into `fork/main` with `--no-ff`. Both feature branches
build on wt-perf's selection code and `CachedTextBlock`.

Optional upstreamable spin-offs, cut from `master` (verify in the app first):
- `fix/alt-move-index`: renumber `Index` after Alt+Up/Down (§9.4).
- `perf/drop-renumber`: replace `IndexOf` renumbering (ModListDropHandler.cs:218-221) and the
  `SortModOrder` linear lookups (:4581-4582) with O(n) and dictionary versions.

**`feat/overrides-in-order`** (from fork/main, fork-only). One PR, four commits:
1. `ModDivider` (system kind only), `ModRowStyleSelector`, `ModDividerListItem` style,
   `DividerChevronStyle`, `OrderIndexTextConverter`.
2. `ActiveOrderRows` and `OrderRowLayout` (rows, commit/renumber, ranges, visibility). Active
   `ItemsSource` switches to `ActiveRows.Items`. Port the drag, drop, Alt-move, selection,
   MoveSelectedMods and Delete hooks. `ActiveMods` becomes derived. **No JSON change.**
3. The Overrides system block: tail rebuild from `ForceLoadedMods`, retire the pane, the `CanDrag`
   change, drag in/out mapped to `AllowInLoadOrder`, filter applies to overrides,
   `Settings.OverridesCategoryCollapsed`, the `BuildOutputList` guard.
4. Golden lsx diff harness and the test notes (§14).

**`feat/categories`** (from fork/main after `feat/overrides-in-order` merges). One PR, five commits:
1. `OrderDivider`, `DivinityLoadOrder.Dividers`, ShouldSerialize, copy in SetOrder/Clone, and
   round-trip tests including old-format and Avalonia-shaped files.
2. Build rows from `Dividers` in `LoadModOrder`, write them in `UpdateOrderFromActiveMods`, the
   "Current" sidecar (`CategoryStore`), and :2967 `SetOrder(SelectedModOrder)`.
3. User dividers in the UI: create (hotkey + context menu), F2 rename, delete (keep mods), collapse/expand
   (Space and +/−), context menu, screen-reader announcements, `SpeakActiveModOrder` sections.
4. Block drag, snapping D1-D7, auto-expand, `DestinationText`, header-sort rule D2.
5. "Convert placeholder dividers…" (opt-in).

---

## 14. Test plan

There is no test project in the repo (`find -name "*Test*.csproj"` finds nothing outside External).
Add `src/Tests/Core.Tests` (xunit, `net8.0-windows`, references Core). It runs on Windows only, because
Core uses WPF types.

**Unit tests (`OrderRowLayout`, `DivinityLoadOrder`):**
1. Round-trip: rows → `ToOrderDividers` → JSON → deserialize → `BuildRows` gives the same rows.
2. An old-format JSON (no `Dividers`) re-serializes byte-identical. A fork JSON deserialized as the
   **old** `DivinityLoadOrder` shape (a copy of master's class) gives the same `Order` and no error.
3. Anchor fallback: the `Before` mod is missing, so the divider goes at `Position`. `Before` is null, so
   the divider goes at the end. Two dividers sharing a `Before` keep their list order.
4. `CommitStructureChange`: Index is contiguous over exportable mods, -1 elsewhere, and unchanged by
   divider-crossing moves (§9.3).
5. `AdjustInsertIndex` table D1-D7, including index 0 with leading uncategorised mods, an empty
   category, and the collapsed target.
6. `ApplyCategoryVisibility`: with the filter on, collapse is suspended and empty dividers are hidden.
   With the filter off, children of collapsed dividers are hidden and the others visible. `Index` never
   changes.
7. `BuildOutputList` with an order list that includes a pure-override UUID: it is skipped (§5.2).

**Golden-file test (offline, in the app on Windows):**
- G1: with the 6.1 build and then the 6.2 build, on your real 935-mod "Current" order with no
  categories, export to a scratch profile folder and `fc /b` the two modsettings.lsx files. Expect them
  identical.
- G2: add 10 categories, collapse 3, export, and diff against G1's lsx. Expect identical, because
  categories don't change mod sequence. Then `findstr` every divider name and Id in the lsx and expect
  no hits.

**Manual UI checks (Debug build, real mod set):**
- Scroll and thumb-drag with 0 and with 20 dividers using `perfscroll`. Frame work should be within
  noise of 6.1, and the divider rows show no visual glitches after heavy recycling (no mod cells inside
  a divider, no divider look on a mod row).
- Theme toggle (Ctrl+L): divider colours follow.
- Drag a mod across a divider and check `#` doesn't change. Drag a whole collapsed category and check
  that all of its children moved. Drop onto a collapsed category and check it expands.
- Filter "a": collapsed categories show their matches, empty dividers hide, and clearing the filter
  restores the collapse state.
- Drag an eligible override out of Overrides: it gets a number and the Allow toggle shows "on". Drag
  it back: the number goes and the toggle shows "off". Pure overrides can't be dragged.
- Delete on a selected divider removes only the divider, and no file dialog appears.
- Screen reader (NVDA or Narrator): divider rows read name, count and state. The toggle is announced.
  Mod rows that recycled a divider container read the mod name. Ctrl+Home speaks the sections.
- Old build interop: open a fork-saved order in an unmodified 1.0.x build and check there are no
  missing-mod warnings and the same export.

**In-game (the only proof for 6.2/R7), each with the game booted to the main menu's Mod Manager and a
save loaded:**
- G1: the same mods in the same order as before 6.2.
- G2: a categorised export applies correctly.
- G3: an eligible override dragged into the order appears in the in-game Mod Manager at its position
  and works. This is the existing toggle behaviour, now reachable by drag.
- G4: overrides apply regardless of their position in the block (§6.1). This decides D1.
- G5: after placeholder conversion, behaviour is unchanged.

---

## 15. Owner decisions requested

- **D1:** Overrides block pinned at the end without numbers (recommended), or placeable and numbered
  as in the mock-up.
- **D2:** Header-click sort on the active list disabled while categories exist (recommended), or a view
  sort that hides dividers.
- **D3:** Whether a category's collapsed state is saved in the order JSON (recommended; it's written on
  save but doesn't mark the order dirty), or kept per machine only.
- **D4:** Whether "Convert placeholder dividers" deactivates the placeholder mods (recommended) or
  leaves them active under the new header.
