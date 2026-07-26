# Finding-first reissue prompt experiment analysis

Generated: 2026-07-26T00:00:00.000Z

This is an experimental arm comparison. Assignment and attempt events are hard telemetry. Grade A and acceptance are model-judged evidence, not deterministic truth.

## Arm counts and censoring

| Arm | Assigned | Accepted | Right-censored | Observed accepted mean |
|---|---:|---:|---:|---:|
| control | 0 | 0 | 0 | not estimable |
| treatment | 0 | 0 | 0 | not estimable |

Primary effect: not estimable. Negative favors treatment. The right-censor-aware estimate is restricted mean attempts at the common horizon (not estimable attempts).

Grade A sensitivity effect: not estimable.

Assignment consistency: 0 task(s) with arm, template-version, or assignment-hash drift. Coding-route drift: 0 task(s).

## Deterministic-gate regression

Control rate: not estimable. Treatment rate: not estimable. Risk difference: not estimable.

## Prompt-family strata

| Prompt family | Control | Treatment | Primary effect |
|---|---:|---:|---|
| No assignments observed | 0 | 0 | not estimable |

## Cause strata

| Cause | Control | Treatment | Primary effect |
|---|---:|---:|---|
| No assignments observed | 0 | 0 | not estimable |

## Promotion decision

Keep the production default unchanged.

The production default may change only with at least 30 tasks per arm, zero assignment drift, a treatment effect of at most -0.5 restricted mean attempts, a bootstrap interval wholly below zero, and no deterministic-gate risk-difference upper bound above 5 percentage points.
