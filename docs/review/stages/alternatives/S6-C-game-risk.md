# S6-C: Stage 6.2 (overrides folded into the order) and 6.3 (categories/dividers), game behaviour and risk first

Angle C. Written 2026-11-03 against `master` = upstream `fc35358`, `wt-perf` (`origin/perf/list-render`,
head `9f2b163`) and `wt-avalonia` (`upstream/avalonia`). Nothing was built or run, and the repo was not
touched.

Reports in `scratchpad/reports/` when I started and again when I finished: `AUDIT-PLAN.md`,
`AUDIT-CLAUDE.md`, `AVA.md`, plus `BLD.md` by the end (build/CI only; its line-ending note means new files should go
through the owner's `core.autocrlf=true` like his perf commits). VM2, S6, LOAD and UI had not landed, so re-check §2 against LOAD
(`BuildOutputList`/`GenerateModSettingsFile`) and VM2 (drop handler) when they do. The sibling designs
`docs/review/stages/alternatives/S6-A-data-model.md` and `docs/review/stages/alternatives/S6-B-list-ux.md` exist. This design agrees with both on the
central decision and says where it adds or differs (§0, §13).

Labels:
- **[E]** evidence, with a `path:line` cite (master unless prefixed `wt-perf:` / `wt-ava:`).
- **[I]** an inference from code.
- **[GAME]** a claim about how BG3 itself behaves. It can't be checked here. Each one names the in-game
  test in §10 that settles it.

---

## 0. Headline

**Treat `modsettings.lsx` as a pure function of (campaign, exportable mods in order, auto-add-deps
setting) and make that structural.** Dividers, collapse state and folded-in overrides go in a parallel
row collection. They never enter `ActiveMods` or `DivinityLoadOrder.Order`, so they can't reach
`BuildOutputList`. That's the same decision S6-A and S6-B reached. Then add two layers that neither
sibling has:

1. **An upstreamable write guard, shipped first (`fix/modsettings-write-guard`, cut from `master`).**
   Never write a `ModuleShortDesc` whose UUID isn't a GUID, or that belongs to a meta-less override pak.
   Read the file back with LSLib after every write. Today the only filter is `IsNullOrWhiteSpace(UUID)`
   (DivinityModDataLoader.cs:1478). A meta-less override's UUID is its **pak path**
   (DivinityModDataLoader.cs:620), and its Folder is a base-game folder such as `Gustav`/`GustavX`
   (:617). If it is ever written, the result is a file that **the manager's own reader can't parse**:
   LSLib's `new Guid(str)` throws (External/lslib/LSLib/LS/NodeAttribute.cs:440-447, via
   LSXReader.cs:169). The manager then sees zero active mods and fires its "reset externally" alarm.
   The game very likely resets the order **[GAME, T-G5]**. Master has a reachable path into that state
   (§2.4), so this is a real bug, not hypothetical hardening.
2. **A verification protocol with a golden-file invariant.** For any order with no categories, the
   `modsettings.lsx` bytes must be identical before and after 6.2/6.3. Adding dividers, collapsing them
   and pinning overrides must not change one byte either. Only the in-game tests confirm what the game
   accepts. The byte invariant means the game only has to be re-tested where bytes actually changed.

For 6.2 that means **folding overrides into the list is presentational only**. A meta-less override
pak has no module, so the game never orders it through `modsettings.lsx` **[GAME, T-G3]**. The list
must not pretend otherwise: override rows get no `Index`. The plan's mock-up numbers them 4–5; this
design asks the owner to drop that (Q1).

---

## 1. The owner's intentions I have gleaned

You asked for this in the relayed request. Sources: CLAUDE.md, implementation-plan, plan-overview and
the computed task.

1. **Upstream first, fork second.** `master` mirrors upstream. Upstreamable work (`fix/*`, `perf/*`) is
   cut from a clean `master` and goes first. 6.1 render perf is upstreamable. 6.2 and 6.3 are fork-only
   (`feat/overrides-in-order`, then `feat/categories`, on `fork/main`). Merges use `--no-ff`, one
   branch per issue (plan §3).
2. **The game is the authority.** "Verify by exporting and booting the game, not just by looking at
   the list" (plan 6.2, §15). A load order that looks right but resets in-game is a failure.
3. **The game must never see a divider (R7).** Older builds and upstream mustn't be confused by one
   either.
4. **Categories are flat rows, never WPF grouping.** The `IsGrouping` trigger turns off virtualization
   (MainResourceDictionary.xaml:460-461). 935 active rows must stay virtualized and smooth, on top of
   the 6.1 gains (`CachedTextBlock`, selection reconciled through `SelectedItems`).
5. **Overrides become a normal category** in the active list (plan mock-up: "▶ Overrides 2 collapsed").
   That retires the separate Overrides pane.
6. **Collapse is view-only.** Order indices must not shift, and `Index` stays contiguous across drags.
7. **Keep Stage 5's options open.** It may keep stamping `Visibility` or move to collection filtering.
8. **Orders are shared with friends and published.** Zip export/import (Stages 2–3), multi-order
   export (Stage 8) and later syncing (Stage 9) all carry the order JSON, so its format is a public
   contract.
9. **Evidence over argument.** Measure, cite, label inferences. The plan keeps a record of disproved
   hypotheses so they don't get re-argued.
10. **Ideas only, no code, from Redux/Avalonia.** Logic goes in Core/view models, not code-behind, to
    keep a future port cheap.
11. **Visible failures.** `ShowAlert` for anything the user must know. Logging is off by default (R8).
12. **House style.** No `string?` in Core/GUI. Tabs, CRLF, BOM, Allman, `var`. New types unprefixed.
    Mutate settings in place.
13. **The owner already uses placeholder "divider" mods.** The real 935-mod order contains `=====`
    divider entries (implementation-plan.txt:406-407). Those are real paks the game loads today, so
    migrating them is a decision with game-side consequences (§9.3).

---

## 2. What the manager writes today, and how failures show up (evidence)

### 2.1 The writer

| Step | Code | Notes |
|---|---|---|
| Campaign first | `BuildOutputList` adds `selectedAdventure` before anything else (DivinityModDataLoader.cs:1431-1445). The export falls back GustavX → GustavDev → the inherent GustavDev (MainWindowViewModel.cs:2904-2928) | [E] So whatever 6.2/6.3 put at the top of the list, the campaign is still the first `ModuleShortDesc`. README.md:23 says the game needs it. |
| Order resolution | `foreach (var m in order.Where(x => !x.Missing))` → `allMods.FirstOrDefault(x => x.UUID == m.UUID)`. Unresolved entries are logged and dropped (:1447-1465). Auto-add deps inserts dependencies *before* their dependent (:1452-1455) | [E] Implicitly strips anything whose UUID matches no loaded mod (AUDIT-PLAN claim 32). It does **not** strip a pure override, because a pure override *is* a loaded mod, keyed by its path. |
| Serialisation | `String.Format(XML_MODULE_SHORT_DESC, Folder, MD5, Escape(Name), UUID, Version64, PublishHandle)`, skipping only `IsNullOrWhiteSpace(UUID)` (:1471-1485; template DivinityApp.cs:33) | [E] `UUID` is declared `type="guid"`. Only `Name` is XML-escaped. |
| Validation | `XmlDocument.LoadXml` before writing. Exceptions are caught and the method returns `false` (:1331-1364) | [E] Malformed XML never reaches disk, and the caller alerts (MainWindowViewModel.cs:2986-2995). But **well-formed XML with a non-GUID UUID passes**. |
| Template | `<save><version major="4" minor="8" .../><region id="ModuleSettings">…<node id="Mods">` with no `ModOrder` node (DivinityApp.cs:34) | [E] Patch 7+ shape. **[GAME, T-G1]** the order of `Mods` children is the load order. |

