# Research: Schärferer Reissue-Prompt = schnellere Konvergenz? (AGT-2380)

Status: abgeschlossene Recherche, keine Code-Änderung. Ergänzt die bereits laufende
quantitative Spur unter [`docs/quality/reissue-prompt-convergence/`](../quality/reissue-prompt-convergence/index.html)
und das dort verlinkte randomisierte Experiment
[`docs/quality/pipeline-time-economy/reissue-prompt-experiment.md`](../quality/pipeline-time-economy/reissue-prompt-experiment.md)
um eine korpusweite, qualitative Auswertung: nicht "hat es gewirkt", sondern
**"was steckt in einem Reissue-Prompt eigentlich drin, und warum sollte er wirken oder nicht"**.

## Fragestellung

Konvergiert ein wiederaufgesetzter Lauf (Reissue) schneller/zuverlässiger, wenn der
Reissue-Prompt schärfer/spezifischer ist (konkrete Findings zuerst, Datei-/Zeilenverweise,
explizite Nicht-Ziele) statt generisch? Und: lohnt sich ein automatischer
Reissue-Prompt-Builder für den Eskalations-Flow?

## Methode

Zwei Bausteine, die sich ergänzen:

1. **Korpus-Zensus statt Stichprobe.** Über alle drei Task-Buckets in
   `agent-taskboard-workspace/projects/agent-taskboard/tasks/{000,001,002}` (1375 Karten)
   wurden alle `orchestrator-follow-up-history/*.md`-Dateien eingelesen (996 `*-reissue.md`
   + 8 `*-noop-recovery.md` = 1004 Dateien über 440 Karten mit mindestens einem Reissue).
   Jede Datei wurde nach dem ersten Satz nach der Überschrift `## Steering prompt (verbatim)`
   klassifiziert (deterministische Präfix-Regel, siehe unten), außerdem wurde pro Karte der
   Endzustand (`task.json.state`) sowie die Reissue-Anzahl erfasst.
2. **Tiefenlektüre an 14 Karten mit mehreren Läufen**, ausgewählt nach Reissue-Anzahl
   (2 bis 16) und Verfügbarkeit von `model-qualification.jsonl` (Modell-Konfundierung
   prüfbar). Volltext gelesen: `prompt.md` (inkl. nachträglicher Operator-Absätze),
   alle `orchestrator-follow-up-history/*.md`, `status.md`, `task.json`, teilweise
   `logs/run-context/*.md` und `model-qualification.jsonl`.

Zusätzlich wurde geprüft, ob bereits vorhandene Analysen zum Thema existieren, bevor neu
gerechnet wurde — Ergebnis siehe Befund 8: es gibt bereits eine registrierte, randomisierte
Kontrollstudie samt Observational-Snapshot auf demselben `develop`-Stand (Commit `693dac523`,
geborgen aus dem eigenen Vorlauf von AGT-2380). Diese Recherche dupliziert deren Statistik
nicht, sondern zitiert sie und liefert die Korpus-weite qualitative Erklärungsebene, die dort
fehlt (die Studie deckt nur die Ursachenfamilie "model-review-finding" ab, nicht die
mengenmäßig größeren `deterministic-gate`- und Evidence-Gate-Reissues).

## Datenbasis

**Korpus:** 1375 indizierte Karten, davon 440 mit ≥1 Reissue (996 Reissues + 8
Noop-Recovery). Verteilung der Reissue-Anzahl pro Karte:

| Reissues/Karte | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 16 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Karten | 182 | 167 | 26 | 23 | 4 | 14 | 7 | 8 | 3 | 5 | 1 |

**14 Karten in Tiefenlektüre:**

