# HARV-M2 verification results

Read-only review of the SpasticPengu fork of BG3 Mod Manager. Statuses come from the workflow's verifier votes; nothing was re-verified here.

## Summary

| Status | Count |
|---|---|
| Confirmed | 6 |
| Plausible | 2 |
| Disputed | 0 |
| Refuted | 1 |
| Unverified | 0 |
| **Total** | **9** |

| Final severity | Count |
|---|---|
| Medium | 6 |
| Low | 3 |

Severity changes: HARV-30, HARV-47 and HARV-54 went from Medium to Low. HARV-69 was refuted (a voter suggested Low; the ledger keeps Medium).

## Findings

### HARV-33 [Medium] Importing a mod replaces the installed one by UUID even when it is older (Confirmed, PARTLY_KNOWN)
Location: MWVM:3160-3227
AddImportedMod finds the existing mod by UUID and calls Replace in Active/InactiveMods, ModOrderList and the cache with no version check (MainWindowViewModel.cs:3172-3192). All three import paths reach it (:1846-1853, :3297-3323, :3451). On refresh, DivinityModDataLoader.cs:787-805 keeps the highest VersionInt, so the UI can disagree with what was imported. When file names match, the copy or extract also overwrites the newer pak on disk, so the downgrade is real.
Fix caveats: compare versions and confirm before a downgrade. Also check before overwriting a same-named pak. Use Version.VersionInt, the value refresh uses, not Version64.
DocRef: AUDIT-PLAN-04

### HARV-53 [Medium] One unparseable entry in modsettings.lsx reads as a total reset (Confirmed, NEW)
Location: DMDL:838-845; MWVM:2283-2292, 5420-5445
The LSX LoadResourceAsync overload and the outer try/catch both swallow parse failures and return null, which gives an empty ActiveMods (DivinityModDataLoader.cs:838-847, 963-978). LSLib calls new Guid(str) on UUID attributes (NodeAttribute.cs:440-448), so one non-GUID value empties the whole file. Both reset detectors (MainWindowViewModel.cs:2287-2298, 5426-5440) then fire on CountActive()<=0 and say "reset externally" without mentioning a read error. Their preconditions are normal states.
Fix caveats: return a parse-failed flag and the exception, show "could not be read" and skip the restore offer. The catch inside the overload has to change too. Apply the same to the profile loader (DMDL:931), and stop the startup "Yes" from overwriting the file silently.
DocRef: CLAUDE.md section 6 Loader (names LoadModSettingsFileAsync only)

### HARV-62 [Medium] Saving an order truncates the JSON file in place (Confirmed, PARTLY_KNOWN)
Location: DMDL:1098-1113
ExportLoadOrderToFileAsync opens the file with FileMode.Create and makes one WriteAsync, with no temp file or rename (DivinityModDataLoader.cs:1098-1113; sync twin at 1080-1096). Ctrl+S reaches it for every order other than "Current" (MainWindowViewModel.cs:2597-2633, 4969). A crash or full disk leaves the file empty or partial. The next scan only logs the failure, and the order silently drops from the list (DMDL:1115-1145). The failure is unlikely but real.
Fix caveats: write to a .tmp file, call Flush(true), then File.Replace (or File.Move with overwrite when no target exists). Apply the same to the sync twin and probably to ExportLoadOrderAsync for modsettings.lsx.
DocRef: AUDIT-PLAN-01

### HARV-63 [Medium] Duplicate mods with equal versions pick a random winner, and the loser is offered for deletion (Confirmed, NEW)
Location: DMDL:787-806; MWVM:1695-1708 -> DeleteMods(..., true) :4359-4371
Paks load in parallel into a ConcurrentBag, and the dedupe uses a strict > on VersionInt (DivinityModDataLoader.cs:773-806), so on a tie whichever copy enumerates first wins, and that order is not deterministic. The losers go to DeleteMods(...,true), which opens the dialog with every entry pre-selected (MainWindowViewModel.cs:1702-1708, 4359-4370; ModFileDeletionData.cs:13-20). Ties are common because many mods keep the same version across re-downloads. The impact is limited because the user must confirm.
Fix caveats: tie-break on newest LastWriteTime, then ordinal path, and consider not pre-selecting entries on a tie. Correction: the dialog already shows the kept file's path (Duplicates field). It lacks dates and sizes.
DocRef: none

