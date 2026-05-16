/* eslint-disable */
// Fullscreen Activity View — opens as its own editor tab when the pane is too cramped.
// Left: runs (timeline of orchestrator runs). Right: detail of the selected run.

const { useState: useStateAV, useRef: useRefAV } = React;

const RUNS = [
  {
    id: "run-3",
    n: 3,
    cli: "claude",
    model: "Claude Opus 4.7",
    state: "live",
    started: "14:13:43",
    elapsed: "2m 14s",
    tokens: "11.2k",
    summary: "Reissue iteration — debounce + Playwright deferred",
  },
  {
    id: "run-2",
    n: 2,
    cli: "claude",
    model: "Claude Opus 4.7",
    state: "blocked",
    started: "13:42:01",
    elapsed: "26m 13s",
    tokens: "94.8k",
    summary: "Implementation attempt — 3 aspects flagged block",
    verdict: { aspect: "multi-aspect block", note: "requirement-fit, code-quality, tests-and-evidence" },
  },
  {
    id: "run-1",
    n: 1,
    cli: "claude",
    model: "Claude Sonnet 4",
    state: "ok",
    started: "11:01:18",
    elapsed: "08m 04s",
    tokens: "32.4k",
    summary: "Plan + scaffolding (Phase 0)",
  },
];

const RUN_DETAIL = {
  "run-2": {
    decision: {
      verdict: "reissue",
      aspects: [
        { key: "requirement-fit", verdict: "block", note: "Chip renders, but does not react to JobChanged events. Index must invalidate on events, not just on load." },
        { key: "code-quality", verdict: "block", note: "SessionToJobIndex rebuilds on every event — debounce or batch invalidation required." },
        { key: "documentation-impact", verdict: "concerns", note: "Plan section 1.3 should mention the debounce decision." },
        { key: "tests-and-evidence", verdict: "block", note: "Playwright spec session-task-link-chip.spec.ts type-checks but was never executed. Add to stable's Playwright pass." },
      ],
      decidedAt: "14:13:43",
      decidedBy: "Orchestrator (Codex GPT-5)",
    },
    steps: [
      { time: "14:13:43", who: "Orchestrator", kind: "decision", text: "Decision: reissue (multi-aspect block). Aspects: requirement-fit=block, code-quality=block, documentation-impact=concerns, tests-and-evidence=block" },
      { time: "14:09:12", who: "Claude", kind: "step", text: "Frontend unit suite: cli-usage-sheet.spec.ts still passes (1/1) — no regression on the legacy panel." },
      { time: "14:08:01", who: "Claude", kind: "step", text: "Playwright spec: session-task-link-chip.spec.ts type-checks and discovers cleanly; execution is reserved for stable's Playwright pass per the dev-backend-lifecycle rule." },
      { time: "14:07:30", who: "Claude", kind: "step", text: "Git: clean, on main, in sync with origin/main." },
      { time: "14:06:08", who: "Claude", kind: "step", text: "Backend dotnet test → 282 / 282 passed in 14.2s." },
      { time: "14:01:42", who: "Claude", kind: "tool", text: "Edited backend/Services/Cli/SessionToJobIndex.cs (+116)" },
      { time: "14:00:15", who: "Claude", kind: "tool", text: "Read agent-taskboard/7-archive/.../results/session-task-linkage-plan.md" },
      { time: "13:58:09", who: "Claude", kind: "tool", text: "Read backend/Models/CliTypes.cs (+ 4 referenced files)" },
      { time: "13:55:21", who: "Claude", kind: "step", text: "Picked up job #1. Reading plan and task contract." },
      { time: "13:54:50", who: "Orchestrator", kind: "system", text: "Routed job #1 to Claude Opus 4.7. Token budget reserved: 96k." },
      { time: "13:42:01", who: "Orchestrator", kind: "system", text: "Run started." },
    ],
  },
};

const ASPECT_COLOR = {
  accept: "var(--accent-2)",
  concerns: "var(--accent-warn)",
  block: "var(--accent-6)",
};

const KIND_COLOR = {
  decision: "var(--accent)",
  step: "var(--fg-dim)",
  tool: "var(--accent-3)",
  system: "var(--fg-muted)",
};

function RunCard({ run, active, onClick }) {
  return (
    <div className={`run-card ${active ? "active" : ""} run-state-${run.state}`} onClick={onClick}>
      <div className="run-card-head">
        <span className={`run-state-pip run-state-${run.state}`}/>
        <span className="run-card-n">RUN {run.n}</span>
        <span className="run-card-time mono">{run.started}</span>
      </div>
      <div className="run-card-cli">
        <span className="cli-glyph sm" style={{ background: "var(--accent)" }}>{run.cli[0].toUpperCase()}</span>
        <span className="run-card-model">{run.model}</span>
      </div>
      <div className="run-card-summary">{run.summary}</div>
      <div className="run-card-meta mono">
        <span>{run.elapsed}</span>
        <span>·</span>
        <span>{run.tokens} tok</span>
        {run.state === "live" && <><span>·</span><span style={{ color: "var(--accent-2)" }}>● live</span></>}
        {run.state === "blocked" && <><span>·</span><span style={{ color: "var(--accent-6)" }}>blocked</span></>}
        {run.state === "ok" && <><span>·</span><span style={{ color: "var(--accent-2)" }}>ok</span></>}
      </div>
    </div>
  );
}

