/* eslint-disable */
// Project Hub — full project view as its own editor tab.
// Inner left sub-nav (grouped), right content area.

const { useState: useStatePH } = React;

const HUB_SECTIONS = [
  { group: "Insight", items: [
    { id: "overview", label: "Overview", icon: "grid" },
    { id: "visual", label: "Visual Evidence", icon: "eye" },
    { id: "architecture", label: "Architecture", icon: "layout" },
    { id: "drift", label: "Drift", icon: "warn" },
    { id: "uxui", label: "UX / UI", icon: "sliders" },
    { id: "observability", label: "Observability", icon: "activity" },
  ]},
  { group: "Quality", items: [
    { id: "security", label: "Security", icon: "check" },
    { id: "tests", label: "Test Quality", icon: "check" },
    { id: "audits", label: "Audits & Checks", icon: "check" },
    { id: "runtime", label: "Product Runtime", icon: "play" },
  ]},
  { group: "Operations", items: [
    { id: "jobs", label: "Jobs", icon: "list" },
    { id: "tokens", label: "Token Usage", icon: "bot" },
    { id: "steering", label: "Steering Docs", icon: "book", badge: "15 / 15" },
  ]},
  { group: "Config", items: [
    { id: "settings", label: "Settings", icon: "settings" },
    { id: "orchestrator", label: "Orchestrator", icon: "runbook" },
    { id: "activity", label: "Activity", icon: "activity" },
  ]},
];

const STEERING_DOCS = [
  { name: "README", path: "README.md", desc: "Product description and on-boarding entry point. The first thing a new contributor reads.", size: "31.4 KB", time: "13.5.2026, 20:51:59" },
  { name: "AGENTS.md", path: "AGENTS.md", desc: "Single source of truth for agent instructions across CLIs.", size: "47.4 KB", time: "15.5.2026, 17:24:12", warn: false },
  { name: "CLAUDE.md", path: "CLAUDE.md", desc: "Compatibility shim that points Claude Code at AGENTS.md. Should stay tiny.", size: "363 B", time: "27.4.2026, 00:43:19" },
  { name: ".github/copilot-instructions.md", path: ".github/copilot-instructions.md", desc: "Compatibility shim for the GitHub Copilot coding agent. Should stay tiny.", size: "238 B", time: "27.4.2026, 00:17:45" },
  { name: "frontend/AGENTS.md", path: "frontend/AGENTS.md", desc: "Frontend-scoped agent instructions; applies to changes under frontend/.", size: "5.8 KB", time: "10.5.2026, 16:47:03" },
  { name: "ROADMAP.md", path: "ROADMAP.md", desc: "Product thesis, near-term themes, hard boundaries, and decision principles.", size: "60.3 KB", time: "14.5.2026, 14:56:03" },
  { name: "Task contract", path: "docs/agent-task-contract.md", desc: "The boundary the application enforces against CLI agents per task.", size: "8.0 KB", time: "13.5.2026, 20:54:38" },
  { name: "Skills architecture", path: "docs/skills-architecture.md", desc: "How portable skills are defined, distributed, and discovered.", size: "9.0 KB", time: "13.5.2026, 20:54:06" },
  { name: "CLI skills lookup", path: "docs/cli-skills/README.md", desc: "Per-CLI skill index. Required reading before touching a CLI driver.", size: "4.0 KB", time: "2.5.2026, 15:44:07" },
  { name: "Architecture decisions", path: "docs/architecture-decisions.md", desc: "Running log of architectural decisions and trade-offs.", size: "148.6 KB", time: "15.5.2026, 18:24:33", warn: false },
  { name: "Coding standards", path: "docs/coding-standards.md", desc: "Style, patterns, and review gates for incoming changes.", size: "12.3 KB", time: "8.5.2026, 09:12:00" },
];

// ============ Sections ============