| Karte | Titel (gekürzt) | Typ | Reissues | Endzustand |
|---|---|---|---|---|
| AGT-2182 | P0 Remote attempt authority (RunAttempt/ReviewAttempt-Fencing) | feature | 16 | 7-archive |
| AGT-2255 | Lane-Move meldet Success trotz verbleibendem Quellordner | bug | 10 | 7-archive |
| AGT-2233 | TaskServerStoreTests: offenes DB-Handle blockiert Aufräumen | bug | 10 | 7-archive |
| AGT-2168 | Completion-Judge: Freitext-Evidenz typisiert interpretieren | bug | 10 | 7-archive |
| AGT-2135 | Tab-Leiste: aktiver Tab automatisch in Sicht scrollen | chore | 10 | 7-archive |
| AGT-2110 | Theoretical-Cost-Provider CAR → TokenEconomy | chore | 10 | 7-archive |
| AGT-2258 | Gate testet Subject auf aktuellem develop | feature | 9 | 7-archive |
| AGT-2194 | Task-Server-Management: Live-Admin-API statt Seed-UI | feature | 9 | 7-archive |
| AGT-2177 | Remote-Runner: divergente Salvage-Branches ohne Requeue-Loop | feature | 9 | 7-archive |
| AGT-2108 | Wiki-Quelle: Projekt-weiter Branch statt Checkout-Stand | chore | 7 | 7-archive |
| AGT-2169 | Progress-Admission atomar + Zombie-Selbstheilung | bug | 4 | 7-archive |
| AGT-2197 | Knowledge Drift: Remote-Hosts UX-Mockups | chore | 4 | 7-archive |
| AGT-2149 | Runner-Host Push-Identität (Deploy-Key/Org-Policy) | bugfix | 3 | 7-archive |
| AGT-2158 | Project Hub URL-Start robust machen | chore | 2 | 7-archive |

Alle 14 sind letztlich `7-archive` (angenommen/gemergt) — bewusst gewählt, weil gerade der
lange Weg dorthin (wie viele Runden, welche Art Reissue) die interessante Variable ist, nicht
Erfolg/Misserfolg (siehe Befund 6).

## Befunde

### 1. Reissue-Prompts sind fast nie freie Prosa — es gibt genau acht Vorlagen

Über alle 996 `*-reissue.md` im Korpus reduziert sich der Text nach der Überschrift
"Steering prompt (verbatim)" auf **acht** unterscheidbare Eröffnungssätze:

| Vorlage | Anzahl | Auslöser | Charakter |
|---|---:|---|---|
| `build-gate` ("Auto-review re-opened … deterministic build/test gate failed") | 285 | Build/Test-Gate rot | langer Rohlog (Ø 60–90 Zeilen), Fehler steht meist erst spät im Text |
| `steer-diff` ("STEER THE DIFF, DO NOT RESTART") | 255 | Completion-Gate sieht offene Punkte in Selbstauskunft des Vorlaufs | zitiert wörtlich Zeilen aus dem letzten Lauf, mit Zeitstempel |
| `aspect-block` ("Auto-review found one or more blocking aspect verdicts") | 187 | Aspekt-Review (Doku/Code-Quality) blockiert | kurz, nennt Aspektname + eine Zeile Begründung, aber keine Datei |
| `evidence-gate` ("Auto-review will not accept … bare success claim") | 179 | fehlender Beleg (Screenshot/E2E) | kurz, nennt exakt geforderten Beleg-Pfad |
| `needs-input-answer` ("The orchestrator answered your NEEDS_INPUT request") | 57 | Agent hatte Rückfrage gestellt | 2–3 Sätze, reine Entscheidungsweitergabe |
| `quality-concerns` ("Auto-review found solution-quality concerns") | 15 | Mensch-artige Review-Notiz | mittel |
| `council-reaction` ("Council reaction to quality grade C/D") | 10 | automatisches Grading | kurz |
| `noop-recovery` (Agent meldete fälschlich `[[TASK_NOOP]]`) | 8 | — | reinjiziert die komplette Original-Anforderung |

Das ist der zentrale Rahmenbefund: "Reissue-Prompt schärfen" ist im Ist-Zustand **kein
Freitext-Spielraum eines Menschen**, sondern die Auswahl trifft der auslösende Gate-Typ, und
die "Schärfe" ergibt sich fast ausschließlich daraus, *wie viel konkreter dynamischer Inhalt*
in die Vorlage eingesetzt wird — nicht aus Wortwahl.

### 2. Es gibt doch eine echte "geschärfte" Ebene — von Menschen nachträglich in `prompt.md` eingefügt

Neben der automatischen Vorlagen-Ebene existiert eine zweite, seltenere Ebene: Operator-
Nachträge, die direkt an `prompt.md` angehängt werden (81 von 1375 Karten, erkennbar an
Überschriften wie `## Steer-Runde (Operator, …)`, `## OPERATOR-KLARSTELLUNG`,
`## Gezielte Runde (Operator-Sweep …)`, `## Re-Cut (Operator …) - VERBINDLICH`). Beispiel
AGT-2149 (Original-Ziel war eine vierteilige Infrastruktur+Doku-Aufgabe), 12 Stunden später:

