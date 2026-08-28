# Ideas

Hypotheses, open questions, ruled-out approaches.

- **Open: disable the legacy per-commit completed push for dual-line projects.**
  `TaskTransitionService.PushJobCommitsAsync` targets `main` directly on every
  `6-completed` transition and is rejected by design once a project has both
  `develop` and `main`. It is most of the 570+ warning volume but does not
  touch `origin/develop` or block acceptance. Gating it off
  `AcceptanceIntegrationPolicy`/the project's configured integration branch
  would remove the noise; deferred to a follow-up card so it gets its own
  project-settings-driven test rather than riding on this git-behavior fix.
- **Considered: force-push `develop` when local and `origin` have diverged.**
  Rejected. A blind force-push can silently discard another writer's commits;
  the fix keeps a genuine non-fast-forward failing closed and instead makes
  that failure honest (`integration-push-blocked`, not `pending`) so an
  operator resolves the real divergence.
- **Considered: skip `RecordPushStep` bookkeeping and instead have
  `TaskIntegrationStatusService` treat "no push step at all" as a failure.**
  Rejected: a card can legitimately be mid-flight between merge and its first
  deferred push attempt (the queue is async, off the request path by design);
  treating an absent step as a failure would misclassify perfectly healthy,
  brand-new deliveries. Recording the blocked/rejected result as an explicit
  terminal step is the correct fix - it makes the *actual* failure visible
  without inventing a failure for the merely-not-yet-attempted case.
- **Ruled out: reading `origin/develop` strictly (no local-branch fallback) in
  `TaskIntegrationStatusService`.** The existing union read
  (`[integrationBranch, "origin/"+integrationBranch]`) is deliberate - it lets
  a same-repo, same-process read see its own just-merged local commit before
  the deferred push lands, instead of a card reading `pending` for the entire
  push-queue latency window on every single delivery. The real bug was the
  push never running, not the read being too lenient.
