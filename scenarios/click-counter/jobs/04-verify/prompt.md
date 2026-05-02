Verify that the click-counter scenario is complete and document how to run it.

Steps:

1. Read `index.html`, `style.css`, `script.js`. Confirm:
   - `index.html` has `<h1>Click Counter</h1>`, the intro paragraph, the `<button id="increment">+1</button>`, and the `<p>Current count: <span id="count">0</span></p>` inside `<main>`.
   - `script.js` increments a counter on click and writes it into `#count`.
   - All three files exist and are not empty.
2. If any of the above is missing or wrong, emit `[[TASK_BLOCKED:<one-line description of what is missing>]]` and stop. Do not try to fix it; that's a previous task's job.
3. If everything checks out, append a `## How to run` section to `README.md` with this exact line:
   `Open \`index.html\` in any modern browser. Click the **+1** button; the counter updates in place.`
4. Emit `[[TASK_DONE]]`.

This task is a check, not an implementation. Keep it minimal.
