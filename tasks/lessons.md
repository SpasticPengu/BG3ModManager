# Lessons: BG3 Mod Manager fork

Read this at the start of every session in this project. Each entry is a mistake that happened, then the
rule that prevents it. Cross-project lessons live in the global `~/.claude/CLAUDE.md` instead.

## 2026-11-23: the plan is Markdown, not HTML

**What happened:** I extended `implementation-plan.html` because this project's `CLAUDE.md` still named it
as the roadmap, although HTML plans were retired on 2026-11-06 (global workflow framework).

**Rule:** the plan is `implementation-plan.md` at the repo root, with a "Resume here" section and a
checklist at the top. HTML is only for a mock-up or diagram that the plan links to. If any file still names
an HTML plan, convert or fix the reference; never extend the HTML.

## 2026-11-23: plain language for the owner

**What happened:** my status reports were full of developer jargon (awaiters, `SetFrom`, lists of
`file:line` references, review IDs). The owner is not a software developer. A memory already said so in
September and I still did it.

**Rule:** in chat, and in the plan's summaries ("Resume here", "Checklist", the "In short" lines), say what
a problem means for someone using the app and what we will do about it, in everyday words. Explain a
technical term the first time it is needed, or leave it out. Keep exact references, IDs and mechanisms in
the plan's technical sections and in commit messages, where they belong; don't water down the engineering
or the testing.
