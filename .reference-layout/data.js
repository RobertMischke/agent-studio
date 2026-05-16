// Mock data for Agent Software Studio prototype

const PROJECTS = [
  { id: "runbook", name: "Runbook", short: "R", color: "#d97557", cli: "auto", quota: "447", branch: "main", tasks: 12, workspace: "agentic-platform" },
  { id: "agent-task-processor", name: "Agent Task Processor", short: "A", color: "#d97757", cli: "auto", quota: "1.3M", branch: "main", tasks: 26, workspace: "agentic-platform" },
  { id: "lotta-dashboard", name: "Lotta Dashboard", short: "L", color: "#4ec9b0", cli: "auto", quota: "5.8M", branch: "main", tasks: 8, workspace: "lotta-school" },
  { id: "acme-shop", name: "Acme Shop", short: "S", color: "#c586c0", cli: "auto", quota: "920K", branch: "develop", tasks: 14, workspace: "client-acme" },
  { id: "acme-admin", name: "Acme Admin", short: "M", color: "#569cd6", cli: "auto", quota: "340K", branch: "develop", tasks: 5, workspace: "client-acme" },
];

const WORKSPACES = [
  {
    id: "agentic-platform",
    name: "agentic-platform",
    path: "~/work/agentic-platform",
    projectIds: ["runbook", "agent-task-processor"],
  },
  {
    id: "client-acme",
    name: "client-acme",
    path: "~/work/client-acme",
    projectIds: ["acme-shop", "acme-admin"],
  },
  {
    id: "lotta-school",
    name: "lotta-school",
    path: "~/work/lotta-school",
    projectIds: ["lotta-dashboard"],
  },
];
const WORKSPACE = WORKSPACES[0];

const LANES = {
  backlog: [
    { key: "backlog", label: "Backlog", count: 2 },
    { key: "in-prep", label: "In Preparation", count: 15 },
    { key: "orch-prep", label: "Orch Prep", count: 0 },
    { key: "needs-clar", label: "Needs Clarification", count: 9 },
    { key: "human-ready", label: "Human Ready", count: 0 },
  ],
  active: [
    { key: "in-progress", label: "In Progress", count: 1 },
    { key: "auto-review", label: "Auto Review", count: 8 },
  ],
  done: [
    { key: "human-review", label: "Human Review", count: 108 },
    { key: "completed", label: "Completed", count: 0 },
    { key: "archive", label: "Archive", count: 288 },
  ],
};

