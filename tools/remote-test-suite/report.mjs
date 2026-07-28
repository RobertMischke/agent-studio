#!/usr/bin/env node
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const phaseOrder = Object.freeze(['claim', 'run', 'gate', 'review', 'integration']);
const outcomes = new Set(['baseline', 'healed', 'recovered', 'lost', 'invalid']);
const statuses = new Set(['pass', 'fail', 'skipped']);
const rootFields = ['$schema', 'schemaVersion', 'suite', 'generatedAt', 'runs'];
const runFields = [
  'runId', 'taskKey', 'taskHref', 'attemptId', 'attemptHref', 'scenario',
  'outcome', 'accepted', 'wallMs', 'phases', 'tokens', 'baseSha', 'resultSha',
  'rawArtifactHref', 'components', 'assertions', 'incidents', 'links'
];

export function validateRunResult(value) {
  const errors = [];
  if (!record(value)) return { valid: false, errors: ['$: expected an object'] };
  unknown(value, rootFields, '$', errors);
  if (value.schemaVersion !== 1) errors.push('$.schemaVersion: expected 1');
  if (!record(value.suite)) {
    errors.push('$.suite: expected an object');
  } else {
    unknown(value.suite, ['name', 'sourceTaskKey', 'sourceTaskHref', 'chronicleHref'], '$.suite', errors);
    requiredText(value.suite.name, '$.suite.name', errors);
    taskKey(value.suite.sourceTaskKey, '$.suite.sourceTaskKey', errors);
    href(value.suite.sourceTaskHref, '$.suite.sourceTaskHref', errors);
    href(value.suite.chronicleHref, '$.suite.chronicleHref', errors);
  }
  if (!dateTime(value.generatedAt)) errors.push('$.generatedAt: expected an ISO date-time');
  if (!Array.isArray(value.runs)) {
    errors.push('$.runs: expected an array');
  } else {
    const runIds = new Set();
    value.runs.forEach((run, index) => validateRun(run, index, runIds, errors));
  }
  return { valid: errors.length === 0, errors };
}

function validateRun(run, index, runIds, errors) {
  const at = `$.runs[${index}]`;
  if (!record(run)) {
    errors.push(`${at}: expected an object`);
    return;
  }
  unknown(run, runFields, at, errors);
  if (!/^[A-Za-z0-9._-]{1,100}$/.test(run.runId ?? '')) errors.push(`${at}.runId: invalid run id`);
  else if (runIds.has(run.runId)) errors.push(`${at}.runId: duplicate run id`);
  else runIds.add(run.runId);
  taskKey(run.taskKey, `${at}.taskKey`, errors);
  href(run.taskHref, `${at}.taskHref`, errors);
  requiredText(run.attemptId, `${at}.attemptId`, errors);
  href(run.attemptHref, `${at}.attemptHref`, errors);
  if (!record(run.scenario)) {
    errors.push(`${at}.scenario: expected an object`);
  } else {
    unknown(run.scenario, ['id', 'manifestHref'], `${at}.scenario`, errors);
    if (!/^[a-z0-9][a-z0-9-]{1,63}$/.test(run.scenario.id ?? '')) errors.push(`${at}.scenario.id: invalid scenario id`);
    href(run.scenario.manifestHref, `${at}.scenario.manifestHref`, errors);
  }
  if (!outcomes.has(run.outcome)) errors.push(`${at}.outcome: unsupported outcome`);
  if (typeof run.accepted !== 'boolean') errors.push(`${at}.accepted: expected a boolean`);
  nonNegativeInteger(run.wallMs, `${at}.wallMs`, errors);
  validatePhases(run.phases, at, errors);
  validateTokens(run.tokens, at, errors);
  for (const field of ['baseSha', 'resultSha']) {
    if (!/^[0-9a-f]{40}$/.test(run[field] ?? '')) errors.push(`${at}.${field}: expected a full lowercase SHA`);
  }
  href(run.rawArtifactHref, `${at}.rawArtifactHref`, errors);
  validateComponents(run.components, at, errors);
  validateAssertions(run.assertions, at, errors);
  validateIncidents(run.incidents, at, errors);
  validateLinks(run.links, at, errors);
  const hasFailure = Array.isArray(run.assertions) && run.assertions.some(item => item?.status === 'fail');
  const phaseFailed = Array.isArray(run.phases) && run.phases.some(item => item?.status === 'fail');
  if (run.accepted === true && (hasFailure || phaseFailed)) errors.push(`${at}.accepted: cannot be true when an assertion or phase failed`);
  if (run.outcome === 'invalid' && run.accepted === true) errors.push(`${at}.outcome: invalid input cannot be accepted`);
  if (Array.isArray(run.phases) && Number.isInteger(run.wallMs)) {
    const accounted = run.phases.reduce((sum, phase) => sum + (phase?.queueMs ?? 0) + (phase?.executionMs ?? 0), 0);
    if (accounted > run.wallMs) errors.push(`${at}.wallMs: shorter than the sum of phase timings`);
  }
}

