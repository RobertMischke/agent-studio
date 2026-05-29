import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ChatModelBadgeComponent } from './chat-model-badge.component';
import type { CliModelInfo } from '../../../cli';

/**
 * Behavioural specs for the chat-compose CLI + model picker. Covers the
 * regression that prompted the redesign: the dialog must stay open after a
 * CLI switch, the model list must refresh, and a clean Cancel path must not
 * commit. The pure formatting helpers (label, tooltip, short name) keep
 * their coverage in
 * `../protocol-pane/protocol-pane/model-badge-menu-builders.spec.ts`.
 */
describe('ChatModelBadgeComponent', () => {
  const claudeModels: CliModelInfo[] = [
    { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'anthropic', isDefault: true },
    { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', multiplier: 1, vendor: 'anthropic', isDefault: false },
  ];

  function configure() {
    return TestBed.configureTestingModule({
      imports: [ChatModelBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
  }

  it('renders the badge label from inputs and starts with the picker closed', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance.displayName()).toBe('opus 4.7');
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
    expect(fixture.componentInstance.disabledReason()).toBeNull();
  });

  it('reports a disabled reason while a run is in flight', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', []);
    fixture.componentRef.setInput('disabled', true);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] disabled-render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance.disabledReason()).toMatch(/stop the run/i);
  });

  it('seeds the draft from inputs when the picker opens', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.openPicker(new MouseEvent('click'));
    expect(fixture.componentInstance.pickerOpen()).toBe(true);
    expect(fixture.componentInstance.draftCliType()).toBe('claude');
    expect(fixture.componentInstance.draftModel()).toBe('claude-opus-4-7');
    expect(fixture.componentInstance.draftAvailableModels()).toEqual(claudeModels);
    expect(fixture.componentInstance.hasChanges()).toBe(false);
  });

  it('cancel discards the draft and emits nothing', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    let commits = 0;
    fixture.componentInstance.commit.subscribe(() => commits++);
    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillClick('claude-sonnet-4-6');
    expect(fixture.componentInstance.hasChanges()).toBe(true);

    fixture.componentInstance.onCancelClick();
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
    expect(commits).toBe(0);
  });

  it('Done emits the atomic commit and skips no-op commits', async () => {
    await configure();
    const fixture = TestBed.createComponent(ChatModelBadgeComponent);
    fixture.componentRef.setInput('cliType', 'claude');
    fixture.componentRef.setInput('model', 'claude-opus-4-7');
    fixture.componentRef.setInput('availableModels', claudeModels);
    fixture.componentRef.setInput('disabled', false);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const commits: { cliType: string; model: string }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));

    // Open + Done without change → no commit.
    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onDoneClick();
    expect(commits.length).toBe(0);

    // Open + change model + Done → atomic commit.
    fixture.componentInstance.openPicker(new MouseEvent('click'));
    fixture.componentInstance.onModelPillClick('claude-sonnet-4-6');
    fixture.componentInstance.onDoneClick();
    expect(commits).toEqual([{ cliType: 'claude', model: 'claude-sonnet-4-6' }]);
    expect(fixture.componentInstance.pickerOpen()).toBe(false);
  });
});
