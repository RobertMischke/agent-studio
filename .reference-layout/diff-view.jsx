/* eslint-disable */
// Fullscreen diff view — opens as its own editor tab

const { useState: useStateDV, useRef: useRefDV } = React;

const DIFF_FILES = [
  { path: "backend/Endpoints/CliEndpoints.cs", change: "m", plus: 11, minus: 2 },
  { path: "backend/Models/CliTypes.cs", change: "m", plus: 31, minus: 0 },
  { path: "backend/Services/Cli/SessionRegistry.cs", change: "m", plus: 83, minus: 0 },
  { path: "backend/Services/Cli/SessionToJobIndex.cs", change: "a", plus: 116, minus: 0 },
  { path: "backend/Program.cs", change: "m", plus: 1, minus: 0 },
  { path: "backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs", change: "m", plus: 6, minus: 6 },
  { path: "backend.Tests/SessionToJobIndexTests.cs", change: "a", plus: 165, minus: 0 },
  { path: "frontend/src/app/models/job.model.ts", change: "m", plus: 4, minus: 0 },
  { path: "frontend/src/app/features/cli/components/cli-usage-sheet.ts", change: "m", plus: 18, minus: 2 },
  { path: "frontend/src/app/features/cli/components/cli-usage-sheet.html", change: "m", plus: 12, minus: 0 },
  { path: "frontend/src/app/features/cli/cli-usage-sheet.spec.ts", change: "m", plus: 2, minus: 0 },
  { path: "frontend/e2e/session-task-link-chip.spec.ts", change: "a", plus: 86, minus: 0 },
];

const DIFF_BODIES = {
  default: [
    { type: "hunk", text: " -40,7 +40,7 @@ public class OrchestratorChatProjectStateSnapshotTests" },
    { type: "ctx", n: [40, 40], text: "{" },
    { type: "ctx", n: [41, 41], text: "  var sb = new StringBuilder();" },
    { type: "ctx", n: [42, 42], text: '  sb.Append("Runbook");' },
    { type: "del", n: [43, null], text: '  OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook",' },
    { type: "add", n: [null, 43], text: '  OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook",' },
    { type: "ctx", n: [44, 44], text: "  var rendered = sb.ToString();" },
    { type: "ctx", n: [45, 45], text: "}" },
    { type: "hunk", text: " -50,7 +50,7 @@ public class OrchestratorChatProjectStateSnapshotTests" },
    { type: "ctx", n: [50, 50], text: '  Assert.Contains("AUTHORITATIVE current state of \\"Runbook\\"",' },
    { type: "ctx", n: [51, 51], text: "    rendered);" },
    { type: "ctx", n: [52, 52], text: "}" },
    { type: "ctx", n: [53, 53], text: "" },
    { type: "ctx", n: [54, 54], text: "[Fact]" },
    { type: "ctx", n: [55, 55], text: "public void Snapshot_EmptyProject_RendersNoTasksMarker()" },
    { type: "ctx", n: [56, 56], text: "{" },
    { type: "ctx", n: [57, 57], text: "  var sb = new StringBuilder();" },
    { type: "del", n: [58, null], text: '  OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook",' },
    { type: "add", n: [null, 58], text: '  OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook",' },
    { type: "ctx", n: [59, 59], text: "    new ProjectState(\"Runbook\", new Dictionary<string, int>()));" },
    { type: "ctx", n: [60, 60], text: "  var rendered = sb.ToString();" },
    { type: "ctx", n: [61, 61], text: '  Assert.Contains("1-preparation: 0", rendered);' },
    { type: "hunk", text: " -91,7 +91,7 @@ public class OrchestratorChatProjectStateSnapshotTests" },
    { type: "ctx", n: [91, 91], text: "[Fact]" },
    { type: "ctx", n: [92, 92], text: "public void Snapshot_InstructsAgentToUseTheseExactNumbers()" },
    { type: "ctx", n: [93, 93], text: "{" },
    { type: "del", n: [94, null], text: '  OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook",' },
    { type: "add", n: [null, 94], text: '  OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook",' },
    { type: "ctx", n: [95, 95], text: '  Assert.Contains("Use these exact numbers", sb.ToString());' },
    { type: "ctx", n: [96, 96], text: '  Assert.Contains("stale", sb.ToString());' },
    { type: "ctx", n: [97, 97], text: "}" },
  ],
  "backend/Services/Cli/SessionToJobIndex.cs": [
    { type: "hunk", text: " new file +116,0 @@ SessionToJobIndex.cs" },
    { type: "add", n: [null, 1], text: "using System.Collections.Concurrent;" },
    { type: "add", n: [null, 2], text: "using Backend.Models;" },
    { type: "add", n: [null, 3], text: "" },
    { type: "add", n: [null, 4], text: "namespace Backend.Services.Cli;" },
    { type: "add", n: [null, 5], text: "" },
    { type: "add", n: [null, 6], text: "/// <summary>" },
    { type: "add", n: [null, 7], text: "/// In-memory map from session id to the kanban job that owns it." },
    { type: "add", n: [null, 8], text: "/// Rebuilt from JobScannerService.ScanAllJobs() and each job's" },
    { type: "add", n: [null, 9], text: "/// SessionChain. Invalidated on JobChanged events." },
    { type: "add", n: [null, 10], text: "/// </summary>" },
    { type: "add", n: [null, 11], text: "public sealed class SessionToJobIndex" },
    { type: "add", n: [null, 12], text: "{" },
    { type: "add", n: [null, 13], text: "    private readonly ConcurrentDictionary<string, LinkedJob> _map = new();" },
    { type: "add", n: [null, 14], text: "    private readonly JobScannerService _scanner;" },
    { type: "add", n: [null, 15], text: "    private readonly ILogger<SessionToJobIndex> _log;" },
    { type: "add", n: [null, 16], text: "" },
    { type: "add", n: [null, 17], text: "    public SessionToJobIndex(JobScannerService scanner, ILogger<SessionToJobIndex> log)" },
    { type: "add", n: [null, 18], text: "    {" },
    { type: "add", n: [null, 19], text: "        _scanner = scanner;" },
    { type: "add", n: [null, 20], text: "        _log = log;" },
    { type: "add", n: [null, 21], text: "    }" },
    { type: "add", n: [null, 22], text: "" },
    { type: "add", n: [null, 23], text: "    public LinkedJob? TryGet(string sessionId, string? watchPath)" },
    { type: "add", n: [null, 24], text: "    {" },
    { type: "add", n: [null, 25], text: "        if (string.IsNullOrEmpty(sessionId)) return null;" },
    { type: "add", n: [null, 26], text: "        if (!_map.TryGetValue(sessionId, out var job)) return null;" },
    { type: "add", n: [null, 27], text: "        // Tie-break on WatchPath equality with the session row's cwd" },
    { type: "add", n: [null, 28], text: "        if (watchPath is not null && job.WatchPath != watchPath) return null;" },
    { type: "add", n: [null, 29], text: "        return job;" },
    { type: "add", n: [null, 30], text: "    }" },
  ],
};

