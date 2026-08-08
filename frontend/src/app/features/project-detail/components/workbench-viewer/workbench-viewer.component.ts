import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, computed, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { WorkbenchDecisionAnswer, WorkbenchDocument } from '../../../../models/project-docs.model';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';
import {
  ISOLATED_HTML_LINK_MESSAGE,
  resolveIsolatedHtmlNavigation,
} from '../../../../services/sandboxed-html.util';
import {
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_THEME_MESSAGE,
  buildWorkbenchDecisionSrcdoc,
  normalizeWorkbenchDecisionAnswers,
  parseWorkbenchDecisionPoints,
} from './workbench-decision-markup';

/**
 * Trusted host chrome around repository-authored HTML. The artifact receives an
 * opaque origin (`allow-scripts` without `allow-same-origin`) and a deny-by-
 * default CSP. No credential, API, form, direct navigation, download, popup,
 * modal, or clipboard capability is bridged into the frame. Link clicks and
 * declared decision drafts cross typed host boundaries. Decision ids are
 * checked against an inert parse before host state changes; mutations remain
 * in trusted chrome.
 */
@Component({
  selector: 'app-workbench-viewer',
  standalone: true,
  imports: [StudioIconComponent, WorkbenchDecisionPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer.component.html',
  styleUrl: './workbench-viewer.component.scss',
})
export class WorkbenchViewerComponent {
  readonly projectName = input.required<string>();
  readonly workbenchId = input.required<string>();
  readonly openWiki = output<string>();
  private readonly docs = inject(ProjectDocsService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly frame = viewChild<ElementRef<HTMLIFrameElement>>('workbenchFrame');

  readonly document = signal<WorkbenchDocument | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly maximized = signal(false);
  readonly inlineAnswers = signal<WorkbenchDecisionAnswer[]>([]);
  readonly studioTheme = signal<'light' | 'dark'>(readStudioTheme());

  readonly decisionPoints = computed(() => parseWorkbenchDecisionPoints(this.document()?.html ?? ''));
  readonly decisionInteractionDisabled = computed(() => {
    const document = this.document();
    if (!document) return true;
    const settled = document.workbench.decision?.state === 'succeeded'
      || document.workbench.status === 'decided'
      || document.workbench.status === 'archived';
    const ready = document.workbench.phase === 'decision-ready'
      || document.workbench.decision !== null && document.workbench.decision !== undefined;
    return settled || !ready || document.workingTreeModified
      || (!document.revision && !document.fingerprint);
  });
  readonly sourceAnswers = computed(() => {
    const points = this.decisionPoints();
    return normalizeWorkbenchDecisionAnswers(
      points,
      this.document()?.workbench.decision?.answers ?? [],
    ) ?? [];
  });
  readonly srcdoc = computed(() => buildWorkbenchDecisionSrcdoc(
    this.document()?.html ?? '',
    this.decisionPoints(),
    this.sourceAnswers(),
    this.decisionInteractionDisabled(),
    untracked(() => this.studioTheme()),
  ));

  constructor() {
    const themeObserver = new MutationObserver(() => this.studioTheme.set(readStudioTheme()));
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-studio-theme'],
    });
    this.destroyRef.onDestroy(() => themeObserver.disconnect());
    effect(() => {
      const frame = this.frame();
      // This is the single audited HTML sink. The fixed wrapper is assigned as
      // a native string so Angular cannot remove the artifact's inline scripts.
      if (frame) frame.nativeElement.srcdoc = this.srcdoc();
    });
    effect(() => {
      const theme = this.studioTheme();
      this.frame()?.nativeElement.contentWindow?.postMessage({
        type: WORKBENCH_THEME_MESSAGE,
        theme,
      }, '*');
    });
    effect(() => {
      const project = this.projectName();
      const id = this.workbenchId();
      this.maximized.set(false);
      this.loadDocument(project, id);
    });
  }

  openCurrentPageInWiki(): void {
    const path = this.document()?.workbench.entryPath.replace(/^docs\//i, '');
    if (path) this.openWiki.emit(path);
  }

  @HostListener('window:message', ['$event'])
  onFrameMessage(event: MessageEvent): void {
    const frameWindow = this.frame()?.nativeElement.contentWindow;
    if (!frameWindow || event.source !== frameWindow) return;
    const message = event.data as { type?: unknown; href?: unknown; answers?: unknown } | null;
    if (message?.type === WORKBENCH_DECISION_CHANGE_MESSAGE) {
      if (this.decisionInteractionDisabled()) return;
      const answers = normalizeWorkbenchDecisionAnswers(this.decisionPoints(), message.answers);
      if (answers) this.inlineAnswers.set(answers);
      return;
    }
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
        this.inlineAnswers.set(normalizeWorkbenchDecisionAnswers(
          parseWorkbenchDecisionPoints(document.html),
          document.workbench.decision?.answers ?? [],
        ) ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Workbench could not be loaded.');
        this.loading.set(false);
      },
    });
  }
}

function readStudioTheme(): 'light' | 'dark' {
  return document.documentElement.dataset['studioTheme'] === 'dark' ? 'dark' : 'light';
}
