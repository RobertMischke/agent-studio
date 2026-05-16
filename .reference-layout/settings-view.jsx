/* eslint-disable */
// Settings — full-page settings editor as its own tab

const { useState: useStateS } = React;

const SETTINGS_CATEGORIES = [
  { group: "User", items: [
    { id: "appearance", label: "Appearance", icon: "sliders" },
    { id: "keymap", label: "Keymap & shortcuts", icon: "cli" },
    { id: "notifications", label: "Notifications", icon: "bell" },
    { id: "account", label: "Account", icon: "settings" },
  ]},
  { group: "Workspace", items: [
    { id: "workspace", label: "General", icon: "folder" },
    { id: "projects", label: "Projects", icon: "grid", badge: null },
    { id: "git", label: "Git & Branches", icon: "branch" },
  ]},
  { group: "Agents", items: [
    { id: "clis", label: "CLIs & Models", icon: "bot" },
    { id: "routing", label: "Routing rules", icon: "send" },
    { id: "autorun", label: "Auto-Run policy", icon: "runbook" },
    { id: "review", label: "Review gates", icon: "check" },
  ]},
  { group: "Integrations", items: [
    { id: "github", label: "GitHub", icon: "branch" },
    { id: "slack", label: "Slack", icon: "send" },
    { id: "linear", label: "Linear", icon: "list" },
  ]},
];

// ============ Generic settings primitives ============

function SRow({ title, desc, control, children }) {
  return (
    <div className="settings-row">
      <div className="settings-row-text">
        <div className="settings-row-title">{title}</div>
        {desc && <div className="settings-row-desc">{desc}</div>}
      </div>
      <div className="settings-row-control">{control || children}</div>
    </div>
  );
}

function SToggle({ value, onChange }) {
  return <span className={`toggle ${value ? "on" : ""}`} onClick={() => onChange && onChange(!value)}><div/></span>;
}

function SInput({ value, onChange, placeholder, mono, type = "text", style }) {
  return <input type={type} className={mono ? "mono" : ""} value={value || ""} onChange={(e) => onChange && onChange(e.target.value)} placeholder={placeholder} style={style}/>;
}

function SSelect({ value, onChange, options }) {
  return (
    <select value={value} onChange={(e) => onChange && onChange(e.target.value)}>
      {options.map(o => <option key={typeof o === "object" ? o.value : o} value={typeof o === "object" ? o.value : o}>{typeof o === "object" ? o.label : o}</option>)}
    </select>
  );
}

function SSegmented({ value, onChange, options }) {
  return (
    <div className="settings-segmented">
      {options.map(o => {
        const v = typeof o === "object" ? o.value : o;
        const l = typeof o === "object" ? o.label : o;
        return (
          <button key={v} className={v === value ? "active" : ""} onClick={() => onChange && onChange(v)}>{l}</button>
        );
      })}
    </div>
  );
}

function SSection({ title, desc, children }) {
  return (
    <div className="settings-section">
      <h3 className="settings-section-title">{title}</h3>
      {desc && <p className="settings-section-desc">{desc}</p>}
      <div className="settings-section-body">{children}</div>
    </div>
  );
}

// ============ Category pages ============

