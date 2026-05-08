#!/usr/bin/env node
// Layer 3 system health check (bus-shaped evidence).
//
// Reads Agent Message Bus JSONL evidence (single fixture file or full
// workspace bus directory) and prints a structured Markdown report with
// findings for every check listed in the Layer 3 deliverable:
//   - long silent periods
//   - repeated interventions
//   - repeated failed or cancelled runs
//   - token spikes
//   - many supporting jobs without accepted review
//   - stuck loops
//   - jobs that reached review with weak or missing evidence
//   - backend crash markers
//
// Read-only. Never mutates the workspace, never starts the app, never
// posts to the bus. Designed to be runnable standalone (no npm install)
// so it can be called from inside the system-review CLI session as a
// dry-run, or from a CI test, or by hand.
//
// Exit codes:
//   0 - findings produced (healthy or otherwise)
//   2 - input missing or unreadable
//   3 - unsupported argument

import { readFileSync, statSync, readdirSync, existsSync } from "node:fs";
import { join, basename, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

// ---- argv ------------------------------------------------------------

const argv = process.argv.slice(2);
const opts = {
  fixture: null,
  workspace: null,
  project: null,
  stable: process.env.ATP_STABLE_CHECKOUT || null,
  out: null,
  json: false,
  silentMinutes: parseInt(process.env.ATP_SILENT_MINUTES || "60", 10),
  stuckLoopThreshold: parseInt(process.env.ATP_STUCK_LOOP_THRESHOLD || "3", 10),
  failedRunThreshold: parseInt(process.env.ATP_FAILED_RUN_THRESHOLD || "2", 10),
  interventionThreshold: parseInt(process.env.ATP_INTERVENTION_THRESHOLD || "3", 10),
  tokenSpikeOutput: parseInt(process.env.ATP_TOKEN_SPIKE_OUTPUT || "20000", 10),
  tokenSpikeDollars: parseFloat(process.env.ATP_TOKEN_SPIKE_DOLLARS || "5"),
  supportingWithoutReviewThreshold: parseInt(
    process.env.ATP_SUPPORT_WITHOUT_REVIEW_THRESHOLD || "3",
    10,
  ),
};

for (let i = 0; i < argv.length; i += 1) {
  const a = argv[i];
  switch (a) {
    case "--fixture": opts.fixture = argv[++i]; break;
    case "--workspace": opts.workspace = argv[++i]; break;
    case "--project": opts.project = argv[++i]; break;
    case "--stable": opts.stable = argv[++i]; break;
    case "--out": opts.out = argv[++i]; break;
    case "--json": opts.json = true; break;
    case "-h":
    case "--help":
      printHelp();
      process.exit(0);
      break;
    default:
      process.stderr.write(`unknown argument: ${a}\n`);
      process.exit(3);
  }
}

if (!opts.fixture && !opts.workspace) {
  // Default to the bundled sample fixture so a bare `node system-health-check.mjs`
  // is a working dry-run.
  const here = dirname(fileURLToPath(import.meta.url));
  opts.fixture = join(here, "fixtures", "sample-bus.jsonl");
}

function printHelp() {
  process.stdout.write(`Usage:
  node system-health-check.mjs [--fixture <file>] [--workspace <dir>] [options]

Inputs (pick one):
  --fixture <file>     A single bus JSONL file (a day-file or a hand-built fixture).
  --workspace <dir>    A workspace root (the script reads logs/bus/ underneath).
  --project <name>     When --workspace is set, scope to one project subdirectory.
                       Default: scan every project plus the _workspace scope.

Optional:
  --stable <dir>       Stable checkout root; the script also reads
                       <stable>/logs/backend/last-crash.json when present.
  --out <path>         Write the Markdown report to <path> instead of stdout.
  --json               Emit machine-readable JSON instead of Markdown.

Thresholds (env-overridable):
  ATP_SILENT_MINUTES                       (default 60)
  ATP_STUCK_LOOP_THRESHOLD                 (default 3)
  ATP_FAILED_RUN_THRESHOLD                 (default 2)
  ATP_INTERVENTION_THRESHOLD               (default 3)
  ATP_TOKEN_SPIKE_OUTPUT                   (default 20000)
  ATP_TOKEN_SPIKE_DOLLARS                  (default 5)
  ATP_SUPPORT_WITHOUT_REVIEW_THRESHOLD     (default 3)
`);
}

// ---- load messages ---------------------------------------------------

/** @typedef {{
 *   id: string, createdAt: string, participantId: string, role: string,
 *   kind: string, severity?: string, project?: string|null, jobId?: string|null,
 *   runId?: string|null, cliSessionId?: string|null, topic?: string|null,
 *   summary?: string, body?: string, replyToId?: string|null,
 *   correlationId?: string|null, tokens?: any, artifacts?: any[],
 *   payload?: any, tags?: string[],
 *   _sourceFile?: string,
 * }} BusMessage */

function readJsonl(path) {
  const text = readFileSync(path, "utf8");
  /** @type {BusMessage[]} */
  const out = [];
  let lineNo = 0;
  for (const line of text.split(/\r?\n/)) {
    lineNo += 1;
    if (!line.trim()) continue;
    try {
      const msg = JSON.parse(line);
      msg._sourceFile = `${path}:${lineNo}`;
      out.push(msg);
    } catch (err) {
      process.stderr.write(`skip malformed JSONL line ${path}:${lineNo}\n`);
    }
  }
  return out;
}

function readWorkspaceBus(workspaceRoot, projectFilter) {
  const root = join(workspaceRoot, "logs", "bus");
  if (!existsSync(root)) {
    process.stderr.write(`no bus directory at ${root}\n`);
    return [];
  }
  /** @type {BusMessage[]} */
  const out = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    if (entry.name === "participants") continue;
    const project = entry.name === "_workspace" ? null : entry.name;
    if (projectFilter && project !== projectFilter) continue;
    const dir = join(root, entry.name);
    for (const f of readdirSync(dir)) {
      if (!f.endsWith(".jsonl")) continue;
      out.push(...readJsonl(join(dir, f)));
    }
  }
  return out;
}

