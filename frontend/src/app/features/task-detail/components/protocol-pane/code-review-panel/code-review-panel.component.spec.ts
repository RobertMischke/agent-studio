import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { CodeReviewPanelComponent } from './code-review-panel.component';
import { CodeReviewActivityStore } from '../../../../../services/code-review-activity.store';
import type { TaskInfo } from '../../../../../models/task.model';

/**
 * Behaviour spec for the user-triggered code-review panel. Pins the
 * load-bearing flows the user can observe:
 *
 * <ol>
 *   <li>On init, the panel pulls the existing list endpoint.</li>
 *   <li>An empty list shows the empty-state hint.</li>
 *   <li>A populated list renders one row per entry, with the verdict.</li>
 *   <li>Run posts the chosen model, surfaces the running indicator, and
 *       refreshes the list when the call returns.</li>
 *   <li>The picker seeds from the configured default, or from a remembered
 *       last-used pair when one exists.</li>
 *   <li>A run registers in the shared activity store so the kanban card can
 *       show a progress badge, then clears it when the call resolves.</li>
 * </ol>
 *
 * <p>The HTTP layer is stubbed via Angular's testing controller so the
 * spec runs offline. Required-input seeding mirrors the smoke specs in
 * this folder.</p>
 */
describe('CodeReviewPanelComponent', () => {
  const LAST_AGENT_KEY = 'atp.codeReview.lastAgent';

  // Each test starts with no remembered pair, so the panel exercises the
  // "no last-used -> fetch configured default" path unless a test seeds it.
  beforeEach(() => {
    try {
      localStorage.removeItem(LAST_AGENT_KEY);
    } catch {
      // No storage in this environment; the component tolerates that.
    }
  });

  function setup() {
    return TestBed.configureTestingModule({
      imports: [CodeReviewPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
  }

  function seedJob(): TaskInfo {
    return {
      id: 'demo-job',
      taskKey: 'p::demo-job',
      title: 'Demo job',
      state: '3-progress',
      order: 1,
      agent: 'claude',
      cliType: 'claude',
      sessionName: '',
      ownerClientId: 'local-default',
      watchPath: 'C:/projects/foo',
      projectName: 'Foo',
      folderPath: 'C:/projects/foo/3-progress/demo-job',
      createdAt: '2026-05-01T00:00:00Z',
      sessionChain: [],
    } as unknown as TaskInfo;
  }

  it('pulls the listing on init and renders an empty-state when no MDs exist', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    // First visit with nothing remembered: the panel fetches the configured
    // default before listing existing reviews.
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-opus-4-7' });
    const req = httpCtrl.expectOne((r) => r.url.includes('/api/tasks/demo-job/code-review/list'));
    expect(req.request.method).toBe('GET');
    req.flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="code-review-empty"]')?.textContent).toMatch(/No reviews yet/);
    httpCtrl.verify();
  });

  it('renders one row per listed review with the verdict label', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-opus-4-7' });
    httpCtrl.expectOne((r) => r.url.includes('/api/tasks/demo-job/code-review/list')).flush({
      entries: [
        {
          fileName: 'code-review-2026-05-14T12-00-00Z.md',
          verdict: 'pass',
          summary: 'Looks fine.',
          model: 'claude-opus-4-7',
          cliType: 'claude',
          commit: '0aa4c5d',
          runAt: '2026-05-14T12:00:00Z',
        },
        {
          fileName: 'code-review-2026-05-13T18-30-00Z.md',
          verdict: 'concerns',
          summary: 'Helper duplicated.',
          model: 'claude-sonnet-4-6',
          cliType: 'claude',
          commit: '7892fe6',
          runAt: '2026-05-13T18:30:00Z',
        },
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const list = root.querySelector('[data-testid="code-review-list"]');
    expect(list).toBeTruthy();
    expect(list?.querySelectorAll('.cr-row').length).toBe(2);
    const verdicts = Array.from(list?.querySelectorAll('.cr-verdict') ?? []).map((el) => el.textContent?.trim());
    expect(verdicts).toEqual(['pass', 'concerns']);
    httpCtrl.verify();
  });

  it('posts the chosen model when Run is clicked, shows the running indicator, then refreshes the list', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-opus-4-7' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    // The unified selector is a chip + popover, not a <select>. Drive the
    // signal directly to exercise the "operator picked a different model"
    // path without re-opening the picker in a unit test.
    fixture.componentInstance.onAgentCommit({ cliType: 'claude', model: 'claude-haiku-4-5-20251001', thinkingLevel: null });
    // Committing through the picker persists the pair for the next visit.
    expect(JSON.parse(localStorage.getItem(LAST_AGENT_KEY) ?? '{}')).toEqual({
      cliType: 'claude',
      model: 'claude-haiku-4-5-20251001',
      thinkingLevel: null,
    });

    const button = root.querySelector('[data-testid="code-review-run"]') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-running"]')).toBeTruthy();

    const post = httpCtrl.expectOne(
      (r) => r.method === 'POST' && r.url.includes('/api/tasks/demo-job/code-review')
    );
    expect(post.request.body).toEqual({ cliType: 'claude', model: 'claude-haiku-4-5-20251001' });
    post.flush({
      fileName: 'code-review-2026-05-14T13-00-00Z.md',
      verdict: 'pass',
      summary: 'ok',
      model: 'claude-haiku-4-5-20251001',
      cliType: 'claude',
      commit: 'abcdef0',
      durationMs: 1234,
      startedAt: '2026-05-14T13:00:00Z',
    });
    fixture.detectChanges();

    // After the post resolves, the panel re-pulls the list.
    httpCtrl.expectOne((r) => r.method === 'GET' && r.url.includes('/code-review/list')).flush({
      entries: [
        {
          fileName: 'code-review-2026-05-14T13-00-00Z.md',
          verdict: 'pass',
          summary: 'ok',
          model: 'claude-haiku-4-5-20251001',
          cliType: 'claude',
          commit: 'abcdef0',
          runAt: '2026-05-14T13:00:00Z',
        },
      ],
    });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-running"]')).toBeNull();
    expect(root.querySelectorAll('.cr-row').length).toBe(1);
    // The pair the backend ran with is remembered for the next visit.
    expect(JSON.parse(localStorage.getItem(LAST_AGENT_KEY) ?? '{}')).toEqual({
      cliType: 'claude',
      model: 'claude-haiku-4-5-20251001',
      thinkingLevel: null,
    });
    httpCtrl.verify();
  });

  it('surfaces an error message when the run POST fails', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-opus-4-7' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('[data-testid="code-review-run"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const post = httpCtrl.expectOne((r) => r.method === 'POST');
    post.flush({ error: 'CLI exploded' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-error"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="code-review-running"]')).toBeNull();
    httpCtrl.verify();
  });

  it('seeds the picker from the configured backend default when nothing is remembered', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'codex', model: 'gpt-5-codex' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    expect(fixture.componentInstance.selectedCli()).toBe('codex');
    expect(fixture.componentInstance.selectedModel()).toBe('gpt-5-codex');
    httpCtrl.verify();
  });

  it('seeds from the remembered last-used pair and skips the configured-default fetch', async () => {
    localStorage.setItem(
      LAST_AGENT_KEY,
      JSON.stringify({ cliType: 'codex', model: 'gpt-5-codex' }),
    );
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    // A remembered pair short-circuits the defaults round-trip entirely.
    httpCtrl.expectNone((r) => r.url.includes('/tasks/code-review/defaults'));
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    expect(fixture.componentInstance.selectedCli()).toBe('codex');
    expect(fixture.componentInstance.selectedModel()).toBe('gpt-5-codex');
    httpCtrl.verify();
  });

  it('marks the shared activity store while a run is in flight and clears it on completion', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl
      .expectOne((r) => r.url.includes('/tasks/code-review/defaults'))
      .flush({ cliType: 'claude', model: 'claude-opus-4-7' });
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const store = TestBed.inject(CodeReviewActivityStore);
    const key = CodeReviewActivityStore.key('C:/projects/foo', 'demo-job');
    expect(store.isRunning(key)).toBe(false);

    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('[data-testid="code-review-run"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    // While the synchronous POST is outstanding, the kanban card badge is lit.
    expect(store.isRunning(key)).toBe(true);

    const post = httpCtrl.expectOne((r) => r.method === 'POST');
    post.flush({
      fileName: 'code-review-2026-05-14T13-00-00Z.md',
      verdict: 'pass',
      summary: 'ok',
      model: 'claude-opus-4-7',
      cliType: 'claude',
      commit: 'abcdef0',
      durationMs: 10,
      startedAt: '2026-05-14T13:00:00Z',
    });
    fixture.detectChanges();

    httpCtrl.expectOne((r) => r.method === 'GET' && r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    expect(store.isRunning(key)).toBe(false);
    httpCtrl.verify();
  });
});
