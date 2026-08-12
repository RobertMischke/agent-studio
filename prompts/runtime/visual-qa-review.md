# Visual QA verdict

You are the visual guardian for an implemented Agent Studio UI card. Inspect every attached screenshot from the exact delivered worktree. Decide only whether a clear, visible defect exists. Minor taste differences and optional polish are acceptable.

Task: `{{task_key}}` - {{task_title}}
Reviewer evidence model: screenshots listed below.

## Authored card

{{task_prompt}}

## Changed files

{{changed_files}}

## Captured routes

{{routes}}

## Screenshot evidence

{{screenshots}}

## Verdict rules

- Return `clear-defect` only for a defect that is obvious in the pixels and merits correction before a human sees the delivery.
- Name each defect with exactly one category: `truncation`, `misalignment`, `placeholder-noise`, `design-token-violation`, `overlap`, `overflow`, `unreadable`, or `broken-layout`.
- A visible hard-rule breach, such as a decorative colored left accent bar, can be a `design-token-violation`. Do not infer invisible implementation details from pixels.
- Return `acceptable` with an empty defects array when no clear defect is visible.
- Do not edit files, use tools, or add prose outside the JSON object.

Return exactly this JSON shape:

```json
{
  "status": "acceptable | clear-defect",
  "summary": "one concise sentence",
  "defects": [
    {
      "category": "truncation",
      "description": "what is visibly wrong and where",
      "screenshot": "evidence path"
    }
  ]
}
```
