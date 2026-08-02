# Tranche C — Container-Anschluss (Folgekarte nach T2) · Kompaktfassung

**Stand:** 28.07.2026 · Vollfassung im Sitzungsprotokoll · Vorbedingung: T2 + V1/V2/V3 aus car-migration-plan §5

## Klarstellungen

1. **Spawner-Implementierung ist Studio-Code** (Mount-Berechnung = Host-Sache); CAR liefert nur den Nahtpunkt `CliOptions.Spawner` + zwei Mini-Karten (CAR-J: Versions-Probe durch den Spawner routen; CAR-K: KillOverride in allen Stop-Pfaden). CAR-G (setsid) wird durch `--init` gegenstandslos.
2. **Git-Credentials müssen nicht in den Container:** Push macht der Host-Worker (GitWorkspace:675/689). Isolation = `$HOME`, Runner-Token, State-Dir, docker.sock werden schlicht NICHT gemountet.
3. **cgroup-Flucht ist Pflichtzeile:** `docker run` verlässt die systemd-Slice → AGT-2340-Quoten greifen still nicht mehr → `--cpus/--memory` aus profile.conf + Nachweis systemd-cgls/docker stats.

## Kartenschnitt

C1 Container-Spawn (L, koordiniert) · C2 Per-Task-Image+CLI-Versionsbindung (M) · C3 Placement/Host-UI (M) · C4 Gate-im-Container (L, ersetzt faktisch die AGT-2229-Linie — Robert-Frage!) · C5 E4-Messreihe/Umschaltung (S) · CAR-J/K (XS).

## C1-Kern

- **ProcessStartInfo → docker run** mechanisch (Env als --env-file, exec-Form, kein TTY, `--rm --init --user uid:gid --pull=never --label agent-studio.attempt=<id>`); Kill via KillOverride → `docker kill`.
- **Mounts, alle pfadgleich (V2 hart):** M1 `$RUNNER_WORKDIR/<projectId>` rw (Worktree+Klon — .git-Datei zeigt absolut in den Klon!); M2 results/ rw (JOB_RESULTS_DIR unverändert); M3 Nachbar-Checkouts **ro** (Leitplanke, ehrliche Fassung); M4 Clean-Context-Home rw; M5 Paket-Caches (NUGET_PACKAGES etc., gleiche Namen wie Review-Allowlist).
- **P18-Blocker-Abnahme:** Verzeichnis mounten (nie die Datei), Hardlink statt Symlink; Test: Token-Refresh im Container schreibt in die Host-Quelldatei durch. Ohne diesen Nachweis keine Integration (AGT-2066-Präzedenz).
- Waisen-Reaper: `docker ps --filter label=…` gegen Attempt-Records beim Daemon-Start.
- Reattach wird BESSER: Container überlebt Daemon-Restart (kein Kindprozess).

## E4-Zahlen (Vorschlag)

N=**20** aufeinanderfolgende Container-Läufe (= eine volle Welle bei Slots=20), Streuung (≥3 Projekte, ≥1 rotes Gate, ≥1 Timeout/Stop, ≥1 über Daemon-Restart), „gültig" = Envelope-Trio + verifizierter Push + integration.status==integrated. Startzeit p50 ≤ 5 s / p95 ≤ 15 s (Spawn→erstes stdout-Byte). Rollback-Trigger: 2 Container-Infrafehler im Fenster ODER 1 Credential-Rückschrieb-Fehler ODER Disk>80% ODER p95>30s → Capability `execution:container` zurückziehen (Kanarien-Mechanik). Kohorten: 1 Karte → 1 Welle → Host-Default → Installer.

## Image (runner/Dockerfile Ist→Soll)

Fehlt fürs Per-Task-Image: zweites Build-Target `agent-cli` (ENTRYPOINT [] statt Daemon), kein VOLUME, `USER`/schreibbares $HOME für fremde uid, **CLI-Versions-Pinning** (E3!), Playwright gehört ins Gate-/Sidecar-Image (Capability placement-abhängig), Config-Home-Konvention, Docker-Provisionierung ins Onboarding-Runbook (heute 0 Docker-Referenzen), Docker-Socket=Host-Root im Sicherheits-Doc benennen (rootless als Zielbild prüfen).

**Netzwerk:** keine Allowlist im Repo vorhanden → Stufe 1 offener Egress + messen, Stufe 2 Proxy-Allowlist. Nicht nötig: Task-Server, Git-Remote, Inbound.

## Gate-im-Container (E6)

**Selbes IMAGE, eigener Container** (Coding-Container ist beim Gate längst beendet; Gate testet exakte Subjekt-SHA im Wegwerf-Worktree — „derselbe Container" würde die Hermetik zerstören). Dockerfile im Projekt-Repo (Source of Truth) + Sidecar-Image keyed by Dockerfile-Hash. Fund vorab klären: Gate-Brücke globt `$HOME/runner-work/*/repo`, Daemon nutzt `RUNNER_WORKDIR` — ungeschriebene Konvention, Mount-Wurzel braucht EINE Quelle.

## Fragen an Robert

1. M3 = ro bestätigt? 2. Ersetzt C4 die AGT-2229/2262-Brücken-Linie? 3. N=20 / p50≤5s akzeptiert?
