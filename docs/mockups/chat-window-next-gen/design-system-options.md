# Design System Options

This note answers the design-system question for the next-generation chat workbench. The goal is not to make Agent Task Processor look like a generic dashboard. The goal is a production-ready workbench system for expert software review: dense, inspectable, themeable, keyboard-friendly, and familiar to users who know VS Code.

## Recommendation

Use an internal **Found Next Workbench Design System**:

- Borrow VS Code's container philosophy: Activity Bar modules, Side Bar views, workbench documents, supporting panels, Status Bar items, command/quick-pick flows.
- Use VS Code Codicons as the primary icon vocabulary, preferably through `@vscode/codicons` once the prototype moves toward production.
- Build Angular-native primitives on top of Angular CDK and Angular Aria instead of importing a broad visual component kit.
- Keep the current CSS-token direction: local design tokens, light-first theme, dark parity, compact density, 1px borders, small radii, sparse toolbar actions.
- Use Code-OSS source as a reference for measurements and behavior, not as a component library to copy wholesale.

This gives us the VS Code mental model without binding the product to VS Code internals.

## Candidate Systems

| Option | What it gives us | Fit | Risk | Decision |
|--------|------------------|-----|------|----------|
| VS Code UX Guidelines | Official rules for Activity Bar, Sidebars, Panels, Status Bar, Views, Editor Actions, Quick Picks, Webviews. | Excellent for information architecture. | Guidelines are for extensions, not Angular apps. | Use as the primary philosophy. |
| Code-OSS source (`microsoft/vscode`) | Real implementation of workbench layout, theming, icons, trees, split views, status bar, editor groups. | Excellent as a reference. | Huge Electron/Monaco service architecture, not reusable as an Angular component kit. Copying internals creates maintenance and licensing hygiene work. | Inspect and measure. Do not clone wholesale. |
| VS Code Codicons | Product-icon vocabulary used by VS Code in views, editor, hovers, and status bar. | Excellent. Matches the desired small-icon density. | Icon-only UI needs tooltips and labels in comfortable density. | Strong candidate for production dependency. |
| VS Code Webview UI Toolkit | VS Code-like webview components, theme-aware. | Conceptually relevant. | Deprecated on January 1, 2025. | Do not adopt. Mine ideas only. |
| Angular CDK / Angular Aria | Accessible overlays, focus, menus, dialogs, tree/list behavior, drag/drop, portals, a11y primitives. | Excellent implementation base for custom design. | More component work than a complete UI kit. | Use for behavior primitives. |
| Angular Material | Official Angular components, accessible and well tested. | Good for conventional forms/dialogs. | Material visual language is not VS Code-like and can fight compact expert density. | Avoid as the main visual system; use CDK instead. |
| Fluent UI Web Components | Microsoft design language, web components, design tokens, Angular integration possible. | Medium. Microsoft-family and accessible. | More Microsoft 365 than VS Code; web components add integration and styling boundaries. | Possible spike for forms only, not default. |
| Taiga UI | Angular-first component kit with many components and customization. | Medium. Strong Angular ecosystem option. | Its own visual language and density; could compete with Found Next tokens. | Consider only if CDK custom primitives become too expensive. |
| PrimeNG / NG-ZORRO / broad admin kits | Lots of ready-made enterprise components. | Low for this workbench. | Dashboard/admin look, heavier theme conventions, visual drift from VS Code. | Do not use for this surface. |
| Tailwind / shadcn-style copy components | Fast custom styling and owned markup. | Medium. Good ownership, but current app is not Tailwind-based. | Introducing Tailwind is a broad build and styling decision. shadcn is React-first. | Not for this slice. |
| Monaco Editor | Real editor/diff editor component used by VS Code. | High for future source/diff panes. | Adds weight and integration work. | Consider later for real Git/source review, not for the mockup. |

## Can We Clone VS Code?

We can clone **Code-OSS** for research. The `microsoft/vscode` repository is MIT licensed, while the Visual Studio Code product distribution has Microsoft-specific customizations and a product license. That distinction matters.

Allowed and useful:

- Clone Code-OSS locally to inspect source, layout constants, workbench structure, theme token names, codicon usage, split-view behavior, and status bar grouping.
- Use the public UX Guidelines and Product Icon Reference as design rules.
- Use `@vscode/codicons` as an icon font or asset package.
- Recreate the interaction model independently in Angular.

