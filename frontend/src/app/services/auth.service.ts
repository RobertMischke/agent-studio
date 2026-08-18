import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';

export interface AuthUser {
  id: string;
  username: string;
  displayName: string;
  role: 'owner' | 'operator' | 'viewer';
  projects: string[];
  disabled: boolean;
  mustChangePassword: boolean;
}

export type SecurityProfile = 'local' | 'networked' | 'public-demo';

export interface AuthStatus {
  profile: SecurityProfile;
  bootstrapRequired: boolean;
  authenticated: boolean;
  user?: AuthUser | null;
}

/** Credential-free browser auth state shared with the 401 response interceptor. */
@Injectable({ providedIn: 'root' })
export class AuthSessionState {
  readonly status = signal<AuthStatus | null>(null);
  readonly loading = signal(true);

  /**
   * The public read-only demo (AGT-W34). The server edge is the boundary that
   * actually denies every mutation; this signal exists so the UI explains that
   * instead of offering controls whose requests come back as typed denials.
   */
  readonly publicDemo = computed(() => this.status()?.profile === 'public-demo');

  readonly studioAllowed = computed(() => {
    const status = this.status();
    return status?.profile === 'local'
      || status?.profile === 'public-demo'
      || (status?.authenticated === true && !status.user?.mustChangePassword);
  });
  readonly networkedAuthenticated = computed(() => {
    const status = this.status();
    return status?.profile === 'networked' && status.authenticated === true;
  });

  expireNetworkedSession(): void {
    const status = this.status();
    if (status?.profile !== 'networked') return;
    this.status.set({
      profile: 'networked',
      bootstrapRequired: false,
      authenticated: false,
      user: null,
    });
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly session = inject(AuthSessionState);
  readonly status = this.session.status;
  readonly loading = this.session.loading;
  readonly studioAllowed = this.session.studioAllowed;
  readonly networkedAuthenticated = this.session.networkedAuthenticated;
  readonly publicDemo = this.session.publicDemo;

  initialize(): void {
    this.loading.set(true);
    this.http.get<AuthStatus>('/api/auth/status').subscribe({
      next: (status) => { this.status.set(status); this.loading.set(false); },
      error: () => { this.status.set(null); this.loading.set(false); },
    });
  }

  login(username: string, password: string): Observable<AuthStatus> {
    return this.http.post<AuthStatus>('/api/auth/login', { username, password })
      .pipe(tap((status) => this.status.set(status)));
  }

  bootstrap(username: string, password: string, displayName: string): Observable<AuthStatus> {
    return this.http.post<AuthStatus>('/api/auth/bootstrap', { username, password, displayName })
      .pipe(tap((status) => this.status.set(status)));
  }

  changePassword(currentPassword: string, newPassword: string): Observable<AuthUser> {
    return this.http.post<AuthUser>('/api/auth/change-password', { currentPassword, newPassword })
      .pipe(tap((user) => {
        const status = this.status();
        if (status) this.status.set({ ...status, authenticated: true, user });
      }));
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', {})
      .pipe(tap(() => this.session.expireNetworkedSession()));
  }
}
