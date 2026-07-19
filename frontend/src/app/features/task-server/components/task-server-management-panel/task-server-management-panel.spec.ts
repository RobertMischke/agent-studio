import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskServerManagementPanelComponent } from './task-server-management-panel';
import type { ManagementActionKind } from '../../models/task-server.model';

describe('TaskServerManagementPanelComponent', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [TaskServerManagementPanelComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    return TestBed.createComponent(TaskServerManagementPanelComponent);
  }

  it('renders the three sweeps and emits run on click', async () => {
    const fixture = await mount();
    const fired: ManagementActionKind[] = [];
    fixture.componentInstance.run.subscribe((k) => fired.push(k));
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="task-server-action-archive-sweep"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-action-orphan-scan"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-action-fixture-cleanup"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-results-empty"]')).toBeTruthy();

    (el.querySelector('[data-testid="task-server-action-orphan-scan"]') as HTMLButtonElement).click();
    expect(fired).toEqual(['orphan-scan']);

    fixture.destroy();
  });

  it('disables the buttons and suppresses run while a sweep is busy', async () => {
    const fixture = await mount();
    const fired: ManagementActionKind[] = [];
    fixture.componentInstance.run.subscribe((k) => fired.push(k));
    fixture.componentRef.setInput('busyAction', 'archive-sweep');
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    const btn = el.querySelector('[data-testid="task-server-action-orphan-scan"]') as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    btn.click();
    expect(fired).toEqual([]);

    fixture.destroy();
  });
});
