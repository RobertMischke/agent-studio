import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PageContextService } from '../../../../services/page-context.service';
import { PageActionBarComponent } from './page-action-bar';

const WORKBENCH = {
  projectName: 'Demo',
  relPath: 'quality/action-bar/index.html',
  title: 'Action bar',
  pageType: 'workbench' as const,
  excerpt: 'Shared page actions.',
};

describe('PageActionBarComponent', () => {
  it('keeps standard actions in order and adds the type-specific action', async () => {
    await TestBed.configureTestingModule({
      imports: [PageActionBarComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PageActionBarComponent);
    fixture.componentRef.setInput('context', WORKBENCH);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo/wiki/home')
      .flush({ sections: [{ title: 'Start', links: [] }] });
    fixture.detectChanges();

    const buttons = [...fixture.nativeElement.querySelectorAll('button')] as HTMLButtonElement[];
    expect(buttons.map(button => button.textContent?.trim())).toEqual([
      'Create Task in Project',
      'Archive',
      'Open in Orchestrator Chat',
      'Pin to Home',
      'Build as feature',
    ]);
    expect(fixture.nativeElement.querySelector('[data-testid="page-action-bar"]')
      ?.getAttribute('data-page-type')).toBe('workbench');
  });

  it('publishes task and chat requests with the active page context', async () => {
    await TestBed.configureTestingModule({
      imports: [PageActionBarComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PageActionBarComponent);
    fixture.componentRef.setInput('context', WORKBENCH);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo/wiki/home')
      .flush({ sections: [{ title: 'Start', links: [] }] });
    fixture.detectChanges();
    const pages = TestBed.inject(PageContextService);
    const taskRequests: string[] = [];
    const chatRequests: string[] = [];
    pages.createTaskRequests$.subscribe(request => taskRequests.push(request.intent));
    pages.openChatRequests$.subscribe(context => chatRequests.push(context.relPath));

    fixture.nativeElement.querySelector('[data-testid="page-action-create-task"]').click();
    fixture.nativeElement.querySelector('[data-testid="page-action-extra"]').click();
    fixture.nativeElement.querySelector('[data-testid="page-action-open-chat"]').click();

    expect(taskRequests).toEqual(['create-task', 'build-feature']);
    expect(chatRequests).toEqual(['quality/action-bar/index.html']);
    expect(pages.activePage()).toEqual(WORKBENCH);
  });

  it('keeps personal stars separate while pinning into a chosen shared Home section', async () => {
    await TestBed.configureTestingModule({
      imports: [PageActionBarComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PageActionBarComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('context', WORKBENCH);
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/home').flush({
      sections: [
        { title: 'Start', links: [] },
        { title: 'UI', links: [] },
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="page-action-pin-home"]')!.click();
    fixture.detectChanges();
    const dialog = document.querySelector<HTMLElement>('[data-testid="page-pin-dialog"]')!;
    expect(dialog.textContent).toContain('Stars remain your personal shortlist');
    const section = dialog.querySelector<HTMLSelectElement>('[data-testid="page-pin-section"]')!;
    section.value = 'UI';
    section.dispatchEvent(new Event('change'));
    dialog.querySelector<HTMLButtonElement>('[data-testid="page-pin-submit"]')!.click();

    const request = http.expectOne('/api/projects/Demo/wiki/home/pins/quality/action-bar/index.html');
    expect(request.request.body).toEqual({
      pinned: true,
      sectionTitle: 'UI',
      label: 'Action bar',
      note: 'Shared page actions.',
    });
    request.flush({ relPath: 'docs/app/config/home.json', sha: 'abc123' });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="page-action-pin-home"]')?.textContent)
      .toContain('Unpin from Home');
    http.verify();
  });
});
