import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { WorkbenchDocument } from '../../../../models/project-docs.model';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { PageActionBarComponent } from '../page-action-bar/page-action-bar';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';
import { PageContext, pageExcerpt } from '../../../../models/page-context.model';
import {
  ISOLATED_HTML_LINK_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  resolveIsolatedHtmlNavigation,
} from '../../../../services/sandboxed-html.util';

/**
 * Trusted host chrome around repository-authored HTML. The artifact receives an
 * opaque origin (`allow-scripts` without `allow-same-origin`) and a deny-by-
 * default CSP. No credential, API, form, direct navigation, download, popup,
 * modal, or clipboard capability is bridged into the frame. Link clicks cross
 * one typed host boundary: docs-relative targets open in the Wiki and absolute
 * HTTP(S) targets open in a separate browser tab.
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
  readonly openWiki = output<string>();
  private readonly docs = inject(ProjectDocsService);
  private readonly frame = viewChild<ElementRef<HTMLIFrameElement>>('workbenchFrame');

  readonly document = signal<WorkbenchDocument | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly maximized = signal(false);

  readonly srcdoc = computed(() => {
    const document = this.document();
    return buildIsolatedHtmlSrcdoc(
      document?.html ?? '',
      document?.workbench.pattern === 'ui' ? 'ui' : 'concept',
    );
  });
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
      this.maximized.set(false);
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

  openCurrentPageInWiki(): void {
    const path = this.document()?.workbench.entryPath.replace(/^docs\//i, '');
    if (path) this.openWiki.emit(path);
  }

  @HostListener('window:message', ['$event'])
  onFrameMessage(event: MessageEvent): void {
    const frameWindow = this.frame()?.nativeElement.contentWindow;
    if (!frameWindow || event.source !== frameWindow) return;
    const message = event.data as { type?: unknown; href?: unknown } | null;
    if (message?.type !== ISOLATED_HTML_LINK_MESSAGE || typeof message.href !== 'string') return;
    const entryPath = this.document()?.workbench.entryPath;
    if (!entryPath) return;
    const navigation = resolveIsolatedHtmlNavigation(entryPath, message.href);
    if (navigation?.kind === 'wiki') {
      this.openWiki.emit(navigation.relPath);
    } else if (navigation?.kind === 'external') {
      window.open(navigation.url, '_blank', 'noopener,noreferrer');
    }
  }

  toggleMaximize(): void {
    this.maximized.update(value => !value);
  }

  @HostListener('document:keydown.escape')
  exitMaximized(): void {
    if (this.maximized()) this.maximized.set(false);
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
