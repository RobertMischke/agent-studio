## Dossier implementation update

This delivery card is linked to the following living Dossier document(s):

{{dossier_targets}}

Before reporting completion, update every listed `index.html` in the same delivery change. The pipeline step `post-dossier-maintenance` verifies this requirement.

Use the implementation log bounded by `<!-- agent-studio:implementation-log:start -->` and `<!-- agent-studio:implementation-log:end -->`. Treat this log as append-only and add exactly one entry for `{{task_key}}` immediately before the end marker. Preserve all existing bytes outside the log, especially alternatives, recommendations, evidence, and decision records. Never edit, reorder, or remove an earlier implementation entry.

Use this entry schema, with HTML-escaped values:

```html
<li data-implementation-entry="" data-task-key="{{task_key}}" data-delivered-at="YYYY-MM-DD" data-slice="Compact slice name">
  <strong>{{task_key}} · Compact slice name</strong>
  <span>Delivered: one compact factual summary of the shipped behavior and evidence.</span>
</li>
```

If the Dossier predates the canonical section, insert this block once immediately before `</main>`, without changing the existing document, then append the entry inside its log:

```html
<!-- agent-studio:implementation-section:start -->
<section id="implementation" data-document-section="implementation">
  <h2>Implementation</h2>
  <p>Delivery slices append their current implementation status here.</p>
  <ol class="implementation-log">
<!-- agent-studio:implementation-log:start -->
<!-- agent-studio:implementation-log:end -->
  </ol>
</section>
<!-- agent-studio:implementation-section:end -->
```

The entry must name the slice, what was delivered, the calendar date, and the card key. An existing valid entry for this card is idempotent; do not duplicate it on a reissue.
