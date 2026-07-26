#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const EXPERIMENT_ID = 'finding-first-v1';
export const ARMS = ['control', 'treatment'];
export const DEFAULT_BOOTSTRAPS = 2000;

function readJsonLines(file) {
  if (!fs.existsSync(file)) return [];
  return fs.readFileSync(file, 'utf8')
    .split(/\r?\n/)
    .filter(Boolean)
    .flatMap(line => {
      try { return [JSON.parse(line)]; } catch { return []; }
    });
}

function walkExperimentLogs(root) {
  if (!root || !fs.existsSync(root)) return [];
  const found = [];
  const pending = [path.resolve(root)];
  while (pending.length > 0) {
    const directory = pending.pop();
    let entries;
    try { entries = fs.readdirSync(directory, { withFileTypes: true }); } catch { continue; }
    for (const entry of entries) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) pending.push(full);
      else if (entry.isFile() && entry.name === 'reissue-prompt-experiment.jsonl') found.push(full);
    }
  }
  return found.sort();
}

function flattenPipeline(record) {
  if (!record || typeof record !== 'object') return [];
  return [record, ...(Array.isArray(record.previousAttempts) ? record.previousAttempts : [])]
    .filter(item => Number.isFinite(Number(item.attempt)))
    .sort((a, b) => Number(a.attempt) - Number(b.attempt));
}

function firstEndpointAttempt(attempts, predicate, firstAttempt) {
  const match = attempts.find(attempt =>
    Number(attempt.attempt) >= firstAttempt
    && (Array.isArray(attempt.steps) ? attempt.steps : []).some(predicate));
  return match ? Number(match.attempt) : null;
}

function recordFromTaskLog(logFile) {
  const assignments = readJsonLines(logFile)
    .filter(row => row.experimentId === EXPERIMENT_ID)
    .sort((a, b) => Number(a.attempt) - Number(b.attempt) || String(a.assignedAt).localeCompare(String(b.assignedAt)));
  if (assignments.length === 0) return null;

  const first = assignments[0];
  const firstAttempt = Number(first.attempt);
  const jobFolder = path.dirname(path.dirname(logFile));
  const pipelineFile = path.join(jobFolder, 'pipeline-execution.json');
  let attempts = [];
  try { attempts = flattenPipeline(JSON.parse(fs.readFileSync(pipelineFile, 'utf8'))); } catch {}

  const acceptedAttempt = firstEndpointAttempt(
    attempts,
    step => step.stepId === 'post-orchestrator-decision'
      && /^accept(?:$|-)/i.test(String(step.verdict ?? '')),
    firstAttempt);
  const firstGradeAAttempt = firstEndpointAttempt(
    attempts,
    step => step.stepId === 'post-code-review-grade'
      && String(step.verdict ?? '').toUpperCase() === 'A',
    firstAttempt);
  const observedAttempts = [
    firstAttempt,
    ...assignments.map(row => Number(row.attempt)),
    ...attempts.map(row => Number(row.attempt)),
  ].filter(Number.isFinite);
  const lastObservedAttempt = Math.max(...observedAttempts);
  const laterAssignments = assignments.filter(row => Number(row.attempt) > firstAttempt);

  return {
    taskId: first.project && first.jobId
      ? `${first.project}/${first.jobId}`
      : first.jobId || path.basename(jobFolder),
    arm: first.arm,
    templateVersion: first.templateVersion,
    assignmentHash: first.assignmentHash,
    promptFamily: first.promptFamily || 'unknown',
    cause: first.cause || 'unknown',
    firstReissueAttempt: firstAttempt,
    acceptedAttempt,
    firstGradeAAttempt,
    lastObservedAttempt,
    deterministicGateRegressed: laterAssignments.some(row => row.promptFamily === 'deterministic-gate'),
    codingModel: first.codingModel ?? null,
    thinkingLevel: first.thinkingLevel ?? null,
    assignmentDrift: assignments.some(row =>
      row.arm !== first.arm
      || row.templateVersion !== first.templateVersion
      || row.assignmentHash !== first.assignmentHash),
    routeDrift: assignments.some(row =>
      (row.codingModel ?? null) !== (first.codingModel ?? null)
      || (row.thinkingLevel ?? null) !== (first.thinkingLevel ?? null)),
  };
}

