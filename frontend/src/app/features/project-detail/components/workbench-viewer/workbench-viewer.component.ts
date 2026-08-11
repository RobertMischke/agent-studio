import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import {
  WorkbenchDecisionResponse,
  WorkbenchDocument,
  DocumentWorkbenchResult,
} from '../../../../models/project-docs.model';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { WorkbenchViewerHeaderComponent } from '../workbench-viewer-header/workbench-viewer-header.component';
import {
  ISOLATED_HTML_LINK_MESSAGE,
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_DECISION_HYDRATE_MESSAGE,
  WORKBENCH_DECISION_READY_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  resolveIsolatedHtmlNavigation,
} from '../../../../services/sandboxed-html.util';
import {
  discoverWorkbenchDecisionMarkup,
  normalizeWorkbenchDecisionResponses,
} from '../../../../services/workbench-decision-markup.util';

/**
 * Trusted host chrome around repository-authored HTML. The artifact receives an
 * opaque origin (`allow-scripts` without `allow-same-origin`) and a deny-by-
 * default CSP. No credential, API, form, direct navigation, download, popup,
 * modal, or clipboard capability is bridged into the frame. Link clicks cross
 * one typed host boundary: registered Dossier targets stay in the viewer,
 * other docs-relative targets open in the Wiki, and absolute HTTP(S) targets
 * open in a separate browser tab.
 */
