# Runbook: CLI-Neuanmeldung auf einem Execution Host

**Kontext:** Jeder Host meldet Claude und Codex **selbst** an (D5, permanent;
`linux-runner-host.md:182-203`). Credential-Dateien werden nie zwischen Host
und Operator kopiert. Läuft die Anmeldung eines Hosts ab, scheitert **jeder**
Lauf mit dieser CLI auf diesem Host identisch, bis dort neu angemeldet wird —
das AGT-2066-Muster vom 2026-07-10 (17 verbrannte Karten). Vorleistung 5 aus
`docs/operations/token-refresh-ohne-tunnel.md` §4; dokumentiert den heute
gebauten Weg, kein Redesign.

## 1. Symptome erkennen

- **Journal auf dem Host:** `[runner] capability-failure
  capability=provider-auth:<codex|claude> classification=ProviderUnauthorized`
  (`RemoteTaskRunner.cs:636`).
- **Karte/Orchestrator-Chat:** Meldung „The agent CLI could not refresh its
  OAuth session … Stopping instead of burning further launches; re-auth the
  CLI, then re-queue." Das ist der `RunOutcomePolicy`-Breaker für
  `AuthRefreshFailed` (`RunOutcomePolicy.cs:253-274`) — **nicht-retryable**,
  die Karte geht sofort in Human Review, ohne Retry-Budget zu verbrauchen.
- Mehrere Karten desselben Hosts fallen **gleichzeitig** identisch aus —
  Unterscheidungsmerkmal gegenüber einem Einzelfehler (Timeout, Kontext-Overflow).
- Heute rein post-mortem: der Coding-Claim (`/api/runner/claim`) prüft die
  Capability nicht vorab, der Host zieht also weiter Karten und verbrennt sie,
  bis jemand eingreift (Lücke 2 im Konzept-Doc).

## 2. Sofort-Check-Kommandos

Vom Studio aus, per SSH auf den betroffenen Host (Login-User = der
systemd-Runner-User, siehe §3):

```bash
ssh <runner-user>@<host> \
  'export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"; \
   echo "[codex]"; codex login status; \
   echo "[claude]"; claude auth status --text'
```

Beide Kommandos müssen den aktiven Account zeigen und Exit-Code 0 liefern.
Dieselbe Prüfung führt `remote_login_status()` in
`scripts/remote-runner-onboard.sh:225-237` aus — bei Bedarf das Skript direkt
mit `--skip-auth` weglassen und erneut laufen lassen, statt die Kommandos
manuell nachzubauen.

## 3. Re-Login Schritt für Schritt je CLI

**Welcher Unix-User:** exakt der Owner des `agent-host`-Prozesses — der User,
unter dem `systemctl status agent-host` `User=…` zeigt (Onboarding setzt ihn
per `runner_user="$(id -un)"` beim SSH-Login, `remote-runner-onboard.sh:274`;
die statische Unit-Vorlage nennt `agent-runner`,
`deploy/systemd/agent-host.service:13`). **Nicht** root, **nicht** der
Operator-Account vom Studio-Rechner. Grund: Die Credential-Datei liegt im
Home dieses Users (`~/.claude/.credentials.json`, `~/.codex/auth.json`) und
**muss eine gewöhnliche, für diesen User beschreibbare Datei bleiben** —
Claude und Codex schreiben ihr eigenes Refresh-Token in genau diese Datei
zurück (Link-statt-Kopie-Invariant, `linux-runner-host.md:205-210`). Ein
readonly-Mount, ein Login als falscher User oder ein späteres Kopieren der
Datei bricht den nächsten automatischen Refresh, nicht nur diesen Login.

**Codex** (Device-Flow, funktioniert headless über SSH):

```bash
ssh -tt <runner-user>@<host> \
  'export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"; \
   codex login status || codex login --device-auth; codex login status'
```

URL und Einmalcode erscheinen im Terminal; der Browser-Schritt läuft lokal
beim Operator, nur die Bestätigung geht an den Host zurück
(`remote-runner-onboard.sh:243-244`).

**Claude** — zwei Wege, je nach Erreichbarkeit:

1. Interaktiv über Port-Forward: `ssh -L <port>:localhost:<port>
   <runner-user>@<host>`, dann auf dem Host `claude` starten und das
   OAuth-Onboarding im lokal geöffneten Browser abschließen
   (`linux-runner-host.md:194-198`).
2. **Headless-Alternative ohne Browser-Tunnel:** `claude setup-token` auf dem
   Host — erzeugt einen langlebigen Token direkt in derselben Credential-Datei
   (`remote-runner-onboard.sh:246-247`, `linux-runner-host.md:196`). Erste Wahl,
   wenn kein Port-Forward möglich ist.

Danach verifizieren: `claude --version` und ein Wegwerf-Aufruf
`claude -p "say hi"` (`linux-runner-host.md:198`).

## 4. Nachweis + Wiederanlauf

1. §2-Check erneut ausführen, bis **beide** CLIs `ok` melden
   (`remote_login_status` liefert Exit 0 nur bei `codex_ok=1 && claude_ok=1`).
2. Die nächste Capability-Advertisement des Runners (alle 60 s,
   `RemoteRunnerDaemon.cs:180`) meldet `provider-auth:<cli>` wieder als
   `ready`, sobald die Probe den echten Status liest.
3. War die Capability serverseitig bereits `Draining` (ab 2 Fehlern,
   exponentielles Cooldown-Fenster ab 120 s,
   `TaskServerCapabilityStore.cs:10-11, 202-213`), muss erst der Cooldown
   ablaufen; danach greift ein Half-Open-Canary-Claim, bevor der Host wieder
   voll frei claimt.
4. **Karten manuell requeuen:** Der Recovery-Pfad hat heute keinen Konsumenten
   (`WaitForCapabilityRecovery` wird nur geloggt, `RemoteTaskRunner.cs:655`) —
   eine Karte, die schon mit `AuthRefreshFailed` in Human Review gelandet ist,
   kommt **nicht** automatisch zurück. Nach bestätigtem Re-Login die
   betroffene(n) Karte(n) von Human Review zurück auf Ready/2-ready setzen.
5. Abschließend eine Karte end-to-end laufen lassen (Diagnose-Modus,
   `linux-runner-host.md` §5) oder den nächsten regulären Claim beobachten,
   bevor der Host als geheilt gilt.

## 5. Was man NIE tut

- **Nie** `~/.claude/.credentials.json` oder `~/.codex/auth.json` zwischen
  Hosts oder vom Operator-Rechner auf den Host kopieren — das erzeugt genau
  die Refresh-Token-Lineage-Drift, die D5 abgeschafft hat (Vorfall 2026-07-09:
  Host-Claude fiel nach Operator-Token-Rotation aus,
  `linux-runner-host.md:186-192`).
- **Nie** Credentials ins Repo, ein Image oder ein Artefakt/`results/`
  committen (`token-refresh-ohne-tunnel.md` §5, §7 Punkt 5).
- **Nie** als root oder als Operator-User re-anmelden, „weil es schneller
  geht" — das Token landet dann nicht im Home des Runner-Users, der nächste
  automatische Refresh bricht.
- **Nie** den Ausfall stillschweigend per `systemctl restart agent-host`
  „lösen" — das ändert nichts am ungültigen Token, es verschleiert nur das
  Symptom bis zum nächsten Lauf.