export function loadWorkspaceRecords(root) {
  return walkExperimentLogs(root).map(recordFromTaskLog).filter(Boolean);
}

export function loadInputRecords(inputFile) {
  const text = fs.readFileSync(inputFile, 'utf8').trim();
  if (!text) return [];
  if (text.startsWith('[')) return JSON.parse(text);
  const parsed = JSON.parse(text);
  if (Array.isArray(parsed.records)) return parsed.records;
  throw new Error('Input must be a JSON array or an object with a records array.');
}

function normalizeRecord(record) {
  const first = Number(record.firstReissueAttempt);
  const last = Math.max(first, Number(record.lastObservedAttempt));
  const accepted = record.acceptedAttempt == null ? null : Number(record.acceptedAttempt);
  const gradeA = record.firstGradeAAttempt == null ? null : Number(record.firstGradeAAttempt);
  return {
    ...record,
    arm: String(record.arm ?? '').toLowerCase(),
    promptFamily: String(record.promptFamily ?? 'unknown'),
    cause: String(record.cause ?? 'unknown'),
    firstReissueAttempt: first,
    lastObservedAttempt: last,
    acceptedAttempt: Number.isFinite(accepted) && accepted >= first ? accepted : null,
    firstGradeAAttempt: Number.isFinite(gradeA) && gradeA >= first ? gradeA : null,
    deterministicGateRegressed: record.deterministicGateRegressed === true,
    assignmentDrift: record.assignmentDrift === true,
    routeDrift: record.routeDrift === true,
  };
}

function endpointRows(records, field) {
  return records.map(record => {
    const endpoint = record[field];
    return {
      event: endpoint != null,
      duration: endpoint != null
        ? endpoint - record.firstReissueAttempt + 1
        : record.lastObservedAttempt - record.firstReissueAttempt + 1,
    };
  });
}

export function restrictedMeanAttempts(rows, tau) {
  if (rows.length === 0 || !Number.isFinite(tau) || tau < 1) return null;
  let survival = 1;
  let area = 0;
  for (let attempt = 1; attempt <= tau; attempt++) {
    area += survival;
    const atRisk = rows.filter(row => row.duration >= attempt).length;
    const events = rows.filter(row => row.event && row.duration === attempt).length;
    if (atRisk > 0) survival *= 1 - events / atRisk;
  }
  return area;
}

function mulberry32(seed) {
  return function random() {
    let value = seed += 0x6D2B79F5;
    value = Math.imul(value ^ value >>> 15, value | 1);
    value ^= value + Math.imul(value ^ value >>> 7, value | 61);
    return ((value ^ value >>> 14) >>> 0) / 4294967296;
  };
}

function sampleWithReplacement(values, random) {
  return Array.from({ length: values.length }, () => values[Math.floor(random() * values.length)]);
}

function percentile(sorted, probability) {
  if (sorted.length === 0) return null;
  const index = (sorted.length - 1) * probability;
  const lower = Math.floor(index);
  const upper = Math.ceil(index);
  if (lower === upper) return sorted[lower];
  return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
}

function bootstrapDifference(control, treatment, estimator, iterations, seed) {
  if (control.length === 0 || treatment.length === 0 || iterations < 1) return null;
  const random = mulberry32(seed);
  const values = [];
  for (let index = 0; index < iterations; index++) {
    values.push(estimator(
      sampleWithReplacement(control, random),
      sampleWithReplacement(treatment, random)));
  }
  values.sort((a, b) => a - b);
  return { lower: percentile(values, 0.025), upper: percentile(values, 0.975) };
}

