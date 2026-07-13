# Project proposals

This directory stores dated proposal generations produced from measured product findings or an explicit operator topic. Each proposal is a Markdown document with structured frontmatter and one durable status: `proposed`, `approved`, `rejected`, or `spawned`.

Every proposal carries visible provenance: `topic`, `categories`, and `source`. Survey generations classify common topics such as Responsiveness from the measured finding. Operator-generated proposals retain the entered topic verbatim and identify the source as an operator request.

Generate a new batch without changing older decisions:

```bash
node scripts/generate-project-proposals.mjs --input=<survey.html> --output=docs/proposals --generation=YYYY-MM-DD --limits=critical:45,medium:21
```

The Project Hub reads these documents directly. Approving a proposal creates an implementation card from the document content and records its task key in `spawnedTask`.

The Project Hub is also the management surface:

- filter all historical generations and decisions;
- generate one repository-grounded draft from a named topic through the configured proposal-management CLI;
- reject with spoken or typed feedback, then translate and refine it through the CLI before recording both the refined and original forms;
- remove one unwanted proposal; or
- permanently delete generations older than the newest retained generation, including evidence no remaining proposal references.

Generated drafts use `ProposalManagement:Cli` (default `claude`) and `ProposalManagement:Model` (defaulting to the prompt-enhancement Haiku model). The CLI is instructed to inspect the project read-only and return structured JSON. It must not invent measured evidence.
