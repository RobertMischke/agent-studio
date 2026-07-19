# Companion App Design (V1)

> Status: V1 contract. Backend `CompanionSyncService` and the standalone relay project under `companion/relay/` implement this contract. The PWA front-end is a follow-up task.

The Agent Software Studio runs on a private machine without inbound network reachability. The Companion App lets the user check pipeline status, token usage, and open decisions from a phone, and post small steering interventions back. Both legs of the conversation flow through a public relay; the local processor only opens outbound HTTPS.

## Three-tier shape

```
Phone (PWA, public Internet)  --HTTPS-->  Relay (small public service)  <--HTTPS poll--  Local Processor
                              <--HTTPS--                                  --HTTPS push-->
```

- **Relay** (`companion/relay/`). Tiny ASP.NET 10 minimal API. Holds the latest snapshot and a small command queue in memory. No business logic, no persistence beyond process lifetime. Bearer-token auth.
- **Local Processor** (`backend/Services/Companion/CompanionSyncService.cs`). HostedService that ticks every `Companion:SyncIntervalSeconds` (default 10 s) when enabled. Each tick is one outbound `POST /sync`: the body carries the current snapshot, the response carries any commands queued since the last call. Pull-pull. No inbound port required on the local box.
- **PWA** (planned: `companion/pwa/`). Angular standalone app, separate deployment. Reads `GET /state`, posts to `POST /commands`. Reuses the dev frontend's Catppuccin styling but is intentionally smaller and read-mostly.

## Endpoints (relay)

| Method | Path | Caller | Purpose |
|--------|------|--------|---------|
| POST | `/sync` | Local Processor | Push current snapshot, drain pending commands. Response body lists commands queued since last sync. |
| GET | `/state` | PWA | Read latest snapshot. |
| POST | `/commands` | PWA | Enqueue a command (decision answer, new task, start job). Returns the assigned command id. |
| GET | `/healthz` | anyone | Liveness. Returns relay version + `lastSyncAt`. |

All routes except `/healthz` require `Authorization: Bearer <token>`. The token is shared between processor, PWA, and relay via env var (`COMPANION_TOKEN`). Token rotation = redeploy the relay and update both clients.

## Snapshot shape (what the processor pushes)

```jsonc
{
  "snapshotAt": "2026-05-04T19:42:11Z",
  "host": { "name": "rmisc-desktop", "isDev": true, "version": "abc1234" },
  "projects": [
    {
      "name": "agent-taskboard",
      "watchPath": "C:/Projects/agent-taskboard-workspace/projects/agent-taskboard",
      "runner": { "mode": "auto", "activeJobId": "companion-app" },
      "pipeline": {
        "ready": [ { "id": "...", "title": "...", "agent": "claude", "model": "claude-opus-4-7" } ],
        "progress": [ { "id": "companion-app", "title": "Companion APP", "agent": "claude" } ],
        "review": [ ],
        "needsInput": [
          {
            "id": "companion-app",
            "reason": "design choices for relay hosting...",
            "lastAgentMessage": "Bevor ich Code schreibe, ..."
          }
        ]
      }
    }
  ],
  "tokens": {
    "today": { "input": 12345, "output": 6789, "cacheRead": 0, "cacheCreate": 0, "estimatedUsd": 0.42 },
    "byModel": [ { "model": "claude-opus-4-7", "calls": 12, "input": 12000, "output": 6000 } ]
  },
  "quota": [ { "cli": "claude", "window": "five_hour", "usedPct": 35, "resetsAt": "2026-05-04T22:00:00Z" } ]
}
```

Snapshots are full state, not deltas. They are small (typically <50 kB per project family), idempotent, and overwrite the relay's previous snapshot in place. Lossy is fine.

## Command shape (what the PWA enqueues)

```jsonc
{
  "id": "cmd-uuid",
  "createdAt": "2026-05-04T19:42:11Z",
  "kind": "decision-answer" | "new-task" | "start-job",
  "payload": { /* kind-specific */ }
}
```

`decision-answer`:
```jsonc
{ "jobId": "companion-app", "watchPath": "C:/.../agent-taskboard", "text": "Use Fly.io.", "mode": "continue" }
```

`new-task`:
```jsonc
{ "watchPath": "C:/.../agent-taskboard", "title": "Add log filter", "prompt": "...", "agent": "claude" }
```

