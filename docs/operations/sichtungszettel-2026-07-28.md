# Sichtungszettel Human-Review · 28.07.2026

Tiefenprüfung aller wartenden Karten (read-only, Branch-Diffs + Evidenz gelesen). Vollbegründungen im Sitzungsprotokoll.

## Systembefunde (betreffen mehrere Karten)

1. **Attributionskette gebrochen:** 4 von 5 Frontend/Docs-Karten zeigen `no-branch` + leere `commits[]`, obwohl echte Arbeit auf `origin/runner/agent-runner-01/<KEY>` liegt; AGT-2242 trägt sogar FREMDE Commits → zwei inhaltlich wertlose Grade-D-Reviews („reviewed commits do not contain the implementation"). Tags `grade-d`/`concerns` auf 2242 sind **Falschsignale**.
2. **Zielbranch falsch:** 2386/2387/2389/2305 sind auf **main-Linie** gebaut (mergen clean nach main, kollidieren gegen develop), Karten sagen `integrationBranch: develop`. 2242 ist develop-basiert.
3. **„escalate" auf 2386/2387 war der heute gefixte Boot-Backfill-Bug** — kein Qualitätsurteil. Alle „Pass"-Grades sind Baseline-Vergleiche, kein Scoped-Diff-Review.

→ **Lohnendste Folge-Karte:** Runner-Salvage muss `commits[]` füllen + `integrationBranch` der Realität angleichen (als AGT-Karte angelegt).

## Voten

| Karte | Votum | Kern |
|---|---|---|
| **AGT-2250** | **Accept** | Sauberer Lückenschluss (FindStep über alle Kataloge), stärkste Evidenz im Feld, merged clean nach develop. |
| **AGT-2384** | **Accept + Folgekarte** | Offload sauber, exakt die vorgesehene Fortsetzung des Gate-Commits. Folgekarte: (a) Accept-Warnung entrauschen (feuert jetzt bei JEDEM Accept), (b) Backstop auch für lokale Deliveries ohne review-subject.json, (c) Worker-Drain-Test. |
| **AGT-2383** | **Steer** | Kompaktierung konzeptionell richtig, ABER: mit 14-Tage-Retention archiviert die Migration **null** Records (alle 12k terminalen Attempts sind jünger — gemessen!), und `GetRun`/`GetReview`-Miss lädt ALLE Archive **im _gate-Lock** auf dem Lease-/Report-Pfad. Steer-Text im Zettel. |
| **AGT-2220** | **Steer** (steht real in 4-auto-review, post-processing hängt >3.5h) | Inhaltlich richtig, aber: `IsVisibleDeliveryFailure` routet pauschal JEDES Blocked nach 5e (ungefragte Systemsemantik-Änderung), Branch konfliktet gegen develop/main, kein einziges Pass-Grade. |
| **AGT-2242** | **Accept + Folgekarte** | Feature ist längst in develop; Rest-Commit (Fixture+CSS) clean. Zählung korrekt attribuiert, Remote-Ingest doppelzählungssicher (E2E-belegt). Folgekarte: commits[]-Reparatur + Falsch-Tags entfernen + Lifecycle-Hänger. |
| **AGT-2386** | **Accept + Folgekarte** | Backend additiv sauber; Wert des Frontend-Teils = ~12 Zeilen engineLabel. ACHTUNG: Branch trägt eine Kopie von overview-runs, die develops Fassung MINUS Kommentare ist — beim main→develop-Merge darf NUR das engineLabel-Delta übernommen werden. |
| **AGT-2387** | **Accept** | Vorbildlich eng: Guard normalisiert, zieht eigenen Block vor Vergleich ab (Zähler wächst korrekt), OrchestratorSteered-Event, alle 10 Aufrufstellen, fails-open dokumentiert. |
| **AGT-2389** | **Accept + Folgekarte** | Community-Dateien gut (7 saubere Commits). VOR Außenwirkung: README-Quickstart widerspricht frontal AGT-2305 (beschreibt exakt den Adoption-Blocker), toter Release-Badge, Versionswirrwarr 0.1.0/1.0.0/0.0.0, CoC-Kontakt via Security-Flow, Topics ignorieren die Marketing-Kanonliste. |
| **AGT-2305** | **Accept + Folgekarte** (Steer vertretbar) | Echte End-to-End-VM-Verifikation (Nested-KVM, 5 ehrliche Fehl-Boots, dann grün). ABER: VM-Harness nie committet (nicht reproduzierbar!) und `docker-compose.yml` bewirbt weiter den konkurrierenden install.sh-Pfad („GENAU EIN Weg" verletzt). Folgekarte: Harness committen + Einstiegs-Pfad auflösen + Disk-Zahl + docs/ in .dockerignore + Smoke in CI. |
| **AGT-2200** | (Epic-Planning-Ergebnis — gesondert sichten) | — |
| **AGT-2301** | (Pass; entsperrt Konzept-Klasse) | — |
| **CAR-9 / CAR-10** | (CAR 0.7.0-Vorleistung, Pass) | — |

**Empfohlene Sichtungsreihenfolge:** 2250 → 2387 → 2384 → 2386 → 2389 → 2242 → 2305 → 2383(Steer) → 2220(erst Hänger prüfen).
