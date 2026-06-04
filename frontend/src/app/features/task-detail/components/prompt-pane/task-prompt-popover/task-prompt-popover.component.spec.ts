import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ModalStackService } from '../../../../../services/modal-stack.service';
import { TaskPromptPopoverComponent } from './task-prompt-popover.component';

async function mount(markdown: string | null | undefined) {
  await TestBed.configureTestingModule({
    imports: [TaskPromptPopoverComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(TaskPromptPopoverComponent);
  fixture.componentRef.setInput('markdown', markdown);
  fixture.componentRef.setInput('jobId', 'job-1');
  fixture.componentRef.setInput('watchPath', '/tmp/watch');
  fixture.detectChanges();
  return fixture;
}

function el(fixture: { nativeElement: HTMLElement }, testid: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${testid}"]`);
}

describe('TaskPromptPopoverComponent', () => {
  it('hides the trigger when there is no prompt text', async () => {
    const fixture = await mount('   ');
    expect(fixture.componentInstance.hasPrompt()).toBe(false);
    expect(el(fixture, 'overview-prompt-trigger')).toBeNull();
  });

  it('renders the trigger when prompt text is present', async () => {
    const fixture = await mount('# Hello\n\nDo the thing.');
    expect(fixture.componentInstance.hasPrompt()).toBe(true);
    expect(el(fixture, 'overview-prompt-trigger')).not.toBeNull();
    // Closed by default — no panel.
    expect(el(fixture, 'overview-prompt-popover')).toBeNull();
  });

  it('opens the read-only popover on trigger click and closes on close button', async () => {
    const fixture = await mount('Task body markdown');
    const trigger = el(fixture, 'overview-prompt-trigger') as HTMLButtonElement;

    trigger.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);
    expect(el(fixture, 'overview-prompt-popover')).not.toBeNull();
    expect(el(fixture, 'overview-prompt-popover-body')).not.toBeNull();

    (el(fixture, 'overview-prompt-popover-close') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);
    expect(el(fixture, 'overview-prompt-popover')).toBeNull();
  });

  it('registers a modal-stack entry only while open (Escape arbitration)', async () => {
    const fixture = await mount('Task body markdown');
    const stack = TestBed.inject(ModalStackService);
    stack.clearForTest();

    fixture.componentInstance.toggle(new MouseEvent('click'));
    fixture.detectChanges();
    expect(stack.topId()).toBe('task-prompt-popover');

    fixture.componentInstance.close();
    fixture.detectChanges();
    expect(stack.topId()).toBeNull();
  });

  it('closes on click outside the host', async () => {
    const fixture = await mount('Task body markdown');
    fixture.componentInstance.toggle(new MouseEvent('click'));
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    fixture.componentInstance.onDocClick(
      new MouseEvent('click', { bubbles: true }),
    );
    fixture.detectChanges();
    // target defaults to null (outside the host) -> closes.
    expect(fixture.componentInstance.open()).toBe(false);
  });
});
