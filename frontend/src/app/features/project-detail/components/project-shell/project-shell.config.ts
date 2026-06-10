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
  // 'steering-docs' is the NON-navigable tree container that groups the
  // documentation rails (Architecture / Wiki / Agent Docs). 'steering' is the
  // (renamed) leaf that used to be labelled "Steering Docs" and now carries the
  // "Agent Docs" label — the AGENTS.md-style instructions agents read of their
  // own accord. Two different nodes; do not collapse them.
  | 'steering-docs'
  | 'steering'
  | 'wiki'
  // Nav-rebuild step 1 (T5a): three reachable shells that get their real
  // content in step 2 (T5b). 'pipeline' ← T4, 'workflow' ← T6, 'prompts' ← T3.
  // Until then they render as the project-shell placeholder panel.
  | 'pipeline'
  | 'workflow'
  | 'prompts'
  | 'runtime-prompts'
  | 'audits'
  | 'jobs'
  | 'settings'
  | 'settings-defaults'
  | 'settings-overrides'
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
  /** Glyph used in the panel header. The rail itself is text-only (no icons). */
  icon: string;
  /**
   * When set, this item is a child of the given parent and renders nested /
   * indented under it; the parent gains a disclosure twisty. Children live in
   * the same `group` as their parent.
   */
  parent?: ProjectRailKey;
  /**
   * Whether selecting the row routes to a panel. Defaults to true. A pure tree
   * container (e.g. "Steering Docs") sets this false: clicking the row only
   * toggles its children, it never becomes the active panel.
   */
  navigable?: boolean;
}

