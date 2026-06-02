# Regression Radar

Regression Radar watches the **spec and test files** a task touched and flags the changes most likely to hide a regression. Tests are the safety net for the rest of the codebase, so when a run edits or removes them the radar asks one question for every changed spec: did this change strengthen the net, or quietly cut a hole in it? The classification is fully deterministic — it reads git history and applies fixed heuristics, with no model in the loop — so the same diff always produces the same verdict.

## Where the data comes from

The radar never guesses from prose. It reconstructs the task's own commit range by walking the run timeline: the baseline is the repository head **before** the task's first tracked run, and the head is the repository head **after** its last run. It then asks git for every file changed across that range and keeps only the spec/test files — `*.spec.*`, `*.test.*`, `*.tests.*`, and .NET `*Tests.cs`, across TypeScript, JavaScript, C#, and Python. For each spec it also resolves the likely **companion implementation** (e.g. `task.service.spec.ts` → `task.service.ts`, `FooTests.cs` → `Foo.cs`) and checks whether that companion changed in the same range. That companion signal is the heart of the classification.

## How changes are classified

Every changed spec lands in exactly one of three buckets:

- **Intended** (green) — the change reads as healthy: a brand-new spec (more coverage), a renamed spec (structural refactor), a spec modified **alongside** its companion implementation, or a deleted spec that has a replacement added in the same range.
- **At Risk** (amber) — a spec was **modified but its companion implementation did not change**. The assertions moved without the code behind them moving, which is the classic shape of a test quietly edited to go green. Worth a human glance.
- **Drift** (red) — a spec was **deleted with no replacement** in the range. Coverage silently dropped, and this is the change most likely to let a future regression through unnoticed.

The header badge reflects the **worst** category present: any drift turns it red ("Drift detected"), otherwise any at-risk turns it amber ("Review needed"), otherwise green ("All intended"). The summary row tallies how many of each you have.

## When it runs

The radar runs as a post-step in the task pipeline and is recomputed on demand whenever you open the task: the panel loads it on first view and refreshes every 30 seconds while you watch the card. Because it spans the task's full tracked run range rather than a single run, a task with many runs (or a long branch) can surface a long list even when the latest run was small — read the per-file reasons rather than reacting to the raw count.

## Reading the findings

Each row names the spec file, its git status (added / modified / deleted / renamed), its category, and the line delta. Expand a row for the full path, the **reason** the radar chose that category, and the companion implementation it paired the spec with — including whether that companion also changed. Start with anything red, then amber; green rows are there for completeness and rarely need action. A finding is a prompt to look, not a failed gate: the radar points at where a test change and its implementation may have diverged, and you decide whether that divergence is intended.