let messages = [];
let inputLabel = "";
try {
  if (opts.fixture) {
    inputLabel = `fixture ${opts.fixture}`;
    messages = readJsonl(opts.fixture);
  } else {
    inputLabel = `workspace ${opts.workspace}` + (opts.project ? ` (project=${opts.project})` : "");
    messages = readWorkspaceBus(opts.workspace, opts.project);
  }
} catch (err) {
  process.stderr.write(`failed to read input: ${err.message}\n`);
  process.exit(2);
}

// Lexical id sort matches creation time for ULID / UUIDv7. Falls back to
// createdAt if ids are not monotonic.
messages.sort((a, b) => {
  const c = a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  if (c !== 0) return c;
  return a.createdAt.localeCompare(b.createdAt);
});

// ---- helpers ---------------------------------------------------------

function ref(m) {
  // One-line evidence link surface used by every finding.
  const parts = [`msg=${m.id}`];
  if (m.project) parts.push(`project=${m.project}`);
  if (m.jobId) parts.push(`job=${m.jobId}`);
  if (m.runId) parts.push(`run=${m.runId}`);
  if (m.artifacts && m.artifacts.length) {
    parts.push(`artifacts=${m.artifacts.map(a => a.uri).join("|")}`);
  }
  return parts.join(" ");
}

function ts(s) { return new Date(s).getTime(); }

function fmtMinutes(ms) {
  return `${Math.round(ms / 60000)}m`;
}

function pushFinding(findings, severity, check, title, evidence, recommendation) {
  findings.push({ severity, check, title, evidence, recommendation });
}

// ---- checks ----------------------------------------------------------

const findings = [];

// 1. Long silent periods, scoped per project (and "_workspace" for nulls).
{
  const byProject = new Map();
  for (const m of messages) {
    const key = m.project || "_workspace";
    if (!byProject.has(key)) byProject.set(key, []);
    byProject.get(key).push(m);
  }
  const limitMs = opts.silentMinutes * 60 * 1000;
  for (const [project, list] of byProject) {
    for (let i = 1; i < list.length; i += 1) {
      const gap = ts(list[i].createdAt) - ts(list[i - 1].createdAt);
      if (gap >= limitMs) {
        // Highlight as actionable only when an active run was open across the gap.
        const before = list.slice(0, i).reverse();
        const lastRunStart = before.find(x => x.kind === "lifecycle" && x.topic === "RunStarted");
        const lastRunFinish = before.find(x => x.kind === "lifecycle" && x.topic === "RunFinished");
        const runOpen = lastRunStart &&
          (!lastRunFinish || ts(lastRunStart.createdAt) > ts(lastRunFinish.createdAt));
        const sev = runOpen ? "High" : "Warn";
        pushFinding(findings, sev, "long-silent-period",
          `Silent period of ${fmtMinutes(gap)} on project=${project}`,
          [
            `before: ${ref(list[i - 1])} (${list[i - 1].createdAt})`,
            `after:  ${ref(list[i])} (${list[i].createdAt})`,
            runOpen ? `run open across gap: ${ref(lastRunStart)}` : `no open run across gap`,
          ],
          runOpen
            ? "Inspect the active run; the agent may be stuck or the backend may be hung."
            : "Confirm the project is intentionally idle (no queued work, no scheduler).");
      }
    }
  }
}

