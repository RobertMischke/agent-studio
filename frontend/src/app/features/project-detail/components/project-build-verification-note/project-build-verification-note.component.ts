import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

interface BuildVerificationStatus {
  profile: { status?: string | null } | null;
  hasVerifyCommands: boolean;
  verifyPlanSource: string;
  verifyCommandCount: number;
}

/** Neutral project-level disclosure when the build/test gate has no commands. */
@Component({
  selector: 'app-project-build-verification-note',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-build-verification-note.component.html',
  styleUrl: './project-build-verification-note.component.scss',
})
export class ProjectBuildVerificationNoteComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly status = signal<BuildVerificationStatus | null>(null);

  private readonly http = inject(HttpClient);

  ngOnInit(): void {
    this.http.get<BuildVerificationStatus>(
      `/api/projects/${encodeURIComponent(this.projectName())}/build-profile`,
    ).subscribe({
      next: status => this.status.set(status),
      error: () => { /* Stay absent when project verification cannot be resolved. */ },
    });
  }
}
