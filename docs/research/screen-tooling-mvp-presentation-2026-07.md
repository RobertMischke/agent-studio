# Screen tooling for the MVP presentation - evaluation (2026-07)

**Status.** Evaluation and recommendation. Decision grounding for how Agent
Studio produces product captures (screenshots and recordings) for the MVP
presentation. No code change ships with this document; it defines the
recommendation and a follow-up task cut.

**Task.** AGT-1926 - "Screen-Tooling fuer MVP-Praesentation evaluieren
(MVP-Prio 4)": check the Playwright pipeline against a dedicated capture tool,
give a recommendation, and cut the follow-up tasks.

## Executive decision

| Requirement | Extend Playwright | Dedicated capture tool |
|---|---|---|
| Repeatable, current screenshots | **Best fit.** Deterministic, both-theme, CI-capable, and already mostly implemented. | Weak fit. Manual recapture can drift from the product. |
| Polished GIF or narrated video | Weak fit. Net-new engineering with no native narration, zoom, or click emphasis. | **Best fit.** ScreenToGif covers short slide loops; OBS covers narrated walkthroughs. |
| Remote-runner compatibility | **Best fit.** Headless stills align with remote-ready Phase 4. | Operator-desktop activity; keep narrated motion local. |
| Data handling | Local demo seed, automated guardrails. | Prefer local OSS tools over SaaS and record only the demo seed. |

**Final recommendation:** use a hybrid split, not one tool for every output.
Extend the existing Playwright pipeline for all presentation stills. Use
ScreenToGif for short silent loops and OBS Studio for narrated MP4
walkthroughs, always against the ADR-0056 demo workspace. Do not build a
Playwright video pipeline for the MVP presentation. T1-T4 below are queued as
the single implementation task `mvp-presentation-capture-kit`; keep T5 optional
and take T6 as independent cleanup.

### Presentation decisions

| Decision | Selected default | Consequence for capture work |
|---|---|---|
| Medium | **Live slide deck**, with optional embedded silent loops and an optional narrated backup recording. No interactive click-through for the MVP. | T4 optimizes the story for a presenter-controlled sequence. Motion supplements the deck rather than replacing it. |
| Deck format | **16:9 at 1920x1080.** | T1 composes and reviews every asset in a 1920x1080 frame. Playwright uses hi-DPI capture so cropped UI remains crisp on the slide. |
| Webcam | **No webcam presence.** Narrated backup footage uses microphone audio and product capture only. | T3 does not need a camera scene or presenter overlay. This keeps attention on the product and simplifies repeatable framing. |

These are delivery defaults for the MVP, not unanswered dependencies. They can
be revisited after the first storyboard review without blocking the capture
kit.

**Context note.** The task points at `docs/concepts/remote-execution-product-integration.md §7`.
That file does not exist in the tree. The live plan of record for the remote
theme is [`remote-ready-kickoff-2026-07.md`](./remote-ready-kickoff-2026-07.md);
its **Phase 4** ("Playwright + previews remote") and **§7 Risks** are the
closest real anchor and are what this evaluation is written against. The
ROADMAP's "Visual Regression Evidence" and "Creativity And Design" sections
([ROADMAP.md](../../ROADMAP.md)) are the adjacent product direction.

---

## 1. The question is really two questions

"Product captures for the presentation" splits into two capture kinds with
different tooling economics. Conflating them is the main risk in this decision.

| Kind | What it is | What makes it good | Cadence |
|---|---|---|---|
| **Stills** | Hero screenshots of surfaces, feature callouts, both-theme pairs, slide imagery | Clean chrome, correct state, both themes, stays in sync with the shipped UI | Re-shot on every relevant UI change |
| **Motion** | Short screencasts / GIFs of a flow (create a task, run, review), a narrated walkthrough | Cursor emphasis, zoom on the point of interest, smooth pacing, optional narration/captions | A small number of one-off reels per milestone |

Stills reward **determinism and repeatability**. Motion rewards **presentation
polish and narration**. Those are different tools' strengths.

---

## 2. What exists today (verified in this worktree)

Ground truth from a code survey of `frontend/playwright.config.ts`,
`frontend/e2e/visual-evidence/*`, and `scripts/visual-docs/*`.

### Stills: a working Playwright pipeline is already ~80% of the way there

- **Deterministic demo data.** ADR-0056's `scripts/seed-demo-workspace.mjs`
  builds a slim, byte-identical demo workspace (`Demo App`, `Demo Platform`)
  with run/token history and a production-root guard. Captures against it carry
  no private data and start from a reproducible stand.
- **A dual-theme surface tour.** `frontend/e2e/visual-evidence/demo-screenshot-tour.spec.ts`
  captures the core surfaces (kanban board, task detail + activity chat
  composer, orchestrator chat, project-hub rails) in **both themes**, labels
  files `--dark--real` / `--light--real`, and attaches each PNG so the
  job-artifact reporter harvests it into `<job>/results/playwright/<spec>/`.