### 2.2 The two reset detectors, and what trips them

- **Watcher.** After an export, any change to `modsettings.lsx` is re-read 2 s later. If
  `ActiveMods.Count > 0` and the file has no non-ignored mods, it shows "reset externally"
  (MainWindowViewModel.cs:5420-5445). [E]
- **Startup.** `CheckForEmptyOrderAsync` offers to restore `LastExported` when the file has zero active
  mods (MainWindowViewModel.cs:2283-2333). [E] The restore path calls `BuildOutputList` +
  `ExportModSettingsToFileAsync` on `lastExported.Order` (:2326-2327). That is a **second export path**,
  and every 6.2/6.3 rule must hold for it too.
- **What "zero active mods" really means.** `LoadModSettingsFileAsync` catches *any* LSLib exception
  and returns an empty list (DivinityModDataLoader.cs:840-845). LSLib throws on a non-GUID `guid`
  attribute (NodeAttribute.cs:440-447 called at LSXReader.cs:169; rethrown at :332-338). [E] So **a
  single bad UUID in the file reads as a total reset**, whether or not the game touched it. [I]
- **What the detectors miss.** A *partial* drop, where the game removes three bad entries and keeps
  932, trips neither (`CountActive() <= 0`, ModSettingsParseResults.cs:7-18). [E] Today the user only
  notices in-game.

### 2.3 The force-loaded taxonomy

Classification at DivinityModDataLoader.cs:526-640. A file under `Mods/<builtin folder>/`
(IgnoredMods.json folders) sets `isOverridingBuiltinDirectory`. Data under any other `Mods/<X>/` sets
`hasModFolderData`.

| Kind | How detected | UUID | `CanAddToLoadOrder` (DivinityModData.cs:381-384) | In `ForceLoadedMods` (MWVM:5244-5246) | Written to modsettings today |
|---|---|---|---|---|---|
| **K1 pure override** | no meta.lsx, overrides a builtin dir (:606-622) | **pak path** (:620), Folder = first builtin key (:617), `HasMetadata` false | false, unless `ForceAllowInLoadOrder` (the `\|\| forceAllow` at :383) | yes | No, unless forced (§2.4) |
| **K2 meta override** | meta.lsx, overrides builtin, no own data | GUID from meta | false, unless forced. The UI toggle is shown only for this kind (`IsForceLoaded && HasMetadata && !Merged`, :419-421) | yes, until toggled | Only when toggled ("Allow in load order", DivinityGlobalCommands.cs:111-125) |
| **K3 merged** | meta.lsx + own `Mods/<X>` data + builtin overrides (:600-603) | GUID | true | no | Yes, at its row position, like any mod |
| K0 ordinary mod | — | GUID | true | no | Yes |

**[GAME, T-G3]** Every `.pak` in the user Mods folder is mounted, and files under a builtin folder
override base data whether or not the pak is listed in `modsettings.lsx`. That is why upstream calls
them "force-loaded". It follows that K1 has **no position in the game's load order**, K2's module
entry is harmless but redundant, and K3's module part is ordered while its override part is not.
Which of two overriding paks wins for the same file is **unknown** **[GAME, T-G4]**. It may be
filesystem enumeration order. The manager has no way to control it today.

### 2.4 A reachable path on master that writes a pak path as a GUID [I, needs T-B1]

1. The text-format export "JSON file (*.json)" serialises `ActiveMods` **plus `ForceLoadedMods`**
   (MainWindowViewModel.cs:3802-3803) as `List<DivinitySerializedModData>`, which carries `UUID`
   (DivinitySerializedModData.cs:10, :42). For K1 that UUID is the pak path.
2. Import that file with "Load Mod Order From File" (MWVM:3940). If the `DivinityLoadOrder`
   deserialisation fails, `LoadOrderFromFile` falls through to the list branch and adds every entry,
   K1 included (DivinityModDataLoader.cs:1215-1236). Whether Newtonsoft's handled error makes the first
   attempt return null or an empty object is the one unverified step. [I]
3. `LoadModOrder` finds the K1 mod (`mods.Lookup(path)`), marks it active and **sets
   `ForceAllowInLoadOrder = true` for any force-loaded mod** (MWVM:2126-2129). `CanAddToLoadOrder`
   flips to true, so it joins `ActiveMods` (:2141).
4. Export → `BuildOutputList` resolves it → `GenerateModSettingsFile` writes
   `<attribute id="UUID" type="guid" value="C:\Users\…\Mods\Foo.pak"/>` with `Folder="Gustav"`.
5. The watcher re-reads the file, LSLib throws, the manager says "reset externally" (§2.2). In-game
   **[GAME, T-G5]**: probably a rejected or reset load order.

The same step 3 happens for any order JSON that contains a K1 UUID: a hand-edit, a friend's file, or
an order written by upstream **Avalonia**. Avalonia drops the force-loaded exclusion
(`CanAddToLoadOrderCheck` = `modType != "Adventure" && !isHidden`, wt-ava:src/Core/Models/Mod/ModData.cs:374-376).
It gives K1 paks `UUID = pakName` (wt-ava:src/Core/Util/ModDataLoader.cs:696-706) and only checks
`UUID.IsValid()` before writing (wt-ava:src/Services/Services/ModSettingsExportService.cs:98). **[I]**
Avalonia therefore writes K1 entries into `modsettings.lsx` itself, which is the cautionary example for
6.2. Don't copy its approach.

### 2.5 Other places a non-mod row or an override would leak

