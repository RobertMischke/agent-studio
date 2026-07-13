# Engineering style guides

This folder is the single navigation entry for repository coding guidance that
depends on a project's technology stack. The Project Hub reads the frontmatter
from this family, shows only applicable guides in Wiki Pulse, and the existing
Preparation intake selector adds those same guides to coding-task prompt
context.

The family does not replace narrower sources of truth. It points to them and
turns their most important, evidenced rules into a small technology-specific
entry surface.

## Guide family

| Guide | Applies when | Canonical sources it incorporates |
|---|---|---|
| [Angular components](angular-components.md) | Angular, TypeScript, or SCSS is detected | [UI hard rules](../design/style-guide-hard-rules.md), [component vocabulary](../frontend/style-guide/README.md), [performance playbook](../frontend/performance.md) |
| [.NET backend](dotnet-backend.md) | .NET or C# is detected | [backend structure style guide](../architecture/backend-structure/styleguide.md), [domain maps](../domains/README.md) |

The [UI hard rules](../design/style-guide-hard-rules.md) remain the
non-negotiable visual baseline. This index is the one place from which a human,
Project Hub, and prompt enrichment discover that baseline together with the
technology-specific guidance around it.

## Applicability contract

Every selectable guide has Markdown frontmatter with a stable id and one
machine-readable `appliesTo` object:

```yaml
---
styleGuideId: angular-components
title: Angular component guide
version: 1
summary: Short Project Hub description.
promptSummary: Bounded rules copied into task prompt context.
appliesTo: {"projects":["*"],"technologies":["angular","typescript","scss"],"taskAreas":["frontend"]}
---
```

- `projects` accepts `*` or a project display name.
- `technologies` uses lowercase stable slugs. V1 detects `angular`,
  `typescript`, `scss`, `dotnet`, and `csharp` from bounded repository markers.
- `taskAreas` maps the guide onto the existing intake selector. It does not add
  a second prompt builder.
- A project match and at least one technology match are required. Within each
  list, matching uses OR semantics. Empty lists match nothing; global scope
  must be explicit with `*`.

These technology slugs are deliberately suitable for reuse by the Project
Graph contract. Style-guide discovery remains self-contained when that richer
graph is unavailable.

See [Adding or changing a rule](adding-a-rule.md) before expanding the family.
Interactive alternatives or uncertain design questions belong in a
[Workbench](../concepts/experimentier-workbench.md), not in a mandatory guide.
