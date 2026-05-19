import { describe, expect, it } from 'vitest';
import { shouldEmitEditorSave } from './markdown-rich-editor.guard';

describe('shouldEmitEditorSave', () => {
  it('blocks the empty-initial-mount race that clobbered prompt.md', () => {
    // Parent has not delivered detail() yet, so committed is the stub ''.
    // Tiptap fired onUpdate for the constructor content load on this
    // version, leaving sourceValue at ''. The autosave timer fires before
    // the real prompt arrives. Saving here would write '' to disk and the
    // next refetch would wipe the editor permanently.
    const result = shouldEmitEditorSave({
      current: '',
      committed: '',
      hasUserEdit: false
    });
    expect(result).toBe(false);
  });

  it('blocks an autosave when the value did not actually change', () => {
    const result = shouldEmitEditorSave({
      current: 'task body',
      committed: 'task body',
      hasUserEdit: true
    });
    expect(result).toBe(false);
  });

  it('blocks programmatic content-load echoes without user input', () => {
    // valueEffect just set sourceValue and committedValue to the parent
    // value; no user edit happened. Even if scheduleAutosave somehow
    // fires, the save must be suppressed.
    const result = shouldEmitEditorSave({
      current: 'task body',
      committed: 'task body',
      hasUserEdit: false
    });
    expect(result).toBe(false);
  });

  it('allows a real edit to persist', () => {
    const result = shouldEmitEditorSave({
      current: 'task body with new line',
      committed: 'task body',
      hasUserEdit: true
    });
    expect(result).toBe(true);
  });

  it('allows a deliberate clear-to-empty (user deletes everything)', () => {
    const result = shouldEmitEditorSave({
      current: '',
      committed: 'task body',
      hasUserEdit: true
    });
    expect(result).toBe(true);
  });

  it('blocks divergence without user input (defensive)', () => {
    // A divergence the user did not produce is suspicious; do not save.
    // Anything that flips hasUserEdit must do so in response to an actual
    // user-driven path (onUpdate with diff, updateSource).
    const result = shouldEmitEditorSave({
      current: '',
      committed: 'task body',
      hasUserEdit: false
    });
    expect(result).toBe(false);
  });
});
