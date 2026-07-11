# Project proposals

This directory stores append-only, dated proposal generations produced from measured product findings. Each proposal is a Markdown document with structured frontmatter and one durable status: `proposed`, `approved`, `rejected`, or `spawned`.

Generate a new batch without changing older decisions:

```bash
node scripts/generate-project-proposals.mjs --input=<survey.html> --output=docs/proposals --generation=YYYY-MM-DD --limits=critical:45,medium:21
```

The Project Hub reads these documents directly. Approving a proposal creates an implementation card from the document content and records its task key in `spawnedTask`.
