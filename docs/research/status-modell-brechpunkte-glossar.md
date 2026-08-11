# Status Model Breakpoints — Adversarial Pass (Glossary, Catalog, Hardening Directions, Test Plan)

Date: 2026-07-29 · Updated: 2026-07-31 (BP-11 mitigation) · Author: adversarial concept analysis (operator-commissioned)
Scope: the distributed task execution model on `main` — lease/fence/epoch authority, multi-runner
task movement, task-as-truth, integration, recovery paths.

Method: deliberately hostile. Every section asks "how do I break this?", using the code on `main`
as the only truth and the incidents of the last 72h as seeds (AGT-2400 salvage/attribution series,
AGT-2401 registry legacy-reseed clobber, AGT-2400/2402/2403 RunSpec fallback CliCrash, AGT-2405
auto-single revert orphan, review double-claim after lease death, AGT-2423 salvage subjects on the
`origin/main` first-parent line). Concept anchors: `docs/concepts/distributed-agent-studio-target-architecture.md`,
`docs/operations/execution-model-shift/` (container-default execution workbench),
`docs/concepts/orchestrator-drive-to-conclusion.html` (incident log),
`docs/operations/car-migration-plan.md` (T0b RunSpec transport).

This document diagnoses and points; it does not design. Decisions belong to the Status-Dossier.

---

## 1. Glossary

Each term: definition, owner, lifetime, code anchor.

**Lease** — Exclusive, TTL-bounded write authority of one executor over one attempt. Not the
attempt itself: the attempt survives lease death. Owner: Task Server (`AttemptAuthorityService`,
the only mint). Lifetime: TTL between 30 s and 10 min (`NormalizeTtl`,
`backend/Features/Runner/AttemptAuthorityService.cs:1416`), extended by renew, ended by release,
expiry, or takeover. Anchor: `AttemptLeaseRecord` / `NewLease`
(`AttemptAuthorityService.cs:1079-1102`), thin HTTP wrapper `RunLeaseService` →
`backend/Features/Tasks/LeaseEndpoints.cs`.

**Fence (fencing token)** — Per-task monotonically increasing integer minted with every lease
grant (`NextFenceLocked`, `AttemptAuthorityService.cs:1104-1110`, backed by `LastFenceByTask`).
Every authority write must present the current fence; a stale fence is rejected
(`ValidateRunWriteLocked`). Owner: Task Server. Lifetime: forever per task (never resets — the
load-bearing property across restarts). This is the split-brain guard for *server-mediated*
writes; it does **not** fence direct git pushes (see BP-01).

**AuthorityEpoch** - Global claim-generation counter over the whole authority store. Bumping it
(`RotateAuthorityEpoch`, `AttemptAuthorityService.cs`) changes the epoch assigned to every new
run or review claim. A lease minted before the bump drains with its original epoch: its exact
holder may renew, write, and settle until release, expiry, settlement, or a higher-fence
takeover. Owner: Task Server. Lifetime: store lifetime; persisted in
`.metadata/attempt-authority.json`. Every write carries the attempt's epoch, and an epoch that
does not match that attempt is rejected (`AttemptWriteStatus.AuthorityEpochMismatch`).

**Takeover** — A new executor acquiring authority over a task whose current lease has expired.
Run side: `AcquireRun` supersedes the old attempt and mints a new fence
(`AttemptAuthorityService.cs:94-137`). Review side: `ClaimReview` re-leases the *same* attempt
with a fresh fence; a dead lease claimed with the *same* delivery key is answered `LeaseExpired`
so a still-alive claiming process is never double-executed (`ClaimReview` comment block,
`AttemptAuthorityService.cs:553-573`, plus in-flight dedup in `runner/RemoteReviewDaemon.cs:94-103`).
Owner: Task Server decides; runner proves generation death (`DurableLeaseAuthority`,
`WorktreeProcessReaper`).

**Delivery-Ref** — The git ref that carries a completed attempt's work into integration. Three
families exist: local `task/<id>` branches (`WorktreeTaskLifecycle.BranchFor`), remote
`runner/<runner-name>/<task-key>` card refs plus run-scoped immutable result refs, and salvage
recovery refs. The integration side resolves it as `review-subject.json → ResultRef` when
present, else `task/<id>` (`MergeIntoDevelopRunner.RunSerializedAsync`,
`backend/Features/Pipeline/MergeIntoDevelopRunner.cs:174-224`). Owner: split — runner writes the
refs, backend records which one counts. Lifetime: undefined (refs are never systematically
retired — a breakpoint source, BP-03).

**ResultEnvelope** — Immutable trio proving what a run produced: `BaseSha` + `ResultSha` +
`ImmutableResultRef`, plus `ArtifactManifestDigest`, bound to `RepositoryId` and the attempt id,
digest-validated (`SettleRun`, `AttemptAuthorityService.cs:341-371`; assembled in
`runner/RemoteTaskRunner.cs:259-299`; degraded gracefully via `BuildEnvelopeCompletionFields`,
`RemoteTaskRunner.cs:1059-1069`). Owner: runner produces, Task Server validates and stores.
Lifetime: retained with the attempt; a subject without one terminalizes as
`SnapshotUnavailable` after a 15-min grace (`LegacyEnvelopeTerminalizeGrace`,
`TerminalizeLegacyReviewSubjectsWithoutResultEnvelope`, `AttemptAuthorityService.cs:692-744`).