> „Die Infrastruktur ist BEREITS ERLEDIGT … Du brauchst KEINEN Zugriff auf den
> Runner-Host … Dein verbleibender Umfang ist reiner CODE + DOKU in diesem Repo: [3 Punkte]
> … Bitte NICHT erneut mit TASK_BLOCKED enden — dieser Scope ist vollständig lokal machbar."

Das ist strukturell genau das, was die Forschungsfrage mit "schärfer" meint: Diagnose zuerst
("bereits erledigt"), Scope explizit verkleinert, ein explizites Nicht-Ziel
(kein Host-Zugriff nötig), ein explizites Anti-Pattern-Verbot (nicht wieder blockieren).
AGT-2149 brauchte danach nur noch einen einzigen weiteren automatischen Reissue bis
`7-archive`.

Bemerkenswert: 37 Karten tragen **wortwörtlich denselben** Absatz
`## Steer-Runde (Operator-Abnahme-Welle, 24.07. nachmittags)` (nur Branchname variiert):

> „Auf dem Task-Branch (…) liegt Arbeit aus früheren Runden – NICHT neu anfangen: 1. Branch
> sichten; wenn die Basis alt ist, eigene Commits per cherry-pick auf aktuelles
> origin/develop neu aufsetzen. 2. Das LETZTE `code-review-grade-*.md` in diesem Ordner
> lesen; NUR die dort benannten Lücken schließen. 3. Implementierung + Tests + Evidenz
> COMMITTEN und pushen … 4. `[[TASK_DONE]]` erst wenn die Grade-Punkte adressiert sind."

Das ist faktisch schon ein von Hand erfundener, dann per Copy-Paste massenhaft angewendeter
Reissue-Prompt-Builder — nur eben nicht automatisiert, sondern manuell dupliziert. Er zeigt
in Reinform das Muster aus dem Katalog unten: Branch-Basis-Anweisung, Verweis auf ein
konkretes Evidenz-Artefakt (statt Inline-Wiederholung), explizite Scope-Klammer, explizites
Nicht-Ziel.

### 3. Zitierte Evidenz reicht nicht, wenn die Fehldiagnose bestehen bleibt (AGT-2168)

AGT-2168 (Completion-Judge) bekam zehn `steer-diff`-Reissues, jede mit wörtlich zitierten
Log-Zeilen aus dem Vorlauf — auf den ersten Blick "scharf". Trotzdem kreiste die Karte über
zwei Cluster (4 Reissues Mitte Juli, dann 11 Tage Pause, dann 6 weitere Reissues an einem
Nachmittag) um dasselbe Scheinproblem: eine historische, längst überholte Log-Zeile
`Application bundle generation failed` wurde vom Completion-Gate wiederholt als offener
Blocker interpretiert, obwohl sie laut Agent nur "Superseded history" war. Zitat aus Reissue
5 von 10:

> „- [ ] Status result is Success but build/test failure evidence was found: … `Application
> bundle generation failed` | Historical Angular output | Superseded history |"

Erst als die letzten Runden zusätzlich ein strukturiertes Schema forderten (`verdict`:
`complete`/`incomplete`/`blocked_external`/`inconclusive`, mit Pflichtfeldern
`requirementId`, `owner`, `reason`, `evidenceRefs`) löste sich die Schleife. Lehre: konkrete
Zitate allein sind kein Ersatz für eine korrekte Diagnose plus ein eindeutiges Zielformat für
die Antwort — sonst wird derselbe Missverständnis-Loop nur mit neuen Zeilennummern
wiederholt.

### 4. Das voluminöseste Template begräbt das Signal oft in Rauschen

