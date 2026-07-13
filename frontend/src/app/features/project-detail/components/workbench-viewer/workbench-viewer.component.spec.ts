import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import { WorkbenchViewerComponent } from './workbench-viewer.component';

const DOCUMENT: WorkbenchDocument = {
  workbench: {
    id: 'boundary',
    title: 'Boundary probe',
    summary: 'Proves the isolated wrapper.',
    status: 'active',
    phase: 'testing',
    updatedAtUtc: '2026-07-12T10:00:00Z',
    entryPath: 'docs/workbenches/boundary/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: ['AGT-2123'],
  },
  html: '<script id="early">document.body.dataset.ran="true"</script><html><head><base href="https://example.invalid/"><meta http-equiv="Content-Security-Policy" content="default-src *"></head><body class="artifact"><meta http-equiv="refresh" content="0;url=https://example.invalid/"><h1>Probe</h1></body></html>',
  branch: 'develop',
  revision: null,
  workingTreeModified: true,
};

describe('WorkbenchViewerComponent', () => {
  it('normalises artifact HTML behind a policy-first fixed wrapper', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('workbenchId', 'boundary');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/workbenches/boundary').flush(DOCUMENT);
    fixture.detectChanges();

    const srcdoc = fixture.componentInstance.srcdoc();
    const parsed = new DOMParser().parseFromString(srcdoc, 'text/html');
    expect(parsed.head.firstElementChild?.tagName).toBe('META');
    expect(parsed.head.firstElementChild?.getAttribute('http-equiv')).toBe('Content-Security-Policy');
    expect(parsed.head.children.item(1)?.tagName).toBe('BASE');
    expect(srcdoc.indexOf('Content-Security-Policy')).toBeLessThan(srcdoc.indexOf('id="early"'));
    expect(parsed.querySelectorAll('meta[http-equiv="Content-Security-Policy"]')).toHaveLength(1);
    expect(parsed.querySelector('meta[http-equiv="refresh"]')).toBeNull();
    expect(parsed.querySelector('base')?.getAttribute('href')).toBe('about:blank');
    expect(parsed.body.classList.contains('artifact')).toBe(true);

    const frame = fixture.nativeElement.querySelector('[data-testid="workbench-viewer-frame"]') as HTMLIFrameElement;
    expect(frame.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame.srcdoc).toBe(srcdoc);
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-viewer-working-tree"]')?.textContent)
      .toContain('uncommitted');
    http.verify();
  });
});
