---
id: verdict-stuck-in-auto-review
title: "Review verdict is recorded but the lane move is not completed"
status: open
first-seen: 2026-06-05T00:00:00Z
last-seen: 2026-06-05T00:00:00Z
severity: major
category: state-machine
tags: [auto-review, verdict, lane-move, stuck-card]
affects:
  - "4-auto-review"
  - "review decision orchestrator"
related-tasks: [ASS-779]
related-adrs: []
---

# Review verdict is recorded but the lane move is not completed

**What.** A review verdict exists, but the card remains in 4-auto-review for hours because the follow-up lane move did not happen.
**Why.** The verdict write and state transition can diverge when post-review processing is interrupted or a move is skipped.
**Workaround.** Check the decision journal and card log before assuming a 4-auto-review card is still pending.
**Long-term.** ASS-779 tracks completing or backfilling the missing lane move after verdict persistence.