const TASKS = [
  {
    id: 1, project: "agent-task-processor", num: "#1", branch: "Local Default",
    title: "Implement session-task linkage chip in side-sheet (Phase 0+1+perf+E2E)",
    type: "feature", state: "auto-review", review: "review reissue",
    cli: "claude", model: "claude-opus-4-7",
    tags: ["code-review:block"], commit: "8e8e658", files: 0,
    activity: "10h ago", lane: "auto-review",
    description: "Follow-up to the evaluation in agent-taskboard/7-archive/<this-evaluation-slug>/results/session-task-linkage-plan.md. Read sections 1-6 of that plan first; section 7 explains why the implementation was deferred to this task.\n\nGoal: Each row in the side-sheet Sessions segment gets a small chip that links to the owning kanban task when one of the project's jobs lists this session id in its SessionChain.\n\nChip states: active (green, owning job is in 3-progress and currently active), linked (neutral, owning job exists but is not active), or no chip for orphan sessions.",
    activityLog: [
      { type: "decision", time: "14:13:43", text: "[reissue] Decision: reissue (multi-aspect block). Aspects: requirement-fit=block, code-quality=block, documentation-impact=concerns, tests-and-evidence=block" },
      { type: "step", time: "14:09:12", text: "Frontend unit suite: cli-usage-sheet.spec.ts still passes (1/1) — no regression on the legacy panel." },
      { type: "step", time: "14:08:01", text: "Playwright spec: session-task-link-chip.spec.ts type-checks and discovers cleanly; execution is reserved for stable's Playwright pass per the dev-backend-lifecycle rule." },
      { type: "step", time: "14:07:30", text: "Git: clean, on main, in sync with origin/main." },
    ],
    diff: { added: 242, removed: 2, files: 12 },
  },
  {
    id: 2, project: "agent-task-processor", num: "#2", branch: "Local Default",
    title: "Unified confirm + notification modals (app-consistent look & feel)",
    type: "feature", state: "auto-review", review: "review reissue",
    cli: "claude", commit: null, files: 0, activity: "1d ago", lane: "auto-review",
  },
  {
    id: 3, project: "agent-task-processor", num: "#3", branch: "Local Default",
    title: "Bug: DELETE-Button auf den Task-Karten hat keine Wirkung",
    type: "bug", state: "auto-review", review: "review escalate",
    cli: "codex", tags: ["reissue:autoreview"], commit: "1e2fcf1", files: 3,
    activity: "1d ago", lane: "auto-review",
  },
  {
    id: 4, project: "agent-task-processor", num: "#4", branch: "Local Default",
    title: "Feature: Auto-Push-Strategie — nur Commits pushen die in Completed-Lane gelandet sind (Review durchlaufen)",
    type: "feature", state: "auto-review", review: "review reissue",
    cli: "codex", warning: "Missing sentinel", commit: "6ed6907", files: 32,
    activity: "1d ago", lane: "auto-review",
  },
  {
    id: 5, project: "agent-task-processor", num: "#7", branch: "Local Default",
    title: "Software-to-architecture drift analysis action",
    type: "chore", state: "auto-review", review: "queued for review",
    cli: "claude", model: "claude-opus-4-7", warning: "Missing sentinel",
    commit: "7507ae2", files: 0, activity: "1d ago", lane: "auto-review",
  },
  {
    id: 6, project: "agent-task-processor", num: "#8", branch: "Local Default",
    title: "Fix Orchestrator Decision closing Task incorrectly",
    type: "bug", state: "auto-review", review: "review accept",
    cli: "claude", model: "claude-opus-4-7",
    commit: null, files: 0, activity: "2d ago", lane: "auto-review",
  },
  {
    id: 7, project: "agent-task-processor", num: "#0", branch: "Local Default",
    title: "Bug: Git-Diff-View zeigt Zeilennummern aber keinen Code-Inhalt",
    type: "bug", state: "human-review", cli: "claude", model: "claude-opus-4-7",
    tags: ["reissue:autoreview", "requirement:concerns"],
    commit: null, files: 0, activity: "52m ago", lane: "human-review",
  },
  {
    id: 8, project: "agent-task-processor", num: "#0", branch: "Local Default",
    title: "Jobs API: batch move/restore endpoint to close the manual-mv excuse",
    type: "feature", state: "human-review", cli: "claude",
    commit: null, files: 0, activity: "15h ago", lane: "human-review",
  },
  {
    id: 9, project: "agent-task-processor", num: "#0", branch: "Local Default",
    title: "Jobs API: restore-from-failed-pickup with slug rewrite",
    type: "feature", state: "human-review", cli: "claude",
    tags: ["requirement:concerns", "quality:concerns"],
    commit: "bc030bb", files: 0, activity: "14h ago", lane: "human-review",
  },
  {
    id: 10, project: "agent-task-processor", num: "#0", branch: "Local Default",
    title: "Notifications: operator-friendly English copy + correct firing time",
    type: "bug", state: "human-review", cli: "claude",
    tags: ["Environment blocker"],
    commit: null, files: 0, activity: "10h ago", lane: "human-review",
  },
  {
    id: 11, project: "agent-task-processor", num: "#0", branch: "Local Default",
    title: "TaskAccess phases 2-4: in-memory store, typed mutations, consumer migration",
    type: "feature", state: "human-review", cli: "claude",
    tags: ["requirement:concerns", "quality:concerns"],
    commit: "e91f30d", files: 0, activity: "14h ago", lane: "human-review",
  },
  {
    id: 12, project: "agent-task-processor", num: "#1", branch: "Local Default",
    title: "Add loading state to Archive All button",
    type: "feature", state: "human-review", cli: "claude",
    commit: null, files: 0, activity: "1d ago", lane: "human-review",
  },
  // Backlog samples
  {
    id: 13, project: "agent-task-processor", num: "#9", branch: "Local Default",
    title: "Refactor: extract job state machine into pure module",
    type: "chore", state: "backlog", cli: null, activity: "3d ago", lane: "in-prep",
  },
  {
    id: 14, project: "agent-task-processor", num: "#10", branch: "Local Default",
    title: "Side-sheet keyboard nav: j/k, enter, esc semantics",
    type: "feature", state: "backlog", cli: null, activity: "3d ago", lane: "in-prep",
  },
  {
    id: 15, project: "agent-task-processor", num: "#11", branch: "Local Default",
    title: "Investigate flaky test in OrchestratorChatProjectStateSnapshotTests",
    type: "bug", state: "backlog", cli: null, activity: "2d ago", lane: "needs-clar",
  },
  {
    id: 16, project: "agent-task-processor", num: "#12", branch: "Local Default",
    title: "Wire Runbook quota chip to live budget endpoint",
    type: "feature", state: "backlog", cli: null, activity: "5h ago", lane: "in-prep",
  },
  // In progress
  {
    id: 17, project: "agent-task-processor", num: "#5", branch: "Local Default",
    title: "Live: Migrate command palette to react-aria",
    type: "feature", state: "in-progress", cli: "claude", model: "claude-opus-4-7",
    activity: "running · 2m", lane: "in-progress",
  },
  // Cross-project samples
  {
    id: 30, project: "runbook", num: "#R-1", branch: "Local Default",
    title: "Runbook editor: insert/replace snippets via slash commands",
    type: "feature", state: "auto-review", review: "review reissue",
    cli: "claude", commit: "a112b3c", files: 4, activity: "2h ago", lane: "auto-review",
  },
  {
    id: 31, project: "runbook", num: "#R-2", branch: "Local Default",
    title: "Bug: Schedule trigger fires twice on midnight rollover",
    type: "bug", state: "human-review", cli: "codex",
    tags: ["reissue:autoreview"], commit: null, files: 0, activity: "4h ago", lane: "human-review",
  },
  {
    id: 32, project: "lotta-dashboard", num: "#L-3", branch: "Local Default",
    title: "Chart legend wraps when more than 8 series — hide overflow & add expander",
    type: "bug", state: "backlog", cli: null, activity: "1d ago", lane: "in-prep",
  },
  {
    id: 33, project: "lotta-dashboard", num: "#L-4", branch: "Local Default",
    title: "Add SSO login (Okta) — phase 1: discovery + ADR",
    type: "feature", state: "auto-review", review: "queued for review",
    cli: "claude", commit: null, files: 0, activity: "3h ago", lane: "auto-review",
  },
  // acme-shop tasks (client-acme workspace)
  {
    id: 40, project: "acme-shop", num: "#S-1", branch: "develop",
    title: "Cart: empty-state CTA copy + illustration swap",
    type: "feature", state: "auto-review", review: "review accept",
    cli: "codex", commit: "92f1aa3", files: 6, activity: "1h ago", lane: "auto-review",
  },
  {
    id: 41, project: "acme-shop", num: "#S-2", branch: "develop",
    title: "Checkout: payment-method radio loses keyboard focus after error",
    type: "bug", state: "in-progress", cli: "claude",
    activity: "running · 12m", lane: "in-progress",
  },
  {
    id: 42, project: "acme-shop", num: "#S-3", branch: "develop",
    title: "Refactor: collapse legacy cart hooks into useCart()",
    type: "chore", state: "backlog", cli: null, activity: "2d ago", lane: "in-prep",
  },
  {
    id: 43, project: "acme-admin", num: "#M-1", branch: "develop",
    title: "Order detail: surface fulfillment SLA timer with alert thresholds",
    type: "feature", state: "human-review", cli: "claude",
    tags: ["requirement:concerns"],
    commit: "d77c12a", files: 11, activity: "6h ago", lane: "human-review",
  },
  {
    id: 44, project: "acme-admin", num: "#M-2", branch: "develop",
    title: "Audit log export: CSV with timezone-aware timestamps",
    type: "feature", state: "auto-review", review: "queued for review",
    cli: "codex", commit: null, files: 0, activity: "30m ago", lane: "auto-review",
  },
];