export const PROJECT_RAIL_ITEMS: readonly ProjectRailItem[] = [
  // ---- INSIGHT: what the project IS / does ----
  // Order matches the agent-orchestrator.zip mockup (hub-view.png):
  // Overview · Visual Evidence · Drift · UX / UI · Observability.
  // Architecture used to live here; it now sits under the "Steering Docs"
  // documentation container in CONFIG (ASS-1711) because it is thematically a
  // doc surface, not a live-health surface.
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
  // Per mockup order: Security · Test Quality · Audits & Checks ·
  // Product Runtime. Product Runtime lived under OPERATIONS before;
  // the mockup groups it with the quality bar since it's about how
  // the built software behaves under load, not project operations.
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
  {
    key: 'product-runtime',
    group: 'quality',
    label: 'Product Runtime',
    panelTitle: 'Product Runtime',
    description: 'How the built software behaved during local runs and tests: events, errors, latency, domain timeline',
    empty: 'No runtime events captured yet. Once the built software emits structured events to the runtime JSONL files, recent events, error groups, latency summaries, and the domain timeline appear here.',
    icon: '⊜',
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
    key: 'activity',
    group: 'operations',
    label: 'Activity',
    panelTitle: 'Activity',
    description: 'Decisions, actions, and observations recorded by the orchestrator',
    empty: 'Activity placeholder. The full feed lives at the project-feed overlay; a scoped view lands later.',
    icon: '⌖',
  },

  // ---- CONFIG: how the project is set up ----
  // Documentation surfaces are grouped under one collapsible "Steering Docs"
  // tree container (ASS-1711): Architecture + Wiki/Docs + Agent Docs. The
  // container itself is non-navigable — it only expands to its children.
  {
    key: 'steering-docs',
    group: 'config',
    label: 'Steering Docs',
    panelTitle: 'Steering Docs',
    description: 'Documentation that steers this project: architecture, the docs tree, and agent-read instructions',
    empty: 'Pick a document surface below: Architecture, Wiki / Docs, or Agent Docs.',
    icon: '⊕',
    navigable: false,
  },
  {
    key: 'architecture',
    group: 'config',
    parent: 'steering-docs',
    label: 'Architecture',
    panelTitle: 'Architecture',
    description: 'Architectural decisions and drift status',
    empty: 'Architecture placeholder. ADR list and high-level map land in the architecture slice.',
    icon: '⊞',
  },
  {
    key: 'wiki',
    group: 'config',
    parent: 'steering-docs',
    label: 'Wiki / Docs',
    panelTitle: 'Wiki / Docs',
    description: 'Browse the project docs/ tree: navigation card, domain docs, and accumulated learnings',
    empty: 'No docs found. Once the project has a docs/ folder, its tree and rendered documents appear here.',
    icon: '📚',
  },
  {
    // Renamed from "Steering Docs": these are the instructions agents read of
    // their own accord (AGENTS.md, frontend/AGENTS.md, the agent-facing
    // domain/nav docs). The key stays 'steering' so deep-links and the
    // shipped steering-docs panel keep working.
    key: 'steering',
    group: 'config',
    parent: 'steering-docs',
    label: 'Agent Docs',
    panelTitle: 'Agent Docs',
    description: 'Instruction files agents read on their own (AGENTS.md and the agent-facing domain docs), with human summary and drift warnings',
    empty: 'No agent-doc inventory yet. The slice lists AGENTS.md, frontend/AGENTS.md, README, ROADMAP, the task contract, the skills lookup, and the ADR archive.',
    icon: '🧭',
  },
  // ---- Nav-rebuild shells (T5a step 1) ----
  // Target navigation (Zielbild §F2): Board · Wiki · Pipeline · Workflow ·
  // Prompts · Einstellungen at project level. These three are reachable
  // placeholder shells now; step 2 (T5b) moves the existing functionality
  // here unchanged. Nothing is moved in this step — the source pages stay put.
  {
    key: 'pipeline',
    group: 'config',
    label: 'Pipeline',
    panelTitle: 'Pipeline',
    description: 'Run pipeline steps (pre / core / post): activation, order, per-step model and prompt binding, and cost.',
    empty: 'Pipeline shell — navigation only. Step 2 (T5b) moves the pipeline sections currently in Project Settings here unchanged: per-step activation and order, model + prompt binding (→ Prompts), and token/cost view. Step 3 (T4a) then redesigns this page.',
    icon: '⫶',
  },
  {
    key: 'workflow',
    group: 'config',
    label: 'Workflow',
    panelTitle: 'Workflow / Lanes',
    description: 'Lanes, ordering, and transitions; later per-transition Git integration.',
    empty: 'Workflow shell — navigation only. Step 2 (T5b) moves the lane sort-order controls here unchanged. The transition view (stage 1) and Git integration (stage 2, after the Git concept decision) land in step 3 (T6a) and step 4.',
    icon: '⇄',
  },
  {
    key: 'prompts',
    group: 'config',
    label: 'Prompts',
    panelTitle: 'Prompts',
    description: 'Prompt registry for this project: inventory, source / override matrix, and coverage.',
    empty: 'Prompts shell — navigation only. Step 2 (T5b) moves the prompt-admin surface here unchanged: registry inventory, source/override matrix, and coverage. Distinct from Runtime Prompts below, which is the read-only browse over prompts/runtime/*.md.',
    icon: '✎',
  },
  {
    // Runtime prompts are a SEPARATE main point from the agent-read docs above:
    // these are the pipeline / aspect / review / orchestrator prompts under
    // prompts/runtime/*.md that the platform feeds to CLIs at run time.
    key: 'runtime-prompts',
    group: 'config',
    label: 'Runtime Prompts',
    panelTitle: 'Runtime Prompts',
    description: 'Pipeline, aspect, review, and orchestrator prompts the platform injects at run time (prompts/runtime/*.md)',
    empty: 'Runtime Prompts placeholder. A read-only browse over prompts/runtime/*.md lands in a later slice; these are CLI-behaviour prompts, distinct from the agent-read Agent Docs.',
    icon: '⌥',
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
    // Settings is a navigable tree parent: the row opens the full Settings
    // panel, and the twisty expands to its grouped sub-areas below.
    key: 'settings',
    group: 'config',
    label: 'Settings',
    panelTitle: 'Settings',
    description: 'How the orchestrator behaves on this project',
    empty: 'Settings placeholder. Runner mode, auto-commit, and orchestrator model controls arrive next.',
    icon: '⚙',
  },
  {
    key: 'settings-defaults',
    group: 'config',
    parent: 'settings',
    label: 'Workspace Defaults',
    panelTitle: 'Settings',
    description: 'Global defaults inherited from Workspace settings (default agent, usage caps)',
    empty: 'Workspace defaults placeholder.',
    icon: '⚙',
  },
  {
    key: 'settings-overrides',
    group: 'config',
    parent: 'settings',
    label: 'Project Overrides',
    panelTitle: 'Settings',
    description: 'Per-project settings that win over the inherited workspace defaults',
    empty: 'Project overrides placeholder.',
    icon: '⚙',
  },
];

/** Keys that have at least one child (i.e. render a disclosure twisty). */
export const PROJECT_RAIL_PARENT_KEYS: readonly ProjectRailKey[] = Array.from(
  new Set(PROJECT_RAIL_ITEMS.filter(i => i.parent).map(i => i.parent as ProjectRailKey)),
);

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
