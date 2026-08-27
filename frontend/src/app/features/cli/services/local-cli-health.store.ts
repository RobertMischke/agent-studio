import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { LocalCliHealthSnapshot } from '../models/cli.model';

@Injectable({ providedIn: 'root' })
export class LocalCliHealthStore {
  private readonly http = inject(HttpClient);

  readonly snapshot = signal<LocalCliHealthSnapshot | null>(null);
  readonly loading = signal(false);

  refresh(): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.http.get<LocalCliHealthSnapshot>('/api/cli/local-health').subscribe({
      next: snapshot => {
        this.snapshot.set(snapshot);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }
}