function AppearancePage({ tweaks, setTweak }) {
  return (
    <>
      <SSection title="Theme" desc="Switch the surface between dark and light. The host persists your choice.">
        <div style={{ display: "flex", gap: 12 }}>
          {[
            { id: "dark", name: "Dark", bg: "#1e1e1e", sb: "#181818", fg: "#cccccc", border: "#2b2b2b" },
            { id: "light", name: "Light", bg: "#ffffff", sb: "#f0f0f0", fg: "#3b3b3b", border: "#e5e5e5" },
          ].map(t => (
            <button
              key={t.id}
              onClick={() => setTweak({ theme: t.id })}
              className={`theme-card ${tweaks.theme === t.id ? "active" : ""}`}
            >
              <div className="theme-card-preview" style={{ background: t.bg, border: `1px solid ${t.border}` }}>
                <div style={{ position: "absolute", left: 0, top: 0, bottom: 0, width: 18, background: t.sb }}/>
                <div style={{ position: "absolute", left: 22, top: 8, width: 60, height: 3, background: t.fg, opacity: 0.85, borderRadius: 1 }}/>
                <div style={{ position: "absolute", left: 22, top: 14, width: 44, height: 3, background: t.fg, opacity: 0.5, borderRadius: 1 }}/>
                <div style={{ position: "absolute", left: 22, top: 20, width: 52, height: 3, background: t.fg, opacity: 0.5, borderRadius: 1 }}/>
                <div style={{ position: "absolute", left: 0, bottom: 0, right: 0, height: 6, background: "#d97757", opacity: 0.5 }}/>
              </div>
              <div className="theme-card-name">{t.name}</div>
            </button>
          ))}
        </div>
      </SSection>

      <SSection title="Density & layout">
        <SRow title="Card density" desc="Kanban cards. Dense fits more rows on screen.">
          <SSegmented value={tweaks.cardDensity || "normal"} options={["dense", "normal", "spacious"]} onChange={(v) => setTweak({ cardDensity: v })}/>
        </SRow>
        <SRow title="Activity bar position" desc="Where the icon column sits.">
          <SSegmented value={tweaks.activityBarPosition || "left"} options={["left", "right"]} onChange={(v) => setTweak({ activityBarPosition: v })}/>
        </SRow>
        <SRow title="Detail layout default" desc="How the task detail splits when opened.">
          <SSegmented value={tweaks.detailLayout || "triple"} options={["focus", "split", "triple"]} onChange={(v) => setTweak({ detailLayout: v })}/>
        </SRow>
        <SRow title="Sidebar width" desc="Default width of the left panel.">
          <input type="range" min="200" max="500" value={tweaks.sidebarWidth || 280} onChange={(e) => setTweak({ sidebarWidth: +e.target.value })}/>
          <span className="mono muted" style={{ marginLeft: 8, fontSize: 11 }}>{tweaks.sidebarWidth || 280}px</span>
        </SRow>
      </SSection>

      <SSection title="Type">
        <SRow title="UI font" desc="Used everywhere except code.">
          <SSelect value="Inter" options={["Inter", "System UI", "IBM Plex Sans", "JetBrains Sans"]}/>
        </SRow>
        <SRow title="Code font" desc="Used in commit diffs, hashes, file paths.">
          <SSelect value="JetBrains Mono" options={["JetBrains Mono", "Fira Code", "SF Mono", "Consolas"]}/>
        </SRow>
        <SRow title="UI scale" desc="Affects body text size and chrome density.">
          <SSegmented value="100" options={[{value:"90",label:"90%"},{value:"100",label:"100%"},{value:"110",label:"110%"},{value:"125",label:"125%"}]}/>
        </SRow>
      </SSection>

      <SSection title="Accent">
        <SRow title="Brand accent color" desc="Used for active states, primary actions, brand glyph.">
          <div style={{ display: "flex", gap: 8 }}>
            {["#d97757", "#569cd6", "#4ec9b0", "#c586c0", "#cca700"].map(c => (
              <span key={c} className="swatch" style={{ background: c, border: c === "#d97757" ? "2px solid var(--fg-strong)" : "1px solid var(--border)" }}/>
            ))}
          </div>
        </SRow>
      </SSection>
    </>
  );
}

