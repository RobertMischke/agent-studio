# Getting started

Docker Compose is the single supported path for a new Agent Studio installation.
It gives the application a consistent Linux runtime on Windows, macOS, and
Linux, and keeps .NET, Node.js, local configuration files, maintainer switches,
and repository-neighbour assumptions out of the first run.

The default stack starts the Studio UI and its API. It deliberately does not
start an Agent Host or install coding-agent CLIs. Those execution credentials
belong to the later host-onboarding step, not to the first successful boot.

## 1. Prerequisites

- Git.
- Docker Desktop, or Docker Engine with Docker Compose v2.
- At least 8 GB of free disk space for base images, build layers, and persistent
  data.

You do not need a .NET SDK, Node.js, Git Bash, a second repository,
`appsettings.Local.json`, or a special environment variable.

Confirm Docker before cloning:

```sh
docker version
docker compose version
```

Both commands must show a working client and server. On Linux, your user must
be allowed to access the Docker daemon.

## 2. Install and start

Clone one repository and run one start command:

```sh
git clone https://github.com/agent-orc/agent-studio.git
cd agent-studio
docker compose up --build --wait
```

The first build downloads the declared .NET, Node.js, and Caddy base images, so it
takes longer than later starts. `--wait` returns only after Compose reports the
API and browser endpoint healthy.

Open [http://localhost:4011](http://localhost:4011). A successful first run
shows the empty Agent Studio board. The same end-to-end check is available at:

```sh
curl --fail http://localhost:4011/healthz
```

It returns `"ok"`.

## 3. What the command creates

The default Compose project contains exactly two services:

| Service | Purpose | Host endpoint |
|---|---|---|
| `orchestrator-api` | Board API and current local orchestration runtime | `127.0.0.1:5031` |
| `frontend` | Production Studio bundle and reverse proxy | `0.0.0.0:4011` |

Task data and managed project data live in the named Docker volumes
`agent-studio_workspace` and `agent-studio_projects`. Rebuilding or replacing a
container does not delete those volumes.

The default ports can be changed only when they conflict with another local
service:

```sh
STUDIO_UI_PORT=14011 STUDIO_API_PORT=15031 docker compose up --build --wait
```

This is the same Compose installation path with port overrides, not a second
setup method.

## 4. Stop, restart, and inspect

```sh
docker compose stop
docker compose start --wait
docker compose ps
docker compose logs -f
```

To remove the containers while retaining product data:

```sh
docker compose down
```

Do not add `--volumes` unless you intentionally want to delete the installation
data.

## 5. Add execution capacity

The green board is the first-install boundary. Running coding tasks also
requires an Agent Host with a coding-agent CLI login and repository access.
Follow [Linux runner host](./linux-runner-host.md) for that separate,
credential-bearing host setup. The control plane remains usable while no Agent
Host is connected.

## Troubleshooting

| Symptom | Check or fix |
|---|---|
| `docker compose` is not a command | Install Docker Compose v2. Docker's legacy `docker-compose` command is not the supported path. |
| A port is already allocated | Use the `STUDIO_UI_PORT` and `STUDIO_API_PORT` overrides shown above. |
| `--wait` ends with an unhealthy service | Run `docker compose ps` and `docker compose logs`; the service health checks preserve the failing component. |
| The browser cannot reach `4011` on a remote host | Allow the selected UI port in the host firewall or bind it through your existing private tunnel. The API stays loopback-only by default. |
| A rebuild consumes too much disk | Inspect with `docker system df`. Do not remove named volumes that contain the installation data. |

If you are changing Agent Studio source code rather than installing the product,
use the separate [contributor setup](./contributor-setup.md).
