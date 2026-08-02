export const DECISION_EMBED_SELECTOR =
  'script[type="application/json"][data-agent-studio-decision]';

export const DECISION_MOVE_TARGETS = [
  '2-ready',
  '5-human-review',
  '6-completed',
  '7-archive',
] as const;

export type DecisionMoveTarget = (typeof DECISION_MOVE_TARGETS)[number];

export type DecisionAction =
  | { kind: 'steer'; prompt: string }
  | { kind: 'move'; targetState: DecisionMoveTarget };

export interface DecisionOption {
  id: string;
  label: string;
  summary: string;
  consequences: string[];
  action: DecisionAction;
}

export interface DecisionDocument {
  version: 1;
  id: string;
  title: string;
  question: string;
  context: string | null;
  recommendation: { optionId: string; reason: string } | null;
  options: DecisionOption[];
  steer: {
    label: string;
    placeholder: string;
    required: boolean;
  };
}

export interface DecisionSurfaceSubmission {
  decisionId: string;
  optionId: string;
  optionLabel: string;
  artifactPath: 'results/decision.html' | 'results/decision.json';
  action: DecisionAction;
  prompt: string;
  reason: string;
}

export interface DecisionParseResult {
  document: DecisionDocument | null;
  error: string | null;
}

const ID_RE = /^[a-z0-9][a-z0-9_-]{0,79}$/;
const MOVE_TARGET_SET = new Set<string>(DECISION_MOVE_TARGETS);

export function parseDecisionJson(raw: string): DecisionParseResult {
  try {
    return parseDecisionValue(JSON.parse(raw) as unknown);
  } catch {
    return { document: null, error: 'decision.json is not valid JSON.' };
  }
}

export function parseEmbeddedDecision(html: string): DecisionParseResult {
  const parsed = new DOMParser().parseFromString(html, 'text/html');
  const node = parsed.querySelector(DECISION_EMBED_SELECTOR);
  if (!node) return { document: null, error: null };
  const raw = node.textContent?.trim() ?? '';
  if (!raw) {
    return { document: null, error: 'The embedded decision contract is empty.' };
  }
  const result = parseDecisionJson(raw);
  return result.error === 'decision.json is not valid JSON.'
    ? { document: null, error: 'The embedded decision contract is not valid JSON.' }
    : result;
}

export function buildDecisionSubmission(
  document: DecisionDocument,
  option: DecisionOption,
  freeSteer: string,
  artifactPath: DecisionSurfaceSubmission['artifactPath'],
): DecisionSurfaceSubmission {
  const steer = freeSteer.trim();
  const consequenceText = option.consequences.join('; ');
  const reasonParts = [
    `Decision ${document.id}: selected "${option.label}" (${option.id}) from ${artifactPath}.`,
    `Consequences: ${consequenceText}.`,
  ];
  if (steer) reasonParts.push(`Additional guidance: ${steer}`);
  const reason = reasonParts.join(' ');

  const prompt = option.action.kind === 'steer'
    ? [
        `Operator decision from ${artifactPath}`,
        `Decision: ${document.title} (${document.id})`,
        `Selected option: ${option.label} (${option.id})`,
        '',
        option.action.prompt,
        '',
        'Consequences acknowledged:',
        ...option.consequences.map((consequence) => `- ${consequence}`),
        ...(steer ? ['', 'Additional operator guidance:', steer] : []),
      ].join('\n')
    : reason;

  return {
    decisionId: document.id,
    optionId: option.id,
    optionLabel: option.label,
    artifactPath,
    action: option.action,
    prompt,
    reason,
  };
}

