/* eslint-disable */
// Detail view — VS-Code-like editor area for a single task
const { useState: useStateD } = React;

function Description({ task }) {
  return (
    <div className="desc">
      <h1>{task.title}</h1>
      <div className="meta">
        <span className="mono">#{task.id}</span>
        <span>·</span>
        <span>created 32h ago</span>
        <span>·</span>
        <span style={{ color: "var(--accent)" }}>● Auto-Review</span>
      </div>

      <p>Follow-up to the evaluation in <code>agent-taskboard/7-archive/&lt;this-evaluation-slug&gt;/results/session-task-linkage-plan.md</code> (slug: <code>im-side-sheet-werden-ja-sessions-angezeigt</code>). Read sections 1-6 of that plan first; section 7 explains why the implementation was deferred to this task.</p>

      <h2>Goal</h2>
      <p>Each row in the side-sheet <code>Sessions</code> segment gets a small chip that links to the owning kanban task when one of the project's jobs lists this session id in its <code>SessionChain</code>. Chip states: <code>active</code> (green, owning job is in <code>3-progress</code> and currently active), <code>linked</code> (neutral, owning job exists but is not active), or no chip for orphan sessions. Click jumps to the task detail.</p>

      <h2>Scope (bundle as one PR)</h2>
      <h3 style={{ fontSize: 14, color: "var(--fg-strong)", margin: "16px 0 6px" }}>Backend (Phase 0 + Phase 1 of the plan)</h3>
      <ul>
        <li>Add <code>backend/Services/Cli/SessionToJobIndex.cs</code> in-memory dictionary <code>sessionId → {`{ jobId, watchPath, lane, isActive }`}</code>, rebuilt from <code>JobScannerService.ScanAllJobs()</code> and each job's <code>SessionChain</code>. Invalidate on the same <code>JobChanged</code> events <code>JobWatcherService</code> already raises.</li>
        <li>Skip <code>(recovery)</code> sentinels. Tie-break on <code>WatchPath</code> equality with the session row's <code>cwd</code> when a session id appears in chains across checkouts.</li>
        <li>Extend <code>CliSessionInfo</code> (in <code>backend/Models/CliTypes.cs:47</code>) with optional <code>LinkedJob</code> carrying <code>{`{ jobId, title, lane, isActive }`}</code>.</li>
      </ul>
      <h3 style={{ fontSize: 14, color: "var(--fg-strong)", margin: "16px 0 6px" }}>Frontend (Phase 1 of the plan)</h3>
      <ul>
        <li>Update <code>frontend/src/app/models/job.model.ts</code> (or wherever the <code>CliSessionInfo</code> TS type lives) to mirror the new optional field.</li>
        <li>In <code>frontend/src/app/features/cli/components/cli-usage-sheet.ts</code> and its template, render the chip with <code>data-testid="session-task-link"</code>. Plain text label, delayed tooltip per the project tooltip rule. Click jumps via the existing router/detail-panel mechanism.</li>
      </ul>
    </div>
  );
}

