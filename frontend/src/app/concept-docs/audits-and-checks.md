---
concept: audits-and-checks
title: Audits and Checks
learnMore: docs/operations/security/overview.md
learnMoreLabel: Security Overview
---

Audits and checks are scoped reviews that run against a project's evidence: source code, configuration, baselines, and prior reports. A security audit is one example; architecture drift, test quality, and token spend are others.

Each audit produces a typed report with a verdict (ok, stale, fail), open findings, and a link to the evidence file. The panel here lists the latest verdict, the active findings split by severity, and the baseline status.

An audit never silently mutates state. It reads files, writes a report, and may queue a follow-up task that you decide to accept.
