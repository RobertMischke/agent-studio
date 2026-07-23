# Open issues — review pipeline health

The living register for this problem class. One row per issue, each linked to
its board card. New problems of this kind start here (add a row, link the card,
reference the incident report if one exists).

| Status | Card | Issue | Home |
|---|---|---|---|
| open | AGT-2257 | Gate has no watchdog after lock acquisition; queue-SLA victims count as card failures and burn anti-churn budget. | [incident 22./23.07.](incident-2026-07-22-gate-churn.md) |
| open | AGT-2258 | Stale-branch trap: gates test branch tips with outdated suites. Target: test the subject merged onto current develop; then shrink the build-profile poison filter. | [incident 22./23.07.](incident-2026-07-22-gate-churn.md) |
| open | AGT-2255 | Lane move reports Success while the copy+delete fallback leaves the full source folder behind (card duplicated across lanes). | [incident 22./23.07.](incident-2026-07-22-gate-churn.md) |
| open | AGT-2259 | No health sensors: lock-hold alarm, cross-card fingerprint repetition, drain-rate collapse, starvation (oldest lane entry age, unclaimed-ready-with-free-slots). | [incident 22./23.07.](incident-2026-07-22-gate-churn.md) |
| open | AGT-2260 | Operator authority, remainder: artifact rotation on requeue + attempt-epoch surfaced in UI. Epoch core (guards anchor on the lane-entry transition) **delivered 23.07.** | [decision history](decision-history.md) |
| open | AGT-2256 | Build-profile validation launches `bash` on Windows and always fails; every profile edit silently blocks auto-pickup until a settings-file patch + restart. | [decision history](decision-history.md) |
| open | AGT-2185/2186 | Typed abort causes and capability health remain after canonical attempt authority. | [incident 22./23.07.](incident-2026-07-22-gate-churn.md) |
| open | AGT-2229 | Host orchestrator instance: gates and post-steps as claimable remote steps. Supersedes the ssh-gate bridge (cleanup card `ssh-gate-shortcut-cleanup` removes it — replacement first, then teardown). | [decision history](decision-history.md) |
| open | AGT-2222 (remainder) | Two-pool admission (build/api) + oldest-first with project fairness in both post-processing admission and claim grants. | [decision history](decision-history.md) |
| open | — | Claim ledger vs lane display drift: the host can be executing runs the board does not show as in-progress. Fold into AGT-2259 visibility. | [incident 22./23.07.](incident-2026-07-22-gate-churn.md) |
| watch | — | Remote-gate slots at 2 (bridge constant); raise to 3–4 if the host stays comfortable. Host run slots at 20 (was 5) — trim to 14–16 if simultaneous build phases thrash. | [decision history](decision-history.md) |
| delivered | AGT-2182 | Persisted RunAttempt, ReviewAttempt, immutable ReviewSubject, restart-safe lease/fence/epoch, typed stale-write rejection, and canonical Remote Result-SHA handoff. | [decision history](decision-history.md) |
| delivered | AGT-2222 T1+T2 | Derived admission, LLM budget, latency metric; ssh remote gates (full suite 2 min 11 s). | [decision history](decision-history.md) |
