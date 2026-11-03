# BLD verification results

Read-only review of the SpasticPengu fork of BG3 Mod Manager. Statuses and final severities were computed by the workflow from independent verifiers. Nothing below was re-verified.

## Summary

| Status | Count |
|---|---|
| Confirmed | 7 |
| Plausible | 1 |
| Disputed | 0 |
| Refuted | 0 |
| Unverified | 0 |
| **Total** | **8** |

| Final severity | Count | Items |
|---|---|---|
| Medium | 6 | BLD-01, BLD-02, BLD-05, BLD-06, BLD-07, BLD-08 |
| Low | 2 | BLD-04, BLD-03 |

Severity changes: BLD-01 High -> Medium, BLD-02 High -> Medium, BLD-03 Medium -> Low, BLD-04 Medium -> Low.

---

### BLD-01 [Medium] The whole solution reaches end of support in 7 days (.NET 8 EOL, 2026-11-10) (Confirmed, NEW)
Location: src/GUI/GUI.csproj:5, src/Core/DivinityModManagerCore.csproj:3, src/Toolbox/Toolbox.csproj:4, External/lslib/LSLib/LSLib.csproj:3, External/lslib/LSLibNative/LSLibNative.vcxproj:26, External/lslib/LSLibStats/LSLibStats.csproj:3; README.md:11
- All six projects target net8.0*, no csproj sets SelfContained or RollForward, and README.md:11 pins runtime-desktop-8.0.15. .NET 8 support ends 2026-11-10. The three verifiers agree the claim holds but lowered it from High: nothing breaks on that date. Builds keep running and just stop getting security patches.
- Corrected statement: the update-failure path is misattributed. MainWindowViewModel.cs:4912 only sets InstalledVersion. The check runs at AppUpdateWindowViewModel.cs:113 against upstream's feed (DivinityApp.cs:22, URL_UPDATE=LaughingLeader/master/Update.xml). So a fork net10 build only reaches users once URL_UPDATE is repointed (upstream wt-avalonia is already on net10.0).
- Fix caveats: the evergreen /8.0 README link can land now. Do the net10 move together with the URL_UPDATE repoint (plan §416-424) and a changelog note that .NET 10 is required. Do not use RollForward=LatestMajor as a stopgap, because C++/CLI compatibility with it is untested. Only the exes (GUI, Toolbox) decide which runtime loads.

### BLD-02 [Medium] The owner's toolchain cannot officially target net10, and the C++/CLI project raises the bar further (Confirmed, PARTLY_KNOWN)
Location: Owner machine (CLAUDE.md §2.1: VS Build Tools 2022 17.14.41, MSVC v143, SDK 8.0.425 + 10.0.400); LSLibNative.vcxproj:36,42,48 (PlatformToolset v143)
- Microsoft's versioning table says net10.0 is officially supported only on VS 18.0+. In dotnet/sdk, minimumMSBuildVersion is 17.14.0 for 10.0.1xx and 18.0.0 for 10.0.2xx-4xx, so 10.0.400 needs MSBuild 18. Two verifiers lowered this to Medium: it fails at the first build, puts no data at risk, has a free fix and affects no end user. The plan stays on net8 for now (implementation-plan.txt:10,72).
- Corrected failure mode: with no global.json, the 17.14 resolver quietly falls back to the newest SDK it can load (NETCoreSdkResolver.cs:163-168), which is why today's builds work. A net10 retarget then fails with NETSDK1045. With global.json pinned to 10.x, it fails with MSBuildSmallerThanMinimumVersion instead. Whether net10 C++/CLI needs v145 is inferred, not measured.
- Fix caveats: install Build Tools 18.x side by side with v143 plus its C++/CLI component, so the vcxproj builds unchanged. Under BLD-06 option A (LSLibNative stays net8), v145 is not strictly needed. A Windows run (`msbuild -getProperty:NETCoreSdkVersion`, plus one v143 net10 build) settles it.
- docRef: CLAUDE.md §2.1 (toolchain table, SDK 10.0.400 line 83)

