/**
 * Project page shell rail configuration. Drives both the left-rail nav
 * order and the placeholder-panel headers. Slice 2 of the quality-system
 * mockup (docs/mockups/quality-system/) is the source of truth for the
 * inventory and copy below; per-panel real content lands in follow-up
 * slices listed in that mockup's README.
 */

export type ProjectRailGroup = 'insight' | 'quality' | 'operations' | 'config';

export type ProjectRailKey =
  | 'overview'
  | 'visual-evidence'
  | 'security'
  | 'architecture'
  | 'drift'
  | 'uxui'
  | 'test-quality'
  | 'token-usage'
  | 'observability'
  | 'product-runtime'
  | 'steering'
  | 'audits'
  | 'jobs'
  | 'settings'
  | 'orchestrator'
  | 'activity';

export interface ProjectRailItem {
  key: ProjectRailKey;
  group: ProjectRailGroup;
  /** Visible label in the rail. */
  label: string;
  /** Title shown at the top of the panel; may include the leading glyph. */
  panelTitle: string;
  /** Single-line panel description, lifted from the mockup's section-head. */
  description: string;
  /** Empty-state copy for the placeholder body. */
  empty: string;
  /** Glyph used both in the rail and the panel header. */
  icon: string;
}

export const PROJECT_RAIL_ITEMS: readonly ProjectRailItem[] = [
  // ---- INSIGHT: what the project IS / does ----
  {
    key: 'overview',
    group: 'insight',
    label: 'Overview',
    panelTitle: 'Overview',
    description: 'Snapshot of project health and quick actions',
    empty: 'Overview placeholder. Health snapshot and quick actions will land in a later slice.',
    icon: '▤',
  },
  {
    key: 'visual-evidence',
    group: 'insight',
    label: 'Visual Evidence',
    panelTitle: 'Visual Evidence',
    description: 'Project screenshots, UI evidence, and task links',
    empty: 'No visual evidence yet. Screenshots created by Playwright or browser checks will appear here.',
    icon: '◉',
  },
  {
    key: 'architecture',
    group: 'insight',
    label: 'Architecture',
    panelTitle: 'Architecture',
    description: 'Architectural decisions and drift status',
    empty: 'Architecture placeholder. ADR list and high-level map land in the architecture slice.',
    icon: '⊞',
  },
  {
    key: 'drift',
    group: 'insight',
    label: 'Drift',
    panelTitle: 'Drift',
    description: 'Overall drift score, per-dimension state, findings, and follow-ups',
    empty: 'No drift reports yet. Run a comparison to produce the first one.',
    icon: '↯',
  },
  {
    key: 'uxui',
    group: 'insight',
    label: 'UX / UI',
    panelTitle: 'UX / UI',
    description: 'Design references, screenshots, council critique, and next-version actions',
    empty: 'UX/UI placeholder. Design surfaces and council critique arrive in a later slice.',
    icon: '◐',
  },
  {
    key: 'observability',
    group: 'insight',
    label: 'Observability',
    panelTitle: 'Observability',
    description: 'Agent communication on the message bus: timeline, participants, kinds, token usage',
    empty: 'No bus messages for this project yet. Once the orchestrator, an agent, or the supervisor speaks, the timeline and counters fill in here.',
    icon: '⌁',
  },

  // ---- QUALITY: what guards the project ----
  {
    key: 'security',
    group: 'quality',
    label: 'Security',
    panelTitle: 'Security',
    description: 'Baseline, reviews, and active findings for this project',
    empty: 'No security baseline yet. The Security slice ships the baseline action and review history.',
    icon: '⊡',
  },
  {
    key: 'test-quality',
    group: 'quality',
    label: 'Test Quality',
    panelTitle: 'Test Quality',
    description: 'Backend tests, end-to-end tests, tuning runs, coverage, and source-code perspective',
    empty: 'Test Quality placeholder. Run history and coverage views land in a later slice.',
    icon: '✓',
  },
  {
    key: 'audits',
    group: 'quality',
    label: 'Audits & Checks',
    panelTitle: 'Audits & Checks',
    description: 'Review definitions, per-task checks, and runtime probe slots for this project',
    empty: 'Audits & Checks placeholder. The review-definition model lands in a later slice.',
    icon: '⊟',
  },

  // ---- OPERATIONS: what's running right now ----
  {
    key: 'jobs',
    group: 'operations',
    label: 'Jobs',
    panelTitle: 'Jobs',
    description: 'Tasks queued, in progress, and recently completed',
    empty: 'Jobs placeholder. The board page is the live view; this panel will show a project-scoped slice.',
    icon: '☰',
  },
  {
    key: 'token-usage',
    group: 'operations',
    label: 'Token Usage',
    panelTitle: 'Token Usage',
    description: 'Inference spend by job, supporting runs, orchestrator turns, and time window',
    empty: 'Token Usage placeholder. Heatmap, timeline, and per-job drill-down land in a later slice.',
    icon: '▦',
  },
  {
    key: 'product-runtime',
    group: 'operations',
    label: 'Product Runtime',
    panelTitle: 'Product Runtime',
    description: 'How the built software behaved during local runs and tests: events, errors, latency, domain timeline',
    empty: 'No runtime events captured yet. Once the built software emits structured events to the runtime JSONL files, recent events, error groups, latency summaries, and the domain timeline appear here.',
    icon: '⊜',
  },
  {
    key: 'activity',
    group: 'operations',
    label: 'Activity',
    panelTitle: 'Activity',
    description: 'Decisions, actions, and observations recorded by the orchestrator',
    empty: 'Activity placeholder. The full feed lives at the project-feed overlay; a scoped view lands later.',
    icon: '⌖',
  },

  // ---- CONFIG: how the project is set up ----
  {
    key: 'steering',
    group: 'config',
    label: 'Steering Docs',
    panelTitle: 'Steering Docs',
    description: 'Agent-facing instruction sources, human summary, drift warnings, and propose-update actions',
    empty: 'No steering inventory yet. The Steering Docs slice lists README, AGENTS, ROADMAP, the task contract, the skills lookup, the ADR archive, runtime prompts, and project settings.',
    icon: '⊕',
  },
  {
    key: 'orchestrator',
    group: 'config',
    label: 'Orchestrator',
    panelTitle: 'Orchestrator',
    description: 'Live session, recent decisions and observations',
    empty: 'Orchestrator placeholder. Session detail and recent decisions land in a later slice.',
    icon: '◈',
  },
  {
    key: 'settings',
    group: 'config',
    label: 'Settings',
    panelTitle: 'Settings',
    description: 'How the orchestrator behaves on this project',
    empty: 'Settings placeholder. Runner mode, auto-commit, and orchestrator model controls arrive next.',
    icon: '⚙',
  },
];

const RAIL_KEY_SET = new Set<string>(PROJECT_RAIL_ITEMS.map(i => i.key));

export function isProjectRailKey(value: string | null | undefined): value is ProjectRailKey {
  return !!value && RAIL_KEY_SET.has(value);
}

export const DEFAULT_PROJECT_RAIL_KEY: ProjectRailKey = 'overview';

/**
 * Slug used in the project-shell URL hash. Stable mapping from a watch-path
 * project name (e.g. "Agent Software Studio") to a kebab-case identifier
 * (e.g. "agent-software-studio"). The reverse lookup happens by computing
 * the slug for every known watch path and matching.
 */
export function toProjectSlug(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}