function parseDecisionValue(value: unknown): DecisionParseResult {
  if (!isRecord(value)) return fail('The decision contract must be a JSON object.');
  if (value['version'] !== 1) return fail('Unsupported decision contract version.');

  const id = requiredText(value, 'id');
  if (!id || !ID_RE.test(id)) return fail('Decision id is missing or invalid.');
  const title = requiredText(value, 'title');
  if (!title) return fail('Decision title is required.');
  const question = requiredText(value, 'question');
  if (!question) return fail('Decision question is required.');
  const context = optionalText(value, 'context');

  const rawOptions = value['options'];
  if (!Array.isArray(rawOptions) || rawOptions.length < 1 || rawOptions.length > 8) {
    return fail('Decision options must contain between one and eight entries.');
  }

  const options: DecisionOption[] = [];
  const ids = new Set<string>();
  for (const rawOption of rawOptions) {
    const parsed = parseOption(rawOption);
    if (typeof parsed === 'string') return fail(parsed);
    if (ids.has(parsed.id)) return fail(`Decision option id "${parsed.id}" is duplicated.`);
    ids.add(parsed.id);
    options.push(parsed);
  }

  const recommendation = parseRecommendation(value['recommendation'], ids);
  if (typeof recommendation === 'string') return fail(recommendation);
  const steer = parseSteer(value['steer']);
  if (typeof steer === 'string') return fail(steer);

  return {
    document: {
      version: 1,
      id,
      title,
      question,
      context,
      recommendation,
      options,
      steer,
    },
    error: null,
  };
}

function parseOption(value: unknown): DecisionOption | string {
  if (!isRecord(value)) return 'Each decision option must be an object.';
  const id = requiredText(value, 'id');
  if (!id || !ID_RE.test(id)) return 'A decision option id is missing or invalid.';
  const label = requiredText(value, 'label');
  if (!label) return `Decision option "${id}" requires a label.`;
  const summary = requiredText(value, 'summary');
  if (!summary) return `Decision option "${id}" requires a summary.`;
  const rawConsequences = value['consequences'];
  if (!Array.isArray(rawConsequences)) {
    return `Decision option "${id}" requires consequences.`;
  }
  const consequences = rawConsequences
    .filter((item): item is string => typeof item === 'string')
    .map((item) => item.trim())
    .filter(Boolean);
  if (consequences.length < 1) {
    return `Decision option "${id}" requires at least one consequence.`;
  }
  const action = parseAction(value['action'], id);
  if (typeof action === 'string') return action;
  return { id, label, summary, consequences, action };
}

function parseAction(value: unknown, optionId: string): DecisionAction | string {
  if (!isRecord(value)) return `Decision option "${optionId}" requires an action.`;
  if (value['kind'] === 'steer') {
    const prompt = requiredText(value, 'prompt');
    return prompt
      ? { kind: 'steer', prompt }
      : `Steer option "${optionId}" requires a prompt.`;
  }
  if (value['kind'] === 'move') {
    const targetState = requiredText(value, 'targetState');
    return targetState && MOVE_TARGET_SET.has(targetState)
      ? { kind: 'move', targetState: targetState as DecisionMoveTarget }
      : `Move option "${optionId}" targets a lane that is not allowed.`;
  }
  return `Decision option "${optionId}" has an unsupported action.`;
}

function parseRecommendation(
  value: unknown,
  optionIds: ReadonlySet<string>,
): DecisionDocument['recommendation'] | string {
  if (value == null) return null;
  if (!isRecord(value)) return 'Decision recommendation must be an object.';
  const optionId = requiredText(value, 'optionId');
  const reason = requiredText(value, 'reason');
  if (!optionId || !optionIds.has(optionId)) {
    return 'Decision recommendation references an unknown option.';
  }
  if (!reason) return 'Decision recommendation requires a reason.';
  return { optionId, reason };
}

function parseSteer(value: unknown): DecisionDocument['steer'] | string {
  if (value == null) {
    return {
      label: 'Additional guidance',
      placeholder: 'Optional constraints for the next run',
      required: false,
    };
  }
  if (!isRecord(value)) return 'Decision steer configuration must be an object.';
  const label = optionalText(value, 'label') ?? 'Additional guidance';
  const placeholder =
    optionalText(value, 'placeholder') ?? 'Optional constraints for the next run';
  return { label, placeholder, required: value['required'] === true };
}

function requiredText(value: Record<string, unknown>, key: string): string | null {
  const candidate = value[key];
  if (typeof candidate !== 'string') return null;
  const trimmed = candidate.trim();
  return trimmed ? trimmed : null;
}

function optionalText(value: Record<string, unknown>, key: string): string | null {
  return requiredText(value, key);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function fail(error: string): DecisionParseResult {
  return { document: null, error };
}