function Evidence({ task }) {
  return (
    <div className="evidence">
      <div className="ev-section">
        <div className="ev-section-head">
          <span style={{ color: "var(--accent-2)" }}>●</span>
          <span>Build &amp; Tests</span>
          <span className="ev-status pass">2 passed</span>
        </div>
        <div className="ev-card pass">
          <div className="ev-card-head">
            <Icon name="check" size={12} color="var(--accent-2)"/>
            <span className="ev-title">backend test suite</span>
            <span className="ev-meta mono">282 / 282</span>
          </div>
          <div className="ev-card-body">dotnet test — completed in 14.2s</div>
        </div>
        <div className="ev-card pass">
          <div className="ev-card-head">
            <Icon name="check" size={12} color="var(--accent-2)"/>
            <span className="ev-title">cli-usage-sheet.spec.ts</span>
            <span className="ev-meta mono">1 / 1</span>
          </div>
          <div className="ev-card-body">no regression on legacy panel</div>
        </div>
      </div>

      <div className="ev-section">
        <div className="ev-section-head">
          <span style={{ color: "var(--accent-warn)" }}>●</span>
          <span>Playwright</span>
          <span className="ev-status defer">deferred</span>
        </div>
        <div className="ev-card defer">
          <div className="ev-card-head">
            <Icon name="warn" size={12} color="var(--accent-warn)"/>
            <span className="ev-title">session-task-link-chip.spec.ts</span>
            <span className="ev-meta mono">type-check ok</span>
          </div>
          <div className="ev-card-body">Execution reserved for stable's Playwright pass per the dev-backend-lifecycle rule.</div>
        </div>
      </div>

      <div className="ev-section">
        <div className="ev-section-head">
          <span style={{ color: "var(--accent-3)" }}>●</span>
          <span>Visual Evidence</span>
          <span className="ev-status">3 captures</span>
        </div>
        <div className="ev-thumbs">
          <div className="ev-thumb">
            <div className="ev-thumb-img" style={{ background: "linear-gradient(135deg,#2d2d2d,#1e1e1e)" }}>
              <div style={{ position: "absolute", inset: 0, padding: 8 }}>
                <div style={{ width: "60%", height: 4, background: "var(--accent)", marginBottom: 6 }}/>
                <div style={{ width: "85%", height: 3, background: "#3a3a3a", marginBottom: 3 }}/>
                <div style={{ width: "70%", height: 3, background: "#3a3a3a", marginBottom: 3 }}/>
                <div style={{ width: "75%", height: 3, background: "#3a3a3a" }}/>
              </div>
            </div>
            <div className="ev-thumb-label">side-sheet · linked state</div>
          </div>
          <div className="ev-thumb">
            <div className="ev-thumb-img" style={{ background: "linear-gradient(135deg,#1e1e1e,#252526)" }}>
              <div style={{ position: "absolute", inset: 0, padding: 8 }}>
                <div style={{ width: "40%", height: 4, background: "var(--accent-2)", marginBottom: 6 }}/>
                <div style={{ width: "75%", height: 3, background: "#3a3a3a", marginBottom: 3 }}/>
                <div style={{ width: "60%", height: 3, background: "#3a3a3a" }}/>
              </div>
            </div>
            <div className="ev-thumb-label">side-sheet · active state</div>
          </div>
          <div className="ev-thumb">
            <div className="ev-thumb-img" style={{ background: "linear-gradient(135deg,#252526,#1e1e1e)" }}>
              <div style={{ position: "absolute", inset: 0, padding: 8 }}>
                <div style={{ width: "30%", height: 4, background: "#5a5a5a", marginBottom: 6 }}/>
                <div style={{ width: "70%", height: 3, background: "#3a3a3a", marginBottom: 3 }}/>
                <div style={{ width: "50%", height: 3, background: "#3a3a3a" }}/>
              </div>
            </div>
            <div className="ev-thumb-label">orphan · no chip</div>
          </div>
        </div>
      </div>

      <div className="ev-section">
        <div className="ev-section-head">
          <span style={{ color: "var(--accent-2)" }}>●</span>
          <span>Git</span>
          <span className="ev-status pass">clean</span>
        </div>
        <div className="ev-card pass">
          <div className="ev-card-head">
            <Icon name="branch" size={12} color="var(--accent-2)"/>
            <span className="ev-title">main</span>
            <span className="ev-meta mono">in sync · ↑0 ↓0</span>
          </div>
        </div>
      </div>

      <div className="ev-section">
        <div className="ev-section-head">
          <span style={{ color: "var(--accent-4)" }}>●</span>
          <span>Sentinels</span>
          <span className="ev-status">ok</span>
        </div>
        <div className="ev-card">
          <div className="ev-card-head">
            <Icon name="check" size={12} color="var(--accent-2)"/>
            <span className="ev-title">requirement-fit</span>
            <span className="ev-meta">sentinel present</span>
          </div>
        </div>
        <div className="ev-card">
          <div className="ev-card-head">
            <Icon name="check" size={12} color="var(--accent-2)"/>
            <span className="ev-title">tests-and-evidence</span>
            <span className="ev-meta">sentinel present</span>
          </div>
        </div>
      </div>
    </div>
  );
}

function ProtocolStrip() {
  return (
    <div className="proto-strip">
      <span className="ic"><Icon name="warn" size={14} color="var(--accent-warn)"/></span>
      <span className="title">Unclear</span>
      <span className="sub">— The last run produced output but no clear verdict.</span>
      <span style={{ marginLeft: "auto", display: "flex", gap: 6 }}>
        <button className="btn-ghost" style={{ padding: "3px 10px" }}>Force-accept</button>
        <button className="btn-ghost" style={{ padding: "3px 10px" }}>Reissue</button>
      </span>
    </div>
  );
}