**Salvage** — Fail-closed rescue of worktree content before teardown: commit whatever exists,
push it to the card ref (never to base branches — allowlist hardening after AGT-2423), report
branch/SHA in the completion (`Salvage*` fields of `RemoteRunCompletionRequest`;
`GitWorkspace.TeardownAsync` / `SecureForHandoffAsync`; unsecured-worktree escalation
`RemoteTaskRunner.ReportUnsecuredWorktreeAsync`). Owner: runner. Salvage evidence is
deliberately *not* promoted into the review subject (`LeaseEndpoints.cs:742-744`). Lifetime:
until an operator or integration consumes the ref — today unbounded.

**Attribution** — Binding commits to the task that produced them, in `task.json commits[]`.
Local: mtime/session-window scoping + rule engine (`TaskTransitionService.RunCommitAttribution`,
`CommitAttributionRunner`). Remote: delivery-range inspection `merge-base..resultSha` with a
foreign-commit guard (`RemoteCommitAttributionGuard`, invoked in `LeaseEndpoints.cs:806-863`).
Owner: backend, at the Progress→AutoReview crossing / completion ingest. Consumed by review
scope and integration status — when it is wrong, "Delivered" lies (AGT-2400/2242 series).

**Backstop** — Periodic durable sweeper that re-derives a fact the volatile path may have
dropped. Integration: `AcceptedIntegrationBackstopHostedService` (15-min default) replays
accepted-but-unmerged deliveries; `CompletedPushBackstopHostedService` re-pushes. Owner:
backend. The dangerous property: a backstop is a second *writer* of the same fact as the live
path, gated only by recorded step verdicts (BP-02, BP-15).

**Phase** — Sub-state below the lane, two disjoint vocabularies: card lifecycle phases
(`lifecycle.json`, e.g. `post-processing-running`, written in
`TaskTransitionService.EnterPostProcessingPhase`) and runner slot phases
(`claimed | launching | running | finalizing | authority-deadline-exhausted` in
`runner/RunnerStateStore.cs` slot files). Owner: whoever wrote it — there is no single phase
authority. Lifetime: until overwritten; consumed by recovery scans and the phase-aware watchdog.

**Orphan** — Execution without a recognized owner: a detached CLI process whose daemon died, a
worktree without a live attempt, a Progress card with a free lease. Detection: server-side
requeue policy (`RemoteRunRequeuePolicy` inside `/api/runner/claim`,
`LeaseEndpoints.cs:270-335` — requires grace elapsed *and* the assigned runner answering that
the task is absent from its active set), runner-side `DurableAgentProcess.VerifyLive`
(PID + process start time), reapers (`WorktreeProcessReaper`, `OrphanReaperHostedService`).

**Backfill** — Boot-time repair that re-derives lane/queue state after a restart: verdict-less
Human-Review repair (AGT-2345 — now protects the latest human lane verdict), auto-review
post-processing re-enqueue (`TaskTransitionService.RequeueAutoReviewPostProcessing`),
accepted-integration recovery (backstop above). Owner: backend startup. Risk class: backfill is
a *writer* that trusts journals over the folder reality (BP-09 context, AGT-2345).

**Reissue** — Sending a card back to `2-ready` for another attempt (auto-review verdict,
environment-failure retry budget, requeue recovery). Bounded by `ReissueLoopBreaker` /
`CompletionRetrigger` budgets; identical-prompt loops additionally suppressed since AGT-2387.
Owner: backend orchestration. Every reissue creates a *new* attempt lineage; the old attempt's
refs and sidecars stay behind (BP-03, BP-12).

**dependsOn-Gate** — Claim-admission filter: a Ready card whose `waitsOn` references are not
satisfied is not eligible (`EvaluateWaitsOn` in the claim snapshot,
`LeaseEndpoints.cs:337-358`). Owner: backend at claim time only — nothing re-validates the
dependency after the claim (BP-17).

---

## 2. Breakpoint catalog (ordered by severity)

Severity scale:
- **S1** — produces silently wrong results or silent data loss (worst: nobody notices).
- **S2** — double execution / stranded work; needs operator intervention; loud or eventually loud.
- **S3** — churn, availability, throughput; annoying but honest.

Each breakpoint: scenario as a step sequence (attacker or chance), broken end state, code
anchors, and 1–2 hardening *directions* (not designs - the Status-Dossier decides).

### S1 — silently wrong