| Consumer | Code | Risk if a divider / K1 joins `ActiveMods` or `Order` |
|---|---|---|
| `UpdateOrderFromActiveMods` | `Order.Clear(); AddRange(ActiveMods, true)` (MWVM:4880-4889) | Order JSON gains the row |
| Watcher count | `ActiveMods.Count` (MWVM:5429) | An order of only overrides/dividers exports nothing and raises a false "reset" alarm |
| `DisplayMissingMods` | every `Order` entry that doesn't resolve is "missing" (MWVM:2724-2745) | Dividers reported as missing mods, here and in older builds |
| `ClearMissingMods` / `RemoveDeletedMods` | mutate `Order` (MWVM:4670, :4378) | Dividers deleted, positions shift |
| Save As | keeps only `Order` entries that match `Mods` (MWVM:2690) | Dividers silently dropped |
| Zip export | `Mods.Where(Order contains)` + `ForceLoadedMods` (MWVM:3572-3573) | Needs `ForceLoadedMods` to survive 6.2, or overrides vanish from zips (the Avalonia bug, AVA.md:232) |
| Order folder scan | **recursive** `*.json` under Orders (DivinityModDataLoader.cs:1147-1159) | Any sidecar `.json` under Orders is loaded as an order by this build and by every older one |
| Text/TSV import | `.txt` needs `(x.pak)`; `.tsv` needs `Length > fileIndex` (DMDL:1239-1324) | Divider lines without those are ignored, which makes them safe to emit if ever wanted |

---

## 3. Game behaviour model (claims to confirm in game)

| ID | Claim | Confidence | Settled by |
|---|---|---|---|
| G1 | The order of `Mods/ModuleShortDesc` children is the load order (Patch 7+ format, DivinityApp.cs:34) | High, community-standard | T-G1 |
| G2 | The campaign entry (GustavX, Patch 8+) must be present, normally first. Without it the main menu fails (README.md:23) | High (upstream README) | T-G2 |
| G3 | Every pak in the Mods folder is mounted. Builtin-folder overrides apply whether or not they are listed | High | T-G3 |
| G4 | Precedence between two overrides of the same file is not controlled by `modsettings.lsx` | Medium, mechanism unknown | T-G4 |
| G5 | An entry the game can't resolve (unknown GUID, non-GUID UUID, or a builtin Folder with a foreign UUID) makes the game drop it *or* clear the whole list (README.md:24 "the game is clearing the load order") | Medium; which of the two is unknown | T-G5 |
| G6 | Subfolders in the Mods folder reset `modsettings.lsx` (README.md:21) | High (upstream README) | none needed, constraint only: **never implement categories as folders** |
| G7 | The game rewrites `modsettings.lsx` on launch (formatting and its own normalisation). Bytes won't round-trip; the UUID sequence should | Medium | T-G1 |
| G8 | Saves record the mods that were active. Loading a save whose mods are no longer active shows a missing-mods warning | Medium | T-M2 |
| G9 | `MD5` is ignored (DMDL:1479 comment). An empty `PublishHandle`/`MD5` is accepted | High (manager has shipped this) | T-G1 |

---

## 4. Failure-mode catalogue (R7, expanded)

| # | Failure | Trigger | In game | In manager | Prevention (section) | Test |
|---|---|---|---|---|---|---|
| F1 | Divider written as a `ModuleShortDesc` | Divider in `Order`/`ActiveMods` | G5 | Missing-mod warnings | Dividers never enter `ActiveMods`/`Order` (§7–8); guard (§6) | T-U1, T-G6 |
| F2 | K1 written with a path UUID | Forced K1 (§2.4) or naive 6.2 | G5 | False "reset" alarm; LSLib throws | Guard (§6); `LoadModOrder` stops forcing K1 (§6.3) | T-U2, T-B1 |
| F3 | Campaign displaced or missing | A category/overrides block ahead of mods confuses "first entry" logic | G2: menu fails | — | Unchanged by design: `BuildOutputList` prepends the campaign (§2.1) | T-U3, T-G2 |
| F4 | Export order ≠ visible order | Collapse/filter/renumber bug moves mods in `ActiveMods` | Wrong mod wins | Silent | `ActiveMods` derived from rows by one function (§8.3); golden files | T-U4 |
| F5 | `Index` gaps or duplicates | Renumbering counts dividers/overrides | none (export uses sequence, AUDIT-PLAN claim 34) | Confusing numbers; keyboard moves misbehave | Counter over exportable rows only (§8.3) | T-U5 |
| F6 | Dividers lost on save | Save As filter (MWVM:2690), `SetOrder(IEnumerable)` | none | Silent loss | Copy `Dividers` in `SetOrder(DivinityLoadOrder)`/`Clone`; Save As copies them (§8.2) | T-U6 |
| F7 | Sidecar picked up as an order | `.json` under Orders (DMDL:1147-1159) | none | Phantom empty order, also in older builds | Sidecar lives in `Data/` (§8.2) | T-C3 |
| F8 | Older build/upstream sees unknown data | Opening fork files there | none | Must ignore it cleanly | Sibling member only; `Order` mods-only (§9) | T-C1, T-C2 |
| F9 | Overrides vanish from zip export | Removing `ForceLoadedMods` with the pane | none | Friend's game lacks overrides | Keep the collection, retire only the pane (§7) | T-C4 |
| F10 | False reset alarm | `ActiveMods.Count` counts non-exported rows | none | Alarm | `ActiveMods` = exportable mods (§7) | T-U7 |
| F11 | Partial drop by the game goes unseen | G5 "drop" variant | Missing mods in game | Silent | Optional watcher diff (§6.4) | T-G5 |
| F12 | Placeholder divider paks removed by migration | Auto-convert `=====` mods | G8 warning on old saves | — | Opt-in conversion only (§9.3) | T-M2 |
| F13 | XML-unsafe `Folder`/`MD5` | Only `Name` is escaped (DMDL:1480-1481) | none (LoadXml refuses) | Export fails, alerted | Out of scope; the guard escapes `Folder` too (§6.1) | T-U8 |

---

## 5. Invariants the design must keep

- **INV-1 (byte identity).** `GenerateModSettingsFile(BuildOutputList(order.Order, …))` depends only on
  `order.Order`, the mod set, the campaign and the deps setting. Dividers, collapse, filter text and
  pinned overrides can't change it. For any order without categories the output is byte-identical to
  `fc35358`'s.
- **INV-2.** `DivinityLoadOrder.Order` holds only mod entries. `ActiveMods` holds only exportable
  `DivinityModData`: K0, K3, and K2 when `ForceAllowInLoadOrder`.
- **INV-3.** No `ModuleShortDesc` is written unless `Guid.TryParse(UUID)` succeeds and the mod is not K1.
  Anything skipped is reported with `ShowAlert`.
- **INV-4.** The campaign is always the first entry (already true; must stay true).
- **INV-5.** The manager never creates files or folders inside the game's Mods folder for this feature (G6).
- **INV-6.** Every `modsettings.lsx` write is read back with the same LSLib settings the manager reads
  with. The UUID sequence must match what was written.

---

## 6. PR-0: `fix/modsettings-write-guard` (upstreamable, cut from `master`, ships first)

Small, self-contained, and useful to upstream even without 6.2/6.3, because §2.4 is reachable today.

### 6.1 Core: `ModSettingsWriteGuard` (new, `src/Core/Util/ModSettingsWriteGuard.cs`, BOM, CRLF, tabs)

