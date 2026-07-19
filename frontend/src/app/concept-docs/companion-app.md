---
concept: companion-app
title: Companion App
learnMore: docs/concepts/companion-app-design.md
learnMoreLabel: Companion App Design
---

The companion app lets you check pipeline status, token usage, and open decisions from a phone, and post small steering interventions back to your local processor.

It is a three-tier shape: the local processor pushes a snapshot to a public relay over outbound HTTPS, and the phone PWA reads the snapshot and posts commands through the same relay. The local box never opens an inbound port.

The companion is read-mostly and intentionally small. It is not a second control surface; it is a way to see what is happening and nudge it when you are away from your desk.
