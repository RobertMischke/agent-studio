# Regression Radar

Regression Radar classifies changed spec and test files so test changes do not quietly hide regressions. It is deterministic: it reads git changes and applies fixed companion-file rules, with no model in the loop.

**Intended** means a new spec was added, a spec was renamed, a spec changed alongside its companion source file, or a deleted spec has a replacement.

**At risk** means a spec changed without its companion source file changing. That can be legitimate, but it is the classic shape of assertions being adjusted to make a run green.

**Drift** means coverage moved in a suspicious direction, such as a spec deleted without a replacement. When the header says **DRIFT DETECTED**, at least one changed spec landed in that bucket.

The `+N/-N` values are lines added and removed for the changed file.

On a task, Regression Radar shows only that task's attributed commits. A task with no attributed commits shows no changes. On the project view, it shows the cross-task aggregate grouped by task where possible.
