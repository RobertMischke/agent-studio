---
concept: skills
title: Skills
learnMore: docs/skills-architecture.md
learnMoreLabel: Portable Skills Architecture
---

Skills are portable specialist workflows. Each skill is a short Markdown guide that explains how to do one specific kind of work well: a security review, a Playwright check, a CLI driver tweak, a release prep.

Skills are optional and situational. They never own task lifecycle, state movement, or queue policy. The orchestrator owns those rules; a skill only describes the craft on top.

A central library lives with the task processor and is shared across watched projects. The orchestrator can attach selected skills to a managed run; direct CLI sessions discover the same skills through a project lookup section.
