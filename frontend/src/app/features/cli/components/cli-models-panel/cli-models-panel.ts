import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../../features/cli';
import { CliCatalogStore } from '../../../../services/cli-catalog.store';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';

interface CliModelGroup {
  cliType: CliType;
  label: string;
  icon: string;
  models: readonly CliModelInfo[];
  defaultModel: CliModelInfo | null;
}

/**
 * Per-CLI model catalog overview for the Admin/CLI page: each known CLI gets a
 * card listing its discovered models, with the default model and its default
 * thinking level called out. Data is the live `/api/cli/{type}/models` catalog,
 * read through the process-wide {@link CliCatalogStore} so the page reuses the
 * boot-time hydration instead of issuing its own per-CLI requests. The refresh
 * button forces a re-probe of one CLI's catalog (bypasses the store TTL).
 */
@Component({
  selector: 'app-cli-models-panel',
  standalone: true,
  imports: [],
  templateUrl: './cli-models-panel.html',
  styleUrl: './cli-models-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliModelsPanelComponent implements OnInit {
  private readonly catalog = inject(CliCatalogStore);

  /** One group per known CLI. Recomputes when the store's catalog map updates. */
  readonly groups = computed<CliModelGroup[]>(() =>
    CLI_TYPES.map((cliType) => {
      const models = this.catalog.modelsFor(cliType);
      return {
        cliType,
        label: cliTypeLabel(cliType),
        icon: cliTypeIcon(cliType),
        models,
        defaultModel: models.find((m) => m.isDefault) ?? null,
      };
    }),
  );

  ngOnInit(): void {
    this.catalog.hydrateAll();
  }

  refresh(cliType: CliType): void {
    this.catalog.refresh(cliType).subscribe({ error: () => void 0 });
  }

  thinkingSummary(m: CliModelInfo): string {
    return m.thinkingLevels?.length ? m.thinkingLevels.join(' · ') : '';
  }
}
