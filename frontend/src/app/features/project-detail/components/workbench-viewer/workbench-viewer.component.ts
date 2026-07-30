import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { WorkbenchDocument } from '../../../../models/project-docs.model';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { PageActionBarComponent } from '../page-action-bar/page-action-bar';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';
import { PageContext, pageExcerpt } from '../../../../models/page-context.model';
import { buildIsolatedHtmlSrcdoc } from '../../../../services/sandboxed-html.util';

/**
 * Trusted host chrome around repository-authored HTML. The artifact receives an
 * opaque origin (`allow-scripts` without `allow-same-origin`) and a deny-by-
 * default CSP. No credential, API, form, navigation, download, popup, modal, or
 * clipboard capability is bridged into the frame. Future chat pinning and
 * decision actions attach to the typed document signal in host chrome only.
 */
@Component({
  selector: 'app-workbench-viewer',
  standalone: true,
  imports: [PageActionBarComponent, StudioIconComponent, WorkbenchDecisionPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer.component.html',
  styleUrl: './workbench-viewer.component.scss',
})
export class WorkbenchViewerComponent {
  readonly projectName = input.required<string>();
  readonly workbenchId = input.required<string>();
  private readonly docs = inject(ProjectDocsService);
  private readonly frame = viewChild<ElementRef<HTMLIFrameElement>>('workbenchFrame');

  readonly document = signal<WorkbenchDocument | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly srcdoc = computed(() => buildIsolatedHtmlSrcdoc(this.document()?.html ?? ''));
  readonly pageContext = computed<PageContext | null>(() => {
    const document = this.document();
    if (!document) return null;
    return {
      projectName: this.projectName(),
      relPath: document.workbench.entryPath.replace(/^docs\//i, ''),
      title: document.workbench.title,
      pageType: 'workbench',
      excerpt: pageExcerpt(document.html, document.workbench.summary),
    };
  });

  constructor() {
    effect(() => {
      const frame = this.frame();
      // This is the single audited HTML sink. The fixed wrapper is assigned as
      // a native string so Angular cannot remove the artifact's inline scripts.
      if (frame) frame.nativeElement.srcdoc = this.srcdoc();
    });
    effect(() => {
      const project = this.projectName();
      const id = this.workbenchId();
      this.loadDocument(project, id);
    });
  }

  statusLabel(): string {
    const workbench = this.document()?.workbench;
    if (!workbench) return '';
    return workbench.status === 'active'
      ? workbench.phase ?? workbench.status
      : workbench.status;
  }

  refreshDecision(): void {
    this.loadDocument(this.projectName(), this.workbenchId(), false);
  }

  private loadDocument(project: string, id: string, clear = true): void {
    this.loading.set(true);
    this.error.set(null);
    if (clear) this.document.set(null);
    this.docs.getWorkbench(project, id).subscribe({
      next: document => {
        this.document.set(document);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Workbench could not be loaded.');
        this.loading.set(false);
      },
    });
  }
}
