import {
  WorkbenchDecisionAnswer,
  WorkbenchDecisionKind,
  WorkbenchDecisionPoint,
} from '../../../../models/project-docs.model';
import { buildIsolatedHtmlSrcdoc } from '../../../../services/sandboxed-html.util';

export const WORKBENCH_DECISION_CHANGE_MESSAGE = 'agent-studio:workbench-decision-change';
export const WORKBENCH_THEME_MESSAGE = 'agent-studio:workbench-theme';

const SAFE_ID = /^[a-z0-9][a-z0-9._-]{0,119}$/;
const KINDS = new Set<WorkbenchDecisionKind>(['single', 'multi', 'confirm']);

/**
 * Discover the author-declared decision contract without executing repository
 * HTML. Invalid or duplicate declarations are left as ordinary static content.
 */
export function parseWorkbenchDecisionPoints(html: string): WorkbenchDecisionPoint[] {
  if (!html) return [];
  const document = new DOMParser().parseFromString(html, 'text/html');
  const seen = new Set<string>();
  const points: WorkbenchDecisionPoint[] = [];

  for (const element of Array.from(document.querySelectorAll<HTMLElement>('[data-decision-id]'))) {
    const id = element.dataset['decisionId']?.trim() ?? '';
    const kind = element.dataset['decisionKind']?.trim() as WorkbenchDecisionKind | undefined;
    if (!SAFE_ID.test(id) || !kind || !KINDS.has(kind) || seen.has(id)) continue;

    const optionIds = new Set<string>();
    const options = Array.from(element.querySelectorAll<HTMLElement>('[data-option-id]'))
      .filter(option => option.closest('[data-decision-id]') === element)
      .flatMap(option => {
        const optionId = option.dataset['optionId']?.trim() ?? '';
        if (!SAFE_ID.test(optionId) || optionIds.has(optionId)) return [];
        const label = compactText(
          option.dataset['optionLabel']
          ?? option.querySelector('strong, b')?.textContent
          ?? option.textContent,
        );
        if (!label) return [];
        optionIds.add(optionId);
        return [{ id: optionId, label }];
      });
    if (options.length === 0 || kind === 'confirm' && options.length !== 1) continue;

    const comment = Array.from(element.querySelectorAll<HTMLElement>('[data-comment]'))
      .find(candidate => candidate.closest('[data-decision-id]') === element);
    const heading = element.querySelector<HTMLElement>('[data-decision-label], h1, h2, h3, h4, h5, h6');
    points.push({
      id,
      kind,
      label: compactText(element.dataset['decisionLabel'] ?? heading?.textContent) || id,
      options,
      commentEnabled: comment !== undefined,
      commentLabel: compactText(comment?.dataset['commentLabel'] ?? comment?.textContent)
        || 'Comment (optional)',
    });
    seen.add(id);
  }
  return points;
}

/**
 * Validate an opaque-frame payload against the inertly parsed author contract.
 * Labels always come from the source document, never from the message sender.
 */
export function normalizeWorkbenchDecisionAnswers(
  points: readonly WorkbenchDecisionPoint[],
  payload: unknown,
): WorkbenchDecisionAnswer[] | null {
  if (!Array.isArray(payload) || payload.length > points.length) return null;
  const pointById = new Map(points.map(point => [point.id, point]));
  const incoming = new Map<string, WorkbenchDecisionAnswer>();

  for (const value of payload) {
    if (!isRecord(value)) return null;
    const decisionId = typeof value['decisionId'] === 'string' ? value['decisionId'] : '';
    const point = pointById.get(decisionId);
    if (!point || incoming.has(decisionId) || value['kind'] !== point.kind) return null;

    const rawSelected = Array.isArray(value['selectedOptionIds'])
      ? value['selectedOptionIds']
      : Array.isArray(value['selectedOptions'])
        ? value['selectedOptions'].map(option => isRecord(option) ? option['id'] : null)
        : null;
    if (!rawSelected || rawSelected.length > point.options.length) return null;
    const optionById = new Map(point.options.map(option => [option.id, option]));
    const selectedIds = new Set<string>();
    for (const selected of rawSelected) {
      if (typeof selected !== 'string' || selectedIds.has(selected) || !optionById.has(selected)) return null;
      selectedIds.add(selected);
    }
    if (point.kind !== 'multi' && selectedIds.size > 1) return null;

    const rawComment = value['comment'];
    if (rawComment !== null && rawComment !== undefined && typeof rawComment !== 'string') return null;
    const comment = typeof rawComment === 'string' ? rawComment.trim() : '';
    if (comment.length > 20_000 || !point.commentEnabled && comment) return null;
    incoming.set(decisionId, {
      decisionId,
      kind: point.kind,
      selectedOptions: point.options.filter(option => selectedIds.has(option.id)),
      comment: comment || null,
    });
  }

  return points.map(point => incoming.get(point.id) ?? ({
    decisionId: point.id,
    kind: point.kind,
    selectedOptions: [],
    comment: null,
  }));
}

