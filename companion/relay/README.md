# Companion Relay

Tiny ASP.NET 10 minimal API. Holds the last snapshot pushed by the local Agent Software Studio and a small queue of commands the PWA enqueues. No persistence; a restart clears state and the next processor sync repopulates it.

The full V1 contract lives in [`docs/companion-app-design.md`](../../docs/companion-app-design.md). [ADR-0018](../../docs/architecture-decisions.md) explains the why.

## Run locally

```sh
cd companion/relay
COMPANION_TOKEN=devtoken dotnet run
# relay listens on http://localhost:5050 by default
```

Health check:

```sh
curl http://localhost:5050/healthz
```

State (PWA's read endpoint, requires the bearer):

```sh
curl -H "Authorization: Bearer devtoken" http://localhost:5050/state
```

## Deploy to Fly.io

```sh
cp fly.toml.example fly.toml
# edit `app = "..."` to a globally unique name
fly launch --no-deploy
fly secrets set COMPANION_TOKEN=$(openssl rand -hex 32)
fly deploy
```

Railway is a documented fallback. Any host that can run a single .NET 10 web container with one env var works.

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/healthz` | Liveness; returns version + lastSyncAt. No auth. |
| POST | `/sync` | Processor pushes a snapshot, drains the command queue. |
| GET | `/state` | PWA reads latest snapshot + pending command count. |
| POST | `/commands` | PWA enqueues a command (decision-answer, new-task, start-job). |

All routes except `/healthz` require `Authorization: Bearer <token>`.