Avoid by default:

- Copying VS Code workbench CSS and DOM structure wholesale.
- Importing VS Code internal packages into the Angular app.
- Reusing product branding, trademarked assets, or Microsoft-specific distribution pieces.
- Treating VS Code's implementation as a stable API. It is application source, not a design-system package.

If a small MIT-licensed snippet is ever copied intentionally, it needs explicit attribution and a reason. The preferred path is still: inspect, learn, reimplement.

## Found Next Primitive Set

The production design system should start small and explicit:

| Primitive | Purpose | Notes |
|-----------|---------|-------|
| `FnActivityBar` | Global modules: Projects, Tasks / Queue, Search, Git, QA, Tokens, Settings. | 48px default, compact possible, codicons, title/aria-label required. |
| `FnSideView` | Queue, project context, grouped lists. | Closable, narrow, not permanent chrome. |
| `FnDocumentTabs` | Opened workbench documents. | Summary default, Chat/Git/Screenshots/Debug closable as appropriate. |
| `FnWorkbenchSplit` | Controlled side-by-side review. | Splitter, keyboard resize, no full docking system. |
| `FnStatusBar` | Ambient run/workspace state. | Short labels, quota visible, contextual controls right. |
| `FnPopover` | Shallow drill-down from status or toolbar. | Not a mini dashboard. Link to full document/debug for depth. |
| `FnCommandPicker` | Model, project/owner, artifact jump, target scope. | Quick-pick style with title, placeholder, section separators. |
| `FnToolbarButton` | Contextual pane actions. | Icon-first, tooltip required, overflow for rare actions. |
| `FnTraceDisclosure` | Technical layer escape hatch. | Default hidden, structured reveal. |

## Baseline Measurements

These are not final pixel laws, but they should guide implementation until screenshots prove otherwise:

| Element | Comfortable | Compact |
|---------|-------------|---------|
| Activity Bar | 48px wide | 36 to 40px wide |
| Status Bar | 24 to 28px high | 22 to 24px high |
| Task chrome | 34 to 38px high | 30 to 34px high |
| Document tabs | 30 to 34px high | icon-only or 28px high |
| Icon button | 28px square | 24px square |
| Primary icon | 16px | 14 to 16px |
| Local padding | 6 to 8px | 4 to 6px |
| Border | 1px | 1px |
| Radius | 4 to 8px | 4 to 6px |
| Queue module | 144 to 176px wide | closable, 132 to 156px if visible |
| Project side sheet | 280 to 360px default | closable, resizable |

The practical rule is vertical discipline: no stacked metadata bands above the transcript. Move metadata into the Activity Bar, Side Bar, document headers, composer toolbar, Status Bar, popovers, or debug surfaces.

## Open Spikes

1. **Codicons spike:** replace prototype inline paths with `@vscode/codicons` or generated local SVG symbols and verify bundle impact.
2. **Angular CDK spike:** implement `FnPopover`, `FnCommandPicker`, and keyboard splitter behavior with CDK/ARIA primitives.
3. **Code-OSS measurement spike:** inspect activity bar, status bar, editor tab, side bar, and split-view measurements in Code-OSS and compare with our current tokens.
4. **Monaco spike:** evaluate a lazy-loaded Monaco diff editor for Git/source review only.
5. **Design-token audit:** extract `FoundNextThemeTokens` into a single SCSS/CSS token document so production components do not invent local colors.

## Source Links

- VS Code UX Guidelines overview: https://code.visualstudio.com/api/ux-guidelines/overview
- VS Code Activity Bar guidance: https://code.visualstudio.com/api/ux-guidelines/activity-bar
- VS Code Sidebars guidance: https://code.visualstudio.com/api/ux-guidelines/sidebars
- VS Code Panel guidance: https://code.visualstudio.com/api/ux-guidelines/panel
- VS Code Status Bar guidance: https://code.visualstudio.com/api/ux-guidelines/status-bar
- VS Code Product Icon Reference: https://code.visualstudio.com/api/references/icons-in-labels
- Code-OSS repository: https://github.com/microsoft/vscode
- VS Code Webview UI Toolkit: https://github.com/microsoft/vscode-webview-ui-toolkit
- Fluent UI Web Components: https://learn.microsoft.com/en-us/fluent-ui/web-components/
- Angular Components / CDK repository: https://github.com/angular/components
- Taiga UI: https://taiga-ui.dev/
