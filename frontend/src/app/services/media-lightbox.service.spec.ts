import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { MediaLightboxService } from './media-lightbox.service';

describe('MediaLightboxService', () => {
  let service: MediaLightboxService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), MediaLightboxService],
    });
    service = TestBed.inject(MediaLightboxService);
  });

  it('starts with no active media', () => {
    expect(service.active()).toBeNull();
  });

  it('opens with src and alt', () => {
    service.open({ src: '/api/jobs/x/attachments/y.png', alt: 'Screenshot' });
    const active = service.active();
    expect(active).not.toBeNull();
    expect(active?.src).toBe('/api/jobs/x/attachments/y.png');
    expect(active?.alt).toBe('Screenshot');
  });

  it('normalises a missing alt to empty string', () => {
    service.open({ src: '/x.png' });
    expect(service.active()?.alt).toBe('');
  });

  it('ignores a blank src so a misfire never opens a black overlay', () => {
    service.open({ src: '   ', alt: 'noop' });
    expect(service.active()).toBeNull();
  });

  it('close() clears the active media', () => {
    service.open({ src: '/x.png', alt: 'y' });
    expect(service.active()).not.toBeNull();
    service.close();
    expect(service.active()).toBeNull();
  });
});
