# Root-cause protocol

1. Record the runtime type and a size-safe summary of the raw argument.
2. Verify that string input is parsed before any indexing, slicing, or chunking.
3. Validate the parsed container and required element types.
4. Calculate and log item count, chunk size, and planned fan-out before launch.
5. Reject counts outside operation-specific hard limits.
6. If work already started, inspect `journal.jsonl` before rerunning because
   completed chunks may contain usable evidence.
