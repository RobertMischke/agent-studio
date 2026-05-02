/**
 * Pure heuristic classifier for the agent's last assistant turn.
 *
 * Why heuristic instead of an LLM call:
 * - The signal we want is structural ("agent ended with a question vs.
 *   reported done"), not semantic. Cheap regex over the last paragraph
 *   answers it ~well enough for the quick-reply UI we're driving.
 * - Calling Haiku per turn would add a per-turn cost and a 1-3 s delay
 *   for output the user is staring at while it lands. We can layer that
 *   in later if heuristic precision proves insufficient.
 *
 * The classifier returns an {@link OutcomeAssessment} that the chat-input
 * banner reads to render: a short summary of where the agent landed, plus
 * up to four quick-reply chips ("Yes", "No", "Continue", a custom one
 * extracted from the question itself). The chips pre-fill the chat input;
 * the user can edit before sending.
 */

export type OutcomeKind =
  | 'done'
  | 'blocked'
  | 'question'
  | 'needs_input'
  | 'progress'
  | 'unknown';

export interface OutcomeAssessment {
  kind: OutcomeKind;
  /** One-sentence headline of where the agent landed. */
  summary: string;
  /** The last question the agent asked, if any (raw text, no Markdown). */
  question: string | null;
  /** Quick-reply suggestions for the chat input, ordered by relevance. */
  suggestions: QuickReply[];
}

export interface QuickReply {
  label: string;
  /** Text to drop into the chat input when the chip is clicked. */
  prompt: string;
  /**
   * If true, the UI may auto-send when the chip is clicked. We default to
   * false so the user always confirms before a follow-up goes out.
   */
  autoSend?: boolean;
}

const DONE_PATTERNS = [
  /\b(committ?ed|merged|landed|shipped|deployed|fixed|resolved|implemented|complete[d]?|finished|done|ready for review)\b/i,
  /\bcommit:?\s*[a-f0-9]{7,}/i,
  /\bPR\s+(opened|created|ready)\b/i
];

