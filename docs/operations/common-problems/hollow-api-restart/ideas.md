# Ideas

Hypotheses, open questions, ruled-out approaches.

- **Open: `update-service.sh` shares the shape.** It resolves a single PID from
  `netstat` and force-kills it, with no process-tree walk and no identity check. Its port
  post-condition check (`WARN: port still occupied`, exit 1) makes it better than the old
  `api.sh`, but a non-listening orphan is still invisible to it. Not changed under
  AGT-2678 because the UpdateService is deliberately the one process that must survive a
  backend restart, so its sweep rules differ.
- **Open: outer devspace wrappers.** `start-stable.sh` / `stop-stable.sh` live above the
  checkout and are not versioned here. `scripts/update-stable.sh` calls the stop wrapper
  and then fast-forwards the checkout, so a wrapper that swallows the exit code
  reintroduces the incident one level up. Documented in
  [contributor-setup.md](../../setup/contributor-setup.md); consider asserting it in
  `scripts/test-update-stable.sh`.
- **Ruled out: killing by process group.** `nohup dotnet run &` in a non-interactive
  shell shares the script's process group, so a group kill would take out `api.sh`
  itself. The descendant walk over the ppid table is used instead.
- **Ruled out: a longer health-check timeout or a retry loop.** The old process answers
  `/healthz` correctly, so no amount of waiting distinguishes it from a new one.
- **Considered: `--no-build` plus an explicit `dotnet build` step.** Would remove the
  MSBuild worker from the picture entirely and make the "who holds the DLLs" question
  simpler, at the cost of changing the boot contract. Out of scope for AGT-2678.
