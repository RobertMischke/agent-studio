# Verteilte Task-Server mit Git als Sync-Transport

Status: Konzept / Bewertung, 2026-07-19. Diese Seite prüft eine von Robert als
*theoretische Idee* aufgeworfene Architektur. Sie ist bewusst keine
Produktentscheidung und kein Pitch, sondern eine ehrliche Abwägung gegen die
kanonische Zielarchitektur in
[distributed-agent-studio-target-architecture.md](distributed-agent-studio-target-architecture.md).

> „Denkbar wäre sogar eine verteilte Architektur mit lokalen Task-Servern, die
> über git pushes und pulls kommunizieren… hier sollte es ja auch die
> Verwaltungsfunktionen geben.“ — Robert

## 1. Die Idee präzisiert

Die Vorstellung lässt sich in vier Bausteine zerlegen:

1. **Board-Zustand lebt im Git.** Der Workspace ist bereits ein eigenes
   Git-Repo (`C:/Projects/agent-taskboard-workspace`). Karten, Lanes und
   Evidenz liegen als Dateien (`task.json`, Ordner je Lane, Artefakte) vor. Das
   Repo *ist* damit schon die durable Wahrheit — der laufende Backend-Prozess
   (`dotnet run`, Port 5031) ist heute nur der Leser/Schreiber darauf.
2. **Lokaler Task-Server je Host.** Jeder Host betreibt seine eigene
   Task-Server-Instanz gegen einen lokalen Klon desselben Repos. Kein zentraler
   Always-on-Dienst ist zwingend nötig.
3. **Sync über push/pull gegen ein zentrales Remote.** Zwei Hosts sehen
   denselben Board-Stand, indem der Committer Transitionen pusht und die
   Gegenseite pullt. Das Remote ist ein reines Git-Remote, kein API-Server.
4. **Verwaltungsfunktionen am Task-Server.** Roberts Zusatz „hier sollte es ja
   auch die Verwaltungsfunktionen geben“ meint Bootstrap, Nutzer/Rollen,
   Runner-Enrollment, Backup/Restore, Health. Diese Funktionen sind bereits als
   Karten gequeut: **AGT-2194** (Live-Admin-API und Recovery-Console) und
   **AGT-2192** (Standalone-Control-Plane / Task-Server-Extraktion). Die
   Verwaltung hängt also nicht an der Git-Frage — sie kommt so oder so.

Kurz: Git ersetzt in dieser Idee den *Transport* zwischen Task-Servern, nicht
die Task-Server selbst.

## 2. Was Git als Transport kauft

Git ist als Sync-Ebene nicht beliebig — es bringt echte, sonst teuer zu bauende
Eigenschaften mit:

- **Offline-Fähigkeit ohne Zusatzarbeit.** Ein Host arbeitet lokal weiter, wenn
  das Remote weg ist, und gleicht später ab. Genau das Szenario „Studio
  detached“ / kurzfristige Trennung ist in Git nativ.
- **Audit-Trail gratis.** Jede Transition ist ein Commit mit Autor, Zeit und
  Diff. Das ist deckungsgleich mit **Härtungs-Säule 1 (Result-SHA-Vertrag)**:
  Ein unveränderlicher Attempt-Record, aus dem Grade, Gate, Merge und UI
  *ausschließlich* lesen — siehe
  [operations/haertung-verteilte-ausfuehrung](../operations/haertung-verteilte-ausfuehrung/index.html).
  Der Git-Commit-Graph ist die natürliche Materialisierung dieses Vertrags.
- **Deterministische Konfliktsichtbarkeit.** Zwei divergierende Stände
  verschwinden nicht still. Das **Collision-Ref-Muster aus AGT-2177**
  (`runner/<id>/<task>-collision-<localSha>-<remoteSha>`, beide SHAs verifiziert,
  kein Force-Push) macht Divergenz zu einem benannten, prüfbaren Objekt statt zu
  einem verlorenen Schreibvorgang.
- **Evidenz-Replikation im selben Kanal.** Artefakte, Logs und Ergebnis-SHAs
  reisen mit demselben push/pull wie der Board-Zustand. Kein separater
  Blob-Store, keine zweite Konsistenzgrenze.
