# Bounded Workflow Fan-out

Status: maintained operational guidance for scripts that turn tool or workflow
arguments into parallel work.

Fan-out is a resource boundary. A value that looks like an array at the caller
may reach a script as serialized JSON. JavaScript also permits string slicing
and indexing, so treating that string as an array can create thousands of tiny
work items without throwing an early error.

## Boundary contract

Before calculating chunks or starting parallel work, a workflow script must:

1. Parse a string argument as JSON and fail clearly if parsing fails.
2. Validate the parsed shape, including element types when required.
3. Apply explicit input-size and fan-out caps appropriate to the operation.
4. Log the validated item count, chunk size, and planned fan-out before work.

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

The constants are local policy, not universal magic numbers. Pick them from the
operation's expected scale and resource budget. The invariant is that an
unexpected representation or scale fails before parallel work begins.

## Failure investigation

When a fan-out run fails, inspect its structured journal before rerunning it.
Completed chunks may still provide usable evidence, and the pre-flight counts
make representation errors distinguishable from legitimate large inputs.

## Related

- [Pipeline domain map](../../domains/pipeline.md)
- [Runtime observability](../../operations/runtime/observability.md)

## Living knowledge log

- 2026-07-11: Migrated the serialized-argument and hard-cap invariant from
  private agent memory into the project wiki.
