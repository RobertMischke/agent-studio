/* eslint-disable */
// Kanban view
const { useMemo: useMemoKb } = React;

function Card({ task, onOpen, active, showProjectBadge }) {
  const { TYPE_META, CLI_META, PROJECTS } = window.MOCK;
  const typeM = TYPE_META[task.type];
  const cliM = task.cli ? CLI_META[task.cli] : null;
  const proj = PROJECTS.find(p => p.id === task.project);
  const reviewMap = {
    "auto review": "review-auto",
    "review reissue": "review-reissue",
    "review escalate": "review-escalate",
    "review accept": "review-accept",
    "queued for review": "review-queued",
    "human review": "review-human",
  };

  return (
    <div className={`card ${active ? "active" : ""}`} onClick={() => onOpen(task.id)}>
      <div className="top">
        <span className="proj-badge" style={proj ? { background: `${proj.color}26`, color: proj.color } : null}>
          <span className="pglyph" style={proj ? { background: proj.color } : null}>{proj?.short || "?"}</span>
          {(proj?.name || "PROJECT").toUpperCase()}
        </span>
        <span className="branch-badge"><Icon name="branch" size={9} color="currentColor"/>{task.branch}</span>
        <span className="task-num">{task.num}</span>
      </div>
      <div className="title">{task.title}</div>
      <div className="mid">
        {typeM && <span className={`tg ${task.type}`}><span className="dot"/>{typeM.label}</span>}
        {task.review && <span className={`tg ${reviewMap[task.review] || ""}`}>{task.review}</span>}
        {task.warning && <span className="tg warn">⚠ {task.warning}</span>}
      </div>
      {(task.tags && task.tags.length > 0) && (
        <div className="mid">
          {task.tags.map((t, i) => (
            <span key={i} className={`tg ${t.includes("concerns") ? "concern" : "label"}`}>{t.includes("concerns") && "⚠ "}{t}</span>
          ))}
        </div>
      )}
      <div className="bot">
        <span className="flex" style={{ gap: 8 }}>
          {cliM && <span className="cli-chip"><span className="cli-glyph sm" style={{ background: cliM.color }}>{cliM.glyph}</span>{cliM.label}</span>}
          {task.commit && (
            <span className="commit">
              <span className="dot" style={{ width: 5, height: 5, borderRadius: "50%", background: "var(--accent-2)" }}/>
              {task.commit}
              <span className="files">{task.files} files</span>
            </span>
          )}
        </span>
        <span className="flex">
          {task.activity && <span style={{ color: "var(--fg-muted)", fontSize: 10, fontFamily: "var(--font-mono)" }}>{task.activity}</span>}
          <span className="avatar">AB</span>
        </span>
      </div>
    </div>
  );
}

function Lane({ title, count, info, tasks, onOpen, activeTaskId, children, foot }) {
  return (
    <div className="lane">
      <div className="lane-head">
        <span style={{ color: "var(--fg-dim)" }}>{title}</span>
        <span className="count">{count}</span>
        <span style={{ marginLeft: "auto" }} className="flex">
          {info && <button className="ic-btn" title={info}><Icon name="bell" size={12}/></button>}
          <button className="ic-btn"><Icon name="expand" size={12}/></button>
        </span>
      </div>
      <div className="lane-body">
        {children}
        {tasks && tasks.map(t => (
          <Card key={t.id} task={t} onOpen={onOpen} active={activeTaskId === t.id}/>
        ))}
        {foot}
      </div>
    </div>
  );
}

function SubHead({ name, count, icon }) {
  return (
    <div className="lane-sub-head">
      <span className="icn"><Icon name={icon || "dot"} size={12}/></span>
      <span>{name}</span>
      <span className="count">{count}</span>
    </div>
  );
}

function KanbanView({ tasks, filters, density, onOpen, activeTaskId }) {
  const matches = (t) => {
    if (filters.state && filters.state.length && !filters.state.includes(t.state)) return false;
    if (filters.type && filters.type.length && !filters.type.includes(t.type)) return false;
    if (filters.cli && filters.cli.length && !(t.cli && filters.cli.includes(t.cli))) return false;
    return true;
  };
  const visible = tasks.filter(matches);
  const inProgress = visible.filter(t => t.state === "in-progress");
  const autoReview = visible.filter(t => t.state === "auto-review");
  const humanReview = visible.filter(t => t.state === "human-review");
  const backlogTasks = visible.filter(t => t.state === "backlog");

  return (
    <div className={`kanban ${density}`}>
      <Lane title="BACKLOG" count={26} onOpen={onOpen} activeTaskId={activeTaskId}>
        <SubHead name="Human Ready" count={0} icon="check"/>
        <div style={{ padding: "12px 8px", textAlign: "center", color: "var(--fg-muted)", fontSize: 11 }}>No jobs</div>
        <button className="btn-ghost" style={{ alignSelf: "stretch", justifyContent: "center", marginTop: 4 }}>
          <Icon name="plus" size={12}/> Add task
        </button>
        <SubHead name="In Preparation" count={backlogTasks.filter(t => t.lane === "in-prep").length} icon="file"/>
        {backlogTasks.filter(t => t.lane === "in-prep").map(t => <Card key={t.id} task={t} onOpen={onOpen} active={activeTaskId === t.id}/>)}
        <SubHead name="Needs Clarification" count={backlogTasks.filter(t => t.lane === "needs-clar").length} icon="warn"/>
        {backlogTasks.filter(t => t.lane === "needs-clar").map(t => <Card key={t.id} task={t} onOpen={onOpen} active={activeTaskId === t.id}/>)}
      </Lane>
      <Lane title="ACTIVE" count={inProgress.length + autoReview.length} onOpen={onOpen} activeTaskId={activeTaskId}>
        <SubHead name="In Progress" count={inProgress.length} icon="play"/>
        {inProgress.length === 0 && (
          <div style={{ padding: "12px 8px", textAlign: "center", color: "var(--fg-muted)", fontSize: 11 }}>No jobs</div>
        )}
        {inProgress.map(t => <Card key={t.id} task={t} onOpen={onOpen} active={activeTaskId === t.id}/>)}
        <SubHead name="Auto Review" count={autoReview.length} icon="bot"/>
        {autoReview.map(t => <Card key={t.id} task={t} onOpen={onOpen} active={activeTaskId === t.id}/>)}
      </Lane>
      <Lane title="DONE & DECIDE" count={108} onOpen={onOpen} activeTaskId={activeTaskId}>
        <SubHead name="Human Review" count={108} icon="eye"/>
        {humanReview.map(t => <Card key={t.id} task={t} onOpen={onOpen} active={activeTaskId === t.id}/>)}
        <SubHead name="Archive" count={288} icon="archive"/>
        <div style={{ padding: "8px", textAlign: "center", color: "var(--fg-muted)", fontSize: 11 }}>288 archived · click to browse</div>
      </Lane>
    </div>
  );
}

window.KanbanView = KanbanView;