- **Kein neuer Infrastruktur-Stack.** Kein Message-Broker, keine
  Replikations-DB, kein Outbox-Consumer-Betrieb. Git-Server (oder ein bloßes
  bare-Repo über SSH) ist vorhanden und verstanden.

## 3. Was es kostet

Die Rechnung hat eine ehrliche Sollseite. Git ist ein *Dateisync*, kein
*Koordinationsprotokoll* — und Board-Orchestrierung ist Koordination.

- **Latenz und kein Push-Signal.** Git kennt kein „hier ist etwas Neues“. Der
  pullende Host merkt Änderungen erst durch Polling oder einen Hook-Trigger
  (post-receive → Webhook → pull). Live-Board-Reaktivität in Sekunden erfordert
  einen zusätzlichen Signalkanal — Git allein liefert ihn nicht.
- **Merge-Semantik auf `task.json` und Ordner-Moves.** Das ist der harte Punkt.
  Lane-Transitionen sind im Dateimodell **Renames** (Ordner-Move zwischen
  Lanes). Zwei Hosts, die dieselbe Karte gleichzeitig bewegen, erzeugen einen
  Rename/Rename- oder Rename/Modify-Konflikt — eine eigene Konfliktklasse, die
  ein Drei-Wege-Textmerge *nicht* korrekt auflöst. Board-Invarianten (eine Karte
  in genau einer Lane) sind für Git unsichtbar; es merged Bytes, nicht
  Zustandsmaschinen.
- **Ordering und Fencing sind nicht geschenkt.** Git garantiert keine globale
  Reihenfolge und keine Exklusivität. Dass nur *ein* Attempt eine Karte
  bearbeitet, bleibt Aufgabe des **Attempt-Fencing (AGT-2182)**. Git repliziert
  die Fence-Records treu, erzwingt sie aber nicht — „letzter Push gewinnt“ ist
  ohne Fence eine Race-Condition, keine Entscheidung.
- **Repo-Wachstum und GC.** Jede Transition, jeder Artefakt-Blob wächst die
  History. Ohne Retention/GC-Strategie (shallow, Artefakt-Auslagerung, packing)
  wird der Klon über Monate teuer — besonders für neue oder schwache Hosts.
- **Secrets und ACL.** Ein geteiltes Repo teilt standardmäßig *alles*.
  Feingranulare Sichtbarkeit (Projekt-A-Host sieht Projekt B nicht),
  Runner-Credentials und Redaction lassen sich in einem flachen Git-Remote nur
  grob abbilden. Die Autorisierung aus der Zielarchitektur (§8) ist damit nicht
  darstellbar.

## 4. Einordnung gegen die kanonische Zielarchitektur

Die [Zielarchitektur](distributed-agent-studio-target-architecture.md) trifft
eine bewusst gegenteilige Grundentscheidung: **HTTP-Control-Plane plus
Outbox**. Der Task-Server ist die immer verfügbare Autorität; Runner
verbinden sich ausgehend; Leases und Fences leben serverseitig; Git trägt
*Code, nicht Task-Wahrheit* (Topologie, §3). Die Outbox-Zustellung typisierter
Events ist **AGT-2183**.

Ausdrücklich zurückgestellt ist dort das isomorphe Multi-Writer-/Offline-Store-
Modell (AGT-2122): kein zweiter mutierbarer Board-Store je Host, keine
autonome Weiterarbeit ohne Task-Server. Roberts Idee ist im Kern genau dieses
zurückgestellte Modell — jeder lokale Task-Server *ist* ein zweiter Writer.

**Das ist aber kein Entweder/Oder.** Die beiden Sichten adressieren
unterschiedliche Ebenen:

| Ebene | Frage | Bestes Werkzeug |
| --- | --- | --- |
| **Steuerung** | Wer darf jetzt was ändern? Reihenfolge, Exklusivität, Live-Signal | HTTP-Control-Plane + Lease/Fence |
| **Wahrheit/Evidenz** | Was ist passiert, unveränderlich und replizierbar? | Git als Commit-Graph |

