---
concept: probes
title: Probes
learnMore: docs/cli/skills/cli-overview.md
learnMoreLabel: CLI Skills Overview
---

A probe is a small, read-only check the backend runs in the background to keep its picture of the world current.

Quota probes drive each CLI's `/usage` or `/status` slash command in a scratch directory and parse the response into the rate-limit pill you see in the header. Session probes discover existing CLI sessions on disk so you can resume one. Drift and audit probes inspect files for staleness.

Probes are not intervention: they observe, write a structured result, and surface it. If a probe fails, the panel shows stale or unknown rather than guessed data.
