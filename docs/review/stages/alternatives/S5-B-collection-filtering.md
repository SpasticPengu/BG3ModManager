# Stage 5, angle B: real collection filtering for the mod lists

Author: design agent (S5-B), 2026-11-03. Base: master at fc35358. Compatible with perf/list-render (wt-perf).
Nothing here was built or run. This Linux container cannot build WPF. Every number marked *est.* is an
inference. Every `path:line` points at master unless it says wt-perf or gong.

---

## 0. Headline

Stop hiding rows by collapsing their containers. Filter the `ICollectionView` that the ListView already
sits on, so a hidden mod has no container at all. Keep `DivinityModData.Visibility` as the per-mod
"passes the filter" flag, but take it out of the row style.

Most of what the plan expected this change to break does not break:

- **Drag and drop:** `ModListDropHandler` uses `UnfilteredInsertIndex` only because it is a copy of gong's
  `DefaultDropHandler.Drop`. That property exists to map a filtered view index back to the source list.
  It needs one fix for the "no row under the cursor" case (§5.4).
- **ItemsSource:** stays `ActiveMods` / `InactiveMods`. So the reference checks (`TargetCollection == ActiveMods`),
  Alt+Arrow (`ItemsSource is ObservableCollectionExtended<…>`) and `IsSelected` all keep working.
- **Index mixing:** what does break is three places that mix view indices with source indices.
  `MoveSelectedMods`, Alt+Arrow's scroll and `FocusSelectedItem` need about 30 lines between them.

**Expected per-keystroke work after the change:** about 30–80 ms in Debug (*est.*). It no longer depends
on list size or match count. Today it is 1.24–3.27 s. With the throttle cut from 500 to 150 ms, the
search should feel close to live.

**When it is worth doing:** only if the probe in §3 shows the time goes into container realization or
layout, not into the loop. §4 has the exact gate. If 6.3's collapsible categories go ahead, the gate is
met regardless, because collapse needs this mechanism.

Rough effort: 2.5–4 dev-days for the upstreamable `perf/filter`, including Windows testing. Plus about
1 day of probing first. Fuzzy search is fork-only and separate: 2–3 days.

### Owner intentions gleaned (relayed request asked for this)

1. Stay on WPF master. Windows now, Linux kept open. Toolkit-neutral wherever it costs nothing.
2. Upstream-first hygiene:
   - perf fixes are clean PRs cut from master;
   - fork-only features (fuzzy, categories, overrides-in-order) live on separable branches.
3. Measure before changing. Numbers beat argument. Don't re-litigate measured hypotheses.
4. Make search feel instant first, then make it fuzzy with ranking. Keep `@author foo`.
5. Load order is sacred. Active-list order must never be changed by a view concern (search, sort, collapse).
6. Categories are flat, collapsible divider rows in the same list. Never WPF grouping, because of the
   `IsGrouping → CanContentScroll=False` landmine.
7. Keep the virtualization-safe selection fixes (309b81e, 396d7b2), untangled from TEMP code before any PR.
8. House rules:
   - ReactiveUI.Fody pin;
   - `ToUIPropertyImmediate` for OAPHs read by logic;
   - `AutoRefresh` before `Filter`;
   - no `string?`;
   - tabs, CRLF, BOM, Allman, `var`.
9. Use as many agents as needed, at medium or high effort.

---

## 1. How filtering works today

### Certain (read from source)

| Fact | Evidence |
|---|---|
| The filter is driven by a 500 ms `Throttle` on each text property, then `OnFilterTextChanged(text, list)` on the UI thread | `MainWindowViewModel.cs:5250-5254` |
| It walks only the list it was given (Active 935 or Inactive about 186), not all 1,121 mods | `:4199`, `:4204-4209`, `:4262-4285`, `:4289-4301` |
| Every mod gets `Visibility` assigned on every pass, even when the value is unchanged. Hidden mods also get `IsSelected = false` | `:4277-4283`, `:4293-4299` |
| The row container binds that flag: `<Setter Property="Visibility" Value="{Binding Visibility, …}"/>` | `MainResourceDictionary.xaml:369` (style `DivinityModDataListItem`, `:255`) |
| `ActiveMods` / `InactiveMods` are plain `ObservableCollectionExtended`, rebuilt imperatively, bound straight as `ItemsSource` | `MainWindowViewModel.cs:161-165`, `:2141-2147`; `HorizontalModLayout.xaml.cs:526-528` |
| Both lists virtualize with Recycling, `ScrollUnit` default (Item) | `HorizontalModLayout.xaml:119-120`, `:223-224` |
| The `IsSelected` setter refuses `true` while `Visibility != Visible` | `DivinityModData.cs:185-196` |
| The filter flag lives on the base class, defaults to Visible | `DivinityBaseModData.cs:69`, `:161` |
| Ctrl+A calls `ListView.SelectAll()`, which selects over `Items`. Today `Items` includes collapsed rows | `HorizontalModLayout.xaml.cs:97-101` |
| gong builds the drag payload from the ListView's `SelectedItems`, not from the model | gong 4.0.0 `DragInfo.cs:165-166` |
| Every drop re-runs the destination list's filter and does a full `CollectionView.Refresh()` 20 ms later | `ModListDropHandler.cs:236-258`, `HorizontalModLayout.xaml.cs:778-785` |
| The column-header sort already uses the same default `ICollectionView` (`SortDescriptions` + `Refresh`) | `HorizontalModLayout.xaml.cs:752-776` |
| `ModListDropHandler.Drop` is gong's `DefaultDropHandler.Drop`, copied and extended. gong's own `GetInsertIndex` uses `UnfilteredInsertIndex` too | `ModListDropHandler.cs:133-147` vs gong `DefaultDropHandler.cs:152-173` |
| `UnfilteredInsertIndex` maps view index to source index only when `Items.Count != ItemsSource.Count`, i.e. only under a CollectionView filter. Under today's visibility stamping the counts are equal, so it returns `InsertIndex` unchanged | gong `DropInfo.cs:55-88` |
| When no container is under the cursor, gong sets `InsertIndex = Items.Count` and leaves `itemParent` null, so `UnfilteredInsertIndex` does **not** map | gong `DropInfo.cs:367-371`, `:60-63` |
| The overrides list (`ForceLoadedModsListView`, `HideHeader`, `LinkedHeaderListView` → active) shares the row style but is never filtered | `HorizontalModLayout.xaml:133-140`; `OnFilterTextChanged` is only called with Active/Inactive (`:2146-2147`, `:5251`, `:5254`, `ModListDropHandler.cs:252,257`) |
| `ModListViewAutomationPeer` derives from `CachedAutomationPeer`, which walks the **visual** tree. So its cost scales with realized containers, collapsed ones included | `ModListViewAutomationPeer.cs:8`, `CachedAutomationPeer.cs:25-50,69-80` |
| The probe found a UIA client attached (`ClientsAreListening=True`). An outside client sometimes walked the tree for seconds | implementation-plan Stage 6.1 table ("Custom accessibility peers") |