// 2. Repeated interventions per project / window.
{
  const intervs = messages.filter(m => m.kind === "intervention");
  const byProject = new Map();
  for (const m of intervs) {
    const key = m.project || "_workspace";
    if (!byProject.has(key)) byProject.set(key, []);
    byProject.get(key).push(m);
  }
  for (const [project, list] of byProject) {
    if (list.length >= opts.interventionThreshold) {
      pushFinding(findings, "High", "repeated-interventions",
        `${list.length} interventions on project=${project}`,
        list.map(m => `${m.topic || "?"} ${m.createdAt} ${ref(m)}`),
        "Investigate before resuming pickup; repeated interventions usually mean a single root cause is recurring.");
    }
  }
}

// 3. Repeated failed or cancelled runs per job.
{
  const finishes = messages.filter(m =>
    m.kind === "lifecycle" && m.topic === "RunFinished" &&
    m.payload && (m.payload.outcome === "Failed" || m.payload.outcome === "Cancelled"));
  const byJob = new Map();
  for (const m of finishes) {
    const key = `${m.project || "_workspace"}::${m.jobId || "?"}`;
    if (!byJob.has(key)) byJob.set(key, []);
    byJob.get(key).push(m);
  }
  for (const [key, list] of byJob) {
    if (list.length >= opts.failedRunThreshold) {
      pushFinding(findings, "High", "repeated-failed-runs",
        `${list.length} failed/cancelled runs on ${key}`,
        list.map(m => `${m.payload.outcome} ${m.createdAt} ${ref(m)}`),
        "Look at the latest run's cli-output.log slice and supervisor advisories before reissuing.");
    }
  }
}

// 4. Token spikes.
{
  for (const m of messages) {
    if (m.kind !== "token-usage" || !m.tokens) continue;
    const out = Number(m.tokens.output || 0);
    const dollars = Number(m.tokens.dollars || 0);
    if (out >= opts.tokenSpikeOutput || dollars >= opts.tokenSpikeDollars) {
      pushFinding(findings, dollars >= opts.tokenSpikeDollars * 2 ? "High" : "Warn",
        "token-spike",
        `Token spike output=${out} dollars=${dollars} on ${ref(m)}`,
        [
          `model=${m.tokens.model || "?"}`,
          `cacheRead=${m.tokens.cacheRead || 0} cacheWrite=${m.tokens.cacheWrite || 0}`,
          `summary=${m.summary || ""}`,
        ],
        "Confirm the spike was a single expensive turn (council, large input) and not an accidental retry loop.");
    }
  }
}

// 5. Many supporting jobs without accepted review.
{
  // Group support: messages by job; flag jobs with >= threshold support
  // decisions but no JobStateMoved with payload.to=6-completed.
  const byJob = new Map();
  for (const m of messages) {
    if (m.kind !== "decision") continue;
    if (!m.participantId.startsWith("support:")) continue;
    const key = `${m.project || "_workspace"}::${m.jobId || "?"}`;
    if (!byJob.has(key)) byJob.set(key, []);
    byJob.get(key).push(m);
  }
  const completedJobs = new Set();
  for (const m of messages) {
    if (m.kind === "lifecycle" && m.topic === "JobStateMoved" &&
        m.payload && m.payload.to === "6-completed") {
      completedJobs.add(`${m.project || "_workspace"}::${m.jobId || "?"}`);
    }
  }
  for (const [key, list] of byJob) {
    if (list.length >= opts.supportingWithoutReviewThreshold && !completedJobs.has(key)) {
      pushFinding(findings, "Warn", "supporting-without-review",
        `${list.length} supporting decisions on ${key} without a 6-completed move`,
        list.map(m => `${m.topic || m.participantId} ${m.createdAt} ${ref(m)}`),
        "Either accept the work into 6-completed or surface the missing acceptance to the user.");
    }
  }
}