const BLOCKED_PATTERNS = [
  /\bcannot\s+(?:proceed|continue|find|access|determine)\b/i,
  /\bblocked\s+by\b/i,
  /\bI\s+don'?t\s+have\s+(?:access|permission)\b/i,
  /\bunable\s+to\b/i,
  /\bI\s+(?:do\s+not|don'?t)\s+see\b/i,
  /\bno\s+(?:files|matches|results)\s+(?:found|match)\b/i
];

const NEEDS_INPUT_PATTERNS = [
  /\b(?:please|could\s+you|can\s+you)\s+(?:provide|share|paste|attach|specify|clarify)\b/i,
  /\bwhich\s+(?:one|file|option)\b.*\?/i,
  /\bdo\s+you\s+want\s+(?:me\s+to|to)\b.*\?/i,
  /\bshould\s+I\b.*\?/i,
  /\bwould\s+you\s+like\b.*\?/i,
  /\bI'?ll\s+wait\s+for\b/i,
  /\bwhat\s+would\s+you\s+like\b/i
];

const PROGRESS_PATTERNS = [
  /\b(?:starting|working|investigating|reading|searching|exploring|analy[sz]ing|building|running)\b/i
];

/**
 * Classifies the agent's final turn. `text` should be the joined text of
 * the last contiguous run of agent message groups (i.e. the body of the
 * `agent` turn from {@link buildConversationTurns}). Pass an empty string
 * if no such turn exists yet.
 */
export function classifyOutcome(text: string): OutcomeAssessment {
  const trimmed = (text ?? '').trim();
  if (!trimmed) {
    return {
      kind: 'unknown',
      summary: 'No agent reply yet.',
      question: null,
      suggestions: []
    };
  }

  // Look at narrowing windows of the tail for each kind. "Done" lives in the
  // very last sentence ("Committed."); a question lives at the end if it's
  // there at all; "blocked" is only meaningful as the terminal state, so we
  // confine its detection to the last few lines too. Walking the entire reply
  // matches too eagerly on transient phrases ("started by reading...") that
  // no longer reflect the final state, and the previous version misclassified
  // "I cannot find X. After searching I found it. Done." as blocked.
  const last2 = lastLines(trimmed, 2).join('\n');
  const last3 = lastLines(trimmed, 3).join('\n');
  const last6 = lastLines(trimmed, 6).join('\n');

  const question = lastQuestion(trimmed);

  // Question / needs-input wins when the agent's final sentence is a question
  // or an explicit "I'll wait for your input" phrase - that is the strongest
  // signal that the user needs to act.
  if (question && hasOpenQuestion(last3)) {
    return {
      kind: 'question',
      summary: `Agent is asking: ${truncate(question, 120)}`,
      question,
      suggestions: questionSuggestions(question)
    };
  }
  if (NEEDS_INPUT_PATTERNS.some((re) => re.test(last3))) {
    return {
      kind: question ? 'question' : 'needs_input',
      summary: question
        ? `Agent is asking: ${truncate(question, 120)}`
        : 'Agent is waiting for your input.',
      question,
      suggestions: questionSuggestions(question)
    };
  }

  // Done beats blocked when both appear: a commit at the end means the agent
  // worked through the obstacle, regardless of any earlier "cannot" phrasing.
  if (DONE_PATTERNS.some((re) => re.test(last2))) {
    return {
      kind: 'done',
      summary: 'Agent reports the task is done.',
      question: null,
      suggestions: doneSuggestions()
    };
  }

  if (BLOCKED_PATTERNS.some((re) => re.test(last3))) {
    return {
      kind: 'blocked',
      summary: question
        ? 'Agent reports it is blocked and is asking for direction.'
        : 'Agent reports it is blocked.',
      question,
      suggestions: blockedSuggestions(question)
    };
  }

  if (PROGRESS_PATTERNS.some((re) => re.test(last6))) {
    return {
      kind: 'progress',
      summary: 'Agent is mid-task.',
      question: null,
      suggestions: progressSuggestions()
    };
  }

  return {
    kind: 'unknown',
    summary: truncate(firstSentence(last6), 140),
    question,
    suggestions: progressSuggestions()
  };
}

function lastLines(text: string, n: number): string[] {
  const lines = text.split('\n').map((l) => l.trim()).filter(Boolean);
  return lines.slice(Math.max(0, lines.length - n));
}

function lastQuestion(text: string): string | null {
  // Sentence-ish split: split on '?' / '!' / '.' boundaries followed by space
  // or end. Then walk backwards to find the last sentence ending with '?'.
  const sentences = text.split(/(?<=[.?!])\s+/).map((s) => s.trim()).filter(Boolean);
  for (let i = sentences.length - 1; i >= 0; i--) {
    const s = sentences[i];
    if (s.endsWith('?')) return s;
  }
  return null;
}

function hasOpenQuestion(tail: string): boolean {
  return /\?\s*$/.test(tail.trim());
}

function blockedSuggestions(question: string | null): QuickReply[] {
  return [
    { label: 'Try anyway', prompt: 'Make a best-effort attempt and document the assumptions.' },
    { label: 'Skip and continue', prompt: 'Skip this part and continue with the rest of the task.' },
    ...(question ? [yesNoFromQuestion(question)].filter((x): x is QuickReply => x !== null) : [])
  ].slice(0, 4);
}

function questionSuggestions(question: string | null): QuickReply[] {
  const fromQuestion = question ? yesNoFromQuestion(question) : null;
  const base: QuickReply[] = [
    { label: 'Just continue', prompt: 'Just continue — pick whatever makes sense.' },
    { label: 'Yes', prompt: 'Yes.' },
    { label: 'No', prompt: 'No.' }
  ];
  return (fromQuestion ? [fromQuestion, ...base] : base).slice(0, 4);
}

function doneSuggestions(): QuickReply[] {
  return [
    { label: 'Looks good', prompt: 'Looks good, thanks.' },
    { label: 'Ask for changes', prompt: 'A few tweaks: ' },
    { label: 'Run the tests', prompt: 'Please run the tests and confirm they pass.' }
  ];
}

function progressSuggestions(): QuickReply[] {
  return [
    { label: 'Keep going', prompt: 'Keep going.' },
    { label: 'Stop and report', prompt: 'Stop where you are and report what you have so far.' }
  ];
}

/**
 * Heuristic: if the question asks "Should I X?" or "Do you want X?" we offer
 * a tailored confirmation prompt rather than a generic Yes. Returns null when
 * the question doesn't fit a common shape.
 */
function yesNoFromQuestion(question: string): QuickReply | null {
  const m1 = /should\s+I\s+([^?]+)\?/i.exec(question);
  if (m1) return { label: 'Yes, do it', prompt: `Yes, please ${m1[1].trim()}.` };
  const m2 = /do\s+you\s+want\s+(?:me\s+to\s+)?([^?]+)\?/i.exec(question);
  if (m2) return { label: 'Yes, do it', prompt: `Yes, please ${m2[1].trim()}.` };
  const m3 = /would\s+you\s+like\s+(?:me\s+to\s+)?([^?]+)\?/i.exec(question);
  if (m3) return { label: 'Yes, do it', prompt: `Yes, please ${m3[1].trim()}.` };
  return null;
}

function firstSentence(text: string): string {
  const m = /^[^.!?]+[.!?]/.exec(text);
  return m ? m[0].trim() : text.trim();
}

function truncate(s: string, max: number): string {
  if (s.length <= max) return s;
  return s.slice(0, max - 1).trimEnd() + '…';
}
