---
styleGuideId: dotnet-backend
title: .NET backend guide
version: 1
summary: Feature ownership, pure policy, and side-effect ordering for .NET work.
promptSummary: Keep backend code in fractal feature folders; express branching lifecycle decisions as pure policy with direct matrix tests; order a feature flow as boundary validation, application coordination, pure decision, then bounded side effects; preserve route and wire contracts unless the task explicitly changes them.
appliesTo: {"projects":["*"],"technologies":["dotnet","csharp"],"taskAreas":["backend","runner","filesystem","git"]}
---

# .NET backend guide

Use this page for C# and HTTP backend changes. It condenses the current
[backend feature-folder guide](../system/architecture/backend-structure/styleguide.md)
and the pure-policy seams already used by runner, transition, and Git behavior.

## Feature ownership

- Organize recursively by domain under `backend/Features/<Domain>/`.
  Subfolders name sub-domains, not technical roles. Do not add `Services`,
  `Models`, `Endpoints`, `Handlers`, `Dtos`, `Helpers`, `Infrastructure`, or
  `Utils` folders below a feature.
- Keep an endpoint, its feature service, domain records, and its request or
  response contract close to the feature that owns them.
- System-boundary types use `Request` or `Response`. Domain types do not gain a
  generic `Model`, `Info`, or `Data` suffix merely to describe their role.
- Preserve existing route strings and serialized property names unless the task
  explicitly changes the public contract.

The architecture test in
`backend.Tests/Architecture/FeatureFolderBoundaryTests.cs` enforces the folder
and namespace rules.

## Pure policy first

When behavior branches over state, intent, configuration, or observed facts:

1. Put the decision in a deterministic function or small policy type with
   plain inputs and a plain result.
2. Test the policy matrix directly without filesystem, process, network, clock,
   or dependency-injection setup.
3. Keep the application service responsible for reading facts and applying the
   chosen side effects.

`RunPlanner`, `WorktreeRunPolicy`, and settings resolvers are repository
examples. A mock-heavy service test is not a substitute for a direct policy
test when the decision can be pure.

## Feature flow ordering

Keep the execution order visible and one-directional:

1. **Boundary validation:** parse and reject invalid HTTP or hosted-service
   input without mutation.
2. **Application coordination:** resolve the project, task, or repository and
   collect the facts the policy needs.
3. **Pure decision:** choose the transition, plan, or verdict.
4. **Bounded side effects:** persist through the owning service, emit existing
   structured observability, and return the boundary response.

Do not let an endpoint and a hosted worker each reimplement the same decision
ordering. They should call the same feature service or policy. When a mutation
has multiple durable effects, tests pin the order and the failure boundary so a
partial write cannot look complete.

## Review evidence

- Add direct tests for every new policy branch, including an invalid or failure
  case.
- Add endpoint or service coverage when the change affects validation,
  persistence, ordering, or side effects.
- Run the feature-folder architecture test when adding or moving backend files.
- Keep new logs structured and stable when the behavior is operationally
  meaningful; do not add instrumentation to tiny pure helpers.
