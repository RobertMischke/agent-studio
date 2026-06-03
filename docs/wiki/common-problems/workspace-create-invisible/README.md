---
id: workspace-create-invisible
title: "POST creates workspace on disk but GET omits it when it has no projects"
status: open
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: minor
category: state-machine
tags: [workspace, registry, empty-workspace, visibility]
affects:
  - "Workspace registry"
  - "Workspace creation flow"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# workspace-create-invisible

**What.** A workspace create request persists data on disk, but the follow-up read does not show the workspace when it has no projects.
**Why.** The read projection filters out empty workspaces or joins only through project entries.
**Workaround.** Attach at least one project before relying on the list response, or inspect the registry file during diagnosis.
**Long-term.** Return explicit empty workspace records so create/read behavior is symmetrical.
