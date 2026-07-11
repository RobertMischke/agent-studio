# Project State Indicator Alternatives

## Decision context

The Explorer project row needs to answer one question before it answers any
counting question: **where does this project stand right now?** The existing
Board child row can keep lane badges or lane dots because that level explains
the work distribution. The project row is one level higher and should use its
limited trailing space for a compact situation summary, not a total job count.

The accompanying [interactive exploration](../../design/tree-indicator-exploration-2026-07.html)
recreates the Explorer hierarchy with realistic calm, active, escalated, and
full-shelf projects. It can switch all project rows between the eight options,
toggle light and dark themes, collapse project children, and pause motion.

## Shared constraints

- No option uses a colored left edge or inset stripe.
- Loud color and pulse are reserved for an acute state. Completed or historical
  work stays quiet.
- Every indicator has a text tooltip and an accessible label. Color is not the
  only carrier of meaning.
- The project row shows a situation, not the total sum of all tasks.
- Board and lane details remain visible one level below as small labeled badges.
- All options fit the same fixed trailing column to avoid row jitter.

## Alternatives catalog

### A. Micro dashboard dots

A stable row of six small dots samples the project's current work mix: muted for
ready, blue for active, green for recently completed, and amber or red only for
attention. It builds directly on the promising AGT-2048 dot language and reads
as a tiny dashboard rather than a number. It uses little vertical space and is
fast to compare across rows, but exact meaning needs a tooltip at first and a
very full project must be sampled rather than drawn one dot per task. It follows
the hard rules because status is encoded in discrete dots, with no left accent.

### B. Segmented horizon

A 68-pixel horizontal track allocates proportional segments to ready, active,
review, and blocked work. The project shape is immediately comparable and the
bar makes sensible use of otherwise awkward horizontal space. It is less useful
for tiny projects, and narrow segments become hard to decode without a tooltip.
The neutral track and full-surface segments avoid the forbidden accent-line
pattern; an alert segment appears only for an unresolved acute item.

### C. Seven-step sparkline

A tiny line shows recent project momentum across seven samples, paired with a
small endpoint dot that carries the current state. This answers whether the
project is moving, stalled, or recovering without exposing a misleading total.
It is excellent for trend recognition but requires reliable historical samples
and does not explain the current work mix on its own. The line is a chart inside
the trailing cell, not a decorative row edge, and only the acute endpoint uses
warning color.

### D. State glyph plus focus number

A semantic glyph (rest, play, warning, or shelf) sits beside one contextual
number: active work for normal projects, blockers for escalation, or waiting
items for a full shelf. This is the most explicit and accessible alternative,
and it remains legible at very small sizes. Its weakness is that the number's
meaning changes with state, so the tooltip and glyph must make the context
unambiguous. It uses a compact badge-like cluster and no edge decoration.

### E. Pulse plus micro dashboard

The existing AGT-1921/AGT-2031 auto-pickup pulse occupies its reserved slot,
followed by four quiet situation dots. Motion means only "working now"; an
escalated project uses a static alert ring instead of a cheerful pulse. This is
the strongest bridge from current behavior to AGT-2048 and carries both liveness
and composition in little space. It is visually busier than dots alone and must
fully stop under reduced motion. The pulse is acute/live-only and does not use a
left accent.

### F. Heat orb

One 12-pixel orb combines state color, fill intensity, and a restrained halo for
acute escalation. It is the smallest option and makes row scanning effortless:
cool means calm, bright blue means active, amber means capacity pressure, red
means action required. It compresses too much nuance into one mark and relies
most heavily on learned color semantics, though a shape/ring change and tooltip
help. The orb is a sanctioned dot treatment and the halo is limited to current
acute state.

### G. Compact state stack

Three overlapping mini tiles represent queued, moving, and attention work; the
front tile carries the dominant state. The stack suggests depth and a project
"shelf" particularly well, including a visibly full state without displaying a
total. It is distinctive and space efficient but less conventional, and overlap
can reduce clarity at high zoom or low resolution. Whole-tile fills carry state,
so the treatment remains consistent with the no-accent-line rule.

### H. Capacity pips

Five fixed slots show operating pressure rather than task count. Filled blue
pips mean healthy utilization, amber pips mean the shelf is near capacity, and
a red cap marks a blocked flow. This makes "shelf full" instantly legible and
compares well across projects. It says less about work type and can be mistaken
for generic progress unless labeled "load" in the tooltip. The pips are compact
status dots with acute color reserved for current pressure or blockage.

## Recommendation

Build **A. Micro dashboard dots** first. It is the clearest continuation of the
operator preference from AGT-2048, preserves the Explorer's density, compares
well across projects, and leaves detailed badges at Board level. Use a stable
six-slot sampling contract rather than one dot per task, so the indicator never
turns into a disguised total.

Prototype **E. Pulse plus micro dashboard** as the second candidate if liveness
must stay visible on the same row. It integrates the existing pulse vocabulary
without making history loud. Keep the pulse in a reserved slot, animate only
while auto-pickup is actually active, and render escalation as a static alert
ring. Option H is a useful fallback if "shelf pressure" proves more important
than work composition in operator testing.

## Decision questions for review

1. Is the first glance primarily about composition (A/E) or capacity (H)?
2. Does live auto-pickup need to remain visible at project level (E), or can it
   stay in a tooltip/details surface (A)?
3. Can operators correctly distinguish active from escalated in light theme at
   normal sidebar width?
4. Does the indicator remain useful when a project has hundreds of tasks?

## Living knowledge log

- **2026-07-11:** Eight project-level alternatives explored against calm,
  active, escalated, and full-shelf states. A and E recommended for the next
  decision round; no production implementation was made.
