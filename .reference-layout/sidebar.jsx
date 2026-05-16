/* eslint-disable */
// Sidebar panels — context-dependent content for each activity bar selection
const { useState, useMemo } = React;

// ============ Projects (file-tree-like) ============
function ProjectsPanel({ activeProject, activeWorkspace, wsProjects, onSelectProject, onOpenHub, tasks, onOpenTask, activeTaskId, onSelectWorkspace }) {
  const [expanded, setExpanded] = useState({ [activeProject]: true, [`ws-${activeWorkspace}`]: true });
  const { TASKS, WORKSPACES, PROJECTS: ALL_PROJECTS } = window.MOCK;

  return (
    <>
      <div className="sb-header">
        <span>Explorer</span>
        <div className="actions">
          <button className="ic-btn" title="New task"><Icon name="plus" size={14}/></button>
          <button className="ic-btn" title="Refresh"><Icon name="refresh" size={14}/></button>
          <button className="ic-btn"><Icon name="collapse" size={14}/></button>
        </div>
      </div>
      <div className="sb-body">
        {WORKSPACES.map(ws => {
          const wsExpanded = expanded[`ws-${ws.id}`];
          const isActiveWs = ws.id === activeWorkspace;
          const wsProjectsList = ws.projectIds.map(pid => ALL_PROJECTS.find(p => p.id === pid)).filter(Boolean);
          return (
            <div className="sb-section" key={ws.id}>
              <div
                className={`sb-section-head ws-head ${isActiveWs ? "active-ws" : ""}`}
                onClick={() => setExpanded(e => ({ ...e, [`ws-${ws.id}`]: !e[`ws-${ws.id}`] }))}
              >
                <span className="chev"><Icon name={wsExpanded ? "chevronDown" : "chevronRight"} size={12}/></span>
                <span className="ws-head-icon"><Icon name="folder" size={11} color={isActiveWs ? "var(--accent)" : "var(--fg-dim)"}/></span>
                <span className="ws-head-name">{ws.name}</span>
                <span className="count">{ws.projectIds.length}</span>
                {!isActiveWs && (
                  <button
                    className="ws-activate-btn"
                    title="Make active workspace"
                    onClick={(e) => { e.stopPropagation(); onSelectWorkspace && onSelectWorkspace(ws.id); }}
                  >
                    <Icon name="send" size={9}/>
                  </button>
                )}
              </div>
              {wsExpanded && (
                <div className="sb-section-body">
                  {wsProjectsList.map(p => {
                    const open = expanded[p.id];
                    const projTasks = TASKS.filter(t => t.project === p.id);
                    return (
                      <React.Fragment key={p.id}>
                        <div className={`tree-row tree-project ${activeProject === p.id ? "active" : ""}`}>
                          <span className="tw-icon" style={{ width: 10, cursor: "pointer" }} onClick={() => setExpanded(e => ({...e, [p.id]: !e[p.id]}))}>
                            <Icon name={open ? "chevronDown" : "chevronRight"} size={10}/>
                          </span>
                          <span className="tw-icon" style={{ cursor: "pointer" }} onClick={() => onSelectProject(p.id)}>
                            <span className="proj-tree-icon" style={{ color: p.color }}>
                              <Icon name="folder" size={14}/>
                            </span>
                          </span>
                          <span className="tw-label" style={{ cursor: "pointer" }} onClick={() => onSelectProject(p.id)}>{p.name}</span>
                          <button className="tree-hub-link" title="Open Project Hub" onClick={(e) => { e.stopPropagation(); onOpenHub && onOpenHub(p.id); }}>
                            <Icon name="grid" size={10}/>
                          </button>
                          <span className="tw-badge">{p.tasks}</span>
                        </div>
                        {open && (
                          <>
                            <div
                              className="tree-row tree-child"
                              onClick={() => { onSelectProject(p.id); }}
                              style={{ paddingLeft: 44, cursor: "pointer" }}
                              title="Open kanban board"
                            >
                              <span className="tw-icon"><Icon name="layout" size={14}/></span>
                              <span className="tw-label">Board</span>
                              <span className="tw-meta">{projTasks.length}</span>
                            </div>
                            <div
                              className="tree-row tree-child"
                              onClick={() => onOpenHub && onOpenHub(p.id, "overview")}
                              style={{ paddingLeft: 44, cursor: "pointer" }}
                              title="Open Project Hub (overview, settings, steering docs, jobs…)"
                            >
                              <span className="tw-icon"><Icon name="grid" size={14}/></span>
                              <span className="tw-label">Project Hub</span>
                            </div>
                            <div
                              className="tree-row tree-child"
                              onClick={() => onOpenHub && onOpenHub(p.id, "activity")}
                              style={{ paddingLeft: 44, cursor: "pointer" }}
                              title="Project activity feed (decisions, commits, runs)"
                            >
                              <span className="tw-icon"><Icon name="activity" size={14}/></span>
                              <span className="tw-label">Activity</span>
                            </div>
                          </>
                        )}
                      </React.Fragment>
                    );
                  })}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </>
  );
}

// ============ Tasks (full outline, hierarchical by lane) ============
function TasksPanel({ tasks, activeTaskId, onOpenTask, currentProject }) {
  const { LANES, TASKS, TYPE_META } = window.MOCK;
  const allTasks = TASKS.filter(t => t.project === currentProject);

  const byState = {
    "backlog": allTasks.filter(t => t.state === "backlog"),
    "in-progress": allTasks.filter(t => t.state === "in-progress"),
    "auto-review": allTasks.filter(t => t.state === "auto-review"),
    "human-review": allTasks.filter(t => t.state === "human-review"),
  };

  const sections = [
    { key: "in-progress", label: "In Progress", icon: "play" },
    { key: "auto-review", label: "Auto Review", icon: "bot" },
    { key: "human-review", label: "Human Review", icon: "eye" },
    { key: "backlog", label: "Backlog", icon: "list" },
  ];

  const [collapsed, setCollapsed] = useState({ backlog: true });

  return (
    <>
      <div className="sb-header">
        <span>Tasks · {window.MOCK.PROJECTS.find(p => p.id === currentProject)?.name}</span>
        <div className="actions">
          <button className="ic-btn"><Icon name="filter" size={14}/></button>
          <button className="ic-btn"><Icon name="plus" size={14}/></button>
        </div>
      </div>
      <div className="sb-body">
        {sections.map(s => {
          const items = byState[s.key] || [];
          const isCol = collapsed[s.key];
          return (
            <div className="sb-section" key={s.key}>
              <div className={`sb-section-head ${isCol ? "collapsed" : ""}`} onClick={() => setCollapsed(c => ({ ...c, [s.key]: !c[s.key] }))}>
                <span className="chev"><Icon name="chevronDown" size={12}/></span>
                <span>{s.label}</span>
                <span className="count">{items.length}</span>
              </div>
              {!isCol && (
                <div className="sb-section-body">
                  {items.length === 0 && <div style={{ padding: "4px 18px", fontSize: 11, color: "var(--fg-muted)" }}>No tasks</div>}
                  {items.map(t => (
                    <div
                      key={t.id}
                      className={`outline-row ${activeTaskId === t.id ? "active" : ""}`}
                      onClick={() => onOpenTask(t.id)}
                    >
                      <span className="num">{t.num}</span>
                      <span className="ttl">{t.title}</span>
                      <span className="tag" style={{ color: TYPE_META[t.type]?.color }}>{TYPE_META[t.type]?.icon}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </>
  );
}

// ============ Filters ============
function FiltersPanel({ filters, setFilters }) {
  const { TYPE_META } = window.MOCK;
  const toggleSet = (key, val) => {
    const cur = new Set(filters[key] || []);
    if (cur.has(val)) cur.delete(val); else cur.add(val);
    setFilters({ ...filters, [key]: [...cur] });
  };
  const isOn = (key, val) => (filters[key] || []).includes(val);

  const stateOpts = [
    { k: "backlog", l: "Backlog", c: 26, color: "#9d9d9d" },
    { k: "in-progress", l: "In Progress", c: 1, color: "#4ec9b0" },
    { k: "auto-review", l: "Auto Review", c: 8, color: "#569cd6" },
    { k: "human-review", l: "Human Review", c: 108, color: "#c586c0" },
    { k: "archive", l: "Archive", c: 288, color: "#6e6e6e" },
  ];

  const cliOpts = [
    { k: "claude", l: "Claude", c: 14, color: "#d97757" },
    { k: "codex", l: "Codex", c: 5, color: "#569cd6" },
    { k: "copilot", l: "Copilot", c: 2, color: "#4ec9b0" },
    { k: "gemini", l: "Gemini", c: 0, color: "#c586c0" },
  ];

  const labelOpts = [
    { k: "review-reissue", l: "review reissue", c: 4 },
    { k: "review-escalate", l: "review escalate", c: 1 },
    { k: "reissue:autoreview", l: "reissue:autoreview", c: 3 },
    { k: "requirement:concerns", l: "requirement:concerns", c: 2 },
    { k: "quality:concerns", l: "quality:concerns", c: 2 },
    { k: "code-review:block", l: "code-review:block", c: 1 },
    { k: "missing-sentinel", l: "missing sentinel", c: 2 },
  ];

  const ownerOpts = ["All", "Me (AB)", "auto-assigned", "unassigned"];

  return (
    <>
      <div className="sb-header">
        <span>Filters</span>
        <div className="actions">
          <button className="ic-btn" title="Save view"><Icon name="plus" size={14}/></button>
          <button className="ic-btn" title="Clear" onClick={() => setFilters({})}><Icon name="close" size={14}/></button>
        </div>
      </div>
      <div className="sb-body">
        <div className="flt-search">
          <Icon name="search" size={13}/>
          <input placeholder="Search tasks, commits, files…"/>
          <span className="kbd-hint">⌘P</span>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Quick Views</span></div>
          <div className="sb-section-body">
            <div className="flt-chips">
              <button className="flt-chip active">All</button>
              <button className="flt-chip">Mine</button>
              <button className="flt-chip">⏰ Stale</button>
              <button className="flt-chip">⚠ Blocked</button>
              <button className="flt-chip">Needs review</button>
            </div>
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>State</span></div>
          <div className="sb-section-body">
            {stateOpts.map(o => (
              <div key={o.k} className="flt-row" onClick={() => toggleSet("state", o.k)}>
                <span className={`flt-check ${isOn("state", o.k) ? "on" : ""}`}>{isOn("state", o.k) && <Icon name="check" size={10}/>}</span>
                <span className="dot" style={{ width: 8, height: 8, borderRadius: "50%", background: o.color }}/>
                <span className="label">{o.l}</span>
                <span className="count">{o.c}</span>
              </div>
            ))}
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Type</span></div>
          <div className="sb-section-body">
            {["feature", "bug", "chore"].map(t => (
              <div key={t} className="flt-row" onClick={() => toggleSet("type", t)}>
                <span className={`flt-check ${isOn("type", t) ? "on" : ""}`}>{isOn("type", t) && <Icon name="check" size={10}/>}</span>
                <span style={{ color: TYPE_META[t].color, fontSize: 11 }}>{TYPE_META[t].icon}</span>
                <span className="label">{TYPE_META[t].label}</span>
              </div>
            ))}
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>CLI</span></div>
          <div className="sb-section-body">
            {cliOpts.map(o => (
              <div key={o.k} className="flt-row" onClick={() => toggleSet("cli", o.k)}>
                <span className={`flt-check ${isOn("cli", o.k) ? "on" : ""}`}>{isOn("cli", o.k) && <Icon name="check" size={10}/>}</span>
                <span className="cli-glyph sm" style={{ background: o.color }}>{o.l[0]}</span>
                <span className="label">{o.l}</span>
                <span className="count">{o.c}</span>
              </div>
            ))}
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Labels</span></div>
          <div className="sb-section-body">
            {labelOpts.map(o => (
              <div key={o.k} className="flt-row" onClick={() => toggleSet("label", o.k)}>
                <span className={`flt-check ${isOn("label", o.k) ? "on" : ""}`}>{isOn("label", o.k) && <Icon name="check" size={10}/>}</span>
                <span className="label">{o.l}</span>
                <span className="count">{o.c}</span>
              </div>
            ))}
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Owner</span></div>
          <div className="sb-section-body">
            {ownerOpts.map(o => (
              <div key={o} className="flt-row" onClick={() => setFilters({ ...filters, owner: o })}>
                <span className={`flt-check ${filters.owner === o ? "on" : ""}`}>{filters.owner === o && <Icon name="check" size={10}/>}</span>
                <span className="label">{o}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </>
  );
}

// ============ CLI Status ============
function CLIPanel() {
  const { CLIS } = window.MOCK;
  return (
    <>
      <div className="sb-header">
        <span>Agents · CLI</span>
        <div className="actions">
          <button className="ic-btn"><Icon name="refresh" size={14}/></button>
          <button className="ic-btn"><Icon name="settings" size={14}/></button>
        </div>
      </div>
      <div className="sb-body">
        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Active CLIs</span><span className="count">{CLIS.length}</span></div>
          <div className="sb-section-body">
            {CLIS.map(c => {
              const anyCrit = c.quotas.some(q => q.critical || q.used > 0.85);
              return (
                <div key={c.id} className={`cli-card ${anyCrit ? "warn" : ""}`}>
                  <div className="head">
                    <span className="cli-glyph" style={{ background: c.color }}>{c.name[0]}</span>
                    <span>{c.name}</span>
                    <span className="state-pill">{c.state}</span>
                  </div>
                  <div className="cli-quotas">
                    {c.quotas.map((q, i) => (
                      <div key={i} className={`cli-quota ${q.critical ? "critical" : ""}`}>
                        <div className="cli-quota-head">
                          <span className="cli-quota-period">{q.period}</span>
                          <span className="cli-quota-used mono">{Math.round(q.used*100)}%</span>
                          <span className="cli-quota-reset mono">↻ {q.resetsIn}</span>
                        </div>
                        <div className="bar"><div style={{ width: `${Math.max(2, Math.round(q.used*100))}%`, background: q.critical ? "var(--accent)" : c.color }}/></div>
                      </div>
                    ))}
                  </div>
                </div>
              );
            })}
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Default Model</span></div>
          <div className="sb-section-body" style={{ padding: "0 14px 8px" }}>
            <div style={{ fontSize: 11, color: "var(--fg-muted)", margin: "6px 0 4px" }}>For review</div>
            <div style={{ display: "flex", gap: 4 }}>
              <select style={{ flex: 1, background: "var(--bg-input)", color: "var(--fg)", border: "1px solid var(--border)", padding: "5px 6px", borderRadius: 3, fontSize: 12 }}>
                <option>Codex</option>
                <option>Claude</option>
                <option>Copilot</option>
                <option>Gemini</option>
              </select>
              <select style={{ flex: 1, background: "var(--bg-input)", color: "var(--fg)", border: "1px solid var(--border)", padding: "5px 6px", borderRadius: 3, fontSize: 12 }}>
                <option>gpt-5</option>
                <option>gpt-4o</option>
                <option>o4-mini</option>
              </select>
            </div>
            <div style={{ fontSize: 11, color: "var(--fg-muted)", margin: "10px 0 4px" }}>For implementation</div>
            <div style={{ display: "flex", gap: 4 }}>
              <select style={{ flex: 1, background: "var(--bg-input)", color: "var(--fg)", border: "1px solid var(--border)", padding: "5px 6px", borderRadius: 3, fontSize: 12 }}>
                <option>Claude</option>
                <option>Codex</option>
                <option>Copilot</option>
              </select>
              <select style={{ flex: 1, background: "var(--bg-input)", color: "var(--fg)", border: "1px solid var(--border)", padding: "5px 6px", borderRadius: 3, fontSize: 12 }}>
                <option>opus-4-7</option>
                <option>sonnet-4</option>
                <option>haiku-4</option>
              </select>
            </div>
          </div>
        </div>
      </div>
    </>
  );
}

// ============ Activity Feed ============
function ActivityPanel() {
  const { ACTIVITY_FEED, PROJECTS } = window.MOCK;
  const [selected, setSelected] = useState(0);
  const projectGlyph = (id) => PROJECTS.find(p => p.id === id);
  return (
    <>
      <div className="sb-header">
        <span>Activity</span>
        <div className="actions">
          <button className="ic-btn"><Icon name="filter" size={14}/></button>
          <button className="ic-btn"><Icon name="refresh" size={14}/></button>
        </div>
      </div>
      <div className="sb-body">
        <div className="flt-chips" style={{ padding: "8px 12px" }}>
          <button className="flt-chip active">All</button>
          <button className="flt-chip">Decisions</button>
          <button className="flt-chip">Commits</button>
          <button className="flt-chip">Reissues</button>
          <button className="flt-chip">Errors</button>
        </div>
        <div className="act-feed">
          {ACTIVITY_FEED.map((f, i) => {
            const p = projectGlyph(f.project);
            return (
              <button
                key={i}
                className={`act-feed-item ${f.kind} ${selected === i ? "active" : ""}`}
                onClick={() => setSelected(i)}
              >
                <span className="act-feed-time mono">{f.time}</span>
                <span className="act-feed-glyph" style={{ background: p?.color || "var(--fg-muted)" }}>{p?.short || "?"}</span>
                <span className="act-feed-text">{f.text}</span>
              </button>
            );
          })}
        </div>
      </div>
    </>
  );
}

// ============ Runbook ============
function RunbookPanel() {
  const [auto, setAuto] = useState(true);
  const [autoPush, setAutoPush] = useState(false);
  const [autoReview, setAutoReview] = useState(true);
  return (
    <>
      <div className="sb-header">
        <span>Runbook</span>
        <div className="actions">
          <button className="ic-btn"><Icon name="settings" size={14}/></button>
        </div>
      </div>
      <div className="sb-body">
        <div className="rb-card">
          <div className="row"><span className="lbl">Orchestrator</span><span style={{ display: "inline-flex", alignItems: "center", gap: 6, color: "var(--accent-2)", fontSize: 11 }}><span className="dot" style={{ width: 6, height: 6, borderRadius: "50%", background: "var(--accent-2)" }}/>running</span></div>
          <div className="row"><span className="lbl">Tick interval</span><span className="val">28s</span></div>
          <div className="row"><span className="lbl">Concurrency</span><span className="val">2 / 3 auto</span></div>
          <button className="rb-play"><Icon name="pause" size={12} color="#1a1a1a"/> Pause Auto-Run</button>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Auto Modes</span></div>
          <div className="sb-section-body" style={{ padding: "4px 14px 8px" }}>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", padding: "6px 0", fontSize: 12 }}>
              <span>Auto-Run</span>
              <span className={`toggle ${auto ? "on" : ""}`} onClick={() => setAuto(!auto)}><div/></span>
            </div>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", padding: "6px 0", fontSize: 12 }}>
              <span>Auto-Review</span>
              <span className={`toggle ${autoReview ? "on" : ""}`} onClick={() => setAutoReview(!autoReview)}><div/></span>
            </div>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", padding: "6px 0", fontSize: 12 }}>
              <span>Auto-Push <span className="muted" style={{ fontSize: 10 }}>only completed</span></span>
              <span className={`toggle ${autoPush ? "on" : ""}`} onClick={() => setAutoPush(!autoPush)}><div/></span>
            </div>
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Last Tick</span></div>
          <div className="sb-section-body" style={{ padding: "8px 14px", fontSize: 12, color: "var(--fg-dim)", lineHeight: 1.6 }}>
            <div className="mono" style={{ color: "var(--fg-muted)", fontSize: 10 }}>28s ago</div>
            <div>0 queued · 0 accept · 0 reissue · 0 escalate</div>
            <div className="muted" style={{ fontSize: 11, marginTop: 4 }}>Sentinel sweep clean.</div>
          </div>
        </div>

      </div>
    </>
  );
}

// ============ Settings ============
function SettingsPanel({ theme, onChangeTheme }) {
  return (
    <>
      <div className="sb-header">
        <span>Settings</span>
        <div className="actions">
          <button className="ic-btn"><Icon name="search" size={14}/></button>
        </div>
      </div>
      <div className="sb-body">
        <div className="flt-search">
          <Icon name="search" size={13}/>
          <input placeholder="Search settings"/>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Appearance</span></div>
          <div className="sb-section-body" style={{ padding: "8px 14px 12px" }}>
            <div style={{ fontSize: 11, color: "var(--fg-muted)", marginBottom: 6, textTransform: "uppercase", letterSpacing: "0.04em" }}>Theme</div>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 6 }}>
              <button
                onClick={() => onChangeTheme && onChangeTheme("dark")}
                style={{
                  padding: "10px 8px",
                  background: theme === "dark" ? "rgba(217,119,87,0.14)" : "var(--bg-elevated)",
                  border: `1px solid ${theme === "dark" ? "var(--accent)" : "var(--border)"}`,
                  borderRadius: 4,
                  display: "flex",
                  flexDirection: "column",
                  alignItems: "center",
                  gap: 6,
                  cursor: "pointer",
                  color: "var(--fg)",
                }}
              >
                <div style={{ width: 50, height: 32, background: "#1e1e1e", border: "1px solid #2b2b2b", borderRadius: 3, position: "relative", overflow: "hidden" }}>
                  <div style={{ position: "absolute", left: 0, top: 0, bottom: 0, width: 10, background: "#181818" }}/>
                  <div style={{ position: "absolute", left: 12, top: 4, width: 30, height: 2, background: "#cccccc", borderRadius: 1 }}/>
                  <div style={{ position: "absolute", left: 12, top: 9, width: 22, height: 2, background: "#6e6e6e", borderRadius: 1 }}/>
                  <div style={{ position: "absolute", left: 12, top: 14, width: 26, height: 2, background: "#6e6e6e", borderRadius: 1 }}/>
                  <div style={{ position: "absolute", left: 0, bottom: 0, right: 0, height: 4, background: "#d97757", opacity: 0.4 }}/>
                </div>
                <div style={{ fontSize: 11, fontWeight: theme === "dark" ? 600 : 400 }}>Dark</div>
              </button>
              <button
                onClick={() => onChangeTheme && onChangeTheme("light")}
                style={{
                  padding: "10px 8px",
                  background: theme === "light" ? "rgba(217,119,87,0.14)" : "var(--bg-elevated)",
                  border: `1px solid ${theme === "light" ? "var(--accent)" : "var(--border)"}`,
                  borderRadius: 4,
                  display: "flex",
                  flexDirection: "column",
                  alignItems: "center",
                  gap: 6,
                  cursor: "pointer",
                  color: "var(--fg)",
                }}
              >
                <div style={{ width: 50, height: 32, background: "#ffffff", border: "1px solid #e5e5e5", borderRadius: 3, position: "relative", overflow: "hidden" }}>
                  <div style={{ position: "absolute", left: 0, top: 0, bottom: 0, width: 10, background: "#f0f0f0" }}/>
                  <div style={{ position: "absolute", left: 12, top: 4, width: 30, height: 2, background: "#1a1a1a", borderRadius: 1 }}/>
                  <div style={{ position: "absolute", left: 12, top: 9, width: 22, height: 2, background: "#8a8a8a", borderRadius: 1 }}/>
                  <div style={{ position: "absolute", left: 12, top: 14, width: 26, height: 2, background: "#8a8a8a", borderRadius: 1 }}/>
                  <div style={{ position: "absolute", left: 0, bottom: 0, right: 0, height: 4, background: "#d97757", opacity: 0.4 }}/>
                </div>
                <div style={{ fontSize: 11, fontWeight: theme === "light" ? 600 : 400 }}>Light</div>
              </button>
            </div>
          </div>
        </div>

        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Workspace</span></div>
          <div className="sb-section-body">
            <div className="tree-row"><span className="tw-icon"><Icon name="folder" size={14}/></span><span className="tw-label">Projects</span></div>
            <div className="tree-row"><span className="tw-icon"><Icon name="bot" size={14}/></span><span className="tw-label">CLIs</span></div>
            <div className="tree-row"><span className="tw-icon"><Icon name="runbook" size={14}/></span><span className="tw-label">Runbook</span></div>
            <div className="tree-row"><span className="tw-icon"><Icon name="bell" size={14}/></span><span className="tw-label">Notifications</span></div>
          </div>
        </div>
        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Editor</span></div>
          <div className="sb-section-body">
            <div className="tree-row"><span className="tw-icon"><Icon name="sliders" size={14}/></span><span className="tw-label">Appearance</span></div>
            <div className="tree-row"><span className="tw-icon"><Icon name="layout" size={14}/></span><span className="tw-label">Layout</span></div>
            <div className="tree-row"><span className="tw-icon"><Icon name="grid" size={14}/></span><span className="tw-label">Kanban</span></div>
          </div>
        </div>
        <div className="sb-section">
          <div className="sb-section-head"><span className="chev"><Icon name="chevronDown" size={12}/></span><span>Auto-Run Policy</span></div>
          <div className="sb-section-body" style={{ padding: "6px 14px 12px", fontSize: 12 }}>
            <div style={{ color: "var(--fg-dim)", marginBottom: 4 }}>Concurrency cap</div>
            <input type="range" min="1" max="6" defaultValue="3" style={{ width: "100%" }}/>
            <div style={{ color: "var(--fg-dim)", marginTop: 10, marginBottom: 4 }}>Idle timeout</div>
            <input type="text" defaultValue="120s" style={{ width: "100%", background: "var(--bg-input)", color: "var(--fg)", border: "1px solid var(--border)", padding: "4px 6px", borderRadius: 3, fontSize: 12 }}/>
          </div>
        </div>
      </div>
    </>
  );
}

window.Sidebar = { ProjectsPanel, TasksPanel, FiltersPanel, CLIPanel, ActivityPanel, RunbookPanel, SettingsPanel };
