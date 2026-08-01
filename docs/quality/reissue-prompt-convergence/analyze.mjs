#!/usr/bin/env node

import crypto from "node:crypto";
import fs from "node:fs";
import http from "node:http";

const host = process.env.TASKBOARD_HOST ?? "127.0.0.1";
const port = Number(process.env.TASKBOARD_PORT ?? 5031);
const project = process.env.TASKBOARD_PROJECT ?? "AGT";
const projectName = process.env.TASKBOARD_PROJECT_NAME ?? "Agent Studio";
const clientId = process.env.TASKBOARD_CLIENT_ID ?? "local-default";
const concurrency = Math.max(1, Number(process.env.TASKBOARD_CONCURRENCY ?? 24));
const bootstrapSamples = Math.max(0, Number(process.env.BOOTSTRAP_SAMPLES ?? 2000));
const horizon = Math.max(1, Number(process.env.ATTEMPT_HORIZON ?? 5));
const requestTimeoutMs = Math.max(1_000, Number(process.env.TASKBOARD_REQUEST_TIMEOUT_MS ?? 45_000));
const requestRetries = Math.max(0, Number(process.env.TASKBOARD_REQUEST_RETRIES ?? 2));
const outputArgument = process.argv.find(argument => argument.startsWith("--output="));
const outputFile = outputArgument?.slice("--output=".length) ?? null;
let retriedRequestCount = 0;
let exhaustedRequestCount = 0;

function request(path) {
  return new Promise(resolve => {
    const call = http.request({
      hostname: host,
      port,
      path,
      method: "GET",
      headers: { "X-Client-Id": clientId },
    }, response => {
      let body = "";
      response.on("data", chunk => body += chunk);
      response.on("end", () => resolve({ status: response.statusCode ?? 0, body }));
    });
    call.setTimeout(requestTimeoutMs, () => call.destroy(new Error(`Timeout: ${path}`)));
    call.on("error", error => resolve({ status: 0, body: "", error: error.message }));
    call.end();
  });
}

async function get(path) {
  for (let attempt = 0; attempt <= requestRetries; attempt++) {
    const response = await request(path);
    const retryable = response.status === 0 || response.status === 429 || response.status >= 500;
    if (!retryable) return response;
    if (attempt === requestRetries) {
      exhaustedRequestCount++;
      return response;
    }
    retriedRequestCount++;
    await new Promise(resolve => setTimeout(resolve, 200 * (attempt + 1)));
  }
}

async function getJson(path) {
  const response = await get(path);
  if (response.status !== 200) return null;
  try {
    return JSON.parse(response.body);
  } catch {
    return null;
  }
}

async function mapPool(items, mapper) {
  const values = new Array(items.length);
  let cursor = 0;
  async function worker() {
    while (cursor < items.length) {
      const index = cursor++;
      values[index] = await mapper(items[index], index);
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, items.length) }, worker));
  return values;
}

function taskPath(task, suffix) {
  const id = encodeURIComponent(task.key || task.id);
  const separator = suffix.indexOf("&");
  const route = separator < 0 ? suffix : suffix.slice(0, separator);
  const extra = separator < 0 ? "" : `&${suffix.slice(separator + 1)}`;
  return `/api/tasks/${id}/${route}?project=${encodeURIComponent(project)}${extra}`;
}

async function listTasks() {
  const grouped = await getJson("/api/tasks/grouped");
  if (!grouped) throw new Error("Task API did not return /api/tasks/grouped");
  const active = Object.values(grouped).flatMap(value => Array.isArray(value) ? value : [])
    .filter(task => task.projectName === projectName);
  const archived = [];
  for (let offset = 0;;) {
    const page = await getJson(
      `/api/tasks/archive?project=${encodeURIComponent(project)}&offset=${offset}&limit=200`);
    if (!page) throw new Error(`Task API did not return archive offset ${offset}`);
    archived.push(...(page.items ?? []));
    offset += page.items?.length ?? 0;
    if (offset >= Number(page.total ?? 0) || (page.items?.length ?? 0) === 0) break;
  }
  const unique = new Map([...active, ...archived].map(task => [task.key || task.id, task]));
  return { tasks: [...unique.values()], active: active.length, archived: archived.length };
}

function historyFileNames(text) {
  return text.split(/\r?\n/).map(line => line.trim())
    .filter(line => /^\d{8}-\d{6}-\d{3}-reissue\.md$/i.test(line));
}

function timestamp(fileName) {
  const match = /^(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})-(\d{3})-reissue\.md$/i
    .exec(fileName);
  if (!match) return null;
  const [, year, month, day, hour, minute, second, millisecond] = match;
  return Date.parse(`${year}-${month}-${day}T${hour}:${minute}:${second}.${millisecond}Z`);
}