### Measured (owner, Debug, UIA-driven, single runs)

| Action | rows | work after throttle |
|---|---|---|
| Inactive, broad "a" | ~186 | 3.27 s |
| Inactive, cleared | ~186 | 1.37 s |
| Inactive, no match "zzzzzz" | ~186 | 1.24 s |
| Active, broad "e" | 935 | 1.39 s |
| Active, cleared | 935 | 1.34 s |

### What the numbers imply

The cost does not scale with N. The 186-row list costs as much as the 935-row list, and its broad match
costs the most of all. The loop itself is at most 935 culture-aware `IndexOf` calls plus 935
`PropertyChanged` raises. That is single-digit milliseconds even in Debug (*est.*: about 1 µs per
compare, about 2–5 µs per ReactiveObject raise). So **the walk is not where the seconds go.** The plan's
§9 text names the walk itself as the cost. That is only half right: the walk *triggers* the cost.

### Inferred (to be confirmed by §3, ranked)

1. **The panel realizes collapsed containers.** A `VirtualizingStackPanel` keeps generating containers
   until the viewport is filled. Collapsed containers measure to 0 px, so a mostly-hidden list makes it
   generate a container for nearly every item. On "zzzzzz" that is every item; on clear, it has to
   re-measure and later virtualize them all away. Each one gets the full row style: a ContextMenu
   setter, `Tag` binding, `ScreenReaderHelperBehavior.Automatic`, which reflects per DataContext,
   `ScreenReaderHelperBehavior.cs:68-120`.
   This is standard WPF behaviour, and the reason CollectionView filtering is the usual advice, but it
   has not been measured in this app.
2. **The UIA client walks the enlarged visual tree.** The harness was a UIA client, and the 6.1 probe saw
   intermittent multi-second tree walks. A structure change after a filter pass invites a walk, and
   `CachedAutomationPeer` recurses through every realized container. Collapsed ones are still visual
   children. This may explain the 3.27 s outlier (single run, no variance known).
3. **Debug first-use costs** (JIT, first template instantiation). The order of the five measurements is
   not recorded.
4. **Layout cascades from Auto columns or extent changes.** Weakest; b123aff already removed one Auto column.

Collection filtering removes #1 by construction: hidden items are not in `Items`, so there is nothing to
realize. It shrinks #2 to about 28 rows. "Assign Visibility only when it changes" (plan step 1) only
helps when few flags change. It does nothing for the measured transitions ("" → "zzzzzz" changes all
186; clear changes all of them back).

### Present-day bugs that B fixes as a side effect (inferred, cheap to confirm, §10 T-S4 and T-S5)

- **Ctrl+A on a filtered list selects hidden mods in the ListView.** `SelectAll` works over `Items`,
  which includes collapsed rows. The model refuses them (`IsSelected` guard), but gong drags
  `SelectedItems`. So dragging after Ctrl+A moves hidden mods too. Shift+click range selection has the
  same problem.
- **The scrollbar extent counts hidden items** under item scrolling. A wheel notch can land on collapsed
  rows and appear to do nothing.

---

## 2. The approach in one picture

```
before:  ActiveMods (935) ──ItemsSource──> ListView.Items (935) ──> VSP realizes until viewport full
                                                                      (collapsed rows: 0 px, keep going)
after:   ActiveMods (935) ──ItemsSource──> default ListCollectionView.Filter(mod.Visibility==Visible)
                                            └─> ListView.Items (k matches) ──> VSP realizes ~28 rows
```

- The source collections, their identity and their order are untouched. So are `Index` and the
  modsettings export.
- The VM still decides matches in one place (`OnFilterTextChanged`). The view only reads the stamp.
  This keeps `@selected` a snapshot, exactly as today.
- The VM-to-view seam is the existing `IModViewLayout` (`HorizontalModLayout.xaml.cs:22-32`), so the VM
  never touches `ICollectionView`. The query parser and matcher move to Core with no WPF dependency.
  An Avalonia port can later apply the same predicate through DynamicData instead (§7).

