import { describe, expect, it, beforeEach } from 'vitest';
import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { MarkdownImageLightboxDirective } from './markdown-image-lightbox.directive';
import { MediaLightboxService } from '../services/media-lightbox.service';

@Component({
  standalone: true,
  imports: [MarkdownImageLightboxDirective],
  template: `<div appMarkdownLightbox [innerHTML]="safeHtml"></div>`,
})
class HostComponent {
  private readonly sanitizer = inject(DomSanitizer);
  html = '';
  get safeHtml(): SafeHtml {
    // The directive runs on top of [innerHTML] which goes through Angular's
    // DOM sanitizer. Production callers always set this via
    // `bypassSecurityTrustHtml(...)` (the upstream rendered + sanitised
    // pipeline owns the trust call); mirror that here so legacy markup
    // such as `<button data-results-lightbox>` survives into the DOM.
    return this.sanitizer.bypassSecurityTrustHtml(this.html);
  }
}

describe('MarkdownImageLightboxDirective', () => {
  let svc: MediaLightboxService;
  let fixture: ReturnType<typeof TestBed.createComponent<HostComponent>>;

  function mount(html: string): HTMLElement {
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.html = html;
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('div') as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideZonelessChangeDetection()],
    });
    svc = TestBed.inject(MediaLightboxService);
  });

  it('opens the lightbox when a markdown image inside is clicked', () => {
    const host = mount('<p>Screenshot: <img src="/api/jobs/x/attachments/y.png" alt="Y"></p>');
    const img = host.querySelector('img')!;
    img.click();
    const active = svc.active();
    expect(active).not.toBeNull();
    expect(active?.src).toContain('/api/jobs/x/attachments/y.png');
    expect(active?.alt).toBe('Y');
  });

  it('opens via Enter key when an image has focus', () => {
    const host = mount('<img src="/x.png" alt="alt-y">');
    const img = host.querySelector('img')!;
    // Directive's view-init pass added tabindex + role.
    expect(img.getAttribute('tabindex')).toBe('0');
    expect(img.getAttribute('role')).toBe('button');
    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true });
    img.dispatchEvent(event);
    expect(svc.active()?.alt).toBe('alt-y');
  });

  it('honours legacy data-results-lightbox button wrappers', () => {
    const host = mount(
      '<figure><button type="button" data-results-lightbox="/api/jobs/x/results/foo.png" data-results-alt="Foo"><img src="/api/jobs/x/results/foo.png" alt="Foo"></button></figure>'
    );
    const btn = host.querySelector('button')!;
    btn.click();
    expect(svc.active()?.src).toBe('/api/jobs/x/results/foo.png');
    expect(svc.active()?.alt).toBe('Foo');
  });

  it('skips images with no usable src', () => {
    const host = mount('<img src="" alt="empty">');
    const img = host.querySelector('img')!;
    expect(img.getAttribute('tabindex')).toBeNull();
    img.click();
    expect(svc.active()).toBeNull();
  });
});
