The page content is in place. Now wire the behavior in `script.js`.

Edit only `script.js`. The script should:

- On `DOMContentLoaded`, look up `#increment` (the button) and `#count` (the span).
- Hold a counter starting at `0`.
- On each click of the button, increment the counter and write the new value into the `#count` span as text.

Constraints:

- Do not modify `index.html`. The page already has the right elements; if it does not, emit `[[TASK_BLOCKED:page content missing]]` and stop.
- Plain DOM JavaScript only. No frameworks, no build step.
- Keep the file short; an inline `addEventListener` is fine.

Light style touches are allowed in `style.css` (for example a centered layout) but not required for this task. Focus on the behavior.

When the file is in place, the page must be fully interactive when opened in a browser locally. Sanity-check by reading the three files before emitting `[[TASK_DONE]]`.
