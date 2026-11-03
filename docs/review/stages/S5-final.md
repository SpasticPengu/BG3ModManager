# Stage 5 (filter, search, fuzzy): final recommended plan

Judge and synthesis of `S5-A-cheapest-first.md`, `S5-B-collection-filtering.md` and `S5-C-fuzzy.md`.
Written 2026-11-03 against master `fc35358`, wt-perf `perf/list-render` (head `9f2b163`) and the gong `DropInfo.cs`/`DragInfo.cs`
copy in `scratchpad/dl-gong`. Nothing was built or run against the WPF app. I re-ran S5-C's prototype
(`scratchpad/fuzzproto`, Release, Linux), and its numbers reproduced within about 10%.

Reports: `AUDIT-PLAN.md` was used. `VM2.md`, `S6.md` and `UI.md` did not exist when I started or when I re-checked before
finishing, so nothing from them is used here.

Labels: **[certain]** means it follows from the source at path:line. **[measured]** means a number from the owner's 0.3 table, the
plan's Stage 6.1 probes, or the S5-C prototype. **[WPF]** means framework behaviour I know but did not re-read here.
**[inferred]** means a hypothesis that Step 0 has to confirm.

---

## Scores

Scale is 1–5, and 5 is always best. So 5 for correctness risk means the *lowest* risk, and 5 for effort means the *least* effort.

| Design | Per-keystroke saving | Correctness risk (drag/drop, selection, filtered multi-select) | Upstreamability of the perf part | Effort | Fit with 6.3 dividers and the wt-perf selection fixes | Fuzzy UX |
|---|---|---|---|---|---|---|
| **S5-A** cheapest first | 4 | **2** | 5 | 4 | 4 | 2 |
| **S5-B** collection filtering | 4 | 4 | 4 | 3 | 5 | 2 |
| **S5-C** fuzzy | 3 (neutral: it inherits A/B and adds ≤0.5 ms) | 4 | 5 (puts no fuzzy code upstream) | 2 | 5 | **5** |

Why the scores come out this way:

- **A and B use the same perf mechanism**, a filter on the default `ListCollectionView`, so they save the same amount. Both saving
  estimates (20–80 ms in-process) may be measured against the wrong baseline (§2).
- **A has one disqualifying gap.** It trusts gong's `UnfilteredInsertIndex` for drops (A §3 B3 table, §5.1). Under a collection
  filter that silently corrupts load order in three cases (§4.3). B's `ResolveSourceInsertIndex` handles all three.
- **B has its own problems.** It overstates one present-day bug: hidden mods are **not** dragged after Ctrl+A, because
  `ModListDragHandler.cs:72,77` rebuilds the drag payload from `IsSelected && Visibility == Visible`. That removes gate G3. Its
  scroll-anchor-on-every-keystroke also needs simplifying.
- **C is the only real fuzzy design.** It was prototyped and measured. Matching all 1,121 names costs 0.02–0.41 ms per
  keystroke, fuzzy misses none of today's 398,340 substring hits, and the anchoring rule removes most of the noise. Its effort is
  high only because it includes optional extras (F5 highlight, F6 find-in-place, F7 typos), which this plan defers.

**Verdict:**

- **The perf part** is B's design, cut down to A's leanness: the `ModFilterQuery` extraction, flip counting, the collection-view
  filter, B's drop resolver and the index fixes, then the throttle change.
- **Before any of it**, the Step 0 probe, including a harness-floor control that neither A nor B proposed.
- **The fuzzy part** is C's F1–F4, plus three corrections (§6.6).

---

## 1. Owner intentions this plan is built on

You asked for this list. It is gleaned from CLAUDE.md, the plan, the task and the three designs.

1. **Platform.** Stay on WPF master. Windows comes first, and Linux and Avalonia stay possible. New logic goes in Core,
   toolkit-neutral where that is free.
2. **Upstream hygiene.** The perf part goes to LaughingLeader as clean PRs cut from master. Fuzzy, categories and
   overrides-in-order are fork-only, on separable branches.
3. **Measure first.** Record eliminated hypotheses as eliminated and don't re-argue them. Keep Debug numbers as controls.
4. **Search should feel live**, then fuzzy and ranked. Keep `@author foo`. Precompute keys. Decide the active-list question
   explicitly.
5. **Load order is sacred.** No view concern (search, sort, collapse) may reorder `ActiveMods` or change what gets saved.
6. **Stage 6 stays intact.** That covers `CachedTextBlock` and 309b81e/396d7b2, and categories as flat, collapsible divider rows,
   never WPF grouping (the `IsGrouping` trap at `MainResourceDictionary.xaml:460-462`).
7. **Accessibility keeps working**, and **house rules hold**: the Fody pin, `ToUIPropertyImmediate`, AutoRefresh before Filter,
   no `string?`, and tabs, CRLF, BOM, Allman, `var`.
8. **Process.** Use as many agents as needed, at medium or high effort.

---

## 2. Diagnosis

### 2.1 "Only assign Visibility when it changes" saves nothing. This is settled.

- `Visibility` is `[Reactive]` on a `ReactiveObject` (`src/Core/Models/DivinityBaseModData.cs:69`), and Core runs the ReactiveUI
  Fody weaver (`src/Core/FodyWeavers.xml`, `Core.csproj:32` pins 19.5.41). The woven setter is `RaiseAndSetIfChanged`, so an
  equal write raises no `PropertyChanged`, updates no binding and causes no layout. **[certain, with WPF/Fody behaviour]**
- `IsSelected` is hand-written and also ends in `RaiseAndSetIfChanged` (`src/Core/Models/DivinityModData.cs:185-196`). **[certain]**
- In the measured worst cases every stamp really does change:
  - `""→"zzzzzz"` hides all ~186 inactive rows;
  - clearing restores all of them.
  - A guard has nothing to skip there.
  - Where few stamps change (active "e"), the setters already skip the rest. **[certain]**
