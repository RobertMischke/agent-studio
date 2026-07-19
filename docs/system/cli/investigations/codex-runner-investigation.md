# Codex runner: `[[TASK_NOOP]]` on every fresh job (2026-05-12)

Forensic note for the bug where 100% of freshly picked-up Codex jobs replied
with `[[TASK_NOOP]]` and 24-52 output tokens, leaving the working tree
unchanged. Fix landed on 2026-05-12; this document captures the root cause and
the two-step path the fix took, so the next prompt-delivery change does not
re-acquire the regression.

## Symptom

Every freshly picked-up Codex job answered with one short line containing
`[[TASK_NOOP]]` and no file edits, on two unrelated boards inside one
afternoon:

- `lotta-dashboard` Sternstunde batch (`sternstunde-01..05`): all NOOP, no
  files touched.
- `agent-taskboard` (`preserve-pager-context-...`, `visual-tooltip-...`,
  `remove-useless-lane-collapse-...`): all NOOP.

Verbatim agent texts:

- `Understood. [[TASK_NOOP]]`
- `Ready for the task. [[TASK_NOOP]]`
- `**No task provided.** [[TASK_NOOP]]`
- `No actionable task was provided in this turn. [[TASK_NOOP]]`
- `I'll include the required terminal sentinel in future replies. [[TASK_NOOP]]`

Two observations made the diagnosis tractable:

1. Input tokens were 38k+: the `prompt.md` content did reach the model. This
   ruled out an empty-prompt or stream-truncation bug.
2. `[[TASK_NOOP]]` does not appear in `prompt.md` itself. It is only present
   in (a) the runtime prompt template's terminal-sentinel reminder and
   (b) the orchestrator's `CodexCliService.BuildSystemPromptPrefix`. For the
   model to volunteer it without instruction, the model had to interpret the
   surrounding context as "rules learned, no user task this turn".

The comparison run that proved this was reproducible-rather-than-flaky: the
successful Lotta redesign on 2026-05-11 (22 files changed) ran the same code
path one day earlier and produced real agent output. The Codex CLI updated
between those two days; the orchestrator code did not.

## Root cause

`codex` 0.130 changed positional-`PROMPT` semantics: a rules-heavy positional
prompt was treated as "initial instructions" / system-side framing rather
than as the user message for the turn. The orchestrator was passing the
fully rendered prompt (system-prefix + task header + `prompt.md` body +
"Rules for this run" + terminal-sentinel reminder) as the last argv slot to
`codex exec`. Under the new CLI behaviour the model received that block as
its onboarding header and concluded there was no user task this turn, so it
emitted the `[[TASK_NOOP]]` sentinel (whose grammar was right there in the
header it had just been handed).

A secondary observation that made this 100% reproducible: Codex 0.130 always
logs `Reading additional input from stdin...` on startup and, if a stdin
handle is connected (including an inherited interactive-console handle from
the parent's shell), appends whatever it can read as a `<stdin>` block onto
the positional prompt. When the backend was launched from an interactive
shell the inherited stdin was a live console handle; Codex read partial or
empty data and reinforced the "no actionable task" reading.

## Fix

`codex exec --help` documents a supported alternative delivery path: pass
`-` as the positional prompt to read instructions from stdin. Switching to
`-` puts Codex into a different code path entirely: it blocks on stdin
until our bytes arrive, treats the resulting payload as the user turn,
and emits real `turn.started` / `agent_message` / `turn.completed` frames.

Live path:

- `src/AgentTaskboard.Runner/Cli/CodexCliService.cs` `BuildStartInfo` appends
  `-` to argv when there is a prompt.
- `GetPromptStdinPayload` returns `BuildSystemPromptPrefix(IsWindows) +
  prompt`.
- `CliExecutionServiceBase` detects the non-empty payload, sets
  `RedirectStandardInput = true`, writes the bytes to the child, and closes
  the pipe.

## What did not work, and why

The first stab was to keep the positional-prompt path and only force-close
inherited stdin (`ForceCloseStdinWhenNoPayload`, commit `a5bdd283`). Codex
0.130 treats a closed-pipe stdin differently from `< NUL`: it logs
`Reading additional input from stdin...`, completes the read with immediate
EOF, and exits clean with no agent output at all. The pattern flipped from
"NOOP with 24-52 tokens" to "silent exit with zero tokens", which was worse
because the watchdog had nothing to time out on.

Commit `3aed3786` reverted the `ForceCloseStdinWhenNoPayload` knob and moved
the prompt over to the `-` / stdin path. Verification on the dev machine:
the same prompt that NOOPed under positional argv produced `[[TASK_DONE]]`
plus real file changes once it was delivered as the stdin payload.

## Regression coverage

Unit tests in `backend.Tests/CodexCliServiceTests.cs` lock the fixed shape
of the invocation:

- `BuildStartInfo_LongPromptKeepsPromptOutOfArgvAndUsesStdin` - asserts that
  a 12 000-char prompt is **not** in `ArgumentList`, that `-` is, and that
  `BuildPromptStdinPayloadForTest` returns a payload containing the prompt
  plus the sentinel reminder (`[[TASK_DONE]]`).
- `BuildStartInfo_ArgvSizeDoesNotGrowWithReissuePromptLength` - asserts that
  argv is identical for a short reissue and a 20 KB reissue (proves the
  prompt is no longer in argv, which would otherwise blow Windows' 32 KB
  command-line cap on long reissues).
- `BuildSystemPromptPrefix_*` - assert the prefix contains the sentinel
  grammar including `[[TASK_NOOP]]` and stays short.

The live spawn integration probe
`CliSpawnIntegrationTests.CodexCliService_StartAsync_ProducesStreamingFrames`
drives the real `codex.cmd` against the dev checkout and asserts at least
one `thread` / `turn` / `item` / `session_meta` frame arrives. This is the
end-to-end check that would re-fail if the prompt-delivery path ever
silently regressed to a NOOP-pattern again.

## Related artifacts

- Bug task: `bug-codex-prompt-delivered-as-system-not-user-noop-loop`.
- Lotta NOOP run logs (no longer needed for triage but kept for posterity):
  `C:\Projects\lotta-dashboard\.orchestrator\jobs\2-ready\sternstunde-*\logs\cli-output.log`.
- Out-of-scope follow-ups carved off this bug:
  `bug-codex-noop-recovery-no-progress-detection-loops`,
  `bug-codex-cmdline-too-long-on-windows-after-reissue`.