function ActivityLog({ task }) {
  const log = task.activityLog || [];
  return (
    <div className="log-list">
      <div style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 11, color: "var(--fg-muted)", margin: "0 0 6px" }}>
        <span className="mono">RUNS</span>
        <span className="kbd-hint">1</span>
        <span className="kbd-hint" style={{ background: "var(--accent)", color: "#1a1a1a", borderColor: "var(--accent)" }}>2</span>
        <span>· 2 runs · 1 ok · 1 live</span>
        <span style={{ marginLeft: "auto" }}>
          <label style={{ fontSize: 11, color: "var(--fg-muted)" }}><input type="checkbox" style={{ marginRight: 4, verticalAlign: "middle" }}/>Show tool activity</label>
        </span>
      </div>
      {log.map((l, i) => (
        <div key={i} className={`log-item ${l.type === "decision" ? "decision" : ""}`}>
          <div className="head">
            <span className="who" style={{ color: l.type === "decision" ? "var(--accent)" : "var(--fg-strong)" }}>
              {l.type === "decision" ? "⚙ ORCHESTRATOR" : "STEP"}
            </span>
            <span className="time">{l.time}</span>
          </div>
          <div className="text">{l.text}</div>
        </div>
      ))}
      {log.length === 0 && <div style={{ color: "var(--fg-muted)", fontSize: 12, padding: 8 }}>No activity yet.</div>}
    </div>
  );
}

function Composer() {
  const [mode, setMode] = useStateD("continue");
  return (
    <div className="composer">
      <div className="modes">
        <span>MODE</span>
        <button className={`mode ${mode === "continue" ? "active" : ""}`} onClick={() => setMode("continue")}><Icon name="play" size={9} color={mode === "continue" ? "#1a1a1a" : "currentColor"}/> Continue</button>
        <button className={`mode ${mode === "steer" ? "active" : ""}`} onClick={() => setMode("steer")}>⟲ Steer</button>
        <button className={`mode ${mode === "extend" ? "active" : ""}`} onClick={() => setMode("extend")}>+ Extend</button>
        <button className={`mode ${mode === "new" ? "active" : ""}`} onClick={() => setMode("new")}>+ New task</button>
      </div>
      <textarea placeholder="Type a follow-up — Ctrl+Enter to send. Sends while running pauses the agent first."/>
      <div className="send-row">
        <span className="hint">Job 2 of 26 · <span className="kbd-hint">⌃⏎</span> send · <span className="kbd-hint">Esc</span> close</span>
        <button className="btn-primary"><Icon name="send" size={11} color="#1a1a1a"/> Send</button>
      </div>
    </div>
  );
}

