/**
 * Workforce role catalogue + attribution mapping.
 *
 * Roles are the "who in the workforce just spoke" identity that the chat
 * surfaces (project chat + per-task chat) render as a small badge next to
 * each agent message. The set of roles is deliberately small and stable:
 * new roles are added by extending {@link ROLE_CATALOGUE}, never invented
 * at the call site.
 *
 * Naming overlaps with the aspect-runner catalogue
 * (`backend/Services/Runner/AspectRunnerService.cs`) so the two surfaces
 * stay consistent: a `code-quality` aspect is a Code Reviewer, a
 * `requirement-fit` aspect is a Plan Curator, etc.
 *
 * No emoji per repo style. Each role's "icon" is a one- or two-character
 * glyph drawn in CSS so high-DPI scaling and the dark Catppuccin palette
 * stay calm. Colour ramps lean on the existing chat palette
 * (mauve / sapphire / sky / teal / yellow / peach / red / lavender / pink).
 */

/** Stable role ids. New ids must be appended; existing values must not be reused. */
export type WorkforceRoleId =
  | 'task-executor'
  | 'code-reviewer'
  | 'architecture-custodian'
  | 'security-auditor'
  | 'test-author'
  | 'documentation-maintainer'
  | 'plan-curator'
  | 'diagnostician'
  | 'health-officer'
  | 'orchestrator'
  | 'supervisor'
  | 'user'
  | 'agent-generic';

export interface WorkforceRole {
  /** Stable id, also used as the badge's data-testid suffix. */
  id: WorkforceRoleId;
  /** Short display label. English, calm casing (no all-caps). */
  label: string;
  /**
   * Plain-text role description, surfaced as the badge's native `title`
   * attribute (default browser delay, no custom widget) per the
   * no-HTML-tooltip rule.
   */
  description: string;
  /**
   * Catppuccin-ish accent for the badge background tint and left
   * accent. Picked to be distinct from the chat user bubble and from
   * each other on the dark theme.
   */
  accent: string;
  /** One- or two-character glyph used as the visual icon. No emoji. */
  glyph: string;
}

/**
 * Authoritative role list. The order is the canonical "rotation" order
 * the workforce doc describes (executor → reviewer → custodian → ...).
 * The `agent-generic` fallback intentionally sits last so iteration
 * order matches the operator's mental model.
 */
export const ROLE_CATALOGUE: readonly WorkforceRole[] = [
  {
    id: 'task-executor',
    label: 'Task Executor',
    description:
      'Performs the task in the prompt: writes code, edits files, runs commands, captures evidence.',
    accent: '#a6e3a1',
    glyph: 'TE',
  },
  {
    id: 'code-reviewer',
    label: 'Code Reviewer',
    description:
      'Reviews the executor\'s diff for regressions, dead code, missing tests, or visible type errors. Maps to the code-quality aspect.',
    accent: '#89b4fa',
    glyph: 'CR',
  },
  {
    id: 'architecture-custodian',
    label: 'Architecture Custodian',
    description:
      'Watches for architectural drift, layering violations, and decisions that contradict ADRs or hard rules.',
    accent: '#cba6f7',
    glyph: 'AC',
  },
  {
    id: 'security-auditor',
    label: 'Security Auditor',
    description:
      'Reviews changes for security regressions: input handling, secrets, command injection, auth surfaces.',
    accent: '#f38ba8',
    glyph: 'SA',
  },
  {
    id: 'test-author',
    label: 'Test Author',
    description:
      'Owns regression coverage: writes the failing test first, watches it go green after the fix. Maps to the tests-and-evidence aspect.',
    accent: '#94e2d5',
    glyph: 'TA',
  },
  {
    id: 'documentation-maintainer',
    label: 'Documentation Maintainer',
    description:
      'Keeps README, ROADMAP, AGENTS, ADRs, and CLI skills in sync with shipped behaviour. Maps to the documentation-impact aspect.',
    accent: '#fab387',
    glyph: 'DM',
  },
  {
    id: 'plan-curator',
    label: 'Plan Curator',
    description:
      'Asks whether the change matches the prompt\'s acceptance criteria and flags scope drift. Maps to the requirement-fit aspect.',
    accent: '#f9e2af',
    glyph: 'PC',
  },
  {
    id: 'diagnostician',
    label: 'Diagnostician',
    description:
      'Reads the evidence envelope on a failure and reports a typed diagnosis (category + confidence) to the rule engine.',
    accent: '#f5c2e7',
    glyph: 'DG',
  },
  {
    id: 'health-officer',
    label: 'Health Officer',
    description:
      'Watches per-project health: pickup failures, quiet runs, capture failures, and other operational signals.',
    accent: '#74c7ec',
    glyph: 'HO',
  },
  {
    id: 'orchestrator',
    label: 'Orchestrator',
    description:
      'Deterministic arbiter that decides reissue / accept / escalate after each run and writes typed decisions to the chat.',
    accent: '#cdd6f4',
    glyph: 'OR',
  },
  {
    id: 'supervisor',
    label: 'Supervisor',
    description:
      'Layer-2 supervisor: cooperative advisories and the four pre-emptive primitives (cancelRun / pausePickup / forceFail / resume).',
    accent: '#b4befe',
    glyph: 'SV',
  },
  {
    id: 'user',
    label: 'You',
    description: 'You, the meta-manager of this workforce.',
    accent: '#f5e0dc',
    glyph: 'YO',
  },
  {
    id: 'agent-generic',
    label: 'Agent',
    description:
      'Fallback badge for an agent message whose specific workforce role could not be derived from the message metadata.',
    accent: '#9399b2',
    glyph: 'AG',
  },
];

