# Adding or changing a style rule

Rules in this family are loaded into coding prompts, so each addition spends
context and changes agent behavior. Add one only when repository evidence shows
a repeated decision or regression class.

## Workflow

1. Collect evidence: a repeated review finding, measured regression, existing
   canonical implementation, architecture test, or accepted product rule.
2. Put the rule in the narrowest applicable guide. Link its detailed source
   instead of copying a whole domain document.
3. Update the guide's one-line `promptSummary`. Keep it imperative and at most
   600 characters; this exact line enters the intake artifact.
4. Update `appliesTo` only when the relevant project, technology, or task area
   changes. Keep the stable `styleGuideId` and technology slugs.
5. Add or update catalogue and intake-selector tests. A UI rule also needs
   focused component coverage and both-theme visual evidence when it changes a
   rendered surface.
6. Verify the guide is reachable from this index and from the Deck Wiki
   card for an applicable fixture project.

## Frontmatter rules

- `styleGuideId`, `title`, `version`, `summary`, `promptSummary`, and
  `appliesTo` are all required for a selectable guide. The id uses at most 64
  lowercase letters, digits, and hyphens; title is capped at 160 characters;
  version at 32; and both summaries at 600.
- `appliesTo` is an inline JSON object so the mapping is valid YAML and can be
  parsed deterministically without a broad YAML execution surface.
- `projects` and `technologies` use OR within the list. The catalogue requires
  both dimensions to match. Empty dimensions match nothing; use an explicit
  `*` for global applicability. Project selectors are stable `PROJ-NNN` ids or
  current short-code aliases, never display names. Technology selectors are
  the canonical `angular`, `dotnet`, and `csharp` keys or `*`.
- `taskAreas` must use an area already detected by the intake selector. Extend
  that selector and its tests in the same change when introducing a new area.
  `*` is the explicit wildcard and an empty list matches no task.
- A guide without valid frontmatter remains an ordinary Wiki page and is not
  injected into prompts.
- Keep the file below 32 KiB. Only the first 64 Markdown files in deterministic
  path order are inspected, and symbolic or reparse paths are excluded.

The Deck and intake normally share a cached five-minute catalogue
snapshot. During authoring, append `?refresh=true` to the style-guide catalogue
request when a bounded immediate refresh is required; confirm the returned
`snapshotId` changed before judging the result.

If the proposed rule is still a design question, compare it in an
[Experiment Workbench](../concepts/experimentier-workbench.md) first. Mandatory
guidance records a settled choice.
