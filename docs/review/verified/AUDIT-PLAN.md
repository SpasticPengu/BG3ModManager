# AUDIT-PLAN: verified results

Read-only review of the SpasticPengu fork of BG3 Mod Manager. The statuses come from independent verifiers; nothing in this report was re-verified.

## Summary

| Status | Count |
|---|---|
| Confirmed | 6 |
| Plausible | 2 |
| Disputed | 0 |
| Refuted | 0 |
| Unverified | 0 |
| **Total** | **8** |

| Final severity | Count | Items |
|---|---|---|
| High | 2 | 01, 02 |
| Medium | 5 | 03, 04, 07, 06, 08 |
| Low | 1 | 05 |

Severity changes: 03 went from High to Medium, and 05 went from Medium to Low.

---

### AUDIT-PLAN-01 [High] Importing a "Current" order flips the live Current order to non-modsettings, or overwrites a saved order (Confirmed, KNOWN)
Location: MWVM:3110-3127 (ImportOrderFromArchive), src/Core/Extensions/ModelExtensions.cs:29-42 (SetFrom<T>), src/Core/Models/DivinityLoadOrder.cs:24-43

All three verifiers confirmed with high confidence. Export writes the order as Current.json (MWVM:3582), and import takes the order's name from the filename (MWVM:3468, 3511-3515), so a shared current order arrives named "Current". Because the class is [DataContract], only Order is restored and IsModSettings comes back false. SetFrom copies every non-null value (ModelExtensions.cs:29-42), so it overwrites IsModSettings with false but keeps FilePath=modsettings.lsx (MWVM:1766-1771). Save (MWVM:2622-2633, DMDL:1098-1110) then writes JSON over modsettings.lsx, and First(x=>x.IsModSettings) at MWVM:2967 throws. In branch B, the selected saved order becomes a clone of Current and the import is dropped. The .json on disk is untouched, so that loss is in memory only.

Fix caveats: for Current, use FirstOrDefault(IsModSettings).SetOrder(order) and then LoadModOrder. Otherwise, add the import as a new saved order with a unique name. Drop the [IgnoreSetFrom] idea, because SetFrom never reads that attribute. Guard against a null SelectedModOrder. IsBaseLoadOrder (MWVM:5194) hides the flip in the UI.

docRef: HARV-31

### AUDIT-PLAN-02 [High] Importing a .bz2/.xz/.zst pak writes an empty file over the target (Confirmed, PARTLY_KNOWN)
Location: MWVM:3272-3326 (ImportCompressedFileAsync), src/Core/Util/TempFile.cs:33-49

Confirmed by all three verifiers. One rated it Medium because these formats are rare; the other two rated it High. TempFile.CreateAsync reads the decompression stream to the end (TempFile.cs:46-49). File.Create then truncates the target (MWVM:3302), and the second CopyToAsync copies 0 bytes (3306). The code still sets success=true and calls AddImportedMod (3307, 3316-3324). The Import dialog and drag-drop accept these formats (MWVM:1999, 2005, 2015). Correction: any existing file at the final path is truncated, not only <Folder>.pak.

Fix caveats: the proposed Seek(0) fix does not work. lslib maps the temp file with leaveOpen=false (PackageReader.cs:61-72), so `using var pak` closes the handle, and DeleteOnClose then deletes the temp file. Instead, decompress to <target>.pak.tmp, load the metadata from that path, call File.Move(overwrite: true) on success, and otherwise delete the tmp.

docRef: HARV-61; HARV-27

### AUDIT-PLAN-03 [Medium] Export cancellation races an abandoned writer and leaves a corrupt zip (Confirmed, PARTLY_KNOWN)
Location: MWVM:3577-3650, 3663-3694

Confirmed by all three verifiers and downgraded from High to Medium by two of them. On cancel, Task.Delay(0, token) throws (MWVM:3599/3623). The using blocks then dispose ZipWriter, which writes the central directory and EOCD (ZipWriter.cs:50-61) while the StartNew child is still writing to the same FileStream. AttachedToParent has no effect under Task.Run's DenyChildAttach. The in-flight entry is never added to the entries list (:504). The child's exception is swallowed (3679), and the catch shows a full stack trace (3640-3647). Users can reach this: canStartExport is always true (MWVM:5006), and Cancel is visible (MainViewControl.xaml:121-123). Source mods are never touched, so the harm is a corrupt zip and a confusing error, not data loss.

Fix caveats: File.OpenWrite (3579) does not truncate, so even a successful shorter export can leave the old file's tail. Write to <out>.tmp with FileMode.Create and finish with File.Move(overwrite). Handle OperationCanceledException with a neutral message, and send the `return false` at 3595 through the same path. Wrap File.OpenRead, because the code calls Write(string, string) at 3677. Directory.Delete at 3653 is not recursive and sits outside the try.

