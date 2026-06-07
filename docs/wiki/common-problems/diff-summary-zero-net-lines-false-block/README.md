---
id: diff-summary-zero-net-lines-false-block
title: "Zero-net-line diff summary caused a false code-quality block"
status: fixed
first-seen: 2026-06-05T00:00:00Z
last-seen: 2026-06-05T00:00:00Z
severity: major
category: runner
tags: [diff-summary, aspect-review, code-quality, reissue-loop]
affects:
  - "aspect review"
  - "auto-review reissue policy"
related-tasks: [ASS-770, ASS-778]
related-adrs: []
---

# Zero-net-line diff summary caused a false code-quality block

**What.** Aspect review saw a +0/-0 diff summary and treated the entire implementation as a code-quality block, causing a false reissue loop.
**Why.** The diff summary collapsed to zero net lines even though the task history needed richer evidence about changed files and commits.
**Workaround.** Inspect commit/file evidence before treating +0/-0 as empty work.
**Long-term.** ASS-778 fixed the deployed path so zero-net summaries do not block complete work on their own.
