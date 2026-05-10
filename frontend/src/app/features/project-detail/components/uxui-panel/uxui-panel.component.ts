import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { DesignService } from '../../../../services/design.service';
import {
  DesignActionKind,
  DesignCouncilNote,
  DesignOverviewResponse,
  DesignReferenceItem,
} from './uxui-panel.types';

/**
 * Project UX/UI panel (slice 6 of the quality-system mockup,
 * docs/mockups/quality-system/). Mirrors the `ui.html` UX/UI screen:
 *
 * <list type="bullet">
 *   <item>Top metric row: design status, references count, screenshots
 *   accepted/rejected, council notes (open vs accepted).</item>
 *   <item>Design references grid: four card kinds (Markdown brief,
 *   accepted screenshots, external inspiration, rejected alternatives).</item>
 *   <item>Current design loop band: four action buttons.</item>
 *   <item>Council critique notes list with category chip and per-row
 *   Task / Accept actions. <c>parseOk = false</c> rows render the raw
 *   Markdown plus an "unstructured report" warning.</item>
 * </list>
 *
 * Action-driven principle: the panel does no analysis on its own. The
 * action buttons delegate to the backend, which queues a normal CLI job
 * for the runner to pick up; the council Accept button writes a small
 * <c>acceptedAt</c> field into the note's frontmatter and refreshes.
 */
