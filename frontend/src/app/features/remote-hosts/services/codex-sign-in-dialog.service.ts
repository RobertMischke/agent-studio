import { Injectable, signal } from '@angular/core';

export interface CodexSignInDialogTarget {
  hostId: string;
  hostName: string;
  sshTarget: string;
  aliases: readonly string[];
}

@Injectable({ providedIn: 'root' })
export class CodexSignInDialogService {
  readonly active = signal<CodexSignInDialogTarget | null>(null);

  open(target: CodexSignInDialogTarget): void {
    if (!target.hostId) return;
    this.active.set(target);
  }

  close(): void {
    this.active.set(null);
  }
}
