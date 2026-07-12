import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { WorkbenchDocument } from '../../../../models/project-docs.model';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

const WORKBENCH_CSP = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'; media-src data:; object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; form-action 'none'; base-uri 'none'";

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
  private readonly sanitizer = inject(DomSanitizer);

  readonly document = signal<WorkbenchDocument | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly srcdoc = computed<SafeHtml>(() => {
    const html = this.document()?.html ?? '';
    if (!html) return this.sanitizer.bypassSecurityTrustHtml('');
    const policy = `<meta http-equiv="Content-Security-Policy" content="${WORKBENCH_CSP}"><base href="about:blank">`;
    const isolated = /<head[\s>]/i.test(html)
      ? html.replace(/<head([^>]*)>/i, `<head$1>${policy}`)
      : `<!doctype html><html><head>${policy}</head><body>${html}</body></html>`;
    return this.sanitizer.bypassSecurityTrustHtml(isolated);
  });

  constructor() {
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
