# Stage 5 (Perf B: filter and search), angle C: fork-only fuzzy search on a cheap filter path

Written 2026-11-03 against master `fc35358`, `perf/list-render` (wt-perf, head `9f2b163`), upstream/avalonia (ideas only)
and gong-wpf-dragdrop's `DropInfo.cs` (copy in `scratchpad/dl-gong`; that copy's version is unconfirmed, and the csproj pins 4.0.0).

This design builds on the two sibling designs rather than repeating them:

- `docs/review/stages/alternatives/S5-A-cheapest-first.md` (collection-view filter, steps B1–B5).
- `docs/review/stages/alternatives/S5-B-collection-filtering.md` (`ModFilterQuery` extraction, index fixes, gate).

Both agree on the upstreamable `perf/filter` branch. This document designs the **fork-only fuzzy layer** on top of it, and
asks `perf/filter` for only two neutral seams (§8.0).

The WPF app was not built or run. **It is not a paper design, though.** The matcher, the keys and the query were prototyped
in C#, built with a .NET 8.0.427 SDK installed into the scratchpad, and benchmarked in Release and Debug on this Linux
container. The source is in `scratchpad/fuzzproto/` (§A).

Labels used throughout:

| Label | Meaning |
|---|---|
| **[certain]** | Follows from source cited as path:line. Paths are repo-relative. |
| **[measured]** | Prototype numbers from this container (Xeon 2.1 GHz, Linux ICU 74, synthetic 1,121-name corpus). |
| **[WPF]** | Framework behaviour I know but could not re-read here. |
| **[inferred]** | A hypothesis that §7 has to confirm. |

Reports read: `docs/review/areas/AUDIT-PLAN.md`, `AUDIT-CLAUDE.md`, `AVA.md` and `BLD.md`. No VM2, S6 or UI report existed when I
started or when I finished.

---

## 0. Headline

1. **Fuzzy search is affordable now.**
   - Scoring all 1,121 mods costs **0.08–0.37 ms per keystroke in Debug** and **0.02–0.12 ms in Release** [measured].
   - Today's culture-aware `Contains` loop costs 0.06–1.1 ms in Debug and 0.06–1.6 ms in Release [measured; same order or worse].
   - The measured 1.2–3.3 s goes to WPF realizing collapsed containers (S5-A §2.3, AUDIT-PLAN claim 25), not to matching.
   - So fuzzy needs **no** off-thread work, no cancellation and no index. It runs synchronously in `OnFilterTextChanged`
     with ~40× headroom under 16 ms.
2. **Algorithm: fzf-v2-style subsequence scoring** (Smith-Waterman with affine gaps, run only inside the window a greedy
   pre-pass finds), over a **precomputed folded key plus a per-char boundary-bonus array**. Two rules on top of fzf, both
   measured to remove noise:
   - **Anchoring:** the first char of each token must start a word, unless the token is a plain substring. A gap may end
     mid-word only after ≤2 skipped chars. Anything longer must land on a word start. Effect on "imp ui": 43 hits → 2.
     Every hit today's search finds is still found (398,340 of 398,340).
   - **Capped boundary gaps:** a gap that ends on a word start costs at most 5. "mcm" then ranks *Mod Configuration Menu* first.

   Rejected: a trigram index (useless below 3 chars, the commonest case while typing) and edit distance as the primary matcher.
   Typo tolerance is an optional fallback (F7).
