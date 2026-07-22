# Skill: Architecture diagrams in the Agent Host Protocol style

**Purpose.** This document makes the diagram and page style of
[microsoft.github.io/agent-host-protocol](https://microsoft.github.io/agent-host-protocol/)
reproducible. It provides (a) the style principles, (b) ready-to-use design
tokens as CSS variables for light and dark themes, (c) HTML/CSS template
snippets for the building blocks (unit box, lane/host, connection/wire, number
badge, pool, and sequence row), and (d) instructions an agent can follow.

**Reference implementation:** `docs/concepts/zielarchitektur-diagramm.html`,
the Agent Studio target architecture in this exact self-contained,
theme-aware style.

**Usage.** The **Agent Studio website** (`agent-studio-marketing`) also uses
this style for diagrams and sections. The tokens and snippets do not require
VitePress or another framework. They can be used in the Wiki and public site.

---

## (a) Principles

1. **Grid and whitespace.** Use few elements, defined spacing, and a consistent
   vertical rhythm. Center the diagram and align elements to a grid.
2. **Flat boxes.** Use 1&nbsp;px borders, an 8-14&nbsp;px radius, and the shadow
   token from section (b). Do not use gradients, 3D effects, or thick borders.
3. **Muted surfaces and exactly two accents.** Neutral gray tones carry the
   surface. **Blue means authority or control plane** and **teal means execution
   plane**. Use coral as a third signal color only for numbers and labels. No
   other colors should carry meaning.
4. **Connections are thin Bezier curves.** A gray rail is static. Colored
   dashed flows move over it. Indicate direction through color and an optional
   arrow. Do not use thick arrowheads.
5. **Technique: HTML boxes plus a thin SVG wire layer.** SVG draws only the
   connections (`preserveAspectRatio="none"`, paths in `<defs>`, reused with
   `<use>`). All boxes and labels are normal, absolutely positioned HTML above
   it. Text therefore stays sharp, selectable, and theme-aware.
6. **Monospace for metadata, sans-serif for content.** Use an Inter-like
   sans-serif for body text and headings. Use monospace with `letter-spacing`
   and uppercase for labels, numbers, endpoints, and channel labels.
7. **Number badges instead of chapter overhead.** Give sections a compact
   coral monospace label such as `§1 · MAIN ARCHITECTURE`. Give pills a thin
   accent outline and a dot or icon.
8. **Light and dark themes.** Define every value for both themes. Use the dark
   theme values in section (b) instead of color inversion. The dark theme uses
   brighter accent values and the host box uses the defined glow token.

---

## (b) Design tokens (light and dark)

Copy this block unchanged into the `<style>` element. Everything else should
reference these variables. Never hardcode colors in a snippet.

```css
:root {
  color-scheme: light;
  /* Surfaces / ink */
  --bg: #ffffff; --bg-soft: #f6f6f7; --bg-elv: #ffffff;
  --ink-1: #3c3c43; --ink-2: #67676c; --ink-3: #929295;
  --line: #e2e2e3; --border: #c2c2c4;
  /* Accent A: blue = authority / control plane */
  --blue: #3b82f6; --blue-ink: #2f6fd6; --blue-soft: #e7f0fe;
  --blue-bd: rgba(59,130,246,.42); --blue-glow: rgba(59,130,246,.20);
  /* Accent B: teal = execution plane */
  --teal: #14a37f; --teal-ink: #0f8e6e; --teal-soft: #e6f6f1;
  --teal-bd: rgba(20,163,127,.42); --teal-glow: rgba(20,163,127,.18);
  /* Signal / numbers */
  --coral: #e5484d;
  /* System font stack with an Inter-like look, no external font */
  --font-sans: ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  --font-mono: ui-monospace, "SFMono-Regular", Menlo, Consolas, "Liberation Mono", monospace;
  --chip-shadow: 0 3px 12px rgba(0,0,0,.10);
}
/* Dark mode via prefers-color-scheme and data-theme. The explicit toggle wins. */
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) { /* Use the same values as below. */ }
}
:root[data-theme="dark"] {
  color-scheme: dark;
  --bg: #1b1b1f; --bg-soft: #202127; --bg-elv: #202127;
  --ink-1: #dfdfd6; --ink-2: #98989f; --ink-3: #6a6a71;
  --line: #2e2e32; --border: #3c3f44;
  --blue: #4c8dff; --blue-ink: #a8b1ff; --blue-soft: #1c2740;
  --blue-bd: rgba(90,150,255,.50); --blue-glow: rgba(76,141,255,.30);
  --teal: #34d3a6; --teal-ink: #4fe0b6; --teal-soft: #16302a;
  --teal-bd: rgba(52,211,166,.48); --teal-glow: rgba(52,211,166,.24);
  --coral: #f06a6f;
  --chip-shadow: 0 3px 14px rgba(0,0,0,.45);
}
```

> **Token origin:** extracted from the reference with Playwright (VitePress
> default theme plus the bespoke `arch-wires` hero). Blue `#3b82f6`, teal
> `#14a37f`, rail gray `#e2e2e3`, host soft `#e7f0fe`; dark surfaces
> `#1b1b1f`/`#202127`, text `#dfdfd6`. The Inter font is represented here by
> the system sans-serif stack.

**Geometry constants:** chip radius `8-9px`, host radius `14px`, chip padding
`8px 13px`, host padding `16px 20px`, `1px` borders throughout, wire
`stroke-width` around `1.6` for rails and `2.4` for flows in viewBox units,
dash `7 12`, and animation duration `1.4-1.7s`.

---

## (c) Template snippets

### 1 · Unit box (chip)

```html
<div class="chip client"><span class="ic"></span> Studio UI · Board · Wiki</div>
<div class="chip exec"><span class="ic"></span> Runner Host</div>
```
```css
.chip { display:inline-flex; align-items:center; gap:8px;
  background:var(--bg-elv); color:var(--ink-1); border:1px solid var(--border);
  border-radius:9px; padding:8px 13px; font:600 13px/1 var(--font-sans);
  box-shadow:var(--chip-shadow); white-space:nowrap; }
.chip .ic { width:9px; height:9px; border-radius:3px; flex:none; }
.chip.client { border-color:var(--blue-bd); } .chip.client .ic { background:var(--blue); }
.chip.exec   { border-color:var(--teal-bd); } .chip.exec   .ic { background:var(--teal); }
```

### 2 · Central host or authority box

```html
<div class="host">
  <span class="lbl">Task Server</span>
  <span class="sub">central configuration and truth · leases · events · management API</span>
</div>
```
```css
.host { text-align:center; background:var(--blue-soft); border:1px solid var(--blue-bd);
  border-radius:14px; padding:16px 20px; box-shadow:0 0 34px var(--blue-glow); }
.host .lbl { display:block; font:700 12.5px/1 var(--font-mono); letter-spacing:.16em;
  text-transform:uppercase; color:var(--blue-ink); margin-bottom:8px; }
.host .sub { display:block; font-size:12.5px; color:var(--ink-2); line-height:1.5; }
```

### 3 · Connection or wire (SVG layer above HTML boxes)

Place the SVG absolutely over a relative stage.
Position boxes above it with percentage `left` plus `top` or `bottom`. Define
paths once in `<defs>`, then reuse them as both static rails and animated flows.

```html
<div class="arch-stage"><!-- position:relative; aspect-ratio matches the viewBox -->
  <svg class="wires" viewBox="0 0 360 300" preserveAspectRatio="none" aria-hidden="true">
    <defs>
      <path id="w1" d="M168,54 C168,92 174,92 174,120"/>   <!-- top: client to host -->
      <path id="w2" d="M158,182 C120,224 96,214 84,250"/>  <!-- bottom: host to runner -->
    </defs>
    <g class="rails"><use href="#w1"/><use href="#w2"/></g>
    <use class="flow up"   href="#w1" style="animation-duration:1.5s"/>
    <use class="flow down" href="#w2" style="animation-duration:1.6s"/>
  </svg>
  <!-- Absolutely positioned HTML boxes go here. -->
</div>
```
```css
.arch-stage { position:relative; width:100%; min-width:640px; aspect-ratio:360/300; }
.wires { position:absolute; inset:0; width:100%; height:100%; }
/* IMPORTANT: fill:none must reach the use elements, or the default fill is black. */
.wires .rails, .wires .rails use { fill:none; stroke:var(--line); stroke-width:1.6; opacity:.9; }
.wires .flow { fill:none; stroke-width:2.4; stroke-linecap:round;
  stroke-dasharray:7 12; animation:dash 1.5s linear infinite; }
.wires .flow.up   { stroke:var(--blue); }   /* Authority channel */
.wires .flow.down { stroke:var(--teal); }   /* Execution channel */
@keyframes dash { to { stroke-dashoffset:-38; } }
@media (prefers-reduced-motion:reduce){ .wires .flow { animation:none; } }
```

**Pitfall:** `class="rails"` and the `.rails` selector must match exactly. If
they do not, the path renders with a black default fill as a solid wedge.
Always set `fill:none` on `use` too. The viewBox aspect ratio must match the
stage `aspect-ratio`, or the dashes will distort.

### 4 · Number badge and pill

```html
<p class="sec-no"><span class="n">§1</span> · Main architecture</p>
<span class="kicker">Concept · Target architecture</span>
```
```css
.sec-no { display:inline-flex; align-items:center; gap:8px;
  font:600 12px/1 var(--font-mono); letter-spacing:.12em; text-transform:uppercase;
  color:var(--coral); }
.sec-no::before { content:""; width:14px; height:1.5px; background:var(--coral); }
.kicker { display:inline-flex; align-items:center; gap:8px;
  font:600 12px/1 var(--font-mono); letter-spacing:.14em; text-transform:uppercase;
  color:var(--blue-ink); border:1px solid var(--blue-bd); background:var(--blue-soft);
  border-radius:99px; padding:7px 14px; }
.kicker::before { content:""; width:7px; height:7px; border-radius:50%; background:var(--blue); }
```

### 5 · Pool or compartment (dashed, inside a host card)

```html
<div class="pools">
  <div class="pool"><span class="pn">Build slots</span><span class="pd">dynamic, on demand</span></div>
  <div class="pool"><span class="pn">API lanes</span><span class="pd">post-processing</span></div>
</div>
```
```css
.pools { display:flex; gap:6px; margin-top:9px; }
.pool { flex:1; border:1px dashed var(--teal-bd); border-radius:7px; padding:6px 7px; background:var(--teal-soft); }
.pool .pn { display:block; font:700 9.5px/1.2 var(--font-mono); letter-spacing:.08em; text-transform:uppercase; color:var(--teal-ink); }
.pool .pd { display:block; font-size:10.5px; color:var(--ink-2); margin-top:2px; }
```

### 6 · Sequence row in wire format

Use two lifelines made from dashed vertical lines and centered message boxes.
Show direction only through color and arrows: teal goes to the server or
outward, and blue comes back. Do not use heavy arrows.

```html
<div class="seq-body">
  <div class="seq-note">Claim</div>
  <div class="seq-step"><div class="seq-msg to-s">POST /api/runner/claim <span class="arw">→</span></div></div>
  <div class="seq-step"><div class="seq-msg to-c"><span class="arw">←</span> fenced lease + run plan</div></div>
</div>
```
```css
.seq-body { position:relative; }
.seq-body::before,.seq-body::after { content:""; position:absolute; top:0; bottom:0; width:1.5px;
  background:repeating-linear-gradient(var(--line) 0 6px, transparent 6px 12px); }
.seq-body::before { left:25%; } .seq-body::after { left:75%; }
.seq-step { display:flex; justify-content:center; padding:11px 0; }
.seq-msg { max-width:74%; font:600 12px/1.4 var(--font-mono); background:var(--bg-elv);
  border:1px solid var(--line); border-radius:8px; padding:8px 14px; text-align:center; box-shadow:var(--chip-shadow); }
.seq-msg.to-s { border-color:var(--teal-bd); color:var(--teal-ink); }  /* Outbound */
.seq-msg.to-c { border-color:var(--blue-bd); color:var(--blue-ink); }  /* Return */
.seq-note { text-align:center; font:600 11px/1.4 var(--font-mono); letter-spacing:.06em;
  text-transform:uppercase; color:var(--ink-3); padding:14px 0 2px; }
```

---

## (d) Instructions for an agent

> **Build diagram X in the Agent Host Protocol style.** Create a
> **self-contained** HTML file with no external CSS, font, or CDN script. Use
> the system sans-serif and monospace stacks from the tokens.
>
> 1. **Apply the tokens:** copy the complete `:root` block from section (b)
>    unchanged. Never hardcode colors elsewhere in the document.
> 2. **Map semantics to two accents:** assign every role to a plane. Use
>    **blue** for the control, authority, or client side, and **teal** for
>    execution or backend. Only these two colors plus coral for numbers may
>    carry meaning.
> 3. **Build the diagram:** use HTML `.chip`, `.host`, and card boxes from
>    snippets 1, 2, and 5. Use the thin SVG wire layer from snippet 3 for
>    connections. SVG draws lines only, never text. Indicate direction with
>    flow color (`up` is blue, `down` is teal).
> 4. **Label the structure:** use coral `.sec-no` badges from snippet 4 for
>    sections and monospace channel labels such as `pull · outbound-only`.
>    Optionally add a wire-format sequence diagram from snippet 6.
> 5. **Support themes:** include the light and dark tokens. Add a small toggle
>    that stamps `data-theme` on `:root` and overrides the system preference in
>    both directions. Check both themes.
> 6. **Support responsive layouts:** put the diagram in an `overflow-x:auto`
>    wrapper with a `min-width`. The body must never scroll horizontally. Use
>    the defined spacing and include only elements required by the diagram.
> 7. **Verify it:** render light and dark modes with Playwright and inspect the
>    screenshots. Confirm that rails are not black-filled, dashes are not
>    distorted, and nothing overflows.
>
> **Definition of done:** the result uses the specified tokens and components,
> opens without network access, and renders correctly in light and dark themes.

---

*Style analyzed on 22 July 2026 with Playwright against
microsoft.github.io/agent-host-protocol (VitePress plus the bespoke
`arch-wires` hero). Screenshots and token dump are in the session scratchpad.
Reference implementation: `docs/concepts/zielarchitektur-diagramm.html`. This
style is also used for the public `agent-studio-marketing` website.*
