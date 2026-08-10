# Entscheidungsvorlage: Marketing-Studio — die nächsten Stufen

**Stand:** 28.07.2026 · read-only erhoben (Karten-API, agent-studio-marketing, Studio-Wiki) · Nadelöhr: **MKT-4-GO**

## 1. Ist-Stand Stufe 1

- **Wiki-Doc** `docs/operations/marketing-studio/index.html` existiert (6 Ausbaustufen) - **Defekt: kein workbench.json**, taucht in der Dossier-Liste nicht auf (Ein-Datei-Fix).
- **Marketing Cockpit als lauffähige App** (`agent-studio-marketing/app`, Angular 21 + ASP.NET 10): 3 Flächen (Impulse, Ziele, Lead-Pipeline), Side Sheet, Wiki-Integration (73 Seiten), Playwright-Capture, `npm run check`-Gate. **Grenze: alle Daten sind Seed-Konstanten, keine Persistenz, kein Schreibpfad.**
- **Inhaltsbestand:** 05-marketing-strategie (16 Docs, 15-Maßnahmen-Landkarte), 06-website-planung (~40 Docs), Positionierungs-Framework (MKT-14/15), Claim-Hierarchie (MKT-13).

**Defekte:**

| Befund | Beleg |
|---|---|
| **MKT-5 nur halb gelandet:** `geo-aeo-llmo-…md` verweist 6× auf `06-website-planung/07-ai-discovery/` (llms.txt, EN-Faktenquelle, JSON-LD, Testmatrix) — **Ordner existiert nicht**; D-016 fehlt im Entscheidungs-Log | Salvage 24.07., Worktree nie gepusht |
| MKT-10 eskaliert (Grade C): reales Anzeigen-Intake + persistierte Mutationen fehlen | council-reaction.json |
| MKT-13 fast fertig (Grade B): nur Mojibake-Encoding-Runde offen | council-reaction.json |
| MKT-8/9 ohne Lieferung (8 als unknown-legacy eskaliert gewesen) | status.md |
| Keine MKT-Karte remote-fähig: origin persönlich, repositoryUrl null | /api/projects |

## 2. Stufen 2–4

- **Stufe 2 · Maßnahmen als TODO+Status · M:** Measure-Modell, **git-backed JSON** + Schreib-API, Einmal-Import der Markdown-Landkarte (wird danach generierte Ansicht), Cockpit-Status-UI, Deep-Link Maßnahme↔MKT-Karte. Robert: Statusschema abnicken.
- **Stufe 3 · Werkzeuge Text&Recherche · M:** „Aus Maßnahme Karte erzeugen" (POST /api/tasks, promptMarkdown), 3–4 versionierte Prompt-Vorlagen, Rückkanal read-only, **Redaktions-Gate** (Draft → Robert → Publish). Robert: Redaktionsregel + 2 erste Templates.
- **Stufe 4 · KPI-Automatik · L (3 Schnitte):** MetricsCollector-Seam + git-backed JSONL; Quellen: GitHub-Stats (read-only PAT), Website-Traffic (Plausible/Caddy-Logs auf der VM), AI-Discovery-Probe aus MKT-5; Wochen-Report ins Wiki. Als **Dienst**, nie als Session-Task. Robert: PAT, Analytics-Wahl, Dienst-Freigabe.
- Stufe 5/6 (Leads/Verkauf) erst nach Stufe 2 — MKT-10 ist exakt an fehlender Persistenz gescheitert.

## 3. Reihenfolge

Neu belegbar seit 27./28.07.: **Container-Default** (P1 komplett; Grenze: belegt für Neuinstallation + hermetische Verifikation, nicht „jeder Lauf isoliert") und **0 PRs in Betrieb** (Thesenstück aber ungeschrieben).

1. **MKT-5-Reparatur zuerst · S · kein Robert-Input:** `07-ai-discovery/` neu erzeugen + D-016 nachtragen — die Maschinen-Faktenquelle, auf die jede spätere Maßnahme zeigt.
2. **MKT-13-Reissue · S (nach E1):** nur Encoding; der Enabler-Satz trägt jetzt („Easy to install. Isolated where configured.") gegen P1-Beleg.
3. **MKT-8 aufteilen:** 8.1 Verzeichnisse/Awesome-Listen (S, rein agentisch — schnellster Sichtbarkeitsgewinn) · 8.2 Launch-Artikel mit 0-PR-Aufhänger (M) · 8.3 Release bleibt geblockt · 8.4 Show HN nach 8.2 · 8.5 Case-Study = Robert.
4. Danach MKT-9/10 (nach Stufe 2). MKT-3 bleibt geparkt.

## 4. Die drei Entscheidungen

- **E1 Claim-Ebene:** **A (empfohlen):** Lead bleibt „vom Agentenlauf zur prüfbaren Aufgabe"; „Easy installieren, isoliert nutzen" wird erste Sektion unter dem Proof (Container-Default belegt den **Enabler**, nicht das Problem). B: Isolation als Hero. C: A/B-Test (doppelte Pflege).
- **E2 Repo-Lage:** **A (empfohlen): in die Org migrieren, PRIVAT, repositoryUrl setzen** → alle MKT-Karten remote-fähig, Strategie bleibt intern; MKT-16 auf „private baseline" reduzieren. B: Split public-Content/privat-Strategie (eigene Welle). C: so lassen (alles bleibt lokal+seriell).
- **E3 Ausbau-Tiefe:** **(ii) empfohlen: Stufen 2+3 als eine Welle** (2 ist Voraussetzung für alles, 3 macht das Studio arbeitsfähig). (i) nur 2. (iii) 2+3+4 (bindet an Secrets/Betriebsentscheidung).

**Nach GO sofort taktbar ohne Rückfrage:** MKT-5-Reparatur (S) · workbench.json fürs Studio-Doc (S) · MKT-8.1 (S) · MKT-13-Reissue (S, nach E1) · Stufe-2-Karten (M, nach E3).
