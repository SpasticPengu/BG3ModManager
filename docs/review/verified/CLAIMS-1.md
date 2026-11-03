# CLAIMS-1: Verified results

This is a read-only review of the SpasticPengu fork of BG3 Mod Manager. Each item below is a claim that an earlier audit or design made about the code. "Confirmed" means the claim holds. "Refuted" means it is wrong.

## Summary

| Status | Count |
|---|---|
| Confirmed | 4 |
| Plausible | 2 |
| Disputed | 0 |
| Refuted | 0 |
| Unverified | 0 |
| **Total** | **6** |

| Final severity | Count | Items |
|---|---|---|
| High | 2 | CLAIM-01 (Confirmed), CLAIM-05 (Plausible) |
| Medium | 2 | CLAIM-03 (Confirmed), CLAIM-04 (Confirmed) |
| Low | 2 | CLAIM-02 (Confirmed), CLAIM-06 (Plausible) |

One severity changed: CLAIM-03 went from High to Medium.

---

### CLAIM-01 [High] Plan I1 is misdiagnosed: SharpCompress 0.39 zip entry streams cannot short-read, and SafeDeserialize returns a partial order rather than null (Confirmed)

**Location:** src/GUI/ViewModels/MainWindowViewModel.cs ~L3440-3470 (ImportArchiveAsync order JSON read); src/Core/Util/DivinityJsonUtils.cs ~L20-40; SharpCompress 0.39 zip entry stream classes.

Both verifiers agree that one ReadAsync of the file's size fills the buffer:
- **Stored zip entries:** reads go through SourceStream.Read, which loops until the request is filled (sc/.../IO/SourceStream.cs:136-175).
- **Deflate zip entries:** ZlibBaseStream.Read loops until the output is full or the entry ends (ZlibBaseStream.cs:556-596).
- **Other formats:** 7z LZMA also fills the buffer (LzmaStream.cs:160-206). RAR very likely does too (RarStream.cs:72-136).

Neither SafeDeserialize nor Newtonsoft's handled-error path returns null in the usual case. They log the error and return a partly filled order (DivinityJsonUtils.cs:11-37, Handled=true). Null comes back only when the error falls outside a property-value try, such as a truncated root property name.

One verifier found a separate gap. Once an entry is opened, an entry with a data descriptor reports Size=0, so the JSON is skipped silently (SeekableZipFilePart.cs:36-41, ZipEntry.cs:60, MainWindowViewModel.cs:3459-3468).

**Fix caveats:**
- ReadExactlyAsync or a MemoryStream copy is still cheap hardening.
- For the real losses: copy until the stream ends rather than sizing the buffer from file.Size, and log a null or partial deserialize instead of dropping it silently.
- Also check JSON schema mismatches and I2.

**docRef:** plan Stage 3 I1; AUDIT-PLAN

### CLAIM-05 [High] The measured 1.2-3.3 s filter cost is not explained by OnFilterTextChanged's loop (Plausible)

**Location:** src/GUI/ViewModels/MainWindowViewModel.cs ~L4199-4316 and the throttle wiring ~L5240-5260; src/GUI/Views/HorizontalModLayout.xaml; src/GUI/Themes/MainResourceDictionary.xaml; the plan's Stage 0.3 measurement method.

Both verifiers agree, with medium confidence and by inference only. For each mod the loop runs one culture-aware IndexOf and one [Reactive] Visibility setter, and that setter goes through RaiseAndSetIfChanged (MainWindowViewModel.cs:4199-4313, DivinityBaseModData.cs:69). With recycled, virtualized containers, that costs milliseconds, not about 1.2 s.

The verifiers name these likely causes instead:
- **UIA harness:** the CachedAutomationPeer.cs:25-82 visual-tree walk.
- **Idle sampling or Debug JIT:** a measurement artifact from the harness.
- **Container realization:** collapsing rows makes the panel realize more containers, each running ScreenReaderHelperBehavior reflection (MainResourceDictionary.xaml:256,369). This cost is driven by the loop.

The claim that the cost is "flat" is only weakly supported, because no active-list no-match case was measured.

**Fix caveats:**
- Before changing the loop, profile it. Add an active-list no-match run, a control run with filtering disabled, and one run without UIA attached.
- Plan step 1 is already handled by RaiseAndSetIfChanged.

**docRef:** plan Stage 0.3 / Stage 5

### CLAIM-03 [Medium, was High] Stage 5 step 1 saves nothing: Visibility and IsSelected setters already skip equal values (Confirmed)

**Location:** src/Core/Models/DivinityBaseModData.cs ~L69 (Visibility); src/Core/Models/DivinityModData.cs ~L194 (IsSelected); ReactiveUI.Fody 19.5.41 weaving.

