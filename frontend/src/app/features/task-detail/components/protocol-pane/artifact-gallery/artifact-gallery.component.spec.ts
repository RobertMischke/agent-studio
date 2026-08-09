import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { MediaLightboxService } from '../../../../../services/media-lightbox.service';
import { ArtifactGalleryComponent } from './artifact-gallery.component';
import type { ConversationArtifact } from './artifact-gallery.model';

const IMAGE = (id: string): ConversationArtifact => ({
  id,
  kind: 'image',
  path: `results/${id}.png`,
  fileName: `${id}.png`,
  label: `${id}.png`,
  url: `/api/results/${id}.png`,
  thumbnailUrl: `/api/thumbs/${id}.webp`,
  contentUrl: null,
});

function documentArtifact(kind: ConversationArtifact['kind'], extension: string): ConversationArtifact {
  return {
    id: kind,
    kind,
    path: `results/delivery.${extension}`,
    fileName: `delivery.${extension}`,
    label: `delivery.${extension}`,
    url: `/api/results/delivery.${extension}`,
    thumbnailUrl: null,
    contentUrl: `/api/files/results/delivery.${extension}`,
  };
}

describe('ArtifactGalleryComponent', () => {
  it('renders lazy thumbnail URLs and opens only the full images in the lightbox', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [IMAGE('light'), IMAGE('dark')]);
    await fixture.whenStable();

    const thumbnails = [...fixture.nativeElement.querySelectorAll('img')] as HTMLImageElement[];
    expect(thumbnails.map((image) => image.getAttribute('src'))).toEqual([
      '/api/thumbs/light.webp', '/api/thumbs/dark.webp',
    ]);
    expect(thumbnails.every((image) => image.getAttribute('loading') === 'lazy')).toBe(true);

    (fixture.nativeElement.querySelector('[data-testid="artifact-gallery-thumbnail"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    const lightbox = TestBed.inject(MediaLightboxService);
    expect(lightbox.count()).toBe(2);
    expect(lightbox.active()?.src).toBe('/api/results/light.png');
    expect(lightbox.active()?.alt).toBe('light.png · results/light.png');
  });

  it('loads and renders a typed diff preview on demand', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [documentArtifact('diff', 'diff')]);
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-diff"]') as HTMLButtonElement).click();
    const request = TestBed.inject(HttpTestingController).expectOne('/api/files/results/delivery.diff');
    request.flush('@@ -1 +1 @@\n-old\n+new');
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('@@ -1 +1 @@');
    expect(fixture.nativeElement.querySelector('[data-line-kind="delete"]')?.textContent).toContain('-old');
    expect(fixture.nativeElement.querySelector('[data-line-kind="add"]')?.textContent).toContain('+new');
  });

  it('opens HTML through the artifact URL instead of embedding another viewer', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [documentArtifact('html', 'html')]);
    await fixture.whenStable();
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);

    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-html"]') as HTMLButtonElement).click();

    expect(open).toHaveBeenCalledWith('/api/results/delivery.html', '_blank', 'noopener,noreferrer');
    expect(fixture.nativeElement.querySelector('iframe')).toBeNull();
    open.mockRestore();
  });

  it('formats JSON and log previews and exposes their copy actions', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [
      documentArtifact('json', 'json'),
      documentArtifact('log', 'log'),
    ]);
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-json"]') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/files/results/delivery.json')
      .flush('{"pinned":true,"images":8}');
    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-log"]') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/files/results/delivery.log')
      .flush('capture light: ok\ncapture dark: ok');
    await fixture.whenStable();

    const json = fixture.nativeElement.querySelector('[data-testid="artifact-document-json"]');
    const log = fixture.nativeElement.querySelector('[data-testid="artifact-document-log"]');
    expect(json.textContent).toContain('"pinned": true');
    expect(log.textContent).toContain('capture dark: ok');
    expect(json.querySelector('[data-testid="artifact-document-copy-json"]')).not.toBeNull();
    expect(log.querySelector('[data-testid="artifact-document-copy-log"]')).not.toBeNull();
  });
});
