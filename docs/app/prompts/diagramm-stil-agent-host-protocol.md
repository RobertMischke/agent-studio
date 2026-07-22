# Skill: Architektur-Diagramme im „Agent Host Protocol"-Stil

**Zweck.** Dieses Dokument macht den Diagramm- und Seitenstil von
[microsoft.github.io/agent-host-protocol](https://microsoft.github.io/agent-host-protocol/)
reproduzierbar. Es liefert (a) die Stil-Prinzipien, (b) fertige Design-Tokens als
CSS-Variablen für Light **und** Dark, (c) HTML/CSS-Template-Snippets für die
Bausteine (Einheiten-Box, Lane/Host, Verbindung/Wire, Nummern-Badge, Pool,
Sequenz-Zeile) und (d) eine Anwendungs-Anweisung für einen Agenten.

**Referenz-Umsetzung:** `docs/concepts/zielarchitektur-diagramm.html` (die
Agent-Studio-Zielarchitektur in genau diesem Stil, self-contained, theme-aware).

**Einsatzhinweis (wichtig).** Dieser Stil ist zugleich die Grundlage für Diagramme
und Sektionen auf der **Agent-Studio-Website** (`agent-studio-marketing`). Die
Tokens und Snippets hier sind bewusst framework-frei (kein VitePress nötig) und
lassen sich direkt in die Marketing-Seite übernehmen — dieselbe Palette, dieselben
Wire-Diagramme, konsistenter Marken-Look zwischen Wiki-Konzept und öffentlicher
Seite.

---

## (a) Prinzipien — worauf der Stil beruht

1. **Ruhe durch Raster und Weißraum.** Wenige Elemente, großzügige Abstände,
   klare vertikale Rhythmen. Das Diagramm ist zentriert, symmetrisch, atmet.
2. **Flache Boxen, keine Verzierung.** 1&nbsp;px-Rahmen, dezenter Radius (8–14&nbsp;px),
   sehr weicher Schatten. Kein Verlauf in den Boxen, kein 3D, keine dicken Ränder.
3. **Gedeckte Fläche, genau zwei Akzente.** Neutrale Graustufen tragen die Fläche;
   **Blau = Autorität/Kontrollebene**, **Teal = Ausführungsebene**. Ein drittes
   Signal-Coral nur für Nummern/Labels. Nie mehr als diese drei Farben tragen Bedeutung.
4. **Verbindungen sind dünne Bezier-Kurven, keine Kästen-Pfeile.** Eine feine graue
   „Rail" liegt statisch da; darüber laufen farbige, gestrichelte „Flow"-Dashes
   (dezent animiert) in Akzentfarbe. Richtung entsteht durch Farbe + optionalen Pfeil,
   nicht durch schwere Pfeilköpfe.
5. **Technik: HTML-Boxen + dünne SVG-Wire-Ebene.** Das SVG zeichnet **nur** die
   Verbindungslinien (`preserveAspectRatio="none"`, Pfade in `<defs>`, per `<use>`
   wiederverwendet). Alle Boxen und Labels sind normales, absolut positioniertes HTML
   darüber. So bleibt Text scharf, selektierbar und theme-aware.
6. **Mono für Metadaten, Sans für Inhalt.** Fließtext und Überschriften in einem
   Inter-nahen Sans; Labels, Nummern, Endpunkte, Kanal-Etiketten in Monospace mit
   `letter-spacing` und `UPPERCASE`.
7. **Nummern-Badges statt Kapitel-Overhead.** Abschnitte tragen ein kleines
   Coral-Mono-Label („§1 · HAUPT-ARCHITEKTUR"), Pills bekommen eine dünne
   Akzent-Outline und einen Punkt/Icon.
8. **Theme-aware als Grundhaltung.** Jeder Wert existiert in Light und Dark. Dark ist
   nicht „Farben invertiert", sondern eine eigene, tiefe, ruhige Palette; Akzente
   leuchten in Dark etwas heller, die Host-Box bekommt einen weichen Glow.

---

## (b) Design-Tokens (Light + Dark)

Kopiere diesen Block unverändert in den `<style>`-Kopf. Alles Weitere referenziert
nur diese Variablen — Farben nie hart im Snippet setzen.

```css
:root {
  color-scheme: light;
  /* Flächen / Ink */
  --bg: #ffffff; --bg-soft: #f6f6f7; --bg-elv: #ffffff;
  --ink-1: #3c3c43; --ink-2: #67676c; --ink-3: #929295;
  --line: #e2e2e3; --border: #c2c2c4;
  /* Akzent A — Blau = Autorität / Kontrollebene */
  --blue: #3b82f6; --blue-ink: #2f6fd6; --blue-soft: #e7f0fe;
  --blue-bd: rgba(59,130,246,.42); --blue-glow: rgba(59,130,246,.20);
  /* Akzent B — Teal = Ausführungsebene */
  --teal: #14a37f; --teal-ink: #0f8e6e; --teal-soft: #e6f6f1;
  --teal-bd: rgba(20,163,127,.42); --teal-glow: rgba(20,163,127,.18);
  /* Signal / Nummern */
  --coral: #e5484d;
  /* Schrift — Systemfont-Stack, dem Inter-Look nah (kein externer Font!) */
  --font-sans: ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  --font-mono: ui-monospace, "SFMono-Regular", Menlo, Consolas, "Liberation Mono", monospace;
  --chip-shadow: 0 3px 12px rgba(0,0,0,.10);
}
/* Dark: per prefers-color-scheme UND per data-theme (Toggle gewinnt in beide Richtungen) */
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) { /* ...gleiche Werte wie unten... */ }
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

> **Token-Herkunft:** per Playwright aus der Vorlage extrahiert (VitePress-Default-Theme
> + der bespoke `arch-wires`-Hero). Blau `#3b82f6`, Teal `#14a37f`, Rail-Grau `#e2e2e3`,
> Host-Soft `#e7f0fe`; Dark-Fläche `#1b1b1f`/`#202127`, Text `#dfdfd6`. Font: Inter →
> hier als System-Sans nachgebildet.

**Geometrie-Konstanten:** Chip-Radius `8–9px`, Host-Radius `14px`, Padding Chip
`8px 13px` / Host `16px 20px`; Rahmen überall `1px`; Wire-`stroke-width` `~1.6`
(Rail) und `~2.4` (Flow) in viewBox-Einheiten; Dash `7 12`, Animation `1.4–1.7s`.

---

## (c) Template-Snippets

### 1 · Einheiten-Box (Chip)

```html
<div class="chip client"><span class="ic"></span> Studio UI · Board · Wiki</div>
<div class="chip exec"><span class="ic"></span> Runner-Host</div>
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

### 2 · Zentrale Host-/Autoritäts-Box

```html
<div class="host">
  <span class="lbl">Task Server</span>
  <span class="sub">zentrale Konfig &amp; Wahrheit · Leases · Events · Management-API</span>
</div>
```
```css
.host { text-align:center; background:var(--blue-soft); border:1px solid var(--blue-bd);
  border-radius:14px; padding:16px 20px; box-shadow:0 0 34px var(--blue-glow); }
.host .lbl { display:block; font:700 12.5px/1 var(--font-mono); letter-spacing:.16em;
  text-transform:uppercase; color:var(--blue-ink); margin-bottom:8px; }
.host .sub { display:block; font-size:12.5px; color:var(--ink-2); line-height:1.5; }
```

### 3 · Verbindung / Wire (SVG-Ebene über HTML-Boxen)

Der Kern des Stils. Das SVG liegt absolut über einem relativen Stage; Boxen werden
per `left:%` + `top/bottom` darüber positioniert. Pfade einmal in `<defs>`, dann als
statische Rail **und** animierten Flow wiederverwenden.

```html
<div class="arch-stage"><!-- position:relative; aspect-ratio passend zum viewBox -->
  <svg class="wires" viewBox="0 0 360 300" preserveAspectRatio="none" aria-hidden="true">
    <defs>
      <path id="w1" d="M168,54 C168,92 174,92 174,120"/>   <!-- oben: Client↔Host -->
      <path id="w2" d="M158,182 C120,224 96,214 84,250"/>  <!-- unten: Host→Runner -->
    </defs>
    <g class="rails"><use href="#w1"/><use href="#w2"/></g>
    <use class="flow up"   href="#w1" style="animation-duration:1.5s"/>
    <use class="flow down" href="#w2" style="animation-duration:1.6s"/>
  </svg>
  <!-- ... HTML-Boxen hier, absolut positioniert ... -->
</div>
```
```css
.arch-stage { position:relative; width:100%; min-width:640px; aspect-ratio:360/300; }
.wires { position:absolute; inset:0; width:100%; height:100%; }
/* WICHTIG: fill:none MUSS auf die <use>-Elemente greifen, sonst Default-Fill = schwarz */
.wires .rails, .wires .rails use { fill:none; stroke:var(--line); stroke-width:1.6; opacity:.9; }
.wires .flow { fill:none; stroke-width:2.4; stroke-linecap:round;
  stroke-dasharray:7 12; animation:dash 1.5s linear infinite; }
.wires .flow.up   { stroke:var(--blue); }   /* Autoritäts-Kanal */
.wires .flow.down { stroke:var(--teal); }   /* Ausführungs-Kanal */
@keyframes dash { to { stroke-dashoffset:-38; } }
@media (prefers-reduced-motion:reduce){ .wires .flow { animation:none; } }
```
**Fallstrick:** `class="rails"` und CSS `.rails` müssen exakt matchen — sonst rendert
der Pfad mit schwarzer Default-Füllung als massiver Keil. Immer `fill:none` auch auf
`use` setzen. viewBox-Seitenverhältnis = Stage-`aspect-ratio`, sonst verzerren die Dashes.

### 4 · Nummern-Badge & Pill

```html
<p class="sec-no"><span class="n">§1</span> · Haupt-Architektur</p>
<span class="kicker">Konzept · Zielarchitektur</span>
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

### 5 · Pool / Unterfach (gestrichelt, in einer Host-Karte)

```html
<div class="pools">
  <div class="pool"><span class="pn">Build-Slots</span><span class="pd">dynamisch, on the fly</span></div>
  <div class="pool"><span class="pn">API-Lanes</span><span class="pd">Post-Processing</span></div>
</div>
```
```css
.pools { display:flex; gap:6px; margin-top:9px; }
.pool { flex:1; border:1px dashed var(--teal-bd); border-radius:7px; padding:6px 7px; background:var(--teal-soft); }
.pool .pn { display:block; font:700 9.5px/1.2 var(--font-mono); letter-spacing:.08em; text-transform:uppercase; color:var(--teal-ink); }
.pool .pd { display:block; font-size:10.5px; color:var(--ink-2); margin-top:2px; }
```

### 6 · Sequenz-Zeile (Wire-Format)

Zwei Lifelines (gestrichelte Vertikalen), zentrierte Nachricht-Boxen; Richtung nur
über Farbe + Pfeil (Teal = zum Server/ausgehend, Blau = zurück). Keine schweren Pfeile.

```html
<div class="seq-body">
  <div class="seq-note">Claim</div>
  <div class="seq-step"><div class="seq-msg to-s">POST /api/runner/claim <span class="arw">→</span></div></div>
  <div class="seq-step"><div class="seq-msg to-c"><span class="arw">←</span> fenced lease + Run-Plan</div></div>
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
.seq-msg.to-s { border-color:var(--teal-bd); color:var(--teal-ink); }  /* ausgehend */
.seq-msg.to-c { border-color:var(--blue-bd); color:var(--blue-ink); }  /* zurück */
.seq-note { text-align:center; font:600 11px/1.4 var(--font-mono); letter-spacing:.06em;
  text-transform:uppercase; color:var(--ink-3); padding:14px 0 2px; }
```

---

## (d) Anwendungs-Anweisung für einen Agenten

> **Baue Diagramm X im „Agent Host Protocol"-Stil.** Erzeuge eine **self-contained**
> HTML-Datei — kein externes CSS, kein externer Font, keine CDN-Skripte; nutze den
> System-Sans/-Mono-Stack aus den Tokens. Gehe so vor:
>
> 1. **Tokens einsetzen:** Kopiere den vollständigen `:root`-Block aus Abschnitt (b)
>    unverändert. Setze im ganzen Dokument **nie** Farben hart — immer die Variablen.
> 2. **Semantik auf zwei Akzente abbilden:** Ordne jede Rolle einer Ebene zu —
>    **Blau** für Kontroll-/Autoritäts-/Client-Seite, **Teal** für Ausführung/Backend.
>    Höchstens diese zwei Farben plus Coral für Nummern tragen Bedeutung.
> 3. **Diagramm bauen:** Boxen als HTML-`.chip`/`.host`/Karten (Snippets 1, 2, 5),
>    Verbindungen als dünne SVG-Wire-Ebene (Snippet 3) — SVG zeichnet **nur** Linien,
>    nie Text. Richtung über Flow-Farbe (`up`=Blau, `down`=Teal).
> 4. **Struktur beschriften:** Abschnitte mit `.sec-no`-Coral-Badges (Snippet 4),
>    Kanäle mit Mono-Etiketten (`pull · outbound-only`), optional ein Sequenz-Diagramm
>    im Wire-Format (Snippet 6).
> 5. **Theme-aware:** Light + Dark aus den Tokens; kleiner Toggle, der `data-theme`
>    auf `:root` stampft und in beide Richtungen gewinnt. Prüfe **beide** Themes.
> 6. **Responsiv & ruhig:** Diagramm in einen `overflow-x:auto`-Wrapper mit `min-width`;
>    der Body scrollt nie horizontal. Viel Weißraum, wenige Elemente, kein Deko-Ballast.
> 7. **Verifizieren:** Mit Playwright in Light und Dark rendern und die Screenshots
>    sichten (Rail nicht schwarz gefüllt, Dashes nicht verzerrt, kein Overflow).
>
> **Definition of done:** Sieht neben `docs/concepts/zielarchitektur-diagramm.html`
> wie aus einem Guss aus; öffnet ohne Netzwerk; korrekt in Light und Dark.

---

*Stil seziert am 22.07.2026 per Playwright gegen microsoft.github.io/agent-host-protocol
(VitePress + bespoke `arch-wires`-Hero). Screenshots & Token-Dump im Session-Scratchpad.
Referenz-Umsetzung: `docs/concepts/zielarchitektur-diagramm.html`. Einsatz auch für
`agent-studio-marketing` (öffentliche Website).*
