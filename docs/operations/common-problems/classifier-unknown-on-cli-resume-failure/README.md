---
id: classifier-unknown-on-cli-resume-failure
title: "Codex resume rejection is routed as classifier-unknown"
status: fixed
first-seen: 2026-06-05T00:00:00Z
last-seen: 2026-06-05T00:00:00Z
severity: major
category: cli
tags: [codex, resume, classifier-unknown, exit-2]
affects:
  - "codex exec resume"
  - "runner outcome classification"
related-tasks: [ASS-775]
related-adrs: []
---

# Codex resume rejection is routed as classifier-unknown

**What.** codex exec resume rejects a resume attempt with exit code 2, but the runner surfaces a terminal classifier-unknown instead of recovery guidance.
**Why.** The resume failure was not classified as a known recoverable Codex session problem.
**Workaround.** Treat exit 2 from Codex resume as a resume/session failure and start a recovery path instead of accepting classifier-unknown at face value.
**Long-term.** ASS-775 fixed and deployed the classifier path.