### BLD-05 [Medium] LSLibStats is built in every configuration, referenced by nothing, and carries a second copy of the first-build parser trap (Confirmed, PARTLY_KNOWN)
Location: BG3ModManager.sln:24, 145-164; External/lslib/LSLibStats/LSLibStats.csproj:8-31
- Confirmed: LSLibStats has Build.0 in all ten configurations (BG3ModManager.sln:22,146-164). Its only reference is StatParser/StatParser.csproj:14, and StatParser is not in the sln. Its parsers depend on generated .lex.cs/.yy.cs files that are gitignored (External/lslib/.gitignore:65-67), so on a clean clone the first build fails the same way LSLib's does. CLAUDE.md:150-162 only covers LSLib's four files.
- Corrected statement: the 10 GPLex/GPPG Execs have !Exists guards (LSLibStats.csproj:20-33), so they run only on the first build, not on every build.
- Fix caveats: remove its Build.0 lines (or the project) from the sln, or extend the glob fix to cover its 10 generated files.
- docRef: CLAUDE.md §2.2; plan §0.2a; AUDIT-CLAUDE 2.2m

### BLD-06 [Medium] LSLibNative (C++/CLI) is the one hard blocker for dotnet build, a cheap CI, Linux, and dropping the VC++ redistributable, and upstream already replaced it (Confirmed, PARTLY_KNOWN)
Location: src/Core/DivinityModManagerCore.csproj:20; LSLib.csproj:44; native call sites LSLib/LS/Compression.cs:98,200, LS/PackageReader.cs:213 (LZ4 frame), VirtualTextures/Compression.cs:54,146, VirtualTextures/VirtualTexture.cs:354 (FastLZ), Granny/GR2/Reader.cs:256-409; README.md:11
- The code matches. Core and LSLib both reference LSLibNative.vcxproj. The mod manager's native path is LZ4 frame, used for chunked and solid paks (Compression.cs:98,104,200; PackageReader.cs:213). FastLZ is only in VirtualTextures and Granny2Compressor only in GR2. Non-chunked LZ4 already goes through managed lz4net. Upstream avalonia uses LSLibNet: net10.0, K4os LZ4/LZ4.Streams 1.3.8, DotFastLZ and YaccLexTools 1.2.3 (wt-avalonia/src/Core/Core.csproj:21; LSLibNet/Directory.Packages.props:6-13). This is platform and build debt, not a user-facing bug.
- Fix caveats: replace LZ4FrameCompressor with K4os frame streams, remove or swap the FastLZ and Granny2 users, then drop both ProjectReferences. Check byte for byte against chunked and solid LZ4 paks. Performance neutrality is still unmeasured.
- docRef: CLAUDE.md §2.1, §8 (setup-msbuild required); AUDIT-CLAUDE 2.1d, Gap 15

### BLD-07 [Medium] Line endings: the repo stores LF, CLAUDE.md says "CRLF", and there are no text/eol rules (Confirmed, KNOWN)
Location: .gitattributes (21 merge=ours lines only); .editorconfig:15,251 (end_of_line = crlf); CLAUDE.md §5
- Every fact checks out. .gitattributes:1-21 has only merge=ours lines. .editorconfig sets crlf at lines 15 and 251, and notes/CLAUDE.md:436 says "CRLF". `git ls-files --eol src` gives 188 LF, 9 CRLF and 11 mixed files. The sln and csprojs are CRLF, while MainWindowViewModel.cs (5,448 lines) and the wt-perf .cs files are LF. This is a real trap for Linux agents, and the .editorconfig rule makes it worse by pushing editors and dotnet format to write CRLF.
- Fix caveats: `* text=auto` will make the files already stored as CRLF or mixed (sln, csprojs, DivinityModDataLoader.cs) show as modified, or get normalised the next time they are staged. Mark them -text or accept the churn. The .editorconfig end_of_line=crlf rule needs fixing too, not just CLAUDE.md.
- docRef: AUDIT-CLAUDE 5.3 and Gap 5 (contradicting CLAUDE.md §5 'CRLF')

