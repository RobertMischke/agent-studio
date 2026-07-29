# AGT-2417 verification

## Live Research pipeline

- Command: `npx playwright test e2e/task-detail/research-lightweight-pipeline.spec.ts --project=chromium`
- Result: passed, one real Research card completed its Core agent run in 1.2 minutes.
- Route: `gpt-5.6-luna` with `medium` thinking. This is within the model-routing policy for a trivial, mechanical HTML artifact probe with deterministic verification and no correctness floor.
- Pipeline: `read-only-task-pipeline`, displayed as `Lightweight report pipeline`.
- Negative proof: the resolved step list contains no build/test gate, Stylelint, review aspect, code-review grade, Wiki automation, task spawner, regression radar, or drift step.
- Screenshot: [research-lightweight-pipeline.png](research-lightweight-pipeline.png)

## Backend verification

- `backend/OrchestratorApi.csproj` compiled successfully as part of the targeted test attempt.
- A backend-only focused harness ran the eight AGT-2417 contracts: 8 passed in 0.818 seconds. This covers the short pipeline shape, dangling-dependency guard, mode and AGT-2301 type routing, Research versus Planning prompt framing, valid primary-HTML acceptance, and invalid-HTML reissue without aspects.
- The repository-wide test project still cannot build through its Runner project reference because current `origin/develop` already has three unrelated `runner/RemoteTaskRunner.cs` call/signature mismatches around `CompleteOrReconcileAsync` and `CompleteAsync`. No AGT-2417 file participates in those errors.
- The broader three-class backend run passed 155 of 156 tests. The remaining existing failure is `OperatorRequeue_WithResolvedOldSentinel_ForcesFreshAspectAssessment`, a coding-card operator-requeue path with no Research mode.

## Result viewer boundary

- Stable API reproduction on AGT-2380: `GET /api/tasks/AGT-2380/results/report.html` returned `404`; its existing Result pane is still driven by the historical `status.md`.
- The missing `results/report.html` primary-selection/sandbox-rendering slice was appended through the Task API to AGT-2409, including relative companion-link handling and a Research-card Playwright proof.
- AGT-2409 is now completed without that viewer slice. AGT-2417 therefore does not claim the AGT-2380 Result-tab screenshot; the convention and report are delivered, and the viewer integration remains the explicitly delegated dependency.
