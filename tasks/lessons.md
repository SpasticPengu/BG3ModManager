# Lessons: BG3 Mod Manager fork

Read this at the start of every session in this project. Each entry is a mistake that happened, then the
rule that prevents it. Cross-project lessons live in the global `~/.claude/CLAUDE.md` instead.

## 2026-11-23: the plan is Markdown, not HTML

**What happened:** I extended `implementation-plan.html` because this project's `CLAUDE.md` still named it
as the roadmap, although HTML plans were retired on 2026-11-06 (global workflow framework).

**Rule:** the plan is `implementation-plan.md` at the repo root, with a "Resume here" section and a
checklist at the top. HTML is only for a mock-up or diagram that the plan links to. If any file still names
an HTML plan, convert or fix the reference; never extend the HTML.

## 2026-11-23: the Edit tool rewrites a mixed-line-ending file whole

**What happened:** on `fix/dependency-cycle` one small Edit to `DivinityModDataLoader.cs` turned into a
1,072-line diff. The file is stored with mixed line endings (`git ls-files --eol` shows `i/mixed`), and the
Edit tool normalised every line. An upstream PR like that is unreviewable.

**Rule:** before editing a file, check `git ls-files --eol <file>`. For an `i/mixed` file (11 on `master`,
listed by `git ls-files --eol | grep i/mixed`), splice the change byte-exactly with a script that keeps the
surrounding line endings, and always read `git diff --stat` before building or committing. Endings can change
even inside one function (`ExportModSettingsToFileAsync`: two CRLF lines, then LF): `stageF-harness\splice.py`
then reports "not found", and `splice2.py` (any ending per line, unchanged leading lines kept byte-exact) works.

## 2026-11-23: a backup name can destroy the user's own backup

**What happened:** my first design for fix 6 kept the previous `modsettings.lsx` as `modsettings.lsx.bak`.
The owner already keeps a hand-made `modsettings.lsx.bak` (934 mods) in that folder, plus `.bak-before-asl`
and `.bak-buildmod`; the first export would have overwritten it. A tracing agent caught it before any run.

**Rule:** before the app writes any new file next to the user's files (a backup, a temp file, a marker), list
that folder on the owner's machine and pick a name the app owns (`modsettings.lsx.bg3mm.bak`). Never assume
a generic name such as `.bak` or `.tmp` is free.

## 2026-11-23: an app launch the owner approved "once or twice" took eight

**What happened:** for Stage F's second real-app run I asked to launch the app "once (maybe twice)". The UI
Automation script needed four fixes on first contact with the real app (owned dialogs live in the main window's
tree, order items have no UIA name, a dialog button ignores Invoke while inactive, `Write-Output` inside a function
pollutes its return value), and one launch ran with a helper missing because its Edit had failed in the same batch.
Eight launches in all, each a short window on the owner's screen.

**Rule:** when asking for an app launch, say "a few short launches" unless the script has already run against this
app; before each launch, read back every edit the launch depends on (a failed Edit does not stop the next call in
the batch); and say the real count afterwards.

## 2026-11-23: plain language for the owner

**What happened:** my status reports were full of developer jargon (awaiters, `SetFrom`, lists of
`file:line` references, review IDs). The owner is not a software developer. A memory already said so in
September and I still did it.

**Rule:** in chat, and in the plan's summaries ("Resume here", "Checklist", the "In short" lines), say what
a problem means for someone using the app and what we will do about it, in everyday words. Explain a
technical term the first time it is needed, or leave it out. Keep exact references, IDs and mechanisms in
the plan's technical sections and in commit messages, where they belong; don't water down the engineering
or the testing.