```csharp
using DivinityModManager.Models;

namespace DivinityModManager.Util;

public class ModSettingsSkippedEntry
{
	public string Name { get; set; }
	public string UUID { get; set; }
	public string Reason { get; set; }
}

public static class ModSettingsWriteGuard
{
	/// <summary>
	/// True if the game can be given this mod as a ModuleShortDesc. Pure override paks (no meta.lsx) have
	/// their pak path as a UUID, and anything that isn't a GUID is unreadable as type="guid".
	/// </summary>
	public static bool CanWrite(DivinityModData mod, out string reason)
	{
		reason = "";
		if (mod == null || String.IsNullOrWhiteSpace(mod.UUID))
		{
			reason = "No UUID";
			return false;
		}
		if (mod.IsForceLoaded && !mod.IsForceLoadedMergedMod && !mod.HasMetadata)
		{
			reason = "Override pak without meta.lsx (always loaded by the game)";
			return false;
		}
		if (!Guid.TryParse(mod.UUID, out _))
		{
			reason = "UUID is not a GUID";
			return false;
		}
		return true;
	}

	public static List<DivinityModData> Filter(IEnumerable<DivinityModData> orderList, List<ModSettingsSkippedEntry> skipped)
	{
		var result = new List<DivinityModData>();
		foreach (var mod in orderList)
		{
			if (CanWrite(mod, out var reason))
			{
				result.Add(mod);
			}
			else
			{
				skipped.Add(new ModSettingsSkippedEntry { Name = mod?.Name, UUID = mod?.UUID, Reason = reason });
			}
		}
		return result;
	}
}
```

Byte identity (INV-1): an order with no K1 entries and GUID UUIDs passes through unchanged. So `fc35358`
output is reproduced exactly. That is test T-U1. The extra `SecurityElement.Escape(mod.Folder)` for
F13 also leaves normal folder names unchanged. Include it only if the owner accepts it in PR-0;
otherwise defer it.

### 6.2 Hook points

| Where | Change |
|---|---|
| `DivinityModDataLoader.GenerateModSettingsFile` (:1471-1485) | Keep the signature for callers. Add an overload `GenerateModSettingsFile(IEnumerable<DivinityModData>, List<ModSettingsSkippedEntry>)` that filters through the guard; the old one calls it with a throwaway list. |
| `ExportModSettingsToFileAsync` (:1331-1364) | Return a result object instead of `bool`: `ModSettingsExportResult { bool Success; List<ModSettingsSkippedEntry> Skipped; bool ReadBackMatches; string Error; }`. After writing, `await LoadModSettingsFileAsync(outputFilePath)` and compare the UUID sequence (with ignored mods included) against what was written. That is INV-6. |
| Callers: MWVM:2327 (restore LastExported) and :2932 (export) | If `Skipped.Count > 0`: `ShowAlert($"{n} entries were not written to modsettings.lsx: {names}", AlertType.Warning, 0)`. If `!ReadBackMatches`: `AlertType.Danger`, and treat it as a failed export (no `HasExported = true`, so the watcher doesn't fire a second alarm). |
| `LoadModOrder` (MWVM:2126-2129) | `if (mod.IsForceLoaded && mod.HasMetadata) mod.ForceAllowInLoadOrder = true;`. A K1 entry in an order is no longer promoted into `ActiveMods`. It stays an override (6.2 shows it in the Overrides block). This closes §2.4 at the entry point; §6.1 closes it at the exit. |

Sketch of the read-back:

```csharp
var readBack = await LoadModSettingsFileAsync(outputFilePath);
var written = order.Select(x => x.UUID).ToList();
var read = readBack.ActiveMods.Select(x => x.UUID).ToList();
// LoadModSettingsFileAsync drops IgnoredMods (DivinityModDataLoader.cs:861), so compare like with like
written.RemoveAll(DivinityModDataLoader.IgnoreMod);
result.ReadBackMatches = written.SequenceEqual(read, StringComparer.OrdinalIgnoreCase);
```

### 6.3 Why upstream would take it

It fixes a real defect (§2.4, F2) with no behaviour change for valid orders. It turns the misleading
"reset externally" alarm into a precise message naming the bad entry. It doesn't depend on any
fork-only work.

### 6.4 Optional PR-0b: `fix/modsettings-partial-reset-report` (upstreamable)

Extend the watcher (MWVM:5420-5445). Keep the last-written UUID list from §6.2. When the file changes
after an export, report entries that **disappeared** even if others remain: "The game removed 3 mods
from the load order: A, B, C". This catches the G5 "drop" variant (F11). It's also the cheapest
instrument for T-G5. Diagnostic only: it never rewrites the file.

---

## 7. Stage 6.2: overrides folded into the active list (`feat/overrides-in-order`, fork-only)

### 7.1 Semantics (minimal and safe)

- The active list shows one **Overrides block** with a header row. Its members are the current
  `ForceLoadedMods` (K1, plus K2 not yet allowed; MWVM:5244-5246).
- **Nothing about the block is exported or persisted in `Order`.** Its members have `Index = -1`, shown
  as "—", and are not in `ActiveMods`. INV-1 holds by construction: same flags, same bytes.
- K2 "Allow in load order" stays exactly the existing toggle (DivinityGlobalCommands.cs:111-125). Once
  allowed, the mod leaves the block and joins the ordered rows at the end, which is what
  `AddActiveMod` does today (MWVM:4841-4851). Untoggling returns it to the block (`RemoveActiveMod`,
  :4853-4878).
- K3 is already an ordinary orderable mod. No change.
- **Members are not draggable within or out of the block in v1.** `CanDrag` is already false for
  K1/K2 (DivinityModData.cs:465-481). Their position means nothing to the game (G3/G4), so reordering
  them would be an empty gesture. K2 enters the order through the toggle only. Dragging K2 out could
  map to the toggle later (S6-B §6.2 proposes this); it's deferred to keep the risk surface small.
- **The block's own position:** pinned at the end of the active list in v1. The owner's mock-up puts
  it between categories. Making it movable is view-only and safe (persist the block position as a
  pseudo-divider anchor in the sidecar), but it adds a drag case. That's Q1.

### 7.2 Row model

The same `ActiveRows` approach as S6-B §4, which I adopt. The active `ListView.ItemsSource` becomes
`ActiveRows` (`ObservableCollectionExtended<object>`): mods, `CategoryDivider` rows (6.3) and one
`OverridesHeaderRow` followed by the override members. `ActiveMods` keeps its type and meaning and is
**derived** from `ActiveRows` by one function (§8.3). Each of the ~70 readers of `ActiveMods` keeps
working, every export path included.

Core, new file `src/Core/Models/OverridesHeaderRow.cs`:

```csharp
namespace DivinityModManager.Models;

/// <summary>
/// The header of the pinned Overrides block in the active list. Never exported, never in Order.
/// </summary>
public class OverridesHeaderRow : ReactiveObject, ISelectable
{
	[Reactive] public bool IsCollapsed { get; set; }
	[Reactive] public int Count { get; set; }
	[Reactive] public bool IsSelected { get; set; }
	[Reactive] public bool CanDrag { get; set; }
	[Reactive] public Visibility Visibility { get; set; } = Visibility.Visible;
}
```

(`ISelectable` is `IsSelected`, `Visibility`, `CanDrag { get; }` (src/Core/Models/ISelectable.cs:5-10). `CanDrag` is
already read through it by ModListDragHandler.cs:105.)

### 7.3 Hook points

| Where | Change |
|---|---|
| HorizontalModLayout.xaml:103-141 | Remove grid row 2 (`ForceLoadedModsListView` and its "Overrides" caption) and the splitter row. Keep `ActiveModsListView` only. |
| HorizontalModLayout.xaml.cs:526 | `OneWayBind(ViewModel, vm => vm.ActiveRows, v => v.ActiveModsListView.ItemsSource)` |
| HorizontalModLayout.xaml.cs:31, :45 (`ForceLoadedModsView`), wt-perf:HorizontalModLayout.xaml.cs:185-193 (`GetListViewFor`) | Overrides now select in `ActiveModsListView`. `GetListViewFor` returns `ActiveModsListView` for K1/K2. Remove the `IModViewLayout.ForceLoadedModsView` member and its uses: ModListDropHandler.cs:242, MWVM:5056-5058 (keyboard target list). |
| ModListDropHandler.cs:105 `isActive = TargetCollection == ActiveMods` and ModListDragHandler.cs:68, :73 `SourceCollection == ActiveMods` | Compare against `ActiveRows`. Never remove a K1/K2 or header row from the source in `Drop`: filter `data` to `DivinityModData` with `CanDrag`. |
| MWVM:5244-5246 (`ForceLoadedMods`) | **Keep the collection.** The zip export (:3573) and text export (:3803) need it (F9). Subscribe to its change set to rebuild the Overrides block. |
| MWVM:792 / `AutoSizeNameColumn_ActiveMods` | Unchanged (it already measures across both collections). |

### 7.4 What 6.2 must prove

1. For the same mod set and flags, `modsettings.lsx` is byte-identical between `fc35358` and
   `feat/overrides-in-order` (T-U1, T-G1).
2. The overrides' in-game effect is unchanged (T-G3).
3. Zip export still contains the K1 paks (T-C4).

---

## 8. Stage 6.3: categories and dividers (`feat/categories`, fork-only, on top of 6.2)

### 8.1 Model and JSON (S6-B §3.2's shape, adopted)

`Order` stays mods-only. A new optional sibling member `Dividers` is anchored by the UUID of the first
mod after the divider, with a position fallback. I chose this over S6-A's `Layout` (which also persists
pinned overrides) because 6.2 persists nothing. A dividers-only member is the smallest new surface for
older builds to ignore.

