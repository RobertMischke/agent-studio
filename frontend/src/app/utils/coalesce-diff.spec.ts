import { describe, it, expect } from 'vitest';
import { coalesceDiffByFile } from './coalesce-diff';

/** Two independent changes to the same file, as the aggregate diff concatenates them. */
const REPEATED_README = `diff --git a/README.md b/README.md
index 1111111..2222222 100644
--- a/README.md
+++ b/README.md
@@ -1,3 +1,4 @@
 # Title
+first change
 body
diff --git a/README.md b/README.md
index 2222222..3333333 100644
--- a/README.md
+++ b/README.md
@@ -10,3 +10,4 @@
 more
+second change
 tail
`;

describe('coalesceDiffByFile', () => {
  it('merges repeated same-file sections into one header with grouped hunks', () => {
    const out = coalesceDiffByFile(REPEATED_README);
    // One header for the file...
    const headers = out.match(/^diff --git /gm) ?? [];
    expect(headers.length).toBe(1);
    // ...but both hunks preserved under it.
    const hunks = out.match(/^@@ /gm) ?? [];
    expect(hunks.length).toBe(2);
    expect(out).toContain('first change');
    expect(out).toContain('second change');
    // The second section's redundant header lines are dropped.
    expect(out).not.toContain('index 2222222..3333333');
  });

  it('leaves a single-file diff byte-for-byte unchanged', () => {
    const single = `diff --git a/src/a.ts b/src/a.ts
index aaa..bbb 100644
--- a/src/a.ts
+++ b/src/a.ts
@@ -1,2 +1,2 @@
-old
+new
`;
    expect(coalesceDiffByFile(single)).toBe(single);
  });

  it('keeps genuinely different files as separate headers', () => {
    const multi = `diff --git a/a.md b/a.md
--- a/a.md
+++ b/a.md
@@ -1 +1 @@
-a
+A
diff --git a/b.md b/b.md
--- a/b.md
+++ b/b.md
@@ -1 +1 @@
-b
+B
`;
    const out = coalesceDiffByFile(multi);
    expect(out).toBe(multi);
    expect((out.match(/^diff --git /gm) ?? []).length).toBe(2);
  });

  it('groups three occurrences and preserves hunk order', () => {
    const three = `diff --git a/f.md b/f.md
--- a/f.md
+++ b/f.md
@@ -1 +1 @@
+one
diff --git a/f.md b/f.md
--- a/f.md
+++ b/f.md
@@ -1 +1 @@
+two
diff --git a/f.md b/f.md
--- a/f.md
+++ b/f.md
@@ -1 +1 @@
+three
`;
    const out = coalesceDiffByFile(three);
    expect((out.match(/^diff --git /gm) ?? []).length).toBe(1);
    const oneAt = out.indexOf('one');
    const twoAt = out.indexOf('two');
    const threeAt = out.indexOf('three');
    expect(oneAt).toBeLessThan(twoAt);
    expect(twoAt).toBeLessThan(threeAt);
  });

  it('handles empty / null input', () => {
    expect(coalesceDiffByFile('')).toBe('');
    expect(coalesceDiffByFile(null)).toBe('');
    expect(coalesceDiffByFile(undefined)).toBe('');
  });
});