Das `build-gate`-Template (285 von 996 Vorkommen, größte Einzelklasse) fügt den kompletten
`dotnet build`/`dotnet test`-Rohlog ein. Stichprobe AGT-2289 (2. Reissue): von 320 Zeilen
Gesamtlänge sind die ersten ~190 Zeilen NuGet-Restore-Meldungen und `CS0618`/`CS8604`-Warnungen,
die mit dem eigentlichen Fehler nichts zu tun haben — die erste echte `Error Message:` steht
erst in Zeile 192, die zusammenfassende `Failed! - Failed: 4, …`-Zeile in Zeile 307. Der
Agent muss also durchschnittlich über die Hälfte des Textes lesen, bevor das eigentliche
Problem sichtbar wird. Das Signal *ist* vorhanden (keine Kürzung vor dem Fehler, wie zuerst
vermutet und dann anhand des Volltexts widerlegt) — aber das Signal-Rausch-Verhältnis ist
schlecht, und genau diese Klasse hat mit die niedrigste Ein-Runden-Erfolgsquote (siehe
Befund 6).

### 5. Wörtliche Wiederholung ist selbst das deutlichste Non-Convergence-Signal

AGT-2255 bekam zwischen 02:47 und 10:57 Uhr (24.07.) **fünf identische**
`evidence-gate`-Reissues hintereinander — byte-identisch bis auf den Zeitstempel:

> „Auto-review will not accept this task on a bare success claim … This is a UI/bug task but
> the run produced no visual evidence: no screenshot or e2e capture under `results/` …"

Das System hat fünfmal dieselbe Forderung wiederholt, ohne die Formulierung zu verschärfen
oder zusätzlichen Kontext nachzulegen, während in `prompt.md` parallel bereits zwei
menschliche Steer-Runden mit wachsender Präzision lagen (siehe Befund 2). Erst ein sechster,
inhaltlich identischer Durchlauf brachte den Screenshot. Eine wörtliche Wiederholung der
eigenen letzten Nachricht ist operational der klarste beobachtbare Hinweis "das bisherige
Framing wirkt nicht" — und wäre technisch trivial zu erkennen (Hash-Vergleich mit dem
vorherigen Reissue-Text).

### 6. Fast jede Karte konvergiert irgendwann — Erfolg/Misserfolg ist die falsche Zielgröße

Von 440 Karten mit ≥1 Reissue landeten **434 (98,6 %)** letztlich bei `7-archive`. Nur 6
Karten nicht (4× `5e-escalated`, 2× `0-backlog`). Bei einer derart hohen Grundrate ist
"konvergiert / konvergiert nicht" statistisch fast wertlos als Zielgröße — die Pipeline ist
so gebaut, dass so gut wie immer weiter reissued wird, bis es klappt. Die eigentlich
interessante Größe ist **wie viele Runden/wie lange**, nicht **ob**. Genau das misst auch die
bereits laufende Kontrollstudie (Befund 8) über "restricted mean attempts", nicht über eine
binäre Erfolgsquote — konsistent mit diesem Korpusbefund.

Trotzdem liefert die Sequenz-Klassifikation einen Hinweis, welche Vorlagen tendenziell in
einer Runde erledigt sind ("Endquote" = Anteil der Vorkommen, die die letzte Runde vor
Archivierung waren):

| Vorlage | Vorkommen | Endquote | Sofort-Wiederholung |
|---|---:|---:|---:|
| `council-reaction` | 10 | 70,0 % | 30,0 % |
| `quality-concerns` | 15 | 60,0 % | 0,0 % |
| `steer-diff` (zitiert konkrete Zeilen) | 255 | 55,3 % | 30,6 % |
| `evidence-gate` (nennt exakten Beleg-Pfad) | 179 | 54,7 % | 24,0 % |
| `noop-recovery` | 8 | 37,5 % | 0,0 % |
| `build-gate` (Rohlog, Signal spät) | 285 | 37,2 % | 34,4 % |
| `aspect-block` (nennt nur Kategorie, keine Datei) | 187 | 31,6 % | 31,0 % |
| `needs-input-answer` | 57 | 29,8 % | 5,3 % |

Die beiden Vorlagen, die sowohl **was falsch ist** als auch **welcher Beleg/welches Format
den Fall schließt** konkret benennen (`steer-diff`, `evidence-gate`), lösen sich am
häufigsten in derselben Runde. `aspect-block` ist zwar kurz und nennt eine konkrete Kategorie
("documentation-impact … missing load-bearing docs"), aber ohne Datei-/Abschnittsverweis oder
Ziel-Artefakt — und schneidet trotz Kürze am schlechtesten ab. Kürze allein macht also keinen
Reissue-Prompt scharf; **Diagnose + Zielartefakt zusammen** scheinen der Hebel zu sein. Diese
Zahlen sind Korrelation über ungleich verteilte, nicht randomisierte Fälle (siehe Grenzen) —
kein kausaler Beleg.

