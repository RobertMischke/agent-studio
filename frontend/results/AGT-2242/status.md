# AGT-2242 verification

Verified on 27 July 2026 from
`runner/agent-runner-01/AGT-2242` after fast-forwarding to `origin/develop`.

## Review gaps closed

- The current workspace registry is now part of the Playwright fixture, so the
  `#/projects/demo/wiki` deep link resolves to the Wiki instead of falling back
  to the all-project board.
- The current Wiki home request is mocked by the feature fixture.
- The narrow fixed-layout folder table now keeps page titles readable while
  retaining the compact Type, Status, and Reads columns.
- Playwright asserts that the rendered page title does not overflow its cell.
- Every screenshot filename declares its mocked API provenance.
- Light and dark evidence is retained in this directory and in the task job
  folder.

## Passing verification

- `dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter 'FullyQualifiedName~WikiAgentRead|FullyQualifiedName~WikiFolderView'`
  passed 26 of 26 tests.
- `dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-restore --filter 'FullyQualifiedName=AgentStudio.Tests.RemoteRunnerEndToEndTests.Runner_drives_one_task_end_to_end_through_the_server_api'`
  passed 1 of 1 test. This test ships a remote runner read through log
  ingestion and asserts the adjacent companion counter.
- `npm --prefix frontend run test -- --watch=false --include='src/app/features/project-detail/components/project-wiki-section/wiki-agent-reads/wiki-agent-reads.component.spec.ts' --include='src/app/features/project-detail/components/project-wiki-section/wiki-folder-view/wiki-folder-view.component.spec.ts'`
  passed 10 of 10 tests.
- `PW_BASE_URL=http://127.0.0.1:4022 JOB_RESULTS_DIR=/home/agent/runner-work/tasks/AGT-2242/results npm --prefix frontend run e2e -- e2e/project/wiki-agent-reads.spec.ts`
  passed 2 of 2 tests against the frontend built from this worktree. The dev
  backend lifecycle was owned by the Playwright fixture.
- Focused ESLint and Stylelint passed for the changed E2E, component, and SCSS
  files.

## Repository baseline observations

The broader Wiki component run currently fails 35 of 160 tests because
`project-wiki-section.spec.ts` does not consistently flush the newer
`GET /api/projects/Demo/wiki/home` request. The repository-wide frontend lint
also reports 15 pre-existing component-size baseline violations. Neither
failure is introduced by this task's scoped changes.

## Evidence

- `wiki-agent-reads-folder-light--mocked.png`
- `wiki-agent-reads-folder-dark--mocked.png`
- `wiki-agent-reads-meta-light--mocked.png`
- `wiki-agent-reads-meta-dark--mocked.png`

The fixture uses deterministic API responses with `total: 23`, a
`lastReadAt` timestamp, and two recent task keys. The screenshots prove the
readable narrow-table layout, Reads column, tooltip-capable timestamp
projection, and recent-history panel in both themes.