**BP-01 · Git is an unfenced side-effect channel** — the fence protects the server, not origin.
1. Runner A holds the lease for T, prepares worktree, starts the CLI.
2. Network partition / clock skew: server-side lease expires; runner-side `stop-before`
   (`DurableLeaseAuthority.ComputeStopBefore` = ExpiresAt − heartbeat margin, evaluated against
   the *runner's* local clock) has not fired yet — or the daemon is dead and only the deadline
   watcher (`RemoteRunnerDaemon.EnforcePersistedAuthorityDeadlineAsync`) would reap, later.
3. Server takes over: `AcquireRun` supersedes A's attempt, mints fence n+1 for runner B.
4. A's worker finishes and runs teardown/salvage: `git push` to
   `runner/<runner>/<task-key>` — the same card ref B will push. Every server write from A is
   rejected (StaleFence — correct), **but the pushes are not fenced**: origin now carries either
   a non-FF collision or, with force-with-lease semantics on retry, A-over-B / B-over-A content.
5. Whichever ref state survives is what `review-subject.json → ResultRef` and integration
   consume. AGT-2423 is the proven cousin: salvage tips became fast-forward integration tips of
   `main`, allowlist notwithstanding — the allowlist constrains *ref names*, not *authority*.
   End state: integrated content whose author-attempt lost the fence race; nobody is alerted
   because every individual step succeeded.
   Anchors: `runner/RemoteTaskRunner.cs` teardown/salvage paths; `GitPushProbe`; allowlist
   hardening described in `orchestrator-drive-to-conclusion.html` (AGT-2423 row);
   `MergeIntoDevelopRunner.cs:174-224` (consumes ResultRef).
   Hardening directions: (a) make the fence visible in git — embed attempt id + fence in the
   ref name (`runner/<host>/<task>/<attemptId>` or run-scoped refs only) so two generations can
   never target the same moving ref, and let integration accept only the ref named by the
   *settled* attempt's envelope; (b) treat origin pushes as fenced deliveries: pre-push probe
   asks the server "is this attempt still current?" and refuses the push (salvage then parks
   under a quarantine namespace instead).

**BP-02 · Crash inside the merge+gate boundary turns into an ungated push via the backstop.**
1. Operator accepts card T; `AcceptedIntegrationWorker` merges `task/T → develop`
   (`MergeIntoIntegrationOutcome.Merged`), pre-develop build gate starts.
2. Hard death (power loss, OOM-kill, watchdog that ignores the AGT-2418 drain — the drain only
   covers the *cooperating* watchdog) after the merge commit exists but before gate verdict /
   rollback / `Record()` are durable.
3. Restart. `pipeline-execution.json` has no passed merge step → backstop `RunOnce` re-runs the
   idempotent runner. The merge commit is already on the local integration branch →
   `IsAncestor` → **AlreadyMerged**.
4. `AlreadyMerged` is recorded `Passed` and enqueues a push with
   `approvedSha = current branch tip` — the **gate never ran** on this merge, and the push
   publishes it. End state: unverified merge on origin/develop with a green pipeline step.
   Anchors: `MergeIntoDevelopRunner.cs:236-249` (approvedSha for AlreadyMerged = branch tip),
   `MergeIntoIntegrationGatedAsync` (gate runs only on `Outcome == Merged`),
   `AcceptedIntegrationBackstopHostedService.cs:93-105`.
   Hardening directions: (a) persist a durable "merge-in-flight" intent marker *before* the
   merge commit and treat its presence at recovery as "roll back to recorded pre-merge tip, then
   re-merge through the gate" — the rollback anchor is already computable
   (`GetFirstParent(mergedSha)`); (b) never let `AlreadyMerged` release a push unless a durable
   gate verdict exists for exactly that SHA (gate evidence is already SHA-stamped in
   `post-steps/pre-develop-build-gate-N.log`).
   Implementation status (AGT-2457, 2026-07-31): direction (b) is implemented for the current
   backstop. An `AlreadyMerged` replay with an applicable pre-develop gate now reuses only a
   complete green receipt whose expected and tested SHA equal the integration tip; otherwise it
   runs the missing gate. The pipeline step remains pending and no push request is released while
   that gate runs. A red recovery gate records `GateFailed` and releases no push. The broader
   Dossier target, merge intent plus isolated candidate promotion, remains separate work.

**BP-03 · Stale `review-subject.json` outlives its attempt and re-targets integration.**
1. Card T runs remotely; completion writes `review-subject.json` (ResultRef = runner ref R1,
   ResultSha = S1) into the card folder (`LeaseEndpoints.cs:891-907`).
2. Review fails; T is reissued and the second attempt runs **locally** (project flipped to
   local, or a local operator-driven run). The local path never writes or clears
   `review-subject.json` — only remote completions write it, and no transition deletes it.
3. Operator accepts the local delivery. `MergeIntoDevelopRunner` reads the folder: subject
   exists → `taskBranch = R1`, merges **S1** — the superseded remote result — instead of
   `task/T`. The backstop's legacy `no-branch` replay path has the same trust
   (`AcceptedIntegrationBackstopHostedService.cs:86-88`).
   End state: old work integrated as if it were the accepted delivery; the accepted local
   commits sit unmerged; the board says Delivered. This is the local→remote→local wander case
   with a poisoned truth-carrier.
   Anchors: `ReviewSubjectStore.Write` call site `LeaseEndpoints.cs:891-907`;
   `MergeIntoDevelopRunner.cs:174-183`.
   Hardening directions: (a) make the subject attempt-scoped, not folder-scoped: stamp
   `runAttemptId` into `review-subject.json` and have integration cross-check it against the
   *current* settled attempt in the authority store before trusting ResultRef; (b) lifecycle
   rule: any transition that mints a new run attempt (reissue, requeue) invalidates/archives
   attempt-scoped sidecars of the previous generation.

**BP-04 · `task.json` is multi-writer with non-atomic writes.**
1. Backend mutation (`TaskMutationService.WriteAllTextWithRetry`) opens
   `FileMode.Create` + `FileShare.ReadWrite|Delete` — truncate-then-write, readers admitted
   mid-write, no temp-file+rename (`TaskMutationService.cs:923-940`; contrast the runner's own
   `RunnerStateStore.Save` and the authority store's `IAtomicJsonFileWriter`, which do this
   correctly).
2. Concurrently, a second writer touches the same card: operator edit, a salvage/repair session
   (the documented card-scoped manual salvage works directly in the task store), or the
   watcher-triggered scanner re-serializing.
3. Interleaving A: reader (scanner, review, integration status) sees a truncated JSON → parse
   error → card temporarily "gone" or defaulted. Interleaving B: read-modify-write races —
   backend stamps `commits[]` from a stale in-memory `TaskInfo` while the operator's
   tags/lane-order write lands in between → whole-file last-writer-wins, fields silently lost.
   End state: exactly the class of quiet truth corruption the "task.json is the truth" doctrine
   cannot tolerate — and it is invisible because the file is valid JSON afterwards.
   Anchors: `backend/Features/Tasks/TaskMutationService.cs:905-940, 779`;
   scanner cache invalidation paths.
   Hardening directions: (a) one write discipline for every truth file: temp-file + atomic
   rename + (where fields are independently owned) per-fact sidecars merged at read, instead of
   whole-file rewrite; (b) an explicit single-writer rule per field family (backend owns
   `commits[]`/state-adjacent fields; operator writes go through the API, never the file), with
   a drift detector that flags out-of-band mtime changes.

**BP-05 · A derived-truth writer can clobber a primary store after a failed load
(registry legacy-reseed class, AGT-2401).**
1. Primary store (`.metadata/projects.json`) becomes unreadable (schema drift, partial write).
2. Loader catches, logs, continues with an **empty** collection.
3. A seeding/migration path sees "empty" as "first boot" and rewrites the file from legacy
   inputs → PROJ-012..017 silently deregistered; every dependent resolution (repositoryId for
   leases, claim admission, envelope `RepositoryId` matching in `SettleRun`) now disagrees with
   reality; completed remote runs can be *refused* terminally (`SubjectMismatch`) because the
   repository identity flipped mid-flight (cousin: PROJ-002 repositoryUrl claim-block).
   Fixed fail-closed for the registry — the catalog entry is the **class**: any component that
   (re)generates a store it did not exclusively own. Candidates with the same shape: attempt
   authority (load failure throws — good), runner slot files (`InvalidDataException` — good),
   `project-settings.json`, tags, pipeline logs (best-effort `EnsureRun` on read).
   Hardening directions: (a) an inventory rule: every durable store declares exactly one writer
   and a fail-closed load policy, verified by a startup self-check; (b) seeding may only ever
   *create-new*, never overwrite an existing path (O_EXCL semantics + quarantine of the
   unreadable original).

**BP-06 · Gate rollback `ResetHard` erases out-of-band commits on the shared integration
checkout.**
1. Accept of T starts the serialized merge+gate (`_mergeGate` — in-process semaphore).
2. During the gate (minutes: cold build), an actor **outside** this process commits to the local
   integration branch: an operator terminal, a Claude salvage session doing card-scoped
   integration (daily practice on this repo), a second backend instance pointed at the same
   checkout.
3. Gate fails → `_git.ResetHard(repoRoot, preMergeTip)` — the rollback anchor predates the
   out-of-band commits → they are gone from the branch (recoverable via reflog only, i.e.
   effectively silent).
   Anchors: `MergeIntoDevelopRunner.cs:327-395`.
   Hardening directions: (a) roll back surgically: verify the branch tip is still exactly
   `result.MergedSha` before ResetHard, otherwise stop and escalate ("branch moved during
   gate"); (b) run merge+gate in an isolated worktree/ref (the gate already builds in one) and
   fast-forward the real branch only on green, making rollback a no-op.

**BP-07 · `integrationBranch` line lie / line switch (AGT-2400 → AGT-2423).**
1. Runner claims T; the base line it actually prepares from (`defaultBranch` of the registered
   repo, e.g. `main`-line) differs from the card's recorded `integrationBranch`
   (e.g. `develop`) — or the completion reports one while the delivery was built on the other.
2. Completion ingest stamps the reported value (`SetRunIntegrationBranchOnFolder`,
   `LeaseEndpoints.cs:812-819` — trusts the runner's report, corrected only when the
   delivery-range inspection succeeds).
3. Accept resolves the merge target from that field
   (`TaskIntegrationBranch.Resolve(job, settings)`): main-line objects merge into develop (line
   contamination) or the card flips to the `main` release path where `MergeBranchFastForward`
   makes the salvage/wip commit graph the literal `main` first-parent history — exactly the
   AGT-2423 surface.
   End state: cross-line contamination or wip-subjects on the release line; both look green.
   Anchors: `LeaseEndpoints.cs:812-863`; `MergeIntoDevelopRunner.MergeIntoMainAsync`
   (fast-forward, `MergeIntoDevelopRunner.cs:506-514`); backstop resolve path.
   Hardening directions: (a) derive, never trust: integration line = f(envelope BaseSha
   ancestry), computed server-side from git, with the runner report demoted to a hint that must
   agree; (b) release-line merges get a structural gate: refuse fast-forward of tips whose
   commit subjects/authors match the wip/salvage pattern — release history must come from a
   curated merge commit.

### S2 — double execution / stranded work

**BP-08 · Acquire-replay reconstructs the claim response but not the lane: Ready+leased split.**
1. Daemon claims; server persists lease, `MoveAsync(→Progress)` **succeeds or not** — crash /
   response loss happens after `TryAcquire` but before the runner receives the body.
2. Runner retries with the same idempotency key. Replay path
   (`LeaseEndpoints.cs:199-257`) rebuilds the response from the durable acquire — and returns
   `Claimed` **without re-driving the lane move**. If the original crash happened between
   acquire-persist and lane move, the card sits in `2-ready` *with a live lease*.
3. Ready is the claim-eligibility state: the local ProjectRunner (which does not consult the
   remote lease plane for admission) or a second remote contender selects it; the second
   `AcquireRun` is blocked only while the first lease is unexpired (`InvalidState`) — after one
   missed renewal window the takeover mints fence n+1 and a **second full execution** starts
   while runner 1 is mid-run. Both later push card refs (BP-01 compounding). Seed: the observed
   double-claim after lease death.
   Anchors: `LeaseEndpoints.cs:199-257` (replay), `531-548` (move + rollback release on refusal
   — but only in the non-replay path); `RemoteRunnerDaemon` claim shutdown handling.
   Hardening directions: (a) replay must converge state, not just answer: re-assert the lane
   (`Progress`) with the same authority write before returning `Claimed`; (b) make lane and
   lease admission one predicate everywhere — local pickup must treat "current live lease
   exists" as claimed regardless of folder lane.

   **Status (fixed by AGT-2459):** daemon acquire replay now classifies the current lane before
   rebuilding the response. Ready is moved to Progress under the claim gate with the original
   AttemptId, fence, authority epoch, and deterministic `lane-claim:<claim-key>` transition
   key. Progress is accepted as already converged; every other lane or failed transition returns
   no claim. The endpoint reads task truth back after the transition and cannot return `Claimed`
   while the card remains Ready. Endpoint coverage reproduces the persisted-acquire/lost-move
   crash boundary and proves that a contender cannot open a second RunAttempt after replay.

**BP-09 · Settled run + lost lane move → requeue → second execution supersedes a good result.**
1. Remote completion: `SettleRun` persists (attempt Completed, envelope stored), review attempt
   created — then the backend dies before the `AutoReview` lane move; card remains `Progress`.
2. Runner's outbox has delivered; the daemon's next claim polls report T absent from the active
   set. Requeue policy fires (`lane-recovery`): T moves `Progress→Ready` with
   `suppressProductExecution` and is claimed again — a **new attempt for already-completed
   work**.
3. The new attempt's settle supersedes the completed one and kills the pending review
   (`SettleRun`/`CreateReviewAttempt` supersede cascade). Best case: double cost + late
   delivery; worse: attempt 2 fails/diverges and the good, already-enveloped result of attempt 1
   is now `Superseded` — recoverable only by hand (this is the phantom/lost-evening-wave shape).
   Anchors: completion pipeline order `LeaseEndpoints.cs:784-1258` (settle → timeline →
   lane move); requeue `LeaseEndpoints.cs:270-335`.
   Hardening directions: (a) requeue must ask the authority store first: a task whose *current*
   run attempt is `Completed` with an envelope is never requeued — it is driven forward
   (re-enter the completion pipeline server-side, exactly what the recorded
   `SourceRunAttemptId` lineage was built for); (b) collapse "settle + review-create + lane
   move" into one recovery-replayable unit keyed by the completion delivery, with a boot scan
   for settled-but-unmoved attempts.

**BP-10 · Runner state-dir loss orphans a live detached worker and re-runs the task.**
1. Worker runs detached; daemon dies; `RUNNER_STATE_DIR` slot files are lost/corrupted (disk,
   operator cleanup, container without the volume — the container-default shift raises exactly
   this risk).
2. Replacement daemon boots with zero slots, polls; its `ActiveTaskKeys` honestly omits T →
   server requeues T after grace → second claim, possibly on the same host into the **same
   worktree path** (`GitWorkspace` derives the path from task key) while the orphan CLI still
   writes there.
3. End state: interleaved working trees / duplicated pushes; the orphan's eventual result can no
   longer be attributed (its authority was superseded), its process survives until a reaper
   correlates it via the worktree.
   Anchors: `RunnerStateStore.LoadAll` (throws on unreadable — good, but absent files are
   simply *absent*); `RemoteRunnerDaemon.RunAsync:81-137`; `WorktreeProcessReaper`.
   Hardening directions: (a) the worktree itself is durable evidence — boot must scan the
   workspace root for foreign/unslotted worktrees and reap or adopt before advertising free
   slots (a Windows sweeper exists; make it a startup admission gate on Linux too);
   (b) attempt-scoped worktree paths (include attempt id) so a re-claim can never collide with
   an orphan generation's checkout.

**BP-11 · Epoch rotation was a global kill switch with a slow blast wave (mitigated by
AGT-2461).**
1. Former behavior: `RotateAuthorityEpoch(reason)` superseded every non-terminal attempt, so
   in-flight workers lost renew and completion authority together and produced a synchronized
   requeue wave.
2. Current behavior: rotation advances only the generation for new claims. Already leased run
   and review attempts remain current with their original epoch and drain through normal renew,
   report, settlement, release, or expiry. A contender cannot replace a live draining lease.
   Pending review work and every genuinely new claim receive the new epoch. Restart reconstructs
   the same rule from the persisted current epoch plus each attempt's own epoch.
   Anchors: `AttemptAuthorityService.RotateAuthorityEpoch`, attempt write validation, and
   `RunLeaseService.Peek` / `IsCurrent`.
   Remaining hardening direction: repository or project scoping can still reduce the semantic
   blast radius of a generation change, but it is no longer required to prevent mass stranding.

**BP-12 · Attribution loses cross-generation and cross-runner commits ("Delivered" lies).**
1. T's chain spans generations: local attempt (commits on `task/T`), reissue, remote attempt
   (commits on `runner/.../T`). Remote attribution inspects only
   `merge-base..ResultSha` of the *last* delivery branch (`LeaseEndpoints.cs:829-852`); local
   attribution scopes by *this* run's session window.
2. `commits[]` ends up: last-generation-only (earlier real work invisible to review scope and
   integration status), or — the AGT-2242 inversion — a wrongly-based branch attributes
   *foreign* commits, producing confident Grade-D reviews of someone else's diff.
3. Integration status (`IsFencedDeliveryIntegrated`, ancestor probes over `commits[]`) then
   reports Delivered/NotDelivered against the wrong commit set — in both directions.
   Anchors: `LeaseEndpoints.cs:806-863`; `TaskTransitionService.cs:574-596`;
   `CommitAttributionRunner`; memory: review-scope = attributed commits.
   Hardening directions: (a) attribution as an append-only per-attempt ledger
   (attempt id → commit set), with card-level `commits[]` a *projection* over all
   non-superseded attempts, never a rewrite; (b) a truth-check post-step: every commit in
   `commits[]` must be reachable from a ref that the authority store links to one of this
   task's attempts — anything else is quarantined with a warning, not silently kept.

**BP-13 · Same-identity re-claim after lease death — the coding plane lacks the review plane's
guard.**
1. Review plane (fixed): dead lease + same claim delivery key → keep answering `LeaseExpired`
   because the claiming *process* may still run (`CurrentClaimDeliveryKey`,
   `AttemptAuthorityService.cs:553-573`); daemon in-flight dedup catches the rest.
2. Coding plane: after a renew outage kills the lease mid-run, the *same daemon* keeps its
   worker alive (heartbeat marks LeaseLost; worker outcome path returns 3). But nothing
   prevents the very next claim poll of the same daemon from being handed **T again** via
   requeue (new attempt, new worktree — or the same path, BP-10) while its first worker still
   finishes; the daemon's only shield is `ActiveTaskKeys` honesty, which depends on the
   inventory tracker having registered the run — a daemon restart between renew-loss and reap
   drops it.
3. PID-reuse footnote: `VerifyLive` matches PID + start-time; on hosts where start-time
   granularity is coarse (container restarts with PID namespaces starting at 1), a recycled PID
   with matching start-time is theoretically adoptable — the wrong process would be "proven
   live" and adopted to 4-auto-review by the bounded replay.
   Anchors: `runner/LeaseHeartbeat.cs` (LeaseLost), `RemoteRunnerDaemon` claim loop,
   `DurableAgentProcess.VerifyLive`, requeue policy `LeaseEndpoints.cs:270-335`.
   Hardening directions: (a) port the review plane's rule: a requeue-minted claim for T must
   carry the superseded attempt as `SourceRunAttemptId` (it does) **and** be refused delivery to
   the executor that still reports T in its inventory (server-side check against the same
   poll's `ActiveTaskKeys`); (b) strengthen process identity beyond PID+start-time (worker
   writes an attempt-scoped token file the daemon must read back).

### S3 — churn / availability

**BP-14 · `ExecuteRunWrite` performs the side effect before persisting the delivery key.**
Persist failure after `sideEffect()` → in-memory rollback (`PersistLocked` catch reloads
durable state) but the side effect may already be durable elsewhere; the retry re-executes it.
The contract is documented ("side effects must deduplicate on delivery identity") — log ingest
does; every *future* side effect wired through this hook is one forgotten receipt away from
double-append. Anchors: `AttemptAuthorityService.cs:255-295`. Directions: (a) invert where
possible (persist-intent → effect → persist-ack); (b) a test-enforced registry of side-effect
kinds with proof of their dedup key.

**BP-15 · Backstop churn: `Error` outcomes retry forever, and the sweep includes Archive.**
`RequiresSweep` = pending tag *or* no passed merge step; decided-state skips cover
conflict/pushed-for-review/gate-failed/no-branch — but **not** `Error` (repo unresolvable,
exception): such a card is re-merged every 15 minutes indefinitely, holding the global
`_mergeGate` and hammering git, including for `7-archive` cards from months ago
(`ScanAllJobsWithArchive`). Anchors: `AcceptedIntegrationBackstopHostedService.cs:44-115,
160-168`. Directions: (a) budget + decided-state for repeated `Error`; (b) age/lane cutoff for
sweep eligibility.

**BP-16 · The 15-minute envelope grace is a clock bet against real outages.**
A completion ingest delayed > `LegacyEnvelopeTerminalizeGrace` (server down over the runner's
outbox replay window, exactly the 10-minute-autonomy scenario stretched) leaves the review
subject envelope-less past grace → terminalized `SnapshotUnavailable` → card escalated although
a valid delivery arrives minutes later; the late settle then races an already-terminal review.
Anchors: `AttemptAuthorityService.cs:24, 628-652, 692-744`. Directions: (a) gate the grace on
*attempt liveness* (an attempt with a live lease or an outbox-known replay-in-progress never
terminalizes), not wall-clock only; (b) a late envelope on a terminalized subject triggers
automatic review re-create instead of operator repair.

**BP-17 · dependsOn is admission-only.**
`EvaluateWaitsOn` filters at claim; if the dependency is later reopened/reverted (its delivery
rolled back by a gate, its card reissued), the dependent — already claimed or already merged —
proceeds on a base that no longer contains what it depends on. No re-validation exists at
completion or integration time. Anchors: `LeaseEndpoints.cs:337-358`. Directions: (a) re-check
`waitsOn` at integration time against git reality (dependency's delivery ancestor of the merge
base); (b) dependency reopening emits a targeted flag on dependents instead of silence.

**BP-18 · One global `ClaimGate` serializes claims, requeues, and completions — with git work
inside the critical section.**
`/api/runner/claim` and `/api/runner/completion` share a single semaphore; inside it run full
task-store scans, requeue decisions, and completion-side `git` inspections
(`InspectRemoteDeliveryCommitRange`). One slow git call (cold FS, network FS, large repo)
stalls every runner's claims *and* completions. With the Task Server moving to the Hetzner VM
(AGT-2404) the poll fan-in rises and this becomes the plane's single availability choke point.
Not a correctness bug — the serialization is what makes claim admission atomic — but a
break-under-load point. Anchors: `LeaseEndpoints.cs:26, 43, 191, 684`. Directions:
(a) split the gate per task/project and move git inspection out of the critical section
(inspect, then re-validate + commit under the gate); (b) budget the completion path's git work
with a deferred-attribution fallback.

**BP-19 · Scheduler-mode truth decays against run reality (auto-single class, AGT-2405).**
The mode field (`auto-single`) reverted to `manual` while the project's only card was mid-run;
the later reissue found no pickup → 4h stall. Fixed for this path ("stays armed while a run,
finalisation record, post-processing phase, or 4-auto-review card exists") — the catalog entry
is the class: any *derived* scheduling state (mode, drain flags, capability pauses) that is
written once from a snapshot and then trusted. Directions: (a) derive, don't store: compute
"armed" from live run/lane facts at each tick; (b) where storing is necessary, every revert
names the evidence snapshot it was based on, and a mismatch at read time re-arms.

---

## 3. Cross-cutting observations (who wins at conflict — and is that right?)

The system has **five truth planes**: folder lane, `task.json` + sidecars, the attempt
authority store, the registry/settings plane, and git reality (origin refs). Current conflict
winners, as implemented:

| Conflict | Winner today | Verdict |
|---|---|---|
| Lane (folder) vs. authority attempt state | Folder for the board, authority for writes — requeue policy arbitrates | Right idea; BP-08/BP-09 show the arbitration has gaps at the seams |
| `review-subject.json` vs. newer local delivery | Sidecar wins unconditionally | **Wrong** (BP-03) — must be attempt-scoped |
| Runner-reported `integrationBranch` vs. git ancestry | Report wins unless range inspection succeeds | **Wrong default** (BP-07) — derive from BaseSha |
| Registry vs. reality after failed load | Was: reseed wins (AGT-2401). Now: fail-closed | Right now; enforce as a class rule (BP-05) |
| Pipeline step verdict vs. git state (backstop) | Step verdict decides replay; git decides AlreadyMerged | Composition creates the gate bypass (BP-02) |
| `commits[]` vs. refs the attempts actually own | `commits[]` wins for review + Delivered badge | **Wrong** when attribution is stale/foreign (BP-12) |
| Server clock vs. runner clock | Server (all authority timestamps `_utcNow`); runner only self-limits via stop-before | Right — but git pushes escape the arbitration entirely (BP-01) |

The doctrine "the task is the truth" is currently aspirational: the task *folder* is one of
five planes, its file writes are the least protected of the five (BP-04), and two sidecars
(review-subject, integrationBranch) override fresher evidence. The Status-Dossier decision
should name, per field, the single derivation source and demote everything else to hint.

---

## 4. Test-suite plan (planning only — no code here)

Relation to the running card **AGT-2427 "Fachliche Pipeline-Uebergangs-Tests: State-Machine-Suite
mit Edge-Case-Katalog"** (currently 5-human-review): AGT-2427 establishes the three pillars this
plan builds on — (1) a business-rule state-machine suite per allowed/forbidden transition,
(2) an edge-case catalog as tests, (3) task-as-truth assertions (after every transition,
`task.json` + folder carry the complete relevant state, no log-only facts). This chapter does
**not** duplicate that card; it maps *this document's breakpoints* onto its test classes and
names what AGT-2427 does not yet cover. Where AGT-2427's suite already lands a case, the
breakpoint row below only adds the missing assertion, as a follow-up finding into that card's
review rather than a new suite.

Test classes (business rules leading, integration tests subordinate — Robert 29.07.):

- **T-SM (state-machine test)** — pure transition rule against the folder+`task.json` pair;
  fixture-level, no network/git. The AGT-2427 base suite.
- **T-AUTH (authority-model test)** — `AttemptAuthorityService` with injected clock and
  injected failing `IAtomicJsonFileWriter`; already partially exists; extended for the fence /
  epoch / persist-crash cases. Task-as-truth applies transitively: after the driven recovery,
  the *card* state must be assertable from `task.json`, not from authority internals.
- **T-EDGE (edge-case test)** — multi-actor sequences over the real transition service +
  authority store, git faked at the service seam; asserts end state exclusively on
  `task.json` + folder + sidecars.
- **T-INT (integration test, MachineBound where load-dependent)** — real git repos in scratch
  dirs (`MergeIntoDevelopRunner`, backstop, salvage refs); marked per BuildProfile convention.
- **T-FAULT (fault-injection acceptance)** — the AGT-2393 isolated Remote-Run harness with its
  explicit activation interlock; for runner-side breakpoints only.

Per breakpoint:

| BP | Test class | Test sketch (business rule; assertion target = `task.json`/folder unless stated) |
|---|---|---|
| BP-01 | T-FAULT + T-INT | Two attempt generations for one task push their delivery refs; assert the settled card's `review-subject`/`commits[]` reference only the *current* attempt's ref, and integration refuses a ref not named by the settled envelope. |
| BP-02 | T-INT | Kill (simulated) between merge commit and gate verdict; run backstop; assert `pipeline-execution.json` does **not** show Passed and no push request is enqueued until a gate verdict exists for the exact SHA; `task.json` integration status stays pending. |
| BP-03 | T-EDGE | Remote completion writes subject → reissue → local delivery → accept; assert merge consumed `task/<id>` and `task.json` provenance names the local SHA; the stale sidecar is invalidated/archived. |
| BP-04 | T-EDGE | Concurrent mutation writers on one card (API mutation vs. tag write vs. attribution stamp); assert no field family is lost and every intermediate read parses — this is the test that forces the atomic-write decision. |
| BP-05 | T-SM | For each durable store: corrupt it, boot; assert the process fails closed (or quarantines) and no writer recreates the file over the corrupt original. |
| BP-06 | T-INT | Advance the integration branch out-of-band during a failing gate; assert rollback refuses (tip ≠ mergedSha) and the card records gate-failed with the escalation reason, out-of-band commit intact. |
| BP-07 | T-EDGE + T-INT | Completion reports `integrationBranch=develop` for a main-based envelope; assert the card's recorded line is derived from BaseSha ancestry and accept merges into the derived line (or refuses loudly). |
| BP-08 | T-EDGE | Acquire persisted, lane move lost, replay claim; assert card is in `3-progress` after replay (`task.json` state + folder), and that a Ready card with a live current lease is claim-ineligible for every other actor. |
| BP-09 | T-EDGE | Settle persisted, lane move lost, restart, runner poll without T; assert requeue is refused and the card is driven to `4-auto-review` with the original attempt's envelope; `commits[]` reflects attempt 1. |
| BP-10 | T-FAULT | Delete slot files under a live detached worker; boot replacement; assert no second claim for T is admitted while the worktree scan reports an unslotted live generation. |
| BP-11 | T-AUTH | Rotate with multiple leased run/review attempts and a pending review; assert leased old-epoch attempts remain current, renew and settle, contenders are refused, pending/new claims use the new epoch, and restart preserves the drain. |
| BP-12 | T-EDGE | Chain local-attempt → reissue → remote-attempt; assert `commits[]` is the union of non-superseded attempts' own commits and contains no commit unreachable from this task's attempt refs (AGT-2242 guard). |
| BP-13 | T-EDGE | Requeue mints a new claim while the same executor still reports T active; assert the claim is refused server-side (not just skipped client-side). |
| BP-14 | T-AUTH | Failing writer injected after side effect; retry delivery; assert the side effect's own dedup receipt prevented the double-append (per registered side-effect kind). |
| BP-15 | T-SM | Backstop over a card with repeated `Error` merge outcome and over an Archive card; assert bounded retries and sweep-eligibility cutoff. |
| BP-16 | T-AUTH | Clocked test: envelope arrives at grace+ε while attempt replay is in progress; assert no SnapshotUnavailable terminalization while liveness evidence exists, and a late envelope re-creates the review. |
| BP-17 | T-EDGE | Dependency reopened after dependent claimed; assert integration-time re-check flags the dependent (`task.json` tag/timeline), not a silent merge. |
| BP-18 | T-INT (MachineBound) | Latency injection on the git seam inside completion; assert claim latency for unrelated tasks stays bounded (or document the accepted coupling). |
| BP-19 | T-SM | Mode revert decision table: for each (pickup-queue, live-run, post-processing, 4-auto-review) combination assert armed/reverted per the AGT-2405 rule. |

Sustainment in the target picture (distributed, multi-runner): the T-SM and T-EDGE suites stay
in `backend.Tests` and run on every gate; T-INT carries the BuildProfile MachineBound markers;
T-FAULT lives in the AGT-2393 harness with its production-import firewall and runs as the
separately marked acceptance phase (runner restart and task-server restart remain distinct
phases, per the AGT-2396 acceptance note). The invariant across all classes is AGT-2427's third
pillar: **every assertion of "what state is the system in" must be answerable from
`task.json` + folder + declared sidecars** — a test that needs a log line or an in-memory
peek to know the state has found a BP-04-class gap and should fail for that reason.

---

## 5. The hardest breakpoints, compact

1. **BP-01** Unfenced git side-effects: a fenced-out generation can still push the card ref the
   next generation delivers on — integration can consume the loser.
2. **BP-02** Crash inside merge+gate → backstop replays as AlreadyMerged → **ungated push** of
   an unverified merge with a green step.
3. **BP-03** Stale `review-subject.json` re-targets integration to a superseded remote result
   after a local reissue — Delivered shows the wrong work.
4. **BP-04** `task.json` truncate-write + multi-writer: torn reads and last-writer-wins field
   loss on the file that is supposed to be *the* truth.
5. **BP-07** `integrationBranch` is trusted from the runner report — line contamination and
   salvage subjects on the `main` first-parent line (AGT-2423 shape).
6. **BP-08 (fixed by AGT-2459)** Claim replay now re-drives and verifies Ready to Progress with
   the original authority tuple before answering `Claimed`; the Ready+leased split is closed on
   replay.
7. **BP-09** Settled-but-unmoved card gets requeued: a completed, enveloped result is superseded
   by a redundant second run.
8. **BP-06** Gate-failure `ResetHard` on the shared checkout erases out-of-band commits made
   during the gate window.
9. **BP-10** Lost runner slot files → live orphan worker + admitted re-claim into the same
   worktree path.
10. **BP-11 (mitigated by AGT-2461)** Epoch rotation now drains already leased attempts while
    assigning the new generation only to new claims; repository/project scoping remains open.
11. **BP-12** Attribution is last-generation-only and can adopt foreign commits — review scope
    and the Delivered badge argue about the wrong commit set.
12. **BP-05** The reseed-clobber class: one failed load plus one well-meaning seeder silently
    rewrites a primary store others derive identity from.
