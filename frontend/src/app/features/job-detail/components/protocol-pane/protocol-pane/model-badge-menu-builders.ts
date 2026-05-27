/**
 * F44 — pure helpers for the chat-composer model badge.
 *
 * The badge renders a subtle "{cliIcon} {short-model} ▾" affordance next to
 * the chat composer in the protocol pane. Click / right-click opens an
 * <app-menu> built from these helpers; the menu lists every available model
 * for the active CLI (current row marked active) and the four CLI types
 * (current row marked active). Selecting a model emits `modelChange`;
 * selecting a CLI emits `cliTypeChange`.
 *
 * The helpers are pure so the protocol-pane component can stay small and
 * the menu shape is unit-testable without instantiating Angular.
 */
import type { CliModelInfo } from '../../../../cli';
import type { CliType } from '../../../../../models/job.model';
import type { MenuItem } from '../../../../../components/menu';
import { shortModelName as _shortModelName } from '../../../../../services/format.util';

export interface ModelMenuInputs {
  cliType: CliType | null;
  model: string | null;
  availableModels: readonly CliModelInfo[];
  cliTypes: readonly CliType[];
  cliTypeLabel: (t: CliType) => string;
  cliTypeIcon: (t: CliType) => string;
}

/**
 * Menu ids use a short prefix so the click handler can route by source
 * without parsing model ids that may contain dashes themselves.
 *   model:<id>     pick a model for the current CLI
 *   model:__default__  clear back to the CLI default
 *   cli:<type>     switch CLI (resets the model)
 */
export const MODEL_MENU_DEFAULT_ID = 'model:__default__';

export function isModelMenuId(id: string): boolean {
  return id.startsWith('model:');
}

export function isCliMenuId(id: string): boolean {
  return id.startsWith('cli:');
}

export function modelIdFromMenuId(id: string): string | null {
  if (!id.startsWith('model:')) return null;
  const rest = id.slice('model:'.length);
  return rest === '__default__' ? '' : rest;
}

export function cliTypeFromMenuId(id: string): CliType | null {
  if (!id.startsWith('cli:')) return null;
  return id.slice('cli:'.length) as CliType;
}

export function buildModelMenuItems(input: ModelMenuInputs): readonly MenuItem[] {
  const items: MenuItem[] = [];

  const headerLabel = currentBadgeText(input);
  items.push({ kind: 'header', label: `Current: ${headerLabel}` });

  // Model section. Always show, even with an empty catalog so the user
  // sees the "no models available" affordance and can still flip CLIs.
  items.push({ kind: 'header', label: 'Model' });

  items.push({
    kind: 'row',
    id: MODEL_MENU_DEFAULT_ID,
    label: '(CLI default)',
    active: !input.model,
  });

  for (const m of input.availableModels) {
    items.push({
      kind: 'row',
      id: `model:${m.id}`,
      label: m.label || m.id,
      hint: m.isDefault ? 'default' : undefined,
      active: input.model === m.id,
    });
  }

  // CLI section.
  items.push({ kind: 'separator' });
  items.push({ kind: 'header', label: 'CLI' });
  for (const t of input.cliTypes) {
    items.push({
      kind: 'row',
      id: `cli:${t}`,
      label: `${input.cliTypeIcon(t)}  ${input.cliTypeLabel(t)}`,
      active: input.cliType === t,
    });
  }

  return items;
}

/**
 * Compact text shown on the chat-composer badge itself. Short, dimmed,
 * 11-12 px — operator wants subtle. Falls back to "No model" when the job
 * has neither a CLI nor a model set yet (rare; usually a CLI is always
 * implied). Delegates to the shared `shortModelName` in format.util.ts.
 */
export function shortModelName(model: string | null | undefined): string {
  return _shortModelName(model);
}

/**
 * The header line in the menu and the badge tooltip both want a humanised
 * "{cli} · {model}" representation. Falls back gracefully when either side
 * is missing.
 */
export function currentBadgeText(input: Pick<ModelMenuInputs, 'cliType' | 'model' | 'cliTypeLabel'>): string {
  const cli = input.cliType ? input.cliTypeLabel(input.cliType) : 'no CLI';
  const model = input.model && input.model.trim() ? input.model.trim() : 'CLI default';
  return `${cli} · ${model}`;
}

export function modelBadgeTooltip(
  input: Pick<ModelMenuInputs, 'cliType' | 'model' | 'cliTypeLabel'>,
  disabledReason: string | null,
): string {
  const base = currentBadgeText(input);
  if (disabledReason) return `${base}\n${disabledReason}`;
  return `${base} — click or right-click to change`;
}
