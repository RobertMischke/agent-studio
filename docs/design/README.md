# Design

Product-wide, prompt-known design hard rules. These are the rules that every
coding card touching the UI is expected to already know, so they live at the top
level and are referenced from `AGENTS.md`.

The single cross-technology navigation entry is the
[engineering style-guide family](../quality/README.md). This folder owns the
short visual baseline that the Angular guide incorporates.

| Page | What lives there |
|---|---|
| [style-guide-hard-rules.md](style-guide-hard-rules.md) | The hard, non-negotiable design rules (no left accent bars, full-bleed views, aggregate = sum of visible children, acute-only signals, both themes). |
| [app-survey-2026-07-11.html](app-survey-2026-07-11.html) | Self-contained, screenshot-by-screenshot visual findings for the 2026-07-11 Stable application sweep. |
| [angular-performance-report-2026-07.html](angular-performance-report-2026-07.html) | Measured Angular 21 performance and best-practice review, focused on the 123-card Human Review lane observed on Stable. |
| [tree-indicator-exploration-2026-07.html](tree-indicator-exploration-2026-07.html) | Interactive light/dark Explorer mockup comparing eight project-level state indicators. |

The concrete component vocabulary (tokens, primitives, canonical components) and
the "why" behind the shell look live under
[frontend/design-system.md](../frontend/design-system.md),
[frontend/style-guide/](../frontend/style-guide/README.md), and
[product/design-principles.md](../product/design-principles.md). This folder is
only the short, hard-rule layer above them.
