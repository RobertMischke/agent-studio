import type { MenuItem } from '../../../../components/menu';
import type { CliType } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../cli';

export function buildCliMenuItems(input: {
  cliTypes: readonly CliType[];
  defaultCli: CliType;
  cliLabel: (t: CliType) => string;
  cliIcon: (t: CliType) => string;
}): readonly MenuItem[] {
  const items: MenuItem[] = [
    { kind: 'header', label: 'Default CLI for new tasks' },
  ];
  for (const t of input.cliTypes) {
    items.push({
      kind: 'row',
      id: `cli:${t}`,
      label: input.cliLabel(t),
      icon: input.cliIcon(t),
      active: t === input.defaultCli,
    });
  }
  return items;
}

export function buildModelMenuItems(input: {
  defaultCli: CliType;
  defaultModel: string;
  models: readonly CliModelInfo[];
  modelsLoading: boolean;
  modelsError: boolean;
  cliLabel: (t: CliType) => string;
}): readonly MenuItem[] {
  const items: MenuItem[] = [
    { kind: 'header', label: `Default model · ${input.cliLabel(input.defaultCli)}` },
    {
      kind: 'row',
      id: 'model:__default__',
      label: 'CLI default',
      icon: '·',
      active: !input.defaultModel,
    },
  ];

  if (input.modelsLoading) {
    items.push({
      kind: 'row',
      id: 'model:__loading__',
      label: 'Loading catalog…',
      disabled: true,
    });
  } else if (input.models.length === 0) {
    const msg = input.modelsError
      ? `Catalog unavailable for ${input.cliLabel(input.defaultCli)}.`
      : `No models reported for ${input.cliLabel(input.defaultCli)}.`;
    items.push({
      kind: 'row',
      id: 'model:__empty__',
      label: msg,
      disabled: true,
    });
  } else {
    for (const m of input.models) {
      items.push({
        kind: 'row',
        id: `model:${m.id}`,
        label: m.label || m.id,
        icon: m.isDefault ? '★' : '·',
        active: m.id === input.defaultModel,
        tooltip: m.id,
      });
    }
  }

  items.push({ kind: 'separator' });
  items.push({
    kind: 'row',
    id: 'model:__refresh__',
    label: 'Refresh catalog',
    icon: '↻',
    disabled: input.modelsLoading,
  });

  return items;
}

export function cliTypeFromMenuId(id: string): CliType | null {
  if (!id.startsWith('cli:')) return null;
  return id.slice(4) as CliType;
}

export function modelIdFromMenuId(id: string): string | null {
  if (!id.startsWith('model:')) return null;
  const val = id.slice(6);
  if (val === '__default__') return '';
  if (val === '__loading__' || val === '__empty__' || val === '__refresh__') return null;
  return val;
}

export function isRefreshAction(id: string): boolean {
  return id === 'model:__refresh__';
}
