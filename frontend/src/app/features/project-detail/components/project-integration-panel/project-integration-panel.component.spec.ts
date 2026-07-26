import { describe, expect, it } from 'vitest';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ProjectIntegrationView } from '../../../git';
import { ProjectIntegrationPanelComponent } from './project-integration-panel.component';

function response(): ProjectIntegrationView {
  return {
    project: 'Demo',
    isRepo: true,
    integrationRef: 'origin/develop',
    releaseRef: 'origin/main',
    integrationHeadSha: 'a'.repeat(40),
    releaseHeadSha: 'b'.repeat(40),
    capturedAt: '2026-07-22T10:00:00Z',
    queue: [
      { taskId: 'one', taskKey: 'AGT-1', title: 'Merged work', lane: '6-completed', stateSince: '2026-07-22T09:00:00Z', status: 'merged', mergeSha: 'a'.repeat(40), reason: null },
      { taskId: 'two', taskKey: 'AGT-2', title: 'Waiting work', lane: '6-completed', stateSince: '2026-07-22T09:01:00Z', status: 'waiting', mergeSha: null, reason: 'Not on origin/develop.' },
      { taskId: 'three', taskKey: 'AGT-3', title: 'Conflict work', lane: '6-completed', stateSince: '2026-07-22T09:02:00Z', status: 'conflict', mergeSha: null, reason: 'Conflict in app.ts.' },
      { taskId: 'four', taskKey: 'AGT-4', title: 'Docs only', lane: '6-completed', stateSince: '2026-07-22T09:03:00Z', status: 'skipped', mergeSha: null, reason: 'No integrable change set.' },
    ],
    publisherMerges: [
      { taskKey: 'AGT-1', title: 'Merged work', sha: 'a'.repeat(40), shortSha: 'aaaaaaa', integratedAt: '2026-07-22T09:30:00Z', publisher: 'publisher', subject: 'merge(AGT-1): merged work' },
    ],
    promotion: {
      fromRef: 'origin/develop', toRef: 'origin/main', fromSha: 'a'.repeat(40), toSha: 'b'.repeat(40),
      tasks: [{ taskKey: 'AGT-1', title: 'Merged work', sha: 'a'.repeat(40), shortSha: 'aaaaaaa', subject: 'merge(AGT-1): merged work' }],
      files: [{ status: 'M', path: 'src/app.ts', added: 12, removed: 3 }],
      filesChanged: 1, added: 12, removed: 3,
    },
    error: null,
  };
}

describe('ProjectIntegrationPanelComponent', () => {
  it('renders all four queue states, publisher merges, and the promotion file stat', () => {
    TestBed.configureTestingModule({
      imports: [ProjectIntegrationPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ProjectIntegrationPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url === '/api/git/integration' && request.params.get('project') === 'Demo').flush(response());
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[data-testid="integration-queue-row"]')).toHaveLength(4);
    expect(root.textContent).toContain('1 merged');
    expect(root.querySelector('[data-testid="integration-queue"]')?.textContent).toContain('Conflict in app.ts');
    expect(root.querySelector('[data-testid="promotion-diff"]')?.textContent).toContain('src/app.ts');
    expect(root.querySelector('[data-testid="promotion-diff"]')?.textContent).toContain('+12');
    expect(root.querySelector('[data-testid="publisher-merges"]')?.textContent).toContain('AGT-1');
    expect(root.querySelector('[data-testid="publisher-merges"]')?.textContent).toContain('publisher');
    expect(root.querySelector('[data-testid="integration-queue"] code[title]')?.getAttribute('title')).toBe('a'.repeat(40));
    expect(root.textContent).toContain('origin/develop');
    http.verify();
  });
});