- A pre-write comparison costs and saves nanoseconds. Its **only** value is as a *flip counter*, so that the view can skip a
  `Refresh()` when nothing flipped. That only matters once there *is* a Refresh (Step 3). AUDIT-PLAN claims 21–22 and
  S5-A §2.1 say the same.
- Plan step 2, "hoist per-keystroke allocations", is also a no-op on the measured path. The plain path calls
  `CompareInfo.IndexOf(m.Name, searchText, IgnoreCase)` and allocates nothing (`MainWindowViewModel.cs:4289-4301`). **[certain]**

### 2.2 Where the measured 1.2–3.3 s goes

**What it cannot be:**

| Candidate | Evidence | Upper bound |
|---|---|---|
| The matching loop | It walks one list, not all 1,121 (`:5250-5254`, `:4204-4301`). The S5-C prototype runs today's loop over 1,121 names in **0.05–0.24 ms Release** for Latin and ~1.2 ms for CJK [measured, Linux ICU; Windows to confirm] | ≤2 ms |
| Per-flip notification fan-out | Each flip reaches the per-item DynamicData `AutoRefresh` handlers (`MainWindowViewModel.cs:5243-5244, 5261, 5332`; none watch `Visibility`), the mod's own `WhenAnyValue` subscriptions, and one row binding (`MainResourceDictionary.xaml:369`). An `IsSelected=false` flip also feeds the 25 ms-buffered selected counts (`:5261-5264`) [certain wiring] | ~10–50 µs × flips ≈ ≤50 ms [inferred] |
| The throttle | 500 ms of pure wait, *added* to "settled at". The table's "work" column already subtracts it | 0 of the "work" |

**What the numbers say about the rest:**

| Case | Flips | Work |
|---|---|---|
| Active "e" (935 rows) | few (only names without an "e") | 1.39 s |
| Active cleared | few | 1.34 s |
| Inactive "zzzzzz" (186 rows) | all ~186 | 1.24 s |
| Inactive cleared | all ~186 | 1.37 s |

- The cost is **flat**. It does not change with list size (186 vs 935) or with the number of flips. **[measured]**
- Any per-row mechanism, realization included, would scale with one or the other. So **most of the ~1.2–1.4 s floor is not
  a function of the filter work at all** [inferred, strong].
- S5-A and S5-B both put the seconds into "WPF realizing collapsed containers". The flat table argues against that being the
  *dominant* term.

**Ranked hypotheses for the floor** [inferred]:

1. **The measurement itself.**
   - The 0.3 numbers come from a UI Automation client that drives the boxes, with "CPU sampled until the process went idle".
   - A fixed idle-detection window or sampling granularity produces exactly a flat floor (AUDIT-PLAN claim 25).
   - The UIA client can also walk the tree itself. The plan measured an outside client walking it in 4 of ~25 runs: 6,206
     `GetChildren` calls at 155 µs, which is **≈0.96 s**, and once 9,847 at 536 µs, which is 5.3 s (implementation-plan §6.1
     table). That is the same order as the floor.
   - A window-wide walk costs about the same whichever list was filtered, which fits the flatness.
2. **In-process UIA subtree updates.**
   - `ClientsAreListening=True` on the owner's machine [measured, plan §6.1]. A UIA client is attached in everyday use, not
     only under the harness.
   - `ModListViewAutomationPeer : CachedAutomationPeer` walks the **visual** tree of every realized container
     (`src/GUI/Util/ScreenReader/CachedAutomationPeer.cs:25-50,69-80`). [WPF] Container generation changes the visual
     children, which invalidates automation ancestors and can trigger `UpdateSubtree` → `GetChildren` after layout.
3. **Realizing collapsed containers.** This is real, but smaller than A and B assume.
   - **The mechanism.** A collapsed container measures to 0 px, so the `VirtualizingStackPanel` keeps generating until the
     viewport is full, which means all remaining rows under a narrow filter [WPF]. Collapsed containers skip Measure, and
     so skip their template [WPF]. They still pay style application, the ContextMenu and the bindings, plus
     `ScreenReaderHelperBehavior` (measured at ~14 µs, plan §6.1).
   - **Estimate.** 100–400 µs per container in Debug [inferred]:
     - inactive "zzzzzz": 186 containers, **~20–75 ms**;
     - **active "zzzzzz" or any narrow active filter**: 935 containers, **~0.1–0.4 s**. This case was **not measured**, and it is
       where realization would dominate.
   - **It also multiplies hypothesis 2,** because every realized container is walked.
4. **First-filter costs** explain the 3.27 s outlier. Per AUDIT-PLAN claim 25 it was the session's first filter, so it carries
   Debug JIT of the filter, Rx and DynamicData paths, ICU collation init and first template instantiation. The run order is
   not recorded.

**Consequences for the plan:**

- **We do not know the true in-process per-keystroke cost.** It could be 50 ms or 1.3 s. Step 0 must measure it, with no UIA
  client and with a **harness-floor control**: the same harness typing into a box whose filter is disabled.
- **Collection filtering is still the right structural change, whatever Step 0 shows.** It removes hypothesis 3 by
  construction, and it shrinks hypothesis 2's walk from "every realized row" (up to 935) to ~28. It is also required by 6.3
  collapse (§7), and it fixes several real index bugs (§4.3, §4.4).
  - What changes with the probe's numbers is the *headline gain*, not the decision.
- **The throttle cut is the one guaranteed felt win.** 500→150 ms takes 350 ms off every settle. It is gated on the per-pass
  cost being small; otherwise a 150 ms throttle fires an expensive pass more often.

### 2.3 Corrections to the three designs (spot-checked)

