# Workbench inline decision markup

Workbench authors place decision points directly beside the analysis that
supports them. The source remains self-contained, static HTML. Agent Studio
adds inputs at render time and persists the confirmed answers in the existing
`workbench.json` decision receipt.

## Minimal example

```html
<section data-decision-id="routing-owner" data-decision-kind="single">
  <h2>Where should the routing policy run?</h2>
  <p>Choose one boundary after reading the analysis above.</p>
  <ul>
    <li data-option-id="task-api">
      <strong>Task API recommendation</strong>
      Return an editable recommendation during task creation and reissue.
    </li>
    <li data-option-id="studio-only">
      <strong>Studio default only</strong>
      Keep routing outside the backend.
    </li>
  </ul>
  <p data-comment data-comment-label="Optional implementation note">
    Add a constraint or exception if needed.
  </p>
</section>
```

Without Agent Studio this is an ordinary heading, paragraph, and list. Do not
put required scripts, form controls, or Studio API calls in the document. The
Workbench viewer adds a radio or checkbox before each option and a textarea to
the optional comment element.

## Attributes

| Element | Attribute | Contract |
|---|---|---|
| Decision container | `data-decision-id` | Required stable id, unique within the document. Use 1 to 120 ASCII letters, digits, `.`, `_`, or `-`, starting with a letter or digit. |
| Decision container | `data-decision-kind` | Required. `single` renders radios, `multi` renders checkboxes, and `confirm` renders one checkbox. |
| Decision container | `data-decision-label` | Optional plain label override. Without it, the first heading inside the container supplies the audit label. |
| Option child | `data-option-id` | Required stable id, unique inside the decision point. It uses the same id grammar. |
| Option child | `data-option-label` | Optional plain label override. Without it, the first `strong` or `b` text supplies the label, then the full option text as a fallback. |
| Comment child | `data-comment` | Optional marker. At most one comment field should occur in a decision point. Its existing text remains the static fallback label. |
| Comment child | `data-comment-label` | Optional accessible label override for the enhanced textarea. |

`single` and `confirm` answers select exactly one option. `confirm` declares
exactly one option. A `multi` point may declare several options and records all
selected values. Every declared point needs a selection before the host enables
`Prepare feature card`.

## Persistence and card preparation

The iframe sends only decision ids, option ids, and comment text through its
source-checked message boundary. The host compares every id with an inert parse
of the loaded HTML and supplies the canonical labels from that parse. Unknown,
duplicate, oversized, or kind-mismatched values are rejected.

Confirmation uses the existing prepare and confirm endpoints. Prepare validates
the exact Workbench revision or fingerprint and writes nothing. Confirm performs
the single atomic `workbench.json` replacement and records:

```json
{
  "decision": {
    "outcome": "feature-spawn",
    "state": "succeeded",
    "confirmedBy": "Robert",
    "decidedAt": "2026-08-08T18:30:00Z",
    "answers": [
      {
        "decisionId": "routing-owner",
        "kind": "single",
        "selectedOptions": [
          { "id": "task-api", "label": "Task API recommendation" }
        ],
        "comment": "Keep the recommendation editable."
      }
    ],
    "taskDraft": {
      "title": "Implement Runtime routing policy",
      "goal": "The Workbench summary and confirmed decisions are combined here.",
      "chosenOption": "Where should the routing policy run?: Task API recommendation"
    }
  }
}
```

The compact host confirmation pre-fills the card title from the Workbench title,
the goal from its summary plus all answers and comments, and `chosenOption` from
the labelled selections. The operator may adjust title and goal before confirm.
The decision service returns this validated proposal but does not bypass the
normal task creation API.

On reload, the receipt supplies the selected controls, comments, timestamp, and
decision owner. Settled controls are read-only. Dirty or provenance-free
Workbenches remain readable but cannot persist a decision.
