import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { WorkbenchDocument } from '../../../../models/project-docs.model';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';

export const WORKBENCH_CSP = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'; media-src data:; object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; form-action 'none'; base-uri 'none'";

/**
 * Parses repository HTML in an inert document, then moves it into a fixed
 * wrapper whose CSP and base elements are always the first nodes in `head`.
 * DOMParser normalises missing, duplicated, or deliberately misplaced head
 * tags without executing their scripts. The iframe parses the returned string
 * only after the policy is in place.
 */
export function buildWorkbenchSrcdoc(html: string): string {
  if (!html) return '';
  const parser = new DOMParser();
  const artifact = parser.parseFromString(html, 'text/html');
  const wrapper = parser.parseFromString('<!doctype html><html><head></head><body></body></html>', 'text/html');

  for (const control of Array.from(artifact.querySelectorAll('base, meta'))) {
    if (isArtifactSecurityControl(control)) control.remove();
  }

  const policy = wrapper.createElement('meta');
  policy.httpEquiv = 'Content-Security-Policy';
  policy.content = WORKBENCH_CSP;
  const base = wrapper.createElement('base');
  base.href = 'about:blank';
  wrapper.head.append(policy, base);

  copyAttributes(artifact.documentElement, wrapper.documentElement);
  copyAttributes(artifact.head, wrapper.head);
  copyAttributes(artifact.body, wrapper.body);
  for (const node of Array.from(artifact.head.childNodes))
    wrapper.head.append(wrapper.importNode(node, true));
  for (const node of Array.from(artifact.body.childNodes))
    wrapper.body.append(wrapper.importNode(node, true));

  // base=about:blank neutralises navigation, but that also breaks in-page
  // anchors: a plain "#section" click navigates the frame to about:blank and
  // blanks it. Re-implement anchor clicks as scrolling; swallow every other
  // link so nothing can blank the frame.
  const nav = wrapper.createElement('script');
  nav.textContent = `document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) return;
    var href = a.getAttribute('href') || '';
    e.preventDefault();
    if (href.charAt(0) === '#') {
      var el = document.getElementById(href.slice(1))
        || document.querySelector('a[name="' + href.slice(1).replace(/"/g, '') + '"]');
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }, true);`;
  wrapper.body.append(nav);

  return `<!doctype html>${wrapper.documentElement.outerHTML}`;
}

function copyAttributes(source: Element, target: Element): void {
  for (const attribute of Array.from(source.attributes))
    target.setAttribute(attribute.name, attribute.value);
}

function isArtifactSecurityControl(node: Node): boolean {
  if (!(node instanceof HTMLBaseElement || node instanceof HTMLMetaElement)) return false;
  if (node instanceof HTMLBaseElement) return true;
  const httpEquiv = node.httpEquiv.trim().toLowerCase();
  return httpEquiv === 'content-security-policy'
    || httpEquiv === 'content-security-policy-report-only'
    || httpEquiv === 'refresh';
}

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
  imports: [StudioIconComponent],
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

  readonly srcdoc = computed(() => buildWorkbenchSrcdoc(this.document()?.html ?? ''));

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
      this.loading.set(true);
      this.error.set(null);
      this.document.set(null);
      this.docs.getWorkbench(project, id).subscribe({
        next: document => { this.document.set(document); this.loading.set(false); },
        error: () => { this.error.set('Workbench could not be loaded.'); this.loading.set(false); },
      });
    });
  }

  statusLabel(): string {
    const workbench = this.document()?.workbench;
    return workbench?.phase ?? workbench?.status ?? '';
  }
}