function NotificationsPage() {
  const [s, setS] = useStateS({
    osNotifs: true,
    sound: false,
    autoReviewBlock: true,
    autoReviewAccept: false,
    humanReviewIdle: true,
    reissue: true,
    quotaLow: true,
    quotaExceeded: true,
    push: false,
    branchPush: false,
    quietHours: true,
    quietFrom: "22:00",
    quietTo: "08:00",
    channelSystem: true,
    channelEmail: false,
    channelSlack: false,
    digest: "weekly",
  });
  const upd = (k, v) => setS(prev => ({ ...prev, [k]: v }));

  return (
    <>
      <SSection title="Channels">
        <SRow title="System notifications" desc="Native OS notification when something needs your attention.">
          <SToggle value={s.osNotifs} onChange={(v) => upd("osNotifs", v)}/>
        </SRow>
        <SRow title="Sound" desc="Play a chime alongside notifications.">
          <SToggle value={s.sound} onChange={(v) => upd("sound", v)}/>
        </SRow>
        <SRow title="In-app notification center" desc="Always available via the bell icon.">
          <SToggle value={s.channelSystem} onChange={(v) => upd("channelSystem", v)}/>
        </SRow>
        <SRow title="Email" desc="Daily and on-event email to the workspace owner.">
          <SToggle value={s.channelEmail} onChange={(v) => upd("channelEmail", v)}/>
        </SRow>
        <SRow title="Slack webhook" desc="Post to a channel via incoming webhook.">
          <SInput placeholder="https://hooks.slack.com/services/…" style={{ width: 280 }}/>
        </SRow>
      </SSection>

      <SSection title="Events" desc="Pick which orchestrator events trigger a notification.">
        <SRow title="Auto-review BLOCKED" desc="An aspect blocked acceptance; agent reissued."><SToggle value={s.autoReviewBlock} onChange={(v) => upd("autoReviewBlock", v)}/></SRow>
        <SRow title="Auto-review ACCEPTED" desc="Pull a notification on each accepted job."><SToggle value={s.autoReviewAccept} onChange={(v) => upd("autoReviewAccept", v)}/></SRow>
        <SRow title="Awaiting human review for &gt; 1h" desc="Job sat in human-review queue too long."><SToggle value={s.humanReviewIdle} onChange={(v) => upd("humanReviewIdle", v)}/></SRow>
        <SRow title="Reissue (auto)" desc="Orchestrator reissued a job autonomously."><SToggle value={s.reissue} onChange={(v) => upd("reissue", v)}/></SRow>
        <SRow title="Quota approaching limit (&lt;15%)" desc="Heads-up before a CLI throttles."><SToggle value={s.quotaLow} onChange={(v) => upd("quotaLow", v)}/></SRow>
        <SRow title="Quota exhausted" desc="A CLI hit its cap and is now paused."><SToggle value={s.quotaExceeded} onChange={(v) => upd("quotaExceeded", v)}/></SRow>
        <SRow title="Branch push" desc="Agent pushed a commit to remote."><SToggle value={s.branchPush} onChange={(v) => upd("branchPush", v)}/></SRow>
      </SSection>

      <SSection title="Quiet hours" desc="No system notifications during this window. The notification center still records everything.">
        <SRow title="Enable quiet hours"><SToggle value={s.quietHours} onChange={(v) => upd("quietHours", v)}/></SRow>
        <SRow title="From / to">
          <div style={{ display: "flex", gap: 8 }}>
            <SInput value={s.quietFrom} onChange={(v) => upd("quietFrom", v)} style={{ width: 80 }} mono/>
            <span style={{ color: "var(--fg-muted)", lineHeight: "30px" }}>→</span>
            <SInput value={s.quietTo} onChange={(v) => upd("quietTo", v)} style={{ width: 80 }} mono/>
          </div>
        </SRow>
      </SSection>

      <SSection title="Digest" desc="Periodic summary of what your agents did.">
        <SRow title="Frequency">
          <SSegmented value={s.digest} options={["off", "daily", "weekly"]} onChange={(v) => upd("digest", v)}/>
        </SRow>
      </SSection>
    </>
  );
}

function WorkspacePage({ workspace }) {
  return (
    <>
      <SSection title="Identity">
        <SRow title="Name" desc="Display name shown in the workspace picker.">
          <SInput value={workspace?.name || ""} style={{ width: 280 }}/>
        </SRow>
        <SRow title="Path" desc="Where this workspace lives on disk.">
          <SInput value={workspace?.path || ""} mono style={{ width: 380 }}/>
        </SRow>
      </SSection>

      <SSection title="Defaults" desc="Used when a new project is added to the workspace.">
        <SRow title="Default branch" desc="Used for new project clones."><SInput value="main" style={{ width: 160 }} mono/></SRow>
        <SRow title="Agent task contract">
          <SSelect value="strict" options={[{value:"strict",label:"Strict (default)"},{value:"loose",label:"Loose"},{value:"per-project",label:"Per-project override"}]}/>
        </SRow>
        <SRow title="Auto-load on launch" desc="Open all projects in this workspace at startup.">
          <SToggle value={true}/>
        </SRow>
      </SSection>

      <SSection title="Storage">
        <SRow title="Local cache" desc="Build artefacts, agent transcripts, evidence captures.">
          <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
            <span className="mono muted" style={{ fontSize: 11 }}>~/Library/Caches/agent-studio · 1.4 GB</span>
            <button className="btn-ghost">Clear cache</button>
          </div>
        </SRow>
        <SRow title="Archived job retention" desc="How long completed jobs stay browsable.">
          <SSelect value="90d" options={["30d", "90d", "1y", "forever"]}/>
        </SRow>
      </SSection>

      <SSection title="Danger zone">
        <SRow title="Remove workspace" desc="Removes from the picker. Files on disk are not touched.">
          <button className="btn-ghost" style={{ color: "var(--accent-6)", borderColor: "var(--accent-6)" }}>Remove workspace</button>
        </SRow>
      </SSection>
    </>
  );
}