const wordPattern = /[A-Za-z0-9][A-Za-z0-9_'’-]*/g;
const filePattern = /(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+|(?:[A-Za-z0-9_-]+\.)+(?:cs|csproj|sln|ts|js|mjs|json|jsonl|md|html|scss|css|sh|yml|yaml|xml|sql|py)\b/i;
const findingPattern = /\b(?:finding|gap|issue|failure|defect|blocker|open item|missing|fails?|failed|incorrect|wrong|broken|stale|unresolved|does not|did not|must not|instead of|expected|actual)\b/i;

export function promptFeatures(content) {
  const marker = "## Steering prompt (verbatim)";
  const body = content.includes(marker)
    ? content.slice(content.indexOf(marker) + marker.length).trim()
    : content.replace(/^# Orchestrator follow-up\s*/i, "").trim();
  const directive = body
    .replace(/```[\s\S]*?```/g, "\n")
    .replace(/^\s*\[(?:stdout|stderr)\].*$/gim, "")
    .trim();
  const listLines = directive.split(/\r?\n/).filter(line => /^\s*(?:[-*+]|\d+[.)])\s+/.test(line));
  const concreteFindings = listLines.filter(line => {
    const normalized = line.replace(/^\s*(?:[-*+]|\d+[.)])\s+/, "").trim();
    const words = normalized.match(wordPattern) ?? [];
    return words.length >= 5 && (findingPattern.test(normalized) || filePattern.test(normalized));
  });
  return {
    directiveWordCount: (directive.match(wordPattern) ?? []).length,
    concreteFindingCount: new Set(concreteFindings.map(line => line.trim().toLowerCase())).size,
  };
}

async function firstPrompt(task) {
  const history = await getJson(taskPath(
    task, "files/orchestrator-follow-up-history/history&scope=workspace"));
  if (!Array.isArray(history) || history.length === 0) return null;
  let listing = null;
  let listingSha = null;
  for (const entry of history) {
    const response = await get(taskPath(
      task,
      `files/orchestrator-follow-up-history&scope=workspace&at=${encodeURIComponent(entry.sha)}`));
    if (response.status !== 200) continue;
    const names = historyFileNames(response.body);
    if (names.length > 0) {
      listing = names.sort();
      listingSha = entry.sha;
      break;
    }
  }
  if (!listing) return null;
  const fileName = listing[0];
  const response = await get(taskPath(
    task, `files/orchestrator-follow-up-history/${encodeURIComponent(fileName)}&scope=workspace`));
  if (response.status !== 200) return null;
  return {
    fileName,
    atMs: timestamp(fileName),
    listingSha,
    promptCount: listing.length,
    ...promptFeatures(response.body),
  };
}

function attemptRecords(execution) {
  if (!execution) return [];
  return [execution, ...(execution.previousAttempts ?? [])]
    .map(record => ({
      attempt: Number(record.attempt),
      startedAtMs: Date.parse(record.startedAt),
    }))
    .filter(record => Number.isFinite(record.attempt) && Number.isFinite(record.startedAtMs))
    .sort((left, right) => left.attempt - right.attempt);
}

function acceptanceEvents(reviews, timeline) {
  const events = [];
  for (const review of reviews) {
    if (review.councilReaction?.disposition !== "accept") continue;
    const atMs = Date.parse(review.councilReaction.createdAt ?? review.runAt);
    if (Number.isFinite(atMs)) events.push(atMs);
  }
  for (const event of timeline) {
    if (event.kind !== "orchestrator_verdict_accepted") continue;
    const atMs = Date.parse(event.ts);
    if (Number.isFinite(atMs)) events.push(atMs);
  }
  return events.sort((left, right) => left - right);
}

function attemptAt(records, atMs) {
  return records.filter(record => record.startedAtMs <= atMs).at(-1)?.attempt ?? null;
}

function firstAttemptAfter(records, atMs) {
  return records.find(record => record.startedAtMs >= atMs)?.attempt ?? null;
}

async function analyzeTask(task, prompt) {
  const [detail, pipeline, reviewData, timelineData] = await Promise.all([
    getJson(`/api/tasks/${encodeURIComponent(task.key || task.id)}?project=${encodeURIComponent(project)}`),
    getJson(taskPath(task, "pipeline")),
    getJson(taskPath(task, "code-review/list")),
    getJson(taskPath(task, "timeline")),
  ]);
  const info = detail?.info ?? task;
  const execution = pipeline?.execution ?? null;
  const records = attemptRecords(execution);
  const firstTarget = firstAttemptAfter(records, prompt.atMs);
  const firstAcceptAt = acceptanceEvents(
    reviewData?.entries ?? [],
    Array.isArray(timelineData) ? timelineData : [],
  ).find(atMs => atMs >= prompt.atMs);
  const acceptedAttempt = firstAcceptAt == null ? null : attemptAt(records, firstAcceptAt);
  const liveAttempt = Number(execution?.attempt);
  const mapped = firstTarget != null && Number.isFinite(liveAttempt) && liveAttempt >= firstTarget;
  const event = mapped && acceptedAttempt != null && acceptedAttempt >= firstTarget;
  return {
    taskKey: info.key || task.key || task.id,
    taskType: info.taskType ?? "missing",
    codingModel: info.model ?? "missing",
    firstPromptFile: prompt.fileName,
    historyListingSha: prompt.listingSha,
    promptCount: prompt.promptCount,
    promptClass: prompt.concreteFindingCount > 0 ? "finding-first" : "generic",
    concreteFindingCount: prompt.concreteFindingCount,
    directiveWordCount: prompt.directiveWordCount,
    liveAttempt: Number.isFinite(liveAttempt) ? liveAttempt : null,
    previousAttemptsRetained: execution?.previousAttempts?.length ?? null,
    firstReissueTargetAttempt: firstTarget,
    acceptedAttempt: event ? acceptedAttempt : null,
    mapped,
    event,
    duration: mapped ? (event ? acceptedAttempt : liveAttempt) - firstTarget + 1 : null,
  };
}

function percentile(values, probability) {
  if (values.length === 0) return null;
  const sorted = [...values].sort((left, right) => left - right);
  const position = (sorted.length - 1) * probability;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  return lower === upper
    ? sorted[lower]
    : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

function summarize(values) {
  if (values.length === 0) return { n: 0, median: null, mean: null };
  return {
    n: values.length,
    median: percentile(values, 0.5),
    mean: values.reduce((sum, value) => sum + value, 0) / values.length,
  };
}

function survivalAt(rows, at) {
  let survival = 1;
  for (let attempt = 1; attempt <= at; attempt++) {
    const atRisk = rows.filter(row => row.duration >= attempt).length;
    const events = rows.filter(row => row.event && row.duration === attempt).length;
    if (atRisk > 0) survival *= 1 - events / atRisk;
  }
  return survival;
}

function restrictedMean(rows, at) {
  let area = 0;
  for (let attempt = 1; attempt <= at; attempt++) area += survivalAt(rows, attempt - 1);
  return area;
}

function groupSummary(rows) {
  const accepted = rows.filter(row => row.event);
  return {
    tasks: rows.length,
    accepted: accepted.length,
    censored: rows.length - accepted.length,
    acceptanceProbabilityByHorizon: rows.length ? 1 - survivalAt(rows, horizon) : null,
    restrictedMeanAttemptsToHorizon: rows.length ? restrictedMean(rows, horizon) : null,
    acceptedOnlyAttempts: summarize(accepted.map(row => row.duration)),
    riskSetAtHorizon: rows.filter(row => row.duration >= horizon).length,
  };
}

function randomGenerator(seed = 0x2380) {
  let state = seed >>> 0;
  return () => {
    state = (1664525 * state + 1013904223) >>> 0;
    return state / 0x100000000;
  };
}

function sample(rows, random) {
  return Array.from({ length: rows.length }, () => rows[Math.floor(random() * rows.length)]);
}

function bootstrapDifference(generic, findings, estimator) {
  if (!generic.length || !findings.length || !bootstrapSamples) return null;
  const random = randomGenerator();
  const estimates = [];
  for (let index = 0; index < bootstrapSamples; index++) {
    estimates.push(estimator(sample(findings, random)) - estimator(sample(generic, random)));
  }
  return {
    lower: percentile(estimates, 0.025),
    upper: percentile(estimates, 0.975),
    samples: bootstrapSamples,
  };
}

function countBy(rows, key) {
  const counts = {};
  for (const row of rows) counts[row[key]] = (counts[row[key]] ?? 0) + 1;
  return Object.fromEntries(Object.entries(counts).sort());
}

function round(value) {
  if (Array.isArray(value)) return value.map(round);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, round(child)]));
  }
  return typeof value === "number" && Number.isFinite(value) ? Number(value.toFixed(4)) : value;
}