### 7. Die einzigen Nicht-Konvergierer im Korpus hatten eine Infrastruktur-, keine Prompt-Ursache

Die 4 `5e-escalated`-Karten unter den 440 (AGT-2220, AGT-2242, AGT-2250, AGT-2302) tragen
alle exakt denselben `status.md`-Befund:

> „Category: review-subject-unmaterialisierbar — Reason: The immutable ReviewSubject has no
> persisted Result-Envelope and cannot be materialized."

Das ist derselbe technische Grund, aus dem auch AGT-2380 selbst (diese Karte) eskaliert
wurde — ein Materialisierungsfehler in der Review-Pipeline, nicht eine zu stumpfe
Reissue-Formulierung. Im vorliegenden Korpus gibt es also **keinen einzigen** belastbaren
Fall von "Reissue wurde probiert und ist an schlechtem Prompt-Text gescheitert" — jeder
beobachtbare Fehlschlag hat eine andere Ursache. Das ist eine wichtige Einschränkung für jede
kausale Interpretation der obigen Korrelationen (siehe Grenzen).

### 8. Es gibt bereits eine registrierte, randomisierte Studie dazu — bisher ohne Effekt nachweisbar

Unter `docs/quality/reissue-prompt-convergence/` und
`docs/quality/pipeline-time-economy/reissue-prompt-experiment.md` liegt bereits eine
methodisch deutlich strengere Vorarbeit (aus dem Vorläufer-Ticket AGT-2322, weitergeführt im
eigenen — inzwischen eskalierten — Lauf dieser Karte AGT-2380, per Salvage-Branch
`693dac523` bereits auf `develop`):

