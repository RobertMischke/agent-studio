import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ConceptHelpComponent } from '../../../components/concept-help/concept-help.component';

/**
 * Project-level "Check skill readiness" surface (docs/skills-architecture.md
 * "First Product Step"). Renders a section with a single button on the
 * project detail panel; clicking it opens an inline modal that:
 *
 *  - lists the skills the task processor knows about for this project,
 *    split into Standard (suggested) vs Project-specific (selected),
 *  - reports whether the watched project's README / AGENTS exposes the
 *    expected skill lookup section (pass / warning / fail),
 *  - offers a one-click "Create README update task" that queues a normal
 *    task in the project's 2-ready lane.
 *
 * No call here mutates the watched project's source tree directly; the
 * fix path goes through the regular task queue (see
 * SkillReadinessService.CreateFixTask on the backend).
 */

type SkillReadinessStatus = 'pass' | 'warning' | 'fail';

interface SkillReadinessFile {
  relPath: string;
  fullPath: string;
  exists: boolean;
  headingFound: boolean;
}

interface SkillReadinessReport {
  projectName: string;
  status: SkillReadinessStatus;
  summary: string;
  checkedFiles: SkillReadinessFile[];
  matchedFile: string | null;
  heading: string | null;
  matchedPhrases: string[];
  missingPhrases: string[];
}

interface SkillEntry {
  id: string;
  name: string;
  description: string;
  category: 'standard' | 'projectSpecific';
  selection: 'selected' | 'suggested';
  relPath: string;
}

interface SkillCatalog {
  projectName: string;
  standard: SkillEntry[];
  projectSpecific: SkillEntry[];
}

interface SkillReadinessFixTaskResult {
  jobId: string;
  watchPath: string;
  targetState: string;
  title: string;
}

@Component({
  selector: 'app-project-skill-readiness-section',
  standalone: true,
  imports: [ConceptHelpComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-skill-readiness-section.html',
  styleUrl: './project-skill-readiness-section.scss'
})
export class ProjectSkillReadinessSectionComponent {
  readonly projectName = input.required<string>();

  private readonly http = inject(HttpClient);

  readonly open = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly report = signal<SkillReadinessReport | null>(null);
  readonly catalog = signal<SkillCatalog | null>(null);
  readonly creating = signal(false);
  readonly createError = signal<string | null>(null);
  readonly createdJobId = signal<string | null>(null);

  // Mirror of the latest report for the closed-modal status badge.
  readonly lastReport = computed(() => this.report());

  openModal(): void {
    this.open.set(true);
    this.error.set(null);
    this.createError.set(null);
    this.createdJobId.set(null);
    this.fetchAll();
  }

  closeModal(): void {
    this.open.set(false);
  }

  statusLabel(status: SkillReadinessStatus): string {
    switch (status) {
      case 'pass': return 'Pass';
      case 'warning': return 'Warning';
      case 'fail': return 'Fail';
    }
  }

  createFixTask(): void {
    const project = this.projectName();
    this.creating.set(true);
    this.createError.set(null);
    this.createdJobId.set(null);
    this.http.post<SkillReadinessFixTaskResult>(
      `/api/projects/${encodeURIComponent(project)}/skill-readiness/fix-task`,
      {}
    ).subscribe({
      next: (res) => {
        this.creating.set(false);
        this.createdJobId.set(res.jobId);
      },
      error: (err) => {
        this.creating.set(false);
        this.createError.set(err?.error?.error || err?.message || 'Failed to queue task');
      }
    });
  }

  private fetchAll(): void {
    const project = this.projectName();
    this.loading.set(true);
    this.report.set(null);
    this.catalog.set(null);

    this.http.get<SkillReadinessReport>(
      `/api/projects/${encodeURIComponent(project)}/skill-readiness`
    ).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: (err) => {
        this.loading.set(false);
        this.error.set(err?.error?.error || err?.message || 'Failed to check skill readiness');
      }
    });

    this.http.get<SkillCatalog>(
      `/api/projects/${encodeURIComponent(project)}/skills`
    ).subscribe({
      next: (c) => this.catalog.set(c),
      error: () => this.catalog.set({ projectName: project, standard: [], projectSpecific: [] })
    });
  }
}
