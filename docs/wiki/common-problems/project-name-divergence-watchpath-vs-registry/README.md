---
id: project-name-divergence-watchpath-vs-registry
title: "WatchPath display name and registry display name diverge after rename"
status: open
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: major
category: state-machine
tags: [project-filter, registry, watchpath, naming]
affects:
  - "Project filters"
  - "Watch path display names"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# project-name-divergence-watchpath-vs-registry

**What.** The same project can appear under different names depending on whether the UI reads watch path metadata or registry metadata.
**Why.** Rename flows can update one naming source without synchronizing the other.
**Workaround.** Use the watchPath as the durable identity and treat display names as presentation data until synchronized.
**Long-term.** Centralize project display-name resolution and make filters consume the same source.
