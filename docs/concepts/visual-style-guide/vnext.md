# Visual Style Guide vNext

This register holds visual modernization candidates before implementation
cards are created. It is deliberately smaller than a roadmap: one candidate,
one problem, bounded alternatives, one decision, and one acceptance boundary at
a time.

Return to the [Visual Style Guide](../visual-style-guide.html) for the rendered
current language and to the [Workbench index](README.md) for focused
comparisons.

## Candidate register

| Candidate | Surface | State | Focused workbench | Delivery card |
|---|---|---|---|---|
| VSG-001 | Welcome panel | `framing` | To be added after the initial problem review | Not cut |

## Welcome panel

**Candidate:** VSG-001  
**State:** `framing`  
**Current implementation:**
[`studio-shell.component.html`](../../../frontend/src/app/features/studio-shell/studio-shell.component.html)
and
[`studio-shell.component.scss`](../../../frontend/src/app/features/studio-shell/studio-shell.component.scss)  
**Textual twin:** AGT-2237, Tone of Voice Style Guide

### Observed current treatment

When no tab is open, the editor centers an idle visualization above a generic
elevated card. The card repeats the product name, explains that no tab is open,
lists project buttons, and offers `New task` and `Open project chat`.

The implementation is usable and token-aware, but the composition is visually
generic:

- the product title consumes the strongest hierarchy without adding new
  operator information;
- project selection repeats navigation already available in the Explorer;
- the card does not help a returning operator resume recent or interrupted
  work;
- the idle visualization and the utility card read as two unrelated empty-state
  concepts;
- the copy and information hierarchy need a joint decision with AGT-2237.

### Design question

What should the editor do for an operator who has intentionally closed every
tab: provide the fastest useful return path, provide a calm empty canvas, or
teach the product?

This question must be answered before details such as illustration, shortcuts,
recent items, or metrics are added.

### Bounded candidates for the next comparison

| Option | Primary purpose | Composition | Main risk |
|---|---|---|---|
| A. Resume | Return to current work | Recent task, last board, one primary action | Recency data may be stale or unavailable |
| B. Intent launcher | Start a deliberate action | New task, open board, project chat as three equal paths | Can become a generic command palette in card form |
| C. Calm canvas | Keep the editor quiet | Small identity mark, keyboard hint, no project grid | Discoverability is lower for first-time operators |
| D. Guided first run | Teach only when the workspace is genuinely new | Setup checklist and first project/task actions | Must never reappear for returning operators |

Do not merge A through D into a single dense dashboard. First-run and returning
states are different contexts and may select different candidates.

### Evidence to gather

1. How often is Welcome reached by closing all tabs versus first launch?
2. Which action is most common within 30 seconds after Welcome appears?
3. Can reliable recency be derived without adding another backend contract?
4. Does the Game of Life idle visualization help orientation or merely compete
   with the action card?
5. Which labels and guidance survive the AGT-2237 voice review?
6. Does each candidate remain useful in light and dark at narrow and wide
   editor sizes?

### Decision gate

Move this candidate to `comparing` only when a self-contained focused workbench
renders the current treatment and at least two candidates in both themes. Move
it to `decided` only when the record contains:

- selected option and rejected alternatives;
- target context: first run, returning operator, or both with explicit
  branching;
- primary action and permitted secondary actions;
- AGT-2237 wording review;
- keyboard, focus and reduced-motion behavior;
- light and dark evidence.

Only then create an implementation card. The card must link back to this
section and the focused Workbench page.

## Decision template

Copy this block for the next candidate:

```text
Candidate:
State: framing | comparing | decided | sliced | shipped | parked
Observed problem:
Evidence:
Options:
Decision:
Rejected alternatives:
Token impact:
Tone of Voice impact:
Accessibility and motion:
Light/dark proof:
Implementation card:
```

## Living knowledge log

- **2026-07-23:** Added the current Welcome panel as the first vNext candidate.
  Kept it in `framing`; no implementation card is warranted before options,
  usage evidence and AGT-2237 wording review exist.

