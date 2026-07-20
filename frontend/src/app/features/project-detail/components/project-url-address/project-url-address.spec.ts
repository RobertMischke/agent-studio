import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ProjectUrlAddressComponent } from './project-url-address';

const RECORD_URL = 'http://localhost:4202';

function mount(url: string | null = RECORD_URL, editable = true) {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(ProjectUrlAddressComponent);
  fixture.componentRef.setInput('identity', 'Demo / Website');
  fixture.componentRef.setInput('url', url);
  fixture.componentRef.setInput('editable', editable);
  fixture.detectChanges();
  const input = fixture.nativeElement.querySelector('[data-testid="url-preview-addr-input"]') as HTMLInputElement;
  return { fixture, input };
}

describe('ProjectUrlAddressComponent', () => {
  it('mirrors the effective URL in a real, selectable input', () => {
    const { input } = mount();
    expect(input.value).toBe(RECORD_URL);
    input.setSelectionRange(0, input.value.length);
    expect((input.selectionEnd ?? 0) - (input.selectionStart ?? 0)).toBe(RECORD_URL.length);
  });

  it('emits navigate on Enter with the trimmed draft', () => {
    const { fixture, input } = mount();
    const targets: string[] = [];
    fixture.componentInstance.navigate.subscribe(target => targets.push(target));
    input.value = `  ${RECORD_URL}/health `;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(targets).toEqual([`${RECORD_URL}/health`]);
  });

  it('discards the draft on Escape and on blur', () => {
    const { fixture, input } = mount();
    input.value = 'http://typo';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(input.value).toBe(RECORD_URL);

    input.value = 'http://second-typo';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
    expect(input.value).toBe(RECORD_URL);
  });

  it('disables editing while the record is still resolving', () => {
    const { input } = mount(null, false);
    expect(input.disabled).toBe(true);
    expect(input.value).toBe('');
    expect(input.placeholder).toContain('Loading URL');
  });
});