docRef: plan Stage 3 E3/E4; HARV-22; HARV-23

### AUDIT-PLAN-04 [Medium] Archive import clobbers existing paks and leaves truncated paks behind (Confirmed, KNOWN)
Location: MWVM:3424-3440

Confirmed with high confidence. File.Create(outputFilePath) in the Mods folder truncates any pak with the same name immediately. There is no existence check, no prompt and no temp file. On error or cancel (MWVM:5106-5111), the catch only logs, and the partial file stays. Callers are at MWVM:1871 and 3082. A second problem: `success` is never reset for each entry (3442), so a failed pak is still loaded after an earlier one succeeded. The plain .pak copy at 1840-1845 also overwrites without asking.

Fix caveats: write to a .tmp file, validate it, File.Move(overwrite), and delete the tmp on failure. Reset `success` for each entry. Apply the overwrite policy to the plain .pak path at 1845 too.

docRef: HARV-61; plan Stage 3 I5

### AUDIT-PLAN-07 [Medium] DivinityModData.Files retains every path of every pak and is never read (memory) (Confirmed, NEW)
Location: DMDL:478, 488, 628; src/Core/Models/DivinityModData.cs:234

Confirmed with high confidence. Every pak load fills a HashSet with all its entry names and assigns it to Files (DMDL:478, 488, 628). A grep for \bFiles\b finds no reads. It has no XAML binding and no serialization, because DivinityBaseModData.cs:15 is an opt-in [DataContract] and Files has no DataMember. wt-perf has the same code. How much memory it costs has not been measured.

Fix caveats: removing the lines and the property changes no behaviour. To size the saving, measure private bytes before and after, or count the entries across the 1,097 paks.

### AUDIT-PLAN-06 [Medium] Pak loading is effectively serial despite Task.WhenAll (Stage 4 candidate) (Plausible, PARTLY_KNOWN)
Location: DMDL:777-784, 689-711, 466-560

One verifier, medium confidence. PackageReader.Read and the entry loop run synchronously before the first await (DMDL:466-518, 597, 689-693). Uncompressed and Zlib reads finish synchronously, and only LZ4's base ReadAsync moves work to the pool (Compression.cs:140-150). The caller runs under TaskpoolScheduler (MWVM:1665, 2375), so the load is serial but does not block the UI. The 9.27 s step covers more than pak loading, so the speedup needs profiling.

Fix caveats: use Parallel.ForEachAsync or Task.Run for each pak. Line 786 already logs pak-load time. Check that DivinityApp.Log is thread-safe, and note that the static _AllPaksNames is never cleared.

docRef: plan Stage 4.2 candidates; plan-overview (9.3 s mod load)

### AUDIT-PLAN-08 [Medium] The builtin-mod load builds a full VFS over the Data paks to find about 30 base mods (Stage 4 candidate) (Plausible, PARTLY_KNOWN)
Location: DMDL:1783-1820; External/lslib/LSLib/LS/VFS.cs:173-215

One verifier, medium confidence. The claim is partly true. AttachGameDirectory reads the file table of every Data and Localization pak not on the blacklist, and Game, Gustav, Shared and the language paks are not on it. FinishBuild then calls TryAddFile for every entry (DMDL:1783-1803; VFS.cs:93-115, 173-215). Every startup reaches this. Corrected statement: the 30 s token is never checked during the VFS build, and metadataOnly cannot help, because it returns before the file list is read (PackageReader.cs:157). How much time this takes has not been measured.

Fix caveats: a real fix has to read the file table, filter it for Mods/*/meta.lsx, and replace ModPathVisitor.DiscoverMods. Measure this step on its own in Stage 4.1 first.

docRef: CLAUDE.md LoadBuiltinModsAsync line 543; plan Stage 4.2

### AUDIT-PLAN-05 [Low] The E2 "collect failures and continue" fix would still ship truncated paks (Confirmed, PARTLY_KNOWN)
Location: MWVM:3677 -> SharpCompress Writers/Zip/ZipWriter.cs:83-86 and the ZipWritingStream.Dispose path (around 405-470)

Confirmed with high confidence and downgraded from Medium to Low. ZipWriter.Write wraps TransferTo in `using var output`. If a read throws in the middle of the copy, Dispose writes the partial CRC and sizes (ZipWriter.cs:399-447), which leaves a valid zip with a truncated pak. Corrected statement: after the E2 fix, the exporter does see an alert naming the mod, but the receiver cannot detect the truncation. Read errors after a successful open are rare on local disks.

Fix caveats: an exception after WriteToStream has started should abort the export and delete the temp/output file. Alternatively, compare the bytes written against the source length.

docRef: plan Stage 3 E2; HARV-21
