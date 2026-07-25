# Decision history

- **2026-07-24 — Nomenclature.** *Agent Orchestrator* is the product umbrella; *Task Server* the truth; *Orchestrator Engine* the deciding actor (council/review); *Runner* the executing actor; *Agent Studio* the UI client. The name "Orchestrator" no longer refers to the API binary — today's `OrchestratorApi` is all three server roles in one binary, and the nomenclature split **is** the migration split.
- **2026-07-24 — Engine placement.** The Engine lives beside the Task Server as its own process/unit, co-located on the same host, public API only. Rationale: engine restarts must not orphan runs (empirical, 23–24 Jul).
- **2026-07-24 — No supervising system.** Self-stability per actor + passive last-seen at the Task Server. See connection-health.md.
- **2026-07-24 — Quality is a contract, not an actor.** The consolidation point between the Architecture and Quality-Layer pages; both now defer to this folder.
- **2026-07-24 — Control plane as distributable (open).** Robert paused AGT-2277: extract the control plane into a versioned distributable before hosting it publicly; see distributable.md. Deploy cards get re-cut against the defined package.
- **2026-07-24 (evening) — Capacity: one source of truth.** Per-project maxParallelism is deprecated; capacity lives at host level (ceiling, target load, ramp strategy — AGT-2228/2302), quota caps enter central admission. Runners report measurements only.
- **2026-07-24 (evening) — Lane semantics.** Escalated is the intervention basin ("machine stuck", always with a stated reason) and sits before Review on the board; human-review is exclusively acceptance of evidenced deliveries.
- **2026-07-24 (evening) — Task-type-aware pipelines.** Pipeline steps resolve per task type; planning gets a lightweight chain (content review, HTML deliverable, no code gates).
- **2026-07-24 (evening) — Document format rule.** HTML only for diagram-first lead pages; text pages stay Markdown (better diffs, fewer tokens, human+machine readable).
- **2026-07-24 (evening) — Orchestrator control plane plan adopted** (see orchestrator-plan.md): persistent deterministic control plane, episodic LLM sessions, phased after current P0 healing.

## 2026-07-25 — Distributable sign-off (D1–D5)

Robert signed off the distributable concept (`distributable.html`):

- **D1 Package cut:** three distributables; control plane = one archive, two binaries.
- **D2 Names:** `agent-orchestrator` (control plane) · **`agent-host`** (per-host daemon; renamed from
  `agent-runner` — collided with the CAR library and hid the host level) · `agent-studio` (web bundle).
- **D3 Order:** extract + package **before** public hosting; AGT-2277 re-cut afterwards as "deploy the distributable".
- **D4 Tranche-0 scope:** monolith split (§2), release pipeline (§3), two-layer config contract (§5/§8.7),
  plus protocol-version handshake with honest refusal and drain-before-update — both mandated by the 25.07. incidents.
- **D5 Installer & onboarding:** super-simple guided installer; on Linux ideally a single executable with
  guided prerequisite steps (docker for the demo path); multi-machine setup guide is first-class.
  Download + easy onboarding is the **marketing milestone**.

Tranche-0 cards cut the same day (see AGT board).
