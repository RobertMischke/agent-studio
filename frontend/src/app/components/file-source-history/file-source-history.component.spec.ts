import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { FileSourceHistoryComponent } from './file-source-history.component';

describe('FileSourceHistoryComponent', () => {
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [FileSourceHistoryComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FileSourceHistoryComponent);
    fixture.componentRef.setInput('jobId', 'demo-job');
    fixture.componentRef.setInput('watchPath', 'C:/projects/foo');
    fixture.componentRef.setInput('path', 'aspect-code-quality.md');
    fixture.componentRef.setInput('content', '# Current\n\nLatest body.');
    fixture.detectChanges();
    return fixture;
  }

  it('loads timeline, selected version, and run-to-run diff when history is opened', async () => {
    const fixture = await setup();
    const http = TestBed.inject(HttpTestingController);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="file-source-history"]')?.textContent).toContain('Current');

    root.querySelector<HTMLButtonElement>('[data-testid="file-source-history-toggle"]')?.click();
    fixture.detectChanges();

    http.expectOne((r) =>
      r.url === '/api/tasks/demo-job/files/aspect-code-quality.md/history' &&
      r.params.get('watchPath') === 'C:/projects/foo')
      .flush([
        {
          sha: '2222222',
          at: '2026-06-09T12:10:00Z',
          runIndex: 2,
          verdict: 'pass',
          message: 'update aspect result',
          author: 'Agent <agent@example.com>',
          provenance: { source: 'workspace', path: 'aspect-code-quality.md' },
        },
        {
          sha: '1111111',
          at: '2026-06-09T12:00:00Z',
          runIndex: 1,
          verdict: 'concerns',
          message: 'create aspect result',
          author: 'Agent <agent@example.com>',
          provenance: { source: 'workspace', path: 'aspect-code-quality.md' },
        },
      ]);

    http.expectOne((r) =>
      r.url === '/api/tasks/demo-job/files/aspect-code-quality.md' &&
      r.params.get('at') === '2222222')
      .flush(utf8Buffer('# Version 2\n\nPass.'));
    http.expectOne((r) =>
      r.url === '/api/tasks/demo-job/files/aspect-code-quality.md/diff' &&
      r.params.get('from') === '1111111' &&
      r.params.get('to') === '2222222')
      .flush(utf8Buffer('@@ -1 +1 @@\n-concerns\n+pass\n'));
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="file-source-history-timeline"]')?.textContent).toContain('Run #2');
    expect(root.querySelector('[data-testid="file-source-history-timeline"]')?.textContent).toContain('pass');
    expect(root.querySelector('[data-testid="file-source-version"]')?.textContent).toContain('Version 2');
    const diff = root.querySelector('[data-testid="file-source-diff"]');
    expect(diff?.textContent).toContain('-concerns');
    expect(diff?.textContent).toContain('+pass');
    http.verify();
  });
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}
