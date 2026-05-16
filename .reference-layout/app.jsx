/* eslint-disable */
// Agent Software Studio — VS-Code-inspired shell

const TWEAK_DEFAULTS = /*EDITMODE-BEGIN*/{
  "sidebarWidth": 280,
  "activityBarPosition": "left",
  "detailPanes": { "description": true, "chat": true, "commit": false },
  "cardDensity": "normal",
  "theme": "light",
  "chatOpen": true,
  "chatWidth": 340
}/*EDITMODE-END*/;

const { useState, useEffect, useRef } = React;

function App() {
  const [tweaks, setTweak] = window.useTweaks
    ? window.useTweaks(TWEAK_DEFAULTS)
    : [TWEAK_DEFAULTS, () => {}];

  useEffect(() => {
    document.documentElement.dataset.theme = tweaks.theme || "dark";
  }, [tweaks.theme]);

  const [activityPanel, setActivityPanel] = useState("projects"); // projects|tasks|filters|cli|activity|runbook|settings
  const [sidebarVisible, setSidebarVisible] = useState(true);
  const [activeWorkspace, setActiveWorkspace] = useState(window.MOCK.WORKSPACES[0].id);
  const [activeProject, setActiveProject] = useState("agent-task-processor");
  const [filters, setFilters] = useState({});

  // Tabs (open editor items) + active tab
  // Each item: { kind: 'board'|'task'|'diff'|'hub'|'activity', projectId?, id?, commit?, taskId? }
  const [openItems, setOpenItems] = useState([
    { kind: "board", projectId: "agent-task-processor" },
    { kind: "task", id: 1 },
  ]);
  const [activeKey, setActiveKey] = useState("board:agent-task-processor");

  const keyOf = (it) =>
    it.kind === "task" ? `task:${it.id}` :
    it.kind === "diff" ? `diff:${it.commit}` :
    it.kind === "hub" ? `hub:${it.projectId}` :
    it.kind === "activity" ? `activity:${it.taskId}` :
    it.kind === "board" ? `board:${it.projectId}` :
    "?";

  const sidebarWidth = tweaks.sidebarWidth || 280;
  const chatWidth = tweaks.chatWidth || 340;
  const chatOpen = tweaks.chatOpen !== false;

  const openBoard = (projectId) => {
    const key = `board:${projectId}`;
    setOpenItems(prev => prev.some(x => x.kind === "board" && x.projectId === projectId) ? prev : [...prev, { kind: "board", projectId }]);
    setActiveKey(key);
    if (projectId !== "__all__") setActiveProject(projectId);
    else setActiveProject("__all__");
  };
  const openTask = (id) => {
    setOpenItems(prev => prev.some(x => x.kind === "task" && x.id === id) ? prev : [...prev, { kind: "task", id }]);
    setActiveKey(`task:${id}`);
  };
  const openDiff = (commit, file) => {
    setOpenItems(prev => prev.some(x => x.kind === "diff" && x.commit === commit) ? prev : [...prev, { kind: "diff", commit, file }]);
    setActiveKey(`diff:${commit}`);
  };
  const openActivity = (taskId) => {
    setOpenItems(prev => prev.some(x => x.kind === "activity" && x.taskId === taskId) ? prev : [...prev, { kind: "activity", taskId }]);
    setActiveKey(`activity:${taskId}`);
  };
  const openHub = (projectId, section) => {
    setOpenItems(prev => prev.some(x => x.kind === "hub" && x.projectId === projectId) ? prev : [...prev, { kind: "hub", projectId, section }]);
    setActiveKey(`hub:${projectId}`);
    setActiveProject(projectId);
  };
  const closeTab = (key, e) => {
    e?.stopPropagation();
    const remaining = openItems.filter(x => keyOf(x) !== key);
    setOpenItems(remaining);
    if (activeKey === key) {
      setActiveKey(remaining.length ? keyOf(remaining[remaining.length - 1]) : null);
    }
  };

  // Tab context-menu actions
  const closeAllTabs = () => {
    setOpenItems([]);
    setActiveKey(null);
  };
  const closeOtherTabs = (keepKey) => {
    const keep = openItems.find(x => keyOf(x) === keepKey);
    setOpenItems(keep ? [keep] : []);
    setActiveKey(keepKey);
  };
  const closeTabsToRight = (anchorKey) => {
    const idx = openItems.findIndex(x => keyOf(x) === anchorKey);
    if (idx < 0) return;
    const next = openItems.slice(0, idx + 1);
    setOpenItems(next);
    if (!next.some(x => keyOf(x) === activeKey)) setActiveKey(anchorKey);
  };
  const closeTabsToLeft = (anchorKey) => {
    const idx = openItems.findIndex(x => keyOf(x) === anchorKey);
    if (idx < 0) return;
    const next = openItems.slice(idx);
    setOpenItems(next);
    if (!next.some(x => keyOf(x) === activeKey)) setActiveKey(anchorKey);
  };

  // Active item lookup
  const activeItem = openItems.find(x => keyOf(x) === activeKey) || null;
  const activeTaskId = activeItem?.kind === "task" ? activeItem.id : null;

  // Sidebar resize
  const dragRef = useRef(null);
  const startResize = (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = sidebarWidth;
    const onMove = (ev) => {
      const dx = (tweaks.activityBarPosition === "right" ? -1 : 1) * (ev.clientX - startX);
      setTweak({ sidebarWidth: Math.max(200, Math.min(560, startW + dx)) });
    };
    const onUp = () => { window.removeEventListener("mousemove", onMove); window.removeEventListener("mouseup", onUp); };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  // Chat resize (drag the chat-panel's left edge)
  const startChatResize = (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = chatWidth;
    const onMove = (ev) => {
      // Drag right = shrink chat (since chat is on right side), unless activity bar is on right
      const dir = tweaks.activityBarPosition === "right" ? 1 : -1;
      setTweak({ chatWidth: Math.max(260, Math.min(640, startW + dir * (ev.clientX - startX))) });
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

  const activityItems = [
    { key: "projects", icon: "folder", label: "Explorer", badge: window.MOCK.PROJECTS.length },
    { key: "tasks", icon: "list", label: "Tasks" },
    { key: "filters", icon: "filter", label: "Filters", badge: Object.values(filters).flat().length || null },
    { key: "cli", icon: "bot", label: "Agents / CLI" },
    { key: "activity", icon: "activity", label: "Activity", badge: 7 },
    { key: "runbook", icon: "runbook", label: "Runbook" },
  ];

  const renderSidebar = () => {
    switch (activityPanel) {
      case "projects":
        return <window.Sidebar.ProjectsPanel
          activeProject={activeProject}
          activeWorkspace={activeWorkspace}
          wsProjects={wsProjects}
          onSelectProject={(pid) => {
            openBoard(pid);
          }}
          onSelectWorkspace={setActiveWorkspace}
          onOpenHub={openHub}
          tasks={openItems.filter(it => it.kind === "task").map(it => window.MOCK.TASKS.find(t => t.id === it.id)).filter(Boolean)}
          onOpenTask={openTask}
          activeTaskId={activeTaskId}
        />;
      case "tasks":
        return <window.Sidebar.TasksPanel
          tasks={projectTasks}
          activeTaskId={activeTaskId}
          onOpenTask={openTask}
          currentProject={activeProject}
        />;
      case "filters":
        return <window.Sidebar.FiltersPanel filters={filters} setFilters={setFilters}/>;
      case "cli":
        return <window.Sidebar.CLIPanel/>;
      case "activity":
        return <window.Sidebar.ActivityPanel/>;
      case "runbook":
        return <window.Sidebar.RunbookPanel/>;
      case "settings":
        return <window.Sidebar.SettingsPanel
          theme={tweaks.theme}
          onChangeTheme={(v) => setTweak({ theme: v })}
        />;
      default:
        return null;
    }
  };

  const bodyCls = ["body"];
  if (tweaks.activityBarPosition === "right") bodyCls.push("activity-right");
  if (!sidebarVisible) bodyCls.push("sidebar-hidden");

  const handleActivity = (k) => {
    if (k === activityPanel && sidebarVisible) {
      setSidebarVisible(false);
    } else {
      setActivityPanel(k);
      setSidebarVisible(true);
    }
  };

  // Workspace-scoped project list
  const wsProjects = window.MOCK.PROJECTS.filter(p => p.workspace === activeWorkspace);

  // When switching workspaces, jump to that workspace's first project if current isn't part of it
  useEffect(() => {
    if (activeProject === "__all__") return;
    const ok = wsProjects.some(p => p.id === activeProject);
    if (!ok && wsProjects.length) setActiveProject(wsProjects[0].id);
  }, [activeWorkspace]);

  // Derive convenient values for Main
  const view = activeItem == null ? "welcome"
    : activeItem.kind === "board" ? "kanban"
    : activeItem.kind === "diff" ? "diff"
    : activeItem.kind === "hub" ? "hub"
    : activeItem.kind === "activity" ? "activity"
    : "detail";
  const boardProjectId = activeItem?.kind === "board" ? activeItem.projectId : activeProject;
  const activeTask = activeTaskId != null ? window.MOCK.TASKS.find(x => x.id === activeTaskId) : null;
  // Tasks displayed in the kanban — driven by boardProjectId
  const wsProjectIds = new Set(wsProjects.map(p => p.id));
  const projectTasks = boardProjectId === "__all__"
    ? window.MOCK.TASKS.filter(t => wsProjectIds.has(t.project))
    : window.MOCK.TASKS.filter(x => x.project === boardProjectId);

  return (
    <div className="app">
      <Titlebar
        activeProject={activeProject}
        setActiveProject={(pid) => { openBoard(pid); }}
        activeWorkspace={activeWorkspace}
        setActiveWorkspace={setActiveWorkspace}
        wsProjects={wsProjects}
        view={view}
        chatOpen={chatOpen}
        chatUnread={3}
        onToggleChat={() => setTweak({ chatOpen: !chatOpen })}
        theme={tweaks.theme}
        onToggleTheme={() => setTweak({ theme: tweaks.theme === "light" ? "dark" : "light" })}
        onOpenHub={openHub}
      />
      <div className={bodyCls.join(" ")} style={{ gridTemplateColumns: tweaks.activityBarPosition === "right"
          ? `${sidebarVisible ? sidebarWidth + "px" : ""} 1fr ${chatOpen ? chatWidth + "px" : "36px"} var(--activitybar-w)`
          : `var(--activitybar-w) ${sidebarVisible ? sidebarWidth + "px" : ""} 1fr ${chatOpen ? chatWidth + "px" : "36px"}` }}>
        {tweaks.activityBarPosition !== "right" && (
          <ActivityBar items={activityItems} active={activityPanel} onClick={handleActivity} onSettings={() => handleActivity("settings")} settingsActive={activityPanel === "settings"}/>
        )}
        {sidebarVisible && (
          <div className="sidebar" style={{ width: sidebarWidth }}>
            {renderSidebar()}
            <div className="sidebar-resize" onMouseDown={startResize}/>
          </div>
        )}
        <Main
          view={view}
          openItems={openItems}
          activeKey={activeKey}
          keyOf={keyOf}
          onSelectTab={setActiveKey}
          onCloseTab={closeTab}
          onCloseAllTabs={closeAllTabs}
          onCloseOtherTabs={closeOtherTabs}
          onCloseTabsToRight={closeTabsToRight}
          onCloseTabsToLeft={closeTabsToLeft}
          onOpenKanban={() => setActiveKey(null)}
          tasks={projectTasks}
          filters={filters}
          density={tweaks.cardDensity === "dense" ? "dense" : tweaks.cardDensity === "spacious" ? "spacious" : ""}
          detailPanes={tweaks.detailPanes || { description: true, chat: true, commit: false }}
          onTogglePane={(key) => {
            const current = tweaks.detailPanes || { description: true, chat: true, commit: false };
            const next = { ...current, [key]: !current[key] };
            if (!next.description && !next.chat && !next.commit) return;
            setTweak({ detailPanes: next });
          }}
          onOpenTask={openTask}
          onOpenDiff={openDiff}
          onOpenActivity={openActivity}
          onOpenHub={openHub}
          activeItem={activeItem}
          activeTask={activeTask}
          activeProject={activeProject}
          showProjectBadges={activeProject === "__all__"}
        />
        {chatOpen && window.ChatPanel ? (
          <window.ChatPanel
            project={window.MOCK.PROJECTS.find(p => p.id === activeProject)?.name || "Project"}
            onClose={() => setTweak({ chatOpen: false })}
            onResize={startChatResize}
          />
        ) : (
          window.ChatRail && <window.ChatRail unread={3} onOpen={() => setTweak({ chatOpen: true })}/>
        )}
        {tweaks.activityBarPosition === "right" && (
          <ActivityBar items={activityItems} active={activityPanel} onClick={handleActivity} onSettings={() => handleActivity("settings")} settingsActive={activityPanel === "settings"}/>
        )}
      </div>
      <Statusbar/>
      {window.TweaksPanel && (
        <window.TweaksPanel title="Tweaks">
          <window.TweakSection label="Appearance">
            <window.TweakRadio label="Theme" value={tweaks.theme} options={[{label: "Dark", value: "dark"}, {label: "Light", value: "light"}]} onChange={(v) => setTweak({ theme: v })}/>
          </window.TweakSection>
          <window.TweakSection label="Layout">
            <window.TweakRadio label="Activity bar" value={tweaks.activityBarPosition} options={[{label: "Left", value: "left"}, {label: "Right", value: "right"}]} onChange={(v) => setTweak({ activityBarPosition: v })}/>
            <window.TweakSlider label="Sidebar width" value={tweaks.sidebarWidth} min={200} max={500} step={10} unit="px" onChange={(v) => setTweak({ sidebarWidth: v })}/>
          </window.TweakSection>
          <window.TweakSection label="Cards">
            <window.TweakRadio label="Density" value={tweaks.cardDensity} options={[{label: "Dense", value: "dense"}, {label: "Normal", value: "normal"}, {label: "Spacious", value: "spacious"}]} onChange={(v) => setTweak({ cardDensity: v })}/>
          </window.TweakSection>
          <window.TweakSection label="Project Chat">
            <window.TweakToggle label="Show chat" value={chatOpen} onChange={(v) => setTweak({ chatOpen: v })}/>
            <window.TweakSlider label="Chat width" value={chatWidth} min={260} max={560} step={10} unit="px" onChange={(v) => setTweak({ chatWidth: v })}/>
          </window.TweakSection>
        </window.TweaksPanel>
      )}
    </div>
  );
}

// ============ Titlebar ============
function Titlebar({ activeProject, setActiveProject, activeWorkspace, setActiveWorkspace, wsProjects, view, chatOpen, chatUnread, onToggleChat, theme, onToggleTheme, onOpenHub }) {
  const { WORKSPACES } = window.MOCK;
  const [wsOpen, setWsOpen] = React.useState(false);
  const ws = WORKSPACES.find(w => w.id === activeWorkspace) || WORKSPACES[0];
  const isAll = activeProject === "__all__";

  React.useEffect(() => {
    if (!wsOpen) return;
    const onDoc = (e) => {
      if (!e.target.closest(".ws-picker") && !e.target.closest(".ws-dropdown")) setWsOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [wsOpen]);

  return (
    <div className="titlebar">
      <div className="logo">A</div>
      <span className="app-name">Agent Software Studio</span>
      <div className="ws-picker-wrap">
        <button className={`ws-picker ${wsOpen ? "open" : ""}`} onClick={() => setWsOpen(o => !o)} title="Switch workspace">
          <Icon name="folder" size={11} color="var(--fg-dim)"/>
          <span className="ws-name">{ws.name}</span>
          <Icon name="chevronDown" size={10} color="var(--fg-muted)"/>
        </button>
        {wsOpen && (
          <div className="ws-dropdown">
            <div className="ws-dropdown-head">Workspaces</div>
            {WORKSPACES.map(w => (
              <button
                key={w.id}
                className={`ws-option ${w.id === activeWorkspace ? "active" : ""}`}
                onClick={() => { setActiveWorkspace(w.id); setWsOpen(false); }}
              >
                <span className="ws-option-icon"><Icon name="folder" size={13}/></span>
                <div className="ws-option-text">
                  <div className="ws-option-name">{w.name}</div>
                  <div className="ws-option-path mono">{w.path}</div>
                </div>
                <span className="ws-option-count">{w.projectIds.length}</span>
                {w.id === activeWorkspace && <span className="ws-option-check"><Icon name="check" size={12} color="var(--accent)"/></span>}
              </button>
            ))}
            <div className="ws-dropdown-sep"/>
            <button className="ws-option ws-add">
              <span className="ws-option-icon"><Icon name="plus" size={13}/></span>
              <span>Add workspace…</span>
            </button>
          </div>
        )}
      </div>
      <span className="ws-sep">/</span>
      <div className="project-pills">
        <button
          className={`proj-pill all ${isAll ? "active" : ""}`}
          onClick={() => setActiveProject("__all__")}
          title={`Show tasks from all projects in ${ws.name}`}
        >
          <span className="glyph all" style={{ background: "var(--fg-dim)" }}>
            <Icon name="grid" size={10} color="#1a1a1a"/>
          </span>
          <span>All projects</span>
        </button>
        {wsProjects.map(p => (
          <button
            key={p.id}
            className={`proj-pill ${activeProject === p.id ? "active" : ""}`}
            onClick={() => setActiveProject(p.id)}
            onDoubleClick={() => onOpenHub && onOpenHub(p.id)}
            title={`${p.name} (double-click for Project Hub)`}
          >
            <span className="glyph" style={{ background: p.color }}>{p.short}</span>
            <span>{p.name}</span>
            <span className="cli-mini">{p.tasks}</span>
          </button>
        ))}
      </div>
      <span className="spacer"/>
      <button className="title-cmd">
        <Icon name="search" size={12}/>
        <span>Search tasks, commits, files… </span>
        <span className="kbd-hint" style={{ marginLeft: "auto" }}>⌘P</span>
      </button>
      <div className="title-actions">
        <button
          className="title-btn theme-toggle"
          title={theme === "light" ? "Switch to dark theme" : "Switch to light theme"}
          onClick={onToggleTheme}
        >
          <Icon name={theme === "light" ? "moon" : "sun"} size={14}/>
        </button>
        <button
          className={`title-chat ${chatOpen ? "active" : ""}`}
          title={chatOpen ? "Close project chat" : "Open project chat"}
          onClick={onToggleChat}
        >
          <Icon name="cli" size={13}/>
          <span>Project Chat</span>
          {!chatOpen && chatUnread > 0 && <span className="title-chat-badge">{chatUnread}</span>}
        </button>
        <button className="title-btn" data-tip="Notifications" aria-label="Notifications"><Icon name="bell" size={14}/></button>
        <button className="title-btn auto-review-title" data-tip="Auto-review status: idle" aria-label="Auto-review status">
          <span className="auto-review-dot"/>
          <Icon name="bot" size={13}/>
        </button>
      </div>
    </div>
  );
}

// ============ ActivityBar ============
function ActivityBar({ items, active, onClick, onSettings, settingsActive }) {
  return (
    <div className="activitybar">
      {items.map(it => (
        <button key={it.key} className={`ab-btn ${active === it.key ? "active" : ""}`} title={it.label} onClick={() => onClick(it.key)}>
          <Icon name={it.icon} size={20}/>
          {it.badge && <span className="badge">{it.badge}</span>}
        </button>
      ))}
      <span className="ab-spacer"/>
      <button className={`ab-btn ${settingsActive ? "active" : ""}`} title="Settings" onClick={onSettings}>
        <Icon name="settings" size={20}/>
      </button>
      <button className="ab-btn" title="Account">
        <span style={{ width: 24, height: 24, borderRadius: "50%", background: "linear-gradient(135deg, #d97757, #8d5a3d)", display: "grid", placeItems: "center", fontSize: 10, fontWeight: 700, color: "#1a1a1a" }}>AB</span>
      </button>
    </div>
  );
}

// ============ Main (editor area) ============
function Main({ view, openItems, activeKey, keyOf, onSelectTab, onCloseTab, onCloseAllTabs, onCloseOtherTabs, onCloseTabsToRight, onCloseTabsToLeft, onOpenBoard, tasks, filters, density, detailPanes, onTogglePane, onOpenTask, onOpenDiff, onOpenActivity, onOpenHub, activeItem, activeTask, activeProject, boardProjectId, showProjectBadges }) {
  const [ctxMenu, setCtxMenu] = React.useState(null); // { key, x, y }
  React.useEffect(() => {
    if (!ctxMenu) return;
    const onDoc = (e) => {
      if (!e.target.closest(".tab-ctx-menu")) setCtxMenu(null);
    };
    document.addEventListener("mousedown", onDoc);
    document.addEventListener("scroll", () => setCtxMenu(null), true);
    return () => {
      document.removeEventListener("mousedown", onDoc);
    };
  }, [ctxMenu]);
  const tabFor = (it) => {
    if (it.kind === "board") {
      const p = window.MOCK.PROJECTS.find(x => x.id === it.projectId);
      const isAll = it.projectId === "__all__";
      const name = isAll ? "All projects" : (p?.name || "Project");
      return {
        key: keyOf(it),
        icon: <span className="tab-glyph board-glyph" style={{ background: isAll ? "var(--fg-dim)" : p?.color }}>
          {isAll ? <Icon name="grid" size={8} color="#1a1a1a"/> : p?.short}
        </span>,
        num: "board",
        title: `${name} · Board`,
      };
    }
    if (it.kind === "task") {
      const t = window.MOCK.TASKS.find(x => x.id === it.id);
      if (!t) return null;
      return { key: keyOf(it), icon: <span className="modified-dot"/>, num: t.num, title: t.title };
    }
    if (it.kind === "diff") {
      return { key: keyOf(it), icon: <Icon name="diff" size={12} color="var(--accent-2)"/>, num: it.commit, title: "12 files · +413/-8" };
    }
    if (it.kind === "hub") {
      const p = window.MOCK.PROJECTS.find(x => x.id === it.projectId);
      return { key: keyOf(it), icon: <span className="tab-glyph" style={{ background: p?.color }}>{p?.short}</span>, num: "hub", title: `${p?.name || "Project"} · Hub` };
    }
    if (it.kind === "activity") {
      const t = window.MOCK.TASKS.find(x => x.id === it.taskId);
      return { key: keyOf(it), icon: <Icon name="activity" size={12} color="var(--accent)"/>, num: t?.num || "?", title: `Activity · ${t?.title || "Task"}` };
    }
    return null;
  };
  return (
    <div className="main">
      <div className="tabbar">
        <div className="tab-list">
          {openItems.map(it => {
            const t = tabFor(it);
            if (!t) return null;
            return (
              <div
                key={t.key}
                className={`tab ${activeKey === t.key ? "active" : ""}`}
                onClick={() => onSelectTab(t.key)}
                onContextMenu={(e) => {
                  e.preventDefault();
                  setCtxMenu({ key: t.key, x: e.clientX, y: e.clientY });
                }}
              >
                {t.icon}
                {it.kind !== "board" && <span className="num">{t.num}</span>}
                <span className="ttl">{t.title}</span>
                <span className="close" onClick={(e) => onCloseTab(t.key, e)}><Icon name="close" size={10}/></span>
              </div>
            );
          })}
        </div>
        {view === "detail" && (
          <DetailHeaderActions
            task={activeTask}
            onOpenDiff={onOpenDiff}
            onOpenActivity={onOpenActivity}
            detailPanes={detailPanes}
            onTogglePane={onTogglePane}
          />
        )}
        {view === "diff" && (
          <DiffHeaderActions commit={activeItem?.commit}/>
        )}
        {view === "kanban" && boardProjectId !== "__all__" && (
          <div className="tab-actions">
            <button className="btn-ghost" style={{ padding: "3px 10px", fontSize: 11 }} onClick={() => onOpenHub && onOpenHub(boardProjectId)}>
              <Icon name="grid" size={11}/> Project Hub
            </button>
          </div>
        )}
      </div>
      {ctxMenu && (
        <TabContextMenu
          x={ctxMenu.x}
          y={ctxMenu.y}
          tabKey={ctxMenu.key}
          openItems={openItems}
          keyOf={keyOf}
          onClose={() => setCtxMenu(null)}
          onCloseTab={onCloseTab}
          onCloseOthers={onCloseOtherTabs}
          onCloseToRight={onCloseTabsToRight}
          onCloseToLeft={onCloseTabsToLeft}
          onCloseAll={onCloseAllTabs}
        />
      )}
      <div className="editor">
        {view === "welcome" ? (
          <WelcomeScreen onOpenBoard={onOpenBoard} activeProject={activeProject}/>
        ) : view === "kanban" ? (
          <window.KanbanView
            tasks={tasks}
            filters={filters}
            density={density}
            onOpen={onOpenTask}
            activeTaskId={null}
            showProjectBadges={showProjectBadges}
          />
        ) : view === "diff" ? (
          window.DiffFullView ? <window.DiffFullView commit={activeItem?.commit}/> : null
        ) : view === "hub" ? (
          window.ProjectHub ? <window.ProjectHub projectId={activeItem?.projectId} initialSection={activeItem?.section || "overview"}/> : null
        ) : view === "activity" ? (
          window.ActivityFullView ? <window.ActivityFullView taskId={activeItem?.taskId}/> : null
        ) : (
          activeTask && <window.DetailView task={activeTask} panes={detailPanes} onOpenDiff={onOpenDiff} onOpenActivity={onOpenActivity}/>
        )}
      </div>
    </div>
  );
}

function WelcomeScreen({ onOpenBoard, activeProject }) {
  const { PROJECTS } = window.MOCK;
  return (
    <div className="welcome">
      <div className="welcome-card">
        <Icon name="layout" size={40} color="var(--fg-muted)"/>
        <div className="welcome-title">No tab open</div>
        <div className="welcome-sub">Open a board to see tasks, or pick a project from the explorer.</div>
        <div className="welcome-actions">
          {PROJECTS.slice(0, 4).map(p => (
            <button key={p.id} className="welcome-btn" onClick={() => onOpenBoard && onOpenBoard(p.id)}>
              <span className="welcome-btn-glyph" style={{ background: p.color }}>{p.short}</span>
              <span>{p.name}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

function TabContextMenu({ x, y, tabKey, openItems, keyOf, onClose, onCloseTab, onCloseOthers, onCloseToRight, onCloseToLeft, onCloseAll }) {
  const idx = openItems.findIndex(it => keyOf(it) === tabKey);
  const total = openItems.length;
  const hasRight = idx >= 0 && idx < total - 1;
  const hasLeft = idx > 0;
  const hasOthers = total > 1;

  const stop = (fn) => (e) => { e.stopPropagation(); fn(); onClose(); };

  return (
    <div className="tab-ctx-menu" style={{ left: x, top: y }} onMouseDown={(e) => e.stopPropagation()}>
      <button className="tab-ctx-item" onClick={stop(() => onCloseTab(tabKey))}>
        <span>Close</span>
        <span className="kbd-hint">⌘W</span>
      </button>
      <button className="tab-ctx-item" disabled={!hasOthers} onClick={stop(() => hasOthers && onCloseOthers(tabKey))}>
        Close Others
      </button>
      <button className="tab-ctx-item" disabled={!hasRight} onClick={stop(() => hasRight && onCloseToRight(tabKey))}>
        Close to the Right
      </button>
      <button className="tab-ctx-item" disabled={!hasLeft} onClick={stop(() => hasLeft && onCloseToLeft(tabKey))}>
        Close to the Left
      </button>
      <div className="tab-ctx-sep"/>
      <button className="tab-ctx-item" onClick={stop(onCloseAll)}>
        Close All
      </button>
    </div>
  );
}

function DetailHeaderActions({ task, onOpenDiff, onOpenActivity, detailPanes, onTogglePane }) {
  if (!task) return null;
  const panes = detailPanes || { description: true, activity: true, commit: true };
  return (
    <div className="tab-actions detail-actions">
      <div className="pane-toggles" title="Toggle panels">
        <button
          className={`pane-toggle icon-only ${panes.description ? "active" : ""}`}
          data-tip="Description / Evidence"
          onClick={() => onTogglePane && onTogglePane("description")}
          aria-label="Description"
        >
          <Icon name="book" size={13}/>
        </button>
        <button
          className={`pane-toggle icon-only ${panes.chat ? "active" : ""}`}
          data-tip="Chat (toggle pane)"
          onClick={() => onTogglePane && onTogglePane("chat")}
          aria-label="Chat"
        >
          <Icon name="cli" size={13}/>
        </button>
        <button
          className={`pane-toggle icon-only ${panes.commit ? "active" : ""}`}
          data-tip="Commit"
          onClick={() => onTogglePane && onTogglePane("commit")}
          aria-label="Commit"
        >
          <Icon name="diff" size={13}/>
        </button>
        <span style={{ width: 1, height: 16, background: "var(--border)", margin: "0 2px" }}/>
        <button
          className="pane-toggle icon-only"
          data-tip="Open Activity / Debug (full view)"
          onClick={() => onOpenActivity && onOpenActivity(task.id)}
          aria-label="Debug"
        >
          <Icon name="activity" size={13}/>
        </button>
      </div>
      <span className="tab-actions-sep"/>
      <div className="pager">
        <button className="ic-btn pager-btn" data-tip="Previous task" aria-label="Previous task"><Icon name="chevronLeft" size={12}/></button>
        <span className="pager-pos mono">2/26</span>
        <button className="ic-btn pager-btn" data-tip="Next task" aria-label="Next task"><Icon name="chevronRight" size={12}/></button>
      </div>
      <span className="tab-actions-sep"/>
      <button className="ic-btn auto-review-btn" data-tip="Auto-review running" aria-label="Auto Review">
        <span className="dot" style={{ background: "var(--accent-3)" }}/>
        <Icon name="bot" size={12}/>
      </button>
      <button className="ic-btn complete-btn" data-tip="Complete this task and move to the next" aria-label="Complete and Next">
        <Icon name="check" size={12} color="#1a1a1a"/>
      </button>
      <button className="ic-btn" data-tip="More actions" aria-label="More"><Icon name="more" size={14}/></button>
    </div>
  );
}

function DiffHeaderActions({ commit }) {
  return (
    <div className="tab-actions" style={{ borderLeft: "1px solid var(--border)" }}>
      <span className="mono" style={{ fontSize: 11, color: "var(--fg-muted)", padding: "0 6px" }}>{commit}</span>
      <button className="ic-btn" title="Previous file"><Icon name="chevronLeft" size={14}/></button>
      <button className="ic-btn" title="Next file"><Icon name="chevronRight" size={14}/></button>
      <button className="btn-ghost" style={{ padding: "3px 10px", fontSize: 11 }}>
        <Icon name="branch" size={11}/> View on git
      </button>
    </div>
  );
}

// ============ Statusbar ============
function Statusbar() {
  const { CLIS } = window.MOCK;
  return (
    <div className="statusbar">
      <span className="sb-item accent">
        <Icon name="branch" size={12}/>
        <span>main</span>
        <span className="muted" style={{ fontSize: 10 }}>↑0 ↓0</span>
      </span>
      <span className="sb-item">
        <span className="dot" style={{ background: "var(--accent-2)" }}/>
        Orchestrator
      </span>
      <span className="sb-item">
        <Icon name="play" size={11} color="var(--accent-2)"/>
        <span style={{ color: "var(--accent-2)" }}>1 running</span>
        <span className="muted">· 2/3 auto</span>
      </span>
      <span className="sb-item">
        <Icon name="activity" size={11}/>
        Feed
      </span>
      <span className="sb-item">
        <Icon name="eye" size={11}/>
        Visual evidence
      </span>
      <span className="sb-spacer"/>
      {CLIS.map(c => {
        const anyCrit = c.quotas.some(q => q.critical || q.used > 0.85);
        return (
          <span key={c.id} className={`sb-item sb-cli ${anyCrit ? "danger" : ""}`}>
            <span className="sb-glyph" style={{ background: c.color }}>{c.name[0]}</span>
            <span>{c.name}</span>
            {anyCrit && <span style={{ color: "var(--accent-warn)" }}>!</span>}
            <span className="sb-cli-quotas">
              {c.quotas.map((q, i) => (
                <span key={i} className={`sb-cli-quota ${q.critical ? "critical" : ""}`} title={`${q.period}: ${Math.round(q.used*100)}% used · resets in ${q.resetsIn}`}>
                  <span className="sb-cli-quota-period">{q.period}</span>
                  <span className="quota-bar"><div style={{ width: `${Math.max(2, Math.round(q.used*100))}%`, background: q.critical ? "var(--accent)" : c.color }}/></span>
                  <span className="sb-cli-quota-reset mono">{q.short}</span>
                </span>
              ))}
            </span>
          </span>
        );
      })}
      <span style={{ width: 1, height: 14, background: "var(--border)", margin: "0 4px" }}/>
      <span className="sb-item">
        <span className="sb-glyph" style={{ background: "var(--accent)" }}>C</span>
        <span>Claude Code</span>
      </span>
      <span className="sb-item">
        <Icon name="dot" size={10} color="var(--accent-2)"/>
        <span>Claude Opus 4.7</span>
      </span>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App/>);