function fileIcon(path) {
  if (path.endsWith(".cs")) return { letter: "C#", color: "#a14fa9" };
  if (path.endsWith(".ts")) return { letter: "TS", color: "#3178c6" };
  if (path.endsWith(".html")) return { letter: "<>", color: "#e44d26" };
  if (path.endsWith(".spec.ts")) return { letter: "T", color: "#cca700" };
  return { letter: "•", color: "#9d9d9d" };
}

function DiffFullView({ commit = "8e8e658", message = "crash-recovery: orphan changes for implement-session-task-linkage-side-sheet-chip", onCloseTab }) {
  const [selected, setSelected] = useStateDV(DIFF_FILES[3].path);
  const [sideW, setSideW] = useStateDV(320);
  const [mode, setMode] = useStateDV("unified"); // unified | split
  const [search, setSearch] = useStateDV("");
  const sideRef = useRefDV(sideW);
  sideRef.current = sideW;

  const startResize = (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = sideRef.current;
    const onMove = (ev) => {
      const dx = ev.clientX - startX;
      setSideW(Math.max(220, Math.min(560, startW + dx)));
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

  const totalPlus = DIFF_FILES.reduce((a, b) => a + b.plus, 0);
  const totalMinus = DIFF_FILES.reduce((a, b) => a + b.minus, 0);

  const body = DIFF_BODIES[selected] || DIFF_BODIES.default;

  const filtered = DIFF_FILES.filter(f => !search || f.path.toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="diff-full">
      <div className="diff-full-bar">
        <span className="diff-full-hash mono">{commit}</span>
        <span className="diff-full-msg">{message}</span>
        <span className="diff-full-stats mono">
          <span style={{ color: "var(--accent-2)" }}>+{totalPlus}</span>
          <span className="muted"> / </span>
          <span style={{ color: "var(--accent-6)" }}>-{totalMinus}</span>
          <span className="muted"> · {DIFF_FILES.length} files</span>
        </span>
        <span className="spc"/>
        <div className="diff-mode-toggle">
          <button className={mode === "unified" ? "active" : ""} onClick={() => setMode("unified")}>Unified</button>
          <button className={mode === "split" ? "active" : ""} onClick={() => setMode("split")}>Split</button>
        </div>
        <button className="btn-ghost" style={{ padding: "3px 10px", fontSize: 11 }}>
          <Icon name="branch" size={11}/> View on git
        </button>
        <button className="btn-ghost" style={{ padding: "3px 10px", fontSize: 11 }}>
          <Icon name="check" size={11}/> Accept
        </button>
      </div>

      <div className="diff-full-body">
        <div className="diff-side" style={{ width: sideW }}>
          <div className="diff-side-head">
            <Icon name="file" size={12}/>
            <span>Changed files</span>
            <span className="count">{DIFF_FILES.length}</span>
          </div>
          <div className="diff-side-search">
            <Icon name="search" size={11}/>
            <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Filter…"/>
          </div>
          <div className="diff-side-list">
            {filtered.map((f, i) => {
              const ic = fileIcon(f.path);
              const dir = f.path.substring(0, f.path.lastIndexOf("/"));
              const file = f.path.substring(f.path.lastIndexOf("/") + 1);
              return (
                <div
                  key={i}
                  className={`diff-side-row ${selected === f.path ? "active" : ""}`}
                  onClick={() => setSelected(f.path)}
                >
                  <span className={`change-letter ${f.change}`}>{f.change.toUpperCase()}</span>
                  <span className="fi" style={{ background: ic.color }}>{ic.letter}</span>
                  <div className="diff-side-name">
                    <div className="filename">{file}</div>
                    <div className="filedir">{dir}</div>
                  </div>
                  <span className="diff-side-stat mono">
                    <span style={{ color: "var(--accent-2)" }}>+{f.plus}</span>
                    {f.minus > 0 && <span style={{ color: "var(--accent-6)", marginLeft: 4 }}>-{f.minus}</span>}
                  </span>
                </div>
              );
            })}
          </div>
          <div className="diff-side-foot">
            <button className="btn-ghost" style={{ flex: 1, justifyContent: "center", padding: "5px 10px", fontSize: 11 }}>
              <Icon name="check" size={11}/> Mark all reviewed
            </button>
          </div>
        </div>
        <div className="diff-resize" onMouseDown={startResize}/>
        <div className="diff-main">
          <div className="diff-main-head">
            <span className="fi" style={{ background: fileIcon(selected).color }}>{fileIcon(selected).letter}</span>
            <span className="diff-main-path mono">{selected}</span>
            <span className="spc"/>
            <span className="diff-main-stats mono">
              {(() => { const f = DIFF_FILES.find(x => x.path === selected); return f ? <><span style={{ color: "var(--accent-2)" }}>+{f.plus}</span> <span style={{ color: "var(--accent-6)" }}>-{f.minus}</span></> : null; })()}
            </span>
            <button className="ic-btn" title="Previous file" style={{ width: 24, height: 24, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}><Icon name="chevronLeft" size={13}/></button>
            <button className="ic-btn" title="Next file" style={{ width: 24, height: 24, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}><Icon name="chevronRight" size={13}/></button>
            <button className="ic-btn" title="Open file" style={{ width: 24, height: 24, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}><Icon name="file" size={13}/></button>
          </div>
          {mode === "unified" ? (
            <div className="diff-main-body unified">
              {body.map((l, i) => (
                <div key={i} className={`diff-line ${l.type}`}>
                  <span className="gutter">{l.n?.[0] ?? ""}</span>
                  <span className="gutter">{l.n?.[1] ?? ""}</span>
                  <span className="sign">{l.type === "add" ? "+" : l.type === "del" ? "-" : l.type === "hunk" ? "@@" : ""}</span>
                  <span className="code">{l.text}</span>
                </div>
              ))}
            </div>
          ) : (
            <div className="diff-main-body split">
              <div className="diff-split-col">
                <div className="diff-split-head">Before</div>
                {body.map((l, i) => {
                  const cls = l.type === "del" ? "del" : l.type === "hunk" ? "hunk" : l.type === "add" ? "empty" : "ctx";
                  return (
                    <div key={i} className={`diff-line ${cls}`}>
                      <span className="gutter">{l.n?.[0] ?? ""}</span>
                      <span className="sign">{l.type === "del" ? "-" : l.type === "hunk" ? "@@" : ""}</span>
                      <span className="code">{l.type === "add" ? "" : l.text}</span>
                    </div>
                  );
                })}
              </div>
              <div className="diff-split-col">
                <div className="diff-split-head">After</div>
                {body.map((l, i) => {
                  const cls = l.type === "add" ? "add" : l.type === "hunk" ? "hunk" : l.type === "del" ? "empty" : "ctx";
                  return (
                    <div key={i} className={`diff-line ${cls}`}>
                      <span className="gutter">{l.n?.[1] ?? ""}</span>
                      <span className="sign">{l.type === "add" ? "+" : l.type === "hunk" ? "@@" : ""}</span>
                      <span className="code">{l.type === "del" ? "" : l.text}</span>
                    </div>
                  );
                })}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

window.DiffFullView = DiffFullView;
