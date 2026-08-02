# Entscheidungsvorlagen: AGT-2230 Re-Cut · AGT-2132 Epic-Buchhaltung

**Stand:** 2026-07-28 · erstellt aus Code-/Board-Analyse (read-only), Evidenz je Punkt geprüft.

---

## Vorlage 1 — AGT-2230 Re-Cut („Task-Server wird zentrale Konfigurationsinstanz")

### Ist-Befund (Kurzfassung)

- **Schritt 1 (Host-Slots): geliefert, aber am falschen Ort UND dupliziert.** `d3b54d191`+`f7b18ca22` liefern HostCapacityPolicy/ClientIdentity-Kapazität/Admission — im **Backend-Monolithen**. Gleichzeitig existiert im eigenständigen task-server ein **verwaister zweiter Kapazitäts-Store**: `task-server/RuntimeCapacitySettingsService.cs` (SQLite, versioniert, auditiert, Routen `GET/PUT /{hostId}/runtime-capacity`, aus WIP-Salvage `90a94b8b4`) — wird von keiner Stelle in backend/ oder runner/ benutzt. Das Frontend (runtime-capacity-editor) kennt den version>=1-Pfad allerdings bereits.
- **„Erlaubte Projekte"/„Fähigkeiten": offen.** Projekt→Host steckt projektseitig (`ProjectSettings.ExecutionRunner`); Capability-Advertising ist im CAR-Plan §4 als Kanarien-Mechanismus verplant.
- **Schritt 2 (Projekt-Settings): komplett offen** — `project-settings.json` je Task-Repo, unverändert.
- **Schritt 3 (Identitäten/Defaults/Budgets): im Wesentlichen erledigt** (ClientIdentity Default-CLI/Modell/Thinking + TokenBudgetMonthly, zentral via /api/clients/*), lebt im Backend statt task-server.
- **Schritt 4 (Modell-/CLI-Defaults):** Auflösungsreihenfolge via AGT-2245 geliefert (ModelRoutingPolicyRegistry, einkompiliert); der Leitungstransport ist als CAR-Plan §T0b/AP3 verplant (läuft bereits).

### Empfehlung

1. Schritt 1 als „geliefert, aber dupliziert" verbuchen: task-server-Store anschließen ODER als Alt-Salvage löschen.
2. Schritt 4 aus 2230 herausnehmen (gehört zur CAR-Kette, T0b/AP3).
3. „Fähigkeiten" an die CAR-Kette abgeben (§4) — kein zweiter Capability-Mechanismus.
4. Re-Cut: (a) Duplikat auflösen, (b) „erlaubte Projekte" host-seitig heben (klein), (c) Projekt-Settings-Migration als eigentliche Restarbeit.
5. Schritt 3 abhaken; Nachtrag nur Audit-Zeitstempel für Default-Änderungen.

### Fragen an Robert

1. `RuntimeCapacitySettingsService` im task-server **weiterbauen** (echte Zielbild-Migration) oder **löschen** (verwaiste Salvage)?
2. Zielbild wörtlich („liegt im Task-Server") oder reicht „zentral über Backend-API" als Erfüllung?
3. 2230 splitten in Sofort-Karte (Duplikat + erlaubte Projekte) und Folgekarte (Projekt-Settings-Migration)?

---

## Vorlage 2 — AGT-2132 Epic-Buchhaltung („Release & Deployment First-Class")

### Ist-Befund (Kurzfassung)

Alle 6 referenzierten Sub-Karten existieren in `7-archive`:

| Key | Ergebnis |
|---|---|
| AGT-2119 | geliefert (Phantom-Rescue → develop @ a0d48186) |
| AGT-2097 | teilgeliefert: Konzept ja; die Deployment-UI existiert real (`project-deployment-panel`, `deployment-definition-editor`) — aber über anderen Liefer-Pfad |
| AGT-2090 | geliefert (Audit-False-Positive, Commit war Ancestor) |
| AGT-2109 | geliefert (Push-Doctrine + Diffs, grade-b) |
| AGT-2105 | **unklar** — einzige Karte ohne 6-completed, direkt 5-human-review → 7-archive |
| WEB-10 | geliefert (grade-c, volle Verifikation) |

Zusätzlich seit 27./28.07. faktisch geliefert: Container-Stack + Installer (`90da8f022` ff.), BuildIdentity/Build-Manifest (`backend/Features/Runtime/BuildIdentity.cs` inkl. CAR/Chat-Artefaktzeilen). Der einzige offene Faden — gepaarte Versions-Pins + Manifest-Validierung — ist in der CAR-Kette (T4/AGT-2373, Plan §6) bereits verplant.

### Empfehlung

1. 2119/2090/2109/WEB-10 als erledigt streichen.
2. 2097 differenziert schließen (Referenz auf den echten Liefer-Pfad korrigieren).
3. AGT-2105 vor dem Schließen klären (verloren vs. anderweitig geliefert).
4. „Gepaarte Releases" der CAR-Kette (T4) zuordnen, nicht dem Epic.
5. **Epic AGT-2132 schließen** — kein eigenständiger Restscope, der nicht anderswo verplant ist.

### Fragen an Robert

1. Zählt `project-deployment-panel`/`deployment-definition-editor` als die gemeinte „Deployment-Seite"?
2. AGT-2105: als abgebrochen dokumentieren oder kurz recherchieren?
3. „Gepaarte Releases" unter 2132 (Epic bleibt offen) oder vollständig unter 2373 (Epic schließen)?
