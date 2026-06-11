export function ensureBrowserStorage(): void {
  const target = globalThis as typeof globalThis & { localStorage?: Storage };
  if (typeof target.localStorage !== 'undefined') return;

  const values = new Map<string, string>();
  const storage: Storage = {
    get length() {
      return values.size;
    },
    clear() {
      values.clear();
    },
    getItem(key: string) {
      return values.has(key) ? values.get(key)! : null;
    },
    key(index: number) {
      return [...values.keys()][index] ?? null;
    },
    removeItem(key: string) {
      values.delete(key);
    },
    setItem(key: string, value: string) {
      values.set(key, String(value));
    },
  };

  Object.defineProperty(target, 'localStorage', {
    configurable: true,
    value: storage,
  });
}
