Capture visual evidence of the click-counter page rendering correctly. Save the result under the **job folder's** `results/` directory (relative to your task folder, not the workspace).

Two acceptable deliverables, in priority order:

1. **Preferred - a real screenshot.** If you have access to a headless browser (Playwright, Puppeteer, headless Chrome / Edge / Firefox), render `index.html` and save the screenshot as `results/click-counter.png`. A 600x400 viewport is fine. If it works, you are done.

2. **Fallback - a written visual snapshot.** If no headless browser is available in this environment, create `results/visual-check.md` instead. The markdown file should describe what the page looks like with one section per visible element (heading, intro paragraph, button, count display) and a paragraph naming the styling currently in `style.css`. Be concise; the goal is a written record a reviewer can compare against the live page when they open it themselves.

Do **not** block on missing tools. Try the screenshot path; if it errors, immediately switch to the fallback. Either deliverable counts as success.

After saving the file, sanity-check by reading it back. If the file is empty or the screenshot is corrupted (zero bytes, etc.), retry once or fall back.

When you finish, end with `[[TASK_DONE]]` (or `[[TASK_BLOCKED:<reason>]]` if both paths failed).
