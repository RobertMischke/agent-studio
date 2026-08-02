# Migrationsplan: Projekt-Settings in die zentrale Konfigurationsinstanz (AGT-2230 Schritt 2)

**Stand:** 28.07.2026 · Kompaktfassung (Vollfassung im Sitzungsprotokoll des Umsetzungs-Marathons)

## Kernentscheidungen

1. **Der Runner liest heute keine einzige Projekt-Einstellung** (0 Treffer) — alles Nötige wird server-seitig in Claim (`RunSpecDto`) und Review-Plan projiziert. Die Migration ist ein **Wahrheitsort-Problem im Backend**, kein Verteilungsproblem.
2. **Vorbedingung Identitätsbrücke (S1):** `project-settings.json` ist nach **Anzeigename** verschlüsselt (Alias-Kette, Rekey) — erst auf `PROJ-NNN` umschlüsseln, dann zentralisieren.
3. **Zielschema aus zwei vorhandenen Mustern** (task-server): `flow_definitions` (per-Projekt-PK, FK, Version) + `runtime_capacity_settings` (ExpectedVersion-Konflikt 409, Audit in derselben Transaktion). Tabelle `project_settings(project_id, family, payload_json, payload_sha, version, updated_at)`.
4. **Sechs Familien** (A BuildProfile, B Pipeline-Steps, C TestExecution, D Integration, E Runner/Execution, F Sonstiges) — **Wahrheitswechsel je Familie**, Reihenfolge nach Blast-Radius: F → B → C → A → D → E.
5. **Nicht-Migrations-Regel:** Was über den Claim fließt (CliModes, EpicPlanning-Modelle, Build-Kommandos, IntegrationBranch-für-Runner), wird zentral gespeichert/editiert, aber **nie** als Lese-API für den Runner geöffnet — sonst zweiter Auflöser = Drift (execution-model-shift §5).
6. **Konflikt Datei vs. zentral:** zentral gewinnt; Datei-Edit wird nach `.metadata/project-settings.rejected/` gesichert + gemeldet; Übernahme NUR per explizitem `adopt-file`-Endpoint (Operator-Aktion, kein Config-Schalter — ADR-0042-Muster).
7. **Nicht migriert:** Registry-Pfade (host-abhängig, ADR-0042), `RunnerMode` (Live-Spiegel), `ExecutionRunner` (Legacy, fällt), BuildProfile-Validierungs**befund** (→ per-Host-Zeile, Frage E-2), LocalAppData-Fallback (ersatzlos).

## Phasen

P0 Identität → P1 Schema+API ohne Leser → P2 Dual-Write (Datei bleibt Wahrheit) → P3 Drift-Sensorik (72h drift-frei als Gate) → P4 Wahrheitswechsel je Familie (Read-through zentral, Fallback Datei) → P5 Single-Write (Datei = generierter Export) → P6 Aufräumen + ADR-0068.

## Tranchen

| ID | Inhalt | Größe | Form |
|---|---|---|---|
| V0 | RuntimeCapacitySettingsService anschließen ODER löschen (Vorbedingung, Vorlage-1a) | S | Karte |
| S1 | Identitätsbrücke PROJ-NNN | M | Karte |
| S2 | Schema+Contracts+Routen+Audit ohne Leser | M | koordiniert |
| S3 | Dual-Write best-effort | M | Karte |
| S4 | Drift-Sensorik + rejected-Ablage + adopt-file + UI-Konfliktkarte | M | Karte |
| S5 | Wechsel F+B | M | Karte |
| S6 | Wechsel C+A | L | koordiniert |
| S7 | Wechsel D+E | L | koordiniert |
| S8 | Single-Write, Export-Degradierung, Guard-Test, ADR-0068 | M | koordiniert |

Rollback je Wechsel: Leser zurück auf Datei (bis S8 immer aktueller Export).

## Fragen an Robert

- **E-1 (blockiert S2):** Zentrale Instanz wörtlich im task-server (SQLite) oder reicht „zentral über Backend-/api/v1"? Nur S2 hängt am Ort.
- **E-2:** BuildProfile-Validierungsbefund pro Host (`project_build_validations`) oder global am Profil?
- **E-3:** Workspace-Tier (ADR-0061) in S5 mitziehen? (Empfehlung: ja.)
- **E-4:** Datei nach S8 als Export behalten (empfohlen) oder abschalten?

## Definition of Done

Ein Wahrheitsort je Familie (Guard-Test), Audit je Mutation, 409 statt still überschreiben, Datei-Edits erkannt/gesichert/adoptierbar, keine Host-Pfade zentral, Runner weiterhin 0 Lesestellen, ADR-0068 verlinkt.