---

## 3. Instrumentation: run on Windows before changing anything

TEMP branch `probe/filter`, cut from `perf/list-render`, which already has `PerfProbe`, per-ModListView
Measure/Arrange probes, Render-priority dispatcher op timing, and the `AutomationPeer.GetChildren`
counter (wt-perf `CachedAutomationPeer.cs:69-86`). Never PR it.

### 3.1 Probes to add

1. **`OnFilterTextChanged`** (`MainWindowViewModel.cs:4199`): stopwatch around the walk. Count
   `changed` (stamp actually differs), `hidden`, and `selectionCleared`. Then post an
   `ApplicationIdle` callback to take time-to-idle in-process, without the UIA harness. Log one line:

   ```csharp
   var probeStart = Util.PerfProbe.Begin();
   //... existing walk, counting changed/hidden/cleared ...
   Util.PerfProbe.End($"Filter walk {listName}", probeStart);
   var walkEnd = Stopwatch.GetTimestamp();
   Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
   {
   	var idleMs = (Stopwatch.GetTimestamp() - walkEnd) * 1000.0 / Stopwatch.Frequency;
   	var realized = Layout?.CountRealizedRows(modDataList) ?? default;
   	DivinityApp.Log($"[FilterProbe] list={listName} text='{searchText}' n={count} changed={changed} hidden={hidden} "
   		+ $"cleared={cleared} walk={walkMs:F1}ms toIdle={idleMs:F1}ms realized={realized.Total} collapsed={realized.Collapsed} "
   		+ $"uia={AutomationInteropProvider.ClientsAreListening}");
   });
   ```
