# Creative Design System - Concept Inventory

Status: design exploration. No naming committed.

## 1. What This Adds

The existing roadmap focuses on reliable task execution, security, quality checks, and review evidence. This mockup adds a generative design dimension:

| Concept | Scope | Trigger | Output | Produces work? |
|---|---|---|---|---|
| Design Loop | Task or project | Manual or after visual task | next version decision | Yes, via follow-up task |
| Screenshot Critique | Task | After screenshot capture | findings and suggestions | No, review evidence |
| Council Review | Task or project | Manual | role-separated critique | No, advisory |
| Visual Direction Skill | Project | Manual | design principles and references | Yes, design brief |
| UI Polish Skill | Task | Manual or review | concrete UI improvement plan | Yes, follow-up task |
| Copy Tone Skill | Task or project | Manual | improved product language | Yes, text suggestions |
| Brand Fit Review | Project | Manual | positioning notes | No, advisory |

## 2. Clean Separation

| | Examines | Generates | Decides |
|---|---|---|---|
| Screenshot Critique | screenshots and current UI | findings | no |
| Council Review | screenshots, prompt, product context | role notes | no |
| Design Skill | product context and current UI | brief, variant prompt, copy | no |
| Orchestrator | council notes and task state | next action | yes |
| Coding Agent | chosen task prompt | code changes | no lifecycle decision |

The council should never become another implementation actor. It observes and critiques. The orchestrator decides whether to accept, iterate, or queue follow-up work.

## 3. Recommended Vocabulary

- **Design Loop** - the full iteration cycle from intent to screenshot to critique to next version.
- **Council** - a structured multi-role critique pass.
- **Version** - one visual attempt captured by screenshots and notes.
- **Design Evidence** - screenshots, variant notes, critique, chosen direction, rejected alternatives.
- **Design Skill** - reusable workflow that generates design guidance or critique.
- **Design Memory** - project-level record of accepted visual direction, brand notes, and example screens.

Avoid using "Quality" as the umbrella. Design is not merely quality control. It is creative direction plus critique.

## 4. Council Roles

Recommended default council:

- Product: workflow usefulness, feature fit, clarity of user intent.
- Visual Design: hierarchy, layout balance, color, spacing, density, polish.
- Interaction Design: state model, controls, flow efficiency, feedback timing.
- Frontend Engineering: implementation feasibility, responsiveness, component reuse.
- Accessibility: contrast, focus order, semantics, touch targets, reduced motion.
- Marketing and Positioning: product story, tone, trust signal, memorable character.

Council output should be structured:

```json
{
  "version": "v2",
  "roles": [
    { "role": "Visual Design", "verdict": "warn", "notes": ["Primary action is visually weak."] }
  ],
  "orchestratorRecommendation": "request-next-version",
  "nextVersionPrompt": "Keep the dense taskboard layout, but make the review panel feel more intentional..."
}
```

## 5. Storage Shape

Runtime design evidence belongs in the job folder, not the app source repository.

Suggested shape:

```text
results/design/
  versions.jsonl
  council-v1.json
  council-v2.json
  screenshots/
    v1-desktop.png
    v1-mobile.png
    v2-desktop.png
    v2-mobile.png
```

Project-level design memory belongs in the watched project's documented project context or in a central app-managed design-memory file that points back to accepted evidence:

```text
.orchestrator/design-memory.md
```

That file should be human-readable and direct-CLI-friendly.

## 6. Orchestrator Actions

The orchestrator needs explicit actions:

- Accept this version.
- Request next version.
- Ask council for harsher critique.
- Ask one role for deeper critique.
- Create follow-up implementation task.
- Save chosen direction to design memory.

These are steering actions, not hidden workflow branches.

## 7. First Implementation Order

1. Add design evidence contract to protocol docs.
2. Add screenshot version comparison in job detail.
3. Add local design Skill definitions.
4. Add council prompt and structured output parser.
5. Add "Next version" follow-up task action.
6. Add project-level design memory.

## Open Questions

1. Should Council be a project-level surface, a task detail action, or both?
2. Should the default council have six roles, or start with three roles: Product, Design, Engineering?
3. Should "next version" create a new queued task or continue the same task session?
4. How much design memory should be global to the app versus local to a watched project?
5. Should image-generation references be allowed in early implementation, or should the first cut use only screenshots from the running app?