Both verifiers confirm the claim with high confidence:
- **Visibility:** a plain [Reactive] auto-property (DivinityBaseModData.cs:69), woven through src/Core/FodyWeavers.xml. The setter becomes RaiseAndSetIfChanged, which returns early on equal values (ReactiveUIPropertyWeaver.cs:141-156; IReactiveObjectExtensions.cs:116-126).
- **IsSelected:** already calls RaiseAndSetIfChanged (DivinityModData.cs:185-196).
- **The loop:** each branch of OnFilterTextChanged sets each mod's Visibility exactly once, with no reset followed by re-hide (MainWindowViewModel.cs:4199-4312). wt-perf has the same code.

**Fix caveats:** a caller-side guard prevents no PropertyChanged events or UI work. Keep it only to count changed rows, or drop it. The real cost per pass is the matching and the re-layout of rows that actually change.

**docRef:** plan Stage 5 step 1

### CLAIM-04 [Medium] Stage 5 step 2 saves nothing: the plain search path allocates no per-mod strings (Confirmed)

**Location:** src/GUI/ViewModels/MainWindowViewModel.cs OnFilterTextChanged ~L4199-4316; DivinityModData / DivinityModFilterData matching helpers.

The claim holds. The plain (non-@) branch only calls CurrentCulture.CompareInfo.IndexOf(m.Name, searchText, IgnoreCase) on an auto-property, so it builds no ToLower copies or substrings (MainWindowViewModel.cs:4287-4301; DivinityBaseModData.cs:24).

**Correction:** the comparison is culture-aware, not ordinal.

The @ path makes no per-mod strings either. Split only runs when separateWhitespace is true, and no caller passes that (DivinityModFilterData.cs:12-24). The @ path does have small per-mod costs: one closure for each @Tag Any call, and DateTime.TryParse for @Modified (:93-108).

**Fix caveats:**
- Hoisting lowercase keys removes no allocations.
- The only possible gain is CPU, by switching to OrdinalIgnoreCase or a precomputed key. That needs measuring and matters mostly once fuzzy matching arrives.
- Retitle or drop step 2. On the @ path, cache the parsed date and avoid the closures.

**docRef:** plan Stage 5 step 2

### CLAIM-02 [Low] Plan E6 is moot: the 1d / modPaks.Count division never takes effect when modPaks is empty (Confirmed)

**Location:** src/GUI/ViewModels/MainWindowViewModel.cs ~L3565-3600 (ExportLoadOrderToArchiveAsync).

Both verifiers confirm the claim with high confidence:
- **No exception:** `1d / modPaks.Count` is a double division, so an empty list gives +Infinity rather than throwing (MainWindowViewModel.cs:3575).
- **Value never applied:** the only read is `MainProgressValue += incrementProgress` at :3628, inside `foreach (var mod in modPaks)` at :3593, and that loop never runs on an empty list. The export still writes the order JSON and reports success. wt-perf has the same code.
- **Plan is off:** it rates E6 "HIGH (cosmetic)" and places it at L3574, but the code is at L3575 (implementation-plan.txt:536-540).

**Fix caveats:**
- Downgrade E6 to trivial.
- An optional guard `modPaks.Count > 0 ? 1d / modPaks.Count : 0` protects future refactors that read the value outside the loop, such as the Stage 8 multi-order work.

**docRef:** plan Stage 2 E6

### CLAIM-06 [Low] MeasureText's 58 startup exceptions don't come from '=' divider names (Plausible)

**Location:** src/GUI/Util/ElementHelper.cs ~L100-133; callers src/GUI/Views/HorizontalModLayout.xaml.cs ~L846 and ~L910.

Both verifiers agree, with medium confidence.
- **What throws:** only the CharacterToGlyphMap[text[n]] indexer, which raises KeyNotFoundException for an unmapped UTF-16 unit. The catch sits inside the per-character loop, so each such character adds zero width (ElementHelper.cs:108-128, indexer :112, catch :124).
- **Why '=' cannot throw:** the lists use the default Segoe UI (Themes has no list FontFamily setter), and that font maps ASCII '='.
- **What is measured:** only longestName is passed (HorizontalModLayout.xaml.cs:846, :910).

**Corrected statement:** the 58 misses are unmapped characters in the longest active and inactive names, such as non-Latin text, emoji surrogate halves, or box-drawing and fullwidth lookalikes of '='.

**Fix caveats:** the TryGetValue fix is right. Without a Windows run the exact characters are unverified. Log the measured strings and the code points that miss to confirm.

**docRef:** plan Stage 0.3 / 6.1
