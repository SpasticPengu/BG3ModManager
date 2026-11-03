# HARV-H2 verification results

Read-only review of the SpasticPengu fork of BG3 Mod Manager. The workflow computed each status from three independent verifiers.

## Summary

| Status | Count |
|---|---|
| Confirmed | 3 |
| Plausible | 0 |
| Disputed | 0 |
| Refuted | 0 |
| Unverified | 0 |

| Final severity | Count |
|---|---|
| High | 2 (HARV-61, HARV-32) |
| Medium | 1 (HARV-52, down from High) |

---

### HARV-61 [High] All three pak import paths truncate the destination in place; on cancel or error the installed mod is destroyed (Confirmed, PARTLY_KNOWN)
Location: Core/Util/DivinityFileUtils.cs:364 (via MWVM:1845 AddModFromFile); MWVM:3302 (ImportCompressedFileAsync); MWVM:3429 (ImportArchiveAsync)

All three verifiers confirmed it (two High, one Medium). Each path calls File.Create on Mods/<name>.pak, which truncates the installed pak before any bytes are copied. A cancel (the Cancel button is visible, CanCancelProgress defaults to true; MWVM:5105-5112, MainViewControl.xaml:121) or an IO error is caught and logged, and the partial file stays (DivinityFileUtils.cs:368). In ImportArchiveAsync, `success` is declared once (MWVM:3386/3396), is never reset per entry, and LoadModDataFromPakAsync still runs after a failed copy (3443).
Correction and extra finding: the compressed path is worse than the claim says. TempFile.CreateAsync drains decompressionStream at MWVM:3279/3281 (TempFile.cs:46-49), so the second copy at 3302-3307 likely writes a 0-byte pak and still reports success, cancelled or not. In that path the truncation only happens after the pak metadata has loaded, so a cancel during decompression does not truncate.
Fix caveats: write to a temp file in the same folder, parse it, then File.Move(tmp, dest, true). Reset `success` per entry, delete the temp in finally, and catch IOException when dest is locked. The compressed path must copy from tempFile.Stream after Seek(0). The rename also breaks hardlinks instead of writing through them.
DocRef: AUDIT-PLAN-04, AUDIT-PLAN-02, plan Stage 3 I2 (the AddModFromFile/DivinityFileUtils:364 path is not documented)

### HARV-32 [High] An imported order with the same name as an existing one overwrites that order's file on save (Confirmed, PARTLY_KNOWN)
Location: MWVM:3128 (AddNewModOrder), 2613-2618, 2633; DMDL:1106

All three verifiers confirmed it with high confidence (two High, one Medium). FilePath has no [DataMember] (DivinityLoadOrder.cs:23-29), so an order from an archive arrives with FilePath null and Name = the entry's file name (MWVM:3468, 3373-3381). AddNewModOrder neither checks for a duplicate nor sets a path (2037-2062, 3112-3131). The next Ctrl+S (AppKeys.cs:25) builds LoadOrderPath/Name.json (2613-2633) and writes it with FileMode.Create without asking (DMDL:1106). The user's existing Main.json is lost silently, and the two "Main" entries look the same in the list.
Scope: orders named "Current" go down their own branch, so the bug needs a non-Current name that collides plus a manual save. One verifier rated it Medium for that reason.
Fix caveats: checking names is not enough. Add one helper that returns a unique path (File.Exists, case-insensitive, sanitised name, " (2)" suffix) and use it both in AddNewModOrder/import and in SaveLoadOrderAsync whenever FilePath is empty. This also catches sanitising clashes and the default New{n} names. Rename the imported order so it stands apart in the list.
DocRef: AUDIT-PLAN-01 (covers FilePath=null and rename-if-taken, but not the silent overwrite)

### HARV-52 [Medium] Master can promote a meta-less override (UUID = pak path) into the active list and write a non-GUID UUID to modsettings.lsx (Confirmed, PARTLY_KNOWN)
Location: MWVM:3802-3803 -> DMDL:1215-1236 -> MWVM:2126-2129, 2142 -> DMDL:1478-1482

All three verifiers agreed the chain is real (two high, one medium confidence). Severity goes from High to Medium because it needs a non-default .json export plus a re-import, and the damage can be undone. Meta-less override paks get UUID=pakPath and IsForceLoaded (DMDL:604-639). The JSON text export includes ForceLoadedMods (MWVM:3802-3803). Import looks them up by path. A JSON array is not a valid DivinityLoadOrder, so Newtonsoft returns null on the handled error and the fallback runs (DivinityJsonUtils.cs:66-72; likely, not tested). LoadModOrder force-allows the mod without checking HasMetadata (MWVM:2126-2129, 2142). Export then writes the path, unescaped, into a type="guid" attribute (DMDL:1478-1482, DivinityApp.cs:33), and LSLib's new Guid(str) throws when it reads it (NodeAttribute.cs:440-443).
Corrections: only a .json export triggers it, because .tsv/.txt skip Override lines. A friend's JSON triggers it only if the exact pak path matches; otherwise the entry is marked Missing. Folder is builtinModOverrides.FirstOrDefault().Key, not always "Gustav". The UUID is not XML-escaped, so a path containing & corrupts the XML.
Fix caveats: force-allow in LoadModOrder only on the toggle's condition (IsForceLoaded && HasMetadata && !IsForceLoadedMergedMod). Skip and log non-GUID UUIDs (Guid.TryParse) in GenerateModSettingsFile/BuildOutputList, and escape Folder/UUID with SecurityElement.Escape. Reading the file back is optional hardening. Test T-B1 still has to confirm the Newtonsoft null step.
DocRef: CLAUDE.md section 6 (Vocabulary, Loader); AUDIT-PLAN table row 33
