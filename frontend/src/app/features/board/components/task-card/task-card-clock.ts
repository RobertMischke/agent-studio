import { signal } from '@angular/core';

// One shared 30s clock keeps every card timer in lockstep without NG0100-prone Date.now() reads.
export const taskCardNow = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => taskCardNow.set(Date.now()), 30_000);
}
