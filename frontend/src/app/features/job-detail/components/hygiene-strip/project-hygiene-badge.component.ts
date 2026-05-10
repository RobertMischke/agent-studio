import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input } from '@angular/core';
import { GitHygieneService } from '../../../../services/git-hygiene.service';

/**
 * Compact dirty / unpushed badge rendered on the project header chip.
 * Subscribes to the shared GitHygieneService so multiple consumers share
 * one polling loop per project. Renders nothing when the project is clean
 * and in sync (no upstream is treated as "no signal" - we don't shout
 * about repos the user hasn't wired to a remote yet).
 */
@Component({
  selector: 'app-project-hygiene-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <span class="project-hygiene-badge"
            [class.project-hygiene-badge--dirty]="isDirty()"
            [class.project-hygiene-badge--unpushed]="!isDirty() && isAhead()"
            [title]="tooltip()"
            data-testid="project-hygiene-badge">
        @if (isDirty()) {
          ⚠ dirty
        } @else if (isAhead()) {
          ↑ {{ aheadCount() }} unpushed
        }
      </span>
    }
  `,
  styles: [`
    :host { display: inline-flex; }
    .project-hygiene-badge {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 1px 7px;
      border-radius: 999px;
      font-size: 10.5px;
      font-weight: 600;
      letter-spacing: 0.03em;
      text-transform: uppercase;
      border: 1px solid currentColor;
    }
    .project-hygiene-badge--dirty {
      color: #f9e2af;
      background: rgba(249, 226, 175, 0.10);
    }
    .project-hygiene-badge--unpushed {
      color: #fab387;
      background: rgba(250, 179, 135, 0.10);
    }
  `]
})
export class ProjectHygieneBadgeComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  private readonly hygieneSvc = inject(GitHygieneService);
  private stopFn: (() => void) | null = null;

  private readonly status = computed(() => this.hygieneSvc.forProject(this.projectName())());

  readonly isDirty = computed(() => !!this.status()?.isDirty);
  readonly isAhead = computed(() => {
    const s = this.status();
    return !!s && s.hasUpstream && s.ahead > 0;
  });
  readonly aheadCount = computed(() => this.status()?.ahead ?? 0);
  readonly visible = computed(() => this.isDirty() || this.isAhead());

  readonly tooltip = computed(() => {
    const s = this.status();
    if (!s) return '';
    const parts: string[] = [];
    if (s.isDirty) {
      parts.push(`Working tree dirty: ${s.stagedCount} staged, ${s.unstagedCount} unstaged, ${s.untrackedCount} untracked.`);
    }
    if (s.hasUpstream && s.ahead > 0) {
      parts.push(`${s.ahead} commit${s.ahead === 1 ? '' : 's'} ahead of ${s.upstream}.`);
    }
    return parts.join(' ');
  });

  ngOnInit(): void {
    this.stopFn = this.hygieneSvc.ensurePolling(this.projectName());
  }

  ngOnDestroy(): void {
    this.stopFn?.();
    this.stopFn = null;
  }
}
