import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { PanelKeyboardContainmentDirective } from './panel-keyboard-containment.directive';

@Component({
  imports: [PanelKeyboardContainmentDirective],
  templateUrl: './panel-keyboard-containment.directive.spec.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class PanelKeyboardContainmentHost {}

describe('PanelKeyboardContainmentDirective', () => {
  let fixture: ComponentFixture<PanelKeyboardContainmentHost>;
  let scope: HTMLElement;
  let surface: HTMLElement;
  let child: HTMLButtonElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PanelKeyboardContainmentHost] }).compileComponents();
    fixture = TestBed.createComponent(PanelKeyboardContainmentHost);
    fixture.detectChanges();
    await Promise.resolve();
    scope = fixture.nativeElement.querySelector('[data-testid="scope"]');
    surface = fixture.nativeElement.querySelector('[data-testid="surface"]');
    child = fixture.nativeElement.querySelector('[data-testid="child"]');
  });

  it('moves initial focus into the preferred panel surface', () => {
    expect(document.activeElement).toBe(surface);
  });

  it.each(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End'])(
    'contains %s without cancelling native scrolling',
    (key) => {
      const parentKeydown = vi.fn();
      scope.addEventListener('keydown', parentKeydown);
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });

      child.dispatchEvent(event);

      expect(parentKeydown).not.toHaveBeenCalled();
      expect(event.defaultPrevented).toBe(false);
    },
  );

  it.each(['Escape', 'Tab'])('leaves %s propagation unchanged', (key) => {
    const parentKeydown = vi.fn();
    scope.addEventListener('keydown', parentKeydown);

    child.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));

    expect(parentKeydown).toHaveBeenCalledOnce();
  });
});
