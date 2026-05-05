/**
 * Runtime registry of in-product concept entries shown by app-concept-help.
 *
 * Each entry mirrors the matching .md file in this folder. The .md files are
 * the human-readable source of truth (they are also the artifact the task
 * deliverable lists); the strings below are what the component reads at
 * runtime so we can avoid a build-loader change. Keep them in sync: when you
 * edit one, edit the other. Each body is at most 120 words by contract.
 */

export type ConceptKey =
  | 'orchestrator'
  | 'supervisor'
  | 'skills'
  | 'audits-and-checks'
  | 'probes'
  | 'companion-app';

export interface ConceptEntry {
  /** Stable key, used as the input value and in test ids. */
  readonly key: ConceptKey;
  /** Title shown at the top of the popover. */
  readonly title: string;
  /** Short body. Prose only. Plain text with blank lines between paragraphs. */
  readonly body: string;
  /** Repo-relative path to the longer companion doc. */
  readonly learnMore: string;
  /** Visible label for the "Learn more" link. */
  readonly learnMoreLabel: string;
}

const ENTRIES: Record<ConceptKey, ConceptEntry> = {
  orchestrator: {
    key: 'orchestrator',
    title: 'Orchestrator',
    body:
      'The orchestrator is the deterministic loop that picks the next ready task per project, starts a CLI agent, watches its output, and decides what happens after the run.\n\n' +
      'It owns task pickup, lifecycle transitions, and the post-run policy. The CLI agent reports its own outcome with sentinels like [[TASK_DONE]] or [[TASK_BLOCKED:...]]; the orchestrator treats those as one input among several and is the single arbiter that moves a job between lanes.\n\n' +
      'A separate global session reasons across all watched projects and surfaces decisions as orchestrator messages alongside your own.',
    learnMore: 'docs/architecture-decisions.md',
    learnMoreLabel: 'Architecture Decisions (ADR-0002)',
  },
  supervisor: {
    key: 'supervisor',
    title: 'Supervisor',
    body:
      'The supervisor is the per-project safety layer that watches the orchestrator and the running CLI agent in real time.\n\n' +
      'By default it is advice-first: it writes typed observations and advisories (info, warn, high) into the project log so a human or a meta-cycle can react. Four emergency primitives exist for the rare case when waiting is wrong: cancel run, pause pickup, force fail, and resume. Auto-intervention is a separate opt-in policy; without it the supervisor never moves work on its own.\n\n' +
      'Think of it as a kill-switch and a soft second opinion, not a parallel orchestrator.',
    learnMore: 'docs/architecture-decisions.md',
    learnMoreLabel: 'Architecture Decisions (ADR-0017)',
  },
  skills: {
    key: 'skills',
    title: 'Skills',
    body:
      'Skills are portable specialist workflows. Each skill is a short Markdown guide that explains how to do one specific kind of work well: a security review, a Playwright check, a CLI driver tweak, a release prep.\n\n' +
      'Skills are optional and situational. They never own task lifecycle, state movement, or queue policy. The orchestrator owns those rules; a skill only describes the craft on top.\n\n' +
      'A central library lives with the task processor and is shared across watched projects. The orchestrator can attach selected skills to a managed run; direct CLI sessions discover the same skills through a project lookup section.',
    learnMore: 'docs/skills-architecture.md',
    learnMoreLabel: 'Portable Skills Architecture',
  },
  'audits-and-checks': {
    key: 'audits-and-checks',
    title: 'Audits and Checks',
    body:
      "Audits and checks are scoped reviews that run against a project's evidence: source code, configuration, baselines, and prior reports. A security audit is one example; architecture drift, test quality, and token spend are others.\n\n" +
      'Each audit produces a typed report with a verdict (ok, stale, fail), open findings, and a link to the evidence file. The panel here lists the latest verdict, the active findings split by severity, and the baseline status.\n\n' +
      'An audit never silently mutates state. It reads files, writes a report, and may queue a follow-up task that you decide to accept.',
    learnMore: 'docs/security/overview.md',
    learnMoreLabel: 'Security Overview',
  },
  probes: {
    key: 'probes',
    title: 'Probes',
    body:
      'A probe is a small, read-only check the backend runs in the background to keep its picture of the world current.\n\n' +
      "Quota probes drive each CLI's /usage or /status slash command in a scratch directory and parse the response into the rate-limit pill you see in the header. Session probes discover existing CLI sessions on disk so you can resume one. Drift and audit probes inspect files for staleness.\n\n" +
      'Probes are not intervention: they observe, write a structured result, and surface it. If a probe fails, the panel shows stale or unknown rather than guessed data.',
    learnMore: 'docs/cli-skills/cli-overview.md',
    learnMoreLabel: 'CLI Skills Overview',
  },
  'companion-app': {
    key: 'companion-app',
    title: 'Companion App',
    body:
      'The companion app lets you check pipeline status, token usage, and open decisions from a phone, and post small steering interventions back to your local processor.\n\n' +
      'It is a three-tier shape: the local processor pushes a snapshot to a public relay over outbound HTTPS, and the phone PWA reads the snapshot and posts commands through the same relay. The local box never opens an inbound port.\n\n' +
      'The companion is read-mostly and intentionally small. It is not a second control surface; it is a way to see what is happening and nudge it when you are away from your desk.',
    learnMore: 'docs/companion-app-design.md',
    learnMoreLabel: 'Companion App Design',
  },
};

export function getConceptEntry(key: ConceptKey): ConceptEntry {
  return ENTRIES[key];
}

export function listConceptEntries(): ConceptEntry[] {
  return Object.values(ENTRIES);
}