function ProjectsPage({ wsProjects, onOpenHub }) {
  return (
    <>
      <SSection title="Projects in this workspace" desc="Each project is a Git repo with its own settings. Open the hub for the full per-project surface.">
        <div className="settings-projects-list">
          {wsProjects.map(p => (
            <div key={p.id} className="settings-project-row">
              <span className="settings-project-glyph" style={{ background: p.color }}>{p.short}</span>
              <div className="settings-project-text">
                <div className="settings-project-name">{p.name}</div>
                <div className="settings-project-meta mono">
                  ~/work/{p.id} · <Icon name="branch" size={9}/> {p.branch} · {p.tasks} open
                </div>
              </div>
              <div className="settings-project-actions">
                <button className="btn-ghost" onClick={() => onOpenHub && onOpenHub(p.id, "settings")}><Icon name="settings" size={11}/> Settings</button>
                <button className="btn-ghost" onClick={() => onOpenHub && onOpenHub(p.id)}>Open hub</button>
              </div>
            </div>
          ))}
        </div>
        <button className="btn-ghost" style={{ marginTop: 12 }}><Icon name="plus" size={11}/> Add existing repo…</button>
      </SSection>
    </>
  );
}

function CLIsPage() {
  const { CLIS } = window.MOCK;
  return (
    <>
      <SSection title="Installed CLIs" desc="Local command-line agents the orchestrator can invoke. Set a default model and authentication per CLI.">
        {CLIS.map(c => (
          <div key={c.id} className="settings-cli-card">
            <div className="settings-cli-head">
              <span className="cli-glyph" style={{ background: c.color }}>{c.name[0]}</span>
              <div className="settings-cli-text">
                <div className="settings-cli-name">{c.name}</div>
                <div className="settings-cli-bin mono">/usr/local/bin/{c.id}</div>
              </div>
              <span className="tg review-accept">authorized</span>
            </div>
            <div className="settings-cli-body">
              <SRow title="Default model">
                <SSelect value="opus-4-7" options={c.id === "claude" ? ["opus-4-7","sonnet-4","haiku-4"] : c.id === "codex" ? ["gpt-5","gpt-4o","o4-mini"] : c.id === "copilot" ? ["copilot-default"] : ["gemini-2.5-pro","gemini-2.5-flash"]}/>
              </SRow>
              <SRow title="Auth">
                <div style={{ display: "flex", gap: 6, alignItems: "center" }}>
                  <SInput value="••••••••••••••••••••2j7Q" mono style={{ width: 220 }}/>
                  <button className="btn-ghost">Rotate</button>
                </div>
              </SRow>
              <SRow title="Concurrency cap" desc="Max parallel jobs this CLI accepts.">
                <SSelect value="2" options={["1","2","3","4"]}/>
              </SRow>
              <SRow title="Auto-pause on quota &lt;5%"><SToggle value={true}/></SRow>
              <SRow title="Allow on" desc="Which lanes this CLI may be auto-assigned to.">
                <div style={{ display: "flex", gap: 6 }}>
                  <span className="tg label">auto-review</span>
                  <span className="tg feature">implementation</span>
                  <button className="btn-ghost" style={{ padding: "1px 6px", fontSize: 10 }}>+</button>
                </div>
              </SRow>
            </div>
          </div>
        ))}
        <button className="btn-ghost" style={{ marginTop: 12 }}><Icon name="plus" size={11}/> Add CLI…</button>
      </SSection>
    </>
  );
}

