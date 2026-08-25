import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProviderLimitBannerComponent } from './provider-limit-banner';

describe('ProviderLimitBannerComponent', () => {
  it('names a limited provider and explains automatic recovery', async () => {
    await TestBed.configureTestingModule({
      imports: [ProviderLimitBannerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProviderLimitBannerComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/runner/status').flush({
      projects: {},
      providerLimits: [{
        provider: 'claude',
        observedAt: '2026-08-23T22:00:00Z',
        retryAt: '2026-08-24T00:20:00Z',
        reason: 'Claude account session limit reached.',
        reportedReset: '12:20am',
      }],
    });
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent.replace(/\s+/g, ' ');
    expect(text).toContain('claude: limited until');
    expect(text).toContain('Waiting cards resume automatically');
    expect(text).toContain('other CLIs remain eligible');
  });
});
