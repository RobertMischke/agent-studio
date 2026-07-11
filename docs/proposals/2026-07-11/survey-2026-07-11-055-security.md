---
id: "survey-2026-07-11-055-security"
generation: "2026-07-11"
finding: "Project Hub, Security, desktop, light: three solid black summary blocks render with no legible content."
evidenceScreenshot: "2026-07-11/assets/055-security.png"
proposal: "Inspect the summary-card or canvas rendering path before assuming a text-contrast bug, then verify its fill and content rendering in both themes."
estimatedEffort: "medium"
severity: "critical"
status: "proposed"
spawnedTask: null
---

# Inspect the summary-card or canvas rendering path before assuming a text-contrast bug, then verify its fill and content rendering in both themes.

## Finding

Project Hub, Security, desktop, light: three solid black summary blocks render with no legible content.

## Evidence

![Security](./assets/055-security.png)

Source capture: `project-hub--security--desktop--light--real.png`

## Proposal

Inspect the summary-card or canvas rendering path before assuming a text-contrast bug, then verify its fill and content rendering in both themes.

Estimated effort: **medium**  
Severity: **critical**