- **A marketing screenshot pipeline.** `frontend/e2e/visual-evidence/readme-screenshots.spec.ts`
  writes ~15 surface PNGs to `docs/assets/images/`, driven by
  `scripts/visual-docs/generate.sh` and `npm run docs:visual`. A **marketing
  capture mode** (`PW_VISUAL_CAPTURE=marketing`, the default) injects CSS via
  `page.addStyleTag` to hide local-only dev chrome (the vertical `DEV` banner
  and the left-edge `body::before` stripe) before shooting.
- **Harvest + review path.** `frontend/e2e/helpers/job-artifact-reporter.ts`
  activates when `JOB_RESULTS_DIR` is set and copies `*.png` / `*.webm` /
  `*.zip` plus a per-run `index.json` into the job's `results/playwright/`.
  This is the same evidence path reviewers already read.

### Motion: nothing exists

- The only video setting is Playwright's failure diagnostic
  `video: 'retain-on-failure'` (`frontend/playwright.config.ts:52`). On a
  passing capture run, no video is kept.
- There is **no** `recordVideo`, `page.video()`, screencast, ffmpeg, or
  gif/mp4/webm authoring anywhere in the repo. (The `.webm` hits in the tree are
  `.webmanifest` PWA strings, not video.)
- Producing any demo video or animated GIF today would be net-new work.

### Two quality gaps in the current stills path

- **No hi-DPI capture.** No `deviceScaleFactor` override anywhere; shots are
  taken at scale factor 1 (`devices['Desktop Chrome']`). Slides and projectors
  want 2x for crisp text.
- **No full-page shots.** Every capture is `fullPage: false` (viewport only).
- **A stale validator path (bonus finding).** `scripts/visual-docs/validate.mjs`
  reads `docs/visual/manifest.json`, but the real manifest is
  `docs/reports/visual/manifest.json` (its own `sourceOfTruth` field says so).
  The screenshot half of `generate.sh` works; the validate half is misaligned
  and would fail. Worth a small fix task regardless of this decision.

---

## 3. Option A - extend the Playwright pipeline

**Fit: excellent for stills, poor for motion.**

Pros:
- Already built, versioned, and CI-able. Marginal cost to add a
  presentation-specific set is low.
- **Determinism is the whole point.** Re-running after any UI change re-shoots
  every image, so the deck never shows stale UI. This is the single biggest
  advantage and it is exactly what a manual tool cannot give.
- Both-theme discipline, dev-chrome hiding, and no-private-data (demo seed) are
  already solved.
- Headless and cross-platform, so it rides the remote-ready Phase 4 plan
  (capture on the Linux runner) without change.
- Free, no SaaS, no footage leaving the machine.

Cons / gaps to close:
- Hi-DPI (`deviceScaleFactor: 2`) and optional full-page shots are not wired
  (small additions).
- **Video is a bad fit.** Playwright can record webm (`recordVideo`) and you can
  slow it down and overlay a synthetic cursor, then transcode with ffmpeg, but
  the output is low-fidelity, has no audio/narration, no zoom, and no click
  emphasis. High effort for a mediocre reel. This is the wrong axis for
  presentation motion.

---

## 4. Option B - a dedicated capture tool

**Fit: excellent for motion and hero polish, poor for keeping-in-sync.**

The operator machine is Windows 11, so the shortlist is Windows-first and
local (footage of product internals should not be uploaded to a SaaS by
default):

| Tool | License | Best for | Notes |
|---|---|---|---|
| **ScreenToGif** | Free, OSS, Windows | Short UI-loop GIFs / mp4 for slides | Built-in frame editor, cursor + click highlight. Best single pick for the "GIF in a slide" need. |
| **OBS Studio** | Free, OSS, cross-platform | Narrated full-flow mp4 walkthroughs | Scenes, high-quality mp4/mkv, mic capture. The pick for a narrated demo reel. |
| **ShareX** | Free, OSS, Windows | Ad-hoc screenshots, region GIFs, annotation | Good general utility; overlaps ScreenToGif for GIFs. |
| **Loom** | Freemium SaaS | Instant narrated share links (webcam optional) | Uploads footage to a third party. Data-egress concern for anything showing real internals. Use only for throwaway shares over the demo seed. |
| **Screen Studio** | Paid, macOS only | Best-in-class auto-zoom / cursor polish | Not applicable to the Windows/Linux stack; note only if a Mac is available. |

Pros:
- Real narration, cursor/click emphasis, auto-zoom, captions - a polished
  60-90s reel is achievable in under an hour.
- No engineering; an operator can produce a reel directly.

Cons:
- **Manual and not reproducible.** Every UI change silently invalidates the
  footage; nothing re-shoots it. This is the mirror image of Playwright's
  strength.
- No enforced both-theme discipline, no automatic dev-chrome hiding.
- Data-safety depends on the operator remembering to record against the demo
  seed, not the production workspace. A SaaS tool adds egress risk on top.
- Not CI-able and does not fit the remote-runner capture plan (narration is an
  operator-desktop activity).