@Component({
  selector: 'app-workbench-viewer',
  standalone: true,
  imports: [PendingButtonDirective, StudioIconComponent, WorkbenchViewerHeaderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer.component.html',
  styleUrl: './workbench-viewer.component.scss',
})
export class WorkbenchViewerComponent {
  readonly projectName = input.required<string>();
  readonly workbenchId = input.required<string>();
  readonly pagePath = input<string | null>(null);
  readonly showWikiAction = input(true);
  readonly openWiki = output<string>();
  private readonly docs = inject(ProjectDocsService);
  private readonly http = inject(HttpClient);
  private readonly hub = inject(JobsHubClient);
  private readonly frame = viewChild<ElementRef<HTMLIFrameElement>>('workbenchFrame');
  private requestGeneration = 0;

  readonly document = signal<WorkbenchDocument | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly maximized = signal(false);
  readonly decisionResponses = signal<WorkbenchDecisionResponse[]>([]);
  readonly documenting = signal(false);
  readonly documentationError = signal<string | null>(null);
  readonly activePagePath = signal<string | null>(null);

  readonly pageNavigation = computed(() => {
    const document = this.document();
    if (!document) return [];
    return [
      { title: 'Overview', path: null as string | null },
      ...(document.pages ?? []).map(page => ({ title: page.title, path: page.path })),
    ];
  });
  readonly activePageTitle = computed(() =>
    this.pageNavigation().find(page => page.path === this.activePagePath())?.title ?? 'Overview',
  );
  readonly currentHtml = computed(() => {
    const document = this.document();
    if (!document) return '';
    const path = this.activePagePath();
    return path === null
      ? document.html
      : document.pages?.find(page => page.path === path)?.html ?? document.html;
  });

  readonly srcdoc = computed(() => {
    const document = this.document();
    return buildIsolatedHtmlSrcdoc(this.currentHtml(), {
      workbenchDecisions: true,
      documentPattern: document?.workbench.pattern === 'ui' ? 'ui' : 'concept',
    });
  });
  readonly decisionMarkup = computed(() =>
    discoverWorkbenchDecisionMarkup(this.document()?.html ?? ''),
  );

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
      this.activePagePath.set(null);
      this.loadDocument(project, id);
    });
    effect(() => {
      this.projectName();
      this.workbenchId();
      const pagePath = this.pagePath();
      this.activePagePath.set(pagePath ?? null);
    });
    effect(() => {
      const event = this.hub.workbenchEvent();
      if (!event) return;
      const project = this.projectName();
      const id = this.workbenchId();
      if (event.projectName && event.projectName !== project) return;
      if (event.workbenchId && event.workbenchId !== id) return;
      this.loadDocument(project, id, false);
    });
  }

  openCurrentPageInWiki(): void {
    const path = this.currentRepositoryPath()?.replace(/^docs\//i, '');
    if (path) this.openWiki.emit(path);
  }

  selectPage(path: string | null): void {
    if (!this.pageNavigation().some(page => page.path === path)) return;
    this.activePagePath.set(path);
  }

  @HostListener('window:message', ['$event'])
  onFrameMessage(event: MessageEvent): void {
    const frameWindow = this.frame()?.nativeElement.contentWindow;
    if (!frameWindow || event.source !== frameWindow) return;
    const message = event.data as {
      type?: unknown;
      href?: unknown;
      responses?: unknown;
    } | null;
    if (message?.type === WORKBENCH_DECISION_READY_MESSAGE) {
      this.hydrateFrame();
      return;
    }
    if (message?.type === WORKBENCH_DECISION_CHANGE_MESSAGE) {
      if (this.document()?.workbench.decision?.state === 'succeeded') return;
      const normalized = normalizeWorkbenchDecisionResponses(
        message.responses,
        this.decisionMarkup().points,
      );
      if (normalized) this.decisionResponses.set(normalized);
      return;
    }
    if (message?.type !== ISOLATED_HTML_LINK_MESSAGE || typeof message.href !== 'string') return;
    const entryPath = this.currentRepositoryPath();
    if (!entryPath) return;
    const navigation = resolveIsolatedHtmlNavigation(entryPath, message.href);
    if (navigation?.kind === 'wiki') {
      const registeredPage = this.registeredPagePath(navigation.relPath);
      if (registeredPage.matched) {
        this.activePagePath.set(registeredPage.path);
        return;
      }
      this.openWiki.emit(navigation.relPath);
    } else if (navigation?.kind === 'external') {
      window.open(navigation.url, '_blank', 'noopener,noreferrer');
    }
  }

  toggleMaximize(): void {
    this.maximized.update((value) => !value);
  }

  @HostListener('document:keydown.escape')
  exitMaximized(): void {
    if (this.maximized()) this.maximized.set(false);
  }

  refreshDecision(): void {
    this.loadDocument(this.projectName(), this.workbenchId(), false);
  }

  documentationReady(): boolean {
    const workbench = this.document()?.workbench;
    return workbench?.status === 'decided' && workbench.documentation?.eligible === true;
  }

  documentCurrent(): void {
    const document = this.document();
    if (!document || !this.documentationReady() || this.documenting()) return;
    this.documenting.set(true);
    this.documentationError.set(null);
    const project = encodeURIComponent(this.projectName());
    const id = encodeURIComponent(document.workbench.id);
    this.http.post<DocumentWorkbenchResult>(`/api/projects/${project}/workbenches/${id}/document`, {
        actor: 'Operator',
        expectedRevision: document.revision,
        expectedFingerprint: document.fingerprint,
      }).subscribe({
      next: () => {
        this.documenting.set(false);
        this.loadDocument(this.projectName(), this.workbenchId(), false);
      },
      error: error => {
        this.documenting.set(false);
        this.documentationError.set(error?.error?.error || 'The lifecycle could not be updated.');
      },
    });
  }

  onFrameLoaded(): void {
    this.hydrateFrame();
  }

  private hydrateFrame(): void {
    const decision = this.document()?.workbench.decision;
    this.frame()?.nativeElement.contentWindow?.postMessage(
      {
        type: WORKBENCH_DECISION_HYDRATE_MESSAGE,
        responses: this.decisionResponses(),
        readonly: decision?.state === 'succeeded',
      },
      '*',
    );
  }

  private loadDocument(project: string, id: string, clear = true): void {
    const generation = ++this.requestGeneration;
    this.loading.set(true);
    this.error.set(null);
    this.documentationError.set(null);
    if (clear) this.document.set(null);
    this.docs.getWorkbench(project, id).subscribe({
      next: (document) => {
        if (generation !== this.requestGeneration) return;
        this.document.set(document);
        if (!this.pageNavigation().some(page => page.path === this.activePagePath()))
          this.activePagePath.set(null);
        const discovered = discoverWorkbenchDecisionMarkup(document.html);
        this.decisionResponses.set(document.workbench.decision?.responses ?? discovered.responses);
        this.loading.set(false);
      },
      error: response => {
        if (generation !== this.requestGeneration) return;
        this.error.set(workbenchLoadError(response));
        this.loading.set(false);
      },
    });
  }

  private currentRepositoryPath(): string | null {
    const entryPath = this.document()?.workbench.entryPath;
    if (!entryPath) return null;
    const pagePath = this.activePagePath();
    if (!pagePath) return entryPath;
    const separator = entryPath.lastIndexOf('/');
    return `${separator < 0 ? '' : entryPath.slice(0, separator + 1)}${pagePath}`;
  }

  private registeredPagePath(relPath: string): { matched: boolean; path: string | null } {
    const document = this.document();
    const entryPath = document?.workbench.entryPath;
    if (!document || !entryPath) return { matched: false, path: null };
    const repositoryPath = `docs/${relPath}`;
    if (repositoryPath === entryPath) return { matched: true, path: null };
    const separator = entryPath.lastIndexOf('/');
    const dossierRoot = separator < 0 ? '' : entryPath.slice(0, separator + 1);
    const page = document.pages?.find(candidate => `${dossierRoot}${candidate.path}` === repositoryPath);
    return page
      ? { matched: true, path: page.path }
      : { matched: false, path: null };
  }
}

function workbenchLoadError(response: unknown): string {
  const candidate = response as {
    error?: unknown;
  } | null;
  const payload = candidate?.error;
  const reason = typeof payload === 'string'
    ? payload.trim()
    : typeof payload === 'object' && payload !== null && 'error' in payload
      ? String((payload as { error?: unknown }).error ?? '').trim()
      : '';
  return reason
    ? `Dossier could not be loaded: ${reason}`
    : 'Dossier could not be loaded. The server did not provide a reason.';
}
