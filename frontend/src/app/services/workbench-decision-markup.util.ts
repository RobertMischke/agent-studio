import {
  WorkbenchDecisionKind,
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
} from '../models/project-docs.model';

const SAFE_ID = /^[A-Za-z0-9_-]{1,80}$/;
const KINDS = new Set<WorkbenchDecisionKind>(['single', 'multi', 'confirm']);

export interface WorkbenchDecisionMarkup {
  points: WorkbenchDecisionPoint[];
  responses: WorkbenchDecisionResponse[];
}

/**
 * Reads the author-facing convention without executing repository HTML. Invalid
 * or duplicate ids fail closed at the individual decision point, leaving the
 * underlying document readable as ordinary static HTML.
 */
export function discoverWorkbenchDecisionMarkup(html: string): WorkbenchDecisionMarkup {
  if (!html) return { points: [], responses: [] };
  const document = new DOMParser().parseFromString(html, 'text/html');
  const points: WorkbenchDecisionPoint[] = [];
  const responses: WorkbenchDecisionResponse[] = [];
  const decisionIds = new Set<string>();

  for (const element of Array.from(document.querySelectorAll<HTMLElement>(
    '[data-decision-id][data-decision-kind]',
  ))) {
    const id = element.dataset['decisionId']?.trim() ?? '';
    const kind = element.dataset['decisionKind']?.trim() as WorkbenchDecisionKind;
    if (!SAFE_ID.test(id) || !KINDS.has(kind) || decisionIds.has(id)) continue;

    const optionIds = new Set<string>();
    const options = Array.from(element.querySelectorAll<HTMLElement>('[data-option-id]'))
      .flatMap(option => {
        const optionId = option.dataset['optionId']?.trim() ?? '';
        if (!SAFE_ID.test(optionId) || optionIds.has(optionId)) return [];
        optionIds.add(optionId);
        return [{ id: optionId, label: optionLabel(option) }];
      });
    if (options.length === 0) continue;

    decisionIds.add(id);
    const comment = element.querySelector<HTMLElement>('[data-comment]');
    const point: WorkbenchDecisionPoint = {
      id,
      kind,
      label: decisionLabel(element),
      options,
      commentLabel: comment ? commentLabel(comment) : null,
    };
    points.push(point);
    responses.push({
      decisionId: id,
      kind,
      selectedOptionIds: options
        .filter(option => optionInitiallySelected(element, option.id))
        .map(option => option.id)
        .slice(0, kind === 'multi' ? options.length : 1),
      comment: initialComment(comment),
    });
  }

  return { points, responses };
}

/** Accept only state that belongs to the markup the trusted host discovered. */
export function normalizeWorkbenchDecisionResponses(
  value: unknown,
  points: readonly WorkbenchDecisionPoint[],
): WorkbenchDecisionResponse[] | null {
  if (!Array.isArray(value)) return null;
  const byId = new Map(points.map(point => [point.id, point]));
  const seen = new Set<string>();
  const result: WorkbenchDecisionResponse[] = [];
  for (const candidate of value) {
    if (!candidate || typeof candidate !== 'object') return null;
    const raw = candidate as Record<string, unknown>;
    const decisionId = typeof raw['decisionId'] === 'string' ? raw['decisionId'] : '';
    const point = byId.get(decisionId);
    if (!point || seen.has(decisionId) || raw['kind'] !== point.kind
      || !Array.isArray(raw['selectedOptionIds'])) return null;
    const allowedOptions = new Set(point.options.map(option => option.id));
    const selectedOptionIds = [...new Set(raw['selectedOptionIds'])]
      .filter((id): id is string => typeof id === 'string' && allowedOptions.has(id));
    if (selectedOptionIds.length !== raw['selectedOptionIds'].length
      || point.kind !== 'multi' && selectedOptionIds.length > 1) return null;
    const rawComment = raw['comment'];
    if (rawComment != null && typeof rawComment !== 'string') return null;
    seen.add(decisionId);
    result.push({
      decisionId,
      kind: point.kind,
      selectedOptionIds,
      comment: typeof rawComment === 'string' ? rawComment.slice(0, 20_000) || null : null,
    });
  }
  return result.length === points.length ? result : null;
}

function decisionLabel(element: HTMLElement): string {
  const explicit = element.dataset['decisionLabel']?.trim();
  if (explicit) return bounded(explicit);
  const heading = element.querySelector<HTMLElement>('h1,h2,h3,h4,h5,h6,legend,strong');
  return bounded(heading?.textContent?.trim() || element.dataset['decisionId'] || 'Decision');
}

function optionLabel(element: HTMLElement): string {
  const explicit = element.dataset['optionLabel']?.trim();
  if (explicit) return bounded(explicit);
  const clone = element.cloneNode(true) as HTMLElement;
  clone.querySelectorAll('input,textarea,select,button').forEach(control => control.remove());
  return bounded(clone.textContent?.replace(/\s+/g, ' ').trim() || element.dataset['optionId'] || 'Option');
}

function commentLabel(element: HTMLElement): string {
  const explicit = element.dataset['comment']?.trim()
    || element.getAttribute('aria-label')?.trim()
    || element.getAttribute('placeholder')?.trim();
  if (explicit) return bounded(explicit);
  const clone = element.cloneNode(true) as HTMLElement;
  clone.querySelectorAll('input,textarea').forEach(control => control.remove());
  return bounded(clone.textContent?.replace(/\s+/g, ' ').trim() || 'Optional comment');
}

function optionInitiallySelected(container: HTMLElement, optionId: string): boolean {
  const option = Array.from(container.querySelectorAll<HTMLElement>('[data-option-id]'))
    .find(candidate => candidate.dataset['optionId']?.trim() === optionId);
  return option?.querySelector<HTMLInputElement>(
    'input[type="checkbox"]:checked,input[type="radio"]:checked') != null;
}

function initialComment(element: HTMLElement | null): string | null {
  if (element instanceof HTMLTextAreaElement || element instanceof HTMLInputElement)
    return element.value.trim().slice(0, 20_000) || null;
  const field = element?.querySelector<HTMLTextAreaElement | HTMLInputElement>('textarea,input[type="text"]');
  return field?.value.trim().slice(0, 20_000) || null;
}

function bounded(value: string): string {
  return value.slice(0, 500);
}