### BLD-08 [Medium] The release zip is built from an uncleaned bin\Publish with error-swallowing deletes and no check of its contents (Confirmed, KNOWN)
Location: BuildRelease.py:33-41, 55-60; GUI.csproj:92-102 (only _Lib cleaned by pre-build); GUI.csproj:71-76
- Confirmed: the Publish build runs BuildRelease.py (GUI.csproj:104-109), which zips all of bin/Publish. Its deletes swallow every error (BuildRelease.py:26-41,55-60) and nothing checks the zip's contents. A stray 'debug' file next to the exe turns logging on (MainWindow.xaml.cs:380-384).
- Corrected statement: stale root DLLs are already moved into _Lib (SetupLibFolder) and wiped by the next pre-build. So leftovers only survive for non-DLL files: Resources/*.json, Tools/, marker files and stale subfolders. The GUI.csproj line numbers should be 73-78 (icons) and 81-101 (pre-build/SetupLibFolder).
- Fix caveats: build releases from a clean tree or in CI, and add a post-zip content check that fails loudly.
- docRef: HARV-05; AUDIT-CLAUDE 7.4

### BLD-04 [Low] Publish|Any CPU / PublishTest|Any CPU builds LSLib, LSLibNative and LSLibStats in Debug, so a release can ship a debug-CRT native DLL (Confirmed, PARTLY_KNOWN)
Location: BG3ModManager.sln:113-114, 117-118 (LSLib -> Debug|Any CPU), :133-134, 137-138 (LSLibNative -> Debug|x64), :153-154, 157-158 (LSLibStats -> Debug); LSLibNative.vcxproj:35 (UseDebugLibraries=true for Debug)
- The mapping is real (BG3ModManager.sln:113-118,133-138,153-158; LSLibNative.vcxproj:33-35 uses the debug CRT). With Any CPU, GUI still builds Publish|x64 and RunBuildRelease makes the zip (GUI.csproj:81-104). Lowered to Low because it only bites if someone picks Any CPU, and no CI or script does (there are no .github/workflows). It is a latent trap.
- Correction: the native call is in Compression.cs:98, which LSFReader.cs:312 reaches through CompressionHelpers.Decompress. It is not in LSFReader.cs:312 itself.
- Fix caveats: map the Publish/PublishTest|Any CPU rows to Release (Release|x64 for LSLibNative and LSLib), or delete the Any CPU solution platforms. The CI part of the fix is moot because there is no CI.
- docRef: CLAUDE.md §2.2; AUDIT-CLAUDE 2.2e/2.2f

### BLD-03 [Low] No global.json, so the SDK is chosen by whatever happens to be installed (Plausible, NEW)
Location: Repo root (absent). Owner: SDK 8.0.425 and 10.0.400; windows-latest runner (VS 18.11) has SDKs 8.0.133/206/319/427, 9.0.x and 10.0.114/204/303/403
- Neither master nor wt-perf has a global.json. But .github holds only ISSUE_TEMPLATE (notes/CLAUDE.md:671 confirms there are no workflows), so CI-vs-owner drift is a planned risk, not a current one. Every TFM is net8 (GUI.csproj:5), so the .NET 10 behaviours are dormant. The terminal logger is a dotnet-CLI default and does not apply to msbuild.exe. One verifier, medium confidence, so this stays Plausible.
- Corrected statement: today it only decides which SDK the owner's MSBuild 17.14 picks between 8.0.425 and 10.0.400. A Windows `dotnet --version` / `msbuild -v:diag` run would settle it. The drift becomes real once CI exists.
- Fix caveats: pinning 8.0.425 with rollForward latestPatch is sound, and the runner's 8.0.427 satisfies it. Land it with the first CI workflow, and point setup-dotnet at global-json-file.
