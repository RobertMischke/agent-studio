---
id: accept-with-concerns-waves-through-unverified
title: "Accept-with-concerns allowed unverified UI or bug work"
status: fixed
first-seen: 2026-06-05T00:00:00Z
last-seen: 2026-06-05T00:00:00Z
severity: major
category: runner
tags: [accept-with-concerns, evidence-gate, visual-evidence, tests]
affects:
  - "auto-review"
  - "UI and bug tasks"
related-tasks: [ASS-764, ASS-773]
related-adrs: []
---

# Accept-with-concerns allowed unverified UI or bug work

**What.** UI or bug work could be accepted with concerns even when visual evidence was missing or tests/builds were red.
**Why.** The review path treated concerns as non-blocking without a deterministic evidence gate for high-risk task types.
**Workaround.** Require screenshot/e2e evidence for UI tasks and green build/test evidence for bug tasks before accepting.
**Long-term.** ASS-773 added the evidence gate and fixed the deployed review path.