New file `src/Core/Models/OrderDivider.cs`:

```csharp
using System.Runtime.Serialization;

namespace DivinityModManager.Models;

[DataContract]
public class OrderDivider
{
	[DataMember] public string Id { get; set; }
	[DataMember] public string Name { get; set; }

	/// <summary>UUID of the first mod after this divider. Empty = end of the order.</summary>
	[DataMember(EmitDefaultValue = false)] public string Before { get; set; }

	/// <summary>Number of Order entries before this divider when saved. Used when Before no longer resolves.</summary>
	[DataMember] public int Position { get; set; }

	[DataMember(EmitDefaultValue = false)] public bool Collapsed { get; set; }

	public OrderDivider Clone()
	{
		return (OrderDivider)MemberwiseClone();
	}
}
```

`DivinityLoadOrder` (src/Core/Models/DivinityLoadOrder.cs):

```csharp
[DataMember(EmitDefaultValue = false)]
public List<OrderDivider> Dividers { get; set; }

public bool HasDividers => Dividers != null && Dividers.Count > 0;
```

- `Dividers` stays **null** when there are none, so with `EmitDefaultValue = false` the order JSON is
  byte-identical to today's for uncategorised orders. That's T-U6b, and it keeps diffs clean for
  people sharing orders.
- `SetOrder(DivinityLoadOrder)` (:289-293) and `Clone()` (:304-312) copy
  `Dividers?.Select(x => x.Clone()).ToList()`. `SetOrder(IEnumerable<DivinityLoadOrderEntry>)` (:283)
  leaves `Dividers` alone.

