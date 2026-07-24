import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { WikiPageActionsComponent } from './wiki-page-actions';

describe('WikiPageActionsComponent', () => {
  it('derives page context and persists archive classification', async () => {
    await TestBed.configureTestingModule({
      imports: [WikiPageActionsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WikiPageActionsComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('relPath', 'concepts/actions.md');
    fixture.componentRef.setInput('title', 'Page actions');
    fixture.componentRef.setInput('content', '# Page actions\n\nPages connect knowledge to delivery.');
    fixture.componentRef.setInput('classification', {
      status: 'aktuell',
      supersededBy: null,
      type: 'concept',
      analyzedAt: '2026-07-24',
    });
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/wiki/home').flush({ sections: [] });
    fixture.detectChanges();

    expect(fixture.componentInstance.context()).toEqual(expect.objectContaining({
      relPath: 'concepts/actions.md',
      pageType: 'concept',
      excerpt: 'Page actions Pages connect knowledge to delivery.',
    }));

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>(
      '[data-testid="page-action-archive"]',
    )!.click();
    const archive = http.expectOne('/api/projects/Demo/wiki/classification/concepts/actions.md');
    expect(archive.request.body).toEqual({ status: 'archived' });
    archive.flush({ relPath: 'concepts/actions.md.meta.json', sha: 'abc123' });
    fixture.detectChanges();

    expect(fixture.componentInstance.archived()).toBe(true);
    http.verify();
  });
});