function VerdictBlock({ decision }) {
  if (!decision) return null;
  return (
    <div className="run-verdict">
      <div className="run-verdict-head">
        <span className={`run-verdict-tag ${decision.verdict}`}>{decision.verdict.toUpperCase()}</span>
        <span className="run-verdict-by">decided by {decision.decidedBy}</span>
        <span className="spc"/>
        <span className="mono muted">{decision.decidedAt}</span>
      </div>
      <div className="run-aspects">
        {decision.aspects.map(a => (
          <div key={a.key} className={`run-aspect ${a.verdict}`}>
            <span className="run-aspect-key mono">{a.key}</span>
            <span className={`run-aspect-verdict ${a.verdict}`}>{a.verdict}</span>
            <div className="run-aspect-note">{a.note}</div>
          </div>
        ))}
      </div>
    </div>
  );
}

function RunDetail({ run }) {
  const d = RUN_DETAIL[run.id] || { steps: [] };
  return (
    <>
      <div className="run-detail-head">
        <span className={`run-state-pip run-state-${run.state}`}/>
        <div className="run-detail-title">
          <div className="run-detail-name">Run {run.n} · {run.model}</div>
          <div className="run-detail-meta mono">
            started {run.started} · {run.elapsed} · {run.tokens} tok
          </div>
        </div>
        <span className="spc"/>
        <div className="run-detail-actions">
          <button className="btn-ghost" data-tip="Copy full transcript"><Icon name="file" size={11}/> Copy</button>
          {run.state === "live" && <button className="btn-ghost" data-tip="Stop this run" style={{ color: "var(--accent-6)" }}>Stop</button>}
        </div>
      </div>

      {d.decision && <VerdictBlock decision={d.decision}/>}

      <div className="run-tabs">
        <button className="run-tab active">Timeline <span className="run-tab-count">{d.steps.length}</span></button>
        <button className="run-tab">Tool calls <span className="run-tab-count">{d.steps.filter(s => s.kind === "tool").length}</span></button>
        <button className="run-tab">Token cost</button>
        <button className="run-tab">Raw transcript</button>
        <span className="spc"/>
        <label className="run-filter"><input type="checkbox" defaultChecked/> Show tool calls</label>
      </div>

      <div className="run-timeline">
        {d.steps.map((s, i) => (
          <div key={i} className={`run-step run-step-${s.kind}`}>
            <div className="run-step-rail">
              <span className="run-step-dot" style={{ background: KIND_COLOR[s.kind] }}/>
              <span className="run-step-line"/>
            </div>
            <div className="run-step-body">
              <div className="run-step-head">
                <span className={`run-step-who run-step-who-${s.kind}`}>{s.who}</span>
                {s.kind !== "step" && <span className="run-step-kind">{s.kind}</span>}
                <span className="mono muted" style={{ marginLeft: "auto", fontSize: 11 }}>{s.time}</span>
              </div>
              <div className="run-step-text">{s.text}</div>
            </div>
          </div>
        ))}
      </div>
    </>
  );
}

function ActivityFullView({ taskId }) {
  const task = window.MOCK.TASKS.find(t => t.id === taskId) || window.MOCK.TASKS[0];
  const [selectedRun, setSelectedRun] = useStateAV(RUNS[1].id);
  const [sideW, setSideW] = useStateAV(300);
  const sideRef = useRefAV(sideW);
  sideRef.current = sideW;

  const startResize = (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = sideRef.current;
    const onMove = (ev) => {
      const dx = ev.clientX - startX;
      setSideW(Math.max(220, Math.min(480, startW + dx)));
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

  const run = RUNS.find(r => r.id === selectedRun) || RUNS[0];

  return (
    <div className="activity-full">
      <div className="activity-full-bar">
        <span className="mono" style={{ color: "var(--fg-muted)" }}>#{task.id}</span>
        <span style={{ color: "var(--fg-strong)", fontWeight: 600, fontSize: 13 }}>{task.title}</span>
        <span className="spc"/>
        <span className="tg label">{RUNS.length} runs</span>
        <button className="btn-ghost" style={{ padding: "3px 10px", fontSize: 11 }}><Icon name="refresh" size={11}/> Reload</button>
      </div>
      <div className="activity-full-body">
        <div className="activity-runs" style={{ width: sideW }}>
          <div className="activity-runs-head">
            <span>Runs</span>
            <span className="count">{RUNS.length}</span>
          </div>
          <div className="activity-runs-list">
            {RUNS.map(r => (
              <RunCard key={r.id} run={r} active={selectedRun === r.id} onClick={() => setSelectedRun(r.id)}/>
            ))}
          </div>
        </div>
        <div className="activity-resize" onMouseDown={startResize}/>
        <div className="activity-detail">
          <RunDetail run={run}/>
        </div>
      </div>
    </div>
  );
}

window.ActivityFullView = ActivityFullView;
