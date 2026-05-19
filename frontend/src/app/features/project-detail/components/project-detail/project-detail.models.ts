/**
 * Curated list of orchestrator models a project can be configured with.
 * Empty `id` is the "use default" option (resolves to claude-opus-4-7
 * in the backend). The list is small on purpose: the orchestrator is
 * supposed to make decisions on the user's behalf, so the model needs
 * to be capable; the cheap models are deliberately excluded as
 * orchestrator-models even though they can run as task agents.
 */
export const OrchestratorRunner_KnownModels: readonly { id: string; label: string }[] = [
  { id: '',                  label: 'Default (Opus 4.7)' },
  { id: 'claude-opus-4-7',   label: 'Claude Opus 4.7' },
  { id: 'claude-sonnet-4-6', label: 'Claude Sonnet 4.6 (cheaper)' }
];
