import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { ProjectStyleGuideCatalogue } from '../../../../models/project-docs.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';

/**
 * Compact Wiki Pulse projection of the repository style-guide catalogue.
 * Selection stays a parent-owned navigation action so this component carries
 * no second document-loading path.
 */
@Component({
  selector: 'app-project-style-guides-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-style-guides-panel.html',
  styleUrl: './project-style-guides-panel.scss',
})
export class ProjectStyleGuidesPanelComponent {
  readonly projectName = input.required<string>();
  readonly openGuide = output<string>();
  readonly catalogue = signal<ProjectStyleGuideCatalogue | null>(null);

  private readonly docs = inject(ProjectDocsService);

  constructor() {
    effect(() => {
      const project = this.projectName();
      if (!project) return;
      this.docs.getProjectStyleGuides(project).subscribe({
        next: catalogue => this.catalogue.set(catalogue),
        error: () => this.catalogue.set(null),
      });
    });
  }
}