function RoutingPage() {
  const [rules] = useStateS([
    { when: "type:bug", route: "Codex (gpt-5)" },
    { when: "type:feature AND files>20", route: "Claude (opus-4-7)" },
    { when: "lane:auto-review", route: "Codex (gpt-5)" },
    { when: "label:docs", route: "Claude (sonnet-4)" },
  ]);
  return (
    <>
      <SSection title="Default routing">
        <SRow title="Implementation" desc="Fallback CLI when no rule matches.">
          <SSelect value="claude-opus-4-7" options={[{value:"claude-opus-4-7",label:"Claude Opus 4.7"},{value:"codex",label:"Codex GPT-5"},{value:"copilot",label:"Copilot"}]}/>
        </SRow>
        <SRow title="Auto-review" desc="Fallback reviewer.">
          <SSelect value="codex" options={[{value:"codex",label:"Codex GPT-5"},{value:"claude",label:"Claude Sonnet 4"},{value:"gemini",label:"Gemini 2.5"}]}/>
        </SRow>
      </SSection>
      <SSection title="Routing rules" desc="First matching rule wins. Drag to reorder.">
        <div className="settings-rules">
          {rules.map((r, i) => (
            <div key={i} className="settings-rule">
              <span className="settings-rule-handle" title="Drag">⋮⋮</span>
              <span className="mono settings-rule-when">when</span>
              <SInput value={r.when} mono style={{ flex: 1, minWidth: 200 }}/>
              <span className="mono settings-rule-then">route to</span>
              <SInput value={r.route} style={{ minWidth: 200 }}/>
              <button className="ic-btn" title="Remove"><Icon name="close" size={11}/></button>
            </div>
          ))}
          <button className="btn-ghost"><Icon name="plus" size={11}/> Add rule</button>
        </div>
      </SSection>
    </>
  );
}

function AutoRunPage() {
  return (
    <>
      <SSection title="Auto-Run">
        <SRow title="Concurrency cap" desc="Maximum jobs running in parallel across all CLIs.">
          <input type="range" min="1" max="8" defaultValue="3"/>
          <span className="mono muted" style={{ marginLeft: 8 }}>3</span>
        </SRow>
        <SRow title="Tick interval" desc="How often the orchestrator surveys the board.">
          <SSelect value="30" options={[{value:"10",label:"10s"},{value:"30",label:"30s"},{value:"60",label:"1m"},{value:"120",label:"2m"}]}/>
        </SRow>
        <SRow title="Idle timeout" desc="Mark a running job stale after no activity.">
          <SInput value="120s" mono style={{ width: 90 }}/>
        </SRow>
        <SRow title="Retry on transient failure"><SToggle value={true}/></SRow>
        <SRow title="Backoff" desc="Wait between retries.">
          <SSegmented value="exp" options={[{value:"linear",label:"Linear"},{value:"exp",label:"Exponential"}]}/>
        </SRow>
      </SSection>
      <SSection title="Auto-push">
        <SRow title="Auto-push commits" desc="When agents commit, push to remote automatically.">
          <SToggle value={false}/>
        </SRow>
        <SRow title="Only push completed-lane work" desc="Skip auto-review and in-progress.">
          <SToggle value={true}/>
        </SRow>
        <SRow title="Branch naming" desc="Template for agent-created branches.">
          <SInput value="agent/{type}/{slug}-{n}" mono style={{ width: 280 }}/>
        </SRow>
      </SSection>
      <SSection title="Sentinels" desc="Required sentinels before a job can move from auto-review to human-review.">
        <SRow title="requirement-fit"><SToggle value={true}/></SRow>
        <SRow title="code-quality"><SToggle value={true}/></SRow>
        <SRow title="documentation-impact"><SToggle value={true}/></SRow>
        <SRow title="tests-and-evidence"><SToggle value={true}/></SRow>
      </SSection>
    </>
  );
}

