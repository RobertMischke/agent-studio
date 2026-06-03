# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| applied | 2026-06-03 | Check `./api.sh status` before blaming the CLI; dev should be offline outside Playwright. | Operator / agent | Keeps diagnosis grounded in current runtime state. |
| tried | 2026-05-27 | Retry after stopping dev backend and watchers. | Operator / agent | Works for suspected file-lock cases, but root cause still unconfirmed. |
| TODO | TODO | Capture handle-owner data at the moment of failure. | Operator | Needed before declaring a permanent fix. |
