import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { OrchestratorContextPickerComponent } from './orchestrator-context-picker.component';

describe('OrchestratorContextPickerComponent', () => {
  afterEach(() => {
    vi.useRealTimers();
    TestBed.inject(HttpTestingController).verify();
  });

  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextPickerComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorContextPickerComponent);
    fixture.componentRef.setInput('projectId', 'Agent Studio');
    return fixture;
  }

  it('adds the active diff with its selected file and hunk, then clears on project change', async () => {
    const fixture = await makeFixture();
    const sha = '0123456789012345678901234567890123456789';
    fixture.componentRef.setInput('currentReference', {
      kind: 'diff',
      reference: sha,
      revision: sha,
      projectId: 'Agent Studio',
      repositoryId: 'Agent Studio',
      path: 'frontend/src/app/app.ts',
      lineRanges: [{ startLine: 8, endLine: 16 }],
    });
    fixture.detectChanges();

    fixture.componentInstance.toggle();
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="orch-context-add-current"]')!.click();

    expect(fixture.componentInstance.snapshot()).toEqual([expect.objectContaining({
      kind: 'diff',
      reference: sha,
      path: 'frontend/src/app/app.ts',
      lineRanges: [{ startLine: 8, endLine: 16 }],
    })]);

    fixture.componentRef.setInput('projectId', 'Other Project');
    fixture.detectChanges();
    expect(fixture.componentInstance.snapshot()).toEqual([]);
  });

  it('searches project-scoped known sources and adds typed file, commit and diff references', async () => {
    vi.useFakeTimers();
    const fixture = await makeFixture();
    fixture.detectChanges();
    fixture.componentInstance.toggle();
    fixture.detectChanges();
    const input = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLInputElement>('[data-testid="orch-context-search"]')!;
    input.value = 'context';
    input.dispatchEvent(new Event('input'));
    vi.advanceTimersByTime(130);

    const sha = 'abcdefabcdefabcdefabcdefabcdefabcdefabcd';
    TestBed.inject(HttpTestingController).expectOne(request =>
      request.url === '/api/search'
      && request.params.get('domains') === 'commits,files'
      && request.params.get('q') === 'context'
    ).flush({
      files: [knownFile('Agent Studio', sha), knownFile('Other Project', sha)],
      commits: [knownCommit('Agent Studio', sha), knownCommit('Other Project', sha)],
      errors: {},
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const addFile = root.querySelector<HTMLButtonElement>('[data-testid="orch-context-files"] button')!;
    const commitButtons = root.querySelectorAll<HTMLButtonElement>('[data-testid="orch-context-commits"] button');
    addFile.click();
    commitButtons[0].click();
    commitButtons[1].click();

    expect(fixture.componentInstance.snapshot().map(reference => reference.kind))
      .toEqual(['repository-file', 'commit', 'diff']);
    expect(root.textContent).not.toContain('other/context.ts');
  });
});

function knownFile(projectName: string, revision: string) {
  return {
    domain: 'files',
    projectName,
    title: 'context.ts',
    subtitle: projectName === 'Agent Studio' ? 'src/context.ts' : 'other/context.ts',
    path: projectName === 'Agent Studio' ? 'src/context.ts' : 'other/context.ts',
    repositoryId: projectName,
    revision,
  };
}

function knownCommit(projectName: string, sha: string) {
  return {
    domain: 'commits',
    projectName,
    title: 'Add context resolution',
    subtitle: sha.slice(0, 8),
    sha,
    repositoryId: projectName,
    revision: sha,
  };
}