2. **`ModListView`** (wt-perf version): override `GetContainerForItemOverride` (count "container
   created"), `PrepareContainerForItemOverride` (count "prepared"), `ClearContainerForItemOverride`
   (count "cleared"). Plus `CountRealizedRows()`:
   `this.FindVisualChildren<ListViewItem>()` → total and `Visibility != Visible`. This is the
   one number that decides inferred cause #1.
3. **`ScreenReaderHelperBehavior.Element_DataContextChanged`**: count and time it (its reflection runs
   once per container prepare).
4. **`PerfProbe.Dump($"filter:{scenario}")`** after each scenario's idle.
5. **Variant flags** (via the existing `perfprobe` file contents):
   - `cmpwrite` — angle A: assign `Visibility` only when it differs.
   - `viewfilter` — crude B: in `ModListView.PrepareContainerForItemOverride` call
     `BindingOperations.ClearBinding(container, VisibilityProperty)`. Install
     `CollectionViewSource.GetDefaultView(list).Filter = o => ((DivinityModData)o).Visibility == Visibility.Visible`
     once. End the walk with `view.Refresh()`.
   - `resetonly` — no filter change; the driver just calls `RefreshDataView` on the list. This is B's
     floor: the cost of one Reset plus re-realizing the viewport.
6. **`FilterDriver`** (copy of `PerfScrollDriver`, enabled by an empty `perffilter` file). After
   `OrderJustLoaded`, run each scenario 6 times and discard the first. Set
   `ViewModel.InactiveModFilterText` / `ActiveModFilterText` **in-process**: this keeps the throttle
   but removes the UIA client from the measurement. Wait for ApplicationIdle plus 250 ms quiet. Write
   a `[PerfProbe:filter]` summary (median and min per scenario), close.
   - Inactive: `"" → "a" → "" → "zzzzzz" → ""`
   - Active: `"" → "e" → "" → "zzzzzz" → ""`
   - Narrowing on active: `"c" → "co" → "com" → "comb"`
   - Field syntax: `"@author la"`
7. **Run matrix:** {baseline, cmpwrite, viewfilter, viewfilter+cmpwrite, resetonly}. Each run twice: once
   with no UIA client, once with the original UIA harness attached (reproduces the owner's numbers and
   isolates UIA's share). Debug, then one Release pass for absolutes. Alternate variants between runs,
   as the 6.1 work did.

### 3.2 What each outcome means

| Probe result | Meaning |
|---|---|
| `realized` ≫ ~28 (≈ N) right after no-match or clear, and Measure dominates `toIdle` | Cause #1 confirmed. B is the fix. A cannot remove it |
| `walk` ≥ 50% of `toIdle` | Loop or notification bound. A plus cached keys is enough. B is optional |
| `GetChildren` total ≥ 30% of the UIA-attached run | Cause #2. Fix `CachedAutomationPeer` separately (Stage 6 item). B shrinks it proportionally |
| `viewfilter` `toIdle` ≈ `resetonly` `toIdle` | Confirms B's cost is just one Reset of the viewport, i.e. the §8 estimate |
| `viewfilter` selection preserved across Reset (T-S2 manual) | B1 as designed. Otherwise add the capture/restore in §5.3 |

---

## 4. When B is worth doing (the gate)

Do B (PR `perf/filter` steps 3–6) if **any** of these holds on the probe build:

- **G1:** max realized containers after a no-match or clear pass is more than twice the visible rows
  (about 56 on the owner's screen), and the ModListView Measure+Arrange share of in-process `toIdle`
  is at least 50%.
- **G2:** with `cmpwrite` applied, any scenario's in-process median is still above 150 ms (Debug) or
  80 ms (Release).
- **G3:** the hidden-selection repro (T-S4) reproduces, and the owner wants it fixed structurally rather
  than with a drag-time guard.
- **G4:** Stage 6.3 collapsible categories are going ahead. Collapse is the same "hide rows without
  moving them" problem. Doing it with `Visibility` would bring back cause #1 every time the user scrolls
  past a collapsed block (*est.*).

**Skip B** if G1 is false and `cmpwrite` plus cached keys bring every scenario under about 100 ms
in-process, and 6.3 is not scheduled. Then the plan's "only if still needed" stands.

---

## 5. The design (B1), upstreamable

Branch `perf/filter`, cut from master. Each commit builds on its own.

### 5.1 Core: extract the query (pure refactor, toolkit-neutral)

New file `src/Core/Models/ModFilterQuery.cs`. It does the same parsing as `MainWindowViewModel.cs:4211-4260`
and the same match semantics as `:4262-4301`, including the untrimmed plain-text path, AND across
fields, and match-all when no fields parse.

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace DivinityModManager.Models;

public sealed class ModFilterQuery
{
	private static readonly Regex _propertyPattern = new("@([^\\s]+?)([\\s]+)([^@\\s]*)", RegexOptions.Compiled);
	private static readonly Regex _propertyPatternWithQuotes = new("@([^\\s]+?)([\\s\"]+)([^@\"]*)", RegexOptions.Compiled);

	public static readonly ModFilterQuery Empty = new(null, new List<DivinityModFilterData>(), true);

	private readonly string _plainText;
	private readonly List<DivinityModFilterData> _fields;

	public bool IsEmpty { get; }

	private ModFilterQuery(string plainText, List<DivinityModFilterData> fields, bool isEmpty)
	{
		_plainText = plainText;
		_fields = fields;
		IsEmpty = isEmpty;
	}

	public static ModFilterQuery Parse(string searchText)
	{
		if (String.IsNullOrWhiteSpace(searchText)) return Empty;
		if (searchText.IndexOf('@') < 0) return new ModFilterQuery(searchText, null, false);

		var fields = new List<DivinityModFilterData>();
		var remaining = searchText;
		var matches = searchText.IndexOf('"') > -1 ? _propertyPatternWithQuotes.Matches(searchText) : _propertyPattern.Matches(searchText);
		foreach (Match match in matches)
		{
			if (!match.Success) continue;
			var prop = match.Groups[1]?.Value;
			var value = match.Groups[3]?.Value ?? "";
			if (!String.IsNullOrWhiteSpace(prop))
			{
				fields.Add(new DivinityModFilterData() { FilterProperty = prop, FilterValue = value });
				remaining = remaining.Replace(match.Value, "");
			}
		}
		remaining = remaining.Replace("\"", "");
		if (!String.IsNullOrWhiteSpace(remaining) && !fields.Any(f => f.PropertyContains("Name")))
		{
			fields.Add(new DivinityModFilterData() { FilterProperty = "Name", FilterValue = remaining.Trim() });
		}
		return new ModFilterQuery(null, fields, false);
	}

	public bool IsMatch(DivinityModData mod)
	{
		if (IsEmpty) return true;
		if (_plainText != null)
		{
			return CultureInfo.CurrentCulture.CompareInfo.IndexOf(mod.Name, _plainText, CompareOptions.IgnoreCase) >= 0;
		}
		foreach (var f in _fields)
		{
			if (!f.Match(mod)) return false;
		}
		return true;
	}
}
```

`MainWindowViewModel.cs:4172-4173` (the two instance regexes) are deleted. The `@` syntax is unchanged.
`DivinityModFilterData.Match` (`DivinityModFilterData.cs:32-112`) is reused as is.

### 5.2 VM: stamp only on change, then ask the view to refresh

Replaces `MainWindowViewModel.cs:4199-4314`.

```csharp
public void OnFilterTextChanged(string searchText, IEnumerable<DivinityModData> modDataList)
{
	var query = ModFilterQuery.Parse(searchText);
	var totalHidden = 0;
	var totalChanged = 0;

	foreach (var mod in modDataList)
	{
		var visibility = query.IsMatch(mod) ? Visibility.Visible : Visibility.Collapsed;
		if (visibility != Visibility.Visible)
		{
			totalHidden++;
			if (mod.IsSelected) mod.IsSelected = false;
		}
		if (mod.Visibility != visibility)
		{
			mod.Visibility = visibility;
			totalChanged++;
		}
	}

	if (modDataList == ActiveMods)
	{
		TotalActiveModsHidden = totalHidden;
	}
	else if (modDataList == InactiveMods)
	{
		TotalInactiveModsHidden = totalHidden;
	}

	//Hidden mods are removed from the list's collection view rather than collapsed in place,
	//so the virtualizing panel never realizes rows for them. Nothing to do if no stamp moved.
	if (totalChanged > 0)
	{
		Layout?.RefreshFilter(modDataList);
	}
}
```

`IModViewLayout` gains `void RefreshFilter(IEnumerable<DivinityModData> list);`
(`HorizontalModLayout.xaml.cs:22-32`).

`Layout` is null during the first `LoadModOrder` before the view activates (`:524` sets it). That is
safe because §5.3 refreshes once when it installs the filter.

Throttle (`:5250-5254`), as its own commit after re-measuring:

```csharp
this.WhenAnyValue(x => x.ActiveModFilterText).Throttle(TimeSpan.FromMilliseconds(150)).DistinctUntilChanged()
	.ObserveOn(RxApp.MainThreadScheduler).Subscribe(s => OnFilterTextChanged(s, ActiveMods));
```

### 5.3 View: filter the default view, and keep the scroll anchor

`MainResourceDictionary.xaml:369`: **delete** the `Visibility` setter. This also covers the overrides
list, which shares the style and was never filtered.

`HorizontalModLayout.xaml.cs`, inside `WhenActivated` after the `ItemsSource` binds (`:526-528`):

```csharp
InstallModFilter(ViewModel.ActiveMods);
InstallModFilter(ViewModel.InactiveMods);
```

```csharp
private static bool PassesModFilter(object item)
{
	//Dividers (Stage 6.3) will add their own case here; anything unknown stays visible.
	return item is not DivinityModData mod || mod.Visibility == Visibility.Visible;
}

private static void InstallModFilter(IEnumerable source)
{
	//The same default view the ListView uses for Items, and that Sort() already uses.
	var view = CollectionViewSource.GetDefaultView(source);
	if (view != null && view.CanFilter && view.Filter == null)
	{
		view.Filter = PassesModFilter;
	}
}

private ModListView GetListViewForList(IEnumerable<DivinityModData> list)
{
	if (list == ViewModel.ActiveMods) return ActiveModsListView;
	if (list == ViewModel.InactiveMods) return InactiveModsListView;
	return null;
}

public void RefreshFilter(IEnumerable<DivinityModData> list)
{
	var listView = GetListViewForList(list);
	if (listView == null) return;
	var view = CollectionViewSource.GetDefaultView(listView.ItemsSource);
	if (view == null) return;

	var anchor = listView.SelectedItem ?? GetFirstVisibleItem(listView);
	view.Refresh();
	if (anchor != null && listView.Items.Contains(anchor))
	{
		listView.ScrollIntoView(anchor);
	}
	else
	{
		ScrollToTop(listView);
	}
}

private static object GetFirstVisibleItem(ModListView listView)
{
	//ScrollUnit is Item (the default; HorizontalModLayout.xaml sets none), so VerticalOffset is an item index.
	//If Stage 6 switches to ScrollUnit="Pixel", replace this with a hit test at the top of the viewport.
	if (listView.GetVisualDescendent<ScrollViewer>() is ScrollViewer sv && listView.Items.Count > 0)
	{
		var index = Math.Clamp((int)sv.VerticalOffset, 0, listView.Items.Count - 1);
		return listView.Items[index];
	}
	return null;
}
```

**Selection across `Refresh()`** (*expected*, verify T-S2). WPF's `Selector` handles a Reset by
re-locating selected items: those still in `Items` stay selected, filtered-out ones are removed and
reported in `SelectionChanged`'s `RemovedItems`.
- On master, `UpdateIsSelected` replays that delta (`:212-237`).
- On wt-perf, the reconcile reads `SelectedItems` (wt-perf `HorizontalModLayout.xaml.cs:213-250`).

Either way the model ends up matching. If T-S2 shows the selection is wiped on Reset, add a
capture/restore around `view.Refresh()` in `RefreshFilter`:
`var keep = list.Where(x => x.IsSelected).ToList(); … listView.SetSelection(keep);`
using wt-perf's `ModListView.SetSelection` (wt-perf `ModListView.cs:202`). That fallback belongs after
6.1's selection commits land, so `perf/filter` doesn't duplicate `SetSelection`.

### 5.4 Drag and drop: resolve the insert index against the source list

`ModListDropHandler.cs:133` currently reads `dropInfo.UnfilteredInsertIndex`. Replace it with:

```csharp
var insertIndex = ResolveSourceInsertIndex(dropInfo, dropInfo.TargetCollection.TryGetList());
```

```csharp
/// Maps gong's view-relative drop position onto the unfiltered list.
/// Keeps today's placement: dropping on the lower half of a visible row inserts directly after it,
/// on the upper half directly before it, even when hidden mods sit next to it in the source list.
private static int ResolveSourceInsertIndex(IDropInfo dropInfo, IList destinationList)
{
	if (destinationList == null || dropInfo is ManualDropInfo) return dropInfo.UnfilteredInsertIndex;

	if (dropInfo.TargetItem is DivinityModData targetMod)
	{
		var targetIndex = destinationList.IndexOf(targetMod);
		if (targetIndex > -1)
		{
			return dropInfo.InsertPosition.HasFlag(RelativeInsertPosition.AfterTargetItem) ? targetIndex + 1 : targetIndex;
		}
	}

	if (dropInfo.VisualTarget is ItemsControl itemsControl)
	{
		var items = itemsControl.Items;
		var viewIndex = dropInfo.InsertIndex;
		if (items.Count == 0)
		{
			//Everything filtered out: gong reports index 0 and cannot map it. Append, as an unfiltered list would.
			return destinationList.Count;
		}
		if (viewIndex >= 0 && viewIndex < items.Count)
		{
			return destinationList.IndexOf(items[viewIndex]);
		}
		return destinationList.IndexOf(items[items.Count - 1]) + 1;
	}
	return dropInfo.UnfilteredInsertIndex;
}
```

Why not just trust gong:
- With an empty filtered view, gong's `UnfilteredInsertIndex` is 0. That would drop the mods at the
  **top of the load order** (gong `DropInfo.cs:367-371` + `:60-63`).
- Gong maps "lower half of A" to "before the next visible row". That silently moves the dropped mods
  past any hidden mods between A and the next visible row.

The rest of `Drop` is unchanged and correct with source indices: the same-list
`index < insertIndex → --insertIndex` adjustment (`:157-165`), the renumber (`:218-221`), the model
selection (`:223-234`), and the destination re-filter (`:250-258`). The renumber loop is O(n²)
(`IndexOf` per mod, about 437k compares). Swapping it for `for (var i = 0; i < count; i++) ActiveMods[i].Index = i;`
is a free side commit.

Optional, low priority: also re-run the *source* list's filter pass so its "N Hidden" label stops being
stale after a drag out of a filtered list. That staleness exists today.

### 5.5 Keyboard paths that mix view and source indices

- **`MoveSelectedMods`** (`HorizontalModLayout.xaml.cs:295-402`).
  - **Insert position:** the target list's `SelectedIndex` is passed as a *source* index
    (`:307`, `:359` → `ManualDropInfo.UnfilteredInsertIndex`, `ModListDropHandler.cs:57`). Pass
    `ToSourceIndex(InactiveModsListView, ViewModel.InactiveMods)` instead:
    `listView.SelectedItem is DivinityModData m ? source.IndexOf(m) : -1`. Here -1 keeps today's
    "nothing selected → clamp to 0" (`ModListDropHandler.cs:172-175`).
  - **Re-select after the move:** `nextSelectedIndex` is a source index (`:304`, `:356`) assigned to
    `SelectedIndex`, a view index (`:345`, `:398`). Use `ActiveModsListView.Items.IndexOf(selectedMod)`
    and clamp against `Items.Count`. The inactive branch already clamps against the wrong collection
    (`:393`).
- **Alt+Up/Down reorder** (`ModListView.cs:228-291`). The moves are source-index moves on `ItemsSource`,
  which is still the `ObservableCollectionExtended`, so the type check at `:228` holds. But
  `Items.GetItemAt(targetScrollIndex)` (`:286`) indexes the *view* with a source index. Under a filter it
  picks the wrong row or throws `ArgumentOutOfRangeException`. Use `ScrollIntoView(list[targetScrollIndex])`.
  - Semantics stay "step one source position", as today. When the neighbour is hidden the row seems not
    to move.
  - Stepping over hidden neighbours (move before the previous *visible* row) is a behaviour change.
    It is listed in §11 as an owner decision, not part of the upstream PR.
- **`FocusSelectedItem`** (`:60-85`): `lv.Items.GetItemAt(0)` throws on an empty filtered view. The
  try/catch only logs it. Guard with `lv.Items.Count > 0`.
- **MoveFocusLeft/Right** set `SelectedIndex = 0` (`:579-598`). This now means "first *visible* mod".
  Today index 0 can be a collapsed row, which is a fix in passing.
- **Arrow keys, PageUp/Down, Home/End, Shift+range and Ctrl+A** now operate only on matching rows.
  This is correct by construction and removes the T-S4 bug class.

---

## 6. Optional B2: incremental instead of Reset

Do this only if the probe shows B1's Reset floor (`resetonly`) above about 50 ms, or users notice focus
or scroll jumps while typing.

- **Option 1, `ListCollectionView.IsLiveFiltering = true` with `LiveFilteringProperties = { "Visibility" }`.**
  WPF then adds and removes only the rows whose stamp changed. Narrowing keystrokes cost a handful of
  Add/Remove events instead of a viewport rebuild.
  - Risks (*inferred*, must be tested): a live-shaping binding per item, rebuilt on every `AddRange`; and
    whether re-admitted items keep source order in an unsorted view.
  - Gate it behind T-B2.
- **Option 2, a VM-owned filtered `ObservableCollectionExtended` maintained by an O(N) two-pointer diff.**
  Granular Insert/Remove, Reset above a threshold.
  - This changes `ItemsSource`, so it inherits every §7 breakage.
  - Not recommended on WPF.

---

## 7. Considered and rejected for WPF master: DynamicData `Filter` + `Bind`

`ActiveMods.ToObservableChangeSet().Filter(predicate$).Bind(out _activeModsView)`, using the
reactive-predicate overload so no `AutoRefresh` is needed. The `ItemsSource` becomes a read-only
filtered collection, and that breaks a lot.

- **Drop.** `dropInfo.TargetCollection` and `DragInfo.SourceCollection` become the read-only collection
  (gong `DropInfo.cs:272`, `DragInfo.cs:140`).
  - `isActive = TargetCollection == ActiveMods` (`ModListDropHandler.cs:116`) goes false.
  - `TryGetList()` returns a read-only list, so `RemoveAt` and `Insert` (`:160`, `:192`) throw `NotSupportedException`.
  - The whole handler would need a filtered-to-backing map.
- **Alt+Arrow** silently stops working: `ItemsSource is ObservableCollectionExtended<DivinityModData>`
  (`ModListView.cs:228`).
- **Selection plumbing.** `UpdateViewSelection` / `SelectMods` / `MoveSelectedMods` compare list
  identity with `ViewModel.ActiveMods` (`HorizontalModLayout.xaml.cs:137-147`, `:299`, `:351`).
- **Two collections to keep coherent.** Plus DynamicData's handling of `Move` through a filter, which
  Alt+Arrow and drop-reorder depend on. That needs its own verification.

It would be toolkit-neutral (Avalonia's `ListBox` has no CollectionView filter), so it is the right shape
for an Avalonia-era port. Because §5 keeps the predicate and query in Core and the application of it in
the view, that port only swaps §5.3.

---

## 8. Expected per-keystroke cost (in-process, after the throttle)

The *est.* figures come from 6.1's measured per-row costs: about 28 rows realized, name cell 21 µs, other
text cells 70–90 µs, panel measure about 8.5 ms per 25-row frame. Fresh-container template cost after a
Reset is **unmeasured**, guessed at 0.5–1.5 ms per row in Debug.

| Step | no match | broad match | clear | narrowing keystroke |
|---|---|---|---|---|
| Today (measured, UIA-attached) | 1.24 s | 1.39–3.27 s | 1.34–1.37 s | (not measured) |
| A only: stamp on change (`cmpwrite`) | ≈ today (all stamps change) | small gain | ≈ today | large gain (few stamps change) |
| **B1: collection filter + Reset** | **30–80 ms** *est.* | **30–80 ms** | **30–80 ms** | 30–80 ms, 0 if no stamp changed |
| B1 with throttle cut 500 → 150 ms | perceived ≈ 0.2 s | ≈ 0.2 s | ≈ 0.2 s | ≈ 0.2 s |
| B2 live filtering (optional) | ≈ B1 | ≈ B1 | ≈ B1 | 5–15 ms *est.* |
| Fuzzy on top (fork) | +2–5 ms scoring; inactive `CustomSort` < 1 ms | | | |

The walk itself in B1: about 1–3 ms (*est.*). That is 935 `CompareInfo.IndexOf` calls plus
`PropertyChanged` only for changed stamps, and the row style no longer binds `Visibility`.

---

## 9. Interactions

- **wt-perf selection fixes (309b81e, 396d7b2).** Compatible, and more reliable under B.
  - The reconcile reads `SelectedItems`, which can no longer contain hidden mods. Today Ctrl+A can put
    them there.
  - `SelectMods`' `x.Visibility == Visibility.Visible` filter (wt-perf `HorizontalModLayout.xaml.cs:208`)
    still means "in the view", because the stamp is kept.
  - Textually, `perf/filter` touches `MoveSelectedMods`, `FocusSelectedItem`, `WhenActivated` and a new
    `RefreshFilter`. wt-perf touches `SelectMods` / `UpdateIsSelected` / `GetListViewFor` and adds
    `SetSelection`. Those are separate hunks. Only the TEMP probe lines in wt-perf's `UpdateIsSelected`
    need hand-resolving, and that is already required (plan 6.1 note).
- **`IsSelected` guard** (`DivinityModData.cs:190`). Kept unchanged. It is now a second line of defence
  rather than the only one.
- **Index renumbering.** Unaffected: always over the source list. The "#" column still shows load-order
  positions with gaps under a filter, as today.
- **Column-header sort.** `SortDescriptions` and `Filter` coexist on the same `ListCollectionView`.
  `ResolveSourceInsertIndex` maps by item, so it is also correct for a sorted view. Today gong's mapping
  is wrong when sorted, because the counts are equal and it returns the view index.
- **Overrides list.** Unchanged and unfiltered. It mirrors the active columns via `LinkedHeaderListView`
  (`ModListView.cs:37-114`), which is untouched. Filtering it too would now be one `InstallModFilter`
  call plus a walk; that is an owner decision (§11), and moot once 6.2 folds overrides into the order.
- **Accessibility.** The UIA tree shrinks from "every realized container" to about 28 rows.
  `ModListViewAutomationPeer.HasNullChildElement` iterates the filtered `Items`. The "N Matched, M Hidden"
  labels are unchanged (`MainWindowViewModel.cs:4178-4197`, `:5266-5271`).
- **`@` field syntax.** The same parser, now `ModFilterQuery.Parse`. Matching still happens once, in the
  walk. The view only reads the stamp, so `@selected` stays a snapshot taken at filter time. Re-running
  the query inside `Refresh()` would have made it depend on selection changes made by a drop.
- **Stage 6.3 dividers and collapse.** B is the enabling mechanism.
  - **Predicate:** `PassesModFilter` gains a divider case. The walk is in source order, so one pass also
    computes, per divider, its matched-child count and whether it is collapsed. A divider shows when
    the text is empty or it has a match. A mod shows when it matches and its category is not collapsed.
    The "3" / "2 collapsed" badges in the plan's mock come free from the same pass.
  - **Collapse:** flip a flag on the divider, then `RefreshFilter`. Indices do not shift because the
    source is untouched, which is the 6.3 requirement.
  - **Drop:** onto a collapsed divider, `ResolveSourceInsertIndex` gets a divider case. Lower half
    inserts after the category's last (hidden) child, upper half before the divider.
  - **Renumber:** must skip dividers.
  - **Element type:** `ActiveMods`' element type becomes a common base (e.g. `IModListEntry`). That
    breaks `ModListView.cs:228`'s type check. That is 6.3 work, but nothing in B blocks it.
  - **Never** add `GroupDescriptions` to these views (`MainResourceDictionary.xaml:460-461`).

---

## 10. Fork-only: fuzzy search (`feat/fuzzy-search`, branched from `perf/filter`)

- **Match key.** `DivinityModData.SearchKey`: a lowercase-invariant copy of `Name`, computed lazily and
  re-computed only when the `Name` reference changes. No subscription, no `[Reactive]`. This is the
  plan's "precompute once at load" with no load-time cost.
- **Scorer.** Core `FuzzyMatcher.Score(string query, string key)`: an fzf-v1-style greedy subsequence
  match with bonuses for word start, consecutive run and prefix. O(|key|). Returns -1 for no match.
- **Query.** `ModFilterQuery` gets a `MatchMode` (Substring / Fuzzy, a setting).
  - Fuzzy applies to the free-text Name term (and `@name`).
  - Other `@` fields stay substring.
  - `IsMatch` becomes `TryScore(mod, out int score)`.
- **Inactive list ranks.** While a query is active, the inactive default view gets
  `ListCollectionView.CustomSort` = score descending, then `DisplayName`. Scores live in a
  `Dictionary<DivinityModData, int>` owned by the inactive filter state, not on the model.
  Clearing the query restores the header sort the user had (`HorizontalModLayout.xaml.cs:705-706` keeps it).
  Inactive order has no load-order meaning, so ranking is safe.
- **Active list never reorders.** Recommendation: fuzzy *include* with a stricter threshold, keeping
  load order, under the same setting.
  - Reordering by score would make drag/drop and Alt+Arrow operate on a misleading order.
  - `ResolveSourceInsertIndex` maps by item, so it would stay correct, but the user would not.
- **Cost.** About 2–5 µs per mod (*est.*), so 2–5 ms for 1,121. The sort is O(k log k) on 186 or fewer.

---

## 11. Branches, PRs, effort, and owner decisions

| Branch | Contents | Upstream? | Effort |
|---|---|---|---|
| `probe/filter` (TEMP, from `perf/list-render`) | §3 probes, `FilterDriver`, variants `cmpwrite` / `viewfilter` / `resetonly` | never | 0.5–1 d + a measurement session |
| `perf/filter` (from master) | c1 `ModFilterQuery` extraction · c2 stamp only on change · c3 collection-view filter + remove style setter + `RefreshFilter` · c4 `ResolveSourceInsertIndex` · c5 view/source index fixes (`MoveSelectedMods`, Alt+Arrow scroll, `FocusSelectedItem`) · c6 O(n) renumber · c7 throttle 150 ms + `DistinctUntilChanged` (after re-measure) | yes (c1–c2 are angle A and stand alone if the gate says stop) | 2.5–4 d incl. Windows testing |
| `perf/filter-live` (optional) | B2 option 1 | yes, if T-B2 passes | ~1 d |
| `feat/fuzzy-search` (from `perf/filter`) | §10 | fork-only | 2–3 d |
| `feat/categories` (6.3) | divider case in predicate, walk, drop and renumber | fork-only | (6.3's own estimate) |

Merge order: the 6.1 selection commits (untangled) and `perf/filter` can land in either order. Rebase
the later one; expect no semantic conflict.

Owner decisions needed:
1. Alt+Arrow in a filtered list: step one source position (today, kept in the PR) or over hidden
   neighbours (fork option)?
2. Should the active-list search also filter the overrides list?
3. Fuzzy on the active list: include-only with a threshold (recommended) or substring only?
4. After clearing a filter, scroll to the selected mod (as designed) or keep the top row?

---

## 12. Test plan (Windows; there is no test project in the repo)

**Unit** (new fork-only `tests/Core.Tests`, xUnit. `net8.0-windows` because Core is `UseWPF`):
- `ModFilterQuery` parity table against a copy of today's loop: plain (incl. trailing space), `@author x`,
  `@author "two words"`, multiple fields (AND), `@` with nothing, `@selected`, `@modified 2024-01-01`,
  unknown field.
- Fuzzy scorer ordering cases.

**Perf** (probe build, §3): every scenario, median of 5, Debug and Release, with and without UIA.
- Pass if B1's in-process `toIdle` ≤ 100 ms Debug for no-match, broad and clear on both lists.
- Realized containers ≤ visible rows + cache after every pass.

**Manual and functional** (Debug build, real 1,121-mod setup, modsettings.lsx diffed before and after
every session):

| ID | Steps | Expect |
|---|---|---|
| T-F1 | Type "a", "zzzzzz", clear, on each list | Correct match counts and labels; hidden mods absent (not blank rows); scrollbar length tracks matches |
| T-F2 | `@author <x>`, `@uuid <part>`, `@selected` after selecting 3 | Same results as master |
| T-S1 | Select 5 visible, type a filter hiding 2 | 3 stay selected; "3 Selected"; hidden 2 deselected in the model |
| T-S2 | Select 50 (beyond the viewport), filter keeping all 50, clear | Selection survives both Resets (decides the §5.3 fallback) |
| T-S3 (wt-perf) | Repro from 309b81e/396d7b2: large selection, move between lists, click one mod | Single selection, no stale highlights |
| T-S4 | **On master first:** filter active to 3 matches, Ctrl+A, drag to inactive | Master (expected bug): hidden mods move too. B: exactly 3 move |
| T-S5 | Shift+click range across hidden rows | B: only visible rows selected |
| T-D1 | Filter active; drag a mod onto the lower half of visible A where hidden H follows A | Lands directly after A, before H (export order checked) |
| T-D2 | Upper half of visible B | Directly before B |
| T-D3 | Active filtered to no matches; drag a mod in | Appended at the **end**, not index 0 |
| T-D4 | Drop below the last visible row | After the last visible row |
| T-D5 | Reorder within a filtered active list (move up past hidden mods) | Index contiguous 0..n-1; export matches |
| T-D6 | Column-header sort on inactive, then drop | Lands at the visual position |
| T-K1 | Enter / Alt+Right to move selection between filtered lists | Inserted before the target's selected mod; re-selection lands on a visible row |
| T-K2 | Alt+Up/Down in a filtered active list | No exception; scrolls to the moved mod |
| T-K3 | Arrows, PageUp/Down, Home/End, focus-swap hotkeys with 0, 1 and many matches | No exceptions in log; focus visible |
| T-A1 | Narrator on: filter, read list | Reads only matches; Matched/Hidden label live |
| T-O1 | Overrides list with active filter set | Unaffected, columns stay linked |
| T-B2 (only for B2) | Live filtering: filter, clear, with a reordered active list | Re-admitted rows keep source order |