| Claim | Verdict | Evidence |
|---|---|---|
| A: "Gong already maps filtered-view indices back to source indices; no change needed" | **Wrong in three cases** | See §4.3. `DropInfo.cs:60-63` returns the raw view index when no container is under the cursor. `:372` sets `InsertIndex = Items.Count` (view count). `:66` remaps only when the counts differ |
| B: "After Ctrl+A on a filtered list, dragging moves hidden mods too" (T-S4, gate G3) | **Refuted** | `ModListDragHandler.StartDrag` replaces gong's `SelectedItems` payload with `ActiveMods.Where(x => x.IsSelected && x.Visibility == Visible)` (`src/GUI/ViewModels/ModListDragHandler.cs:72,77`), and the model refuses `IsSelected=true` while hidden (`DivinityModData.cs:190`). The view-level desync is real but cosmetic |
| AUDIT-PLAN claim 23: `UnfilteredInsertIndex` maps "a filtered *or sorted*" view | **Wrong for sorted views** (C §2.3 is right) | Remap only when `Items.Count != ItemsSource.Count` (`dl-gong/DropInfo.cs:66`). So **today**, a header-sorted active list (`Sort()` applies to either list, `HorizontalModLayout.xaml.cs:752-776`) drops at a *view* index used as a load-order index. That is a pre-existing bug, which B's resolver fixes |
| C: inactive ranking via `ModSearchRanking.Compare` | **Bug** | An item missing from the dictionary gets `default(ModMatch)`, which is Tier 0, the *best* tier. A mod dropped into a ranked inactive list sorts to the top until the next pass. Fix: a missing entry ranks last (§6.6) |
| A/B/C: `Visibility` is the only filter flag; writers are the ctor default and `OnFilterTextChanged` | Confirmed | `DivinityBaseModData.cs:161`; `MainWindowViewModel.cs:4207,4277,4281,4293,4297`; callers at `:2146-2147, 5251, 5254`, `ModListDropHandler.cs:252,257` |
| A/B: `ActiveMods`/`InactiveMods` are never replaced | Confirmed | Readonly fields (`MainWindowViewModel.cs:161-165`), cleared and refilled (`:2141-2144`) |
| A/B: `@desc`/`@file` can throw on null | Plausible [inferred] | `ValueContains` calls `CompareInfo.IndexOf(val, …)` with no null guard (`DivinityModFilterData.cs:24`). `Description` is not set in the ctor (`DivinityBaseModData.cs:147-162`) |
| A/B/C: Alt+Arrow scrolls with a source index into the view | Confirmed | `Items.GetItemAt(targetScrollIndex)` (`src/GUI/Controls/ModListView.cs:286`). This is also wrong today under a header sort |
| A/B: `MoveSelectedMods` passes `SelectedIndex` (view) as the source insert index | Confirmed | `HorizontalModLayout.xaml.cs:307,359` into `ManualDropInfo.UnfilteredInsertIndex` (`ModListDropHandler.cs:57`) |

---

## 3. Ordered steps (perf; upstreamable except Step 0)

Each step lists its expected gain, how to measure it on Windows, and its pass criterion. Gains are **in-process work after the
throttle, Debug**, unless stated.

### Step 0: probe (TEMP branch `probe/filter`, cut from `perf/list-render`, never upstream)

Reuse wt-perf's `PerfProbe`, the `AutomationPeer.GetChildren` counter (8da508f) and the `perfprobe` variant-flag file. Add:

1. **Split timers in `OnFilterTextChanged`:** parse, match (no setters), and stamp (setters, counting flips and deselects).
   Then post a `Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, …)` that records "filter → idle". Log `n`, `flips`,
   `hidden`, `ClientsAreListening`, and **realized containers**: the count of ListViewItem children, plus how many are
   collapsed.
2. **Per-pass counters.** `PerfProbe.Dump("filter:<list>:<text>")` before and after each pass, so Render ops, GC and
   **GetChildren calls** land per pass.
3. **A `PerfFilterDriver`**, modelled on `PerfScrollDriver` and enabled by a `perffilter` file. It sets
   `ActiveModFilterText`/`InactiveModFilterText` **in-process**, so no UIA client is involved. Each scenario runs 6 times and
   the first is discarded. Scenarios:
   - inactive `"" → a → "" → zzzzzz → ""`;
   - active `"" → e → "" → zzzzzz → ""`. The active "zzzzzz" is the unmeasured worst case;
   - active narrowing `c → co → com → comb`;
   - `@author la`.
4. **Variant flags:**
   - `nofilter`: `OnFilterTextChanged` returns at once. This is the **harness-floor control**.
   - `viewfilter`: a crude Step 3. Clear the row's `Visibility` binding in `PrepareContainerForItemOverride`, install
     `view.Filter`, and `Refresh()` after the pass.
   - `resetonly`: `RefreshDataView` with no stamping. This is Step 3's floor.
   - `shadowfuzzy` (from C §7): score with the fuzzy matcher alongside today's, log the time and the extra hits, change nothing.