async function selfTest() {
  const features = promptFeatures(`
## Steering prompt (verbatim)

Please address these findings:
- backend/Thing.cs accepts a stale write instead of rejecting it.
- Run the normal checks when the change is complete.
\`\`\`
logs/generated.cs failed
\`\`\`
`);
  if (features.concreteFindingCount !== 1) throw new Error("finding classifier drifted");
  const rows = [{ duration: 1, event: true }, { duration: 5, event: false }];
  if (Math.abs((1 - survivalAt(rows, 5)) - 0.5) > 1e-12) throw new Error("survival calculation drifted");
  process.stdout.write("self-test passed\n");
}

if (process.argv.includes("--self-test")) {
  await selfTest();
  process.exit(0);
}

const generatedAt = new Date().toISOString();
const listed = await listTasks();
const promptScans = await mapPool(listed.tasks, async task => ({ task, prompt: await firstPrompt(task) }));
const historyBearing = promptScans.filter(scan => scan.prompt?.atMs != null);
const records = await mapPool(historyBearing, scan => analyzeTask(scan.task, scan.prompt));
const mapped = records.filter(record => record.mapped);
const generic = mapped.filter(record => record.promptClass === "generic");
const findings = mapped.filter(record => record.promptClass === "finding-first");
const sourceFingerprint = crypto.createHash("sha256")
  .update(JSON.stringify(records.map(record => [
    record.taskKey,
    record.historyListingSha,
    record.firstPromptFile,
    record.concreteFindingCount,
    record.firstReissueTargetAttempt,
    record.acceptedAttempt,
    record.liveAttempt,
  ])))
  .digest("hex");

