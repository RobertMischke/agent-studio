import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { JobService } from '../../../../services/job.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import type { JobInfo } from '../../../../models/job.model';

interface HubSection {
  key: string;
  label: string;
  icon: string;
  hint: string;
}

interface HealthSnapshot {
  open: number;
  inAuto: number;
  inHuman: number;
  inProgress: number;
  archive: number;
}

/**
 * Project Hub — the per-project landing surface that the new shell
 * opens on double-click of a project pill. Renders project header,
 * an "INSIGHT" side-nav, and lightweight overview cards that mirror
 * the metric vocabulary from .reference-layout/project-hub.jsx
 * (HEALTH, REPOSITORY, ACTIVE CLIS, QUALITY).
 *
 * The deeper interactive sections (Visual Evidence, Architecture
 * drift, UX/UI overlay) reuse the existing project-detail overlays
 * via direct links until they are migrated as Hub-internal pages.
 */
@Component({
  selector: 'app-project-hub-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-hub-view.component.html',
  styleUrl: './project-hub-view.component.scss',
})
export class ProjectHubViewComponent {
  private readonly jobService = inject(JobService);

  readonly projectName = input.required<string>();
  readonly initialSection = input<string>('overview');

  readonly section = signal<string>('overview');

  readonly sections: HubSection[] = [
    { key: 'overview', label: 'Overview', icon: '▤', hint: 'Health, repository, CLIs, quality' },
    { key: 'visual', label: 'Visual Evidence', icon: '✓', hint: 'Screenshots, evidence trail' },
    { key: 'architecture', label: 'Architecture', icon: '⧉', hint: 'Drift detector, dependencies' },
    { key: 'drift', label: 'Drift', icon: '↯', hint: 'Spec / tests / docs drift' },
    { key: 'uxui', label: 'UX / UI', icon: '◐', hint: 'UX critique queue + decisions' },
    { key: 'observability', label: 'Observability', icon: '⌁', hint: 'Token usage, telemetry' },
  ];

  readonly identity = computed(() => projectIdentity(this.projectName()));

  readonly jobs = computed<JobInfo[]>(() => {
    const grouped = this.jobService.grouped();
    const out: JobInfo[] = [];
    for (const lane of Object.values(grouped)) {
      for (const job of lane as JobInfo[]) {
        if (job.projectName === this.projectName()) out.push(job);
      }
    }
    return out;
  });

  readonly health = computed<HealthSnapshot>(() => {
    const grouped = this.jobService.grouped();
    const matchesProject = (j: JobInfo) => j.projectName === this.projectName();
    const len = (key: keyof typeof grouped) => (grouped[key] ?? []).filter(matchesProject).length;
    return {
      open: this.jobs().filter(j => j.state !== '6-completed' && j.state !== '7-archive').length,
      inAuto: len('autoReview'),
      inHuman: len('humanReview'),
      inProgress: len('progress'),
      archive: len('archive'),
    };
  });

  selectSection(key: string): void {
    this.section.set(key);
  }
}