export function workbenchDecisionAnswersComplete(
  points: readonly WorkbenchDecisionPoint[],
  answers: readonly WorkbenchDecisionAnswer[],
): boolean {
  if (points.length === 0 || answers.length !== points.length) return false;
  const byId = new Map(answers.map(answer => [answer.decisionId, answer]));
  return points.every(point => {
    const answer = byId.get(point.id);
    if (!answer || answer.kind !== point.kind || answer.selectedOptions.length === 0) return false;
    return point.kind === 'multi' || answer.selectedOptions.length === 1;
  });
}

export function workbenchDecisionSummary(
  points: readonly WorkbenchDecisionPoint[],
  answers: readonly WorkbenchDecisionAnswer[],
): string[] {
  const pointById = new Map(points.map(point => [point.id, point]));
  return answers
    .filter(answer => answer.selectedOptions.length > 0)
    .map(answer => {
      const label = pointById.get(answer.decisionId)?.label ?? answer.decisionId;
      const selected = answer.selectedOptions.map(option => option.label).join(', ');
      return `${label}: ${selected}${answer.comment ? ` (${answer.comment})` : ''}`;
    });
}

/** Inject only Studio-owned controls; the repository HTML remains unchanged. */
export function buildWorkbenchDecisionSrcdoc(
  html: string,
  points: readonly WorkbenchDecisionPoint[],
  answers: readonly WorkbenchDecisionAnswer[],
  disabled: boolean,
  theme: 'light' | 'dark' = 'light',
): string {
  const isolated = buildIsolatedHtmlSrcdoc(html);
  if (!isolated || points.length === 0) return isolated;
  const document = new DOMParser().parseFromString(isolated, 'text/html');
  document.documentElement.dataset['agentStudioTheme'] = theme;
  const style = document.createElement('style');
  style.dataset['agentStudioDecision'] = 'true';
  style.textContent = WORKBENCH_DECISION_STYLE;
  document.head.append(style);

  const initial = answers.map(answer => ({
    decisionId: answer.decisionId,
    kind: answer.kind,
    selectedOptionIds: answer.selectedOptions.map(option => option.id),
    comment: answer.comment,
  }));
  const config = safeScriptJson({ points, answers: initial, disabled });
  const script = document.createElement('script');
  script.dataset['agentStudioDecision'] = 'true';
  script.textContent = `(function () {
    var config = ${config};
    window.addEventListener('message', function (event) {
      if (!event.data || event.data.type !== '${WORKBENCH_THEME_MESSAGE}') return;
      if (event.data.theme !== 'light' && event.data.theme !== 'dark') return;
      document.documentElement.setAttribute('data-agent-studio-theme', event.data.theme);
    });
    function owned(root, selector) {
      return Array.prototype.filter.call(root.querySelectorAll(selector), function (node) {
        return node.closest('[data-decision-id]') === root;
      });
    }
    function answerFor(id) {
      for (var i = 0; i < config.answers.length; i += 1) {
        if (config.answers[i].decisionId === id) return config.answers[i];
      }
      return { selectedOptionIds: [], comment: null };
    }
    function emit() {
      var answers = config.points.map(function (point) {
        var root = findRoot(point.id);
        var selected = root ? owned(root, 'input[data-option-id]:checked').map(function (input) {
          return input.getAttribute('data-option-id');
        }) : [];
        var comment = root && root.querySelector('textarea[data-decision-comment]');
        return { decisionId: point.id, kind: point.kind, selectedOptionIds: selected,
          comment: comment ? comment.value : null };
      });
      parent.postMessage({ type: '${WORKBENCH_DECISION_CHANGE_MESSAGE}', answers: answers }, '*');
    }
    function findRoot(id) {
      var roots = document.querySelectorAll('[data-decision-id]');
      for (var i = 0; i < roots.length; i += 1) {
        if (roots[i].getAttribute('data-decision-id') === id) return roots[i];
      }
      return null;
    }
    config.points.forEach(function (point) {
      var root = findRoot(point.id);
      if (!root) return;
      root.setAttribute('data-agent-studio-decision', 'true');
      var saved = answerFor(point.id);
      owned(root, '[data-option-id]').forEach(function (option) {
        var optionId = option.getAttribute('data-option-id');
        var input = document.createElement('input');
        input.type = point.kind === 'single' ? 'radio' : 'checkbox';
        input.name = 'agent-studio-decision-' + point.id;
        input.setAttribute('data-option-id', optionId);
        input.setAttribute('data-testid', 'workbench-decision-' + point.id + '-' + optionId);
        input.setAttribute('aria-label', option.getAttribute('data-option-label') || (option.textContent || '').trim());
        input.checked = saved.selectedOptionIds.indexOf(optionId) >= 0;
        input.disabled = config.disabled;
        input.addEventListener('change', emit);
        option.classList.add('agent-studio-decision-option');
        option.insertBefore(input, option.firstChild);
        option.addEventListener('click', function (event) {
          if (config.disabled || event.target === input
              || event.target.closest && event.target.closest('a, button, input, textarea')) return;
          if (input.type === 'radio') input.checked = true;
          else input.checked = !input.checked;
          input.dispatchEvent(new Event('change', { bubbles: true }));
        });
      });
      var comments = owned(root, '[data-comment]');
      if (comments.length) {
        var comment = comments[0];
        comment.classList.add('agent-studio-decision-comment');
        var textarea = document.createElement('textarea');
        textarea.rows = 2;
        textarea.value = saved.comment || '';
        textarea.disabled = config.disabled;
        textarea.placeholder = 'Optional comment';
        textarea.setAttribute('data-decision-comment', 'true');
        textarea.setAttribute('data-testid', 'workbench-decision-' + point.id + '-comment');
        textarea.setAttribute('aria-label', point.commentLabel);
        textarea.addEventListener('input', emit);
        comment.appendChild(textarea);
      }
    });
  }());`;
  document.body.append(script);
  return `<!doctype html>${document.documentElement.outerHTML}`;
}