const ACTIVITY_FEED = [
  { time: "14:13", project: "agent-task-processor", text: "Orchestrator reissued #1 (multi-aspect block)", kind: "reissue" },
  { time: "14:08", project: "agent-task-processor", text: "Playwright spec session-task-link-chip discovered cleanly", kind: "step" },
  { time: "14:01", project: "agent-task-processor", text: "Codex pushed commit 1e2fcf1 (3 files) on #3", kind: "commit" },
  { time: "13:42", project: "lotta-dashboard", text: "Claude completed #18 — Auto-review accepted", kind: "accept" },
  { time: "13:30", project: "runbook", text: "Auto-Review idle — no queued jobs", kind: "idle" },
  { time: "13:12", project: "agent-task-processor", text: "Human review queue grew to 108 items", kind: "info" },
  { time: "12:55", project: "agent-task-processor", text: "Auto-Push deferred for #4 — Completed lane gate failed", kind: "warn" },
];

const CLIS = [
  {
    id: "copilot", name: "Copilot", state: "ok", color: "#4ec9b0",
    quotas: [
      { period: "month", used: 0.78, resetsIn: "tomorrow 04:00", short: "23h" },
    ],
  },
  {
    id: "claude", name: "Claude", state: "throttle", color: "#d97757", warn: true,
    quotas: [
      { period: "5h", used: 0.92, resetsIn: "5h 12m", short: "5h", critical: true },
      { period: "week", used: 0.47, resetsIn: "3d 14h", short: "3d" },
    ],
  },
  {
    id: "codex", name: "Codex", state: "running", color: "#569cd6",
    quotas: [
      { period: "5h", used: 0.12, resetsIn: "4h 23m", short: "4h" },
      { period: "week", used: 0.31, resetsIn: "3d 14h", short: "3d" },
    ],
  },
  {
    id: "gemini", name: "Gemini", state: "idle", color: "#c586c0",
    quotas: [
      { period: "day", used: 0.0, resetsIn: "tomorrow", short: "—" },
    ],
  },
];

const TYPE_META = {
  feature: { label: "Feature", icon: "✦", color: "#4ec9b0" },
  bug: { label: "Bug", icon: "●", color: "#f48771" },
  chore: { label: "Chore", icon: "◆", color: "#b5cea8" },
};

const CLI_META = {
  claude: { label: "claude", color: "#d97757", glyph: "C" },
  codex: { label: "codex", color: "#569cd6", glyph: "X" },
  copilot: { label: "copilot", color: "#4ec9b0", glyph: "P" },
  gemini: { label: "gemini", color: "#c586c0", glyph: "G" },
};

window.MOCK = { PROJECTS, WORKSPACE, WORKSPACES, LANES, TASKS, ACTIVITY_FEED, CLIS, TYPE_META, CLI_META };
