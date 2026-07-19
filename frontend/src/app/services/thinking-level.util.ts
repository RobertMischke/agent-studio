import type { CliExecution } from '../models/task.model';

export interface ThinkingLevelIndicator {
  short: string;
  effective: string;
  configured: string | null;
  defaultLevel: string | null;
  differsFromConfigured: boolean;
  differsFromDefault: boolean;
  tooltip: string;
}

const SHORT_LEVELS: Record<string, string> = {
  low: 'l',
  medium: 'm',
  high: 'h',
  xhigh: 'x',
  ultra: 'u',
};

/**
 * Resolves the level shown beside a task's model. The last execution is the
 * source of truth because it records the normalized level actually passed to
 * the CLI. Before the first start, the configured task/client default is used.
 */
export function buildThinkingLevelIndicator(
  execution: CliExecution | null | undefined,
  configuredLevel: string | null | undefined,
  defaultLevel: string | null | undefined,
  model: string | null | undefined,
): ThinkingLevelIndicator | null {
  const configured = clean(configuredLevel);
  const defaultValue = clean(defaultLevel);
  const effective = clean(execution?.thinkingLevel) ?? configured ?? defaultValue;
  if (!effective) return null;

  const differsFromConfigured = configured !== null && effective !== configured;
  const differsFromDefault = defaultValue !== null && effective !== defaultValue;
  const lines = [`Effective thinking level: ${effective}`, `Model: ${execution?.model || model || 'CLI default'}`];
  if (differsFromConfigured) lines.push(`Configured thinking level: ${configured}`);
  if (differsFromDefault) lines.push(`Default thinking level: ${defaultValue}`);

  return {
    short: SHORT_LEVELS[effective] ?? effective.charAt(0),
    effective,
    configured,
    defaultLevel: defaultValue,
    differsFromConfigured,
    differsFromDefault,
    tooltip: lines.join('\n'),
  };
}

function clean(value: string | null | undefined): string | null {
  const normalized = value?.trim().toLowerCase();
  return normalized || null;
}
