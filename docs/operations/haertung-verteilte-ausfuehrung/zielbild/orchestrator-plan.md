# Orchestrator control plane — the plan

Persistent control plane, episodic cognition. The orchestrator remembers
through verifiable artifacts, not by keeping a session alive.

**Order (after the current P0 healing is done):**

1. **Phase 1 — shared truth visible:** event envelope + transactional outbox,
   SituationSnapshot with event watermark, incident/decision/action/override
   data model, Activity as full operator view. No LLM healing yet.
   *(Partly covered already: bus/lane-event card, activity view card,
   stale-state hygiene, liveness card.)*
2. **Phase 2 — five triggers in shadow mode:** runner heartbeat lost, runner
   saturation, claim bounce, provenance gap, quota overrun — with correlation,
   cooldowns, human-readable reasons. Acceptance: ≥70% relevant incidents,
   ~1 alarm per incident.
3. **Phase 3 — session director + wiki memory:** context compiler; sentinel /
   companion / incident / forensic session types with context caps; living
   state documents with freshness TTL and watermark; handoff envelopes.
   Sessions observe and report only.
4. **Phase 4 — action gateway + first recipes:** typed recipe manifests
   (shadow → approvalRequired → validated), CAS + idempotency, start with the
   safest five (throttle, one CLI retry, profile pause, clean-lease release,
   provenance repair). Autonomy bar: ≥95% sustained success, no work loss.

**Guardrails:** deterministic monitor (no LLM on the hot path); three risk
buckets instead of a formula engine (exact recipe → small model; reversible →
medium; everything else → human); orchestrator budget capped (~8% of quota,
2% reserve); repeated repairs auto-file a "graduate to deterministic
automation" card.