// 6. Stuck loops: N consecutive orchestrator decisions of kind reissue / heuristic-fallback.
{
  const byJob = new Map();
  for (const m of messages) {
    if (m.kind !== "decision") continue;
    if (!m.participantId.startsWith("orchestrator")) continue;
    const key = `${m.project || "_workspace"}::${m.jobId || "?"}`;
    if (!byJob.has(key)) byJob.set(key, []);
    byJob.get(key).push(m);
  }
  for (const [key, list] of byJob) {
    list.sort((a, b) => a.createdAt.localeCompare(b.createdAt));
    let runStart = -1;
    for (let i = 0; i < list.length; i += 1) {
      const t = list[i].topic;
      const looping = t === "reissue" || t === "heuristic-fallback";
      if (looping) {
        if (runStart < 0) runStart = i;
        const len = i - runStart + 1;
        if (len >= opts.stuckLoopThreshold) {
          // Emit once per streak when threshold first crossed; collect remainder.
          if (len === opts.stuckLoopThreshold) {
            const slice = list.slice(runStart, i + 1);
            pushFinding(findings, "High", "stuck-loop",
              `Orchestrator loop of ${len}+ reissue/heuristic-fallback on ${key}`,
              slice.map(m => `${m.topic} ${m.createdAt} ${ref(m)}`),
              "Pause pickup and inspect the run; the prompt or sentinel handling is most likely the culprit.");
          }
        }
      } else {
        runStart = -1;
      }
    }
  }
}

// 7. Jobs reached review with weak or missing evidence.
{
  // For every JobStateMoved that landed on 4-auto-review, look at all bus
  // messages for that (project, jobId) carrying the same runId as the
  // most recent RunStarted for the job. If none of them have artifacts,
  // flag the review as weak.
  const reviewMoves = messages.filter(m =>
    m.kind === "lifecycle" && m.topic === "JobStateMoved" &&
    m.payload && m.payload.to === "4-auto-review");
  for (const move of reviewMoves) {
    const jobKey = `${move.project || "_workspace"}::${move.jobId || "?"}`;
    const jobMsgs = messages.filter(m =>
      (m.project || "_workspace") === (move.project || "_workspace") &&
      m.jobId === move.jobId &&
      ts(m.createdAt) <= ts(move.createdAt));
    const lastRunStart = [...jobMsgs].reverse().find(m =>
      m.kind === "lifecycle" && m.topic === "RunStarted");
    if (!lastRunStart) continue;
    const runId = lastRunStart.runId;
    const runMsgs = jobMsgs.filter(m => m.runId === runId);
    const artifactCount = runMsgs.reduce((n, m) =>
      n + (Array.isArray(m.artifacts) ? m.artifacts.length : 0), 0);
    if (artifactCount === 0) {
      pushFinding(findings, "Warn", "weak-review-evidence",
        `${jobKey} reached 4-auto-review with no artifacts attached to its run`,
        [
          `move: ${ref(move)} (${move.createdAt})`,
          `run:  ${ref(lastRunStart)} (${lastRunStart.createdAt})`,
          `messages-in-run=${runMsgs.length} artifacts=0`,
        ],
        "Block acceptance until the run produces screenshots, log slices, or a markdown-report artifact.");
    }
  }
}

// 8. Backend crash markers.
{
  // 8a. kind=error from runtime:taskboard in the bus stream.
  for (const m of messages) {
    if (m.kind !== "error") continue;
    if (!m.participantId.startsWith("runtime:")) continue;
    pushFinding(findings, m.severity === "High" ? "High" : "Warn", "backend-crash",
      `${m.payload?.exceptionType || "error"} from ${m.participantId}`,
      [
        `source=${m.payload?.source || "?"}`,
        `topFrame=${m.payload?.topFrame || "?"}`,
        ref(m),
      ],
      "Open the matching daily backend log for the full stack and decide whether a restart or a rollback is needed.");
  }
  // 8b. on-disk last-crash.json when --stable supplied.
  if (opts.stable) {
    const crashFile = join(opts.stable, "logs", "backend", "last-crash.json");
    if (existsSync(crashFile)) {
      try {
        const raw = JSON.parse(readFileSync(crashFile, "utf8"));
        pushFinding(findings, "High", "backend-crash",
          `last-crash.json present: ${raw.exceptionType || "?"}`,
          [
            `capturedAt=${raw.capturedAt || "?"}`,
            `source=${raw.source || "?"}`,
            `message=${(raw.message || "").slice(0, 200)}`,
            `file=${crashFile}`,
          ],
          "Cross-reference with the matching daily log under <stable>/logs/backend/<date>.log.");
      } catch (err) {
        pushFinding(findings, "Warn", "backend-crash",
          `last-crash.json present but unparseable`,
          [`file=${crashFile}`, `error=${err.message}`],
          "Inspect the file directly; the writer may have been interrupted mid-write.");
      }
    }
  }
}

