import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { CodeReviewPanelComponent } from './code-review-panel.component';
import type { JobInfo } from '../../../../../models/job.model';

/**
 * Behaviour spec for the user-triggered code-review panel. Pins the four
 * load-bearing flows the user can observe:
 *
 * <ol>
 *   <li>On init, the panel pulls the existing list endpoint.</li>
 *   <li>An empty list shows the empty-state hint.</li>
 *   <li>A populated list renders one row per entry, with the verdict.</li>
 *   <li>Run posts the chosen model, surfaces the running indicator, and
 *       refreshes the list when the call returns.</li>
 * </ol>
 *
 * <p>The HTTP layer is stubbed via Angular's testing controller so the
 * spec runs offline. Required-input seeding mirrors the smoke specs in
 * this folder.</p>
 */
describe('CodeReviewPanelComponent', () => {
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

  function seedJob(): JobInfo {
    return {
      id: 'demo-job',
      jobKey: 'p::demo-job',
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
    } as unknown as JobInfo;
  }

  it('pulls the listing on init and renders an empty-state when no MDs exist', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    const req = httpCtrl.expectOne((r) => r.url.includes('/api/jobs/demo-job/code-review/list'));
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
    httpCtrl.expectOne((r) => r.url.includes('/api/jobs/demo-job/code-review/list')).flush({
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
    httpCtrl.expectOne((r) => r.url.includes('/code-review/list')).flush({ entries: [] });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const select = root.querySelector('[data-testid="code-review-model"]') as HTMLSelectElement;
    select.value = 'claude-haiku-4-5-20251001';
    select.dispatchEvent(new Event('change'));
    fixture.componentInstance.selectedModel.set('claude-haiku-4-5-20251001');

    const button = root.querySelector('[data-testid="code-review-run"]') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="code-review-running"]')).toBeTruthy();

    const post = httpCtrl.expectOne(
      (r) => r.method === 'POST' && r.url.includes('/api/jobs/demo-job/code-review')
    );
    expect(post.request.body).toEqual({ model: 'claude-haiku-4-5-20251001' });
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
    httpCtrl.verify();
  });

  it('surfaces an error message when the run POST fails', async () => {
    await setup();
    const fixture = TestBed.createComponent(CodeReviewPanelComponent);
    fixture.componentRef.setInput('job', seedJob());
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
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
});
