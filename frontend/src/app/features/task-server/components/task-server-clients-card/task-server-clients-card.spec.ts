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
});