function armSummary(records, endpointField) {
  return Object.fromEntries(ARMS.map(arm => {
    const armRecords = records.filter(record => record.arm === arm);
    const events = armRecords.filter(record => record[endpointField] != null);
    return [arm, {
      assigned: armRecords.length,
      observedEndpoint: events.length,
      rightCensored: armRecords.length - events.length,
      observedEndpointMeanAttempts: events.length === 0
        ? null
        : events.reduce((sum, record) =>
          sum + record[endpointField] - record.firstReissueAttempt + 1, 0) / events.length,
    }];
  }));
}

function endpointEffect(records, endpointField, bootstraps, seed) {
  const byArm = Object.fromEntries(ARMS.map(arm => [
    arm,
    endpointRows(records.filter(record => record.arm === arm), endpointField),
  ]));
  const maxima = ARMS.map(arm => Math.max(0, ...byArm[arm].map(row => row.duration)));
  const tau = Math.min(...maxima);
  if (tau < 1 || byArm.control.length === 0 || byArm.treatment.length === 0) {
    return {
      estimable: false,
      horizonAttempts: null,
      controlRestrictedMeanAttempts: null,
      treatmentRestrictedMeanAttempts: null,
      treatmentMinusControl: null,
      bootstrap95Ci: null,
    };
  }
  const estimator = (control, treatment) =>
    restrictedMeanAttempts(treatment, tau) - restrictedMeanAttempts(control, tau);
  return {
    estimable: true,
    horizonAttempts: tau,
    controlRestrictedMeanAttempts: restrictedMeanAttempts(byArm.control, tau),
    treatmentRestrictedMeanAttempts: restrictedMeanAttempts(byArm.treatment, tau),
    treatmentMinusControl: estimator(byArm.control, byArm.treatment),
    bootstrap95Ci: bootstrapDifference(
      byArm.control,
      byArm.treatment,
      estimator,
      bootstraps,
      seed),
  };
}

function deterministicGateEffect(records, bootstraps, seed) {
  const values = Object.fromEntries(ARMS.map(arm => [
    arm,
    records.filter(record => record.arm === arm).map(record => record.deterministicGateRegressed ? 1 : 0),
  ]));
  const rate = rows => rows.length === 0 ? null : rows.reduce((sum, value) => sum + value, 0) / rows.length;
  if (values.control.length === 0 || values.treatment.length === 0) {
    return {
      estimable: false,
      controlRate: rate(values.control),
      treatmentRate: rate(values.treatment),
      treatmentMinusControl: null,
      bootstrap95Ci: null,
    };
  }
  const estimator = (control, treatment) => rate(treatment) - rate(control);
  return {
    estimable: true,
    controlRate: rate(values.control),
    treatmentRate: rate(values.treatment),
    treatmentMinusControl: estimator(values.control, values.treatment),
    bootstrap95Ci: bootstrapDifference(
      values.control,
      values.treatment,
      estimator,
      bootstraps,
      seed),
  };
}

function strata(records, key, bootstraps) {
  return [...new Set(records.map(record => record[key]))].sort().map((value, index) => {
    const subset = records.filter(record => record[key] === value);
    return {
      value,
      counts: Object.fromEntries(ARMS.map(arm => [
        arm,
        subset.filter(record => record.arm === arm).length,
      ])),
      primaryEffect: endpointEffect(subset, 'acceptedAttempt', bootstraps, 2200 + index),
    };
  });
}

function routeCounts(records) {
  const result = {};
  for (const record of records) {
    const key = `${record.codingModel ?? 'unknown'} / ${record.thinkingLevel ?? 'unknown'}`;
    result[key] ??= { control: 0, treatment: 0 };
    if (ARMS.includes(record.arm)) result[key][record.arm]++;
  }
  return result;
}