`start-job`:
```jsonc
{ "jobId": "ready-task", "watchPath": "C:/.../agent-taskboard" }
```

The local `CompanionCommandDispatcher` translates each into existing in-process service calls. It does not call the local API surface from inside the same process; the relay command kinds map directly to the existing `JobMutationService` / `TaskRunnerService` operations the REST endpoints already use.

## Sync cadence and contention

- Default tick: 10 s. Configurable via `Companion:SyncIntervalSeconds`. Floor 5 s, ceiling 60 s.
- One in-flight sync at a time. If the previous tick has not returned, skip this one and log at debug.
- On sync failure the processor logs at warning and retries on the next tick. No exponential backoff in V1; the relay is small and Fly.io's restart cycle is short.
- Commands are processed in the order the relay returns them. Each dispatch is recorded in `cli-output.log` on the `[orchestrator]` stream as a typed `companion-command` event so the activity log shows where steering came from.

## Auth, secrecy, deploy

- **V1 auth: shared bearer token over TLS.** Token lives in `Companion:Token` (processor) and `COMPANION_TOKEN` (relay env). PWA stores it in localStorage after a one-time paste / QR scan.
- **Payload is plaintext.** The relay sees titles, prompts, and decision text. This is acceptable for V1 because (a) the relay is single-tenant and ours, (b) Fly.io disk is volatile, (c) we control the only PWA installation. End-to-end encryption (libsodium symmetric, key generated locally and conveyed to the phone via QR pairing) is V2 and is called out in the ROADMAP.
- **Hosting: Fly.io.** One small machine, scale-to-zero off (we want low-latency snapshots), volume not required. `fly.toml.example` sits next to the relay project. Railway is a documented fallback.
- **CORS:** the PWA origin is configured per-environment; the processor leg does not need CORS.
- **Default-off.** The HostedService only runs when `Companion:Enabled=true` is set in `appsettings.Local.json`. The shipped `appsettings.json` keeps it disabled so a fresh checkout never tries to reach the network.

## Non-goals in V1

- **No live log streaming.** The PWA dashboard shows pipeline + quota + open decisions, not live agent stdout. The latter would balloon snapshot size and destroy the "lossy is fine" property.
- **No diff viewer, no commit list.** Out of scope for the phone surface.
- **No push notifications.** PWA Web Push on Android is achievable but requires VAPID keys and a service worker beyond the V1 shell. Polling while the page is open is enough.
- **No multi-user.** One processor, one PWA, one shared token.
- **No relay persistence.** A relay restart clears state; the next processor sync repopulates the snapshot within one tick. The command queue is in-memory and best-effort. PWAs that already know their command id can poll for `processedAt`.

## Out-of-scope but adjacent (V2+)

- End-to-end encryption with a paired symmetric key.
- Push notifications when a job enters `4-review` or hits NEEDS_INPUT.
- Per-project token-spend timeline view (already on the security roadmap).
- Encrypted archive of historical snapshots so the phone can scroll back.

## File map

```
backend/
  Services/
    Companion/
      CompanionSyncService.cs     # HostedService, runs the tick loop
      CompanionSnapshotBuilder.cs # Pure: project state -> snapshot DTO
      CompanionCommandDispatcher.cs # Pure-ish: command -> existing services
      CompanionDtos.cs            # Shared DTOs (snapshot, command)
      CompanionSyncOptions.cs     # IOptions binding
companion/
  relay/
    CompanionRelay.csproj
    Program.cs
    Models.cs
    appsettings.json
    Dockerfile
    fly.toml.example
    README.md
  pwa/
    README.md                      # placeholder; PWA is the next task
docs/
  companion-app-design.md          # this file
```

## Test surfaces

- `backend.Tests/CompanionSnapshotBuilderTests.cs`: the snapshot builder is a pure function over (job list, runner state, token summary, quota report). Test that it folds correctly and never throws on empty inputs.
- `backend.Tests/CompanionCommandDispatcherTests.cs`: each command kind dispatches to the right existing service and surfaces validation errors as typed responses.
- Relay project ships with one smoke test that round-trips a snapshot and a command.
- Once the PWA exists, an `@offline` Playwright spec verifies the dashboard renders a fixture snapshot.

The first PR following this doc lands the design + relay + backend HostedService + tests. The PWA scaffold and Fly.io deploy are separate follow-ups so each step can be reviewed and rolled back independently.
