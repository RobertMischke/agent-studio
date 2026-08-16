# Public demo runtime replacement

`reset-demo-runtime.sh` replaces a complete runtime from one approved S5 bundle.
The operator-pinned archive digest is the trust anchor; hashes declared only
inside the archive are not sufficient. It never rewrites the datastore used by
the active Task Server. Each installed instance keeps a read-only pristine
release payload and a separate fresh writable runtime datastore.

The caller supplies four executable hooks. Each receives the candidate release
directory as its first argument and the current release directory as its second:

1. `start-hook` starts the candidate against its bundled datastore.
2. `probe-hook` checks browse behavior and the S2 execution-denial matrix.
3. `switch-hook` performs the host-specific atomic traffic cut.
4. `stop-hook` is optional and stops the release that no longer serves traffic.

The switch hook must either complete its host-specific cut atomically or return
nonzero without changing traffic. A successful reset retains the former target
through `previous`. `rollback-demo-runtime.sh` verifies that target's pristine
payload, creates a new runtime from it, starts and probes the new instance, and
then switches traffic. It never reactivates the former writable datastore.
On the first activation the current-release argument is empty. The switch hook
must accept an empty target as the rollback request that disables candidate
traffic if the local active-link update fails.

After a successful cut, cleanup retains only `current` and `previous`. Older
writable runtime drift is removed; the externally approved bundle remains the
durable recovery source.

Install the service and timer templates only as part of the separately approved
S6 host launch. The timer runs once after boot and then every six hours. Starting
the service directly is the on-demand and deploy-time replacement path.
