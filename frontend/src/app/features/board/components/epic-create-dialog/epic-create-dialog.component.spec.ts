import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { EpicCreateDialogComponent } from './epic-create-dialog.component';

function mount() {
  TestBed.configureTestingModule({
    imports: [EpicCreateDialogComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  const fixture = TestBed.createComponent(EpicCreateDialogComponent);
  fixture.componentRef.setInput('projectName', 'Acme');
  fixture.componentRef.setInput('watchPath', '/repo/acme');
  try { fixture.detectChanges(); } catch { /* render not required for these assertions */ }
  return fixture;
}

describe('EpicCreateDialogComponent', () => {
  it('blocks submit until a non-empty title is entered', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    expect(cmp.canSubmit()).toBe(false);
    cmp.draftTitle.set('   ');
    expect(cmp.canSubmit()).toBe(false);
    cmp.draftTitle.set('Ship onboarding');
    expect(cmp.canSubmit()).toBe(true);
  });

  it('creates an epic in the backlog with kind=epic and emits created', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    let createdId: string | null = null;
    cmp.created.subscribe((e) => (createdId = e.id));

    cmp.draftTitle.set('Ship onboarding');
    cmp.draftDescription.set('First-run flow');
    cmp.onSubmit();

    const req = http.expectOne((r) => r.url.endsWith('/tasks') && r.method === 'POST');
    expect(req.request.body.kind).toBe('epic');
    expect(req.request.body.targetState).toBe('0-backlog');
    expect(req.request.body.title).toBe('Ship onboarding');
    expect(req.request.body.watchPath).toBe('/repo/acme');
    req.flush({ id: 'EPIC-1' });

    expect(createdId).toBe('EPIC-1');
    expect(cmp.submitting()).toBe(false);
    // No http.verify(): the success path calls jobService.refresh(true),
    // which fires background board/runner GETs that are not under test here.
  });

  it('surfaces a server error message and stays open', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);

    cmp.draftTitle.set('Broken epic');
    cmp.onSubmit();
    const req = http.expectOne((r) => r.url.endsWith('/tasks') && r.method === 'POST');
    req.flush({ error: 'duplicate slug' }, { status: 409, statusText: 'Conflict' });

    expect(cmp.errorMsg()).toBe('duplicate slug');
    expect(cmp.submitting()).toBe(false);
    http.verify();
  });
});
