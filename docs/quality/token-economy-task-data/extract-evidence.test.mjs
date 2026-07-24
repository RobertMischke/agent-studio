import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import http from "node:http";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const extractorPath = fileURLToPath(new URL("./extract-evidence.mjs", import.meta.url));

test("extractor separates hard evidence, reviewer signals, and coverage", async t => {
  const requests = [];
  const responses = new Map([
    ["/api/tasks/grouped", {
      progress: [{
        id: "benchmark-card",
        key: "AGT-1",
        projectName: "Agent Studio",
        lastActivity: "2026-07-24T10:00:00Z",
        model: "coding-model-a",
        tags: ["code-review:grade-b"],
        tokenSummary: null,
      }],
      archive: [],
    }],
    ["/api/tasks/AGT-1/pipeline?project=AGT", {
      execution: {
        attempt: 2,
        steps: [
          {
            stepId: "post-build-test-gate",
            status: "passed",
            durationMs: 321,
          },
          {
            stepId: "aspect-requirement-fit",
            status: "passed",
            verdict: "pass",
            inputTokens: 100,
            outputTokens: 20,
          },
          {
            stepId: "post-code-review-grade",
            status: "passed",
            verdict: "B",
          },
        ],
      },
    }],
    ["/api/tasks/AGT-1/runs?project=AGT", {
      runCount: 1,
      runs: [{ status: "completed", durationSeconds: 12 }],
    }],
    ["/api/tasks/AGT-1/timeline?project=AGT", [
      { kind: "agent_run_finished" },
      { kind: "orchestrator_verdict_accepted" },
    ]],
    ["/api/tasks/AGT-1/code-review/list?project=AGT", {
      entries: [{ grade: "B", model: "reviewer-model-a" }],
    }],
  ]);

  const server = http.createServer((request, response) => {
    requests.push(request.url);
    const payload = responses.get(request.url);
    if (payload == null) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify(payload));
  });

  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  t.after(() => server.close());

  const address = server.address();
  assert.notEqual(address, null);
  assert.equal(typeof address, "object");

  const { stdout, stderr } = await execFileAsync(process.execPath, [extractorPath], {
    env: {
      ...process.env,
      TASKBOARD_HOST: "127.0.0.1",
      TASKBOARD_PORT: String(address.port),
      TASKBOARD_PROJECT: "AGT",
      TASKBOARD_SAMPLE_LIMIT: "1",
      TASKBOARD_CONCURRENCY: "1",
    },
  });

  assert.equal(stderr, "");
  const snapshot = JSON.parse(stdout);

  assert.deepEqual(requests.sort(), [...responses.keys()].sort());
  assert.equal(snapshot.cohort.selected, 1);
  assert.deepEqual(snapshot.cohort.codingModelCounts, { "coding-model-a": 1 });
  assert.deepEqual(snapshot.coverage.endpointFailures, {
    pipeline: 0,
    runs: 0,
    timeline: 0,
    codeReviewList: 0,
  });
  assert.equal(snapshot.coverage.runRecordsWithDuration, 1);
  assert.equal(snapshot.coverage.aspectStepsWithTokens, 1);
  assert.equal(snapshot.coverage.codeReviewEntries, 1);
  assert.deepEqual(snapshot.hardEvidence.buildGateStatusCounts, { passed: 1 });
  assert.deepEqual(snapshot.hardEvidence.timelineKindCounts, {
    agent_run_finished: 1,
    orchestrator_verdict_accepted: 1,
  });
  assert.deepEqual(snapshot.modelJudgedEvidence.aspectVerdictCounts, { pass: 1 });
  assert.match(snapshot.validityWarning.join("\n"), /not ground truth/);
});
