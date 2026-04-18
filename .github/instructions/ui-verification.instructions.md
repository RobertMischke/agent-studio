---
description: "Use when making frontend UI changes — layout, styling, components, templates. Ensures visual correctness via Playwright after every change."
applyTo: "frontend/src/**"
---
# UI Verification with Playwright

After every UI change (layout, spacing, styling, component templates), verify visually using Playwright:

1. Open the running frontend in a browser page (`open_browser_page` on `http://localhost:4010`)
2. Take a screenshot (`screenshot_page`) to confirm the change renders correctly
3. Check for obvious issues: overlapping elements, missing spacing, broken layouts, invisible text
4. If the detail panel or dialog is involved, open it and screenshot that state too

Do NOT skip this step — spacing bugs, missing gaps, and layout regressions are easy to miss without visual confirmation.
