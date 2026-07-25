# Integration evidence

## Tranche 0

- AGT-2330 `4c0346b111b972ec7a7420defb08d40853d1bd66`
- AGT-2331 `d3695656ce3df140940968dba9a5450dc5620f33`
- AGT-2332 `62193960fefb77332d1cf7dd9df2c1db753e205e`

The shared distributable basis was reconciled once in
`ecfe667800c805985f6a433b40491cacc7416957`. Existing target-architecture,
ProjectDocsService ETag/Git-date, and typed execution-outcome work was retained
from the newer integration branch instead of replayed from the shared WIP
history.

Foreign RemoteReview-plane raw material was excluded in
`d674d5ab82323f7a74069c65e622fbb68a297ac9`. Its review references are:

- source commit `6c74a500107a5c4a52a61c7e2b5d6b50121a7a89`
- `refs/remotes/origin/develop` at
  `729e24b2eb1eb1d5b16f51d420ea4c00ef2294de`
- `refs/remotes/origin/runner/agent-runner-01/AGT-2330` at
  `991152de13ce98a5f381e710be367dc8438f927e`
- `refs/remotes/origin/runner/agent-runner-01/AGT-2331` at
  `60ceb622e05727b072cb72af18430a4abb377a14`
- `refs/remotes/origin/runner/agent-runner-01/AGT-2332` at
  `5a22f530b4ffc1edfc54a03963bcf1deac800faf`

## Verification

- AGT-2330: Task Server Gate 66/66, Runner focused 13/13, Backend focused
  32/32, authenticated HTTPS topology 1/1, Linux x64 self-contained publish
  and version probe passed.
- AGT-2331: Orchestrator Engine 11/11, Task Server Gate 68/68, Backend
  execution-mode tests 5/5. `Orchestration:ExecutionMode` remains `Monolith`.
- AGT-2332: Runner rename/compatibility tests 27/27, Backend health probe 1/1,
  Frontend focused units 22/22, typecheck and lint passed, authenticated HTTPS
  topology 1/1, and 19 Playwright cases passed against the current frontend.

One unrelated Playwright case,
`Drain and graceful Retire require confirmation and keep a revivable retired client`,
failed twice because the existing `projectClient` projection preserves
`busyAction: "drain"` after a successful reload and therefore keeps Retire
disabled. AGT-2332 changes only setup terminology in that component, so this
pre-existing lifecycle defect was not widened into the file-scoped integration.

## Legacy waves 3 and 4

- AGT-2212 `f1a18ea4c2dd60f925067fcddef1d23c1416cd4f`

AGT-2212 was already present as the final platform WIP commit before this
integration run. Its focused component, utility, and triage unit gate passed
60/60. The explicit follow-up integration boundary records adoption without
replaying its stale salvage basis.
