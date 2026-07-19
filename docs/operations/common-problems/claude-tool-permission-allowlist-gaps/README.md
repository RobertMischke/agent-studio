---
id: claude-tool-permission-allowlist-gaps
title: "Claude Code tool call denied because permission is not in the allowlist"
status: open
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: major
category: cli
tags: [claude, permission, allowlist, tool-call]
affects:
  - "Claude CLI managed task runs"
  - "Tasks that need file or shell operations outside the granted tool set"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# claude-tool-permission-allowlist-gaps

**What.** Claude reports that a tool permission was denied and it could not request permission from the user.
**Why.** The run is operating under an allowlist that does not include the required tool or path, and the managed execution context has no interactive approval path.
**Workaround.** Reissue with a permitted approach, adjust the managed permission profile, or route the work to a CLI/configuration that has the required capability.
**Long-term.** Make permission gaps visible as typed outcome issues with a specific remediation hint instead of a generic blocked run.
