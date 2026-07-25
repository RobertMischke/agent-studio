# The three contracts (plus one)

1. **Task API** — the only surface. Everything any actor knows about the world it learned here. Mutations carry `X-Client-Id`; placement, moves and evidence land in provenance.
2. **Result-SHA handoff** — the Runner delivers *a commit*, not a story: the delivery tip enters `task.json commits[]` automatically (AGT-2183/2184 close the manual-attribution era; the operator repairs of 23–24 Jul are the empirical why).
3. **Lease/epoch semantics** — claims are leases with expiry and fencing (attempt authority, AGT-2182); review verdicts anchor on epochs so stale verdicts can never overwrite a fresh operator decision (AGT-2260).

**The cross-cutting fourth: quality.** The Engine executes quality — deterministic build/test gates, LLM aspects and grades, council loops that refuse to ship named deficiencies. The Task Server keeps the evidence: grade files, outcomes, provenance, epochs. Neither owns both sides; that separation is the contract.
