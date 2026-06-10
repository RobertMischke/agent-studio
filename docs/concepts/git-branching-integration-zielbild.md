# Git-Integration: Zielbild, Verhalten & offene Fragen

> **Status: ENTWURF zur Review** (Operator-Entscheidungen in §7 offen).
> Kontext: Epic ASS-1720 (Lane-Rename + Merge-into-Develop), Provenienz-Slice ASS-1724,
> Messung vom 2026-06-10 (§6). Verwandt: [structure-target](../architecture/structure-target.md),
> [STYLEGUIDE](../architecture/STYLEGUIDE.md).
> Leitfrage: *Wo liegt die Arbeit eines Tasks zu jedem Zeitpunkt — und wann wird sie „Wahrheit"?*

---

## 1. Das Zielbild in einem Bild

```
   ARBEIT                 SCHNELLE INTEGRATION            ÜBERGANG (= Abnahme)         RELEASE
┌─────────────┐    auto    ┌──────────────────┐  gated     ┌──────────────────┐  manuell ┌────────┐
│ task/<id>   │ ─────────► │  develop-local   │ ─────────► │  develop         │ ───────► │  main  │
│ (Worktree)  │  Run-Ende  │  (lokal, NIE     │  direct    │  (zentral,       │  Release │  + Tag │
│ 1 Task =    │            │   gepusht,       │  ODER MR   │   origin,        │          │        │
│ 1 Branch    │            │   rekonstruier-  │            │   kuratiert =    │          │        │
│             │            │   bar)           │            │   reviewed-only) │          │        │
└─────────────┘            └──────────────────┘            └──────────────────┘          └────────┘
      ▲                            │
      │     Nachbessern            │   Folge-/Ketten-Tasks branchen
      └────────────────────────────┘   von develop-local (keine Review-Latenz)
        Folge-Run resumed den
        Task-Branch — KEIN Revert
```

**Drei Stufen, drei Wahrheiten:**

1. **`task/<id>`** — die Arbeit. Ein Task = ein Branch = ein Worktree. Wird an
   Lane-Übergängen nach origin gepusht (Durability).
2. **`develop-local`** — der schnelle Arbeitsstrom. Run-Ende ⇒ sofortige Integration.
   Darf „schmutzig" sein (ungereviewt, Zwischenstände). **Wird nie gepusht** und ist
   jederzeit rekonstruierbar: `develop-local = develop + Σ(offene task-Branches)`.
3. **`develop`** — die kuratierte Wahrheit. Erreicht ein Task sie, ist er abgenommen.
   Nur von hier wird deployt/released.

---

## 2. Branch-Rollen

| Branch | Wer schreibt | Wann | Lebensdauer | Push? |
|---|---|---|---|---|
| `task/<id>` | Agent-Run (+ Folge-Runs) | während der Bearbeitung | bis Abnahme, dann prune | ja, an Lane-Übergängen |
| `develop-local` | Runner (automatisch) | am Run-Ende jedes Tasks | **wegwerfbar** — wird periodisch aus develop + offenen Branches neu aufgebaut | **nie** |
| `develop` | Übergangs-Step (direct) **oder** MR-Merge der Git-Plattform | bei Abnahme | dauerhaft | ja |
| `main` | Operator/Release-Prozess | Release | dauerhaft | ja (+ Tag) |

