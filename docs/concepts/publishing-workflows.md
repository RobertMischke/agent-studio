# Publishing workflows — dead simple, task-anchored

**Status:** concept v1, 2026-07-10 — operator-requested ("vollständiges und
sehr gut durchdachtes Konzept"). Related:
[`engineering-workstream.md`](engineering-workstream.md) (self-provisioning
precedent), AGT-2028 (task-spawner step), AGT-2029 (task dependencies),
AGT-1999 (merge-step pushes develop).

## 0. The rediscovered ground truth (operator asked: "war das Magie?")

No magic — it was built on 2026-07-07 and documented; the pieces:

| Piece | Where | State |
|---|---|---|
| Hosting VM | Hetzner CX23 (Nürnberg), Caddy/systemd, `/srv/sites/<slug>/current`, SSH alias `agent-orchestrator-web` | live |
| Hosting meta-repo | private repo `agent-orchestrator-website` (`hosting/setup-vm.md`, `Caddyfile`, `sites.json`, scripts) — **this is the "Metaprojekt"** | exists, local checkout present |
| Site docs | `agent-studio-marketing/06-website-planung/04-infrastruktur-und-sicherheit/deployment-setup-agent-orchestrator-dev.md` | authoritative narrative |
| CAR NuGet | `.github/workflows/release.yml` → nuget.org via **Trusted Publishing (OIDC)**, triggered by version tag (`v0.3.1` current, csproj `<Version>` is source) | automated on tag |
| CAR website | `.github/workflows/deploy-website.yml` (→ VM `/runner/`) | automated |
| CAC npm | `release.yml` → npmjs via Trusted Publishing on tag; **one-time first publish must be manual** (`npm login`, v0.1.0) — still pending | blocked on operator |
| CAC website | `pages.yml` (GitHub Pages) + `deploy-website.yml` (→ VM `/chat/`) | automated |
| Studio consumes CAR | `PackageReference CodingAgentRunner 0.3.1` (NuGet, not project ref) | manual bump per release |

So publishing already IS tag-driven and keyless where it's set up. What is
missing is **visibility and a one-move trigger in the product** — today you
must remember the mechanics per repo.

## 1. Operator intent (anchors, 2026-07-10)

- Every project has publish workflows of different kinds (NuGet, npm,
  websites). "Wir brauchen ein Konzept und einen Weg, das ins User
  Interface zu kriegen."
- **Dead simple:** "Ich muss sehr, sehr easy sehen: hier ist irgendwas
  Fertiges, was ich publizieren könnte — das ist im Prinzip nach jedem Task
  der Fall. Dann brauche ich einen schnellen Weg, tatsächlich zu
  publizieren — optimal an einem Task."

## 2. Model: publish targets, derived not configured

A **publish target** is a per-project entity `{kind, name, currentVersion,
trigger, consumers}`. Targets are **derived from repo facts** (house rule:
convention over settings):

- `release.yml` with tag trigger → package target (npm/NuGet — kind from
  workflow content/package manifest); current version from latest `v*` tag.
- `deploy-website.yml` / `pages.yml` → website target.
- `sites.json` in the hosting meta-repo remains the hosting registry.

Manual configuration only as override (add/hide a target), never as the
primary source.

## 3. Publishable-state detection

Per target, the backend computes the **pending delta**: merged task-commits
on the release branch since the last published version/deploy that touch the
target's path scope (package source paths vs. `website/` etc.).

- Project level: a **publish badge** per target — "NuGet 0.3.1 → 4 merged
  tasks pending", "Website: 2 changes since last deploy". Zero pending =
  quiet (no badge noise).
- Task level: on acceptance, each task shows **which targets it made
  publishable** — a small chip on the accepted card / task detail:
  "publishable: npm, website".

## 4. The one-move publish (UI)

**Where:** Project Hub header (per-target rows) + the task detail of an
accepted task (chips → same action).

**Package targets** — deliberate versioning, guided but one move:
1. Click "Publish" → panel shows pending tasks since last version, proposes
   the next **semver** (patch/minor from the pending tasks' taskType mix;
   editable).
2. Confirm → backend bumps the version source (csproj / package.json),
   commits, creates + pushes the `vX.Y.Z` tag → the existing OIDC workflow
   does the rest. **No new publish mechanics — the product drives the
   proven tag path.**
3. Panel tracks the workflow run (status, link), reports the published
   version.

**Website targets** — no versioning ceremony: "Deploy" triggers the
workflow (`workflow_dispatch` or empty commit per repo convention), shows
run status. Optionally **auto** (see §5).

## 5. Automation ladder (per target, explicit setting)

1. **manual** — badge only, human clicks (default for packages).
2. **suggest** — like manual, plus the accept-flow surfaces "publish now?"
   right at the task (operator's "optimal an einem Task").
3. **auto** — publish/deploy on every relevant merge (sane for websites;
   packages stay ≥ suggest — versions are decisions).

## 6. Consumer chain (v2, composes with existing concepts)

After a successful package publish, the known **consumers** (Studio's
`PackageReference`, apps with npm dep) are stale. The task-spawner step type
(AGT-2028) covers this: spawn "bump CodingAgentRunner to 0.4.0" into the
consumer project, with a `waitsOn` the publish (AGT-2029). This closes
Robert's observed chain CAR-3 → AGT cost audit → website without manual
shepherding.

## 7. Non-goals

- No secret management in the product (Trusted Publishing stays keyless;
  the VM keeps its SSH keys outside the app).
- No replacement of the GH workflows — the product is a **cockpit over the
  existing rails**, not a second publish engine.
- The npm **first publish** of coding-agent-chat stays a one-time manual
  operator act (npm requires it before a Trusted Publisher can be attached).

## 8. Slices

| Slice | Scope | Gate |
|---|---|---|
| PUB-1 | target derivation + pending-delta detection + badges (project hub, accepted-task chips) — read-only | none |
| PUB-2 | publish actions: semver proposal, version-bump commit, tag push, workflow-run tracking; website deploy trigger; automation ladder setting | PUB-1 |
| PUB-3 | consumer-bump spawning via task-spawner + waitsOn | PUB-2, AGT-2028/2029 |

## 9. Executed instances (log)

- **2026-07-10 — CodingAgentRunner v0.5.0 → nuget.org** (release.yml, Trusted
  Publishing; tag pushed by the night operator). Contents: `ultra` reasoning
  level + gpt-5.6 family recognition (CAR-2), **CodingAgentRunner.Pricing**
  price-history catalog + cost API (CAR-3), Workstream frame onboarding
  (CAR-1). Consumer chain (manual until AGT-2028/2029 land): AGT-2025 bumps
  the Studio PackageReference, AGT-2027 moves cost displays to the Pricing
  API, WEB-4 updates the public website. This entry is exactly the kind of
  record the Workstream Log will carry automatically once EW-2 (collector)
  exists.