3. **Ranking:**
   - **Inactive list:** ranked by (tier, score, shorter name, name) through `ListCollectionView.CustomSort`, with one Reset
     per pass. A header click overrides the ranking. Clearing the search restores the previous header sort.
   - **Active list:** the same matcher for **inclusion only**, never reordered, because order there is load order.
     Matched-character highlighting comes later (F5, after 6.1's `CachedTextBlock`). An opt-in "find in place" mode that dims
     instead of hiding comes later still (F6).
4. **The `@field value` syntax is kept byte-for-byte.**
   - Fuzzy changes free text and exact `@name` only. Every other field keeps substring matching, made diacritic-, width-
     and kana-insensitive.
   - A half-typed trailing `@auth` no longer blanks the list.
5. **Branches:**
   - `perf/filter` (upstreamable; S5-A/S5-B) contains no fuzzy concept at all.
   - `feat/fuzzy-search` (fork-only) is cut from `perf/filter` and holds 7 commits.
   - The matcher files are pure C# with no `System.Windows`. A `net8.0` test project runs them on Linux.
6. **Expected per-keystroke cost after the whole stack** [inferred, Debug]:
   - Roughly `perf/filter`'s ~20–80 ms, plus ≤1 ms for scoring and sorting.
   - Highlighting adds ~3 ms.
   - Felt settle time with a 150 ms throttle is ~0.2 s, against 1.7–3.8 s today.

---

## 1. Owner intentions this design is built on (you asked for this list)

1. **Platform.** Stay on legacy WPF `master`. Windows comes first. Keep Linux and Avalonia possible, so new logic goes in Core,
   toolkit-neutral where that is free. Add no new `System.Windows` types to Core (AVA.md:326).
2. **Upstream hygiene.**
   - Perf fixes are clean PRs cut from master for LaughingLeader.
   - Fork features (fuzzy, categories, overrides-in-order) live on separable branches.
   - The perf part of Stage 5 is upstreamable. Fuzzy is not.
3. **Measure first.**
   - Settle questions with numbers.
   - Record eliminated hypotheses as eliminated. The plan already rejected the throttle theory, and AUDIT-PLAN refuted steps 1–2.
   - Keep Debug baselines as controls.
4. **Search should feel live, then fuzzy and ranked.**
   - Keep `@author foo`.
   - Precompute a lowercase key once.
   - Decide explicitly about the active list.
5. **Load order is sacred.** Nothing a view does (search, sort, collapse) may reorder `ActiveMods` or change what gets
   saved or exported.
6. **Stage 6 stays intact.**
   - 6.1 render work: `CachedTextBlock`, and the selection fixes 309b81e/396d7b2 that reconcile `IsSelected` with
     `ListView.SelectedItems`.
   - 6.3 categories as flat, collapsible divider rows, never WPF grouping (the `IsGrouping`→`CanContentScroll=False` trap,
     `MainResourceDictionary.xaml:460-461`).
7. **Accessibility keeps working.** Screen-reader behaviour is on the verify list.
8. **House rules.**
   - Keep the ReactiveUI.Fody 19.5.41 pin.
   - Use `ToUIPropertyImmediate` when logic reads an OAPH.
   - Put `AutoRefresh` before `Filter`.
   - No `string?`.
   - Tabs, CRLF, BOM, Allman, `var`.
   - User-visible failures go through `ShowAlert`.
9. **Process.** As many agents as needed, at medium or high effort. Evidence is cited. Nothing is edited in the repo.

---

## 2. Diagnosis

### 2.1 Where the time goes (agreeing with S5-A/S5-B; summarised)

- **[certain]** `OnFilterTextChanged` (`src/GUI/ViewModels/MainWindowViewModel.cs:4199-4313`) walks one list (`:5250-5254`).
  It stamps `DivinityModData.Visibility` (`src/Core/Models/DivinityBaseModData.cs:69`, `[Reactive]` and therefore
  `RaiseAndSetIfChanged`) and deselects hidden mods.
  - The row hides itself through `<Setter Property="Visibility" Value="{Binding Visibility}">`
    (`src/GUI/Themes/MainResourceDictionary.xaml:369`).
  - The items stay in the `ItemsControl` (Recycling virtualization, `src/GUI/Views/HorizontalModLayout.xaml:119-120,223-224`).
- **[WPF + inferred]** Collapsed containers are 0 px tall, so the panel keeps realizing rows to fill the viewport. That cost,
  plus UIA walks over realized rows when a UIA client is attached (S5-A §2.4), is the 1.2–3.3 s.
  - The flat floor across 186 and 935 rows (AUDIT-PLAN claim 25) points at fixed per-pass or harness cost, not at matching.
- **[certain]** The matching itself cannot be the cost. The plain path is one `CompareInfo.IndexOf(m.Name, text, IgnoreCase)`
  per mod (`:4291`).
  - **[measured]** That costs 58–322 µs per 1,121-mod pass for Latin queries. For CJK or fullwidth queries it costs
    1.0–1.6 ms, which is ICU's slow path (§A table).
- **Conclusion.** Fuzzy only becomes visible once the WPF cost is gone. It must not *reintroduce* per-mod work that touches
  WPF, such as per-mod notifications for highlight state (§6.3).

### 2.2 What today's matcher does semantically: the compatibility surface [certain]

| # | Behaviour | Evidence |
|---|---|---|
| S1 | No `@`: the whole untrimmed text is one phrase, culture case-insensitive substring of `Name`. Not `DisplayName`, although the row shows `DisplayName` (the file name when `DisplayFileForName`). | `MainWindowViewModel.cs:4291`; `DivinityBaseModData.cs:77-80,166-168`; row text `MainResourceDictionary.xaml:715` |
| S2 | No diacritic, width or kana folding: "pokemon" does not find "Pokémon". **[measured]** 0 hits. | `CompareOptions.IgnoreCase` only, `:4291`, `DivinityModFilterData.cs:25` |
| S3 | `@` present: two regexes extract `@prop value` pairs, and quotes allow spaces in the value. Leftover text loses its quotes, is trimmed, and becomes **one** `Name` phrase, *unless any field's property contains "Name"*. | `MainWindowViewModel.cs:4172-4173,4211-4260` |
| S4 | A field matches when the typed property *contains* a keyword (`PropertyContains`). So `@filename x` matches Name **or** File, and also suppresses the free-text Name term. The families are OR'd within a term, and terms are AND'd. An empty value matches all. | `DivinityModFilterData.cs:28-31,34,36-110`; `MainWindowViewModel.cs:4253,4264-4285` |
| S5 | A half-typed `@auth` (no space yet) does not match the regex, so it becomes the Name phrase "@auth" and the list blanks while the user types the field. | `filterPropertyPattern` needs `[\s]+` after the property, `:4172` |
| S6 | `@desc x` / `@file x` can throw: `Description` is never initialised, `FileName` is null without a path, and `CompareInfo.IndexOf(null, …)` throws (S5-A §2.7). | `DivinityBaseModData.cs:146-162`; `DivinityModFilterData.cs:25` |
| S7 | Hidden mods are deselected (`IsSelected=false`), and `IsSelected` refuses `true` while not Visible. | `MainWindowViewModel.cs:4282,4298`; `DivinityModData.cs:185-196` |
| S8 | `@selected` reads `IsSelected`, the value the filter itself just cleared on hidden rows. | `DivinityModFilterData.cs:83-86` |

The fork keeps S3, S4, S7 and S8 exactly. It changes S1, S2 and S5 deliberately, and only behind the fuzzy setting.
S6 is fixed upstream in `perf/filter` (S5-A B4 / S5-B c1).

### 2.3 A correction for the record: gong and sorted views [certain]

AUDIT-PLAN claim 23 says `UnfilteredInsertIndex` maps "a filtered *or sorted* view index" back to the source. It does not for
sorted views:

- It remaps only when `Items.Count != ItemsSource.Count` (`dl-gong/DropInfo.cs:66`).
- A sorted but unfiltered view therefore gets its *view* index back unchanged.
- That is harmless for the ranked inactive list, because the inactive order is meaningless and the index stays in bounds
  (`ModListDropHandler.cs:172-175` clamps it).
- It is the reason this design **never sorts the active list** by anything new (§6.2).

---

## 3. Algorithm choice

### 3.1 Options compared (N = 1,121 names, average 20 and maximum 40 characters in the corpus; real names may be longer)

| Option | Quality for this use | Cost per keystroke | Verdict |
|---|---|---|---|
| **Substring** (today) | No abbreviations ("iui"), no reordering of words ("brawl tav"), no diacritics | 0.06–1.6 ms [measured] | Baseline. It becomes tier 0–2 of the new ranking. |
| **fzf v1** (greedy forward, then backward shrink) | Good inclusion, but not the best alignment: "mcm" picks the leftmost m-c-m, not the word starts | O(n); ~50% of v2 | Used only as a fallback above `MaxCells`. |
| **fzf v2** (Smith-Waterman, affine gaps, boundary bonuses), windowed | Best alignment under the bonus model. Ranks acronyms and word starts well. | **0.02–0.12 ms Release, 0.08–0.41 ms Debug** for all lists [measured] | **Chosen**, with the two rules in §3.3. |
| **Trigram index** | Fails for 1–2-char queries ("a", "ui"), which are the commonest while typing. No word-start ranking. Needs maintenance on rename and load. | Index ~1–2 MB; lookups fast | **Rejected.** It is built for 10⁵+ items; a linear scan of 1,121 already costs < 0.5 ms. |
| **Edit distance** (Levenshtein, or Sellers for substrings) | Typo tolerance, but bad for typing prefixes and abbreviations. Thresholds depend on length. | O(n·m) per mod, ~3–5× v2 [inferred] | **Rejected as primary.** Optional fallback F7, run only when a token has zero hits. |
| **Bitap / Myers bit-parallel** | Fast k-error substring search, but no ranking model | O(n) | Implementation option for F7 only. |

### 3.2 Scoring model (constants from junegunn/fzf `src/algo/algo.go`, as recalled; tuning is ours)

- `ScoreMatch` = 16.
- Gaps: start −3, extension −1.
- Boundary bonuses, precomputed per key char from the class of the previous *source* char
  (`fuzzproto/FuzzyMatcher.cs:45-74`):

  | Previous → current | Bonus |
  |---|---|
  | white → word | 10 |
  | delimiter `/,:;\|` → word | 9 |
  | other non-word (`_ - . ( [ =` …) → word | 8 |
  | lower→Upper (camelCase) | 7 |
  | non-digit → digit | 7 |
  | **cased ↔ uncased letter** (Latin↔CJK, our addition) | 7 |

- A run of consecutive matches keeps at least +4 and inherits its chunk-start bonus.
- The first pattern char's bonus counts ×2.
- **Rank key** (`fuzzproto/ModSearch.cs`, `ModMatchComparer`):
  1. tier ascending: 0 prefix, 1 word-start substring, 2 substring, 3 subsequence;
  2. score descending;
  3. folded length ascending;
  4. `DisplayName` ordinal-ignore-case;
  5. UUID, for a total order.

  The tier is computed with one vectorised `IndexOf` per token. It guarantees that what substring search found today
  appears **above** any new fuzzy-only hit, so users coming from substring search see no regression at the top.

### 3.3 Two deliberate departures from fzf (each measured on the corpus)

| Rule | Why | Before → after [measured] |
|---|---|---|
| **R1: anchored subsequence.** A token's first char must start a word (bonus ≥ 7), unless the token is a substring (tiers 0–2 bypass the DP). After that, a gap may end **mid-word only after ≤ `MaxInWordGap` = 2 skipped chars**. Longer gaps must land on a word start. | Plain fzf takes "imp" from "Unl**im**ited Icons - **P**atch" and "Icons Co**mp**atibility". In an unranked active list that is visible noise. The ≤2 in-word gap keeps "lvl"→**L**e**v**e**l** and "tavrn"→Tavern. | "imp ui": 43 → **2** (ImprovedUI, ImprovedUI Assets). "mcm": 57 → 8. "seb": 19 → 1. "tav": 78 → 57 (53 today). |
| **R2: capped boundary gap.** A gap that ends on a word start (bonus ≥ 8) costs at most `MaxBoundaryGapPenalty` = −5, whatever its length. | Plain affine gaps punish acronyms over long names, so *More Camp* outranked *Mod Configuration Menu* for "mcm". | "mcm" top-1: More Camp (WIP) → **Mod Configuration Menu** (78 vs 70). |

**Superset property [measured].** I took 3,000 random substrings of corpus names and collected the 398,340 (query, mod) pairs
that today's `CompareInfo.IndexOf(IgnoreCase)` finds. Fuzzy missed **0**. Turning fuzzy on can therefore only *add* hits;
it never removes a hit someone relies on.

No minimum-score threshold is needed after R1. Thresholds of 14, 16 and 18 points per char were tried before R1 existed.
They pruned little and hurt legitimate acronym hits, so `MinAverageFuzzyScore = 0`.

### 3.4 Budget and cost bounds

- **Pre-pass:** m vectorised `string.IndexOf(char, start)` calls. Most non-matches exit here, which is why "zzzzzz" costs
  14 µs over 187 mods in Debug.
- **DP:** m × w cells, where w = last − first + 1 of the greedy window. It uses two rolling rows of 4 `int` spans, on the
  stack while w ≤ 256, and allocates nothing.
- **Bound:** `MaxCells` = 2,048 cells per key. Above that, the scorer uses fzf-v1 greedy scoring. Pattern length is not
  otherwise limited, because tokens are split on whitespace.
- **Worst realistic case** [inferred]: names 3× longer than the corpus and an 8-char token. That is ≤ 1.5 ms in Debug for
  1,121 mods, still 10× under budget.
- **Off-thread** is **not** needed. If a future field ever needs it (fuzzy over `Description`, which can run to hundreds of
  chars), §8 F3 notes the shape:
  - snapshot the keys, which are immutable and thread-safe;
  - score under `Throttle → Select(text => Observable.FromAsync(ct => Task.Run(…, ct))) → Switch → ObserveOn(Main)`;
  - apply on the UI thread, treating mods added since the snapshot as "rescore synchronously".

---

## 4. Match keys: precomputed once, folded

### 4.1 What a key holds (`fuzzproto/SearchKey.cs`)

```csharp
public sealed class SearchKey
{
	public static readonly SearchKey Empty = new(String.Empty, String.Empty, Array.Empty<byte>(), Array.Empty<int>());

	public string Source { get; }       // the string it was built from; reference-compared to detect staleness
	public string Text { get; }         // folded: lower-case, diacritics stripped, width/compat folded
	public byte[] Bonus { get; }        // per folded char: boundary bonus (§3.2), precomputed from source classes
	public int[] SourceIndex { get; }   // per folded char: source char index, for highlighting (F5)
	...
	public static SearchKey Build(string source) { ... }
}
```

Immutable, so it can be read from any thread.

**Memory** [inferred]: ~30 chars × (2 + 1 + 4 bytes) × up to 3 keys × 1,121 mods ≈ **0.7 MB**. If that matters, `SourceIndex`
can become lazy, because only realized rows need it.

**Build cost** [measured]: **1.5–2.4 ms warm, 3.9–5.2 ms cold** (including JIT) for 1,121 mods.

### 4.2 The fold pipeline (`SearchText.FoldChar`, the same function for keys and query tokens)

| Input | Rule | Example |
|---|---|---|
| ASCII | `c \| 0x20` for A–Z | `ImprovedUI` → `improvedui` |
| Latin / Greek / Cyrillic, letterlike, enclosed alphanumerics, Latin ligatures, fullwidth ASCII (`IsFoldable` ranges) | NFKD, drop `NonSpacingMark`, then `ToLowerInvariant` | `Faerûn`→`faerun`, `ﬁ`→`fi`, `Ａｒｍｏｒ`→`armor`, `İ`→`i` |
| Letters with no decomposition | Small table | `ß/ẞ`→`ss`, `æ`→`ae`, `œ`→`oe`, `ø`→`o`, `đ`→`d`, `ł`→`l`, `ı`→`i`, `þ`→`th`, `ς`→`σ` |
| Kana, Hangul, CJK ideographs | **Not decomposed.** `ToLowerInvariant` only, which is a no-op. | `ガ` stays `ガ` (NFKD would give `カ`+U+3099); `한` stays a syllable (NFKD would give jamo) |
| Combining mark already in the source | Dropped after a foldable base, kept otherwise | `e`+U+0301 → `e` |
| Surrogate pairs (CJK Ext B, emoji) | Copied as a pair, with no bonus on the low half | — |

Verified in the prototype (`dotnet FuzzProto.dll --fold`):

- `Straße`↔`strasse`, `İstanbul`↔`istanbul`, `ﬁnal`↔`final`, `Ａｒｍｏｒ`↔`armor` and `Pokémon`↔`pokemon` all match.
- `ガイド`↛`カイド` (kana stay distinct).
- `한국어`↔`한국` matches.
- `中文chinese` matches *中文本地化 Chinese Localization* across the script boundary.

Case: always insensitive. No fzf "smart case", which users of a mod manager will not expect.

### 4.3 Where keys live, and when they are built

Keys live on `DivinityBaseModData`. They are **not** `[Reactive]` and not `[DataMember]`, so Fody does not weave them and the
`[DataContract]` serialiser ignores them (`DivinityBaseModData.cs:15`). They are rebuilt lazily when the source string
*reference* changes. They are warmed once, off-thread, at load.

```csharp
	// DivinityBaseModData.cs (fork) — next to Name/DisplayName/Author
	private SearchKey _nameKey = SearchKey.Empty;
	private SearchKey _displayNameKey = SearchKey.Empty;
	private SearchKey _authorKey = SearchKey.Empty;

	/// <summary>Folded match key for Name. Not reactive: rebuilt lazily when the Name reference changes.</summary>
	public SearchKey NameKey => GetSearchKey(ref _nameKey, Name);

	/// <summary>Folded match key for DisplayName (the file name when DisplayFileForName is on).</summary>
	public SearchKey DisplayNameKey => ReferenceEquals(DisplayName, Name) ? NameKey : GetSearchKey(ref _displayNameKey, DisplayName);

	public SearchKey AuthorKey => GetSearchKey(ref _authorKey, Author);

	private static SearchKey GetSearchKey(ref SearchKey cache, string source)
	{
		source ??= String.Empty;
		var key = cache;
		if (!ReferenceEquals(key.Source, source))
		{
			//Benign race if two threads build at once: both produce equal immutable keys.
			key = SearchKey.Build(source);
			cache = key;
		}
		return key;
	}

	public void WarmSearchKeys()
	{
		_ = NameKey;
		_ = DisplayNameKey;
		_ = AuthorKey;
	}
```

**Warm-up.** Call `WarmSearchKeys()` for each loaded mod in the background part of `RefreshAsync`, before the
`Observable.Start(..., RxApp.MainThreadScheduler)` that calls `SetLoadedMods` (`MainWindowViewModel.cs:2416-2477`, scheduler at `:2477`). That puts the ~2–5 ms on
the task pool, not the UI thread.

**When warm-up is skipped.** The lazy getter still works: the first keystroke pays the build once. A mod added later
(`:3167,3225`) builds its key on first use.

`DisplayName` is a non-deferred OAPH (`DivinityBaseModData.cs:166-168`, `ToProperty` without `deferSubscription`), so reading
it from the warm-up thread returns a value [certain for the default; inferred for the thread read, which is a plain field read].

---

## 5. Query semantics (fork, behind the setting)

### 5.1 Grammar

| Input | Today | Fork (fuzzy on) |
|---|---|---|
| `tav brawl` (no `@`) | One phrase "tav brawl", substring of Name | Tokens `tav`, `brawl`, AND'd and order-free, each fuzzy against `NameKey` and `DisplayNameKey` (best of the two). The score is the sum. |
| `"tavern brawl"` (no `@`) | Literal quotes in the phrase, so it matches nothing | Exact folded substring token, never fuzzy |
| `@author norbyte` | Substring of Author | Unchanged semantics, plus diacritic, width and kana folding (§5.3) |
| `@name imp ui` | Name phrase "imp" (the regex value stops at whitespace) plus the remainder "ui", dropped because a Name field exists | Exact `@name` (property equals "name", ignoring case) feeds its value to the fuzzy free-text term. The remainder is dropped exactly as today (S3). |
| `@filename foo` | Name **or** File substring; suppresses free text (S4) | **Unchanged.** Only the exact property `name` becomes fuzzy, so the `PropertyContains` OR-families keep working. |
| `@author "Laughing Leader" spells` | Author phrase plus Name phrase "spells" | Author field unchanged, plus a fuzzy token `spells` |
| `@auth` (being typed) | Name phrase "@auth", so the list blanks (S5) | A trailing `@word` with no whitespace yet is **ignored**: match-all for that pending term |
| `@selected`, `@editor`, `@modified 2025-01-01`, `@depend x`, `@tag x`, `@uuid x`, `@type x`, `@version x`, `@desc x` | As `DivinityModFilterData.Match` | Unchanged (null-safe from `perf/filter`) |
| empty or whitespace | All visible | All visible; ranking off; the header sort is restored |

Ranking applies only when the query has a free-text or `@name` term (`HasRanking`). A field-only query such as
`@author x` keeps the user's header sort.

### 5.2 Decision: does fuzzy apply to the active list?

**Yes, for inclusion. Never for order.** The same matcher runs on both lists. The active list gets no `CustomSort`, and the
VM never writes to `ActiveMods` during a filter pass: it only stamps `Visibility` and `IsSelected` (S7).

Load order is therefore unchanged *by construction*. T-A1 (§12) proves it on the saved order and on the `modsettings.lsx`
bytes.

R1 (§3.3) keeps the active list's hit count close to substring for real words ("spell" 34 → 34, "hair" 42 → 42 [measured]),
so an unranked list stays readable. A stricter "word-prefix only" mode (no in-word gaps) was prototyped as
`FuzzyStrictness.WordPrefix`. It is kept only as a knob, in case the owner finds the active list noisy (§13 Q2).

### 5.3 Folding for the non-keyed `@` fields (fork commit F3b)

`DivinityModFilterData.ValueContains` (`src/Core/Models/DivinityModFilterData.cs:12-26`) adds diacritic, width and kana
insensitivity. This is one flag change and needs no per-mod keys:

```csharp
	private const CompareOptions FoldedCompare = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace
		| CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

	// in ValueContains, both branches:
	return CultureInfo.CurrentCulture.CompareInfo.IndexOf(val ?? String.Empty, FilterValue, FoldedCompare) >= 0;
```

`@author` could use `AuthorKey` instead, but the gain is microseconds and the culture compare is already correct for these
short strings. Keep it one mechanism.

---

## 6. Presentation and ranking

### 6.1 Inactive list: ranked

- Ranking uses `ListCollectionView.CustomSort = ranking` (a non-generic `IComparer`, so there is no reflection, unlike
  `SortDescriptions`) inside `view.DeferRefresh()`. Filter and sort then cost **one Reset** per pass. [WPF] Setting
  `CustomSort` refreshes the view by itself, which is why `DeferRefresh` is needed to coalesce the two.
- **The inactive list is safe to rank** [certain]. It is rebuilt as a set (`MainWindowViewModel.cs:2143-2144`), and its order
  is not persisted.
- A ranked pass **always** refreshes, even with zero Visibility flips, because the order changed. That is one Reset over
  ~28 realized rows, the same unit cost as `perf/filter` [inferred].
- **Scroll:** after a ranked refresh, `ScrollIntoView(Items[0])` puts the best match on screen.
- **Header-sort interplay:**
  - **Ranking starts** (first non-empty ranked query): save `view.SortDescriptions` as `_savedInactiveSort`.
  - **The user clicks a header during the search:** `Sort()` (`HorizontalModLayout.xaml.cs:752-776`) first sets
    `CustomSort = null` and records `_inactiveUserSortedDuringSearch = true`. Later keystrokes in the same search then
    **don't** re-apply ranking, because the user's explicit choice wins.
  - **The query is cleared:** set `CustomSort = null`, restore `_savedInactiveSort`, and reset the flag.
  - [WPF, verify in T-R3] Adding a `SortDescription` may clear `CustomSort` by itself. The explicit null makes that moot.
- **Scores** live in a per-pass `ModSearchRanking` (`Dictionary<DivinityModData, ModMatch>`), owned by the VM and handed to
  the view. They do not live on the model, so stale scores cannot leak between lists. `DivinityModData` has no
  `Equals`/`GetHashCode` override, so the dictionary is keyed by reference [certain: grep finds none in `src/Core/Models`].

### 6.2 Active list: filter in load order, highlight later, optional "find in place"

- **Default (F3):** hide non-matches, as today. The "#" column still shows the true load-order `Index`, so context survives.
- **Highlight (F5, after 6.1's `CachedTextBlock` is in the fork):** matched characters get a tinted rounded rectangle, in
  both lists.
- **"Find in place" (F6, opt-in setting "Active list search: Filter | Find in place"):**
  - Non-matches stay visible at 0.45 opacity, and matches are highlighted.
  - F3 / Shift+F3 and Enter / Shift+Enter in the box move to the next or previous match (`ScrollIntoView` plus focus, with
    no selection change). The label reads "3 of 17".
  - **Why it is worth having:** in hide mode you cannot drop a mod *next to a non-matching neighbour* without clearing the
    search, because gong maps the drop to "before the next visible match" (`dl-gong/DropInfo.cs:68-74`). In place mode,
    every row is a real drop target and `UnfilteredInsertIndex == InsertIndex`.
  - **Cost:** no Refresh, no Visibility flips, and only realized rows re-render (§6.3).

### 6.3 How highlight and dim reach rows without O(N) notifications

**The mechanism.**

- One inherited attached property, `ModSearchHighlight.Result`, is set on the `ModListView` and registered with
  `FrameworkPropertyMetadataOptions.Inherits`. It flows into every realized container [WPF].
- Each keystroke sets **one** value, an immutable `ModSearchResult` (the query tokens and the match set).
- Only realized rows (~28) re-evaluate. Rows realized later read the current value when they bind.
- A Core model property such as `IsSearchMatch` would instead raise a notification on up to 935 mods per keystroke. That
  per-mod cost is what Stage 5 is removing.

**Dim (F6).** One `MultiBinding` on `ListViewItem.Opacity`: `{Binding (c:ModSearchHighlight.Result), RelativeSource=Self}` plus
`{Binding}`, through a converter that returns 1.0 or 0.45.

**Highlight (F5).** A separate `SearchHighlightOverlay : FrameworkElement`, placed in the name cell **behind** the
`CachedTextBlock` (wt-perf `MainResourceDictionary.xaml:719`).

- On render it asks the result for the mod's positions: `FuzzyMatcher.TryGetPositions`, mapped through
  `SearchKey.SourceIndex` and merged into runs.
- It builds `FormattedText(DisplayName,…).BuildHighlightGeometry(start, length)` for each run, cached per
  (text, runs, typeface, size).
- This keeps the fork-only code **out of** `CachedTextBlock`, which is upstream-bound in `perf/list-render`.
- **Cost:** positions for 28 rows take 0.14–0.19 ms [measured]. Geometry takes ~50–100 µs per row [inferred], so ~3 ms per
  keystroke in total.
- **On master** (before 6.1) the name is a plain `TextBlock` (`MainResourceDictionary.xaml:712-715`). Inline `Run`s would
  re-shape every row, so F5 waits for 6.1.

---

## 7. Instrumentation: run on Windows before changing anything

These steps reuse S5-A Step 0 and S5-B §3: wt-perf's `PerfProbe`, a `PerfFilterDriver`, and the UIA on/off matrix.
**Branch `probe/filter`, TEMP, never upstream.** The fuzzy angle adds four things:

1. **Split the VM pass into three timers**, so the matching share is known before and after:

   ```csharp
   		var t0 = PerfProbe.Begin();
   		var query = ModFilterQuery.Parse(searchText);       // parse
   		PerfProbe.End("Filter parse", t0);
   		var t1 = PerfProbe.Begin();
   		//...match loop only (no setters): compute bool[] isMatch...
   		PerfProbe.End("Filter match " + listName, t1);
   		var t2 = PerfProbe.Begin();
   		//...apply loop: Visibility / IsSelected setters, count flips...
   		PerfProbe.End("Filter stamp " + listName, t2);
   		PerfProbe.Note($"filter '{searchText}' list={listName} n={count} flips={flips} hidden={totalHidden}");
   		var t3 = Stopwatch.GetTimestamp();
   		Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
   			PerfProbe.Record("Filter -> idle " + listName, Stopwatch.GetTimestamp() - t3));
   ```

2. **Export the real corpus.** Add a TEMP hotkey, or a `perfprobe` flag `dumpcorpus`, that writes
   `UUID\tName\tDisplayName\tAuthor\tIsActive` for all mods to `%LOCALAPPDATA%\BG3ModManager\search-corpus.tsv`. It holds
   names only, so there is nothing sensitive. This file:
   - becomes the golden corpus for ranking tests (§12 T-U5);
   - replaces the synthetic names in `fuzzproto` (change `Program.cs` to read the TSV if one is passed).
3. **Run `fuzzproto` on the Windows box** with that corpus, `-c Debug` and `-c Release`. Windows ICU or NLS can differ from
   Linux ICU 74, especially on the CJK slow path. This confirms or refutes the ≤ 0.5 ms Debug claim on real names.
4. **A "shadow fuzzy" variant.** Behind a `shadowfuzzy` flag, run `FreeTextQuery.TryMatch` *in addition to* today's matcher.
   Record its time and the per-query (substring hits, fuzzy hits, fuzzy-only names). Do not let it affect visibility.
   This gives:
   - in-app timing under real JIT, GC and dispatcher conditions;
   - a list of what fuzzy would add, for the owner to sanity-check, before any behaviour change ships.

**Decision rules:**

- **"Filter match" ≤ 2 ms Debug with shadow fuzzy:** proceed synchronously. This is the expected outcome.
- **2–16 ms:** profile the keys first (cold build, `DisplayName` divergence), then the DP window. Still synchronous.
- **> 16 ms:** only then adopt the off-thread shape from §3.4.
- **"Filter → idle" still > 100 ms after `perf/filter`:** fuzzy is not the problem. Stay on S5-A/S5-B's levers.

---

## 8. Code changes

### 8.0 Seams `perf/filter` must provide (upstreamable, no fuzzy concepts)

The fork can then add fuzzy by touching **one** call in the VM and **one** method in the view. Everything else is in S5-A
and S5-B; this list is only the contract.

- `src/Core/Models/ModFilterQuery.cs`, with `static ModFilterQuery Parse(string)` and `bool IsMatch(DivinityModData)`. It has
  today's semantics and is null-safe (S5-B §5.1, S5-A B4).
- `OnFilterTextChanged` computes `isMatch` per mod, stamps only on a flip, counts flips, then calls
  `Layout?.RefreshFilter(list)` (S5-A B2).
- `IModViewLayout.RefreshFilter(IEnumerable<DivinityModData> list)` filters the default `ListCollectionView`
  (S5-A B1/B2, S5-B §5.3).
- The row style no longer binds `Visibility` (S5-A B4), and there are view/source index fixes (S5-A B3, S5-B c4/c5).
- The throttle drops to 150 ms with `DistinctUntilChanged`, after the re-measure (S5-A B5).

### 8.1 `feat/fuzzy-search` (fork-only, cut from `perf/filter`)

**F1: Core search primitives (pure, toolkit-neutral).** New files:

- `src/Core/Search/SearchKey.cs`
- `src/Core/Search/SearchText.cs`
- `src/Core/Search/FuzzyMatcher.cs`
- `src/Core/Search/ModMatch.cs` (`readonly record struct ModMatch(int Tier, int Score, int KeyLength)`)

Port them from `fuzzproto` as is: BOM, CRLF, tabs, namespace `DivinityModManager.Search`, and no `using System.Windows`.

Add `tests/Core.Search.Tests` (xunit, **`net8.0`**, not `-windows`). It links the four files through
`<Compile Include="..\..\src\Core\Search\*.cs" />`, so it runs on Linux and in any future CI (BLD.md notes there is none
today). It does not reference the Core project, which needs WPF and C++/CLI.

**F2: Keys and warm-up.** §4.3. This touches `DivinityBaseModData.cs` (+30 lines) and `RefreshAsync` (+1 line).

**F3: Query, matching and setting.**

- **Setting.** All three attributes are required (CLAUDE.md §4):

  ```csharp
  	[DefaultValue(true)]
  	[SettingsEntry("Fuzzy Search", "Match mod names approximately (word starts, abbreviations, accents) and rank inactive mods by relevance. Active mods are never reordered")]
  	[DataMember, Reactive] public bool EnableFuzzySearch { get; set; }
  ```

- **`ModFilterQuery` extension.** It returns a match with a score; free-text and `@name` become a `FreeTextTerm`:

  ```csharp
  public sealed class ModFilterQuery
  {
  	//Trailing "@prop" with no whitespace yet: the user is still typing the field name.
  	private static readonly Regex _pendingFieldPattern = new(@"@\S*$", RegexOptions.CultureInvariant);

  	private readonly List<DivinityModFilterData> _fields;
  	private readonly FreeTextTerm _freeText;
  	private readonly bool _fuzzy;

  	public bool HasRanking => _fuzzy && _freeText != null && !_freeText.IsEmpty;

  	public static ModFilterQuery Parse(string searchText, bool fuzzy)
  	{
  		if (!fuzzy) return Parse(searchText); //perf/filter's upstream behaviour, unchanged
  		if (String.IsNullOrWhiteSpace(searchText)) return Empty;

  		var fields = new List<DivinityModFilterData>();
  		var remaining = searchText;
  		var hasFields = searchText.IndexOf('@') > -1;
  		if (hasFields)
  		{
  			remaining = ExtractFields(searchText, fields); //the existing two regexes, verbatim (MWVM:4219-4251)
  			remaining = _pendingFieldPattern.Replace(remaining.Replace("\"", ""), "");
  		}

  		FreeTextTerm freeText = null;
  		var exactName = fields.FindIndex(f => String.Equals(f.FilterProperty, "name", StringComparison.OrdinalIgnoreCase));
  		if (exactName > -1)
  		{
  			freeText = new FreeTextTerm(fields[exactName].FilterValue, allowQuotedPhrases: false);
  			fields.RemoveAt(exactName);
  		}
  		else if (!fields.Any(f => f.PropertyContains("Name")) && !String.IsNullOrWhiteSpace(remaining))
  		{
  			//No '@': quotes mean an exact phrase. With '@': quotes were consumed by the field regex, as today.
  			freeText = new FreeTextTerm(remaining, allowQuotedPhrases: !hasFields);
  		}
  		return new ModFilterQuery(fields, freeText, fuzzy: true);
  	}

  	public bool TryMatch(DivinityModData mod, out ModMatch match)
  	{
  		match = default;
  		foreach (var f in _fields)
  		{
  			if (!f.Match(mod)) return false;
  		}
  		return _freeText == null || _freeText.TryMatch(mod.NameKey, mod.DisplayNameKey, out match);
  	}
  }
  ```

  `FreeTextTerm` is `fuzzproto/ModSearch.cs` `FreeTextQuery`, taking keys instead of a fake mod. Its tier rules are in §3.2,
  and the R1/R2 rules are inside `FuzzyMatcher.Score`.
- **F3b:** folded compare for the `@` fields (§5.3).

**F4: Ranking on the inactive list.**

- **Core, pure:**

  ```csharp
  public sealed class ModSearchRanking : IComparer
  {
  	private readonly Dictionary<DivinityModData, ModMatch> _matches = new();

  	public void Add(DivinityModData mod, ModMatch match)
  	{
  		_matches[mod] = match;
  	}

  	public int Compare(object x, object y)
  	{
  		if (x is not DivinityModData a || y is not DivinityModData b) return 0;
  		_matches.TryGetValue(a, out var ma);
  		_matches.TryGetValue(b, out var mb);
  		var c = ma.Tier.CompareTo(mb.Tier);
  		if (c != 0) return c;
  		c = mb.Score.CompareTo(ma.Score);
  		if (c != 0) return c;
  		c = ma.KeyLength.CompareTo(mb.KeyLength);
  		if (c != 0) return c;
  		c = String.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
  		return c != 0 ? c : String.CompareOrdinal(a.UUID, b.UUID);
  	}
  }
  ```

- **VM.** This replaces `perf/filter`'s loop; the parts that differ are marked:

  ```csharp
  	public void OnFilterTextChanged(string searchText, IEnumerable<DivinityModData> modDataList)
  	{
  		var fuzzy = Settings?.EnableFuzzySearch == true;                                   // fork
  		var query = ModFilterQuery.Parse(searchText, fuzzy);                                // fork
  		var ranking = query.HasRanking && modDataList == InactiveMods ? new ModSearchRanking() : null; // fork
  		var flips = 0;
  		var totalHidden = 0;
  		foreach (var mod in modDataList)
  		{
  			//if (mod.IsDivider) continue; // Stage 6.3, whichever branch lands second adds this
  			var isMatch = query.TryMatch(mod, out var match);                              // fork (was IsMatch)
  			if (isMatch)
  			{
  				ranking?.Add(mod, match);
  			}
  			else
  			{
  				totalHidden += 1;
  			}
  			if (SetFilterMatch(mod, isMatch)) flips++;
  		}
  		if (flips > 0 || ranking != null || modDataList == InactiveMods)                     // fork: ranked passes always refresh;
  		{                                                                                   // the unranked inactive pass refreshes
  			Layout?.RefreshFilter(modDataList, ranking);                                    // once more to drop a stale CustomSort
  		}
  		//...TotalActiveModsHidden / TotalInactiveModsHidden as today (MWVM:4305-4312)
  	}
  ```

  A refinement, once T-R2 passes: keep a `_inactiveWasRanked` flag so the unranked inactive refresh happens only on the
  ranked → unranked transition.
- **View** (`HorizontalModLayout.xaml.cs`). This adds one overload to `IModViewLayout`:

  ```csharp
  	private List<SortDescription> _savedInactiveSort;
  	private bool _inactiveUserSortedDuringSearch;

  	public void RefreshFilter(IEnumerable<DivinityModData> list, IComparer ranking)
  	{
  		if (CollectionViewSource.GetDefaultView(list) is not ListCollectionView view)
  		{
  			RefreshFilter(list);
  			return;
  		}
  		var isInactive = list == ViewModel.InactiveMods;
  		using (view.DeferRefresh())
  		{
  			if (isInactive && ranking != null && !_inactiveUserSortedDuringSearch)
  			{
  				_savedInactiveSort ??= view.SortDescriptions.ToList();
  				view.CustomSort = ranking;
  			}
  			else if (isInactive && ranking == null)
  			{
  				if (view.CustomSort != null) view.CustomSort = null;
  				if (_savedInactiveSort != null)
  				{
  					view.SortDescriptions.Clear();
  					foreach (var sd in _savedInactiveSort) view.SortDescriptions.Add(sd);
  					_savedInactiveSort = null;
  				}
  				_inactiveUserSortedDuringSearch = false;
  			}
  		}
  		//DeferRefresh's Dispose performs the single Refresh (filter + sort).
  		PruneHiddenSelection(isInactive ? InactiveModsListView : ActiveModsListView); //perf/filter's belt-and-braces
  		if (isInactive && ranking != null && InactiveModsListView.Items.Count > 0)
  		{
  			InactiveModsListView.ScrollIntoView(InactiveModsListView.Items[0]);
  		}
  	}
  ```

  In `Sort(...)` (`:752`), before `SortDescriptions.Clear()`, add:

  ```csharp
  			if (dataView is ListCollectionView lcv && lcv.CustomSort != null)
  			{
  				lcv.CustomSort = null;
  				if (lv == InactiveModsListView) _inactiveUserSortedDuringSearch = true;
  			}
  ```

**F5: Match highlighting** (requires 6.1 in the fork). §6.3:

- `ModSearchHighlight` (attached inherited property) and `ModSearchResult` (immutable: tokens plus a mod→positions cache);
- `SearchHighlightOverlay`, placed in `ModNameTemplate` behind `ModNameText`;
- the VM exposes `ActiveSearchResult` / `InactiveSearchResult`, `OneWayBind`'d to the attached property on each `ModListView`.

**F6: Active list "find in place"** (opt-in). It adds:

- a setting enum `ActiveSearchMode { Filter, FindInPlace }`;
- in `FindInPlace`, `OnFilterTextChanged` scores but stamps nothing; it publishes the `ModSearchResult` and a match index list;
- the `Opacity` `MultiBinding` (§6.3);
- F3 / Shift+F3 and Enter / Shift+Enter navigation in `HorizontalModLayout`.

It touches no collection and no `Visibility`, so drag/drop and selection need nothing new.

**F7: Typo fallback** (optional, owner decision).

- It runs only when a token of length ≥ 4 has **zero** hits in the pass.
- It retries that token with a bounded optimal-string-alignment distance (k = 1) against the word-prefixes of each key.
- Hits get tier 4, so they rank after everything else and show dimmed.
- **Cost** [inferred]: ~0.5 ms Debug, and only on zero-hit passes.

---

## 9. Expected per-keystroke cost (work after the throttle; Debug unless noted)

| After | Matching (VM) | WPF/view | Total per pass | Felt settle |
|---|---|---|---|---|
| Today (0.3 table) | 0.06–1.1 ms [measured, prototype of today's loop] | 1.2–3.3 s | 1.24–3.27 s [measured, owner] | 1.7–3.8 s |
| `perf/filter` (S5-A/B) | same | ~20–80 ms [inferred, S5-A/B] | ~20–80 ms | ~0.55 s at 500 ms, **~0.2 s at 150 ms** |
| + F1–F3 fuzzy include | **0.08–0.41 ms** for the 935 active / 0.01–0.08 ms for the 187 inactive [measured] | unchanged | ~20–80 ms | ~0.2 s |
| + F4 ranking (inactive) | + rank sort 0.09–0.11 ms for 170 hits [measured] | a Reset on every ranked keystroke, even without flips (~28 rows) [inferred, ~15–40 ms before 6.1, less after] | ~20–60 ms | ~0.2 s |
| + F5 highlight | + positions 0.14–0.19 ms for 28 rows [measured] | + ~1.5–3 ms geometry [inferred] | +~3 ms | ~0.2 s |
| F6 find-in-place (active) | ≤ 0.41 ms | no Refresh; ~28 rows re-render ~3–10 ms [inferred] | ~5–12 ms | ~0.16 s |
| Key warm-up (once at load) | 1.5–5.2 ms on the task pool [measured] | — | — | — |

Release is ~3–5× faster on the matching column (§A).

The 16 ms UI budget is dominated by WPF's Reset, not by fuzzy. Fuzzy uses at most ~3% of it.

---

## 10. Interactions

**Drag and drop** [certain wiring]. Ranking only sets `CustomSort` on the inactive view.

- **Drop into the ranked inactive list.** The drop handler inserts at `UnfilteredInsertIndex`, clamped
  (`ModListDropHandler.cs:133,172-175`). It then calls `OnFilterTextChanged` (`:252-258`), which re-ranks, and
  `RefreshDataView` (`:236-247`).
  - The dropped mod lands at its *rank*, not at the drop point. That is acceptable, because the inactive order is meaningless.
  - It may vanish if it does not match, which is today's behaviour for filtered lists.
- **Drag out of the ranked list.** Removal is by item (`sourceList.IndexOf(o)`, `:153-166`), so the view order is irrelevant.
- **Active list.** Never sorted by search, so drop indices mean exactly what they mean under `perf/filter`. §2.3 explains why
  this matters.
- **F6.** No filtering, so `InsertIndex == UnfilteredInsertIndex` and precise drops work during a search.

**Selection and the wt-perf fixes** (309b81e, 396d7b2) [certain wiring; WPF Reset semantics to verify in T-S1/T-S2].

- **309b81e:** `UpdateIsSelected` reconciles every mod against `ListView.SelectedItems` (wt-perf
  `HorizontalModLayout.xaml.cs`). After a Reset:
  - items no longer in the view drop out of `SelectedItems` [WPF], so the reconcile deselects them;
  - items that only moved under ranking stay selected [WPF].
- **396d7b2:** `SelectMods` uses `ModListView.SetSelection` (`SetSelectedItems`), with no containers involved, and already
  excludes non-Visible mods. That works unchanged with a sorted view.
- **Neither commit reads indices.** That makes them compatible with `CustomSort`.
- **Ranking itself causes no `IsSelected` writes.** Only flips deselect (S7).

**Stage 6.3 dividers** (S6-A's `CategoryDivider : DivinityModData`, F-9 `UpdateCategoryRows`, `IsCollapsedByCategory`).

- **Scorer.** It skips `IsDivider` rows: one line in the loop (§8.1 F4).
- **Inactive list.** It has no dividers (S6-A puts them in `ActiveMods`), so ranking and dividers never meet.
- **Active list** (unranked).
  - S6-A's rule applies unchanged: while filtering, a divider is visible iff it has a visible member, and matches inside
    collapsed categories are shown.
  - The collection-view predicate becomes `Visibility == Visible && !IsCollapsedByCategory`.
- **F6 dims dividers too**, except those with matches.
- **A natural extension that fits the `@` grammar:** `@category Cosmetics` matches the members of a category (§13 Q5).

**The `@` field syntax.** See §5.1. It is unchanged except `@name`, which becomes fuzzy, and a trailing pending `@word`,
which is ignored. The fuzzy setting off gives `perf/filter`'s exact upstream behaviour (`Parse(searchText)`).

**Accessibility.** Counts keep flowing through `ActiveModsFilterResultText` / `InactiveModsFilterResultText`
(`MainWindowViewModel.cs:5269-5271`). Highlight is visual only. F6's "3 of 17" goes through the existing
`ScreenReaderHelper` announce path (verify name in T-X1). With fewer realized containers, UIA walks get cheaper.

**Header sort.** See §6.1. Active-list header sort is today's behaviour and untouched. It was already a view-only reorder,
and the save path uses `Index`, not view order [inferred; S6-A notes 6.3 disables header sort with categories].

**Linux and Avalonia.** F1, F3's query and F4's comparer are pure Core. Only `RefreshFilter` (`ICollectionView`) and the F5/F6
overlay and bindings are WPF. upstream/avalonia has the same stamping filter (`wt-avalonia src/Core/Util/ModUtils.cs:182-277`)
and would take F1–F3 unchanged.

---

## 11. Branches and PRs

| Branch | From | Contents | Upstream? |
|---|---|---|---|
| `probe/filter` (TEMP) | `perf/list-render` (for `PerfProbe`) | S5-A Step 0 + §7 here: split timers, corpus dump, shadow fuzzy | never |
| `perf/filter` | `master` | S5-A B1–B5 / S5-B c1–c7, providing the seams in §8.0. **No fuzzy code, setting or `Search/` folder.** | **yes** |
| `feat/fuzzy-search` | `perf/filter` | F1 primitives + tests · F2 keys + warm-up · F3 query, setting, F3b fold · F4 inactive ranking · F5 highlight (after 6.1) · F6 find-in-place (opt-in) · F7 typo fallback (optional) | **fork-only** |
| `feat/categories` (6.3) | per S6 designs | adds `IsDivider` skip in the scorer loop if it lands after fuzzy | fork-only |

**Separation.**

- `feat/fuzzy-search` touches upstream files in exactly these places:
  - `DivinityBaseModData.cs` (+keys);
  - `ModFilterQuery.cs` (+`Parse(text, fuzzy)`, `TryMatch`);
  - `DivinityModFilterData.cs` (F3b);
  - `DivinityModManagerSettings.cs` (+1 setting);
  - `OnFilterTextChanged` (~6 lines);
  - `IModViewLayout` and `HorizontalModLayout.xaml.cs` (+1 overload, +4 lines in `Sort`);
  - and, for F5/F6, `MainResourceDictionary.xaml` (row template).
- Everything else is new files.
- Rebasing it onto a new `perf/filter` should conflict only in `OnFilterTextChanged`.

**Order of work.**

1. `probe/filter`, and measure.
2. `perf/filter`, then re-measure.
3. F1–F3 behind the setting (default on in fork builds). Ship them to friends with shadow-fuzzy data already reviewed.
4. F4.
5. F5 once 6.1 is in `fork/main`.
6. F6 and F7 only on request.

**Effort** [inferred]:

| Commits | Effort |
|---|---|
| F1–F2 | 1 day, including tests |
| F3–F4 | 1–1.5 days, including Windows testing |
| F5 | 1 day |
| F6 | 1 day |
| F7 | 0.5 day |

---

## 12. Test plan

### Unit tests (`tests/Core.Search.Tests`, xunit, `net8.0`; Linux and Windows)

| ID | Test |
|---|---|
| T-U1 | **Fold table:** every row of §4.2, including `ガイド`↛`カイド` and `한`≠jamo; surrogate pairs survive; `SourceIndex` is monotone non-decreasing and maps `ß` to both `s`. |
| T-U2 | **Bonus classes:** camel (`ImprovedUI` gives 7 at `U`), digit (`Level20`), delimiter (`a/b` 9), white (10), non-word (`_x` 8), Latin↔CJK (7). |
| T-U3 | **Scorer invariants.** (a) **Superset property:** for random substrings of corpus names, every `CompareInfo.IndexOf(IgnoreCase)` hit is a fuzzy hit (the prototype ran 398,340 pairs, 0 misses). (b) **Monotone:** if `p` has no match, then `p + c` has none. (c) `TryGetPositions` returns ascending positions whose chars equal the pattern, and its score equals `Score`. (d) R1: "imp" ↛ "Unlimited Icons - Patch 7"; "lvl" → "Unlock Level Curve". |
| T-U4 | **Parser:** each row of §5.1. In particular: `@author foo` is unchanged; `@filename foo` still ORs Name/File; `@auth` is match-all; `"tavern brawl"` is exact; `@name imp ui` drops "ui" as today; fuzzy off gives the same `IsMatch` as `perf/filter` on the corpus × 200 queries. |
| T-U5 | **Ranking goldens on the owner's exported corpus (§7.2):** top-1 for ~20 queries the owner names (seed: "mcm"→Mod Configuration Menu, "iui"→Improved UI, "5e"→5e Spells, "brawl tav"→Tavern Brawl), and today's substring hits all rank above fuzzy-only hits (tier rule). |
| T-U6 | **Benchmark (not a pass/fail test):** the `fuzzproto` loop on the corpus, recorded in the PR description for Debug and Release. |

### Windows, in-app (the `probe/filter` driver and manual checks)

| ID | Test |
|---|---|
| T-P1 | Probe timers (§7.1): "Filter match" ≤ 2 ms Debug for the active list with fuzzy on; "Filter → idle" within `perf/filter`'s numbers + 5 ms. |
| T-A1 | **Load order is untouched.** Load a 935-mod order, then run 20 searches on the active list, including ranked-looking queries and F6 mode. `ActiveMods` UUID sequence, `Index` values, the saved order JSON and the exported `modsettings.lsx` are **byte-identical** to before. |
| T-R1 | Inactive "tav": the first rows are tier 0 (Tav's Tavern, Tavern Brawl…), and the list is scrolled to the top. |
| T-R2 | Typing "t", "ta", "tav", then backspacing to empty: the header sort the user had before (e.g. by Author) is restored. |
| T-R3 | During a ranked search, click the Name header: the list sorts by name; further keystrokes keep the name sort; clearing the search restores the pre-search sort. |
| T-D1 | Drag 5 mods from the ranked inactive list into the active list at a position: they land exactly there in the source order, and the saved order matches. |
| T-D2 | Drag an active mod onto the ranked inactive list: it appears at its rank, or is hidden if it does not match; nothing throws. |
| T-D3 | F6 on: search "hair", drag a non-matching mod between two specific rows: it lands precisely between them. |
| T-S1 | Select 40 inactive mods (more than a screen), then type a query that hides 30: the 10 visible stay selected; the hidden 30 have `IsSelected=false`; the "Selected" counts are right. This exercises 309b81e under Reset. |
| T-S2 | Ranked list, select 3, type one more char that keeps all 3: still selected, still highlighted after scrolling (wt-perf regression). |
| T-S3 | Ctrl+A under a filter selects only the matches; Move Selected moves only those. |
| T-K1 | Alt+Arrow and keyboard moves in a ranked or filtered list operate on the intended item (S5-A B3 fixes, exercised with `CustomSort`). |
| T-F1 | `@` regressions: `@author Norbyte`, `@author "Laughing Leader" spells`, `@desc x` on mods without a description (no exception, from `perf/filter`), `@selected`, `@modified 2025-01-01`. |
| T-F2 | Typing `@`, `@a`, `@au`, `@author`, `@author `, `@author n`: the list never blanks before a value is typed. |
| T-I1 | Diacritics, CJK, fullwidth: "pokemon", "faerun", "日本", "ｃａｍｐ" in a real install with such names. |
| T-H1 | **F5:** highlights line up with glyphs at 100/150/200% DPI, for bold/italic font settings, and for `DisplayFileForName` names; scrolling 900 rows with a query active stays smooth (Stage 6 scroll probe). |
| T-X1 | Narrator / NVDA: the search box announces "N Matched"; F6's "3 of 17" is announced; no change in the realized-row walk cost beyond `perf/filter`'s. |
| T-C1 | **Stage 6.3**, when present: S6-A's M4 ("hair" with Cosmetics collapsed) passes with fuzzy on; empty dividers are hidden; `@category` if adopted. |
| T-O1 | Setting off: behaviour identical to `perf/filter` (spot-check T-F1 and S1/S2 cases). |

---

## 13. Open decisions for the owner

| ID | Question | Recommendation |
|---|---|---|
| Q1 | Default of `EnableFuzzySearch` in fork builds | On |
| Q2 | Active list: anchored fuzzy (same as inactive) or the stricter word-prefix-only mode? | Same matcher; revisit after shadow-fuzzy data |
| Q3 | Should free text also match `DisplayName` when it differs from `Name` (file names)? | Yes; it is what the row shows |
| Q4 | F6 find-in-place: wanted? Default stays "Filter" | — |
| Q5 | Add `@category name` to the grammar with 6.3? | — |
| Q6 | F7 typo fallback: wanted? | Default off |
| Q7 | Pressing Enter in a search box: keep "move focus next" (`UnfocusableTextBox.cs:47-61`) or "focus and select the top match"? | The latter, fork-only |

---

## A. Prototype: what was built and measured

**Files.** Source is in `docs/review/prototypes/fuzzy-search/`:

| File | Contents |
|---|---|
| `SearchKey.cs` | key, fold, classes |
| `FuzzyMatcher.cs` | DP scorer, R1/R2, greedy fallback, positions |
| `ModSearch.cs` | lazy keys on a stand-in mod, free-text tokens, tiers, comparer |
| `Program.cs` | corpus, benchmarks, superset check, ranking printout, `--fold` checks |

The prototype is LF without BOM; port it with BOM and CRLF.

**Running it.** SDK 8.0.427 is in `scratchpad/dotnet8`. Set `DOTNET_ROOT` to it, then run
`dotnet build -c Release && dotnet bin/Release/net8.0/FuzzProto.dll`. Add `--fold` for the fold and position checks.

**Corpus.** 1,121 names: ~150 realistic BG3 mod names (dividers, `[WIP]`, CJK, Cyrillic, Greek, diacritics, fullwidth,
halfwidth) plus generated "Adjective Noun Suffix" names and underscore file-style names. Average 20.1 characters, maximum 40.
The list is split 187 inactive and 934 active.

Benchmark: µs per pass, mean of 300 passes after warm-up. "Today" is `CompareInfo.IndexOf(Name, q, IgnoreCase)` over all 1,121.

| Query | Hits inact/act | Release inact | Release act | Release today | Debug inact | Debug act | Debug today |
|---|---|---|---|---|---|---|---|
| a | 147 / 770 | 21 | 58 | 151 | 41 | 239 | 236 |
| e | 170 / 860 | 11 | 70 | 58 | 79 | 412 | 77 |
| ui | 35 / 156 | 8 | 50 | 123 | 34 | 198 | 104 |
| spell | 2 / 32 | 6 | 47 | 111 | 20 | 130 | 103 |
| imp ui | 1 / 1 | 10 | 66 | 121 | 37 | 210 | 109 |
| zzzzzz | 0 / 0 | 4 | 19 | 100 | 14 | 88 | 158 |
| tavern brawl | 0 / 2 | 4 | 30 | 213 | 26 | 145 | 213 |
| 日本 | 0 / 1 | 4 | 21 | **1,420** | 13 | 67 | **942** |
| ｆｕｌｌ | 0 / 2 | 22 | 36 | **1,562** | 16 | 83 | **1,132** |
| compatibility framework patch | 0 / 0 | 12 | 105 | 76 | 29 | 259 | 60 |

Other figures:

| Measurement | Result |
|---|---|
| Key build | 2.2 ms warm / 4.6 ms cold (Release); 1.5 / 3.9 ms (Debug) |
| Rank sort, 170 hits | 0.09–0.11 ms |
| Highlight positions, 28 rows | 0.14–0.19 ms |
| Superset check | 398,340 substring hits, 0 missed |

**Caveats.**

- A synthetic corpus on Linux ICU, not the owner's names on Windows. §7.2–7.3 close that gap.
- Micro-benchmark conditions: warm caches, no GC pressure from the WPF app.
- Cost scales about linearly with name length.
