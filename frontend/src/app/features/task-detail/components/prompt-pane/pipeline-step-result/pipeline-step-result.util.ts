/**
 * Cleaning helpers for a pipeline step's raw on-disk result markdown so the
 * Overview step-result card renders well-formatted prose instead of the raw
 * file. The aspect reports (`aspect-{id}.md`) and the CORE run summary
 * (`status.md`) are the two sources; the aspect file in particular wraps the
 * model's prose in a bare ``` fence and trails machine sentinels
 * (`[[ASPECT_VERDICT: …]]`, `[[TASK_DONE]]`) that should never reach the
 * rendered card. The user-triggered code-review report uses the same shape
 * with a "Reviewer reply" heading, so this helper also normalises that report
 * before the code-review panel hands it to the shared markdown renderer.
 *
 * Pure string transforms with no markdown parsing, so they are cheap and
 * unit-testable without a DOM. A `status.md` (already clean prose, no
 * frontmatter, no fences) passes through unchanged.
 */

/** A line that is only a machine sentinel token, e.g. `[[TASK_DONE]]`. */
const SENTINEL_LINE = /^\[\[[^\]]*\]\]$/;

/**
 * Normalise a raw step-result markdown body for display:
 *  1. strip a leading YAML frontmatter block,
 *  2. unwrap the bare ``` fence around the "Model reply" section so its prose
 *     renders as markdown rather than a monospace blob,
 *  3. drop machine sentinel lines and the now-empty fences that wrapped them,
 *  4. collapse the runs of blank lines those removals leave behind.
 */
export function cleanStepResultMarkdown(raw: string | null | undefined): string {
  if (!raw) return '';
  let lines = raw.replace(/\r\n/g, '\n').split('\n');
  lines = stripFrontmatter(lines);
  lines = unwrapReplyFence(lines);
  lines = extractAgentTextFromEventDump(lines);
  lines = stripSentinelLines(lines);
  lines = removeEmptyFences(lines);
  return collapseBlankRuns(lines).join('\n').trim();
}

/**
 * A codex CLI run in JSON mode can leave its raw JSONL event stream in the
 * report body (`{"type":"thread.started"…}` lines instead of prose). The only
 * part a reader cares about is the agent's message text, so when a line (or a
 * run of concatenated objects on one line) parses as such events, it is
 * replaced by the `agent_message` texts and the transport events are dropped.
 * A body with no recognisable agent text is left untouched — never destroy
 * evidence for the sake of formatting.
 */
function extractAgentTextFromEventDump(lines: string[]): string[] {
  const out: string[] = [];
  let foundText = false;
  for (const line of lines) {
    const events = parseConcatenatedJsonObjects(line.trim());
    if (!events) {
      out.push(line);
      continue;
    }
    for (const ev of events) {
      const item = (ev as { item?: { type?: string; text?: string } }).item;
      if (item?.type === 'agent_message' && typeof item.text === 'string') {
        foundText = true;
        out.push(...item.text.split('\n'), '');
      }
    }
  }
  return foundText ? out : lines;
}

/**
 * Parse a line consisting of one or more concatenated JSON objects (separated
 * by nothing or whitespace). Returns null when the line is anything else, so
 * ordinary prose that happens to contain braces is never misread.
 */
function parseConcatenatedJsonObjects(text: string): unknown[] | null {
  if (!text.startsWith('{') || !text.includes('"type"')) return null;
  const objects: unknown[] = [];
  let depth = 0;
  let start = -1;
  let inString = false;
  let escaped = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      if (escaped) escaped = false;
      else if (c === '\\') escaped = true;
      else if (c === '"') inString = false;
      continue;
    }
    if (c === '"') { inString = true; continue; }
    if (c === '{') {
      if (depth === 0) start = i;
      depth++;
    } else if (c === '}') {
      depth--;
      if (depth === 0 && start >= 0) {
        try { objects.push(JSON.parse(text.slice(start, i + 1))); } catch { return null; }
        start = -1;
      }
    } else if (depth === 0 && c.trim() !== '') {
      return null;
    }
  }
  return depth === 0 && objects.length > 0 ? objects : null;
}

/** Drop a leading `---` … `---` YAML frontmatter block, if present. */
function stripFrontmatter(lines: string[]): string[] {
  if (lines[0]?.trim() !== '---') return lines;
  for (let i = 1; i < lines.length; i++) {
    if (lines[i].trim() === '---') return lines.slice(i + 1);
  }
  return lines;
}

/**
 * Remove the pair of bare ``` fence lines that wrap the first model/reviewer
 * reply section so the reply renders as prose. Only a language-less fence is
 * unwrapped (a fenced code sample with a language tag is left intact).
 */
function unwrapReplyFence(lines: string[]): string[] {
  const heading = lines.findIndex((l) => /^#+\s*(model|reviewer) reply\b/i.test(l.trim()));
  if (heading === -1) return lines;

  let open = -1;
  for (let i = heading + 1; i < lines.length; i++) {
    const t = lines[i].trim();
    if (t === '') continue;
    if (t === '```') open = i;
    break;
  }
  if (open === -1) return lines;

  let close = -1;
  for (let i = open + 1; i < lines.length; i++) {
    if (lines[i].trim() === '```') {
      close = i;
      break;
    }
  }
  if (close === -1) return lines;

  const out = lines.slice();
  out.splice(close, 1);
  out.splice(open, 1);
  return out;
}

/** Remove whole lines that are only a `[[…]]` sentinel token. */
function stripSentinelLines(lines: string[]): string[] {
  return lines.filter((l) => !SENTINEL_LINE.test(l.trim()));
}

/**
 * Drop pairs of bare ``` fences whose body is now only blank lines — the
 * residue left after a sentinel that lived inside its own fence is removed.
 */
function removeEmptyFences(lines: string[]): string[] {
  const out: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].trim() === '```') {
      let close = -1;
      let onlyBlank = true;
      for (let j = i + 1; j < lines.length; j++) {
        if (lines[j].trim() === '```') {
          close = j;
          break;
        }
        if (lines[j].trim() !== '') onlyBlank = false;
      }
      if (close !== -1 && onlyBlank) {
        i = close;
        continue;
      }
    }
    out.push(lines[i]);
  }
  return out;
}

/** Collapse 2+ consecutive blank lines down to a single blank line. */
function collapseBlankRuns(lines: string[]): string[] {
  const out: string[] = [];
  let blank = false;
  for (const line of lines) {
    const isBlank = line.trim() === '';
    if (isBlank && blank) continue;
    blank = isBlank;
    out.push(line);
  }
  return out;
}