function validatePhases(phases, at, errors) {
  if (!Array.isArray(phases) || phases.length < 4 || phases.length > 5) {
    errors.push(`${at}.phases: expected Claim, Run, Gate, Review, and optional Integration`);
    return;
  }
  phases.forEach((phase, index) => {
    const phaseAt = `${at}.phases[${index}]`;
    if (!record(phase)) {
      errors.push(`${phaseAt}: expected an object`);
      return;
    }
    unknown(phase, ['name', 'status', 'queueMs', 'executionMs'], phaseAt, errors);
    if (phase.name !== phaseOrder[index]) errors.push(`${phaseAt}.name: expected ${phaseOrder[index]}`);
    if (!statuses.has(phase.status)) errors.push(`${phaseAt}.status: unsupported status`);
    nonNegativeInteger(phase.queueMs, `${phaseAt}.queueMs`, errors);
    nonNegativeInteger(phase.executionMs, `${phaseAt}.executionMs`, errors);
  });
}

function validateTokens(tokens, at, errors) {
  const tokenAt = `${at}.tokens`;
  if (!record(tokens)) {
    errors.push(`${tokenAt}: expected an object`);
    return;
  }
  if (tokens.available === false) {
    unknown(tokens, ['available', 'reason'], tokenAt, errors);
    requiredText(tokens.reason, `${tokenAt}.reason`, errors);
    return;
  }
  if (tokens.available !== true) {
    errors.push(`${tokenAt}.available: expected a boolean`);
    return;
  }
  unknown(tokens, ['available', 'total', 'input', 'output', 'phases'], tokenAt, errors);
  for (const field of ['total', 'input', 'output']) nonNegativeInteger(tokens[field], `${tokenAt}.${field}`, errors);
  if (Number.isInteger(tokens.total) && Number.isInteger(tokens.input) && Number.isInteger(tokens.output)
      && tokens.total !== tokens.input + tokens.output) {
    errors.push(`${tokenAt}.total: must equal input plus output`);
  }
  if (!record(tokens.phases)) {
    errors.push(`${tokenAt}.phases: expected an object`);
  } else {
    unknown(tokens.phases, phaseOrder, `${tokenAt}.phases`, errors);
    for (const [phase, count] of Object.entries(tokens.phases)) {
      nonNegativeInteger(count, `${tokenAt}.phases.${phase}`, errors);
    }
    const attributed = Object.values(tokens.phases).reduce((sum, count) => sum + (Number.isInteger(count) ? count : 0), 0);
    if (Number.isInteger(tokens.total) && attributed > tokens.total) {
      errors.push(`${tokenAt}.phases: attribution exceeds the run total`);
    }
  }
}

function validateComponents(components, at, errors) {
  if (!Array.isArray(components) || components.length === 0) {
    errors.push(`${at}.components: expected at least one component version`);
    return;
  }
  components.forEach((component, index) => {
    const componentAt = `${at}.components[${index}]`;
    if (!record(component)) return errors.push(`${componentAt}: expected an object`);
    unknown(component, ['name', 'version'], componentAt, errors);
    requiredText(component.name, `${componentAt}.name`, errors);
    requiredText(component.version, `${componentAt}.version`, errors);
  });
}

function validateAssertions(assertions, at, errors) {
  if (!Array.isArray(assertions) || assertions.length === 0) {
    errors.push(`${at}.assertions: expected at least one assertion`);
    return;
  }
  const ids = new Set();
  assertions.forEach((assertion, index) => {
    const assertionAt = `${at}.assertions[${index}]`;
    if (!record(assertion)) return errors.push(`${assertionAt}: expected an object`);
    unknown(assertion, ['id', 'label', 'status', 'detail', 'evidenceHref'], assertionAt, errors);
    if (!/^[a-z0-9][a-z0-9-]{1,63}$/.test(assertion.id ?? '')) errors.push(`${assertionAt}.id: invalid assertion id`);
    else if (ids.has(assertion.id)) errors.push(`${assertionAt}.id: duplicate assertion id`);
    else ids.add(assertion.id);
    requiredText(assertion.label, `${assertionAt}.label`, errors);
    if (!['pass', 'fail'].includes(assertion.status)) errors.push(`${assertionAt}.status: expected pass or fail`);
    requiredText(assertion.detail, `${assertionAt}.detail`, errors);
    href(assertion.evidenceHref, `${assertionAt}.evidenceHref`, errors);
  });
}

function validateIncidents(incidents, at, errors) {
  if (!Array.isArray(incidents)) {
    errors.push(`${at}.incidents: expected an array`);
    return;
  }
  incidents.forEach((incident, index) => {
    const incidentAt = `${at}.incidents[${index}]`;
    if (!record(incident)) return errors.push(`${incidentAt}: expected an object`);
    unknown(incident, ['class', 'label', 'chronicleAnchor', 'injected', 'recoveryOutcome'], incidentAt, errors);
    if (!/^[a-z0-9][a-z0-9-]{1,63}$/.test(incident.class ?? '')) errors.push(`${incidentAt}.class: invalid incident class`);
    requiredText(incident.label, `${incidentAt}.label`, errors);
    if (!/^incident-[a-z0-9-]+$/.test(incident.chronicleAnchor ?? '')) errors.push(`${incidentAt}.chronicleAnchor: invalid chronicle anchor`);
    if (typeof incident.injected !== 'boolean') errors.push(`${incidentAt}.injected: expected a boolean`);
    requiredText(incident.recoveryOutcome, `${incidentAt}.recoveryOutcome`, errors);
  });
}

