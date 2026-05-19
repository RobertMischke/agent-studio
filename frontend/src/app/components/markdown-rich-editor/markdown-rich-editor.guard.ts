/**
 * Pure no-clobber rule for the markdown rich editor's autosave path.
 * Returns true when the editor should actually persist its current value,
 * false when the save would either be a no-op (value unchanged) or a
 * stub-mount race that would clobber the real prompt before the parent's
 * async fetch has arrived.
 *
 * The race we are guarding against: on mount, the parent passes
 * `value=''` until detail() resolves. Tiptap's onUpdate has been
 * observed to fire for the constructor's content load on some versions,
 * which schedules an autosave 600ms later. If detail() takes longer
 * than 600ms, the save fires with the stub empty value, the parent
 * writes prompt.md='' to disk, and the next refetch reads back that
 * empty value, wiping the editor permanently. The rule below blocks
 * that exact path while still allowing legitimate empty-clear saves
 * (the user intentionally deletes everything).
 *
 * Lives in its own file so unit tests can import it without dragging in
 * the Angular runtime.
 */
export function shouldEmitEditorSave(input: {
  current: string;
  committed: string;
  hasUserEdit: boolean;
}): boolean {
  if (input.current === input.committed) return false;
  if (!input.hasUserEdit) return false;
  return true;
}