**Eiserne Regeln:**
- Die **MR-/Abnahme-Einheit ist der Task-Branch**, niemals develop-local.
  (Sonst wird develop-local ein zweiter „Friedhof" mit untrennbar vermischter Arbeit.)
- Commit-Mengen werden **graph-basiert** bestimmt (`task/<id>` ahead of merge-base),
  nie über Zeitfenster (→ ASS-1724, Landed-State-Leiter).
- Reject existiert nicht als Revert: **Nachbessern = Folge-Run auf demselben Branch.**

---

## 3. Unterschiedliche Projekte, unterschiedliche Anforderungen

Integration ist **pro Projekt konfigurierbar** (`integrationProfile` in den Projekt-Settings).
Der MR-Prozess ist **optional** — eine Stufe im Profil, kein Zwang.

```
                         Tier 1                Tier 2 (Übergang)
Profil                   develop-local?        develop erreicht via …       typisch für
─────────────────────────────────────────────────────────────────────────────────────────
direct                   nein                  direkter Merge am Run-Ende   Prototypen, Solo-
(heutiges Verhalten)                           (= heute)                    Spielwiese, Demos

local-then-direct        ja                    direkter Merge bei Abnahme   Solo-Dogfooding mit
                                               (operator-getriggerter       Qualitätsanspruch
                                               Merge-Step, ASS-1721)        (Agent Studio selbst)

local-then-mr            ja                    MR/PR auf der Git-Plattform  Teams mit GitHub/
                                               (Produkt ÖFFNET den MR,      GitLab-Workflow,
                                               Review/Checks/Merge-Queue    Branch-Protection,
                                               laufen im Team-Tooling)      CODEOWNERS
```

**Warum MR optional sein muss:** Teams haben existierende Git-Workflows (Branch-Protection,
Pflicht-Reviews, CI-Checks, Merge-Queues). Das Produkt darf daran nicht vorbeipushen —
es **erzeugt** den MR (Titel/Beschreibung aus dem Task, Provenienz verlinkt) und
orchestriert ihn. Solo-Nutzer brauchen den Overhead nicht → `direct`-Profile.

---

## 4. Verhalten an den Übergängen (Doku)

```
Lane:        2-ready ──► 3-progress ──────► Review-Lane ─────────► completed
                          │       │            │                       │
Git-Aktion:   branch+      │     Run-Ende:     │   Abnahme:            │  prune
              worktree     │     merge nach    │   direct-merge ODER   │  task/<id>,
              von          │     develop-local │   MR öffnen → Team    │  develop-local
              develop-local│     + push        │   merged → develop    │  rebuild
                           │     task/<id>     │                       │
              Nachbessern: ◄─────────────────────  C/D-Grade / Review-Findings:
              Folge-Run resumed task/<id>, iteriert, develop-local aktualisiert
```

| Ereignis | Verhalten |
|---|---|
| **Run-Ende** | `task/<id>` → develop-local mergen (no-ff, 1 Merge = 1 Task-Landing); Branch nach origin pushen; Build/Auto-Review laufen **gegen develop-local** (Integrationsfehler früh) |
| **Reissue/Folge-Run** | resumed denselben `task/<id>`; develop-local erhält den neuen Stand. **Kein zweites unabhängiges Landing** (heutiger Doppel-Landing-Bug entfällt strukturell) |
| **Abnahme** | je Profil: direkter Merge `task/<id>` → develop **oder** MR öffnen; nach Merge: Branch prune, develop-local rebuild |
| **Konflikt bei Tier-1** | früh sichtbar (am Run-Ende, nicht erst beim MR); Task → Eskalation mit Konflikt-Dateien am Task sichtbar |
| **Kette (B baut auf A)** | B brancht von develop-local (enthält A). Abnahme **in Abhängigkeits-Reihenfolge** (§7.2) |
| **Deploy/Release** | stable/Release nur von **develop** (= reviewed) — nie von develop-local |

---

## 5. Provenienz: Wo ist meine Arbeit gerade?

Jeder Task zeigt seine **Landed-State-Leiter** (ASS-1724):

```
 ○──────────○──────────────○─────────────○──────────○
 branch     develop-local  MR offen      develop    main
 (Worktree) (integriert)   (nur Profil   (abgenommen)(released)
                            local-then-mr)
```

An jedem Lane-Übergang werden Anker gespeichert (branch-tip, develop-HEAD, Merge-Commit),
sodass die Frage *„Ist das schon im Develop?"* aus dem Graphen beantwortet wird — nicht geraten.

---

## 6. Warum dieser Schnitt? (Messung 2026-06-10)

Analyse der letzten **40 Task-Landings** (≈36 h Parallelbetrieb, max=3):

| Messwert | Ergebnis | Konsequenz fürs Design |
|---|---|---|
| Paare mit File-Overlap | **3 %** (25/780), je 1–3 Dateien | Merge-Kosten am Übergang sind KLEIN — MRs fast immer konfliktfrei |
| Ketten-Iterationen (Task baut auf Vorgänger) | ~4–6 von 15 Nacht-Landings | Ketten sind der echte Kostentreiber → brauchen develop-local, nicht Review-Latenz |
| Doppel-Landings durch Reissues | mehrere ×2–×3 | Reissue darf kein neues Landing erzeugen → Folge-Run auf demselben Branch |
| Hot-Files | wenige (ReviewDecisionOrchestrator, project-shell, conversation-view) | Admission-Policy serialisiert überlappende Scopes weiterhin |

---

## 7. Kritische Fragestellungen (OFFEN — Operator-Entscheidung)

**7.1 Was gate't den Übergang Tier-1 → Tier-2?**
Optionen: (a) nur Operator-Klick · (b) Auto-Review-Grade (A/B ⇒ automatisch, C/D ⇒ hold
zum Nachbessern) · (c) Human-Review zwingend.
*Tendenz: (b) mit Operator-Override — hält Ketten flüssig, hält Schrott von develop fern.*

**7.2 Ketten bei der Abnahme**
B enthält A's Diff, solange A nicht abgenommen ist. Optionen: (a) Abnahme serialisieren
(in Abhängigkeits-Reihenfolge — einfach, erste Stufe) · (b) stacked MRs (Base = Vorgänger-
Branch; GitHub kann Retarget) · (c) Squash-Promotion nur des B-Deltas (fragil).
*Tendenz: (a) zuerst, (b) später.*

**7.3 develop-local-Hygiene**
Wann rebuild? (a) nach jeder Abnahme · (b) nightly · (c) bei Konflikt/Drift-Erkennung.
Was passiert mit Zwischenständen, die NIE abgenommen werden (verworfene Tasks)?
*Tendenz: (a)+(c); verworfene Branches fallen beim Rebuild automatisch raus.*

**7.4 MR-Provider-Abstraktion**
GitHub PR zuerst (origin = GitHub). Interface so schneiden, dass GitLab/Gitea/Azure später
andocken. Wie viel MR-Status zurück ins Board spiegeln (Checks, Review-Status, Merge-Konflikt)?

**7.5 Multi-Seat / Multi-System (Slice D, SPÄTER)**
Heute: 1 Maschine ⇒ 1 develop-local. Mehrere Seats: develop-local pro Seat? Lease-basierte
Integration? *Bewusst vertagt — Design darf es nicht verbauen (develop-local rekonstruierbar
⇒ pro Seat trivial möglich).*

**7.6 Migration vom IST**
Heute merged der Runner am Run-Ende DIREKT auf develop (Profil „direct", linear, ohne
Merge-Commits). Schrittfolge: (1) no-ff-Landings + Doppel-Landing-Fix · (2) develop-local
einführen, zentral-develop-Merge an die Abnahme hängen (Profil local-then-direct) ·
(3) MR-Provider (Profil local-then-mr). Jeder Schritt einzeln deploybar, Endpoints fest.

---

## 8. Slice-Plan (Umsetzungsreihenfolge)

| # | Slice | hängt an |
|---|---|---|
| 1 | no-ff Task-Landings + Reissue-Doppel-Landing-Fix | — |
| 2 | `develop-local` einführen (Tier 1) + Rebuild-Mechanik | 1 |
| 3 | Abnahme-Merge auf zentral develop (Profil `local-then-direct`, nutzt ASS-1721-Step) | 2 |
| 4 | Integrations-Profil in Projekt-Settings + UI | 3 |
| 5 | Provenienz-Leiter um develop-local/MR-Stufe erweitern (ASS-1724-Anschluss) | 2 |
| 6 | MR-Provider GitHub (Profil `local-then-mr`): MR öffnen, Status spiegeln, Merge erkennen | 3, 4 |
| 7 | Ketten-Abnahme serialisieren (7.2a) | 3 |

---

*Dieses Dokument ist Zielbild + Verhaltens-Doku + offene Fragen in einem. Änderungen am
Konzept bitte HIER nachziehen (Wiki ist die Quelle), nicht nur in Task-Prompts.*