5. **Matrix:**
   - {baseline, `nofilter`, `viewfilter`, `resetonly`} × {driver only, driver with the owner's UIA harness attached} × Debug;
   - one Release pass;
   - **the owner's original harness once against `nofilter`**: if it still reports ~1.2 s, the 0.3 floor was the harness;
   - one PerfView Thread Time capture (`Microsoft-Windows-WPF` provider) of inactive "zzzzzz" under the harness.
6. **Corpus dump** (`dumpcorpus` flag, from C §7.2). Write `UUID\tName\tDisplayName\tAuthor\tIsActive` to a TSV, for the fuzzy
   goldens and for running `fuzzproto` on Windows.

**Decision table:**

| Probe result | Action |
|---|---|
| Harness against `nofilter` ≈ 1.2 s | Floor confirmed as measurement. Re-baseline every number on the driver, and report the 0.3 table as "harness-inflated" |
| Driver-only `toIdle` ≫ match + stamp, and realized ≫ 28 after narrow passes | Realization confirmed. Steps 1–5 as planned |
| `GetChildren` calls per pass in the hundreds or more with UIA attached | UIA walk confirmed. Steps 1–5 shrink it by about realized/28. File a Stage 6 item for an item-aware peer instead of the visual-tree walk |
| Driver-only `toIdle` already ≤ 60 ms in every scenario, including active "zzzzzz" | Do Steps 1, 2 and 6 now. Do Steps 3–5 for correctness and as 6.3's prerequisite, but sell the PR on correctness, not speed |
| Match + stamp ≥ 50 ms | Unexpected. Profile `DivinityModFilterData.Match` and the notification fan-out before anything else |

### Step 1: extract `ModFilterQuery` (Core, pure refactor plus a null guard)

- **What:** B §5.1. `src/Core/Models/ModFilterQuery.cs` with `Parse(string)` and `IsMatch(DivinityModData)`. It keeps today's
  semantics byte for byte: an untrimmed plain phrase on `Name`, `PropertyContains` families, AND across terms, an empty value
  matching all, and free text dropped when any property contains "Name". The regexes become `static readonly`.
  - Add A's B4 null-coalescing for `Description`, `FileName` and dependency fields.
  - The instance regexes at `MainWindowViewModel.cs:4172-4173` go.
- **Gain:** ≤1 ms on the `@` path. It removes the `@desc`/`@file` crash, which could kill that list's filter subscription.
  It also gives fuzzy its seam.
- **Measure:** the probe's "Filter parse" and "Filter match" timers, before and after. Add a parity check: run old and new
  `IsMatch` over the corpus × ~200 queries and log any mismatch (TEMP).

### Step 2: count flips (`SetFilterMatch`)

- **What:** A §3 B2. Write `Visibility` only when it differs and count flips. On a hide, also set `IsSelected=false`, which is
  equivalent to today because a hidden mod cannot become selected (`DivinityModData.cs:190`).
- **Gain:** **0** on its own (§2.1). It exists so Step 3 can skip `Refresh()` when `flips == 0`.
- **Measure:** none needed. The probe's `flips` field just has to be correct.

### Step 3: filter through the default collection view, and drop the row's Visibility binding

- **What:**
  - Install `view.Filter = item => item is not DivinityModData m || m.Visibility == Visible` on
    `CollectionViewSource.GetDefaultView(ActiveMods/InactiveMods)`, in `WhenActivated` after the `ItemsSource` binds
    (`HorizontalModLayout.xaml.cs:526-528`). This is the same view `Sort()` already uses (`:762-769`).
  - Add `IModViewLayout.RefreshFilter(list)`. It calls `view.Refresh()`, then prunes any hidden mod still in `SelectedItems`
    (A's belt-and-braces).
  - The VM calls it when `flips > 0`.
  - Delete `MainResourceDictionary.xaml:369`.
  - `ItemsSource` stays the `ObservableCollectionExtended`, so identity checks (`ModListDropHandler.cs:116`,
    `ModListView.cs:228`) keep working.
- **Scroll after a refresh** (simplified from B §5.3): if `SelectedItem` is still in the view, `ScrollIntoView` it. Otherwise
  scroll to the top. Do **not** anchor on "the first visible item" on every keystroke. Under `ScrollUnit=Item` that is an index
  into a list that just changed, and it fights the user.
- **Gain** [inferred]:
  - removes all collapsed-container realization: **~20–75 ms** on inactive narrow passes, **~0.1–0.4 s on active narrow
    passes**;
  - shrinks any UIA walk from up to 935 rows to ~28;
  - expected per pass after the step: **~20–80 ms**, flat across list size and match count. That is B's estimate, and it is
    dominated by one Reset re-realizing ~28 rows at 0.5–1.5 ms each before 6.1, less after;
  - passes with zero flips cost ~2 ms.
- **Measure:** the same driver scenarios. Pass if:
  - every scenario's in-process `toIdle` is ≤ 100 ms (Debug);
  - realized containers are ≤ viewport + cache (~30) after every pass;
  - `GetChildren` per pass scales with realized rows, not list length;
  - `perfscroll` shows no regression.

### Step 4: map drops by item, not by view index (must land **before or with** Step 3)

- **What:** B §5.4 `ResolveSourceInsertIndex`, called at `ModListDropHandler.cs:133`:
  - **a target item under the cursor:** `IndexOf(targetMod)`, plus 1 on `AfterTargetItem`;
  - **no item, and an empty view:** append, at `destinationList.Count`;
  - **no item, and a non-empty view:** after the last visible item;
  - **`ManualDropInfo`:** pass through.
- **Why A's "no change" is wrong**, case by case [certain from `dl-gong/DropInfo.cs:60-87, 372`]:
  1. **Drop into empty space below the last visible row.** `itemParent` is null, so `UnfilteredInsertIndex` returns
     `InsertIndex = Items.Count`, the *view* count k. The mods land at load-order position k, mid-list.
  2. **Drop onto a list filtered to zero matches.** k = 0, so they land at the **top of the load order**.
  3. **Drop on the lower half of visible row A.** It maps to "before the next visible row B", so the dropped mods silently
     jump past any hidden mods between A and B. Today they land directly after A.
  - Under today's stamping, view index equals source index, so none of these occur. They would be **new load-order
    regressions**.
- **Gain:** correctness only. It also fixes the pre-existing header-sorted drop bug (§2.3).
- **Order:** it is behaviour-preserving under stamping except for that sorted fix, so it can be the PR's first functional
  commit, which keeps the series bisectable.
- **Measure:** T-D1…T-D7 (§9), with `modsettings.lsx` diffed.

### Step 5: view and source index fixes (with Step 4)

- **`MoveSelectedMods`** (`HorizontalModLayout.xaml.cs:307,359`):
  - **Insert index:** `other.SelectedItem is DivinityModData t ? otherList.IndexOf(t) : -1`. A −1 is clamped to 0 at
    `ModListDropHandler.cs:172-175`, as today.
  - **Re-select** (`:345,398`): re-select with `Items.IndexOf(mod)`, clamped against `Items.Count`.
- **Alt+Arrow** (`ModListView.cs:286`): `ScrollIntoView(list[targetScrollIndex])`.
- **`FocusSelectedItem`** (`HorizontalModLayout.xaml.cs:64-68`): guard `lv.Items.Count > 0`.
- **Gain:** correctness, no exceptions with a filter applied.
- **Measure:** T-K1…T-K3.

### Step 6: O(n) renumber after a drop (free side commit)

- **What:** `for (var i = 0; i < ActiveMods.Count; i++) ActiveMods[i].Index = i;` replaces the `IndexOf` per mod
  (`ModListDropHandler.cs:218-221`), which costs about 437k compares for 935 mods.
- **Gain:** a few ms per drop in Debug [inferred].
- **Measure:** a probe timer around `Drop`.

### Step 7: throttle 500 → 150 ms, plus `DistinctUntilChanged` (last, and gated)

- **What:** `MainWindowViewModel.cs:5250-5254`:
  `.Throttle(150 ms).DistinctUntilChanged().ObserveOn(MainThread)`.
  - Direct callers (drop, `LoadModOrder`) are unaffected, so a list change re-filters with the same text.
- **Gate:** only once Step 3's driver numbers are ≤ 60–100 ms per pass. Otherwise a short throttle makes typing worse.
- **Gain:** **−350 ms on every settle** [certain arithmetic]. Felt settle goes from 1.7–3.8 s (harness) to **~0.2 s** [inferred].
- **Measure:**
  - the owner's original UIA harness, for comparability with 0.3; pass if it reports ≤ 0.3 s settle, once its own floor
    from Step 0 is subtracted;
  - a hand test: hold a key in the box, and the UI must not hitch.

### Optional Step 8: live filtering, only on evidence

`ListCollectionView.IsLiveFiltering` with `LiveFilteringProperties = {"Visibility"}`. Adopt it only if the probe shows Step 3's
`resetonly` floor above ~50 ms, *and* live filtering wins in both the few-flip and the all-flip cases (A §3 C, B §6).

### Expected cost after each step (in-process, Debug) [inferred unless marked]

| After | Inactive narrow / cleared | Active broad / cleared | Active narrow (unmeasured today) | Felt settle |
|---|---|---|---|---|
| Today (harness) | 1.24–1.37 s [measured] | 1.34–1.39 s [measured] | predicted worst | 1.7–3.8 s |
| Today (driver, no UIA) | **unknown**: Step 0 | unknown | unknown | — |
| Steps 1–2 | ≈ today | ≈ today | ≈ today | ≈ today |
| Step 3 (+4, 5) | ~20–80 ms | ~20–80 ms; ~2 ms if 0 flips | ~25–80 ms | throttle-bound, ~0.55 s |
| Step 7 | same | same | same | **~0.2 s** |
| Fuzzy F1–F4 | +≤0.5 ms match, +≤0.1 ms rank sort [measured, prototype]. The inactive list refreshes on every ranked keystroke (~28 rows) | +≤0.5 ms | +≤0.5 ms | ~0.2 s |

---

## 4. Correctness design points (perf branch)

### 4.1 Selection under Reset, and the wt-perf fixes

- [WPF, verify in T-S2] On a Reset, `Selector` keeps selected items that are still in `Items` and removes the others, reporting
  them in `SelectionChanged.RemovedItems`.
- **309b81e** (wt-perf `HorizontalModLayout.xaml.cs` `UpdateIsSelected`) rebuilds `IsSelected` from `SelectedItems`, so hidden
  mods end up false. That is today's "hidden ⇒ deselected" rule.
- **On master**, the delta replay (`HorizontalModLayout.xaml.cs:212-237`) reaches the same state.
- **396d7b2** `SelectMods` → `SetSelection(group.Where(x => x.Visibility == Visible))`. Under Step 3, "Visible" means "in the
  view", so it never hands the Selector an item it does not contain. **Compatible.**
- **The fallback.** If T-S2 shows Reset wipes the selection, capture and restore around `Refresh()` with wt-perf's
  `ModListView.SetSelection`. That fallback lands after 6.1's selection commits, so `perf/filter` does not duplicate
  `SetSelection`.
- **This is not new exposure.** Every drop already calls a full `Refresh()` (`ModListDropHandler.cs:236-248`), so Reset
  semantics are exercised today.
- **A side benefit** [inferred]:
  - Ctrl+A and Shift-ranges stop putting hidden rows into `SelectedItems` (`SelectAll` at `HorizontalModLayout.xaml.cs:97-101`);
  - "hidden selection reappearing in the view after clear" goes away.

### 4.2 Merge surface with wt-perf

**The hunks are separate:**

| File | `perf/filter` touches | wt-perf touches |
|---|---|---|
| `HorizontalModLayout.xaml.cs` | `MoveSelectedMods`, `FocusSelectedItem`, `WhenActivated`, new `RefreshFilter` | `SelectMods`, `UpdateIsSelected`, `GetListViewFor` |
| `ModListView.cs` | `:286` | `:194-255` |
| `MainResourceDictionary.xaml` | `:369` | `ModNameTemplate`, the column widths |

- Only the TEMP probe lines in wt-perf's `UpdateIsSelected` need hand-resolving, and that is already required (plan §6.1).
- Verify with a scratch merge of both branches into `fork/main` before either PR.

### 4.3 Drag and drop

This is the behaviour after Step 4:

- **Between visible rows:** it lands exactly where the user aimed in load order, after A or before B, regardless of hidden
  neighbours.
- **Below the last visible row:** after it.
- **On an empty filtered list:** appended.
- **A dropped mod that does not match the destination filter** disappears after the drop, as it effectively does today.
- **The drop handler's own refreshes.** It re-runs the filter (`:250-258`) and schedules `RefreshDataView` (`:236-248`). A drop
  can therefore Reset twice. Leave both: deduping them is cosmetic.

### 4.4 Toolkit neutrality

- The VM never touches `ICollectionView`. It calls `Layout.RefreshFilter`, and the query lives in Core.
- `Visibility` (a `System.Windows` type in Core) stays as the flag, because renaming it to a neutral `IsFilterMatch` would
  conflict with wt-perf's `SelectMods`. That rename is a later tidy-up.
- An Avalonia port would replace `RefreshFilter` only.

---

## 5. Rejected alternatives

- **DynamicData `Filter` + `Bind` as the `ItemsSource`** (plan step 5 as written). The read-only bound collection breaks:
  - `TargetCollection == ActiveMods` (`ModListDropHandler.cs:116`);
  - `TryGetList().Insert/RemoveAt` (`:160,192`);
  - the Alt+Arrow type check (`ModListView.cs:228`);
  - the list-identity checks in the layout.
  - A plain DD `Filter` also needs `AutoRefresh` to re-evaluate (house rule).
  - B §7 is right to reject it on WPF. It remains the natural shape for an Avalonia era.
- **Plan steps 1–2 as perf work.** Eliminated (§2.1). Record them as eliminated in the plan.
- **Trigram index, or edit distance as the primary matcher** (C §3.1). The index is useless for 1–2-character queries, and a
  linear scan already costs <0.5 ms.

---

## 6. Fuzzy design (fork-only, `feat/fuzzy-search`, cut from `perf/filter`)

This is C's design (F1–F4), with the corrections in §6.6.

### 6.1 Algorithm

- **The matcher.** fzf-v2-style scoring: Smith-Waterman with affine gaps, run only inside the window a greedy pre-pass finds,
  with the fzf boundary bonuses (white 10, delimiter 9, other non-word 8, camelCase 7, digit 7, cased↔uncased script 7). It
  falls back to fzf-v1 greedy above 2,048 cells.
- **Two fork rules:**
  - **R1, anchored subsequence.** A token's first character must start a word, unless the token is a plain substring. A gap
    may end mid-word only after ≤2 skipped characters. Effect [measured, prototype]: "imp ui" 43→2 hits, "mcm" 57→8,
    "tav" 78→57 (53 today).
  - **R2, capped boundary-gap penalty (−5).** "mcm" then ranks *Mod Configuration Menu* first.
- **Superset property** [measured, re-run here]: fuzzy found all 398,340 of today's substring hits. Turning fuzzy on only
  *adds* hits.
- **Cost** [measured, Release, Linux]:
  - 3–81 µs per pass over 186 or 935 rows; 0.08–0.41 ms in Debug;
  - rank sort of 170 hits: 86 µs.
  - Synchronous on the UI thread, with no cancellation and no off-thread work. `shadowfuzzy` on Windows confirms this (Step 0).

### 6.2 Keys

- **What they are.** `SearchKey` (Core, immutable) holds the folded text, a per-character bonus array and a source-index
  map. Folding is lower-case, diacritics stripped (NFKD minus nonspacing marks), width and compatibility folded, and a small
  table for `ß/æ/œ/ø/ł/ı/þ`. Kana, Hangul and CJK are not decomposed.
- **Where they live.** On `DivinityBaseModData`: `NameKey`, `DisplayNameKey` (shared when it equals `Name`) and `AuthorKey`.
  They are not `[Reactive]` and not `[DataMember]`. They are rebuilt lazily when the source string reference changes.
- **When they are built.** Warmed once in the background part of `RefreshAsync`, before `SetLoadedMods` is scheduled on the
  UI thread (`MainWindowViewModel.cs:2416-2430`, with `SetLoadedMods` at `:2419`). That costs 2–5 ms [measured]. This satisfies the plan's "precompute once at
  load". The lazy path covers mods added later.
- **Match surface.** Free text matches `Name` **and** `DisplayName` (best of the two). That fixes today's mismatch, where the
  row shows `DisplayName` but search reads `Name` (`DivinityBaseModData.cs:77-80`, `MainWindowViewModel.cs:4291`).

### 6.3 Ranking

- **Tiers:**
  - **0** prefix;
  - **1** word-start substring;
  - **2** substring;
  - **3** subsequence.
- **Sort order:** tier ascending, then score descending, then shorter folded key, then `DisplayName` (ordinal, ignoring
  case), then UUID.
- **The guarantee:** everything today's substring search finds ranks **above** every fuzzy-only hit.
- **Where scores live.** In a per-pass `ModSearchRanking : IComparer` owned by the VM (a dictionary keyed by reference), not on
  the model.

### 6.4 Active vs inactive (the decision the plan asked for)

- **Active list: fuzzy decides inclusion only, never order.**
  - It gets no `CustomSort`, and the filter pass never writes `ActiveMods`.
  - Load order is unchanged by construction. T-A1 proves it on the `modsettings.lsx` bytes.
  - R1 keeps real-word queries at the same hit counts as substring ("spell" 34→34, "hair" 42→42 [measured]).
  - If the owner finds it noisy, the knob is C's `WordPrefix` strictness, not a threshold.
  - A user header sort on the active list stays as it is today.
- **Inactive list: inclusion and ranking.**
  - Ranking applies `ListCollectionView.CustomSort` inside `DeferRefresh`, so filter and sort cost one Reset.
  - **Header sort:** save the header `SortDescriptions` when ranking starts. A header click during a search clears
    `CustomSort` and wins for the rest of that search. Clearing the query restores the saved sort.
  - Scroll to the top on a ranked refresh.
  - It is safe to rank because the inactive list is a set (`MainWindowViewModel.cs:2143-2144`).
  - Ranking applies only when there is a free-text or exact `@name` term. A field-only query such as `@author x` keeps the
    user's sort.

### 6.5 `@` syntax

- **Unchanged:** the extraction regexes are kept verbatim. `PropertyContains` families (`@filename` = Name ∪ File, and it
  suppresses free text) stay. Every other field keeps substring matching, made diacritic-, width- and kana-insensitive
  through `CompareOptions` (F3b).
- **Exactly two deliberate changes:**
  - an exact `@name x` feeds the fuzzy term;
  - a trailing, still-being-typed `@word` with no whitespace yet is ignored, so the list no longer blanks while the user types
    `@auth`.
- **Quoted text with no `@`** (`"tavern brawl"`) is an exact phrase. Unquoted multi-word text is AND'd and order-free.
- **Setting:** `[DefaultValue(true)] [SettingsEntry("Fuzzy Search", …)] [DataMember, Reactive] public bool EnableFuzzySearch`.
  It needs all three attributes (CLAUDE.md §4). With it off, `Parse(text)` behaves exactly as `perf/filter`.

### 6.6 Corrections and deferrals relative to S5-C

1. **The missing-entry rank.** `ModSearchRanking.Compare` must treat a mod absent from the dictionary as *worst*
   (`Tier = int.MaxValue`), not as `default` (Tier 0, the best). Otherwise a mod dropped into a ranked list jumps to the top
   until the next pass.
2. **The refresh condition.** C's `if (flips > 0 || ranking != null || modDataList == InactiveMods)` refreshes every unranked
   inactive pass. Ship it with the `_inactiveWasRanked` refinement, so the extra refresh happens only on the ranked→unranked
   transition.
3. **The "iui" golden.** C's T-U5 seed expects "iui" → ImprovedUI first. The prototype re-run ranks *Better Inventory UI* first
   (83 vs 73). Take the goldens from the owner's exported corpus and the owner's own expectations, not from the seed list.
4. **Deferred:**
   - **F5 highlight** until 6.1's `CachedTextBlock` is in `fork/main`. C's overlay design (an inherited attached
     `ModSearchResult`, a `SearchHighlightOverlay` behind the text, no per-mod notifications) is sound.
   - **F6 find-in-place** and **F7 typo fallback**: owner opt-in only.

---

## 7. Stage 6.3 dividers

- **Flat divider rows in the same `ObservableCollectionExtended`**, never `GroupDescriptions`. That keeps the `IsGrouping` →
  `CanContentScroll=False` trap inert (`MainResourceDictionary.xaml:460-462`).
- **The predicate gains a divider case.** One source-order pass computes, per divider, its match count and whether it is
  collapsed:
  - a mod shows when it matches **and** its category is not collapsed;
  - a divider shows when the query is empty or it has a match.
- **Collapse is a flag flip plus `RefreshFilter`.** It costs about the viewport, and it never realizes the hidden block. With
  Visibility stamping it would realize every row in the collapsed block (§2.2 h3).
  - **This is why Step 3 is needed even if Step 0 shows small numbers.**
- **Drop onto a collapsed divider** is one more case in `ResolveSourceInsertIndex`:
  - lower half: after the category's last hidden child;
  - upper half: before the divider.
- **Renumbering skips dividers.**
- **The element-type change** (a divider subclass or a common base) is 6.3's call. Nothing here constrains it.
- **The fuzzy loop** skips `IsDivider` rows, one line. The inactive list has no dividers, so ranking and dividers never meet.

---

## 8. Branches and PRs

| Branch | From | Commits | Upstream? | Effort [inferred] |
|---|---|---|---|---|
| `probe/filter` (TEMP) | `perf/list-render` | Step 0: timers, realized and GetChildren counters, `PerfFilterDriver`, variants `nofilter`/`viewfilter`/`resetonly`/`shadowfuzzy`, corpus dump | never | 0.5–1 d + a measurement session |
| `perf/filter` | clean `master` | c1 `ModFilterQuery` + null guard (Step 1) · c2 drop index by item (Step 4) · c3 view/source index fixes (Step 5) · c4 flip counting + collection-view filter + remove style setter + `RefreshFilter` (Steps 2–3) · c5 O(n) renumber (Step 6) · c6 throttle 150 ms + `DistinctUntilChanged` (Step 7, after re-measure) | **yes**, one PR with before/after driver numbers. c1 and c5 also stand alone if the gate says stop | 2.5–3.5 d incl. Windows testing |
| `perf/filter-live` (optional) | `perf/filter` | Step 8 | yes, only if it wins | ~1 d |
| `feat/fuzzy-search` | `perf/filter` | F1 `src/Core/Search/*` + `tests/Core.Search.Tests` (net8.0, links the files, runs on Linux) · F2 keys + warm-up · F3 query/setting + F3b folded `@` compare · F4 inactive ranking (with the §6.6 fixes) · later F5 (after 6.1) | **no**, fork-only. It touches upstream files at about 7 small sites. A rebase onto a new `perf/filter` conflicts only in `OnFilterTextChanged` | F1–F4: 2–2.5 d; F5: 1 d |
| `fork/main` | — | merges `perf/list-render` (selection commits untangled from TEMP), `perf/filter`, `feat/fuzzy-search`, and later `feat/categories` | — | scratch-merge check before each PR |

House style in every file: tabs, CRLF, UTF-8 BOM, Allman, `var`, file-scoped namespaces, no `string?`. Keep the
ReactiveUI.Fody 19.5.41 and ReactiveUI 20.1.63 pin. C's prototype is LF with no BOM and must be converted when ported.

---

## 9. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Step 0 shows the in-process cost was never seconds, so the perf PR's headline shrinks | **Medium-high** (§2.2) | Sell `perf/filter` on correctness, the throttle win and 6.3 readiness. Report the harness floor honestly |
| Reset clears the multi-selection | Low [WPF] | T-S2. Capture/restore fallback via `SetSelection` (§4.1) |
| Drop index regressions | Low after Step 4; **high without it** (A) | Step 4 before Step 3. T-D1…T-D7 with `modsettings.lsx` diffs |
| `ScrollIntoView` of a mod hidden in the destination after `MoveSelectedMods` (`HorizontalModLayout.xaml.cs:336,389`) | Unknown [WPF] | T-K1 with a destination filter that hides the moved mod |
| `DistinctUntilChanged` suppresses a needed re-filter after a list change | Low: the list-changing paths call `OnFilterTextChanged` directly (`:2146-2147`, drop handler) | T-F3: switch profile or order with filter text present, and refresh |
| A mod moved between lists keeps the other list's stamp | Same as today: `LoadModOrder` re-stamps both lists | T-F3 |
| Fuzzy noise in the unranked active list | Low (R1 measured) | `shadowfuzzy` review before shipping. The `WordPrefix` knob |
| Ranked list header-sort interplay ([WPF] adding a `SortDescription` may or may not clear `CustomSort`) | Medium | Clear `CustomSort` explicitly in `Sort()`. T-R2/T-R3 |
| Windows ICU/NLS differs from the Linux prototype | Low | Run `fuzzproto` on Windows with the exported corpus |

---

## 10. Test plan (Windows; the repo has no test project today)

**Unit tests** (fork-only `tests/Core.Search.Tests`, xUnit, `net8.0`, run on Linux too):

- `ModFilterQuery` parity table against a copy of today's loop. Cover: plain text including a trailing space, `@author x`,
  `@author "two words"`, multiple fields (AND), `@` with nothing, `@selected`, `@modified 2024-01-01`, an unknown field, and
  null fields.
- Fold table. Scorer invariants: the superset property, monotone under extension, positions consistent with the score, R1
  cases. Parser rows from C §5.1.
- Ranking goldens on the owner's corpus.

**Perf** (probe build, §3 Step 0):

- every scenario, median of 5, Debug and Release, with and without UIA, before and after Steps 3 and 7;
- pass criteria as given in Steps 3 and 7;
- re-run `perfscroll` for no scroll regression.

**Functional and manual** (real 1,121-mod setup; diff `modsettings.lsx` and the saved order JSON before and after each
session):

| ID | Steps | Expect |
|---|---|---|
| T-F1 | "a", "zzzzzz", clear, on each list | Correct "N Matched, M Hidden" (`MainWindowViewModel.cs:4178-4188`); hidden mods are absent, not blank rows; the scrollbar tracks matches |
| T-F2 | `@author x`, `@uuid part`, `@selected`, `@desc x`/`@file x` on mods without a description or path | Same results as master; no exception; the filter still works afterwards |
| T-F3 | Filter text present, then switch profile, switch order, F5 refresh | Stamps re-applied; counts right |
| T-S1 | Select 5 visible, filter so 2 hide | 3 stay selected; the 2 hidden are deselected in the model |
| T-S2 | Select 50 (beyond the viewport), filter keeping all 50, clear | Selection survives both Resets (decides the §4.1 fallback) |
| T-S3 | The 309b81e/396d7b2 repro on a scratch merge of `perf/filter` + `perf/list-render` | Single selection, no stale highlights |
| T-S4 | Ctrl+A and Shift-range in a filtered list | Only visible rows are selected in the ListView *and* the model |
| T-D1 | Drop on the lower half of visible A, with hidden H after A | Lands right after A, before H |
| T-D2 | Upper half of visible B | Right before B |
| T-D3 | **Empty space below the last visible row** of a filtered active list | After the last visible row, **not** at view-count position k |
| T-D4 | Drop into an active list filtered to zero matches | Appended at the end, **not** index 0 |
| T-D5 | Reorder within a filtered active list past hidden mods | `Index` contiguous 0..n-1; export matches |
| T-D6 | Header-sorted active list, then drop | Lands next to the visual target in load order (a fix vs master) |
| T-D7 | Drag 5 from the ranked inactive list into the active list | They land at the drop point; a dropped-back mod does not jump to the top of the ranked list (§6.6.1) |
| T-K1 | Move-selected (Enter / Alt+Right) between filtered lists, with and without a destination selection, and with a destination filter hiding the moved mods | Correct insert; re-selection on a visible row; no exception |
| T-K2 | Alt+Up/Down in filtered and header-sorted active lists | No exception; scrolls to the moved mod |
| T-K3 | Arrows, PageUp/Down, Home/End, focus-swap hotkeys with 0, 1 and many matches | No exceptions in the log |
| T-A1 | 20 searches on the active list with fuzzy on, including ranked-looking queries | `ActiveMods` UUID sequence, `Index` values and `modsettings.lsx` are byte-identical to before |
| T-R1–R3 | Inactive "tav" ranks tier 0 first and scrolls to the top; backspace to empty restores the prior header sort; a header click mid-search wins until the search clears | As stated |
| T-I1 | "pokemon", "faerun", "日本", "ｃａｍｐ" | Folded matches |
| T-X1 | Narrator/NVDA: filter, then read the list | Only matches are read; the Matched/Hidden label is announced |
| T-O1 | Overrides list with an active filter | Unaffected; columns stay linked |
| T-O2 | Fuzzy setting off | Identical to `perf/filter` |

---

## 11. Open owner decisions

1. **Run order and idle detection of the 0.3 measurements.** What was the order, and what idle window did the harness use?
   Step 0's `nofilter` control answers this anyway.
2. **The background UIA client.** Is an accessibility client normally running on your PC: Narrator, a password manager,
   voice access, a text-input host? `ClientsAreListening=True` says something is. If so, Step 3 helps everyday use, not only
   the benchmark.
3. **Fuzzy on the active list.** Anchored fuzzy inclusion (recommended), or `WordPrefix` strictness?
4. **Defaults and extras.** Fuzzy on by default in fork builds (recommended)? F6 find-in-place and F7 typo fallback: wanted?
5. **Should the active-list search also filter the overrides list?** That becomes moot once 6.2 folds overrides into the
   order.