function CommitPanel({ task }) {
  const files = [
    { name: "backend/", indent: 0, dir: true, stat: "+242 / -2" },
    { name: "Endpoints/", indent: 1, dir: true, stat: "+11 / -2" },
    { name: "CliEndpoints.cs", indent: 2, change: "m", stat: { plus: 11, minus: 2 } },
    { name: "Models/", indent: 1, dir: true, stat: "+31 / -0" },
    { name: "CliTypes.cs", indent: 2, change: "m", stat: { plus: 31, minus: 0 } },
    { name: "Services/Cli/", indent: 1, dir: true, stat: "+199 / -0" },
    { name: "SessionRegistry.cs", indent: 2, change: "m", stat: { plus: 83, minus: 0 } },
    { name: "SessionToJobIndex.cs", indent: 2, change: "a", stat: { plus: 116, minus: 0 } },
    { name: "Program.cs", indent: 2, change: "m", stat: { plus: 1, minus: 0 } },
    { name: "backend.Tests/", indent: 0, dir: true, stat: "+171 / -6" },
    { name: "OrchestratorChatProjectStateSnapshotTests.cs", indent: 1, change: "m", stat: { plus: 6, minus: 6 } },
    { name: "SessionToJobIndexTests.cs", indent: 1, change: "a", stat: { plus: 165, minus: 0 } },
  ];

  return (
    <>
      <div className="commit-strip">
        <span className="hash">8e8e658</span>
        <span className="msg">crash-recovery: orphan changes for implement-session-task-linkage-side-sheet-chip</span>
        <span className="stats">+413 / -8 · 12 files</span>
      </div>
      <div className="diff-tree">
        {files.map((f, i) => (
          <div key={i} className={`diff-row ${f.dir ? "dir" : ""}`}>
            <span className="indent" style={{ width: f.indent * 12 }}/>
            {f.dir && <Icon name="chevronDown" size={10}/>}
            {!f.dir && <span className={`change-letter ${f.change}`}>{f.change?.toUpperCase()}</span>}
            <span className="icn"><Icon name={f.dir ? "folder" : "file"} size={12}/></span>
            <span className="name">{f.name}</span>
            <span className="stat">
              {typeof f.stat === "string" ? (
                <span className="muted">{f.stat}</span>
              ) : (
                <>
                  <span className="plus">+{f.stat.plus}</span>
                  {f.stat.minus > 0 && <span className="muted"> </span>}
                  {f.stat.minus > 0 && <span className="minus">-{f.stat.minus}</span>}
                </>
              )}
            </span>
          </div>
        ))}
      </div>

      <div className="diff-file-head">
        <Icon name="file" size={12}/>
        <span>backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs</span>
        <span className="stats">LINE-BY-LINE</span>
      </div>
      <div className="diff-body">
        <div className="diff-line hunk"><span className="gutter">@@</span><span className="sign"></span><span className="code"> -40,7 +40,7 @@ public class OrchestratorChatProjectStateSnapshotTests</span></div>
        <div className="diff-line"><span className="gutter">40</span><span className="sign"></span><span className="code">    {`{`}</span></div>
        <div className="diff-line"><span className="gutter">41</span><span className="sign"></span><span className="code">      var sb = new StringBuilder();</span></div>
        <div className="diff-line"><span className="gutter">42</span><span className="sign"></span><span className="code">      sb.Append("Runbook");</span></div>
        <div className="diff-line del"><span className="gutter">43</span><span className="sign">-</span><span className="code">      OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook",</span></div>
        <div className="diff-line add"><span className="gutter">43</span><span className="sign">+</span><span className="code">      OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook",</span></div>
        <div className="diff-line"><span className="gutter">44</span><span className="sign"></span><span className="code">      var rendered = sb.ToString();</span></div>
        <div className="diff-line hunk"><span className="gutter">@@</span><span className="sign"></span><span className="code"> -50,7 +50,7 @@ public class OrchestratorChatProjectStateSnapshotTests</span></div>
        <div className="diff-line"><span className="gutter">50</span><span className="sign"></span><span className="code">      Assert.Contains("AUTHORITATIVE current state of \"Runbook\"",</span></div>
        <div className="diff-line"><span className="gutter">51</span><span className="sign"></span><span className="code">      public void Snapshot_EmptyProject_RendersNoTasksMarker()</span></div>
        <div className="diff-line"><span className="gutter">52</span><span className="sign"></span><span className="code">      {`{`}</span></div>
        <div className="diff-line del"><span className="gutter">53</span><span className="sign">-</span><span className="code">         OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook",</span></div>
        <div className="diff-line add"><span className="gutter">53</span><span className="sign">+</span><span className="code">         OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook",</span></div>
        <div className="diff-line"><span className="gutter">71</span><span className="sign"></span><span className="code">      var rendered = sb.ToString();</span></div>
        <div className="diff-line"><span className="gutter">72</span><span className="sign"></span><span className="code">      Assert.Contains("1-preparation: 1", rendered);</span></div>
        <div className="diff-line hunk"><span className="gutter">@@</span><span className="sign"></span><span className="code"> -91,7 +91,7 @@ public class OrchestratorChatProjectStateSnapshotTests</span></div>
        <div className="diff-line"><span className="gutter">91</span><span className="sign"></span><span className="code">      public void Snapshot_InstructsAgentToUseTheseExactNumbers()</span></div>
        <div className="diff-line del"><span className="gutter">93</span><span className="sign">-</span><span className="code">         OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook",</span></div>
        <div className="diff-line add"><span className="gutter">94</span><span className="sign">+</span><span className="code">         OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook",</span></div>
        <div className="diff-line"><span className="gutter">95</span><span className="sign"></span><span className="code">      Assert.Contains("Use these exact numbers", sb.ToString());</span></div>
        <div className="diff-line"><span className="gutter">96</span><span className="sign"></span><span className="code">      Assert.Contains("stale", sb.ToString());</span></div>
      </div>
    </>
  );
}

function DetailView({ task, panes, onOpenDiff, onOpenActivity }) {
  const visible = panes || { description: true, chat: true, commit: false };
  const [leftTab, setLeftTab] = useStateD("description"); // description | evidence
  const [chatW, setChatW] = useStateD(420);
  const [commitW, setCommitW] = useStateD(420);

  const chatRef = React.useRef(chatW);
  const commitRef = React.useRef(commitW);
  chatRef.current = chatW;
  commitRef.current = commitW;

  const startResize = (which) => (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startChat = chatRef.current;
    const startCommit = commitRef.current;
    const onMove = (ev) => {
      const dx = ev.clientX - startX;
      if (which === "chat") {
        setChatW(Math.max(280, Math.min(900, startChat - dx)));
      } else {
        setCommitW(Math.max(280, Math.min(900, startCommit - dx)));
      }
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

  const Left_ = visible.description && (
    <div className="det-pane" key="left">
      <div className="det-pane-head pane-tabs-head">
        <div className="pane-tabs">
          <button
            className={`pane-tab ${leftTab === "description" ? "active" : ""}`}
            onClick={() => setLeftTab("description")}
          ><Icon name="book" size={12}/>Description</button>
          <button
            className={`pane-tab ${leftTab === "evidence" ? "active" : ""}`}
            onClick={() => setLeftTab("evidence")}
          ><Icon name="check" size={12}/>Evidence <span className="pane-tab-badge">5</span></button>
        </div>
        <span className="spacer"/>
        {leftTab === "description" && <span className="dim mono" style={{ fontSize: 10, marginRight: 6 }}>#{task.id}</span>}
      </div>
      <div className="det-pane-body">
        {leftTab === "description" ? <Description task={task}/> : <Evidence task={task}/>}
      </div>
    </div>
  );

  const Chat_ = visible.chat && (
    <div className="det-pane" key="chat" style={{ width: chatW }}>
      <div className="det-pane-head">
        <span className="ttl">Chat</span>
        <span className="badge live">session running</span>
        <span className="badge">344 · 95t</span>
        <span className="spacer"/>
        <button className="btn-ghost" data-tip="Open Activity / Debug (full view)" onClick={() => onOpenActivity && onOpenActivity(task.id)} style={{ padding: "2px 8px", fontSize: 10, marginRight: 4 }}>
          <Icon name="expand" size={10}/> Debug
        </button>
        <button className="ic-btn" data-tip="Refresh" style={{ width: 22, height: 22, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}><Icon name="refresh" size={12}/></button>
        <button className="ic-btn" data-tip="More" style={{ width: 22, height: 22, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}><Icon name="more" size={12}/></button>
      </div>
      <ProtocolStrip/>
      <div style={{ flex: 1, overflow: "auto", minHeight: 0 }}>
        <ActivityLog task={task}/>
      </div>
      <Composer/>
    </div>
  );

  const Commit_ = visible.commit && (
    <div className="det-pane" key="commit" style={{ width: commitW }}>
      <div className="det-pane-head">
        <span className="ttl">Commit</span>
        <span className="badge" style={{ background: "rgba(78,201,176,0.12)", color: "var(--accent-2)" }}>● 8e8e658</span>
        <span className="spacer"/>
        <button className="btn-ghost" data-tip="Open full diff view" onClick={() => onOpenDiff && onOpenDiff("8e8e658")} style={{ padding: "2px 8px", fontSize: 10, marginRight: 4 }}>
          <Icon name="expand" size={10}/> Full diff
        </button>
        <button className="ic-btn" data-tip="Diff view" onClick={() => onOpenDiff && onOpenDiff("8e8e658")} style={{ width: 22, height: 22, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}><Icon name="diff" size={12}/></button>
      </div>
      <div className="det-pane-body"><CommitPanel task={task}/></div>
    </div>
  );

  // Build ordered list of visible panes + resize handles
  const items = [];
  if (Left_) items.push({ key: "left", node: Left_, kind: "description" });
  if (Chat_) items.push({ key: "chat", node: Chat_, kind: "chat" });
  if (Commit_) items.push({ key: "commit", node: Commit_, kind: "commit" });

  const children = [];
  items.forEach((it, i) => {
    const node = React.cloneElement(it.node, {
      className: `${it.node.props.className} ${i === 0 ? "grow" : "fixed"}`.trim(),
      style: i === 0 ? null : it.node.props.style,
    });
    if (i > 0) {
      const which = it.kind === "commit" ? "commit" : "chat";
      children.push(<div className="det-resize" key={`r-${i}`} onMouseDown={startResize(which)} title="Drag to resize"/>);
    }
    children.push(node);
  });

  return <div className="detail">{children}</div>;
}

window.DetailView = DetailView;