const ROLE_BY_ID = new Map<WorkforceRoleId, WorkforceRole>(
  ROLE_CATALOGUE.map((r) => [r.id, r])
);

export function getRole(id: WorkforceRoleId): WorkforceRole {
  return ROLE_BY_ID.get(id) ?? ROLE_BY_ID.get('agent-generic')!;
}

/**
 * Loose input shape the chat surfaces can pass without first picking a
 * specific message-model. Everything is optional except `author` so a
 * caller with only the legacy ChatMessage role still gets a sensible
 * badge.
 */
export interface RoleAttributionInput {
  /** Wire-level author label. */
  author?: string | null;
  /** Wire-level message kind (turn / event-tool-call / decision / ...). */
  kind?: string | null;
  /**
   * Optional references the projection attached. Aspect-runner output
   * carries entries like `aspect:code-quality`; the diagnostician
   * pickup-failure agent carries `agent:diagnostician`; etc.
   */
  refs?: readonly string[] | null;
  /**
   * Optional explicit role id the projection already resolved. When
   * present, this wins so a backend that has stronger metadata can
   * bypass the heuristic mapping entirely.
   */
  roleId?: WorkforceRoleId | null;
}

const ASPECT_TO_ROLE: Readonly<Record<string, WorkforceRoleId>> = {
  'code-quality': 'code-reviewer',
  'requirement-fit': 'plan-curator',
  'documentation-impact': 'documentation-maintainer',
  'tests-and-evidence': 'test-author',
  // Future-proofing: a `security-review` or `arch-fit` aspect would
  // naturally map here. New entries land in lockstep with the aspect
  // catalogue per the workforce prompt's "naming consistency" rule.
};

/**
 * Resolve a workforce role for a chat row deterministically. Pure
 * function: given the same input, always returns the same role id.
 *
 * Resolution order:
 *   1. Explicit `roleId` on the input.
 *   2. Aspect ref (`aspect:<id>`) lookup against the aspect ↔ role
 *      mapping.
 *   3. Explicit role ref (`role:<id>`) lookup against the catalogue.
 *   4. Author + kind heuristics for the message taxonomy that already
 *      ships on the wire (user / orchestrator / supervisor / agent /
 *      claude / codex / copilot / gemini × turn / event-*).
 *   5. `agent-generic` fallback (never throws, never returns null).
 */
export function resolveRole(input: RoleAttributionInput): WorkforceRole {
  if (input.roleId && ROLE_BY_ID.has(input.roleId)) {
    return ROLE_BY_ID.get(input.roleId)!;
  }

  const refs = input.refs ?? [];
  for (const ref of refs) {
    if (!ref) continue;
    const trimmed = ref.trim();
    if (trimmed.startsWith('aspect:')) {
      const aspectId = trimmed.slice('aspect:'.length).toLowerCase();
      const mapped = ASPECT_TO_ROLE[aspectId];
      if (mapped) return ROLE_BY_ID.get(mapped)!;
    }
    if (trimmed.startsWith('role:')) {
      const roleId = trimmed.slice('role:'.length).toLowerCase() as WorkforceRoleId;
      if (ROLE_BY_ID.has(roleId)) return ROLE_BY_ID.get(roleId)!;
    }
  }

  const author = (input.author ?? '').toLowerCase();
  const kind = (input.kind ?? '').toLowerCase();

  if (author === 'user') return ROLE_BY_ID.get('user')!;
  if (author === 'orchestrator') return ROLE_BY_ID.get('orchestrator')!;
  if (author === 'supervisor') return ROLE_BY_ID.get('supervisor')!;
  if (author === 'system') return ROLE_BY_ID.get('orchestrator')!;

  // Event kinds carry their own role flavour even when the author is a
  // generic agent label. Keep this conservative: the executor is the
  // dominant role, so unknown kinds drop through to it.
  if (kind === 'event-decision' || kind === 'decision.orchestrator') {
    return ROLE_BY_ID.get('orchestrator')!;
  }
  if (kind === 'event-watchdog' || kind === 'supervisor.wait') {
    return ROLE_BY_ID.get('health-officer')!;
  }

  // CLI-typed agents (claude / codex / copilot / gemini) and the bare
  // `agent` author land on the Task Executor by default. This matches
  // the workforce doc's "sequential rotation, executor is the dominant
  // role" framing.
  if (
    author === 'agent' ||
    author === 'claude' ||
    author === 'codex' ||
    author === 'gemini'
  ) {
    return ROLE_BY_ID.get('task-executor')!;
  }

  return ROLE_BY_ID.get('agent-generic')!;
}