function validateLinks(links, at, errors) {
  if (!Array.isArray(links)) {
    errors.push(`${at}.links: expected an array`);
    return;
  }
  links.forEach((link, index) => {
    const linkAt = `${at}.links[${index}]`;
    if (!record(link)) return errors.push(`${linkAt}: expected an object`);
    unknown(link, ['label', 'href'], linkAt, errors);
    requiredText(link.label, `${linkAt}.label`, errors);
    href(link.href, `${linkAt}.href`, errors);
  });
}

function unknown(value, allowed, at, errors) {
  if (!record(value)) return;
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) errors.push(`${at}.${key}: unsupported field`);
  }
}

function record(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function requiredText(value, at, errors) {
  if (typeof value !== 'string' || value.trim() === '') errors.push(`${at}: expected non-empty text`);
}

function taskKey(value, at, errors) {
  if (!/^[A-Z][A-Z0-9-]*-[0-9]+$/.test(value ?? '')) errors.push(`${at}: invalid task key`);
}

function nonNegativeInteger(value, at, errors) {
  if (!Number.isInteger(value) || value < 0) errors.push(`${at}: expected a non-negative integer`);
}

function dateTime(value) {
  return typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{3})?Z$/.test(value)
    && !Number.isNaN(Date.parse(value));
}