### HARV-71 [Medium] Dropping into a column-sorted active list inserts at the view index, not the load-order index (Confirmed, CONTRADICTS_DOCS)
Location: src/GUI/Views/HorizontalModLayout.xaml:115, .xaml.cs:755-769; src/GUI/ViewModels/ModListDropHandler.cs:150-175
A header click adds a SortDescription to the ActiveMods default view (HorizontalModLayout.xaml:115; .xaml.cs:752-769). gong's UnfilteredInsertIndex remaps only when Items.Count differs from the source count (dl-gong/DropInfo.cs:55-80), and sorting keeps the count, so the handler inserts at the sorted-view index (ModListDropHandler.cs:133, 150-162, 188-191). The same-list adjustment mixes source and view indices, DragOver does not block sorted drops (:79-88), and the view stays sorted, so the order change is silent. This contradicts AUDIT-PLAN's claim that UnfilteredInsertIndex handles sorted views.
Fix caveats: map the target item to its ActiveMods index, including in the same-list adjustment, or turn off sorting or block drops while a sort other than '#' is applied. The finding's paths were missing the src/ prefix. A Windows run would show the effect, but the code alone settles the claim.
DocRef: AUDIT-PLAN claim 23 and Stage 5 step 5 note

### HARV-69 [Medium] `@desc` and `@file` filters can throw on mods with no description or no file path (Refuted, PARTLY_KNOWN)
Location: Core/Models/DivinityModFilterData.cs:23, 65, 70; Core/Models/DivinityBaseModData.cs:146-162
Refuted because no mod in the filtered lists can have a null Description or FileName. ValueContains does lack a null guard, but every mod in Active/InactiveMods comes from ParseMetaFile (Description falls back to "") or from an override pak (Description set), and both loaders set FilePath (DivinityModDataLoader.cs:73-80, 211, 230, 364, 618, 641; DivinityModData.cs:383, 514-517). Base-game mods have no FilePath, but CanAddToLoadOrder/IsLarianMod keeps them out (MainWindowViewModel.cs:2142-2144, 5243). The voter put the severity at Low.
Fix caveats: `if (String.IsNullOrEmpty(val)) return false;` is a cheap safeguard worth bundling into perf/filter work, but it fixes nothing a user can hit today.
DocRef: CLAUDE.md section 3 (filter parsing 4178-4316); AUDIT-PLAN claim 22

### HARV-30 [Low] Archive import flattens paks, so same-name paks overwrite each other (I5) (Confirmed, KNOWN)
Location: MWVM:3424-3429
Each .pak goes to Path.Combine(AppDataModsPath, Path.GetFileName(...)), and File.Create truncates any existing file with no collision check (MainWindowViewModel.cs:3423-3429). Mods are added with no duplicate-path check (:3443-3453), so the first mod stays listed as a stale entry pointing at the overwritten file. Downgraded to Low: the Mods folder is flat, so the manager's own exports can't produce two paks with the same name. Only unusual third-party archives can.
Fix caveats: track the output names written during the import and warn on a repeat, or keep the subfolder path in the name. The uuid/version layout only helps archives the manager exported itself.
DocRef: plan Stage 3 I5; AUDIT-PLAN claim 13, AUDIT-PLAN-04

### HARV-47 [Low] The Script Extender updater writes DWrite.dll in place, so cancel or failure leaves it truncated (B12) (Plausible, PARTLY_KNOWN)
Location: MWVM:341-366; the game-running check is commented out at MWVM:5424-5425
Corrected claim: File.Create writes DWrite.dll in place with no temp file or hash (MainWindowViewModel.cs:362-363), but new ZipArchive(webStream) buffers the non-seekable HTTP stream into memory first (:352, :356). A network drop therefore fails before anything is truncated. A partial file is possible only if the user cancels, or a corrupt deflate stream fails, during a very short copy from memory, and the user then sees an error alert (:396). Lines 5424-5425 belong to the modsettings watcher, not the updater.
Fix caveats: the window is small but the fix is cheap: write to .part, then File.Move with overwrite. A game-running check adds little, since a loaded DLL would block File.Create anyway.
DocRef: CLAUDE.md section 3 and section 7; AUDIT-CLAUDE 9.2 / item 11

### HARV-54 [Low] LastExported is shared by all profiles, so the startup "restore" offers another profile's order (Plausible, NEW)
Location: MWVM:2850-2858, 2291-2327
Corrected claim: there is one Orders/LastExported.json with no profile key (MainWindowViewModel.cs:2852-2858), and CheckForEmptyOrderAsync exports it into SelectedProfile without checking which profile it came from (:2283-2327, :2553). But every load forces the "Public" profile when one exists (:2425-2428; DivinityModDataLoader.cs:901-928), and BG3 normally has only Public. Cross-profile misuse therefore needs a profile set with no profile named Public.
Fix caveats: save the profile UUID in the backup and compare it before prompting. Worth bundling with §6.4, but not Medium on its own.
DocRef: none
