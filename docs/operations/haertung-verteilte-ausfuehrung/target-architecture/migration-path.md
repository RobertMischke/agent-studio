# Migration path

Tranches — each shippable, each reversible:

1. **Task Server on the host.** Deploy the server unit on agent-runner-01, migrate the workspace store, point the Runner at `localhost` (one env value). The tunnel dies for the Runner. Studio points at the host URL.
2. **Engine as its own unit** next to the Task Server. From here on, an Engine deploy never touches the truth. CLI access and quota knowledge already live on the host (20 slots, gates run there daily).
3. **Studio stays a client**; later a static build served from the host. Local dev keeps `ng serve`.

**Already delivered toward this** (as of 24 Jul): distributed attempt authority (AGT-2182, grade B), authenticated management API + recovery console (AGT-2194, grade A), result-SHA handoff (AGT-2183/2184, queued), remote orchestrator steps (AGT-2229, partial). Estimated: first productive cutover 1–2 weeks of card waves; full migration incl. backup operation and rollback path 3–4 weeks at current velocity.