const snapshot = round({
  schemaVersion: 1,
  generatedAt,
  sourceTask: "AGT-2380",
  source: {
    endpoint: `http://${host}:${port}`,
    project,
    projectName,
    selection: "All indexed active and archived Agent Studio tasks with a recoverable committed first reissue prompt",
    sourceFingerprintSha256: sourceFingerprint,
    modelScoringUsed: false,
    requestConcurrency: concurrency,
    requestTimeoutMs,
    requestRetries,
  },
  definitions: {
    findingFirst: "The first reissue directive has at least one Markdown list item of five or more words containing a concrete path/file or an explicit deficiency term",
    generic: "No such concrete finding item occurs in the first reissue directive",
    duration: "Explicit pipeline attempts from the first retained attempt starting after the first reissue prompt through acceptance or last observation, inclusive",
    acceptance: "First later code-review council accept or orchestrator_verdict_accepted event; this is model-judged, not deterministic ground truth",
    primaryHorizonAttempts: horizon,
    speedEffect: "Finding-first minus generic restricted mean attempts unresolved through the horizon; negative favors finding-first",
    reliabilityEffect: "Finding-first minus generic Kaplan-Meier acceptance probability by the horizon; positive favors finding-first",
  },
  coverage: {
    indexedTasks: listed.tasks.length,
    activeTasks: listed.active,
    archivedTasks: listed.archived,
    tasksWithFirstReissuePrompt: records.length,
    tasksWithMappedFirstReissueAttempt: mapped.length,
    excludedBecauseFirstReissuePredatesRetainedAttemptWindowOrPipelineMissing: records.length - mapped.length,
    retriedRequestCount,
    exhaustedRequestCount,
    previousAttemptsRetentionCaveat: "The on-disk previousAttempts list is capped. No attempt is inferred from list length or prompt count.",
  },
  groups: {
    generic: groupSummary(generic),
    findingFirst: groupSummary(findings),
  },
  effectsFindingFirstMinusGeneric: {
    restrictedMeanAttempts: findings.length && generic.length
      ? restrictedMean(findings, horizon) - restrictedMean(generic, horizon)
      : null,
    restrictedMeanAttemptsBootstrap95: bootstrapDifference(
      generic, findings, rows => restrictedMean(rows, horizon)),
    acceptanceProbability: findings.length && generic.length
      ? (1 - survivalAt(findings, horizon)) - (1 - survivalAt(generic, horizon))
      : null,
    acceptanceProbabilityBootstrap95: bootstrapDifference(
      generic, findings, rows => 1 - survivalAt(rows, horizon)),
  },
  balanceWarnings: {
    genericTaskTypes: countBy(generic, "taskType"),
    findingFirstTaskTypes: countBy(findings, "taskType"),
    genericCodingModels: countBy(generic, "codingModel"),
    findingFirstCodingModels: countBy(findings, "codingModel"),
  },
  evidenceClasses: {
    hard: "Prompt text features, explicit attempt numbers, timestamps, and recorded event counts",
    modelJudged: "Council and orchestrator acceptance",
    confounded: "All between-group effects because prompt style was not randomized and task difficulty, route, cause, and pipeline varied",
  },
  recommendationRule: "Do not change the production default from this observational snapshot. Keep finding-first structure in the randomized finding-first-v1 treatment and promote only if its predeclared gate passes.",
  records,
});

const serialized = `${JSON.stringify(snapshot, null, 2)}\n`;
if (outputFile) fs.writeFileSync(outputFile, serialized);
else process.stdout.write(serialized);
