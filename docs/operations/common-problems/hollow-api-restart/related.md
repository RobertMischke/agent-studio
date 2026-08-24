# Related

- [[services-killed-by-harness-sweep]] - the mirror image: a sweep that terminates too
  much rather than too little. Read both before widening any process-matching rule.
- [[stale-progress-orphan-race]] - another orphaned-process failure, on the job-folder
  side.
- ADR-0044 (dev backend lifecycle, Playwright-only) - the boot gate at the top of
  `cmd_start`; the fix keeps it intact.
- ASS-1715 (isolated worktree test stack) - why the sweep must leave a same-checkout
  process that serves a different port alone. See `scripts/worktree-test-stack.sh`.
- Code: [`api.sh`](../../../../api.sh), [`tools/api-restart-selfcheck.sh`](../../../../tools/api-restart-selfcheck.sh).
- Docs: [contributor-setup.md](../../setup/contributor-setup.md) step 2.4 and the
  dev + stable side-by-side reference, [troubleshooting.md](../../setup/troubleshooting.md).