@Component({
  selector: 'app-uxui-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="ux-panel" data-testid="uxui-panel">
      <header class="ux-panel__head">
        <div class="ux-panel__title-row">
          <h2 class="ux-panel__title">
            <span class="ux-panel__icon" aria-hidden="true">🎨</span>
            UX/UI
          </h2>
          @if (loading()) {
            <span class="ux-panel__loading" data-testid="uxui-loading">Loading…</span>
          }
        </div>
        <p class="ux-panel__sub">Design references, screenshots, council critique, and next-version actions.</p>
      </header>

      @if (loadError(); as err) {
        <div class="ux-panel__error" data-testid="uxui-load-error">
          Could not load design data: {{ err }}
        </div>
      }

      <div class="ux-panel__cards" data-testid="uxui-metric-grid">
        <article class="ux-panel__card" data-testid="uxui-card-status">
          <h3 class="ux-panel__card-title">Design status</h3>
          <p class="ux-panel__card-value" data-testid="uxui-card-status-value">{{ overview()?.status ?? '—' }}</p>
          <p class="ux-panel__card-detail">
            @if (overview()?.lastReviewDate) {
              Last council {{ overview()?.lastReviewDate }}
            } @else {
              No council run yet
            }
          </p>
        </article>

        <article class="ux-panel__card" data-testid="uxui-card-references">
          <h3 class="ux-panel__card-title">References</h3>
          <p class="ux-panel__card-value ux-panel__card-value--big" data-testid="uxui-card-references-value">
            {{ overview()?.referencesCount ?? 0 }}
          </p>
          <p class="ux-panel__card-detail">screens, markdown briefs, images</p>
        </article>

        <article class="ux-panel__card" data-testid="uxui-card-screenshots">
          <h3 class="ux-panel__card-title">Screenshots</h3>
          <p class="ux-panel__card-value ux-panel__card-value--big" data-testid="uxui-card-screenshots-value">
            {{ totalScreenshots() }}
          </p>
          <p class="ux-panel__card-detail">
            <span class="ux-panel__chip ux-panel__chip--ok" data-testid="uxui-screenshots-accepted">
              {{ overview()?.screenshotsAcceptedCount ?? 0 }} accepted
            </span>
            <span class="ux-panel__chip ux-panel__chip--bad" data-testid="uxui-screenshots-rejected">
              {{ overview()?.screenshotsRejectedCount ?? 0 }} rejected
            </span>
          </p>
        </article>

        <article class="ux-panel__card" data-testid="uxui-card-council">
          <h3 class="ux-panel__card-title">Council notes</h3>
          <p class="ux-panel__card-value ux-panel__card-value--big" data-testid="uxui-card-council-value">
            {{ overview()?.councilOpenCount ?? 0 }}
          </p>
          <p class="ux-panel__card-detail">
            {{ overview()?.councilAcceptedCount ?? 0 }} accepted, product / visual / interaction / a11y
          </p>
        </article>
      </div>

      <section class="ux-panel__group" data-testid="uxui-references">
        <header class="ux-panel__group-head">
          <h3 class="ux-panel__group-title">Design references</h3>
          <span class="ux-panel__group-meta">{{ overview()?.referencesCount ?? 0 }} artifacts</span>
          <button type="button" class="ux-panel__btn ux-panel__btn--ghost"
                  data-testid="uxui-add-reference"
                  (click)="onCreateFollowUp('add-reference')">
            ＋ Add reference
          </button>
        </header>
        <div class="ux-panel__ref-grid">
          <article class="ux-panel__ref-card ux-panel__ref-card--brief" data-testid="uxui-ref-brief">
            <h4>Markdown brief</h4>
            @if (overview()?.briefExists) {
              <p>{{ overview()?.briefSummary || 'Brief on file. Open the design folder to read.' }}</p>
            } @else {
              <p class="ux-panel__empty">No brief.md yet.</p>
            }
          </article>
          <article class="ux-panel__ref-card ux-panel__ref-card--accepted" data-testid="uxui-ref-accepted">
            <h4>Accepted screenshots</h4>
            @if (acceptedRefs().length === 0) {
              <p class="ux-panel__empty">No accepted variants yet.</p>
            } @else {
              <ul class="ux-panel__ref-list">
                @for (r of acceptedRefs(); track r.fileName) {
                  <li>
                    <span class="ux-panel__ref-name">{{ r.title ?? r.fileName }}</span>
                    @if (r.summary) {
                      <span class="ux-panel__ref-sum">{{ r.summary }}</span>
                    }
                  </li>
                }
              </ul>
            }
          </article>
          <article class="ux-panel__ref-card ux-panel__ref-card--external" data-testid="uxui-ref-external">
            <h4>External inspiration</h4>
            @if (externalRefs().length === 0) {
              <p class="ux-panel__empty">No external references.</p>
            } @else {
              <ul class="ux-panel__ref-list">
                @for (r of externalRefs(); track r.fileName) {
                  <li>
                    <span class="ux-panel__ref-name">{{ r.title ?? r.fileName }}</span>
                    @if (r.summary) {
                      <span class="ux-panel__ref-sum">{{ r.summary }}</span>
                    }
                  </li>
                }
              </ul>
            }
          </article>
          <article class="ux-panel__ref-card ux-panel__ref-card--rejected" data-testid="uxui-ref-rejected">
            <h4>Rejected alternatives</h4>
            @if (rejectedRefs().length === 0) {
              <p class="ux-panel__empty">No rejected alternatives.</p>
            } @else {
              <ul class="ux-panel__ref-list">
                @for (r of rejectedRefs(); track r.fileName) {
                  <li>
                    <span class="ux-panel__ref-name">{{ r.title ?? r.fileName }}</span>
                    @if (r.summary) {
                      <span class="ux-panel__ref-sum">{{ r.summary }}</span>
                    }
                  </li>
                }
              </ul>
            }
          </article>
        </div>
      </section>

      <section class="ux-panel__group" data-testid="uxui-loop">
        <header class="ux-panel__group-head">
          <h3 class="ux-panel__group-title">Current design loop</h3>
          <span class="ux-panel__group-meta">action-driven</span>
        </header>
        <p class="ux-panel__hint">
          The user chooses when to run each step. Skills produce Markdown plus structured JSON; failed parsing still leaves the raw report visible.
        </p>
        <div class="ux-panel__actions" data-testid="uxui-action-buttons">
          <button type="button"
                  class="ux-panel__btn ux-panel__btn--primary"
                  data-testid="uxui-run-screenshot-critique"
                  [disabled]="busyAction() !== null"
                  (click)="onRunAction('screenshot-critique')">
            {{ busyAction() === 'screenshot-critique' ? 'Queueing…' : 'Run screenshot critique' }}
          </button>
          <button type="button"
                  class="ux-panel__btn"
                  data-testid="uxui-run-council-review"
                  [disabled]="busyAction() !== null"
                  (click)="onRunAction('council-review')">
            {{ busyAction() === 'council-review' ? 'Queueing…' : 'Run council review' }}
          </button>
          <button type="button"
                  class="ux-panel__btn"
                  data-testid="uxui-request-next-version"
                  [disabled]="busyAction() !== null"
                  (click)="onRunAction('request-next-version')">
            {{ busyAction() === 'request-next-version' ? 'Queueing…' : 'Request next version' }}
          </button>
          <button type="button"
                  class="ux-panel__btn ux-panel__btn--ghost"
                  data-testid="uxui-create-followup"
                  (click)="onCreateFollowUp('design-followup')">
            Create follow-up task
          </button>
          @if (actionError(); as err) {
            <span class="ux-panel__chip ux-panel__chip--error"
                  role="status"
                  data-testid="uxui-action-error">{{ err }}</span>
          }
          @if (actionQueued(); as ok) {
            <span class="ux-panel__chip ux-panel__chip--ok"
                  role="status"
                  data-testid="uxui-action-queued">{{ ok }}</span>
          }
        </div>
      </section>

      <section class="ux-panel__group" data-testid="uxui-council-notes">
        <header class="ux-panel__group-head">
          <h3 class="ux-panel__group-title">Council critique</h3>
          <span class="ux-panel__group-meta">
            {{ overview()?.councilOpenCount ?? 0 }} open
            @if ((overview()?.councilAcceptedCount ?? 0) > 0) {
              · {{ overview()?.councilAcceptedCount }} accepted
            }
          </span>
        </header>
        @if (councilNotes().length === 0) {
          <p class="ux-panel__empty" data-testid="uxui-council-empty">
            No council notes recorded yet. Click "Run council review" to queue one.
          </p>
        } @else {
          <ul class="ux-panel__council-list">
            @for (n of councilNotes(); track n.fileName) {
              <li class="ux-panel__council-row"
                  [class.ux-panel__council-row--accepted]="!!n.acceptedAt"
                  [class.ux-panel__council-row--unstructured]="!n.parseOk"
                  [attr.data-testid]="'uxui-council-row'"
                  [attr.data-rel-path]="n.relPath"
                  [attr.data-parse-ok]="n.parseOk"
                  [attr.data-accepted]="!!n.acceptedAt">
                <div class="ux-panel__council-main">
                  @if (n.category) {
                    <span class="ux-panel__tag"
                          [class]="'ux-panel__tag--' + categoryTone(n.category)">{{ n.category }}</span>
                  }
                  <span class="ux-panel__council-title">{{ n.title ?? n.fileName }}</span>
                  @if (n.noteDate) {
                    <span class="ux-panel__council-date">{{ n.noteDate }}</span>
                  }
                  @if (n.acceptedAt) {
                    <span class="ux-panel__chip ux-panel__chip--ok" data-testid="uxui-council-accepted-chip">accepted</span>
                  }
                </div>
                @if (!n.parseOk) {
                  <p class="ux-panel__warning" data-testid="uxui-unstructured-warning">
                    ⚠ unstructured report ({{ n.parseError ?? 'no structured block detected' }}). Raw Markdown shown below.
                  </p>
                  @if (rawCache()[n.fileName]; as raw) {
                    <pre class="ux-panel__raw" data-testid="uxui-council-raw">{{ raw }}</pre>
                  } @else {
                    <button type="button"
                            class="ux-panel__btn ux-panel__btn--ghost"
                            data-testid="uxui-council-load-raw"
                            (click)="loadRaw(n.fileName)">Load raw Markdown</button>
                  }
                } @else if (n.summary) {
                  <p class="ux-panel__council-summary">{{ n.summary }}</p>
                }
                <div class="ux-panel__council-actions">
                  <button type="button"
                          class="ux-panel__btn ux-panel__btn--ghost"
                          data-testid="uxui-council-task"
                          (click)="onCreateFollowUp('council', n)">
                    Task
                  </button>
                  <button type="button"
                          class="ux-panel__btn ux-panel__btn--ghost"
                          [disabled]="!!n.acceptedAt || acceptingFile() === n.fileName"
                          [attr.data-testid]="'uxui-council-accept'"
                          [attr.data-file-name]="n.fileName"
                          (click)="onAccept(n.fileName)">
                    {{ n.acceptedAt ? 'Accepted' : (acceptingFile() === n.fileName ? 'Accepting…' : 'Accept') }}
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </section>
    </section>
  `,
  styles: [`
    :host { display: block; }

    .ux-panel { display: flex; flex-direction: column; gap: 18px; }

    .ux-panel__head {
      padding-bottom: 12px;
      border-bottom: 1px solid #313244;
    }
    .ux-panel__title-row { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .ux-panel__title { margin: 0; font-size: 1.05rem; font-weight: 600; color: #f8fafc; display: flex; align-items: center; gap: 8px; }
    .ux-panel__icon { width: 18px; text-align: center; }
    .ux-panel__sub { margin: 6px 0 0; color: #a6adc8; font-size: 0.85rem; }
    .ux-panel__loading { color: #a6adc8; font-size: 0.78rem; margin-left: auto; }

    .ux-panel__error {
      padding: 10px 14px;
      background: rgba(243, 139, 168, 0.10);
      border: 1px solid rgba(243, 139, 168, 0.30);
      color: #f38ba8;
      border-radius: 6px;
      font-size: 0.85rem;
    }

    .ux-panel__cards {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 14px;
    }
    .ux-panel__card {
      background: #181825;
      border: 1px solid #313244;
      border-radius: 8px;
      padding: 14px 16px;
      display: flex;
      flex-direction: column;
      gap: 6px;
      min-height: 110px;
    }
    .ux-panel__card-title { margin: 0; color: #a6adc8; font-size: 0.74rem; letter-spacing: 0.06em; text-transform: uppercase; font-weight: 600; }
    .ux-panel__card-value { margin: 4px 0 0; color: #cdd6f4; font-size: 0.95rem; display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
    .ux-panel__card-value--big { font-size: 1.7rem; font-weight: 600; color: #f8fafc; }
    .ux-panel__card-detail { margin: 0; color: #a6adc8; font-size: 0.78rem; display: flex; gap: 6px; flex-wrap: wrap; align-items: baseline; }

    .ux-panel__group {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .ux-panel__group-head {
      display: flex;
      align-items: baseline;
      gap: 12px;
      flex-wrap: wrap;
    }
    .ux-panel__group-title { margin: 0; color: #a6adc8; font-size: 0.74rem; letter-spacing: 0.06em; text-transform: uppercase; font-weight: 600; }
    .ux-panel__group-meta { color: #6c7086; font-size: 0.74rem; letter-spacing: 0.02em; }
    .ux-panel__hint { margin: 0; color: #a6adc8; font-size: 0.82rem; }

    .ux-panel__ref-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px;
    }
    .ux-panel__ref-card {
      background: #181825;
      border: 1px solid #313244;
      border-radius: 8px;
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .ux-panel__ref-card h4 { margin: 0; color: #cdd6f4; font-size: 0.85rem; font-weight: 600; }
    .ux-panel__ref-card p { margin: 0; color: #a6adc8; font-size: 0.80rem; }
    .ux-panel__ref-card--accepted { border-color: rgba(166, 227, 161, 0.36); }
    .ux-panel__ref-card--rejected { border-color: rgba(243, 139, 168, 0.36); }
    .ux-panel__ref-card--external { border-color: rgba(137, 180, 250, 0.36); }
    .ux-panel__ref-card--brief { border-color: rgba(249, 226, 175, 0.36); }
    .ux-panel__ref-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; font-size: 0.80rem; }
    .ux-panel__ref-list li { display: flex; flex-direction: column; }
    .ux-panel__ref-name { color: #cdd6f4; font-weight: 600; }
    .ux-panel__ref-sum { color: #a6adc8; font-size: 0.76rem; }
    .ux-panel__empty { margin: 0; color: #6c7086; font-style: italic; font-size: 0.80rem; }

    .ux-panel__actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .ux-panel__btn {
      background: #313244;
      color: #cdd6f4;
      border: 1px solid #45475a;
      padding: 6px 12px;
      border-radius: 6px;
      cursor: pointer;
      font: inherit;
      font-size: 0.82rem;
    }
    .ux-panel__btn:hover:not(:disabled) { background: #45475a; }
    .ux-panel__btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .ux-panel__btn--primary {
      background: rgba(166, 227, 161, 0.18);
      color: #a6e3a1;
      border-color: rgba(166, 227, 161, 0.40);
    }
    .ux-panel__btn--primary:hover:not(:disabled) { background: rgba(166, 227, 161, 0.28); }
    .ux-panel__btn--ghost { background: transparent; border-style: dashed; color: #a6adc8; }
    .ux-panel__btn--ghost:hover:not(:disabled) { background: rgba(255,255,255,0.04); }

    .ux-panel__chip {
      font-size: 0.74rem;
      padding: 2px 8px;
      border-radius: 999px;
      border: 1px solid transparent;
      font-weight: 600;
    }
    .ux-panel__chip--error { background: rgba(243, 139, 168, 0.16); color: #f38ba8; border-color: rgba(243, 139, 168, 0.36); }
    .ux-panel__chip--ok { background: rgba(166, 227, 161, 0.16); color: #a6e3a1; border-color: rgba(166, 227, 161, 0.36); }
    .ux-panel__chip--bad { background: rgba(243, 139, 168, 0.16); color: #f38ba8; border-color: rgba(243, 139, 168, 0.36); }

    .ux-panel__tag {
      font-size: 0.70rem;
      padding: 2px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.10);
      text-transform: capitalize;
    }
    .ux-panel__tag--workflow { background: rgba(137,180,250,0.18); color: #89b4fa; border-color: rgba(137,180,250,0.40); }
    .ux-panel__tag--polish { background: rgba(245,194,231,0.18); color: #f5c2e7; border-color: rgba(245,194,231,0.40); }
    .ux-panel__tag--a11y { background: rgba(249,226,175,0.18); color: #f9e2af; border-color: rgba(249,226,175,0.40); }
    .ux-panel__tag--product { background: rgba(166,227,161,0.18); color: #a6e3a1; border-color: rgba(166,227,161,0.40); }
    .ux-panel__tag--visual { background: rgba(203,166,247,0.18); color: #cba6f7; border-color: rgba(203,166,247,0.40); }
    .ux-panel__tag--interaction { background: rgba(148,226,213,0.18); color: #94e2d5; border-color: rgba(148,226,213,0.40); }
    .ux-panel__tag--neutral { background: rgba(255,255,255,0.06); color: #cdd6f4; }

    .ux-panel__council-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
    .ux-panel__council-row {
      background: #181825;
      border: 1px solid #313244;
      border-radius: 6px;
      padding: 10px 14px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .ux-panel__council-row--accepted { opacity: 0.75; border-left: 3px solid rgba(166,227,161,0.55); }
    .ux-panel__council-row--unstructured { border-color: rgba(249, 226, 175, 0.36); background: rgba(249, 226, 175, 0.05); }
    .ux-panel__council-main { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    .ux-panel__council-title { color: #f8fafc; font-weight: 600; font-size: 0.88rem; flex: 1; min-width: 0; }
    .ux-panel__council-date { color: #6c7086; font-size: 0.74rem; font-variant-numeric: tabular-nums; }
    .ux-panel__council-summary { margin: 0; color: #a6adc8; font-size: 0.82rem; line-height: 1.5; }
    .ux-panel__council-actions { display: flex; gap: 6px; }

    .ux-panel__warning {
      margin: 0;
      color: #f9e2af;
      font-size: 0.78rem;
      padding: 6px 10px;
      background: rgba(249, 226, 175, 0.08);
      border: 1px solid rgba(249, 226, 175, 0.24);
      border-radius: 4px;
    }
    .ux-panel__raw {
      margin: 0;
      max-height: 220px;
      overflow: auto;
      font-size: 0.74rem;
      background: #11111b;
      color: #cdd6f4;
      padding: 8px 10px;
      border-radius: 4px;
      border: 1px solid #313244;
      white-space: pre-wrap;
      word-break: break-word;
    }

    @media (max-width: 1100px) {
      .ux-panel__cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    }
    @media (max-width: 720px) {
      .ux-panel__cards, .ux-panel__ref-grid { grid-template-columns: 1fr; }
    }
  `],
})
export class UxuiPanelComponent {
  private readonly design = inject(DesignService);

  readonly projectName = input.required<string>();

  /** Emits when the user clicks "Create follow-up task" or per-row "Task". */
  readonly createFollowUp = output<{ projectName: string; prefill: string; title: string }>();

  /** Emits when an action is successfully queued. */
  readonly actionQueuedEvent = output<{ projectName: string; action: DesignActionKind; jobId: string }>();

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly overview = signal<DesignOverviewResponse | null>(null);
  readonly references = signal<DesignReferenceItem[]>([]);
  readonly councilNotes = signal<DesignCouncilNote[]>([]);
  readonly busyAction = signal<DesignActionKind | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly actionQueued = signal<string | null>(null);
  readonly acceptingFile = signal<string | null>(null);
  readonly rawCache = signal<Record<string, string>>({});

  readonly acceptedRefs = computed(() => this.references().filter(r => r.kind === 'accepted'));
  readonly rejectedRefs = computed(() => this.references().filter(r => r.kind === 'rejected'));
  readonly externalRefs = computed(() => this.references().filter(r => r.kind === 'external' || (r.kind !== 'accepted' && r.kind !== 'rejected' && r.kind !== 'brief')));

  readonly totalScreenshots = computed(() => {
    const o = this.overview();
    if (!o) return 0;
    return (o.screenshotsAcceptedCount ?? 0) + (o.screenshotsRejectedCount ?? 0);
  });

  constructor() {
    effect(() => {
      const name = this.projectName();
      if (name) this.refresh(name);
    });
  }

  private refresh(name: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.actionError.set(null);
    this.actionQueued.set(null);
    this.rawCache.set({});

    this.design.getOverview(name).subscribe({
      next: (o) => this.overview.set(o),
      error: (err: HttpErrorResponse) => {
        this.loadError.set(err.message ?? 'unknown');
        this.overview.set(null);
      },
    });

    this.design.listReferences(name).subscribe({
      next: (r) => this.references.set(r.references ?? []),
      error: (err: HttpErrorResponse) => {
        this.loadError.set(this.loadError() ?? err.message ?? 'unknown');
        this.references.set([]);
      },
    });

    this.design.listCouncilNotes(name).subscribe({
      next: (c) => {
        this.councilNotes.set(c.notes ?? []);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(this.loadError() ?? err.message ?? 'unknown');
        this.councilNotes.set([]);
        this.loading.set(false);
      },
    });
  }

  /** Public refresh hook so the host can re-poll after a queued action returns. */
  refreshNow(): void {
    const n = this.projectName();
    if (n) this.refresh(n);
  }

  onRunAction(action: DesignActionKind): void {
    const name = this.projectName();
    if (!name) return;
    if (this.busyAction()) return;
    this.busyAction.set(action);
    this.actionError.set(null);
    this.actionQueued.set(null);
    this.design.runAction(name, action).subscribe({
      next: (res) => {
        this.busyAction.set(null);
        this.actionQueued.set(`${actionLabel(action)} queued (${res.jobId}).`);
        this.actionQueuedEvent.emit({ projectName: name, action, jobId: res.jobId });
        // The new job appears in the kanban; refresh in case the skill has
        // already produced evidence on disk for the previous run.
        this.refresh(name);
      },
      error: (err: HttpErrorResponse) => {
        this.busyAction.set(null);
        const body = err.error;
        const reason = body?.message ?? body?.error ?? err.message ?? 'action failed';
        this.actionError.set(reason);
      },
    });
  }

  onAccept(fileName: string): void {
    const name = this.projectName();
    if (!name) return;
    if (this.acceptingFile()) return;
    this.acceptingFile.set(fileName);
    this.design.acceptCouncilNote(name, fileName).subscribe({
      next: () => {
        this.acceptingFile.set(null);
        this.refresh(name);
      },
      error: () => {
        this.acceptingFile.set(null);
      },
    });
  }

  onCreateFollowUp(reason: 'add-reference' | 'design-followup' | 'council', note?: DesignCouncilNote): void {
    const name = this.projectName();
    if (!name) return;
    let prefill = '';
    let title = '';
    if (reason === 'add-reference') {
      title = `Add design reference (${name})`;
      prefill = [
        '# Add design reference',
        '',
        'Add a screenshot, brief, or external reference under `design/references/` for this project.',
        'Frontmatter must include `kind: accepted|rejected|external` and an optional `screenshot:` path.',
      ].join('\n');
    } else if (reason === 'council' && note) {
      title = `Design follow-up: ${note.title ?? note.fileName}`;
      const lines = ['# Design follow-up', ''];
      lines.push(`Source council note: \`${note.relPath}\`${note.noteDate ? ' (' + note.noteDate + ')' : ''}.`);
      if (note.category) lines.push(`Category: ${note.category}`);
      if (note.summary) {
        lines.push('');
        lines.push(note.summary);
      }
      lines.push('');
      lines.push('Action: address the council finding above. Evidence-driven; do not silently mutate task state.');
      prefill = lines.join('\n');
    } else {
      title = `Design follow-up (${name})`;
      const o = this.overview();
      const lines = ['# Design follow-up', ''];
      if (o?.briefSummary) {
        lines.push(o.briefSummary);
        lines.push('');
      }
      lines.push(`Open council notes: ${o?.councilOpenCount ?? 0}.`);
      lines.push('');
      lines.push('Action: pick the most relevant open council note and address it as a normal queued task.');
      prefill = lines.join('\n');
    }
    this.createFollowUp.emit({ projectName: name, prefill, title });
  }

  loadRaw(fileName: string): void {
    const name = this.projectName();
    if (!name) return;
    if (this.rawCache()[fileName] !== undefined) return;
    this.design.readCouncilNote(name, fileName).subscribe({
      next: (res) => {
        const next = { ...this.rawCache(), [fileName]: res.content };
        this.rawCache.set(next);
      },
      error: (err: HttpErrorResponse) => {
        const next = { ...this.rawCache(), [fileName]: `Failed to load: ${err.message ?? 'unknown'}` };
        this.rawCache.set(next);
      },
    });
  }

  categoryTone(category: string | null | undefined): string {
    if (!category) return 'neutral';
    const c = category.toLowerCase();
    if (c.includes('workflow')) return 'workflow';
    if (c.includes('polish')) return 'polish';
    if (c.includes('a11y') || c.includes('access')) return 'a11y';
    if (c.includes('product')) return 'product';
    if (c.includes('visual')) return 'visual';
    if (c.includes('interaction')) return 'interaction';
    return 'neutral';
  }
}

function actionLabel(action: DesignActionKind): string {
  switch (action) {
    case 'screenshot-critique': return 'Screenshot critique';
    case 'council-review': return 'Council review';
    case 'request-next-version': return 'Next-version plan';
    default: return action;
  }
}