// ---- summary ---------------------------------------------------------

function severityRank(s) { return s === "High" ? 0 : s === "Warn" ? 1 : 2; }
findings.sort((a, b) => severityRank(a.severity) - severityRank(b.severity) ||
  a.check.localeCompare(b.check));

const counts = { High: 0, Warn: 0, Info: 0 };
for (const f of findings) counts[f.severity] = (counts[f.severity] || 0) + 1;

const verdict = counts.High > 0 ? "Action needed" :
                counts.Warn > 0 ? "Caution" : "Healthy";

const firstAt = messages.length ? messages[0].createdAt : null;
const lastAt = messages.length ? messages[messages.length - 1].createdAt : null;

const report = {
  generatedAt: new Date().toISOString(),
  source: inputLabel,
  messagesScanned: messages.length,
  windowFirstAt: firstAt,
  windowLastAt: lastAt,
  thresholds: {
    silentMinutes: opts.silentMinutes,
    stuckLoopThreshold: opts.stuckLoopThreshold,
    failedRunThreshold: opts.failedRunThreshold,
    interventionThreshold: opts.interventionThreshold,
    tokenSpikeOutput: opts.tokenSpikeOutput,
    tokenSpikeDollars: opts.tokenSpikeDollars,
    supportingWithoutReviewThreshold: opts.supportingWithoutReviewThreshold,
  },
  verdict,
  counts,
  findings,
};

// ---- output ----------------------------------------------------------

function renderMarkdown(r) {
  const lines = [];
  lines.push(`# System health findings`);
  lines.push("");
  lines.push(`**Generated:** ${r.generatedAt}`);
  lines.push(`**Source:** ${r.source}`);
  lines.push(`**Messages scanned:** ${r.messagesScanned}`);
  if (r.windowFirstAt) lines.push(`**Window:** ${r.windowFirstAt} -> ${r.windowLastAt}`);
  lines.push("");
  lines.push(`## Verdict`);
  lines.push("");
  lines.push(`**${r.verdict}.** High=${r.counts.High || 0}, Warn=${r.counts.Warn || 0}, Info=${r.counts.Info || 0}.`);
  lines.push("");
  lines.push(`## Thresholds`);
  for (const [k, v] of Object.entries(r.thresholds)) {
    lines.push(`- \`${k}\`: ${v}`);
  }
  lines.push("");
  lines.push(`## Findings (${r.findings.length})`);
  if (r.findings.length === 0) {
    lines.push("");
    lines.push(`No findings. Nothing tripped a check.`);
  } else {
    for (const f of r.findings) {
      lines.push("");
      lines.push(`### [${f.severity}] ${f.check}: ${f.title}`);
      for (const e of f.evidence) lines.push(`- ${e}`);
      lines.push(`- **recommend:** ${f.recommendation}`);
    }
  }
  lines.push("");
  lines.push(`## Notes`);
  lines.push(`- Advice-first. This monitor never moves a job, never restarts the backend, and never edits source.`);
  lines.push(`- Every evidence line above is a typed reference (\`msg=\`, \`job=\`, \`run=\`, \`artifacts=\`) into bus messages and on-disk artifacts. Drill into them through the Project Screen Observability panel or by reading the JSONL line directly.`);
  return lines.join("\n") + "\n";
}

const output = opts.json ? JSON.stringify(report, null, 2) + "\n" : renderMarkdown(report);

if (opts.out) {
  const path = resolve(opts.out);
  // Lazy import to keep the script dependency-free at import time.
  const { writeFileSync, mkdirSync } = await import("node:fs");
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, output, "utf8");
  process.stderr.write(`wrote ${path}\n`);
} else {
  process.stdout.write(output);
}