Example (the plan's mock-up, Cosmetics collapsed):

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

The `modsettings.lsx` exported from this order is exactly GustavX, then the four mods. It is
byte-identical to the same order without `Dividers` (T-U1).

Note that `Id` is a GUID string, but it is **never** a `DivinityLoadOrderEntry`, so it can't collide
with a mod UUID in `BuildOutputList`.

### 8.2 Where dividers persist

| Order | Store |
|---|---|
| Saved orders (`Orders/*.json`) | `Dividers` member of the same file, via the existing `ExportLoadOrderToFileAsync` (DMDL:1098-1114) |
| "Current" (from modsettings.lsx, MWVM:1766-1790) | `Data/categories.json` (`DivinityApp.GetAppDirectory("Data", …)`), as `Dictionary<string, List<OrderDivider>>` keyed by profile UUID. **Not under Orders** (F7: recursive `*.json` scan, DMDL:1147-1159). Attach in `BuildModOrderList` after :1790. Save after a successful export and on divider edits (the 250 ms debounce at MWVM:5407). Write failures → `ShowAlert(Warning)`. |
| LastExported | `BackupCurrentLoadOrderAsync` (MWVM:2850-2874) uses `SetOrder(DivinityLoadOrder)`, so it picks up `Dividers` once §8.1 lands |
| Export "X" → Current | MWVM:2967 `SetOrder(SelectedModOrder.Order)` becomes `SetOrder(SelectedModOrder)` |
| Save As | MWVM:2689-2690 copies `Dividers` too. Anchors to filtered-out mods fall back to `Position` on load |
| Zip export | The order JSON in the zip carries `Dividers` without any extra code. Stage 3 import must keep them; watch AUDIT-PLAN-01's `SetFrom` path, which already mangles imported orders |

Because the game rewrites modsettings.lsx (G7) and may drop entries (G5), the "Current" sidecar
**must** tolerate missing anchors: `Before` unresolved → use `Position`, clamped. A divider with no
remaining members is kept, empty and visible, never deleted silently.

### 8.3 The one rebuild function

`RowLayout` is a Core class with no WPF types except `Visibility`, which Core already uses. It is pure
and unit-testable. It has two directions:

```csharp
public static class RowLayout
{
	/// <summary>Order + dividers + overrides → rows, as shown in the active list.</summary>
	public static List<object> BuildRows(IList<DivinityModData> orderedMods, IList<OrderDivider> dividers,
		IList<DivinityModData> overrides, OverridesHeaderRow overridesHeader, Func<string, CategoryDivider> getDividerRow)
	{
		// For each divider: anchor = index of Before in orderedMods, else Clamp(Position, 0, orderedMods.Count).
		// Stable-sort dividers by anchor, insert. Then overridesHeader + overrides at the end (v1).
		...
	}

	/// <summary>Rows → exportable mods in order, renumbered, plus the dividers to persist.</summary>
	public static void Project(IList<object> rows, IList<DivinityModData> activeModsOut, List<OrderDivider> dividersOut)
	{
		activeModsOut.Clear();
		dividersOut.Clear();
		var index = 0;
		var pending = new List<CategoryDivider>();
		foreach (var row in rows)
		{
			if (row is CategoryDivider divider)
			{
				pending.Add(divider);
			}
			else if (row is DivinityModData mod && mod.CanAddToLoadOrder)
			{
				foreach (var d in pending)
				{
					dividersOut.Add(d.ToOrderDivider(mod.UUID, index));
				}
				pending.Clear();
				mod.Index = index;
				activeModsOut.Add(mod);
				index++;
			}
			// OverridesHeaderRow and K1/K2 members: not exported, Index stays -1.
		}
		foreach (var d in pending)
		{
			dividersOut.Add(d.ToOrderDivider("", index));
		}
	}
}
```

- `Project` is the **only** writer of `ActiveMods` and `mod.Index` for the active side. It replaces
  the `IndexOf` renumbering loop at ModListDropHandler.cs:218-221, which is O(n²) at 935 rows (AUDIT-PLAN
  claim 34), and is called at the end of `Drop`, `AddActiveMod`/`RemoveActiveMod` (MWVM:4841-4878),
  keyboard moves (`ModListView.cs:219-321`) and divider edits.
- `UpdateOrderFromActiveMods` (MWVM:4880-4889) also copies `dividersOut` into `SelectedModOrder.Dividers`.
- **F4/F5 proof obligation:** for any row permutation, `activeModsOut` equals the rows filtered to
  exportable mods in sequence, and `Index` is 0..n-1 with no gaps. Property-test it (T-U4, T-U5).

### 8.4 Divider row model (`src/Core/Models/CategoryDivider.cs`)

`ReactiveObject, ISelectable` with `Id`, `Name`, `IsCollapsed`, `MemberCount`, `IsSelected`,
`CanDrag = true` and `Visibility`, plus `ToOrderDivider(string before, int position)`. It's not a
`DivinityModData`, so wt-perf's selection reconcile (`SelectedItems.OfType<DivinityModData>()`,
wt-perf:HorizontalModLayout.xaml.cs:238-249) ignores it without change.

### 8.5 Rendering (virtualization-safe)

Use a type-keyed `ItemContainerStyleSelector`. Divider rows get their own full-width `ListViewItem`
template; mods keep `ListViewItemMouseEvents` → `DivinityModDataListItem` (MainResourceDictionary.xaml:255,
HorizontalModLayout.xaml:23). **Never** use `GroupStyle`/`CollectionViewSource.GroupDescriptions`
(MainResourceDictionary.xaml:460-461). The `ItemContainerStyle` attribute set at HorizontalModLayout.xaml:116
and in code at HorizontalModLayout.xaml.cs:109 must be removed, or it overrides the selector. S6-B §7
covers this in detail. XAML sketch:

```xml
<Style x:Key="CategoryDividerListItem" TargetType="{x:Type ListViewItem}">
	<Setter Property="Tag" Value="{Binding}" />
	<Setter Property="Visibility" Value="{Binding Visibility}" />
	<Setter Property="IsSelected" Value="{Binding IsSelected, Mode=TwoWay}" />
	<Setter Property="HorizontalContentAlignment" Value="Stretch" />
	<Setter Property="Template">
		<Setter.Value>
			<ControlTemplate TargetType="{x:Type ListViewItem}">
				<Border Padding="4,2" Background="{DynamicResource {x:Static adonisUI:Brushes.Layer2BackgroundBrush}}">
					<StackPanel Orientation="Horizontal">
						<ToggleButton Width="16" IsChecked="{Binding IsCollapsed, Mode=TwoWay}" />
						<controls:CachedTextBlock Margin="6,0" FontWeight="SemiBold" Text="{Binding Name}" />
						<TextBlock Foreground="{DynamicResource {x:Static adonisUI:Brushes.DisabledForegroundBrush}}" Text="{Binding MemberCount}" />
					</StackPanel>
				</Border>
			</ControlTemplate>
		</Setter.Value>
	</Setter>
</Style>
```

`CachedTextBlock` (wt-perf:src/GUI/Controls/CachedTextBlock.cs) is reused for the divider name so a
divider costs no more than a mod name. Measure the template swap on recycle with PerfProbe before
merging (T-P1).

### 8.6 Collapse and filter (one visibility rule, Stage-5-neutral)

`IsRowVisible(row) = MatchesFilter(row) && !(IsInCollapsedCategory(row) && !FilterActive)`. Divider
rows match a filter if any member does. A single `ApplyRowVisibility()` is called by
`OnFilterTextChanged` (today stamps `Visibility`) and by collapse toggles. If Stage 5 moves to collection
filtering, the same predicate becomes the `Filter`. Collapse never adds, removes or reorders rows, so
`Project` and `Index` are untouched (owner rule 6, T-U9).

### 8.7 Drag, drop and selection rules

| Gesture | Rule |
|---|---|
| Drag mods across a divider | Allowed. Rows move; `Project` renumbers; the mods become members of whichever category they land in |
| Drag a divider (expanded) | Moves the divider row only. Membership is re-derived from position |
| Drag a divider (collapsed) | Moves the divider **and its hidden members** as one block. `StartDrag` today takes only `IsSelected && Visible` (ModListDragHandler.cs:70-72), so expand the data explicitly to the contiguous member range |
| Drop into the Overrides block / drag K1-K2 | Rejected (`Effects = None` in `DragOver`). Overrides are not positional |
| Drop dividers into the inactive list | Rejected. Dividers exist only in the active list |
| Move active → inactive | Unchanged; dividers left behind stay where they are, possibly empty |
| Selection | `IsSelected` on a hidden row stays refused (DivinityModData.cs:185-196, same rule on `CategoryDivider`). wt-perf's `SetSelection` (wt-perf:src/GUI/Controls/ModListView.cs:202) is fed `ISelectable` rows from `ActiveRows` |
| Keyboard move (ModListView.cs:219-321) | A move hops over a collapsed category as one step, never into its hidden range |

### 8.8 Exports other than the game

The TXT/TSV/JSON "Export Order to Text" (MWVM:3770-3820) leaves dividers out in v1. That keeps the
deciphered-import paths (DMDL:1215-1324) and every older build unaffected. Dividers could be added to
TSV later as single-column lines, which the importer skips because `Length > fileIndex` fails.

---

