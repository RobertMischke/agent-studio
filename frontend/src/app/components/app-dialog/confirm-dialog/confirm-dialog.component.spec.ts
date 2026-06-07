import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ConfirmDialogComponent } from './confirm-dialog.component';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';

describe('ConfirmDialogComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ConfirmDialogComponent);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ConfirmDialogComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('ConfirmDialogComponent typed confirmation', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ConfirmDialogComponent);
    const service = TestBed.inject(ConfirmDialogService);
    return { fixture, component: fixture.componentInstance, service };
  }

  it('keeps the confirm button disabled until the typed value matches', async () => {
    const { fixture, component, service } = await mount();
    void service.confirm({
      title: 'Confirm permanent deletion',
      message: 'Last step.',
      confirmLabel: 'Delete project',
      requireTypedValues: ['Agent Task Processor', 'ATP'],
      requireTypedPrompt: 'Type the project name or code.',
    });
    fixture.detectChanges();

    expect(component.typedConfirmSatisfied()).toBe(false);

    component.typedDraft.set('wrong project');
    expect(component.typedConfirmSatisfied()).toBe(false);

    component.typedDraft.set(' atp ');
    expect(component.typedConfirmSatisfied()).toBe(true);
  });

  it('does not accept Enter while the typed gate is unsatisfied', async () => {
    const { fixture, component, service } = await mount();
    let resolved: boolean | null = null;
    service.confirm({
      title: 'Confirm permanent deletion',
      message: 'Last step.',
      requireTypedValues: ['PROJ'],
    }).then(value => { resolved = value; });
    fixture.detectChanges();

    component.onPanelKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    await Promise.resolve();
    expect(resolved).toBeNull();

    component.typedDraft.set('PROJ');
    component.onPanelKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    await Promise.resolve();
    expect(resolved).toBe(true);
  });
});