**Empfehlung: Git als Evidenz- und Replikationsebene, nicht als primärer
Steuerkanal.** Der bereits geplante Transition-Committer, der Transitionen als
Commits schreibt und pusht, *ist* die Vorstufe dieses Bildes — nur ohne die
Illusion, dass der Push selbst die Koordination erledigt. Die Koordination
(darf dieser Host diese Karte bewegen?) läuft über eine leichte
Lease/HTTP-Ebene; der bestätigte Zustand landet als Git-Commit, den alle Hosts
read-only replizieren.

**Hybrides Zielbild:**

- *Control via HTTP/Lease:* Wer eine Karte bewegen darf, entscheidet ein
  Fence-Grant (AGT-2182) über einen minimalen Control-Kanal. Genau ein Writer
  je Karte zu einer Zeit.
- *State-Truth via Git:* Der bewilligte Writer committet die Transition; das
  Remote repliziert sie; andere Hosts pullen und rendern read-only. Divergenz,
  falls sie doch entsteht, wird über das Collision-Ref-Muster (AGT-2177)
  sichtbar statt überschrieben.
- *Verwaltung* (AGT-2194 / AGT-2192) bleibt am Task-Server und bewegt sich nicht
  ins Git — Nutzer, Rollen, Enrollment und Backup brauchen Autorisierung, die
  ein flaches Remote nicht bietet.

So bleibt Roberts Intuition erhalten (Git trägt den Zustand, Hosts arbeiten
lokal, Audit ist gratis), ohne die Kosten aus §3 in den kritischen Pfad zu
legen.

## 5. Entscheidungsfragen für Robert

1. **Ein Writer oder mehrere?** Soll je Karte zu jeder Zeit genau ein Host
   schreibberechtigt sein (Fence-Grant, empfohlen) — oder wollen wir echte
   Multi-Writer-Merges auf `task.json` und akzeptieren die Rename-Konfliktklasse
   als Normalfall?
2. **Wie viel Live?** Reicht Board-Sync im Sekunden-bis-Minuten-Bereich (Polling
   / post-receive-Hook) — oder ist Echtzeit-Reaktivität eine harte Anforderung,
   die einen HTTP-Event-Kanal ohnehin erzwingt?
3. **Wer hält die Autorität, wenn das Remote weg ist?** Fail-closed wie die
   Zielarchitektur (kein neuer Claim ohne Server) — oder darf ein isolierter
   Host autonom weiter Karten bewegen und später mergen?
4. **Sichtbarkeitsgrenzen.** Brauchen wir projekt-/host-granulare ACLs und
   Secret-Trennung? Falls ja, scheidet ein einzelnes geteiltes Remote als
   alleinige Ebene aus.

## 6. Nächste konkrete Schritte

- **Transition-Committer + Push aktivieren.** Der bereits vorgesehene Committer,
  der jede Lane-Transition als Commit schreibt und pusht, ist die risikoärmste
  Vorstufe und liefert sofort den Audit-Trail (Säule 1). Kein Multi-Writer nötig.
- **Remote-Replika read-only als Experiment.** Ein zweiter Host pullt dasselbe
  Remote und rendert das Board *nur lesend*. Das validiert Latenz, Repo-Wachstum
  und Rendering-Treue, ohne die Merge-Frage anzufassen.
- **Admin-API-Karte schärfen.** AGT-2194 (Live-Admin-API) und AGT-2192
  (Standalone-Control-Plane) so schneiden, dass die Verwaltungsfunktionen
  unabhängig vom Git-Transport landen — sie sind die Voraussetzung für *jede*
  verteilte Variante.
- **Merge-Spike auf `task.json`.** Ein kleiner, ehrlicher Test: zwei divergente
  Lane-Moves derselben Karte mergen und beobachten, was Git tut. Ergebnis
  entscheidet, ob Multi-Writer überhaupt weiter verfolgt wird.

---

*Verwandte Seiten:*
[Zielarchitektur](distributed-agent-studio-target-architecture.md) ·
[Härtung verteilte Ausführung](../operations/haertung-verteilte-ausfuehrung/index.html)
