---
concept: supervisor
title: Supervisor
learnMore: docs/architecture/decisions/adr-archive.md
learnMoreLabel: Architecture Decisions (ADR-0017)
---

The supervisor is the per-project safety layer that watches the orchestrator and the running CLI agent in real time.

By default it is advice-first: it writes typed observations and advisories (info, warn, high) into the project log so a human or a meta-cycle can react. Four emergency primitives exist for the rare case when waiting is wrong: cancel run, pause pickup, force fail, and resume. Auto-intervention is a separate opt-in policy; without it the supervisor never moves work on its own.

Think of it as a kill-switch and a soft second opinion, not a parallel orchestrator.