---

## 5. Recommendation - hybrid, split by capture kind

**Do not pick one tool for everything. Route each capture kind to the tool
whose comparative advantage matches it.**

1. **Stills -> extend the Playwright pipeline.** Make it the single source of
   truth for presentation screenshots. Add a presentation set (reuse the demo
   tour surfaces) at `deviceScaleFactor: 2`, marketing mode on, both themes,
   harvested to a stable location. The payoff is that the deck's imagery is
   regenerated on demand and never drifts from the shipped product.

2. **Motion -> a dedicated capture tool over the demo seed.** Use **ScreenToGif**
   for short silent UI-loop GIFs embedded in slides and **OBS Studio** for
   narrated full-flow mp4 walkthroughs. Both are free, Windows-native, and
   local (no footage egress). **Always record against the ADR-0056 demo
   workspace, never production.** Treat Loom as an optional quick-share only,
   with the SaaS-egress caveat.

3. **Explicitly deprioritize a bespoke Playwright video pipeline for the MVP
   presentation.** The ROI is poor (low fidelity, no narration) versus a screen
   recorder. Keep it as a later, timeboxed spike only if a *deterministic,
   re-runnable* motion clip is ever needed (for example an auto-looping GIF
   embedded in docs), not for the human-narrated MVP demo.

**Why the split:** stills that must stay in sync with a fast-moving UI want
Playwright's determinism; a one-off narrated reel wants a recorder's polish.
Forcing video into Playwright throws away the recorder's polish; forcing stills
into a manual recorder throws away Playwright's repeatability.

### Remote-execution alignment

Remote-ready **Phase 4** already moves Playwright capture onto the Linux
runner - the stills recommendation fits that unchanged (headless, CI, demo
seed). Motion recording is inherently an operator-desktop activity (real-time
narration) and should stay on the operator machine against a demo backend.
Call this out in the remote plan so "capture" is not assumed to be one thing:
**stills go remote with the runner; narrated motion stays local.**

---

## 6. Task cut (Task-Zuschnitt)

T1-T4 are the independently verifiable slices of the queued follow-up
`mvp-presentation-capture-kit` in `0-backlog`. One card keeps the capture set,
demo story, operator runbook, and storyboard aligned to the same presentation
decisions. T5 remains an optional later spike. T6 is decision-independent
cleanup and is not part of the queued MVP capture card.

- **T1 - Presentation stills set (Playwright).** Add a presentation capture set
  (extend `demo-screenshot-tour.spec.ts` or a new `presentation-*.spec.ts`) at
  `deviceScaleFactor: 2`, marketing mode on, both themes, over the ADR-0056
  demo seed. Add a `docs:presentation` npm script and a `generate.sh` sibling.
  Output to a stable `docs/assets/images/presentation/` (or the job
  `results/`). *Done when:* one command regenerates the full slide image set
  deterministically.

- **T2 - Demo-seed enrichment for the story.** Audit whether the demo seed
  tells the MVP story well (enough lanes, run/token history, a review with
  findings, a readable orchestrator-chat transcript). Fill gaps in
  `seed-demo-workspace.mjs` so both stills and recordings land on a compelling,
  deterministic stand. *Done when:* the seed produces a "demo-ready" workspace.

- **T3 - Recording runbook + tool setup (dedicated tool).** Install ScreenToGif
  and OBS; write `docs/operations/setup/presentation-capture.md`: which tool for
  GIF vs narrated mp4, capture settings (resolution, fps, cursor/click
  highlight), the demo-seed reset step, the both-theme rule, output naming
  (`--real`), and a hard "never record the production workspace" guard.
  *Done when:* an operator can produce a clean reel in under an hour by
  following the runbook.

- **T4 - MVP shot list / storyboard.** Define the deck: which stills (T1) and
  which flows (T3) tell the MVP story, in order, with captions. *Done when:* a
  storyboard the operator follows exists.

- **T5 (optional, timeboxed) - Scripted-GIF spike (Playwright video).** Only if
  a deterministic, re-runnable motion clip is later needed. Timeboxed spike:
  `recordVideo` + `slowMo` + synthetic cursor + ffmpeg to gif; decide keep/kill
  from the result. Deliberately out of scope for the MVP presentation itself.

- **T6 (cleanup, decision-independent) - Fix the visual-docs validator path.**
  `scripts/visual-docs/validate.mjs` reads `docs/visual/manifest.json`, which
  does not exist; point it at `docs/reports/visual/manifest.json`. Small,
  unblocks `generate.sh`'s validate step. Surfaced during this evaluation.

---

## 7. Completion record

- Presentation medium decided: live deck, optional silent loops, optional
  narrated backup, and no interactive click-through for the MVP.
- Deck format decided: 16:9 at 1920x1080 with hi-DPI source captures.
- Webcam decision: no webcam; microphone narration only when backup footage is
  produced.
- Follow-up created through the Task API: `mvp-presentation-capture-kit` in
  `0-backlog`, covering T1-T4.