## 9. Migration and compatibility

### 9.1 Existing saved orders

None needs migrating. No `Dividers` member means no categories, and the bytes written stay the same
(T-U6b). An order containing a K1 UUID (§2.4) now loads with that mod shown in the Overrides block and
not exported. The user gets one `ShowAlert(Info)` saying so, which is a behaviour change for the better
(PR-0).

### 9.2 Other builds reading fork files

| Reader | Loads `Order` | `Dividers` | modsettings.lsx it would write |
|---|---|---|---|
| This fork | yes | used | INV-1 |
| Older fork / upstream WPF master | yes (Newtonsoft ignores unknown members by default; `[DataContract]` opt-in) **[I, T-C1]** | ignored; dropped if that build re-saves the file | identical to this fork's for the same mods |
| Upstream Avalonia | migrates `Order` into `Entries` (per S6-B, wt-ava ModOrder.cs:324-333) **[I, T-C2]** | ignored | Its own rules. It may write K1 entries (§2.4), which isn't caused by this design |
| This fork reading files from those builds | yes | none | A K1 entry written by Avalonia is neutralised by PR-0 |

Downgrade path: a user going back to upstream loses categories but nothing else, and their game sees
the same mods.

### 9.3 The owner's existing placeholder divider paks

The real order contains `=====`-style divider **mods** (plan :406-407). They are real paks with
meta.lsx, listed in modsettings.lsx today. **[GAME, T-M2]** The game loads them, they do nothing, and
saves made with them active may list them.

- **No automatic conversion.** Removing them changes `modsettings.lsx` and may raise G8 missing-mod
  warnings on existing saves (F12).
- Offer an explicit context-menu action, "Replace with category divider". It inserts a
  `CategoryDivider` named after the mod at the mod's row and moves the mod to the inactive list. It
  confirms first: "This mod will no longer be loaded by the game. Saves made with it active may warn
  that it is missing."
- Detection (for offering the action only) is a heuristic: a name made mostly of `=`/`-`/`_`/`#`, or
  no files beyond meta.lsx (`Files.Count <= 1`, DivinityModData.Files). Label it a heuristic in the UI.
- Do this in a follow-up PR inside `feat/categories`, after T-M2 settles what the game does with
  saves.

### 9.4 Stage 8 (multi-order export)

Each order's JSON in the zip carries its own `Dividers`. The deduplicated mod union is computed from
`Order` (mods-only) plus `ForceLoadedMods`, so dividers can't add phantom entries to the union. One
helper function is shared with §6: a "would-be-written" set per order (`BuildOutputList` + guard). It
shows what each order would put into modsettings.lsx, a natural preview for a friend importing it.

---

## 10. Verification protocol

### 10.1 Unit/golden tests (Core, no game needed)

No test project exists on master (CLAUDE.md §8.4). Add a small `src/Tests` (xUnit, `net8.0-windows`,
referencing Core) in `feat/overrides-in-order`, or in PR-0 if upstream wants it.

| ID | Test |
|---|---|
| T-U1 | **Golden bytes.** Fixture mods (K0×3, K3, K2 allowed/not, K1, campaign). `GenerateModSettingsFile(BuildOutputList(order))` for (a) no dividers, (b) with dividers + collapse, (c) with the Overrides block. All three are byte-equal to a golden file generated once from `fc35358` code. |
| T-U2 | Guard: K1 (path UUID), `"not-a-guid"`, empty UUID are skipped and reported; K2 with `ForceAllow` is written. |
| T-U3 | Campaign first in all T-U1 variants, including a divider/overrides block at row 0. |
| T-U4 | `RowLayout.Project` property test: random permutations of mods + dividers + overrides → `ActiveMods` equals exportable mods in sequence. |
| T-U5 | `Index` is 0..n-1, contiguous, after random drags; dividers/overrides have `Index = -1`. |
| T-U6 | `SetOrder`/`Clone`/Save As/LastExported carry `Dividers`. T-U6b: an order without dividers serialises byte-identically to today's. |
| T-U7 | Watcher count uses `ActiveMods` only: an order of only overrides + dividers doesn't trigger "reset". |
| T-U8 | `&`, `<`, `"` in mod Name (and Folder, if F13 is in) produce valid XML. Divider names never appear in the output. |
| T-U9 | Collapse/expand any category: `ActiveMods` sequence and every `Index` unchanged. |
| T-U10 | Read-back (INV-6): writing a file then `LoadModSettingsFileAsync` yields the same UUID sequence. A hand-crafted path UUID makes LSLib throw (documents §2.2). |

### 10.2 In-app checks (Windows, Debug **and** Publish build, R5)

| ID | Check |
|---|---|
| T-B1 | Reproduce §2.4 on `fc35358`: Export Order to Text → JSON, then Load Mod Order From File, then Export to Game. Expected on master: an override row appears in Active and the file contains a pak path as UUID. On PR-0: the override stays in Overrides, the export alert lists nothing skipped (it never reached the list), and modsettings.lsx contains no path. If step 2 doesn't reproduce, record that; PR-0's guard is still the exit-side defence. |
| T-C1 | Save a categorised order and open it in an unmodified upstream release: mods load, no missing-mod popup, Export to Game writes the same UUID sequence as the fork. |
| T-C2 | Same file in an upstream Avalonia build if one runs: it loads without error (no export needed). |
| T-C3 | After using categories on "Current", the order dropdown in both the fork and the upstream release shows no new phantom order. |
| T-C4 | Zip export of an order with categories: the zip contains every K1 pak and an order JSON with `Dividers`. Re-import restores categories (once Stage 3 is fixed). |
| T-P1 | PerfProbe scroll run (plan §10, 6.1 rig) on 935 rows with ~20 dividers and the Overrides block: median frame and >33 ms share within noise of the 6.1 numbers (21 ms / 19–21 %). Containers created stays ~106 (virtualization alive). |

### 10.3 In-game protocol (the gate for each feature PR)

**Setup (once).**
1. Close the game. Copy `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\modsettings.lsx`
   to a safe folder, plus a directory listing of `...\Baldur's Gate 3\Mods`. Record the game version
   (title screen).
2. Create a **test profile** in-game for destructive tests (T-G5). New profiles must be made in-game
   (README.md:50).
3. Fixture paks in the Mods folder (no subfolders, G6): one K1 that visibly changes something
   (e.g. a UI/texture override you can see at the main menu), one K2, one K3, one placeholder divider
   mod, and your normal 935 mods.
4. A save made before the test, with your normal order.

**Per build under test (`fc35358` baseline, PR-0, 6.2, 6.3).**

