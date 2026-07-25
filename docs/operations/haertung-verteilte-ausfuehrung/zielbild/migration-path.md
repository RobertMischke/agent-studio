# Migration path

Tranches, each shippable and reversible:

0. **Restore the Review plane without moving authority.** The current
   `OrchestratorApi` mounts the versioned Review subset under `/api/v1`: protocol
   compatibility, Review Executor registration and claim, review lease renew,
   fenced report and cleanup, plus immutable result-envelope reads. These routes
   translate into the monolith's existing task store and
   `AttemptAuthorityService`; they do not introduce a second store or a second
   fence counter. The deployed `agent-runner-review.service` can therefore run
   immediately against the current control-plane URL. Its requests carry
   AttemptId, fence, authority epoch, and idempotency key. A capability marker
   advertises `review-plane` only, so existing Coding Runners remain on the
   legacy monolith routes instead of being switched to an incomplete V1 Coding
   plane. This is an internal operational bridge, not the public control-plane
   distributable.
1. **Task Server on the host.** Deploy the server unit on agent-runner-01,
   migrate the workspace store, point the Runner at `localhost` with one
   environment value, and point Studio at the host URL. The Runner tunnel is no
   longer needed.
2. **Engine as its own unit** next to the Task Server. From here on, an Engine
   deploy never touches the truth. CLI access and quota knowledge already live
   on the host with 20 slots and daily gates.
3. **Studio stays a client** and can later become a static build served from the
   host. Local development keeps `ng serve`.

## Tranche 0 cutover and rollback

- Deploy the monolith containing the V1 Review mount, then restart only
  `agent-runner-review.service`.
- A green readiness proof is: compatibility returns 200 with
  `review-plane`, registration succeeds, an Auto Review card is claimed, and a
  grade write is accepted with the claimed AttemptId, fence, epoch, and
  idempotency key.
- Rollback stops the Review unit and restores the previous monolith binary.
  Task and AttemptAuthority data need no reverse migration because Tranche 0
  uses their existing persisted formats additively.
- The later actor split moves these exact `/api/v1` routes to
  `task-server/TaskServer.csproj`. Runner configuration changes only the base
  URL. The wire contract and stale-write behavior do not change.

**Already delivered toward this** (as of 25 Jul): distributed attempt authority
(AGT-2182, grade B, accepted), durable result-SHA handoff (AGT-2183, grade A,
accepted), exact-SHA review (AGT-2184, grade B, accepted), authenticated
management API and recovery console (AGT-2194, grade A, integrated), and the
Tranche 0 monolith Review mount. Store migration and the full standalone
Task Server deployment remain Tranche 1.
