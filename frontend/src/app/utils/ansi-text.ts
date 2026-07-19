/** Remove terminal control sequences from text rendered outside a terminal. */
// eslint-disable-next-line no-control-regex -- OSC sanitising intentionally matches terminal control bytes.
const OSC_SEQUENCE = new RegExp('\\u001B\\][^\\u0007]*(?:\\u0007|\\u001B\\\\)', 'g');
// eslint-disable-next-line no-control-regex -- CSI sanitising intentionally matches terminal control bytes.
const CSI_SEQUENCE = new RegExp('(?:\\u001B\\[|\\u009B)[0-?]*[ -/]*[@-~]', 'g');
const NAKED_SGR_SEQUENCE = /\[(?:\d{1,3}(?:;\d{1,3})*)?m/g;

export function stripAnsi(text: string): string {
  if (!text) return text;
  return text
    // Operating-system command sequences, terminated by BEL or ST.
    .replace(OSC_SEQUENCE, '')
    // CSI sequences such as ESC[33m, ESC[2K, and the 8-bit CSI form.
    .replace(CSI_SEQUENCE, '')
    // Some persisted logs have already lost ESC but retain naked SGR colours.
    .replace(NAKED_SGR_SEQUENCE, '');
}