function OverviewSection({ project }) {
  return (
    <div className="hub-grid">
      <div className="hub-card hub-card-wide">
        <div className="hub-card-head">
          <span className="hub-card-title">Health</span>
          <span className="tg review-accept">healthy</span>
        </div>
        <div className="hub-stats">
          <div className="hub-stat">
            <div className="hub-stat-label">Open tasks</div>
            <div className="hub-stat-value">{project.tasks}</div>
            <div className="hub-stat-sub muted">8 auto · 12 human · 26 backlog</div>
          </div>
          <div className="hub-stat">
            <div className="hub-stat-label">Auto-review</div>
            <div className="hub-stat-value" style={{ color: "var(--accent-2)" }}>96%</div>
            <div className="hub-stat-sub muted">acceptance · last 7d</div>
          </div>
          <div className="hub-stat">
            <div className="hub-stat-label">Cycle</div>
            <div className="hub-stat-value">4.2h</div>
            <div className="hub-stat-sub muted">median · last 30d</div>
          </div>
          <div className="hub-stat">
            <div className="hub-stat-label">Reissue rate</div>
            <div className="hub-stat-value" style={{ color: "var(--accent)" }}>11%</div>
            <div className="hub-stat-sub muted">last 7d (target ≤15)</div>
          </div>
        </div>
      </div>

      <div className="hub-card">
        <div className="hub-card-head">
          <span className="hub-card-title">Repository</span>
          <button className="btn-ghost" style={{ padding: "2px 8px", fontSize: 11 }}><Icon name="branch" size={11}/> View</button>
        </div>
        <div className="hub-kv">
          <div><span className="muted">path</span><span className="mono">~/work/{project.id}</span></div>
          <div><span className="muted">branch</span><span className="mono"><Icon name="branch" size={10}/> {project.branch}</span></div>
          <div><span className="muted">remote</span><span className="mono">origin/main · ↑0 ↓0</span></div>
          <div><span className="muted">last push</span><span>14h ago by Anna B</span></div>
        </div>
      </div>

      <div className="hub-card">
        <div className="hub-card-head">
          <span className="hub-card-title">Active CLIs</span>
        </div>
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          {window.MOCK.CLIS.map(c => (
            <div key={c.id} style={{ display: "flex", alignItems: "center", gap: 8 }}>
              <span className="cli-glyph" style={{ background: c.color, width: 18, height: 18, fontSize: 10 }}>{c.name[0]}</span>
              <span style={{ flex: 1, fontSize: 12 }}>{c.name}</span>
              <span className="bar" style={{ flex: 1, height: 4, background: "var(--bg-elevated-2)", borderRadius: 2, overflow: "hidden" }}>
                <div style={{ width: `${Math.round((c.quotas[0]?.used || 0)*100)}%`, height: "100%", background: c.color }}/>
              </span>
              <span className="mono muted" style={{ fontSize: 10, width: 36, textAlign: "right" }}>{Math.round((c.quotas[0]?.used || 0)*100)}%</span>
            </div>
          ))}
        </div>
      </div>

      <div className="hub-card hub-card-wide">
        <div className="hub-card-head">
          <span className="hub-card-title">Recent activity</span>
          <button className="btn-ghost" style={{ padding: "2px 8px", fontSize: 11 }}>See all</button>
        </div>
        <div style={{ display: "flex", flexDirection: "column" }}>
          {window.MOCK.ACTIVITY_FEED.slice(0, 5).map((f, i) => (
            <div key={i} style={{ display: "flex", gap: 10, padding: "6px 0", borderBottom: i < 4 ? "1px solid var(--border)" : "0" }}>
              <span className="mono muted" style={{ fontSize: 10, width: 36 }}>{f.time}</span>
              <span style={{ fontSize: 12, flex: 1 }}>{f.text}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

function SteeringDocsSection() {
  const [editing, setEditing] = useStatePH(null);
  return (
    <>
      <div className="hub-section-head">
        <span style={{ fontSize: 18, marginRight: 4 }}>📚</span>
        <h2 className="hub-section-title">Steering Docs</h2>
        <span className="tg review-accept">15 / 15 present</span>
        <span className="spc"/>
        <button className="btn-ghost"><Icon name="plus" size={11}/> Add doc</button>
        <button className="btn-primary"><Icon name="refresh" size={11} color="#1a1a1a"/> Re-scan</button>
      </div>
      <p className="hub-section-lead">
        Inventory of the agent-facing instruction files for this project. Raw sources stay visible; the human summary and warnings below help spot stale or missing guidance. Action buttons queue normal <code>1-preparation</code> tasks — the surface never silently rewrites docs.
      </p>

      <div className="hub-subhead">RAW SOURCES</div>
      <div className="hub-doclist">
        {STEERING_DOCS.map((d, i) => (
          <div
            key={i}
            className="hub-doc"
            onMouseEnter={() => setEditing(i)}
            onMouseLeave={() => setEditing(null)}
          >
            <div className="hub-doc-main">
              <div className="hub-doc-name">
                <span>{d.name}</span>
                <span className="hub-doc-path mono">{d.path}</span>
              </div>
              <div className="hub-doc-desc">{d.desc}</div>
            </div>
            <div className="hub-doc-meta">
              <span className="mono muted" style={{ fontSize: 11 }}>{d.size}</span>
              <span className="mono muted" style={{ fontSize: 11 }}>·</span>
              <span className="mono muted" style={{ fontSize: 11 }}>{d.time}</span>
              {editing === i && (
                <div className="hub-doc-actions">
                  <button className="ic-btn" title="Open"><Icon name="file" size={12}/></button>
                  <button className="ic-btn" title="Queue rewrite task"><Icon name="send" size={12}/></button>
                </div>
              )}
            </div>
          </div>
        ))}
      </div>
    </>
  );
}

function JobsSection() {
  const { TASKS } = window.MOCK;
  const tasks = TASKS.filter(t => t.project === "agent-task-processor").slice(0, 10);
  return (
    <>
      <div className="hub-section-head">
        <span style={{ fontSize: 18, marginRight: 4 }}>📋</span>
        <h2 className="hub-section-title">Jobs</h2>
        <span className="tg label">{TASKS.filter(t => t.project === "agent-task-processor").length} total</span>
        <span className="spc"/>
        <button className="btn-ghost"><Icon name="filter" size={11}/> Filter</button>
        <button className="btn-ghost"><Icon name="archive" size={11}/> Archived</button>
        <button className="btn-primary"><Icon name="plus" size={11} color="#1a1a1a"/> New job</button>
      </div>
      <p className="hub-section-lead">
        All jobs queued, running, and awaiting human review in this project. Use the board for kanban view; this is the flat list for bulk operations.
      </p>
      <div className="hub-table">
        <div className="hub-tr hub-th">
          <div style={{ width: 60 }}>#</div>
          <div style={{ flex: 1 }}>Title</div>
          <div style={{ width: 100 }}>Type</div>
          <div style={{ width: 140 }}>State</div>
          <div style={{ width: 90 }}>CLI</div>
          <div style={{ width: 100 }}>Last activity</div>
        </div>
        {tasks.map(t => {
          const tm = window.MOCK.TYPE_META[t.type];
          const cm = t.cli ? window.MOCK.CLI_META[t.cli] : null;
          return (
            <div key={t.id} className="hub-tr">
              <div className="mono muted" style={{ width: 60 }}>{t.num}</div>
              <div style={{ flex: 1, color: "var(--fg-strong)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{t.title}</div>
              <div style={{ width: 100 }}><span className={`tg ${t.type}`}>{tm?.icon} {tm?.label}</span></div>
              <div style={{ width: 140 }}><span className="tg label">{t.state}</span></div>
              <div style={{ width: 90 }}>{cm && <span style={{ display: "inline-flex", alignItems: "center", gap: 4, fontSize: 11 }}><span className="cli-glyph sm" style={{ background: cm.color }}>{cm.glyph}</span> {cm.label}</span>}</div>
              <div style={{ width: 100 }} className="mono muted">{t.activity}</div>
            </div>
          );
        })}
      </div>
    </>
  );
}

function PlaceholderSection({ id, label }) {
  return (
    <div className="hub-empty">
      <Icon name="layout" size={28} color="var(--fg-muted)"/>
      <div className="hub-empty-title">{label}</div>
      <div className="hub-empty-desc">This section will surface {label.toLowerCase()} data for the project. Coming up next.</div>
      <button className="btn-ghost"><Icon name="plus" size={11}/> Configure {label}</button>
    </div>
  );
}

const ACTIVITY_ITEMS = [
  { id: "a1", time: "14:13", kind: "decision", verdict: "reissue", actor: "Orchestrator (Codex)", task: { num: "#1", title: "Implement session-task linkage chip…" }, summary: "Decision: reissue (multi-aspect block)", body: "Aspects: requirement-fit=block, code-quality=block, documentation-impact=concerns, tests-and-evidence=block." },
  { id: "a2", time: "14:08", kind: "step", actor: "Claude Opus 4.7", task: { num: "#1", title: "Implement session-task linkage chip…" }, summary: "Playwright spec discovered cleanly", body: "session-task-link-chip.spec.ts type-checks. Execution reserved for stable's Playwright pass per the dev-backend-lifecycle rule." },
  { id: "a3", time: "14:01", kind: "commit", actor: "Codex GPT-5", task: { num: "#3", title: "Bug: DELETE-Button auf den Task-Karten…" }, summary: "Pushed 1e2fcf1 (3 files)", body: "Branch: agent/bug/delete-button-noop-3 · +42 / -11 across CliEndpoints.cs, CliService.cs, DeleteButton.tsx" },
  { id: "a4", time: "13:42", kind: "accept", actor: "Orchestrator (Claude)", task: { num: "#18", title: "Auto-review: Lotta Dashboard chart legend" }, summary: "Auto-review accepted", body: "All four aspects accept. Job moved to human-review for sign-off." },
  { id: "a5", time: "13:30", kind: "system", actor: "Auto-Run", task: null, summary: "Auto-Review idle — no queued jobs", body: "Survey cycle completed in 28s." },
  { id: "a6", time: "13:12", kind: "info", actor: "Orchestrator", task: null, summary: "Human review queue grew to 108 items", body: "Routing rule \"lane:auto-review → Codex\" continues to apply." },
  { id: "a7", time: "12:55", kind: "warn", actor: "Auto-Push", task: { num: "#4", title: "Auto-Push-Strategie — nur Completed-Lane" }, summary: "Auto-Push deferred — Completed-lane gate failed", body: "32 files touched; rule requires the job to land in Completed before push. Job currently in human-review." },
  { id: "a8", time: "12:30", kind: "decision", verdict: "accept", actor: "Orchestrator (Claude)", task: { num: "#8", title: "Fix Orchestrator decision closing task incorrectly" }, summary: "Decision: accept", body: "All aspects accept. Pushed to human-review for visual evidence sign-off." },
];

const KIND_META = {
  decision: { label: "Decision", color: "var(--accent)", icon: "send" },
  step: { label: "Step", color: "var(--fg-dim)", icon: "check" },
  commit: { label: "Commit", color: "var(--accent-3)", icon: "branch" },
  accept: { label: "Accept", color: "var(--accent-2)", icon: "check" },
  system: { label: "System", color: "var(--fg-muted)", icon: "settings" },
  info: { label: "Info", color: "var(--accent-4)", icon: "bell" },
  warn: { label: "Warning", color: "var(--accent-warn)", icon: "warn" },
};

function ActivitySection({ project }) {
  const [selected, setSelected] = useStatePH("a1");
  const [filter, setFilter] = useStatePH("all");
  const items = ACTIVITY_ITEMS.filter(i => filter === "all" || i.kind === filter);
  const item = ACTIVITY_ITEMS.find(i => i.id === selected) || ACTIVITY_ITEMS[0];
  const meta = KIND_META[item.kind] || KIND_META.info;

  return (
    <>
      <div className="hub-section-head">
        <span style={{ fontSize: 18, marginRight: 4 }}>📡</span>
        <h2 className="hub-section-title">Activity</h2>
        <span className="tg label">{ACTIVITY_ITEMS.length} events</span>
        <span className="spc"/>
        <button className="btn-ghost"><Icon name="refresh" size={11}/> Reload</button>
      </div>

      <div className="hub-act-filter">
        {[
          { id: "all", label: "All" },
          { id: "decision", label: "Decisions" },
          { id: "commit", label: "Commits" },
          { id: "accept", label: "Accepts" },
          { id: "warn", label: "Warnings" },
          { id: "system", label: "System" },
        ].map(f => (
          <button key={f.id} className={`flt-chip ${filter === f.id ? "active" : ""}`} onClick={() => setFilter(f.id)}>{f.label}</button>
        ))}
      </div>

      <div className="hub-act-master">
        {items.map(i => {
          const km = KIND_META[i.kind] || KIND_META.info;
          return (
            <button
              key={i.id}
              className={`hub-act-row ${selected === i.id ? "active" : ""}`}
              onClick={() => setSelected(i.id)}
            >
              <span className="hub-act-time mono">{i.time}</span>
              <span className="hub-act-kind" style={{ color: km.color }}>
                <span className="hub-act-dot" style={{ background: km.color }}/>
                {km.label}
              </span>
              <span className="hub-act-task mono">{i.task ? i.task.num : "—"}</span>
              <span className="hub-act-summary">{i.summary}</span>
              <span className="hub-act-actor">{i.actor}</span>
            </button>
          );
        })}
      </div>

      <div className="hub-act-detail">
        <div className="hub-act-detail-head">
          <span className="hub-act-kind" style={{ color: meta.color, fontSize: 12, fontWeight: 600 }}>
            <span className="hub-act-dot" style={{ background: meta.color, width: 8, height: 8 }}/>
            {meta.label}
            {item.verdict && <span className={`tg ${item.verdict === "accept" ? "review-accept" : "review-reissue"}`} style={{ marginLeft: 8 }}>{item.verdict.toUpperCase()}</span>}
          </span>
          <span className="spc"/>
          <span className="mono muted" style={{ fontSize: 11 }}>{item.time}</span>
        </div>
        <div className="hub-act-detail-title">{item.summary}</div>
        <div className="hub-act-detail-meta">
          <span>by <strong>{item.actor}</strong></span>
          {item.task && (
            <>
              <span>·</span>
              <span>on <span className="mono" style={{ color: "var(--accent)" }}>{item.task.num}</span> <span style={{ color: "var(--fg)" }}>{item.task.title}</span></span>
            </>
          )}
        </div>
        <div className="hub-act-detail-body">{item.body}</div>
        <div className="hub-act-detail-actions">
          {item.task && <button className="btn-ghost"><Icon name="layout" size={11}/> Open task</button>}
          {item.kind === "commit" && <button className="btn-ghost"><Icon name="diff" size={11}/> View diff</button>}
          {item.kind === "decision" && <button className="btn-ghost"><Icon name="activity" size={11}/> Open run</button>}
        </div>
      </div>
    </>
  );
}

function SettingsSection({ project }) {
  return (
    <>
      <div className="hub-section-head">
        <span style={{ fontSize: 18, marginRight: 4 }}>⚙</span>
        <h2 className="hub-section-title">Project Settings</h2>
        <span className="spc"/>
      </div>
      <div className="hub-grid">
        <div className="hub-card">
          <div className="hub-card-head"><span className="hub-card-title">Identity</span></div>
          <div className="hub-form">
            <label><span>Name</span><input defaultValue={project.name}/></label>
            <label><span>Short code</span><input defaultValue={project.short} style={{ maxWidth: 80 }}/></label>
            <label><span>Color</span>
              <div style={{ display: "flex", gap: 6 }}>
                {["#d97757","#4ec9b0","#569cd6","#c586c0","#b5cea8","#cca700"].map(c => (
                  <span key={c} style={{ width: 24, height: 24, borderRadius: 4, background: c, border: c === project.color ? "2px solid var(--fg-strong)" : "1px solid var(--border)" }}/>
                ))}
              </div>
            </label>
          </div>
        </div>
        <div className="hub-card">
          <div className="hub-card-head"><span className="hub-card-title">Automation</span></div>
          <div className="hub-form">
            <label><span>Auto-run</span><span className="toggle on"><div/></span></label>
            <label><span>Auto-review</span><span className="toggle on"><div/></span></label>
            <label><span>Auto-push</span><span className="toggle"><div/></span></label>
            <label><span>Concurrency</span><input type="number" defaultValue={3} style={{ maxWidth: 80 }}/></label>
          </div>
        </div>
        <div className="hub-card hub-card-wide">
          <div className="hub-card-head"><span className="hub-card-title">Danger zone</span></div>
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn-ghost"><Icon name="archive" size={11}/> Archive project</button>
            <button className="btn-ghost" style={{ color: "var(--accent-6)", borderColor: "var(--accent-6)" }}><Icon name="close" size={11}/> Delete project</button>
          </div>
        </div>
      </div>
    </>
  );
}

function ProjectHub({ projectId, initialSection = "steering" }) {
  const project = window.MOCK.PROJECTS.find(p => p.id === projectId) || window.MOCK.PROJECTS[0];
  const [section, setSection] = useStatePH(initialSection);
  const [navW, setNavW] = useStatePH(220);

  const startResize = (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = navW;
    const onMove = (ev) => {
      const dx = ev.clientX - startX;
      setNavW(Math.max(160, Math.min(360, startW + dx)));
    };
    const onUp = () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
    };
    document.body.style.cursor = "ew-resize";
    document.body.style.userSelect = "none";
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  const renderSection = () => {
    switch (section) {
      case "overview": return <OverviewSection project={project}/>;
      case "steering": return <SteeringDocsSection/>;
      case "jobs": return <JobsSection/>;
      case "settings": return <SettingsSection project={project}/>;
      case "activity": return <ActivitySection project={project}/>;
      default: return <PlaceholderSection id={section} label={HUB_SECTIONS.flatMap(g => g.items).find(s => s.id === section)?.label || section}/>;
    }
  };

  return (
    <div className="hub">
      <div className="hub-banner">
        <div className="hub-banner-glyph" style={{ background: project.color }}>{project.short}</div>
        <div className="hub-banner-text">
          <div className="hub-banner-title">{project.name}</div>
          <div className="hub-banner-sub">
            <span className="mono">~/work/{project.id}</span>
            <span>·</span>
            <span className="mono"><Icon name="branch" size={10}/> {project.branch}</span>
            <span>·</span>
            <span>{project.tasks} open · auto-run on</span>
          </div>
        </div>
        <span className="spc"/>
        <button className="btn-ghost"><Icon name="layout" size={11}/> Open board</button>
        <button className="btn-primary"><Icon name="plus" size={11} color="#1a1a1a"/> New job</button>
      </div>
      <div className="hub-body">
        <div className="hub-nav" style={{ width: navW }}>
          {HUB_SECTIONS.map(group => (
            <div key={group.group} className="hub-nav-group">
              <div className="hub-nav-group-head">{group.group}</div>
              {group.items.map(it => (
                <button
                  key={it.id}
                  className={`hub-nav-row ${section === it.id ? "active" : ""}`}
                  onClick={() => setSection(it.id)}
                >
                  <span className="hub-nav-icon"><Icon name={it.icon} size={14}/></span>
                  <span className="hub-nav-label">{it.label}</span>
                  {it.badge && <span className="hub-nav-badge">{it.badge}</span>}
                </button>
              ))}
            </div>
          ))}
        </div>
        <div className="hub-resize" onMouseDown={startResize}/>
        <div className="hub-content">
          <div className="hub-content-inner">
            {renderSection()}
          </div>
        </div>
      </div>
    </div>
  );
}

window.ProjectHub = ProjectHub;
