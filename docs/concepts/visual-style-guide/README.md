# Visual Style Guide Workbench

This folder is the working area behind the
[Visual Style Guide](../visual-style-guide.html). The upper page records the
current visual language as rendered specimens. This folder holds focused
comparisons, vNext candidates, and the decision trail that turns a visual idea
into a bounded implementation card.

## Workbench map

| Page | Question it answers | Current status |
|---|---|---|
| [Visual Style Guide](../visual-style-guide.html) | What does the product look like today, which tokens create that look, and how do both themes behave? | Living inventory |
| [Empty states](empty-states.html) | How much guidance and action belongs in an empty surface at each density? | Comparing |
| [Mini indicators](mini-indicators.html) | Which compact signals communicate a current situation without making history loud? | Comparing |
| [Runner cards](runner-cards.html) | How should local and remote execution identity appear on active task cards? | Comparing |
| [vNext](vnext.md) | Which modernization candidates are being framed, compared, decided, or sliced into delivery cards? | Candidate register |

Each focused HTML page is self-contained, works without a backend or network,
and includes light and dark tokens. A future implementation card for one of
these patterns must link to its focused page and record the chosen option in
its acceptance criteria.

## Source hierarchy

The Workbench is an observation and decision surface. It does not replace the
implementation sources of truth:

1. Raw palette and shadow values live in
   [`frontend/src/styles/_tokens-primitives.scss`](../../../frontend/src/styles/_tokens-primitives.scss).
2. Theme-aware meanings live in
   [`frontend/src/styles/_tokens-semantic.scss`](../../../frontend/src/styles/_tokens-semantic.scss).
3. Canonical components and mixins live in the
   [frontend component style guide](../../quality/frontend/style-guide/README.md).
4. Non-negotiable constraints live in the
   [design hard rules](../../quality/design/style-guide-hard-rules.md).
5. Architecture diagrams follow the
   [Agent Host Protocol diagram recipe](../../app/prompts/diagramm-stil-agent-host-protocol.md).

If a Workbench decision changes a token or component contract, update the
appropriate source above in the implementation slice. Do not make this folder
a competing token registry.

## Tone of Voice twin

**AGT-2237, the Tone of Voice Style Guide, is the textual twin of this visual
Workbench.** The relation is registered in the Wiki companion metadata for this
family, so the Wiki can route from this page to the task. Use the pair together:

- this Workbench owns visual hierarchy, density, color, containment, state and
  motion;
- AGT-2237 owns labels, voice, vocabulary, guidance, errors and action wording;
- patterns that combine both, such as empty states, Welcome, error banners and
  runner status, need a joint review before implementation.

The visual guide may use placeholder copy to compare hierarchy. That copy is
not approved product wording until it is checked against AGT-2237.

## Adding a comparison

1. Add the current production treatment as option `Current`.
2. Add two to four bounded alternatives that answer one named design question.
3. Render the same realistic states in light and dark.
4. Name every semantic token the pattern reads.
5. Test the hard rules, especially no left status bars, acute-only signals,
   honest aggregates, reduced motion and both themes.
6. Record the decision in [vNext](vnext.md) before creating an implementation
   card.
7. Put the selected option and Workbench link into the card's acceptance
   criteria.

## Decision states

| State | Meaning |
|---|---|
| `framing` | Problem and evidence are still being gathered. |
| `comparing` | Bounded variants exist and can be reviewed side by side. |
| `decided` | One variant and its acceptance boundary are recorded. |
| `sliced` | A delivery card references the decision and owns implementation. |
| `shipped` | Product and Workbench inventory agree. |
| `parked` | Candidate is intentionally not proceeding; rationale remains. |

## Living knowledge log

- **2026-07-23:** Created the visual Style Guide as a Wiki Workbench with a
  rendered current-state inventory, focused comparison pages, a vNext register,
  explicit AGT-2237 pairing, and permanent light/dark evidence.