- **Observational Snapshot** (404 von 1375 Karten mit auswertbarem erstem Reissue,
  klassifiziert nach einer deterministischen Regel "≥1 Markdown-Listenpunkt mit
  Datei-/Pfadverweis oder explizitem Mangel-Begriff" = "finding-first" vs. "generic"):
  Finding-first: 202 Karten, 69 angenommen, RMST (restricted mean attempts) bis Runde 5 =
  3,594; Generic: 202 Karten, 84 angenommen, RMST = 3,752. Differenz −0,159 Runden zugunsten
  finding-first, 95-%-Bootstrap-Intervall **−0,530 … +0,194 (schließt Null ein)**. Annahme-
  Wahrscheinlichkeit bis Runde 5: 49,85 % vs. 47,75 %, Differenz +2,1 pp, 95-%-Intervall
  **−12,6 … +18,1 pp (schließt Null ein)**. Explizite Warnung im Dokument: Modell- und
  Task-Typ-Verteilung sind zwischen den Gruppen unausgeglichen (generic: 110×`gpt-5.6-sol`/56×
  `claude-opus-4-8`; finding-first: 49×`gpt-5.6-sol`/115×`claude-opus-4-8`) — ein Confounder,
  keine Adjustierung.
- **Laufendes randomisiertes Experiment** `finding-first-v1`: 50/50-Zuteilung per
  SHA-256-Hash auf Task-Key, Kontroll- vs. Behandlungs-Template, vorregistrierte
  Freigabe-Schwelle (≥30 Karten/Arm, Effekt ≥0,5 Runden, Bootstrap-Intervall vollständig unter
  Null, Gate-Regressions-Risiko ≤5 pp). Der eingecheckte Analyse-Stand
  (`reissue-prompt-experiment-analysis.md`) zeigt **0 Zuweisungen in beiden Armen** ("not
  estimable") — das Dokument selbst vermerkt, dass der Report vor der Produktionsfreigabe
  erzeugt wurde.

Kurz: Die Frage wird bereits mit der richtigen Methodik (zensur-bewusste
Survival-Analyse statt binärer Erfolgsquote, vorregistrierte Freigabeschwelle, randomisierte
Zuteilung) untersucht — nur eben noch ohne belastbares Ergebnis, weil (a) die
Beobachtungsstudie zu wenig Fälle/zu viel Streuung hat und (b) das randomisierte Experiment
noch keine produktionsseitigen Zuweisungen gesammelt hat. Diese Recherche widerspricht dem
Nullbefund nicht — die hier gefundenen Korrelationen (Befund 6) sind schwächer belastbar als
die dortige Survival-Analyse und decken zudem eine andere, breitere Grundgesamtheit ab (alle
8 Templates statt nur der `model-review-finding`-Familie).

## Muster-Katalog: Was einen guten Reissue-Prompt ausmacht

Als Checkliste für einen künftigen automatischen Reissue-Prompt-Builder, destilliert aus den
Fällen oben (v. a. dem manuell erfundenen "Operator-Abnahme-Welle"-Template und den
Endquoten aus Befund 6):

1. **Diagnose vor Anweisung.** Erst benennen, was konkret fehlgeschlagen ist (welcher Test,
   welcher Aspekt, welcher Beleg fehlt) — nicht nur "es hat nicht geklappt".
2. **Zielartefakt explizit, nicht nur Kategorie.** `aspect-block` nennt nur "missing
   load-bearing docs" (Kategorie) und schneidet schlecht ab; `evidence-gate` nennt den
   exakten erwarteten Pfad/Dateityp und schneidet besser ab. Immer sagen, welches Artefakt
   den Fall schließt.
3. **Auf Evidenz verweisen statt sie zu duplizieren.** Das Mensch-Template verweist auf "das
   LETZTE `code-review-grade-*.md`" statt es einzukopieren — spart Rauschen, bleibt aktuell
   auch wenn sich die Evidenz seither geändert hat (Gegenbeispiel: `build-gate` kopiert den
   gesamten Rohlog inline, Signal geht in ~190 Zeilen Restore-Rauschen unter, Befund 4).
4. **Branch-Basis-Anweisung, wenn relevant.** "Wenn die Basis alt ist, eigene Commits per
   cherry-pick auf aktuelles `origin/develop` neu aufsetzen" — verhindert stille Arbeit auf
   veraltetem Stand.
5. **Explizites Nicht-Ziel / Anti-Pattern-Verbot.** "NICHT neu anfangen", "Kein Neuentwurf",
   "Bitte NICHT erneut mit TASK_BLOCKED enden, dieser Scope ist vollständig machbar" — schärft
   durch Ausschluss, nicht nur durch Einschluss.
6. **Scope explizit verkleinern, wenn Teile bereits erledigt sind.** AGT-2149: "Die
   Infrastruktur ist BEREITS ERLEDIGT … Du brauchst KEINEN Zugriff auf den Runner-Host" —
   verhindert, dass der Agent Bereiche außerhalb seiner Reichweite erneut zu lösen versucht.
7. **Korrekte Diagnose vor jeder erneuten Zitierung sicherstellen.** Zitate aus dem Vorlauf
   sind nur hilfreich, wenn die zugrundeliegende Interpretation stimmt (Gegenbeispiel
   AGT-2168, Befund 3) — sonst lieber das Zielschema/-format verschärfen statt nur mehr vom
   selben Log zu zitieren.
8. **Nie denselben Text zweimal unverändert senden.** Ein Hash-Vergleich mit dem vorherigen
   Reissue an dieselbe Karte ist ein billiger, sehr aussagekräftiger Trigger: bei Treffer statt
   Wiederholung entweder eskalieren oder zusätzlichen/anderen Kontext nachlegen
   (Befund 5, AGT-2255).
9. **Klares Abschlusskriterium wiederholen.** Jede funktionierende Vorlage endet mit einer
   eindeutigen Regel, wann `[[TASK_DONE]]` erlaubt ist und wann `[[TASK_BLOCKED:<Grund>]]`
   die ehrliche Alternative ist.

## Grenzen

- **Kleine, nicht-randomisierte Stichprobe für die Tiefenlektüre.** 14 Karten wurden gezielt
  nach Reissue-Anzahl und Datenverfügbarkeit ausgewählt, nicht zufällig — die dort gezogenen
  Einzelfall-Lehren (Befunde 2, 3, 5) sind illustrativ, kein statistischer Beweis.
- **Der Korpus-Zensus (996 Reissues, Befund 1 und 6) ist vollständig, aber die
  Endquoten-Tabelle in Befund 6 ist reine Korrelation über konfundierte, nicht randomisierte
  Fälle.** Welche Vorlage ausgelöst wird, hängt vom auslösenden Gate ab, das wiederum mit
  Task-Typ, Komplexität und Modell korreliert (siehe nächster Punkt) — ein sauberer
  Kausalschluss "Vorlage X verursacht schnellere Konvergenz" ist daraus nicht ableitbar.
- **Modell ist über Karten hinweg konfundiert, innerhalb einer Karte meist nicht.** Stichprobe
  aus `model-qualification.jsonl` (AGT-2108, AGT-2168, AGT-2169, AGT-2197): das Modell bleibt
  pro Karte i. d. R. stabil (Task-Override auf `gpt-5.6-sol`), unabhängig vom empfohlenen
  Modell der Qualifikations-Heuristik. Cross-Card variiert das Modell aber stark, und die
  bereits vorhandene Kontrollstudie (Befund 8) weist ein deutliches Modell-Ungleichgewicht
  zwischen "generic" und "finding-first" nach (`gpt-5.6-sol`-lastig vs.
  `claude-opus-4-8`-lastig). Ein Effekt, der wie "schärferer Prompt" aussieht, kann teilweise
  ein Modell-Effekt sein. Bei AGT-2169 war der erste Versuch zudem technisch unterbrochen
  (`status: stopped`, 0 Token) — eine Infrastruktur-Störung, kein echter erfolgloser
  Reissue-Zyklus; solche Fälle sind leicht mit "Prompt hat nicht gewirkt" zu verwechseln.
- **Erfolg ist keine brauchbare Zielgröße (Befund 6), aber diese Recherche hat keine eigene
  Zeit-/Runden-Regression gerechnet** — das leistet bereits die zensur-bewusste
  Survival-Analyse aus Befund 8 (RMST, Kaplan-Meier), die hier nur zitiert, nicht repliziert
  wurde, um Doppelarbeit zu vermeiden.
- **Alle beobachtbaren Nicht-Konvergierer (6 von 440) haben eine andere Ursache als
  Prompt-Qualität** (Befund 7) — das bedeutet nicht, dass Prompt-Qualität irrelevant ist,
  sondern nur, dass dieser Korpus keinen Fall enthält, an dem sich "Prompt zu stumpf → Karte
  gescheitert" kausal zeigen ließe.
- **Statische Dateien, kein Live-API-Zugriff.** Bis auf den initialen Abruf der Karte AGT-2380
  selbst wurde ausschließlich der Datei-Snapshot unter `agent-taskboard-workspace` gelesen,
  wie im Auftrag vorgegeben; `previousAttempts` auf der Karte ist laut Doku der
  Kontrollstudie ohnehin auf 10 gedeckelt und wurde hier nicht als Zähl-Quelle verwendet.
- **Die eigene Klassifikationsregel (Präfix der ersten Zeile, 8 Klassen) ist eigenständig und
  nicht identisch mit der Klassifikationsregel der Kontrollstudie** ("≥1 Markdown-Listenpunkt
  mit Datei-/Mangel-Begriff"). Beide sind deterministisch und textbasiert, messen aber nicht
  exakt dasselbe; die beiden Ergebnisse sind komplementär, nicht direkt vergleichbar.

## Empfehlung

**Keinen neuen automatischen Reissue-Prompt-Builder von Null aufsetzen — die Infrastruktur
dafür existiert bereits** (`finding-first-v1`, randomisiert, vorregistrierte Freigabeschwelle,
siehe Befund 8). Konkret:

1. **Laufen lassen, nicht neu bauen.** Das randomisierte Experiment sammelt noch keine
   Produktions-Zuweisungen (0/0 im eingecheckten Stand). Vor jeder Entscheidung erst prüfen,
   ob inzwischen genug Fälle vorliegen (`node scripts/reissue-prompt-experiment-analysis.mjs`
   erneut laufen lassen) — das ist außerhalb des Scopes dieser Recherche (nur `docs/`,
   keine Skript-Ausführung mit Seiteneffekten), aber der naheliegende nächste Schritt.
2. **Scope der Kontrollstudie erweitern.** Sie deckt nur `model-review-finding`-Reissues ab.
   Der Korpus-Zensus zeigt, dass `build-gate` (285) und `aspect-block` (187) mengenmäßig
   größer sind und laut Befund 6 die schwächsten Endquoten haben — dort liegt der größere
   Hebel. Für `build-gate` speziell: Rohlog nicht mehr komplett inline einfügen, sondern nur
   die erste `error`/`FAIL`-Zeile plus wenige Kontextzeilen (siehe Muster 3).
3. **Den Muster-Katalog oben als Bauplan für die `treatment`-Vorlage nutzen**, nicht als neue
   Parallelinitiative — er ist aus genau den Fällen destilliert, die die Kontrollstudie als
   Grundgesamtheit nutzt, und deckt sich mit deren eigener Beschreibung des
   `treatment`-Templates ("ein nummerierter Punkt pro offenem Finding mit Mangel, Referenz,
   nötiger Änderung, Verifikation").
4. **Den Wiederholungs-Check aus Muster 8 unabhängig vom Experiment sofort umsetzen** — er
   ist trivial (Hash-Vergleich des Reissue-Texts mit dem letzten an dieselbe Karte gesendeten)
   und adressiert einen konkret beobachteten, teuren Fehlmodus (AGT-2255: 5 identische
   Runden), unabhängig davon, welche Vorlage am Ende gewinnt.

## Die 5 wichtigsten Befunde

1. Reissue-Prompts sind im Ist-Zustand keine frei formulierte Prosa, sondern eine von acht
   festen Vorlagen, deren Auswahl der auslösende Gate-Typ bestimmt — "schärfer machen" heißt
   in der Praxis "mehr konkreten Inhalt in die Vorlage einsetzen", nicht "besser formulieren".
2. Die einzige echte Instanz eines von Menschen erfundenen "scharfen" Reissue-Prompts wurde
   händisch auf 37 Karten dupliziert und enthält exakt die Muster, die die Analyse als
   wirksam identifiziert: Diagnose zuerst, Verweis auf ein konkretes Evidenz-Artefakt statt
   Inline-Duplikation, Branch-Basis-Anweisung, explizites Nicht-Ziel.
3. Zitierte Evidenz und Kürze allein reichen nicht: `aspect-block` ist kurz und trotzdem die
   schwächste Vorlage (31,6 % Endquote), weil sie nur eine Kategorie statt eines
   Zielartefakts nennt; `steer-diff` zitiert Evidenz, kann aber trotzdem 10 Runden um dieselbe
   Fehldiagnose kreisen (AGT-2168), wenn die Interpretation dahinter falsch bleibt.
4. Fast jede Karte (98,6 %) konvergiert irgendwann — Erfolg/Misserfolg ist als Zielgröße fast
   wertlos, und alle 6 Nicht-Konvergierer im Korpus scheiterten an derselben
   Infrastruktur-Ursache (Review-Materialisierung), nicht an Prompt-Qualität.
5. Es existiert bereits eine methodisch stärkere, randomisierte, vorregistrierte Studie zu
   genau dieser Frage (`finding-first-v1`); ihr aktueller Stand zeigt einen schwachen,
   statistisch nicht gesicherten Vorteil für konkrete Findings-first-Prompts (RMST-Differenz
   −0,159 Runden, 95-%-Intervall schließt Null ein) bei nachgewiesenem Modell-/Task-Typ-
   Ungleichgewicht zwischen den Gruppen.

**Empfehlung in drei Sätzen:** Keinen neuen automatischen Reissue-Prompt-Builder von Null
bauen, sondern das bereits laufende, korrekt konzipierte `finding-first-v1`-Experiment
weiterlaufen lassen, bis genug Produktions-Fälle für eine belastbare Aussage vorliegen. Den
hier destillierten Muster-Katalog (Diagnose vor Anweisung, Zielartefakt statt Kategorie,
Verweis statt Duplikation, explizite Nicht-Ziele, Wiederholungs-Check) als Bauplan für dessen
`treatment`-Vorlage verwenden und auf die mengenmäßig größeren, bisher nicht abgedeckten
Ursachenfamilien `build-gate` und `aspect-block` ausweiten. Den Wiederholungs-Check (nie
denselben Reissue-Text zweimal unverändert senden) unabhängig davon sofort umsetzen, weil er
trivial ist und einen konkret beobachteten teuren Fehlmodus behebt.