function href(value, at, errors) {
  if (typeof value !== 'string' || !/^(#|\.{1,2}\/|https?:\/\/)\S+$/.test(value)
      || /^(?:javascript|data):/i.test(value)) {
    errors.push(`${at}: expected a safe relative, anchor, or HTTP(S) link`);
  }
}

export function renderReport(report, { validationErrors = [] } = {}) {
  if (validationErrors.length > 0) return renderRejectedReport(validationErrors);
  const counts = countOutcomes(report.runs);
  const accepted = report.runs.filter(run => run.accepted).length;
  const totalWall = report.runs.reduce((sum, run) => sum + run.wallMs, 0);
  const phaseTotals = Object.fromEntries(phaseOrder.map(name => [name, { queueMs: 0, executionMs: 0, count: 0 }]));
  for (const run of report.runs) {
    for (const phase of run.phases) {
      phaseTotals[phase.name].queueMs += phase.queueMs;
      phaseTotals[phase.name].executionMs += phase.executionMs;
      phaseTotals[phase.name].count++;
    }
  }
  const tokenRuns = report.runs.filter(run => run.tokens.available).length;
  const totalTokens = report.runs.reduce((sum, run) => sum + (run.tokens.available ? run.tokens.total : 0), 0);
  const assertionLabels = [...new Map(report.runs.flatMap(run => run.assertions.map(item => [item.id, item.label]))).entries()];
  const matrixRows = report.runs.map(run => matrixRow(run, assertionLabels)).join('');
  const runCards = report.runs.map(run => runCard(run, report.suite.chronicleHref)).join('');
  const phaseSummary = phaseOrder
    .filter(name => phaseTotals[name].count > 0)
    .map(name => `<tr><th scope="row">${title(name)}</th><td>${duration(phaseTotals[name].queueMs)}</td><td>${duration(phaseTotals[name].executionMs)}</td><td>${phaseTotals[name].count}</td></tr>`)
    .join('');

  return documentShell(`
    <header class="page">
      <div>
        <p class="kicker">Infrastructure verification · Static report</p>
        <h1>${escapeHtml(report.suite.name)}</h1>
        <p class="lede">Remote execution acceptance, recovery evidence, phase timing, and telemetry coverage. No model or CLI comparisons are included.</p>
      </div>
      <button class="theme" type="button" data-theme-toggle aria-label="Switch color theme">Theme</button>
      <div class="meta">
        <span>Generated <strong>${escapeHtml(formatDate(report.generatedAt))}</strong></span>
        <span>Source <a id="source-agt-2200" href="${escapeAttribute(report.suite.sourceTaskHref)}">${escapeHtml(report.suite.sourceTaskKey)}</a></span>
        <span>Schema <a href="#contract">v${report.schemaVersion}</a></span>
      </div>
    </header>

    <main>
      <section aria-labelledby="summary-heading">
        <div class="section-heading">
          <div><p class="eyebrow">Acceptance</p><h2 id="summary-heading">Suite summary</h2></div>
          <p class="summary-line">${accepted} of ${report.runs.length} runs accepted</p>
        </div>
        <div class="metrics" aria-label="Outcome and telemetry totals">
          ${metric('Runs', report.runs.length, 'All validated run records')}
          ${metric('Healed', counts.healed, 'Self-healed infrastructure')}
          ${metric('Recovered', counts.recovered, 'Recovered after an incident')}
          ${metric('Lost', counts.lost, 'Evidence or execution lost', counts.lost ? 'bad' : '')}
          ${metric('Invalid', counts.invalid, 'Schema-incompatible records', counts.invalid ? 'bad' : '')}
          ${metric('Wall time', duration(totalWall), 'Across all visible runs')}
          ${metric('Telemetry coverage', `${tokenRuns}/${report.runs.length}`, `Tokens · phase timing ${report.runs.length}/${report.runs.length}${totalTokens ? ` · ${number(totalTokens)} tokens` : ''}`)}
        </div>
      </section>

      <section aria-labelledby="matrix-heading">
        <div class="section-heading">
          <div><p class="eyebrow">One click to assertion</p><h2 id="matrix-heading">Acceptance matrix</h2></div>
          <p class="quiet">Each status opens the exact assertion. Raw evidence is the next link.</p>
        </div>
        <div class="table-wrap" tabindex="0" role="region" aria-label="Scrollable acceptance matrix">
          <table class="matrix">
            <thead><tr><th scope="col">Run</th>${assertionLabels.map(([, label]) => `<th scope="col">${escapeHtml(label)}</th>`).join('')}</tr></thead>
            <tbody>${matrixRows || '<tr><td colspan="2">No runs supplied.</td></tr>'}</tbody>
          </table>
        </div>
      </section>

      <section aria-labelledby="timing-heading">
        <div class="section-heading">
          <div><p class="eyebrow">Queue versus execution</p><h2 id="timing-heading">Phase totals</h2></div>
          <p class="quiet">Totals reconcile only the phase records visible below.</p>
        </div>
        <div class="table-wrap" tabindex="0" role="region" aria-label="Scrollable phase timing table">
          <table><thead><tr><th scope="col">Phase</th><th scope="col">Queue</th><th scope="col">Execution</th><th scope="col">Runs</th></tr></thead><tbody>${phaseSummary}</tbody></table>
        </div>
      </section>

      <section aria-labelledby="runs-heading">
        <div class="section-heading">
          <div><p class="eyebrow">Expandable evidence</p><h2 id="runs-heading">Runs</h2></div>
          <p class="quiet">Native disclosure controls support keyboard navigation and print expansion.</p>
        </div>
        <div class="runs">${runCards || '<p class="empty">The validated input contains no runs.</p>'}</div>
      </section>

      <section id="contract" class="contract" aria-labelledby="contract-heading">
        <p class="eyebrow">Contract</p>
        <h2 id="contract-heading">Validation behavior</h2>
        <p>This report was generated only after schema version 1 and cross-field invariants passed. Unknown fields, malformed links, incorrect phase order, inconsistent token totals, and accepted runs containing failures are rejected visibly.</p>
      </section>
    </main>
  `, `${report.runs.length} run remote infrastructure report`);
}

function renderRejectedReport(errors) {
  return documentShell(`
    <header class="page rejected">
      <div>
        <p class="kicker">Infrastructure verification · Input rejected</p>
        <h1>Remote test report could not be generated</h1>
        <p class="lede">Malformed or schema-incompatible input was detected. No run was silently omitted.</p>
      </div>
      <span class="status bad">Invalid input</span>
    </header>
    <main>
      <section class="validation" aria-labelledby="validation-heading">
        <p class="eyebrow">Validation findings</p>
        <h2 id="validation-heading">${errors.length} ${errors.length === 1 ? 'problem' : 'problems'} found</h2>
        <ol>${errors.map(error => `<li><code>${escapeHtml(error)}</code></li>`).join('')}</ol>
      </section>
    </main>
  `, 'Remote infrastructure report input rejected');
}

function matrixRow(run, labels) {
  const byId = new Map(run.assertions.map(item => [item.id, item]));
  return `<tr>
    <th scope="row"><a href="#run-${anchor(run.runId)}">${escapeHtml(run.runId)}</a><small>${escapeHtml(run.scenario.id)}</small></th>
    ${labels.map(([id]) => {
      const assertion = byId.get(id);
      if (!assertion) return '<td><span class="na" aria-label="Not applicable">n/a</span></td>';
      return `<td><a class="status ${assertion.status}" href="#assertion-${anchor(run.runId)}-${anchor(assertion.id)}">${assertion.status === 'pass' ? 'Pass' : 'Fail'}</a></td>`;
    }).join('')}
  </tr>`;
}

function runCard(run, chronicleHref) {
  const queueTotal = run.phases.reduce((sum, phase) => sum + phase.queueMs, 0);
  const executionTotal = run.phases.reduce((sum, phase) => sum + phase.executionMs, 0);
  const unaccounted = Math.max(0, run.wallMs - queueTotal - executionTotal);
  const phases = run.phases.map(phase => `
    <li class="phase ${phase.status}">
      <div class="phase-head"><span class="phase-dot" aria-hidden="true"></span><strong>${title(phase.name)}</strong><span class="status ${phase.status}">${title(phase.status)}</span></div>
      <dl><div><dt>Queue</dt><dd>${duration(phase.queueMs)}</dd></div><div><dt>Execution</dt><dd>${duration(phase.executionMs)}</dd></div></dl>
    </li>`).join('');
  const assertions = run.assertions.map(assertion => `
    <article class="assertion ${assertion.status}" id="assertion-${anchor(run.runId)}-${anchor(assertion.id)}" tabindex="-1">
      <div><span class="status ${assertion.status}">${title(assertion.status)}</span><h4>${escapeHtml(assertion.label)}</h4></div>
      <p>${escapeHtml(assertion.detail)}</p>
      <a class="evidence" href="${escapeAttribute(assertion.evidenceHref)}">Open raw evidence <span aria-hidden="true">↗</span></a>
    </article>`).join('');
  const incidents = run.incidents.length ? `
    <section class="incidents" aria-labelledby="incidents-${anchor(run.runId)}">
      <h3 id="incidents-${anchor(run.runId)}">Injected incidents and recovery</h3>
      ${run.incidents.map(incident => `
        <article class="incident">
          <div><span class="status warn">${incident.injected ? 'Injected' : 'Observed'}</span><strong>${escapeHtml(incident.label)}</strong></div>
          <p>${escapeHtml(incident.recoveryOutcome)}</p>
          <a href="${escapeAttribute(`${chronicleHref}#${incident.chronicleAnchor}`)}">Open ${escapeHtml(incident.class)} in the hardening chronicle</a>
        </article>`).join('')}
    </section>` : '';
  const tokens = run.tokens.available
    ? `<strong>${number(run.tokens.total)} total</strong><span>${number(run.tokens.input)} input · ${number(run.tokens.output)} output</span>
       <span>${Object.entries(run.tokens.phases).length
          ? Object.entries(run.tokens.phases).map(([phase, count]) => `${title(phase)} ${number(count)}`).join(' · ')
          : 'Phase attribution unavailable'}</span>`
    : `<strong>Unavailable</strong><span>${escapeHtml(run.tokens.reason)}</span>`;
  const components = run.components.map(component => `<li><span>${escapeHtml(component.name)}</span><code>${escapeHtml(component.version)}</code></li>`).join('');
  const extraLinks = run.links.map(link => `<a href="${escapeAttribute(link.href)}">${escapeHtml(link.label)}</a>`).join('');

  return `<details class="run-card ${run.accepted ? 'accepted' : 'failed'}" id="run-${anchor(run.runId)}">
    <summary>
      <span class="chevron" aria-hidden="true"></span>
      <span class="run-identity"><strong>${escapeHtml(run.runId)}</strong><span>${escapeHtml(run.scenario.id)}</span></span>
      <span class="status ${run.accepted ? 'pass' : 'fail'}">${run.accepted ? 'Accepted' : 'Failed'}</span>
      <span class="outcome">${title(run.outcome)}</span>
      <span class="wall">${duration(run.wallMs)}</span>
    </summary>
    <div class="run-body">
      <div class="run-links">
        <a href="${escapeAttribute(run.taskHref)}">${escapeHtml(run.taskKey)}</a>
        <a id="attempt-${anchor(run.runId)}" href="${escapeAttribute(run.attemptHref)}"><code>${escapeHtml(run.attemptId)}</code></a>
        <a href="${escapeAttribute(run.scenario.manifestHref)}">Scenario manifest</a>
        <a href="${escapeAttribute(run.rawArtifactHref)}">Raw run artifact</a>
        ${extraLinks}
      </div>

      <section aria-labelledby="timeline-${anchor(run.runId)}">
        <div class="subheading"><h3 id="timeline-${anchor(run.runId)}">Phase timeline</h3><span>Queue ${duration(queueTotal)} · Execution ${duration(executionTotal)}${unaccounted ? ` · Other ${duration(unaccounted)}` : ''}</span></div>
        <ol class="timeline">${phases}</ol>
      </section>

      <div class="evidence-grid">
        <section class="data-panel" aria-labelledby="sha-${anchor(run.runId)}">
          <h3 id="sha-${anchor(run.runId)}">Exact revisions</h3>
          <dl class="sha-list">
            <div><dt>Base SHA</dt><dd><a href="${escapeAttribute(`${run.rawArtifactHref}#baseSha`)}"><code>${escapeHtml(run.baseSha)}</code></a></dd></div>
            <div><dt>Result SHA</dt><dd><a id="result-ref-${anchor(run.runId)}" href="${escapeAttribute(`${run.rawArtifactHref}#resultSha`)}"><code>${escapeHtml(run.resultSha)}</code></a></dd></div>
          </dl>
        </section>
        <section class="data-panel tokens" aria-labelledby="tokens-${anchor(run.runId)}">
          <h3 id="tokens-${anchor(run.runId)}">Token telemetry</h3>
          ${tokens}
        </section>
        <section class="data-panel" aria-labelledby="components-${anchor(run.runId)}">
          <h3 id="components-${anchor(run.runId)}">Component versions</h3>
          <ul class="component-list">${components}</ul>
        </section>
      </div>

      ${incidents}
      <section class="assertions" aria-labelledby="assertions-${anchor(run.runId)}">
        <h3 id="assertions-${anchor(run.runId)}">Assertions</h3>
        ${assertions}
      </section>
    </div>
  </details>`;
}

function metric(label, value, detail, tone = '') {
  return `<article class="metric ${tone}"><span>${escapeHtml(label)}</span><strong>${escapeHtml(String(value))}</strong><small>${escapeHtml(detail)}</small></article>`;
}

function countOutcomes(runs) {
  const counts = { baseline: 0, healed: 0, recovered: 0, lost: 0, invalid: 0 };
  for (const run of runs) counts[run.outcome]++;
  return counts;
}

function title(value) {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

function number(value) {
  return new Intl.NumberFormat('en-US').format(value);
}

function duration(ms) {
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms % 1000 === 0 ? 0 : 1)} s`;
  const minutes = Math.floor(ms / 60_000);
  const seconds = Math.round((ms % 60_000) / 1000);
  return `${minutes}m ${seconds}s`;
}

function formatDate(value) {
  return new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' }).format(new Date(value)) + ' UTC';
}

function anchor(value) {
  return value.toLowerCase().replace(/[^a-z0-9_-]+/g, '-');
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[character]);
}