| Step | Action | Look for |
|---|---|---|
| 1 | In the manager, load order X, Export to Game | Success alert, no skipped-entries alert (or the expected one on T-B1 fixtures) |
| 2 | Copy modsettings.lsx → `X_<build>.lsx`; `certutil -hashfile X_<build>.lsx SHA256` | **INV-1:** hash equal across `fc35358`, PR-0, 6.2, 6.3 for the same order without categories. For 6.3, add dividers, collapse two, export again: the hash must not change |
| 3 | Launch the game (from the manager or directly). Reach the main menu and wait ~30 s | No "mods disabled/reset" prompt. Main menu loads (G2) |
| 4 | Open the in-game Mod Manager (Patch 7+) | Enabled mod count and order match the manager's active list, excluding Overrides (G1). Overrides don't appear as modules (G3) |
| 5 | Check the K1 fixture's visible effect | Present, whatever its position in the list (G3) |
| 6 | Load the pre-test save | Same missing-mod warning as on the baseline build, ideally none (G8) |
| 7 | Quit to desktop. Copy modsettings.lsx → `X_<build>_after.lsx` | UUID sequence (compare with a small script or the manager's import) equal to step 2's (G7: formatting may differ, the sequence must not). The manager's watcher shows no "reset" alert, and on PR-0b no "removed" report |

**One-off, on the test profile only. Establishes G5 and validates the detectors.**

| ID | Hand-edit modsettings.lsx, then boot | Record |
|---|---|---|
| T-G5a | Add a `ModuleShortDesc` with a random valid GUID that matches no installed mod | Dropped alone, or whole list cleared? Does PR-0b report it? |
| T-G5b | Add one with `UUID="C:\x.pak"` and `Folder="Gustav"` (the §2.4 shape) | Same question. The manager should alarm on read-back |
| T-G4 | Two K1 paks overriding the same visible file. Swap their file names (alphabetical order) | Does the winner change? If yes, override precedence is by file name. Note it as a possible future feature (rename-based ordering), not part of 6.2 |
| T-M2 | Deactivate the placeholder divider mod, boot, load a save made with it active | Is there a missing-mod warning? That decides §9.3's wording |
| T-G1/T-G2/T-G3 | Covered by the per-build steps 3–5 | — |

Restore the backed-up modsettings.lsx after the destructive tests. The manager's `LastExported` restore
(MWVM:2283-2333) is a second safety net, but don't rely on it while it is the thing under test.

**Pass criteria per PR.** All of T-U*; steps 1–7 green on the baseline order and on a categorised
copy; T-C1, T-C3, T-C4; T-P1 for 6.3. PR-0 additionally needs T-B1. Record results in
implementation-plan "Testing" with the hashes.

---

## 11. Risks

| # | Risk | Likelihood | Mitigation |
|---|---|---|---|
| C1 | G5 is "clear all", so any leaked entry wipes the user's order in-game | Medium | PR-0 guard + read-back. INV-1 golden test. Leaks are blocked structurally (ActiveRows/ActiveMods split) |
| C2 | `RowLayout.Project` bug reorders exported mods silently (F4) | Medium | One function, property tests, golden bytes. Export reads `Order` only |
| C3 | Removing the Overrides pane drops overrides from zip/text exports (F9) | Low with mitigation | Keep `ForceLoadedMods`; T-C4 |
| C4 | Users read the Overrides block's position as meaningful | Medium | No `Index`; header tooltip "Always loaded by the game; position has no effect". T-G4 may change this |
| C5 | Sidecar `Data/categories.json` lost (portable folder replaced on update) | Medium (the release path deletes `Data/`, CLAUDE.md §7) | Same exposure as settings.json. Also mirror Current's dividers into LastExported.json (§8.2). Note the release-packaging deletion is of the *build* `Data/`, not the user's; confirm with T-C5 when Stage 1's updater exists |
| C6 | Container template swap on recycle costs frames | Low–medium | T-P1; fall back to one container style with a divider `DataTemplate` in the name column if needed |
| C7 | PR-0 guard skips a mod the game would have accepted (non-GUID meta UUID that BG3 tolerates) | Low (attribute is `type="guid"`) | The alert names it. If T-G5 shows tolerance, relax the GUID clause to "warn, still write" |
| C8 | Merge friction with perf/list-render | Medium | 6.2 starts after `perf/list-render` is merged to `master` and `fork/main`. The ActiveRows switch touches the same `SelectMods`/`GetListViewFor` lines |

---

## 12. Branches and PRs

```
master ── fix/modsettings-write-guard (PR-0, upstream PR)      ← first, independent of Stage 6
       ├─ fix/modsettings-partial-reset-report (PR-0b, optional, upstream PR)
       └─ perf/list-render (6.1, upstream PR; drop TEMP commits first)
fork/main ← merge master (with the above) --no-ff
       └─ feat/overrides-in-order
             1. Core: OverridesHeaderRow, RowLayout (BuildRows/Project) + tests
             2. VM: ActiveRows, Project wiring, LoadModOrder → BuildRows
             3. View: remove Overrides pane, bind ActiveRows, style selector, drag/drop guards
             Gate: §10 per-build steps, T-C4, T-U1/4/5/7
       └─ feat/categories (on top of feat/overrides-in-order)
             1. Core: OrderDivider, DivinityLoadOrder.Dividers (+SetOrder/Clone), CategoryDivider
             2. VM: categories.json store for Current, Save As / export / LastExported carry-through
             3. View: divider container style, collapse, divider drag, rename/add/delete context menu
             4. (follow-up) "Replace with category divider" for placeholder paks, after T-M2
             Gate: §10 full, T-C1-3, T-P1, T-U6/6b/9
```

PR-0 and PR-0b are cut from `master` and contain nothing fork-only (R3).

---

## 13. Where this differs from or adds to S6-A / S6-B

- **Agrees:** `Order` mods-only; dividers in a sibling member; overrides not persisted;
  flat rows; ActiveRows/ActiveMods split (S6-B); Current's dividers outside the Orders folder.
- **Adds:** the upstreamable write guard and read-back (PR-0), backed by the LSLib evidence that a
  non-GUID UUID reads as a total reset (§2.2). The master-reachable K1 leak (§2.4) and Avalonia's version
  of it. The partial-drop detector (PR-0b). The game-claims table with a test for each. The
  per-build hash protocol. Opt-in placeholder-pak migration with the save-warning risk.
- **Differs:** v1 doesn't let K2 be dragged out of the Overrides block (S6-B §6.2 does). The toggle is
  the only entry, to keep 6.2's risk surface at zero byte changes. Overrides get no `Index`, against
  the plan's mock-up numbering (Q1).

## 14. Open questions for the owner

- **Q1.** Overrides block: pinned at the end with no index numbers (this design), or movable between
  categories as in the mock-up (view-only, still unnumbered)? Numbering them as in the mock-up implies
  an ordering effect the game doesn't have (G3).
- **Q2.** PR-0's GUID clause: skip-and-alert (this design) or warn-but-write until T-G5 is run?
- **Q3.** Placeholder divider paks: offer "Replace with category divider" at all, and only after T-M2?
- **Q4.** May PR-0 add a `src/Tests` project to master-based branches? Upstream has none.