const WORKBENCH_DECISION_STYLE = `
  html[data-agent-studio-theme="light"] { color-scheme: light; }
  html[data-agent-studio-theme="dark"] { color-scheme: dark; }
  [data-agent-studio-decision="true"] {
    border: 1px solid color-mix(in srgb, currentColor 24%, transparent);
    border-radius: 10px;
    background: color-mix(in srgb, Canvas 94%, AccentColor 6%);
    padding: 16px;
  }
  .agent-studio-decision-option {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    gap: 10px;
    align-items: start;
    margin-block: 8px;
    border: 1px solid color-mix(in srgb, currentColor 20%, transparent);
    border-radius: 8px;
    background: Canvas;
    color: CanvasText;
    padding: 11px 12px;
    cursor: pointer;
  }
  .agent-studio-decision-option:has(input:checked) {
    border-color: color-mix(in srgb, AccentColor 74%, currentColor 26%);
    background: color-mix(in srgb, Canvas 78%, AccentColor 22%);
  }
  .agent-studio-decision-option > input { width: 16px; height: 16px; margin: 2px 0 0; accent-color: AccentColor; }
  .agent-studio-decision-option > input:checked { opacity: 1; }
  .agent-studio-decision-option > :not(input) { grid-column: 2; }
  .agent-studio-decision-option:has(input:disabled) { cursor: default; }
  .agent-studio-decision-comment { display: grid; gap: 6px; margin-top: 10px; }
  .agent-studio-decision-comment textarea {
    width: 100%; box-sizing: border-box; min-height: 54px; resize: vertical;
    border: 1px solid color-mix(in srgb, currentColor 24%, transparent);
    border-radius: 8px; background: Canvas; color: CanvasText; padding: 9px; font: inherit;
  }
  .agent-studio-decision-option:focus-within,
  .agent-studio-decision-comment textarea:focus-visible { outline: 2px solid AccentColor; outline-offset: 2px; }
`;

function compactText(value: string | null | undefined): string {
  return (value ?? '').replace(/\s+/g, ' ').trim().slice(0, 2_000);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function safeScriptJson(value: unknown): string {
  return JSON.stringify(value)
    .replaceAll('<', '\\u003c')
    .replaceAll('\u2028', '\\u2028')
    .replaceAll('\u2029', '\\u2029');
}
