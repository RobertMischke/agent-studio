# Actors & processes

Five actors, each with its own lifecycle. No shared memory, no shared files — the public Task API is the only meeting point.

| Actor | Runs as | Responsibility | Never does |
|---|---|---|---|
| Task Server | systemd unit on the Linux host | store, API, leases, provenance, review epochs, management/recovery API (AGT-2194) | execute work, call LLMs, push code |
| Orchestrator Engine | own systemd unit next to the Task Server | council & review pipelines, post-processing, gate dispatch, prompts | own any state that must survive it |
| Runner | one daemon per host | claim → worktree → CLI slot → deliver (result-SHA handoff), salvage | decide review outcomes |
| Agent Studio | web app, own release cycle | board, deck, wiki UI | anything the API does not offer |
| Libraries | versioned npm packages | shared UI/behavior (e.g. coding-agent-chat 0.3.x) | reach into hosts |

## Mini lifecycle strips

- **Task Server** — start: load store, serve. crash: nothing in flight is lost (truth on disk). deploy: drain writes, restart; clients retry.
- **Engine** — start: subscribe to work via API. crash: runs stay server state; a fresh Engine resumes from the queue. deploy: restart freely.
- **Runner** — start: register, poll. crash: leases expire, server offers work again; salvage branches keep partial work. deploy: per host, independent.
- **Studio** — start: load, read API. crash/reload: F5. deploy: static release, no server coupling.
- **Libraries** — released via tag push (trusted publishing); consumers bump explicitly. No hidden coupling to any running process.
