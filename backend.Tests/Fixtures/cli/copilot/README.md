# Copilot CLI fixtures

Captured plain-text output from `copilot -p ... --allow-all`. See [`docs/cli-skills/cli-copilot.md`](../../../../docs/cli-skills/cli-copilot.md).

Copilot has no JSON output mode for headless runs, so fixtures here are plain-text snippets. Suggested first fixtures:

- `footer-tokens-changes-premium.txt` — the standard summary line that `TryParseUsage` scrapes (`Tokens ↑ ... Changes +N -M ... 1 Premium`). Lock the regex against this.
- `footer-no-changes.txt` — same shape with `Changes 0 -0`, edge case for the changes regex.
- `model-picker.txt` — a captured `/model` panel scrape, for `CopilotModelDiscovery` regression tests.