function escapeAttribute(value) {
  return escapeHtml(value);
}

function documentShell(body, titleText) {
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>${escapeHtml(titleText)}</title>
<style>
  :root {
    color-scheme: light;
    --surface-1: #fcfcfb; --surface-2: #f2f1ee; --surface-3: #e8e7e2;
    --ink-1: #10100f; --ink-2: #565550; --ink-3: #77756f; --line: #d5d3cc;
    --accent: #246db5; --accent-soft: #e7f0fa; --ok: #167044; --ok-soft: #e7f4ec;
    --bad: #b9333b; --bad-soft: #f9e9eb; --warn: #8a6400; --warn-soft: #f7efd9;
    --shadow: 0 12px 30px rgba(22, 22, 18, .08);
  }
  :root[data-theme="dark"] {
    color-scheme: dark;
    --surface-1: #181817; --surface-2: #222221; --surface-3: #2c2c2a;
    --ink-1: #f7f6f1; --ink-2: #c6c4ba; --ink-3: #9a9890; --line: #3d3c38;
    --accent: #77afe7; --accent-soft: #1d2b3a; --ok: #65bf8d; --ok-soft: #1b3025;
    --bad: #ef7b83; --bad-soft: #3a2023; --warn: #e1b84f; --warn-soft: #352f1c;
    --shadow: 0 12px 30px rgba(0, 0, 0, .22);
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      color-scheme: dark;
      --surface-1: #181817; --surface-2: #222221; --surface-3: #2c2c2a;
      --ink-1: #f7f6f1; --ink-2: #c6c4ba; --ink-3: #9a9890; --line: #3d3c38;
      --accent: #77afe7; --accent-soft: #1d2b3a; --ok: #65bf8d; --ok-soft: #1b3025;
      --bad: #ef7b83; --bad-soft: #3a2023; --warn: #e1b84f; --warn-soft: #352f1c;
      --shadow: 0 12px 30px rgba(0, 0, 0, .22);
    }
  }
  * { box-sizing: border-box; }
  html { scroll-behavior: smooth; }
  body { margin: 0; background: var(--surface-1); color: var(--ink-1); font: 15px/1.55 system-ui, "Segoe UI", sans-serif; }
  a { color: var(--accent); text-underline-offset: 3px; }
  a:hover { text-decoration-thickness: 2px; }
  a:focus-visible, button:focus-visible, summary:focus-visible, [tabindex="0"]:focus-visible { outline: 3px solid var(--accent); outline-offset: 3px; }
  .page, main { width: 100%; padding-inline: clamp(18px, 4vw, 64px); }
  .page { position: relative; display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 18px 32px; padding-block: 42px 28px; border-bottom: 1px solid var(--line); background: var(--surface-2); }
  .page .meta { grid-column: 1 / -1; display: flex; flex-wrap: wrap; gap: 8px 24px; color: var(--ink-2); }
  .page.rejected { background: var(--bad-soft); }
  h1, h2, h3, h4, p { margin-top: 0; }
  h1 { margin-bottom: 8px; font-size: clamp(28px, 4vw, 44px); line-height: 1.12; letter-spacing: -.025em; }
  h2 { margin-bottom: 0; font-size: clamp(22px, 2.5vw, 30px); line-height: 1.2; }
  h3 { margin-bottom: 10px; font-size: 17px; }
  h4 { margin-bottom: 4px; font-size: 15px; }
  .kicker, .eyebrow { margin-bottom: 7px; color: var(--ink-3); font-size: 12px; font-weight: 700; letter-spacing: .11em; text-transform: uppercase; }
  .lede { max-width: 72ch; margin-bottom: 0; color: var(--ink-2); font-size: 17px; }
  .theme { align-self: start; border: 1px solid var(--line); border-radius: 999px; padding: 8px 14px; background: var(--surface-1); color: var(--ink-1); font: inherit; cursor: pointer; }
  main { padding-block: 34px 80px; }
  main > section { margin-bottom: 52px; scroll-margin-top: 20px; }
  .section-heading, .subheading { display: flex; align-items: end; justify-content: space-between; gap: 16px; margin-bottom: 18px; }
  .summary-line, .quiet, .subheading span { margin: 0; color: var(--ink-2); }
  .metrics { display: grid; grid-template-columns: repeat(7, minmax(130px, 1fr)); gap: 10px; }
  .metric { min-height: 126px; padding: 15px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface-2); }
  .metric > span { display: block; color: var(--ink-2); font-weight: 650; }
  .metric > strong { display: block; margin: 8px 0; font-size: 25px; line-height: 1.1; }
  .metric small { display: block; color: var(--ink-3); }
  .metric.bad { background: var(--bad-soft); border-color: color-mix(in srgb, var(--bad) 30%, var(--line)); }
  .table-wrap { overflow-x: auto; border: 1px solid var(--line); border-radius: 10px; background: var(--surface-2); }
  table { width: 100%; border-collapse: collapse; min-width: 620px; }
  th, td { padding: 11px 14px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: middle; }
  thead th { color: var(--ink-3); font-size: 11px; letter-spacing: .08em; text-transform: uppercase; }
  tbody tr:last-child > * { border-bottom: 0; }
  .matrix th[scope="row"] small { display: block; color: var(--ink-3); font-weight: 400; }
  .status { display: inline-flex; align-items: center; width: fit-content; border-radius: 999px; padding: 3px 9px; font-size: 11px; font-weight: 750; letter-spacing: .03em; text-decoration: none; }
  .status.pass { color: var(--ok); background: var(--ok-soft); }
  .status.fail, .status.bad { color: var(--bad); background: var(--bad-soft); }
  .status.warn, .status.skipped { color: var(--warn); background: var(--warn-soft); }
  .na { color: var(--ink-3); }
  .runs { display: grid; gap: 12px; }
  .run-card { border: 1px solid var(--line); border-radius: 12px; background: var(--surface-2); box-shadow: var(--shadow); overflow: clip; scroll-margin-top: 18px; }
  .run-card.failed { background: color-mix(in srgb, var(--bad-soft) 45%, var(--surface-2)); }
  .run-card summary { display: grid; grid-template-columns: 18px minmax(190px, 1fr) auto auto auto; align-items: center; gap: 14px; padding: 16px 18px; cursor: pointer; list-style: none; }
  .run-card summary::-webkit-details-marker { display: none; }
  .chevron { width: 8px; height: 8px; border-right: 2px solid var(--ink-3); border-bottom: 2px solid var(--ink-3); transform: rotate(-45deg); transition: transform .16s ease; }
  details[open] .chevron { transform: rotate(45deg); }
  .run-identity strong, .run-identity span { display: block; }
  .run-identity span, .outcome, .wall { color: var(--ink-2); }
  .run-body { padding: 0 18px 22px; border-top: 1px solid var(--line); }
  .run-links { display: flex; flex-wrap: wrap; gap: 8px 18px; padding-block: 14px; }
  code { font: 12px/1.45 ui-monospace, SFMono-Regular, Consolas, monospace; overflow-wrap: anywhere; }
  .timeline { display: grid; grid-template-columns: repeat(5, minmax(120px, 1fr)); gap: 8px; margin: 0; padding: 0; list-style: none; }
  .phase { position: relative; min-height: 112px; padding: 13px; border: 1px solid var(--line); border-radius: 9px; background: var(--surface-1); }
  .phase-head { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; }
  .phase-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--ok); }
  .phase.fail .phase-dot { background: var(--bad); }
  .phase.skipped .phase-dot { background: var(--warn); }
  .phase dl { display: grid; gap: 2px; margin: 12px 0 0; color: var(--ink-2); }
  .phase dl div { display: flex; justify-content: space-between; gap: 8px; }
  .phase dt, .phase dd { margin: 0; }
  .evidence-grid { display: grid; grid-template-columns: 1.3fr .8fr .9fr; gap: 10px; margin-top: 18px; }
  .data-panel, .incident, .assertion, .validation, .contract { padding: 16px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface-1); }
  .sha-list { display: grid; gap: 10px; margin: 0; }
  .sha-list div { min-width: 0; }
  .sha-list dt { color: var(--ink-3); font-size: 12px; }
  .sha-list dd { margin: 2px 0 0; }
  .tokens > * { display: block; margin-bottom: 5px; }
  .tokens span { color: var(--ink-2); }
  .component-list { display: grid; gap: 5px; margin: 0; padding: 0; list-style: none; }
  .component-list li { display: flex; justify-content: space-between; gap: 10px; }
  .incidents, .assertions { margin-top: 18px; }
  .incident { background: var(--warn-soft); }
  .incident > div { display: flex; align-items: center; flex-wrap: wrap; gap: 9px; }
  .incident p { margin: 8px 0; }
  .assertions { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 9px; }
  .assertions > h3 { grid-column: 1 / -1; }
  .assertion { scroll-margin-top: 18px; }
  .assertion.fail { background: var(--bad-soft); border-color: color-mix(in srgb, var(--bad) 35%, var(--line)); }
  .assertion.pass { background: var(--ok-soft); border-color: color-mix(in srgb, var(--ok) 25%, var(--line)); }
  .assertion > div { display: flex; align-items: center; gap: 9px; }
  .assertion h4 { margin: 0; }
  .assertion p { margin: 10px 0; color: var(--ink-2); }
  .evidence { font-weight: 650; }
  .validation { max-width: 980px; background: var(--bad-soft); }
  .validation li { margin-block: 8px; }
  .contract { background: var(--accent-soft); }
  .contract p:last-child { max-width: 80ch; margin-bottom: 0; }
  .empty { color: var(--ink-2); }
  @media (max-width: 1100px) {
    .metrics { grid-template-columns: repeat(4, minmax(130px, 1fr)); }
    .timeline { grid-template-columns: repeat(3, minmax(120px, 1fr)); }
    .evidence-grid { grid-template-columns: 1fr 1fr; }
  }
  @media (max-width: 680px) {
    .page { grid-template-columns: 1fr; padding-block: 28px 20px; }
    .page > div:first-child { padding-right: 82px; }
    .theme { position: absolute; top: 14px; right: 14px; }
    main { padding-block-start: 24px; }
    .section-heading, .subheading { align-items: start; flex-direction: column; }
    .metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .metric { min-height: 112px; }
    .run-card summary { grid-template-columns: 16px minmax(0, 1fr) auto; gap: 9px; }
    .run-card summary .outcome, .run-card summary .wall { grid-column: 2; }
    .timeline, .evidence-grid, .assertions { grid-template-columns: 1fr; }
    .assertions > h3 { grid-column: 1; }
  }
  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { scroll-behavior: auto !important; transition-duration: 0s !important; animation-duration: 0s !important; }
  }
  @media print {
    :root { color-scheme: light; }
    body { background: white; color: black; font-size: 10pt; }
    .theme { display: none; }
    .page, main { padding-inline: 0; }
    main { padding-block: 16px 0; }
    main > section { margin-bottom: 24px; }
    .run-card { break-inside: avoid; box-shadow: none; }
    details:not([open]) > .run-body, details > .run-body { display: block !important; }
    .chevron { display: none; }
    a { color: inherit; }
  }
