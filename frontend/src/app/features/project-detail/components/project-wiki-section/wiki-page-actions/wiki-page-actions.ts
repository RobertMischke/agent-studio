import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { WikiClassification } from '../../../../../models/project-docs.model';
import {
  PageContext,
  derivePageType,
  pageExcerpt,
} from '../../../../../models/page-context.model';
import { NotificationService } from '../../../../../services/notification.service';
import { ProjectDocsService } from '../../../../../services/project-docs.service';
import { PageActionBarComponent } from '../../page-action-bar/page-action-bar';

/** Adapts the open Wiki reader state to the shared page-head action contract. */
@Component({
  selector: 'app-wiki-page-actions',
  standalone: true,
  imports: [PageActionBarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-page-actions.html',
  styleUrl: './wiki-page-actions.scss',
})
export class WikiPageActionsComponent {
  readonly projectName = input.required<string>();
  readonly relPath = input.required<string>();
  readonly title = input.required<string>();
  readonly content = input.required<string>();
  readonly classification = input<WikiClassification | null>(null);
  readonly registeredWorkbenchPaths = input<ReadonlySet<string>>(new Set());
  readonly archiveCompleted = output<void>();

  private readonly docs = inject(ProjectDocsService);
  private readonly notifications = inject(NotificationService);
  private readonly archivedOverrides = signal<ReadonlySet<string>>(new Set());
  readonly archiveBusy = signal(false);

  readonly context = computed<PageContext>(() => ({
    projectName: this.projectName(),
    relPath: this.relPath(),
    title: this.title(),
    pageType: derivePageType(
      this.relPath(),
      this.classification(),
      this.registeredWorkbenchPaths(),
    ),
    excerpt: pageExcerpt(this.content(), this.title()),
  }));

  readonly archived = computed(() =>
    this.archivedOverrides().has(this.relPath())
    || this.classification()?.status?.toLowerCase() === 'archived');

  archivePage(): void {
    const context = this.context();
    if (this.archiveBusy() || this.archived()) return;
    this.archivedOverrides.update(current => new Set(current).add(context.relPath));
    this.archiveBusy.set(true);
    this.docs.setWikiClassification(context.projectName, context.relPath, 'archived').subscribe({
      next: () => {
        this.archiveBusy.set(false);
        this.notifications.success(`Archived ${context.relPath}.`, 'Page classification');
        this.archiveCompleted.emit();
      },
      error: () => {
        this.archivedOverrides.update(current => {
          const next = new Set(current);
          next.delete(context.relPath);
          return next;
        });
        this.archiveBusy.set(false);
        this.notifications.error(`Could not archive ${context.relPath}.`, 'Page classification');
      },
    });
  }
}
