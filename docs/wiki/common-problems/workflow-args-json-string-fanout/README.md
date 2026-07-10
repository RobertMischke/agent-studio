---
id: workflow-args-json-string-fanout
title: "Workflow arguments arrive as a JSON string and create unbounded fan-out"
status: open
first-seen: 2026-07-07T00:00:00Z
last-seen: 2026-07-07T00:00:00Z
severity: major
category: cli
tags: [workflow, args, fan-out, token-budget]
affects:
  - "workflow scripts"
  - "parallel agent execution"
related-tasks: [ASS-1761]
related-adrs: []
---

# Workflow arguments arrive as a JSON string and create unbounded fan-out

**What.** A workflow value that looks like an array at the caller may reach a
JavaScript workflow as serialized JSON. String length, indexing, and slicing
are valid operations, so code can silently turn the serialized value into
thousands of tiny work items instead of failing early.

**Why.** The workflow boundary does not guarantee that structured arguments
retain their in-memory representation. Scripts that assume an array and chunk
before validating type and scale can exhaust agent and token budgets.

**Workaround.** Parse string input as JSON, validate its shape, apply explicit
item and fan-out caps, and log the planned count before starting parallel work.

```js
let items = args;
if (typeof items === 'string') {
  try {
    items = JSON.parse(items);
  } catch {
    throw new Error('Workflow args must be a JSON array');
  }
}

if (!Array.isArray(items)) {
  throw new Error(`Workflow args must be an array, received ${typeof items}`);
}
if (items.length > MAX_ITEMS) {
  throw new Error(`Workflow item count ${items.length} exceeds ${MAX_ITEMS}`);
}

const plannedFanOut = Math.ceil(items.length / CHUNK_SIZE);
if (plannedFanOut > MAX_FAN_OUT) {
  throw new Error(`Workflow fan-out ${plannedFanOut} exceeds ${MAX_FAN_OUT}`);
}
log({ itemCount: items.length, chunkSize: CHUNK_SIZE, plannedFanOut });
```

The cap values are operation-specific. The invariant is that an unexpected
representation or scale fails before parallel work begins.

**Long-term.** Workflow boundaries should expose validated argument schemas and
bounded fan-out as explicit contracts rather than relying on script convention.
