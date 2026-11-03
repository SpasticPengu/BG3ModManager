# Stage 5 (Perf B: filter and search): design, angle A, cheapest first

Written 2026-11-03 against master `fc35358`, with `perf/list-render` (wt-perf) and gong-wpf-dragdrop's
`DropInfo.cs` (copy in `scratchpad/dl-gong`; the copy's version is unconfirmed, the csproj pins 4.0.0).
Nothing here was built or run. This Linux container cannot run WPF. Every claim is labelled:
**[certain]** means it follows from source cited path:line. **[WPF]** means it follows from WPF
framework behaviour I know but could not re-read here. **[inferred]** means a hypothesis that the
Step 0 instrumentation has to confirm. All paths are relative to the repo root unless they are given in full.
The `reports/` folder was empty both when I started and when I finished, so no review report is used.

---

## 0. Headline

1. **The plan's first two steps win roughly nothing. The real fix is a collection-view filter, and it is the cheap version of step 5.**
   - "Only assign Visibility when it changes": the setter already skips equal values
     ([certain], §2.1). In the no-match and cleared cases every row really does change, so a guard cannot help.
   - "Hoist per-keystroke allocations": the plain-text path allocates nothing per mod ([certain], §2.2).
     The `@` path allocates a little. Either way it is a few milliseconds out of 1,200–3,300.
2. **[inferred, high confidence]** The time goes into WPF, not `OnFilterTextChanged`. The items stay in
   the ItemsControl and are only collapsed, and a collapsed container is 0 px tall. So the virtualizing
   panel keeps generating containers until it fills the viewport, and under a narrow filter that means
   *every* row in the list is realized. The plan's own Stage 6 data adds a second multiplier: any UI
   Automation client walks *all realized containers*. The filter latency was measured *by a UI
   Automation client* (§2.4).
3. **The fix:** set `ICollectionView.Filter` on the `ListCollectionView` WPF already creates over
   `ActiveMods`/`InactiveMods`. Keep `DivinityModData.Visibility` as the "matches filter" flag. Call
   `Refresh()` only when at least one flag flipped, and remove the row's `Visibility` binding.
   - `ItemsSource` stays `ActiveMods`/`InactiveMods`, so drag/drop keeps working. Gong already maps
     filtered-view indices back to source indices ([certain], §5.1).
   - It needs four small index fixes in the view (§3, B3), and it does not need DynamicData.
   - This is the plan's "step 5", but at about 1/5th of the invasiveness the plan feared.
4. **Expected per-keystroke work after B plus a 150 ms throttle** [inferred]: **~20–60 ms (Debug)**,
   about the same for any list size or match count. Felt settle time goes from **1.7–3.8 s** to
   **~0.2 s**.

---

## 1. Owner intentions this design is built on (gleaned from CLAUDE.md, the plan and the task)

- Stay on the legacy WPF `master` and do not switch to upstream's Avalonia rewrite. Windows comes first;
  keep Linux possible, and stay toolkit-neutral where that costs nothing.
- Keep fixes **upstreamable** to LaughingLeader: cut each one from clean master as one focused PR.
  Fork-only features (fuzzy search, categories, UI refresh, multi-order export) live on separate branches.
- **Measure first**: settle questions with numbers rather than argument, and record theories you
  eliminate as eliminated. The plan already re-scoped Stage 5 twice on evidence.
- Search should feel **live**, and then become **fuzzy and ranked**. Keep the `@author foo` power
  syntax. Do not reorder the active list, because its order is the load order.
- The list workstream (Stage 6) must not be undermined. That covers smooth scrolling, the
  `CachedTextBlock` work and the selection fixes (309b81e/396d7b2), and later categories as **flat,
  collapsible divider rows** (no WPF grouping, because of the `IsGrouping` → `CanContentScroll=False` trap).
- Keep accessibility working (screen-reader checks are on the owner's verify list), and follow house
  style and the ReactiveUI.Fody pin.
- On process: this run was explicitly allowed as many subagents as needed, at medium or high effort.

---

## 2. Diagnosis: where 1.2–3.3 s per keystroke can and cannot go

### 2.1 The setters: "only assign when changed" is already true [certain]

- `DivinityModData.Visibility` is declared on the base class as `[Reactive] public Visibility Visibility { get; set; }`
  (`src/Core/Models/DivinityBaseModData.cs:69`). The class is a `ReactiveObject` (`:16`), and Core has
  Fody weaving (CLAUDE.md §4). ReactiveUI.Fody weaves the setter into `RaiseAndSetIfChanged`, which
  compares with `EqualityComparer<T>.Default` and returns without notifying when the value is equal.
- `IsSelected` is a hand-written setter that also ends in `this.RaiseAndSetIfChanged(ref isSelected, value)`
  (`src/Core/Models/DivinityModData.cs:185-196`). It forces `value=false` while `Visibility != Visible` (`:190`).
- `OnFilterTextChanged` therefore raises `PropertyChanged` **only for mods whose state actually flips**.
  Re-typing a query that hides the same set already costs ~nothing in the VM.
- In the measured worst cases the flips are real. "zzzzzz" collapses all ~186 inactive rows; "cleared"
  restores them. A compare-before-write guard cannot remove a single notification.
- **Verdict:** drop this as a perf step. Comparing before writing is still worth doing, but for a
  different reason: it **counts flips**, so we can skip the view refresh when nothing changed (B2).

### 2.2 The VM loop is cheap [certain on allocations; inferred on timing]

`OnFilterTextChanged` (`src/GUI/ViewModels/MainWindowViewModel.cs:4199-4313`):

- **Plain path (`:4289-4301`):** `CultureInfo.CurrentCulture.CompareInfo.IndexOf(m.Name, searchText, IgnoreCase)`
  for each mod in **one** list (it is passed `ActiveMods` or `InactiveMods`, `:5250-5254`). It does not
  walk all 1,121 mods, and it allocates nothing per mod.
  - [inferred] .NET 8 on Windows uses ICU. For ASCII inputs in an en-* culture it takes a managed
    ordinal-ignore-case fast path, roughly ≤1 µs per call; non-ASCII names go through ICU at roughly
    10–50 µs. Worst case for 935 mods is about **1–40 ms**.
- **`@` path (`:4212-4287`):**
  - Once per pass: one regex `Matches`, plus a `List<DivinityModFilterData>` built from it. The regexes
    are instance fields constructed once (`:4172-4173`), not per keystroke.
  - Per mod and per term: `DivinityModFilterData.Match` (`src/Core/Models/DivinityModFilterData.cs:32-111`).
    This runs up to 13 `PropertyContains` culture compares on the *property name*, repeated for every
    mod even though the answer never changes within a pass.
  - Per mod for `@depend`: `mod.Dependencies.Items` (`:48`). [inferred] This is a DynamicData snapshot
    copy, so it allocates.
  - Per mod for `@modified`/`@updated`: `DateTime.TryParse(FilterValue)` (`:96`).
  - [inferred] All of this totals **≤5–10 ms** for 935 mods.
- **Per-flip fan-out [inferred]:** each `PropertyChanged` on a mod reaches:
  - ~40–50 `WhenAnyValue` self-subscriptions in the ctor (`DivinityModData.cs:395-566`). These are
    ReactiveUI property-name filters over `Changed`.
  - The DynamicData `AutoRefresh` handlers on `modsConnection` (`MainWindowViewModel.cs:5243-5244, 5261, 5332`).
  - One WPF binding (the row's `Visibility` setter, `src/GUI/Themes/MainResourceDictionary.xaml:369`),
    when a container exists.
  - That is roughly 10–20 µs per flip in Debug, so ~2–20 ms for 186–935 flips.

**Conclusion:** the VM side is about **10–60 ms** in total. That is two orders of magnitude short of the
measured 1.24–3.27 s. Fixing VM code first cannot produce the win.

### 2.3 What WPF does with collapsed rows [WPF + certain wiring]

- **The wiring [certain].** The search does not filter the collection (CLAUDE.md §4). The row style
  binds `ListViewItem.Visibility` to the mod (`MainResourceDictionary.xaml:369`). Both lists are
  `VirtualizationMode="Recycling"` (`src/GUI/Views/HorizontalModLayout.xaml:119-120,222-223`) with the
  default item-based `ScrollUnit`, and `ItemsSource` is the raw `ObservableCollectionExtended`
  (`HorizontalModLayout.xaml.cs:526-527`).
- **Collapsed rows stay realized [WPF].** A collapsed container measures to 0 px. The
  VirtualizingStackPanel generates containers until it has filled the viewport (plus its cache), so
  with zero-height rows it realizes every row up to the end of the list. Under "zzzzzz" all 186 inactive
  containers are realized. An active-list no-match would realize all 935; that case was not measured,
  and it is predicted to be the worst.
- **What a realized container costs [WPF]:**
  - A collapsed container skips `Measure`, so a *never-visible* container never applies its row
    template. It still pays for style application: ~8 setters, the `Tag`/`Visibility` bindings, two
    `MultiDataTrigger`s with four bindings (`:383-402`), the shared `ContextMenu`, and the
    `ScreenReaderHelperBehavior` reflection, which the plan measured at ~14 µs per bind (`:256`).
  - A *recycled* container that was visible before keeps its template, and re-binds its ~35 bindings
    even while collapsed. The plan measured ~340 µs per re-bind for the panel's measure.
  - When collapsed→visible flips land on realized containers (a broad match after a narrow one, or
    clearing), each newly visible container gets fully templated and measured. [inferred] The panel
    then virtualizes the off-screen ones away again. That is a full row build, ~1 ms or more per row
    in Debug before 6.1, thrown away.
- **The Auto-width "Last Updated" column [certain that it exists on master].** It is `Width="Auto"` on
  master (`MainResourceDictionary.xaml:875,924,956`); only wt-perf has fixed it (b123aff). [inferred]
  Mass re-measures can ratchet its width and re-measure every row presenter. The plan found no
  measurable effect *during scrolling*, but it is untested under mass visibility flips.
- **Selection re-sync after each generation [certain].** `ItemContainerGenerator.StatusChanged` →
  `UpdateViewSelection` (`HorizontalModLayout.xaml.cs:243-257, 131-176`) runs `ContainerFromItem` for
  every mod. That is O(N × realized). [inferred] At 935 × 935 it costs milliseconds, not seconds, but
  it grows with the number of realized collapsed rows.

### 2.4 The UI Automation multiplier: measurement and real users [certain wiring; inferred magnitude]

- **The peers walk the visual tree [certain].** `ModListView.OnCreateAutomationPeer` returns
  `ModListViewAutomationPeer`, a `CachedAutomationPeer : FrameworkElementAutomationPeer`
  (`src/GUI/Controls/ModListView.cs`, `src/GUI/Util/ScreenReader/ModListViewAutomationPeer.cs`). It is
  *not* WPF's item-aware `ListViewAutomationPeer`. Its `GetChildrenCore` walks the **visual tree**
  (`CachedAutomationPeer.cs:24-79`), so its children are the *realized containers*, collapsed ones included.
- **External walks cost seconds [certain, from the plan].** In Stage 6 the plan measured
  `ClientsAreListening=True`. During scripted scrolls an outside client sometimes walked the tree:
  6,206 calls at 155 µs, and once 9,847 calls at 536 µs, which is **5.3 s**.
- **The harness is itself a UIA client [certain, from the plan].** The filter numbers were taken by
  "UI Automation driving the real search boxes". A UIA client that focuses a text box and sets its
  value, or that searches the tree to read a result, triggers exactly these walks. [inferred] Each walk
  covers every realized row, and the visibility-stamped design realizes the **whole list** under a
  narrow filter.
- **A suspicious floor [inferred].** The measured work has a floor of **~1.24–1.39 s that barely
  depends on list size (186 vs 935) or on how many rows flipped.** Active "e" probably flips few rows,
  yet costs 1.39 s. Pure layout cost should scale with flips; a constant floor looks like a fixed
  per-pass walk, or like harness overhead from how idle was detected. That cannot be settled from
  here; Step 0 separates it.

### 2.5 Why the inactive list can be slower than the active list [inferred, ranked]

The inactive list is the smaller one (~186 vs 935 rows), so list size is *not* the reason. It is also
not VM compute (§2.2). Candidates, most likely first:

1. **State and order dependence.** If "a" ran after "zzzzzz" or straight after load, most of the ~186
   realized-but-collapsed containers flip to visible together. Each gets a full template build and
   measure (§2.3), and the "Last Updated" Auto column may re-measure them again. The active "e" run
   started from a cleared state, where few rows flip. The per-run state (realized count before and
   after, flip count) was not recorded, and Step 0 records it.
2. **UIA walks over the larger realized set** (§2.4). After a narrow-then-broad sequence, the inactive
   list may simply have held more realized containers when the walk happened.
3. **The Auto column.** The inactive GridView has its own Auto "Last Updated" column (`:956`), and this
   is unfixed on master.

### 2.6 The throttle [certain]

- `WhenAnyValue(...).Throttle(500 ms).ObserveOn(MainThread)` (`MainWindowViewModel.cs:5250-5254`) is a
  trailing debounce on the thread-pool scheduler. Its 500 ms is pure wait, added to every settle time.
- It has no `DistinctUntilChanged`: "a", backspace, "a" re-runs a full pass for an unchanged query.
- Cutting it is only safe once a pass is cheap, as the plan says. At 150 ms, a 1.2 s pass would block
  typing.

### 2.7 Correctness found on the way [certain wiring; inferred crash path]

- `ValueContains` calls `CompareInfo.IndexOf(val, …)` (`DivinityModFilterData.cs:25`).
  `Description` is never initialised (`DivinityBaseModData.cs:146-162` sets Name/Author/UUID/Folder
  but not Description). `FileName` is `Path.GetFileName(FilePath)`, which is null when there is no path.
  `CompareInfo.IndexOf(null, …)` throws `ArgumentNullException`.
- So `@desc x` or `@file x` can throw from inside the `Subscribe` at `:5251/5254`. [inferred] The throw
  goes to the default exception handler and can leave that list's filter subscription dead for the
  session. The B4 query refactor null-guards this.
- The plain search matches `Name` (`:4291`), but the row shows `DisplayName`, which is the file name
  when `DisplayFileForName` is on (`DivinityBaseModData.cs:77-80`). This is a UX mismatch that fuzzy
  search (F1) fixes.

---

## 3. The changes, in priority order

### Step 0: instrument before changing anything (TEMP, Windows; reuses the wt-perf rig)

wt-perf already has `PerfProbe`: per-dispatcher-operation timing, counters, and a UIA
`ClientsAreListening` report, plus the `AutomationPeer.GetChildren` probe from 8da508f. Add these on a
TEMP branch (never upstream):

1. **Time the VM pass, separated from WPF.** In `OnFilterTextChanged`, wrap the body with
   `var probe = PerfProbe.Begin();` … `PerfProbe.End("Filter VM pass " + listName, probe);`. Also
   `PerfProbe.Note` the text, path (`@` or plain), `flips` (Visible↔Collapsed) and `deselects`.
2. **Time until idle.** At the end of the pass, record a timestamp and post
   `Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => PerfProbe.Record("Filter → idle", now - t0))`.
   This is the in-process equivalent of "CPU sampled to idle", with no harness involved.
3. **Count realized containers.** Override `PrepareContainerForItemOverride` and
   `ClearContainerForItemOverride` in `ModListView` and count them by `ProbeName`. Log
   `VirtualizingStackPanel.Children.Count` before and after each pass (`PerfScrollDriver` shows how to
   find the panel).
4. **Bracket each pass.** Call `PerfProbe.Dump($"filter:{list}:{text}")` before and after, so the
   existing "Render ops (layout+render)", GC and `AutomationPeer.GetChildren` counters land per pass.
5. **Add `PerfFilterDriver`.** Model it on `PerfScrollDriver`, enabled by a `perffilter` file. Once
   loading is quiet, for each list it sets the VM text to `"" → "a" → "" → "zzzzzz" → "" → "e" → ""`,
   then `"zzzzzz"` on the **active** list (the unmeasured worst case). It waits for ApplicationIdle
   plus 1 s between steps.
   - Run it once with the throttle in place.
   - Run it once calling `OnFilterTextChanged` directly, which removes the 500 ms.
   - Then the app shuts down.
6. **Run the A/B matrix.** Variant flags in the `perfprobe` file:
   - (a) the driver alone (no UIA client);
   - (b) the driver with the owner's UIA harness attached and idle, or with Narrator/Accessibility
     Insights running;
   - (c) the owner's original UIA harness, for comparability with the 0.3 table;
   - (d) the `cvfilter` variant: Step B behind a flag in the TEMP build, so one binary A/Bs it;
   - Debug **and** Release.
7. **One PerfView capture** (Thread Time, `Microsoft-Windows-WPF` provider) of the inactive "zzzzzz"
   and "a" passes under (c). This single stack sample answers §2.4 against §2.3 directly.

**Decision rule:**

- If the VM pass is under 60 ms and the rest of the time sits in Render ops and/or GetChildren, do B.
  That is the expected outcome.
- If (a) is already far below (c), the harness or OS clients inflated the 0.3 numbers. Re-baseline
  against (a), and **still do B**, because B removes both the realization cost and what UIA has to walk.
- Only if the VM pass itself is ≥ 200 ms does B4/F1 move ahead of B.

### Step A: do not do the plan's steps 1–2 as perf work

- They are rejected for perf on the evidence in §2.1 and §2.2, so record them as eliminated.
- Comparing before writing (flip counting) moves into B2.
- Hoisting allocations moves into B4 (null-safety) and F1 (fuzzy keys), which want it for other reasons.

### Step B: filter through the collection view (upstreamable, `perf/filter`)

**B1. Filter predicate on the default views.** In `src/GUI/Views/HorizontalModLayout.xaml.cs`, in
`WhenActivated` after the `ItemsSource` OneWayBinds (`:526-528`):

```csharp
	private static bool IsModInFilter(object item)
	{
		return item is not DivinityModData mod || mod.Visibility == Visibility.Visible;
	}

	private static void SetListFilter(IEnumerable<DivinityModData> list)
	{
		var view = CollectionViewSource.GetDefaultView(list);
		if (view != null && view.CanFilter)
		{
			view.Filter = IsModInFilter;
		}
	}

	// in WhenActivated, after the ItemsSource bindings:
	SetListFilter(ViewModel.ActiveMods);
	SetListFilter(ViewModel.InactiveMods);
```

- **Why the default view [certain].** `ActiveMods`/`InactiveMods` are readonly
  `ObservableCollectionExtended` instances that are never replaced (`MainWindowViewModel.cs:161-165`),
  so their default `ListCollectionView` is stable. `Sort()` already puts `SortDescriptions` on that same
  view (`HorizontalModLayout.xaml.cs:762-769`, `Refresh` at `:769`), so filter and header sort compose.
  `ForceLoadedModsListView` gets no filter, which matches today, because the filter never stamps
  force-loaded mods.
- **Why keep `Visibility` as the flag.** This is the low-conflict choice. The `IsSelected` guard
  (`DivinityModData.cs:190`), `ModListDragHandler` (`:72,77`) and wt-perf's `SelectMods`
  (`x.Visibility == Visible`) all already read it as "is in view". A neutral `bool IsFilterMatch` would
  be nicer for Linux, but renaming it would conflict with wt-perf; do it in a later tidy-up.
- **The invariant [certain from grep].** The view shows exactly the mods whose `Visibility==Visible`.
  The only writers of `Visibility` are the ctor default (`DivinityBaseModData.cs:161`) and
  `OnFilterTextChanged` (`MainWindowViewModel.cs:4207,4277,4281,4293,4297`), and B2 makes the latter
  refresh after it writes. Adds and moves into the source are filtered by the view on arrival [WPF].

**B2. Count flips, refresh only on change, prune selection.** In `MainWindowViewModel.OnFilterTextChanged`,
replace each pair of `Visibility = …` and `IsSelected = false` with a helper, then refresh once:

```csharp
	private static bool SetFilterMatch(DivinityModData mod, bool isMatch)
	{
		var next = isMatch ? Visibility.Visible : Visibility.Collapsed;
		if (mod.Visibility == next)
		{
			return false;
		}
		mod.Visibility = next;
		if (!isMatch)
		{
			mod.IsSelected = false;
		}
		return true;
	}

	// in OnFilterTextChanged: var changed = 0; ... if (SetFilterMatch(m, isMatch)) changed++;
	// at the end, before the TotalHidden assignment:
	if (changed > 0)
	{
		Layout?.RefreshFilter(modDataList);
	}
```

- Setting `IsSelected=false` only on a flip is equivalent to setting it on every non-match. A hidden
  mod cannot become selected while hidden (`DivinityModData.cs:190`), so a non-flipping hidden mod is
  already deselected [certain].
- `IModViewLayout` (`HorizontalModLayout.xaml.cs:22-31`) gets one new member:

```csharp
	void RefreshFilter(IEnumerable<DivinityModData> list);

	public void RefreshFilter(IEnumerable<DivinityModData> list)
	{
		ModListView listView = null;
		if (list == ViewModel.ActiveMods) listView = ActiveModsListView;
		else if (list == ViewModel.InactiveMods) listView = InactiveModsListView;
		if (listView == null) return;

		CollectionViewSource.GetDefaultView(list)?.Refresh();

		//Belt and braces: a Reset should already drop hidden items from the selection, but
		//prune explicitly so the ListView and the models cannot disagree (see 309b81e).
		var hiddenSelected = listView.SelectedItems.OfType<DivinityModData>()
			.Where(x => x.Visibility != Visibility.Visible).ToList();
		foreach (var mod in hiddenSelected)
		{
			listView.SelectedItems.Remove(mod);
		}
	}
```

- The VM stays toolkit-neutral: it calls a layout method and never touches `ICollectionView`.
- All callers already run on the UI thread [certain]. That covers the `ObserveOn(MainThread)` at
  `:5250-5254`, the drop handler, and `LoadModOrder` at `:2146-2147`, which mutates bound collections
  and so must already be on the UI thread.

**B3. Translate view indices to source indices.** With a filter, `Items`/`SelectedIndex` indices are
*view* indices and no longer equal source indices. Today they only coincide because collapsed rows
count as items. Every site [certain, from grep of `Items[`, `GetItemAt`, `SelectedIndex`, `IndexFromContainer`]:

| Site | Today | Fix |
|---|---|---|
| `ModListView.cs:287` Alt+Arrow | `Items.GetItemAt(targetScrollIndex)` with a *source* index; under a filter this picks the wrong item or throws out-of-range | `ScrollIntoView(list[targetScrollIndex]);` |
| `HorizontalModLayout.xaml.cs:307,359` `MoveSelectedMods` | `new ManualDropInfo(..., OtherListView.SelectedIndex, ...)` feeds a view index into `UnfilteredInsertIndex` | `var insertIndex = other.SelectedItem is DivinityModData target ? otherList.IndexOf(target) : -1;` (−1 is already clamped to 0 in `ModListDropHandler.cs:172-175`, the same as today's −1) |
| `HorizontalModLayout.xaml.cs:345,398` | `SelectedIndex = nextSelectedIndex` with a source index | `listView.SelectedItem = list.Skip(nextSelectedIndex).FirstOrDefault(x => x.Visibility == Visibility.Visible) ?? list.LastOrDefault(x => x.Visibility == Visibility.Visible);` |
| `HorizontalModLayout.xaml.cs:330,383,437-441` | view index compared with `Items.Count`; `Items.IndexOf(mod)` | already consistent (view vs view); no change |
| Gong `DropInfo.UnfilteredInsertIndex` | maps `Items[InsertIndex]` → `ItemsSource.IndexOf` **whenever the counts differ** (`dl-gong/DropInfo.cs:55-87`) | no change: this is exactly the filtered-view case, and `ModListDropHandler.cs:133` already reads `UnfilteredInsertIndex` |

**B4. Remove the row's `Visibility` binding and guard the query.**

- Delete `MainResourceDictionary.xaml:369`. Filtered-out mods no longer have containers, so the binding
  is dead weight that would fire on every flip. The unused `VerticalModLayout.xaml` shares the style
  [certain it is unused: only `HorizontalModLayout` is instantiated, `MainViewControl.xaml:502`].
- In `OnFilterTextChanged`'s `@` path, make the regexes `static readonly` and pass
  `RegexOptions.CultureInvariant`. Resolve each `DivinityModFilterData`'s property keywords **once per
  pass** into flags (`[Flags] enum ModFilterField { Author, Version, Depend, Name, File, Desc, Type,
  UUID, Selected, Editor, Modified, Tag }`), using exactly today's rule: the typed property *contains*
  the keyword, case-insensitive (`DivinityModFilterData.cs:28-31`).
- Pre-parse the `@modified` date once per pass, and null-coalesce every field
  (`mod.Description ?? String.Empty`, `mod.FileName ?? String.Empty`).
- Keep these behaviours exactly, and document them in the PR:
  - an empty value matches everything (`:34`), so `@selected ` with nothing after it matches all;
  - free text maps to `Name` only when no `@name` term is given (`:4253-4263`);
  - all terms must match, and different fields within one term are OR'd.
- Gain: ≤10 ms on the `@` path, plus removing the null crash (§2.7).

**B5. Throttle 500 → 150 ms, plus `DistinctUntilChanged`.** Only after Step 0's re-measure shows a pass
is ≤ 60 ms:

```csharp
		this.WhenAnyValue(x => x.ActiveModFilterText)
			.Throttle(TimeSpan.FromMilliseconds(150))
			.DistinctUntilChanged()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(s => OnFilterTextChanged(s, ActiveMods));
```

- Make the same change for `InactiveModFilterText`.
- Direct calls are unaffected: the drop handler (`ModListDropHandler.cs:252,257`) and `LoadModOrder`
  (`:2146-2147`) call `OnFilterTextChanged` themselves, so de-duplicating the text stream drops nothing they need.

### Step C: only on evidence, live filtering instead of `Refresh()`

- `ListCollectionView` with `IsLiveFiltering=true` and `LiveFilteringProperties.Add("Visibility")`
  turns flips into incremental Remove/Add events instead of a Reset.
- That keeps scroll offset and focus without any code, and avoids rebuilding the ~28 viewport rows when
  only off-screen rows changed.
- It also costs a per-item PropertyChanged listener inside the view, and N events for a mass change.
- A/B it in the Step 0 rig as a `livefilter` variant, and adopt it only if it beats Refresh in both the
  few-flip and the all-flip cases.

### Expected per-keystroke cost after each step [inferred; Debug; confirm with Step 0]

| After | Inactive "a" | Inactive "zzzzzz" / cleared | Active "e" / cleared | Active "zzzzzz" (unmeasured) | Felt settle |
|---|---|---|---|---|---|
| Baseline (UIA harness, 0.3 table) | 3.27 s | 1.24 / 1.37 s | 1.39 / 1.34 s | predicted worst (935 realized) | 1.7–3.8 s |
| A (Visibility guard, hoist allocations) | ≈ same (−0–10 ms) | ≈ same | ≈ same | ≈ same | 1.7–3.8 s |
| B1–B4 (collection filter) | ~20–60 ms | ~20–60 ms | ~20–60 ms; ~2–5 ms if nothing flips | ~25–70 ms | ~0.55 s (throttle-bound) |
| B5 (150 ms throttle) | same | same | same | same | **~0.2 s** |
| F (fuzzy, fork) | +3–10 ms scoring, +1–5 ms rank sort | +3–10 ms | +3–10 ms | +3–10 ms | ~0.2 s |

How the B1–B4 estimate is built:

- one predicate pass, 186–935 × ≤2 µs;
- the flips, 10–20 µs each;
- the `ListCollectionView` rebuild, O(N), under 1 ms;
- the Reset, which re-realizes **only the viewport**, ~28 rows at 0.5–1.5 ms each. The plan counted 28
  realized rows (26 visible) in the active list. The cost falls further once 6.1's `CachedTextBlock`
  lands;
- `UpdateViewSelection` over ~28 realized rows.

Under a UIA client, the per-walk cost also drops by the realized-row ratio, roughly 186→28 or 935→28.

---

## 4. Fuzzy search (fork-only, `fork/fuzzy-search`, on top of `perf/filter`)

**F1. A match key per mod, computed once.** In `DivinityModData` (Core) add
`public string SearchKey { get; private set; }` and `public string AuthorKey { get; private set; }`.
Fill them in the ctor:
`this.WhenAnyValue(x => x.Name, x => x.DisplayName, x => x.Author).Subscribe(x => { SearchKey = BuildKey(x.Item1, x.Item2); AuthorKey = (x.Item3 ?? String.Empty).ToLowerInvariant(); });`

- These are plain properties with no notification: the filter reads them, nothing binds them.
- In practice they are computed once at load, and recomputed only when name, display name or author changes.
- `BuildKey` lowercases `Name`, plus `DisplayName` when it differs. That fixes the Name/DisplayName
  mismatch from §2.7.
- Also cache `public int FilterScore;` as a plain field for ranking.

**F2. `FuzzyMatcher` (Core, pure, toolkit-neutral).** `static int Score(string pattern, string key)`
returns −1 for no match.

- Algorithm: an fzf-v1-style greedy subsequence, then a backward tighten pass. Bonuses for word start
  (after a space, `_`, `-`, `.` or a camel-case hump), for consecutive characters and for a prefix;
  a small gap penalty; a large bonus for a whole-pattern substring, so today's exact hits always rank first.
- It works on the pre-lowered key with an invariant-lowered pattern: no culture calls and no
  allocation per mod, about 1–5 µs per mod.
- Multi-word free text is tokenised on whitespace. Every token must match (AND), and the score is the sum.

**F3. Query semantics.**

- `@field value` terms keep B4's exact-substring rule, because the power syntax should stay predictable.
  `@name`/`@author` *can* opt into fuzzy, but only with the setting on.
- Free text is fuzzy against `SearchKey`. A mod passes if every term passes.
- Setting: `[DataMember, Reactive, SettingsEntry("Fuzzy Search", "Match mod names approximately and rank inactive results by relevance")] public bool FuzzySearch { get; set; } = true;`
  on `DivinityModManagerSettings`. It needs all three attributes (CLAUDE.md §4). Turned off, the filter
  behaves exactly as `perf/filter`.

**F4. Active list vs inactive list. This is the decision the plan asked for.**

- **Active list: fuzzy inclusion only, never reordered.**
  - Order there is the load order. Re-sorting by score would make a drag/drop insert position
    meaningless, and would show an order that does not match what gets saved.
  - The active list uses a stricter minimum score (word-start or consecutive quality), because
    accidental scattered subsequence hits in a 935-row list are noise.
- **Inactive list: inclusion and ranking.**
  - When the query is non-empty, save the view's `SortDescriptions` (the user's header sort), then set
    `((ListCollectionView)view).CustomSort = ModFilterScoreComparer.Instance`. This is fast (no
    reflection) and orders by `FilterScore` descending, then by name.
  - When the query is cleared, set `CustomSort = null` and restore the saved `SortDescriptions`.
    [WPF] Setting `CustomSort` clears `SortDescriptions`, which is why they are saved first.
  - A header click while ranked clears `CustomSort` first, so the user's choice wins.
  - The inactive list is a set (`MainWindowViewModel.cs:2143-2144`), so ranking it loses nothing.
  - Ranking means refreshing even with zero flips when the order changed. Always refresh the inactive
    list for a non-empty fuzzy query; ~186 rows is cheap after B.

---

## 5. Interactions

### 5.1 Drag and drop [certain]

- The Gong `UnfilteredInsertIndex` remap (§3 B3 table) is what makes collection filtering safe.
  Dropping "before visible X" inserts directly before X in the source, after any hidden mods that
  precede it. Today, with zero-height containers, dropping before X also lands at X's index. Same result.
- **Re-checking after a drop.** The drop handler re-runs the filter (`ModListDropHandler.cs:250-258`)
  and schedules `RefreshDataView` (`:236-248`). Both re-apply the predicate.
- **A drop can trigger two Resets.** The filter pass refreshes only when something flipped (B2), and
  the scheduled `RefreshDataView` refreshes again. Leave both in the upstream PR: deduping them is cosmetic.
- **Dropping into a filtered list.** A dropped mod that does not match the destination's filter now
  disappears from view when it lands, as it effectively does today. Its `IsSelected` is cleared by the
  filter pass, the same as today.
- **Ctrl+A changes, for the better.** `SelectAll` now selects only visible mods. Today it selects
  collapsed items in the ListView while their models refuse `IsSelected=true`, which is the
  "programmatic multi-select no-ops" desync noted in CLAUDE.md.

### 5.2 Selection and the wt-perf fixes (309b81e, 396d7b2) [certain wiring; WPF Reset semantics to verify]

- **309b81e** rebuilds `mod.IsSelected` from `ListView.SelectedItems` for every mod in the list.
  - After a filter Reset, hidden mods are not in `SelectedItems`: the Reset drops them [WPF], and
    `RefreshFilter` prunes them as well.
  - So the reconcile sets them false, which is today's "hidden ⇒ deselected" rule.
  - Visible selected mods stay selected across a refine.
- **396d7b2** `SelectMods` → `SetSelection(group.Where(x => x.Visibility == Visible))`.
  Under B, "Visible" means "in the view", so `SetSelection` is only ever given items the Selector
  contains. It is compatible.
- **Merge surface [inferred].** B touches different hunks from wt-perf:
  - `HorizontalModLayout.xaml.cs`: `MoveSelectedMods`, `WhenActivated` and the new `RefreshFilter`,
    against wt-perf's `SelectMods`/`UpdateIsSelected`;
  - `ModListView.cs:287`, against wt-perf's additions at `:194-255`;
  - `MainResourceDictionary.xaml:369`, against wt-perf's `ModNameTemplate` and `:875/924/956`.
  - So a clean textual merge is expected; verify it by merging both into a scratch `fork/main`.
- **Focus and scroll [WPF].** A Reset destroys the focused row's container. While typing, focus is in
  the TextBox, so nothing is lost. The item-based scroll offset survives a Reset (clamped to the new
  extent). Keep that default upstream; F4 may add "scroll to top on a new ranked query".

### 5.3 Stage 6.3 category dividers [design-compatible]

- The predicate generalises, and collapse is the same mechanism as search: `IsModInFilter(o)` =
  `o is CategoryDivider d ? (d.HasVisibleChildren || noQuery) : mod.MatchesSearch && !mod.InCollapsedCategory`.
  - The filter pass does two passes: children first, then dividers.
  - Collapsing a category is a flag flip plus `RefreshFilter`, costing ~viewport rows. It does **not**
    realize the collapsed category's 500 hidden containers, which is what stamping `Visibility` would do.
- Flat rows in one collection, no `GroupDescriptions`, so the `IsGrouping` → `CanContentScroll=False`
  trap (`MainResourceDictionary.xaml:460-462`) stays inert.
- In the ranked inactive list (F4), hide the dividers while ranking.
- Dividers must be items of `ObservableCollectionExtended<DivinityModData>`, either a `DivinityModData`
  subclass or a change to the element type. That is 6.3's decision, and this filter design does not constrain it.

### 5.4 The `@` field syntax

Preserved exactly in `perf/filter` (B4), quirks included and listed in the PR. `fork/fuzzy-search`
changes only how free-text terms match (F3).

### 5.5 Accessibility

- B only shrinks the set of realized containers the custom peers walk.
- Screen readers can still find matching rows, because UIA sees what is in the view.
- Hidden rows stop appearing as offscreen items, which is arguably more correct.
- Replacing `ModListViewAutomationPeer` with an item-aware peer is a **Stage 6** item, not this one.

---

## 6. Branches and PRs

| Branch | From | Commits | Upstream? |
|---|---|---|---|
| `perf/filter-probe` (TEMP) | `perf/list-render` (needs PerfProbe) | Step 0 probes, `PerfFilterDriver`, `cvfilter`/`livefilter` variants | never |
| `perf/filter` | clean `master` | 1. "Filter mod lists through their collection view instead of collapsing rows" (B1, B2, B4 XAML line). 2. "Map view indices to list indices when moving selected mods" (B3). 3. "Parse the filter query once per pass and guard null fields" (B4 code). 4. "Skip filter passes that change nothing and shorten the filter delay to 150 ms" (B5) | **yes**, one PR, with before/after numbers from the probe build |
| `fork/fuzzy-search` | `perf/filter` | F1 keys, F2 matcher, F3 query plus setting, F4 inactive ranking | no, fork-only; separable because it only adds Core types, one setting and a CustomSort hook |

House style for every file: tabs, CRLF, UTF-8 BOM, Allman, `var`, file-scoped namespaces, no `string?`
in Core/GUI. Keep the ReactiveUI.Fody 19.5.41 / ReactiveUI 20.1.63 pin untouched.

---

## 7. Test plan

**Performance (Windows; Debug and Release; Step 0 rig).**

- Run every 0.3 case plus active "zzzzzz", under variants (a), (b) and (c), before and after B.
- Pass criteria:
  - in-process "VM pass + until idle" ≤ 60 ms (Debug) for every case;
  - realized containers ≤ viewport + cache (about 30) after every pass;
  - `AutomationPeer.GetChildren` work per pass scales with realized rows, not list length;
  - the UIA harness settle time ≤ 0.3 s with B5.
- Also re-run `perfscroll` to confirm scrolling did not regress.

**Function (manual checklist, for the PR description).**

1. Type, refine, clear and re-type in both lists. Counts in "N Matched, M Hidden" are correct
   (`MainWindowViewModel.cs:4178-4188`).
2. `@author foo`, `@author "two words"`, `@name x @type adventure`, `@depend <uuid>`, `@modified 2024-01-01`.
   Run `@desc x` and `@file x` with mods that have no description or no path: no exception, and the
   filter still works afterwards.
3. Drag/drop within the filtered active list: between visible rows, at the end, and onto a filtered
   list with zero matches. Drop from filtered inactive to filtered active and back. Check the order
   saved to `modsettings.lsx` after each drop.
4. Move selected (the keybinding behind `MoveSelectedMods`) from both filtered lists, with and without
   a selection in the destination.
5. Alt+Up/Down in a filtered, then unfiltered, active list; also with a column-header sort applied.
6. Ctrl+A in a filtered list selects only visible mods. Clearing the filter shows the hidden ones unselected.
7. Select visible mods, refine so some hide, and clear: hidden ones are deselected and visible ones stay selected.
8. Switch load order and profile with filter text present; refresh (F5).
9. The 309b81e/396d7b2 repro (move a large selection between lists, deselect, scroll) on a scratch
   merge of `perf/filter` and `perf/list-render`.
10. Screen reader (if available) reads the filtered rows.

**Unit tests (fork branch).** `ModFilterQuery` parse table (each `@` keyword, quotes, mixed free text)
and `FuzzyMatcher` ranking cases (exact > prefix > word-start > scattered; no-match = −1). The repo has
no test project today [certain: only Core/GUI/Toolbox csproj], so adding one is the owner's call.
Core targets `net8.0-windows`, so the tests run on Windows.

---

## 8. Open questions for the owner

1. In what order did the 0.3 filter runs happen, and how was idle detected (sampling interval, and how
   long it had to be idle)? Step 0 makes this moot, but it would explain the ~1.2 s floor.
2. Is an accessibility client (Narrator, a magnifier, voice access, a password manager, the Windows
   text-input host) normally running on your PC? If so, B helps your everyday use and not only the
   benchmark.
3. Should the default be "scroll to top on a new query"? This design keeps WPF's default upstream, which is to keep the offset.