function ReviewGatesPage() {
  return (
    <>
      <SSection title="Aspect verdicts" desc="When all aspects accept, the job moves out of auto-review. block forces reissue; concerns is acceptable but logged.">
        <div className="settings-aspect-grid">
          {[
            ["requirement-fit", "Does the change deliver what the task asked for?"],
            ["code-quality", "Idiomatic, clean, follows project conventions."],
            ["documentation-impact", "Docs and changelog updated when needed."],
            ["tests-and-evidence", "Verified with tests or evidence (screenshots, logs)."],
          ].map(([k, d]) => (
            <div key={k} className="settings-aspect">
              <div className="settings-aspect-head">
                <span className="mono settings-aspect-key">{k}</span>
                <SSegmented value="strict" options={["off", "soft", "strict"]}/>
              </div>
              <div className="settings-aspect-desc">{d}</div>
            </div>
          ))}
        </div>
      </SSection>
      <SSection title="Reissue policy">
        <SRow title="Max reissues per job" desc="After this many, the job escalates to a human."><SSelect value="3" options={["1","2","3","5","∞"]}/></SRow>
        <SRow title="Escalation route" desc="Where escalated jobs land."><SSelect value="human-review" options={["human-review","archive","new-task"]}/></SRow>
      </SSection>
    </>
  );
}

function PlaceholderPage({ id, label }) {
  return (
    <div className="hub-empty">
      <Icon name="layout" size={28} color="var(--fg-muted)"/>
      <div className="hub-empty-title">{label}</div>
      <div className="hub-empty-desc">Settings for {label.toLowerCase()}. Coming up next.</div>
    </div>
  );
}

// ============ Main view ============

function SettingsView({ tweaks, setTweak, activeWorkspace, wsProjects, onOpenHub, initialCategory = "appearance" }) {
  const [category, setCategory] = useStateS(initialCategory);
  const [navW, setNavW] = useStateS(240);
  const [search, setSearch] = useStateS("");

  const ws = (window.MOCK.WORKSPACES || []).find(w => w.id === activeWorkspace);

  const startResize = (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = navW;
    const onMove = (ev) => {
      const dx = ev.clientX - startX;
      setNavW(Math.max(180, Math.min(360, startW + dx)));
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

  const renderPage = () => {
    switch (category) {
      case "appearance": return <AppearancePage tweaks={tweaks} setTweak={setTweak}/>;
      case "notifications": return <NotificationsPage/>;
      case "workspace": return <WorkspacePage workspace={ws}/>;
      case "projects": return <ProjectsPage wsProjects={wsProjects} onOpenHub={onOpenHub}/>;
      case "clis": return <CLIsPage/>;
      case "routing": return <RoutingPage/>;
      case "autorun": return <AutoRunPage/>;
      case "review": return <ReviewGatesPage/>;
      default: return <PlaceholderPage id={category} label={SETTINGS_CATEGORIES.flatMap(g => g.items).find(i => i.id === category)?.label || category}/>;
    }
  };

  const currentLabel = SETTINGS_CATEGORIES.flatMap(g => g.items).find(i => i.id === category)?.label || category;

  // Filter nav by search
  const matches = (label) => !search || label.toLowerCase().includes(search.toLowerCase());

  return (
    <div className="settings-view">
      <div className="settings-nav" style={{ width: navW }}>
        <div className="settings-search">
          <Icon name="search" size={12}/>
          <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search settings…"/>
        </div>
        {SETTINGS_CATEGORIES.map(group => {
          const visibleItems = group.items.filter(it => matches(it.label));
          if (!visibleItems.length) return null;
          return (
            <div key={group.group} className="settings-nav-group">
              <div className="settings-nav-group-head">{group.group}</div>
              {visibleItems.map(it => (
                <button
                  key={it.id}
                  className={`settings-nav-row ${category === it.id ? "active" : ""}`}
                  onClick={() => setCategory(it.id)}
                >
                  <span className="settings-nav-icon"><Icon name={it.icon} size={13}/></span>
                  <span>{it.label}</span>
                </button>
              ))}
            </div>
          );
        })}
      </div>
      <div className="settings-resize" onMouseDown={startResize}/>
      <div className="settings-pane">
        <div className="settings-header">
          <h2 className="settings-title">{currentLabel}</h2>
          <span className="spc"/>
          <span className="mono muted" style={{ fontSize: 11 }}>auto-saved</span>
        </div>
        <div className="settings-content">
          {renderPage()}
        </div>
      </div>
    </div>
  );
}

window.SettingsView = SettingsView;
