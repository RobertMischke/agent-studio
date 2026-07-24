import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskServerClientsCardComponent } from './task-server-clients-card';
import type { TaskServerClient } from '../../models/task-server.model';

const CLIENTS: TaskServerClient[] = [
  { id: 'local-default', displayName: 'Local Default', emoji: '🦊', kind: 'human', lastSeenAt: null, ownedTaskCount: 5 },
  { id: 'retired-one', displayName: 'Old Runner', emoji: null, kind: 'retired', lastSeenAt: null, ownedTaskCount: 0 },
];

describe('TaskServerClientsCardComponent', () => {
  it('renders one row per client and marks retired ones calm', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskServerClientsCardComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskServerClientsCardComponent);
    fixture.componentRef.setInput('clients', CLIENTS);
    fixture.componentRef.setInput('now', Date.parse('2026-07-11T12:00:00Z'));
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    const rows = el.querySelectorAll('[data-testid="task-server-clients"] > li');
    expect(rows.length).toBe(2);
    expect(el.querySelector('[data-testid="task-server-client-retired-one"]')?.classList.contains('client--retired')).toBe(true);

    fixture.destroy();
  });

  it('shows an empty note when there are no clients', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskServerClientsCardComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskServerClientsCardComponent);
    fixture.componentRef.setInput('clients', []);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="task-server-clients-empty"]')).toBeTruthy();

    fixture.destroy();
  });

  it('previews then explicitly confirms a Runner lifecycle command', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskServerClientsCardComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskServerClientsCardComponent);
    fixture.componentRef.setInput('clients', [CLIENTS[0]]);
    const fired: unknown[] = [];
    fixture.componentInstance.run.subscribe(event => fired.push(event));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    (el.querySelector('[data-testid="task-server-runner-runner-drain-local-default"]') as HTMLButtonElement).click();
    expect(fired).toEqual([{ kind: 'runner-drain', runnerId: 'local-default', confirmed: false }]);

    fixture.componentRef.setInput('results', [{
      kind: 'runner-drain', ranAt: '2026-07-20T00:00:00Z', summary: 'Would drain.',
      affected: 0, matched: 1, dryRun: true, commandId: 'cmd-preview', state: 'completed',
      targetId: 'local-default',
    }]);
    fixture.detectChanges();
    (el.querySelector('[data-testid="task-server-runner-confirm-runner-drain-local-default"]') as HTMLButtonElement).click();
    expect(fired.at(-1)).toEqual({ kind: 'runner-drain', runnerId: 'local-default', confirmed: true });
    fixture.destroy();
  });

  it('previews and confirms enrollment with the entered Runner name', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskServerClientsCardComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskServerClientsCardComponent);
    fixture.componentRef.setInput('clients', []);
    fixture.componentRef.setInput('securityAvailable', true);
    const fired: unknown[] = [];
    fixture.componentInstance.run.subscribe(event => fired.push(event));
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const input = el.querySelector('#task-server-runner-name') as HTMLInputElement;
    input.value = 'build-runner-02';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el.querySelector('[data-testid="task-server-runner-enrollment-preview"]') as HTMLButtonElement).click();
    expect(fired).toEqual([{ kind: 'runner-enrollment-create', runnerName: 'build-runner-02', confirmed: false }]);

    fixture.componentRef.setInput('results', [{
      kind: 'runner-enrollment-create', ranAt: '2026-07-20T00:00:00Z', summary: 'Would enroll.',
      affected: 0, matched: 1, dryRun: true, commandId: 'cmd-enroll-preview', state: 'completed',
      targetId: 'build-runner-02',
    }]);
    fixture.detectChanges();
    (el.querySelector('[data-testid="task-server-runner-enrollment-confirm"]') as HTMLButtonElement).click();
    expect(fired.at(-1)).toEqual({ kind: 'runner-enrollment-create', runnerName: 'build-runner-02', confirmed: true });
    fixture.destroy();
  });
});