</style>
</head>
<body>
${body}
<script>
  (() => {
    const root = document.documentElement;
    const button = document.querySelector('[data-theme-toggle]');
    if (!button) return;
    button.addEventListener('click', () => {
      const current = root.dataset.theme || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
      root.dataset.theme = current === 'dark' ? 'light' : 'dark';
      button.setAttribute('aria-label', 'Switch to ' + current + ' theme');
    });
    if (location.hash.startsWith('#assertion-')) {
      const target = document.querySelector(location.hash);
      const details = target?.closest('details');
      if (details) details.open = true;
    }
    addEventListener('hashchange', () => {
      const target = document.querySelector(location.hash);
      const details = target?.closest('details');
      if (details) details.open = true;
    });
    let printOpenState = [];
    addEventListener('beforeprint', () => {
      printOpenState = [...document.querySelectorAll('details')].map(details => details.open);
      document.querySelectorAll('details').forEach(details => { details.open = true; });
    });
    addEventListener('afterprint', () => {
      document.querySelectorAll('details').forEach((details, index) => {
        details.open = printOpenState[index] ?? details.open;
      });
      printOpenState = [];
    });
  })();
</script>
</body>
</html>
`;
}

export function rebaseReportLinks(report, inputFile, outputFile) {
  const clone = structuredClone(report);
  const inputDirectory = path.dirname(path.resolve(inputFile));
  const outputDirectory = path.dirname(path.resolve(outputFile));
  const rebase = value => {
    if (typeof value !== 'string' || value.startsWith('#') || /^https?:\/\//.test(value)) return value;
    const [filePart, fragment] = value.split('#', 2);
    const absolute = path.resolve(inputDirectory, filePart);
    let relative = path.relative(outputDirectory, absolute).split(path.sep).join('/');
    if (!relative.startsWith('.')) relative = `./${relative}`;
    return fragment === undefined ? relative : `${relative}#${fragment}`;
  };
  clone.suite.sourceTaskHref = rebase(clone.suite.sourceTaskHref);
  clone.suite.chronicleHref = rebase(clone.suite.chronicleHref);
  for (const run of clone.runs) {
    for (const field of ['taskHref', 'attemptHref', 'rawArtifactHref']) run[field] = rebase(run[field]);
    run.scenario.manifestHref = rebase(run.scenario.manifestHref);
    for (const assertion of run.assertions) assertion.evidenceHref = rebase(assertion.evidenceHref);
    for (const link of run.links) link.href = rebase(link.href);
  }
  return clone;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  let parsed;
  let errors = [];
  try {
    parsed = JSON.parse(await readFile(args.input, 'utf8'));
    const validation = validateRunResult(parsed);
    errors = validation.errors;
  } catch (error) {
    errors = [`$: JSON parse failed: ${error.message}`];
  }
  const report = errors.length === 0 ? rebaseReportLinks(parsed, args.input, args.output) : null;
  await mkdir(path.dirname(path.resolve(args.output)), { recursive: true });
  await writeFile(args.output, renderReport(report, { validationErrors: errors }));
  if (errors.length > 0) {
    console.error(`Report input rejected with ${errors.length} validation error(s). Visible report written to ${args.output}.`);
    process.exitCode = 2;
  } else {
    console.log(`Report written to ${args.output} (${parsed.runs.length} runs).`);
  }
}

function parseArgs(values) {
  const args = {};
  for (let index = 0; index < values.length; index++) {
    if (values[index] === '--input') args.input = values[++index];
    else if (values[index] === '--output') args.output = values[++index];
    else throw new Error(`Unknown argument: ${values[index]}`);
  }
  if (!args.input || !args.output) throw new Error('Usage: node report.mjs --input RUNS.json --output REPORT.html');
  return args;
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1]);
if (isMain) await main();
