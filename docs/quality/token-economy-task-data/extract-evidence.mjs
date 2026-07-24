#!/usr/bin/env node

import http from "node:http";

const host = process.env.TASKBOARD_HOST ?? "127.0.0.1";
const port = Number(process.env.TASKBOARD_PORT ?? 5031);
const project = process.env.TASKBOARD_PROJECT ?? "AGT";
const sampleLimit = Number(process.env.TASKBOARD_SAMPLE_LIMIT ?? 60);
const concurrency = Math.max(1, Number(process.env.TASKBOARD_CONCURRENCY ?? 8));

function getJson(path) {
  return new Promise((resolve, reject) => {
    const request = http.request({
      hostname: host,
      port,
      path,
      method: "GET",
      headers: { "X-Client-Id": "local-default" },
    }, response => {
      let body = "";
      response.on("data", chunk => body += chunk);
      response.on("end", () => {
        if (response.statusCode !== 200) {
          resolve(null);
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch (error) {
          reject(new Error(`Invalid JSON from ${path}: ${error.message}`));
        }
      });
    });
    request.on("error", reject);
    request.end();
  });
}

function countBy(items, keyOf) {
  const result = {};
  for (const item of items) {
    const key = keyOf(item);
    if (key == null || key === "") continue;
    result[key] = (result[key] ?? 0) + 1;
  }
  return result;
}

function hasTokens(step) {
  return [
    step?.inputTokens,
    step?.outputTokens,
    step?.cacheReadTokens,
    step?.cacheCreationTokens,
  ].some(value => Number(value ?? 0) > 0);
}

const grouped = await getJson("/api/tasks/grouped");
if (!grouped) {
  throw new Error("Task Server did not return /api/tasks/grouped");
}

const candidates = [];
for (const [lane, tasks] of Object.entries(grouped)) {
  if (!Array.isArray(tasks) || lane === "review" || lane === "archive") continue;
  for (const task of tasks) {
    const gradeTag = task.tags?.find(tag => tag.startsWith("code-review:grade-"));
    if (task.projectName === "Agent Studio" && gradeTag) {
      candidates.push({ ...task, lane, gradeTag });
    }
  }
}

candidates.sort((left, right) =>
  new Date(right.lastActivity).getTime() - new Date(left.lastActivity).getTime());
const sample = candidates.slice(0, sampleLimit);
const enriched = new Array(sample.length);
let cursor = 0;

async function worker() {
  while (cursor < sample.length) {
    const index = cursor++;
    const task = sample[index];
    const key = encodeURIComponent(task.key || task.id);
    const suffix = `?project=${encodeURIComponent(project)}`;
    const [pipeline, runs, timeline, reviews] = await Promise.all([
      getJson(`/api/tasks/${key}/pipeline${suffix}`),
      getJson(`/api/tasks/${key}/runs${suffix}`),
      getJson(`/api/tasks/${key}/timeline${suffix}`),
      getJson(`/api/tasks/${key}/code-review/list${suffix}`),
    ]);
    enriched[index] = { task, pipeline, runs, timeline, reviews };
  }
}

await Promise.all(Array.from({ length: concurrency }, worker));

const executions = enriched.map(item => item.pipeline?.execution).filter(Boolean);
const steps = executions.flatMap(execution => execution.steps ?? []);
const runRecords = enriched.flatMap(item => item.runs?.runs ?? []);
const timelineEvents = enriched.flatMap(item => item.timeline ?? []);
const reviewEntries = enriched.flatMap(item => item.reviews?.entries ?? []);
const gateSteps = steps.filter(step => step.stepId === "post-build-test-gate");
const aspectSteps = steps.filter(step => step.stepId?.startsWith("aspect-"));
const gradeSteps = steps.filter(step => step.stepId === "post-code-review-grade");

const snapshot = {
  schemaVersion: 1,
  generatedAt: new Date().toISOString(),
  source: {
    endpoint: `http://${host}:${port}`,
    project,
    selection: "Most recently active non-archived Agent Studio tasks carrying a code-review grade tag",
    sampleLimit,
    pipelineScope: "Latest pipeline attempt per selected card",
    purpose: "Technical extraction and coverage proof, not a representative performance cohort",
  },
  cohort: {
    selected: sample.length,
    laneCounts: countBy(sample, task => task.lane),
    codingModelCounts: countBy(sample, task => task.model ?? "missing"),
    gradeTagCounts: countBy(sample, task => task.gradeTag),
  },
  coverage: {
    endpointFailures: {
      pipeline: enriched.filter(item => !item.pipeline).length,
      runs: enriched.filter(item => !item.runs).length,
      timeline: enriched.filter(item => !item.timeline).length,
      codeReviewList: enriched.filter(item => !item.reviews).length,
    },
    pipelineExecutions: executions.length,
    pipelineSteps: steps.length,
    runTimelineCards: enriched.filter(item => Number(item.runs?.runCount ?? 0) > 0).length,
    runRecords: runRecords.length,
    runRecordsWithDuration: runRecords.filter(run => run.durationSeconds != null).length,
    taskTokenSummaries: sample.filter(task => Number(task.tokenSummary?.totalTokens ?? 0) > 0).length,
    pipelineStepsWithTokens: steps.filter(hasTokens).length,
    aspectStepsWithTokens: aspectSteps.filter(hasTokens).length,
    codeReviewEntries: reviewEntries.length,
  },
  hardEvidence: {
    buildGateRecords: gateSteps.length,
    buildGateStatusCounts: countBy(gateSteps, step => step.status),
    buildGateRecordsWithDuration: gateSteps.filter(step => Number(step.durationMs ?? 0) > 0).length,
    pipelineAttemptCounts: countBy(executions, execution => String(execution.attempt)),
    timelineKindCounts: countBy(timelineEvents, event => event.kind),
    outcomeIssueCounts: countBy(sample, task => task.outcomeIssue?.kind),
  },
  modelJudgedEvidence: {
    gradeStepRecords: gradeSteps.length,
    gradeStepStatusCounts: countBy(gradeSteps, step => step.status),
    aspectStepRecords: aspectSteps.length,
    aspectVerdictCounts: countBy(aspectSteps, step => step.verdict),
  },
  validityWarning: [
    "Selection is conditioned on a grade tag and non-archive state.",
    "Coding-model mix is unbalanced.",
    "Card difficulty, prompt quality, environment, and pipeline configuration are uncontrolled.",
    "Grades and aspects are reviewer-model outputs, not ground truth.",
    "Missing token or duration fields are coverage gaps and must not be treated as zero usage or zero time.",
  ],
};

process.stdout.write(`${JSON.stringify(snapshot, null, 2)}\n`);
