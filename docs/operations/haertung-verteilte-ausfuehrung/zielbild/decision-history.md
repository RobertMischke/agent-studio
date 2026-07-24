# Decision history

- **2026-07-24 — Nomenclature.** *Agent Orchestrator* is the product umbrella; *Task Server* the truth; *Orchestrator Engine* the deciding actor (council/review); *Runner* the executing actor; *Agent Studio* the UI client. The name "Orchestrator" no longer refers to the API binary — today's `OrchestratorApi` is all three server roles in one binary, and the nomenclature split **is** the migration split.
- **2026-07-24 — Engine placement.** The Engine lives beside the Task Server as its own process/unit, co-located on the same host, public API only. Rationale: engine restarts must not orphan runs (empirical, 23–24 Jul).
- **2026-07-24 — No supervising system.** Self-stability per actor + passive last-seen at the Task Server. See connection-health.md.
- **2026-07-24 — Quality is a contract, not an actor.** The consolidation point between the Architecture and Quality-Layer pages; both now defer to this folder.