export function analyzeExperiment(rawRecords, options = {}) {
  const bootstraps = options.bootstraps ?? DEFAULT_BOOTSTRAPS;
  const records = rawRecords.map(normalizeRecord)
    .filter(record =>
      ARMS.includes(record.arm)
      && Number.isFinite(record.firstReissueAttempt)
      && Number.isFinite(record.lastObservedAttempt));
  const armCounts = Object.fromEntries(ARMS.map(arm => [
    arm,
    records.filter(record => record.arm === arm).length,
  ]));
  const primary = endpointEffect(records, 'acceptedAttempt', bootstraps, 2322);
  const sensitivity = endpointEffect(records, 'firstGradeAAttempt', bootstraps, 2323);
  const deterministicGate = deterministicGateEffect(records, bootstraps, 2324);
  const enoughPerArm = ARMS.every(arm => armCounts[arm] >= 30);
  const promotionEligible = enoughPerArm
    && records.every(record => !record.assignmentDrift)
    && primary.estimable
    && primary.treatmentMinusControl <= -0.5
    && primary.bootstrap95Ci?.upper < 0
    && deterministicGate.estimable
    && deterministicGate.bootstrap95Ci?.upper <= 0.05;

  return {
    schemaVersion: 1,
    experimentId: EXPERIMENT_ID,
    generatedAt: options.generatedAt ?? new Date().toISOString(),
    evidenceLabels: {
      assignmentAndAttemptEvents: 'hard',
      acceptanceEndpoint: 'model-judged',
      gradeAEndpoint: 'model-judged',
      armComparison: 'experimental',
    },
    predeclaredAnalysis: {
      assignmentUnit: 'task from first eligible mapped reissue',
      primaryEndpoint: 'attempts from first reissue to first model-judged acceptance',
      sensitivityEndpoint: 'attempt at first model-judged Grade A',
      censoring: 'right-censor at the last observed pipeline attempt',
      effect: 'treatment minus control restricted mean attempts at the common observed horizon; negative is beneficial',
      uncertainty: `${bootstraps} task-level bootstrap resamples, percentile 95% interval`,
      meaningfulBenefitThresholdAttempts: -0.5,
      minimumTasksPerArmForPromotion: 30,
      assignmentDriftTasksAllowed: 0,
      deterministicGateRiskDifferenceUpperBound: 0.05,
    },
    eligibleTasks: records.length,
    armCounts,
    primaryEndpoint: {
      ...primary,
      arms: armSummary(records, 'acceptedAttempt'),
    },
    gradeASensitivity: {
      ...sensitivity,
      arms: armSummary(records, 'firstGradeAAttempt'),
    },
    deterministicGateRegression: deterministicGate,
    promptFamilyStrata: strata(records, 'promptFamily', bootstraps),
    causeStrata: strata(records, 'cause', bootstraps),
    codingRouteCounts: routeCounts(records),
    assignmentDriftTasks: records.filter(record => record.assignmentDrift).length,
    routeDriftTasks: records.filter(record => record.routeDrift).length,
    promotionDecision: {
      eligible: promotionEligible,
      recommendation: promotionEligible
        ? 'Treatment meets the predeclared promotion gate.'
        : 'Keep the production default unchanged.',
    },
  };
}

function fmt(value, digits = 2) {
  return value == null ? 'not estimable' : Number(value).toFixed(digits);
}

function pct(value) {
  return value == null ? 'not estimable' : `${(100 * Number(value)).toFixed(1)}%`;
}

function effectText(effect) {
  if (!effect.estimable) return 'not estimable';
  return `${fmt(effect.treatmentMinusControl)} attempts (95% CI ${fmt(effect.bootstrap95Ci?.lower)} to ${fmt(effect.bootstrap95Ci?.upper)})`;
}

export function renderMarkdown(report) {
  const lines = [
    '# Finding-first reissue prompt experiment analysis',
    '',
    `Generated: ${report.generatedAt}`,
    '',
    'This is an experimental arm comparison. Assignment and attempt events are hard telemetry. Grade A and acceptance are model-judged evidence, not deterministic truth.',
    '',
    '## Arm counts and censoring',
    '',
    '| Arm | Assigned | Accepted | Right-censored | Observed accepted mean |',
    '|---|---:|---:|---:|---:|',
    ...ARMS.map(arm => {
      const row = report.primaryEndpoint.arms[arm];
      return `| ${arm} | ${row.assigned} | ${row.observedEndpoint} | ${row.rightCensored} | ${fmt(row.observedEndpointMeanAttempts)} |`;
    }),
    '',
    `Primary effect: ${effectText(report.primaryEndpoint)}. Negative favors treatment. The right-censor-aware estimate is restricted mean attempts at the common horizon (${report.primaryEndpoint.horizonAttempts ?? 'not estimable'} attempts).`,
    '',
    `Grade A sensitivity effect: ${effectText(report.gradeASensitivity)}.`,
    '',
    `Assignment consistency: ${report.assignmentDriftTasks} task(s) with arm, template-version, or assignment-hash drift. Coding-route drift: ${report.routeDriftTasks} task(s).`,
    '',
    '## Deterministic-gate regression',
    '',
    `Control rate: ${pct(report.deterministicGateRegression.controlRate)}. Treatment rate: ${pct(report.deterministicGateRegression.treatmentRate)}. Risk difference: ${report.deterministicGateRegression.estimable ? `${pct(report.deterministicGateRegression.treatmentMinusControl)} (95% CI ${pct(report.deterministicGateRegression.bootstrap95Ci?.lower)} to ${pct(report.deterministicGateRegression.bootstrap95Ci?.upper)})` : 'not estimable'}.`,
    '',
    '## Prompt-family strata',
    '',
    '| Prompt family | Control | Treatment | Primary effect |',
    '|---|---:|---:|---|',
    ...(report.promptFamilyStrata.length === 0
      ? ['| No assignments observed | 0 | 0 | not estimable |']
      : report.promptFamilyStrata.map(row =>
        `| ${row.value} | ${row.counts.control} | ${row.counts.treatment} | ${effectText(row.primaryEffect)} |`)),
    '',
    '## Cause strata',
    '',
    '| Cause | Control | Treatment | Primary effect |',
    '|---|---:|---:|---|',
    ...(report.causeStrata.length === 0
      ? ['| No assignments observed | 0 | 0 | not estimable |']
      : report.causeStrata.map(row =>
        `| ${row.value} | ${row.counts.control} | ${row.counts.treatment} | ${effectText(row.primaryEffect)} |`)),
    '',
    '## Promotion decision',
    '',
    report.promotionDecision.recommendation,
    '',
    'The production default may change only with at least 30 tasks per arm, zero assignment drift, a treatment effect of at most -0.5 restricted mean attempts, a bootstrap interval wholly below zero, and no deterministic-gate risk-difference upper bound above 5 percentage points.',
    '',
  ];
  return lines.join('\n');
}

function parseArgs(argv) {
  const options = { root: 'agent-taskboard-workspace/projects', bootstraps: DEFAULT_BOOTSTRAPS };
  for (let index = 0; index < argv.length; index++) {
    const value = argv[index];
    if (value === '--root') options.root = argv[++index];
    else if (value === '--input') options.input = argv[++index];
    else if (value === '--json') options.json = argv[++index];
    else if (value === '--markdown') options.markdown = argv[++index];
    else if (value === '--bootstrap') options.bootstraps = Number(argv[++index]);
    else if (value === '--generated-at') options.generatedAt = argv[++index];
    else throw new Error(`Unknown argument: ${value}`);
  }
  return options;
}

function writeFile(target, content) {
  fs.mkdirSync(path.dirname(path.resolve(target)), { recursive: true });
  fs.writeFileSync(target, content);
}

export function main(argv = process.argv.slice(2)) {
  const options = parseArgs(argv);
  const records = options.input ? loadInputRecords(options.input) : loadWorkspaceRecords(options.root);
  const report = analyzeExperiment(records, options);
  const json = JSON.stringify(report, null, 2) + '\n';
  const markdown = renderMarkdown(report);
  if (options.json) writeFile(options.json, json);
  if (options.markdown) writeFile(options.markdown, markdown);
  if (!options.json && !options.markdown) process.stdout.write(json);
  return report;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { main(); } catch (error) {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
  }
}
