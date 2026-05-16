/* eslint-disable */
// Project chat panel — right side, collapsible

const { useState: useStateC, useRef: useRefC, useEffect: useEffectC } = React;

const CHAT_MESSAGES = [
  {
    who: "orchestrator",
    name: "Orchestrator",
    time: "08:42",
    text: "Good morning. 26 jobs in flight; 8 in Auto Review and 1 currently running. Claude Code is at 92% quota — resets in 5h 12m. Want me to pause auto-assignment to Claude until then?",
  },
  {
    who: "user",
    name: "Anna B",
    time: "08:43",
    text: "Yes pause Claude. Route reviews through Codex for now.",
  },
  {
    who: "orchestrator",
    name: "Orchestrator",
    time: "08:43",
    text: "Routing updated. Codex picks up the next 4 reissues. I'll surface anything that needs a human verdict in this thread.",
  },
  {
    who: "agent",
    name: "Codex",
    time: "09:11",
    text: "Reissued #3 (DELETE-Button task) escalated — the latest run touched 3 files but the orchestrator can't confirm requirement-fit. Note added to the task.",
    ref: { num: "#3", title: "Bug: DELETE-Button auf den Task-Karten…" },
  },
  {
    who: "user",
    name: "Anna B",
    time: "09:14",
    text: "Open #1 — what's blocking the linkage chip?",
  },
  {
    who: "agent",
    name: "Claude",
    time: "09:14",
    text: "Three aspects flagged block:\n• requirement-fit — the chip is rendered but doesn't react to JobChanged events.\n• code-quality — the index rebuilds on every event; needs debouncing.\n• tests-and-evidence — Playwright spec discovered but not executed.",
    ref: { num: "#1", title: "Implement session-task linkage chip…" },
  },
  {
    who: "user",
    name: "Anna B",
    time: "09:15",
    text: "Got it. Steer Claude: debounce 150ms, keep Playwright deferred. Reissue.",
  },
];

function ChatMessage({ m }) {
  return (
    <div className={`chat-msg chat-msg-${m.who}`}>
      <div className="chat-msg-head">
        <span className={`chat-avatar chat-avatar-${m.who}`}>
          {m.who === "user" ? "AB" : m.who === "orchestrator" ? "⚙" : m.name[0]}
        </span>
        <span className="chat-name">{m.name}</span>
        <span className="chat-time">{m.time}</span>
      </div>
      {m.ref && (
        <div className="chat-ref">
          <Icon name="list" size={11} color="var(--accent)"/>
          <span className="mono" style={{ color: "var(--accent)" }}>{m.ref.num}</span>
          <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{m.ref.title}</span>
        </div>
      )}
      <div className="chat-text">{m.text.split("\n").map((line, i) => <div key={i}>{line}</div>)}</div>
    </div>
  );
}

function ChatPanel({ onClose, project, onResize }) {
  const scrollRef = useRefC(null);
  useEffectC(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, []);

  return (
    <div className="chat-panel">
      <div className="chat-resize" onMouseDown={onResize} title="Drag to resize"/>
      <div className="chat-head">
        <span className="chat-head-icon" style={{ background: "var(--accent)" }}>⚙</span>
        <div className="chat-head-text">
          <div className="chat-head-title">Project Chat</div>
          <div className="chat-head-sub">{project} · with Orchestrator</div>
        </div>
        <span className="spc"/>
        <button className="ic-btn" title="History" style={{ width: 24, height: 24, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}>
          <Icon name="archive" size={14}/>
        </button>
        <button className="ic-btn" title="Settings" style={{ width: 24, height: 24, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}>
          <Icon name="more" size={14}/>
        </button>
        <button className="ic-btn" title="Close chat" onClick={onClose} style={{ width: 24, height: 24, display: "grid", placeItems: "center", color: "var(--fg-dim)", borderRadius: 3 }}>
          <Icon name="close" size={14}/>
        </button>
      </div>

      <div className="chat-meta-strip">
        <span className="chat-pill" style={{ background: "rgba(78,201,176,0.14)", color: "var(--accent-2)" }}>
          <span style={{ width: 6, height: 6, borderRadius: "50%", background: "var(--accent-2)" }}/>
          orchestrator listening
        </span>
        <span style={{ marginLeft: "auto", fontSize: 10, color: "var(--fg-muted)" }}>{CHAT_MESSAGES.length} messages</span>
      </div>

      <div className="chat-list" ref={scrollRef}>
        <div className="chat-day-divider">
          <span>Today</span>
        </div>
        {CHAT_MESSAGES.map((m, i) => <ChatMessage key={i} m={m}/>)}
      </div>

      <div className="chat-composer">
        <div className="chat-context-chips">
          <button className="chat-chip" title="Reference a task">
            <Icon name="list" size={10}/> #
          </button>
          <button className="chat-chip" title="Reference a file">
            <Icon name="file" size={10}/> @
          </button>
          <button className="chat-chip" title="Reference a commit">
            <Icon name="branch" size={10}/> ⌥
          </button>
          <span style={{ marginLeft: "auto", fontSize: 10, color: "var(--fg-muted)" }}>routing: Codex (Claude paused)</span>
        </div>
        <textarea placeholder="Ask the orchestrator anything about this project… (⌃⏎ to send)"/>
        <div className="chat-send-row">
          <span className="hint">
            <span className="kbd-hint">⌃⏎</span> send · <span className="kbd-hint">/</span> command
          </span>
          <button className="btn-primary"><Icon name="send" size={11} color="#1a1a1a"/> Send</button>
        </div>
      </div>
    </div>
  );
}

window.ChatPanel = ChatPanel;

function ChatRail({ unread, onOpen }) {
  return (
    <div className="chat-rail" onClick={onOpen} role="button" title="Open project chat">
      <div className="chat-rail-icon">
        <Icon name="cli" size={16}/>
        {unread > 0 && <span className="chat-rail-badge">{unread}</span>}
      </div>
      <div className="chat-rail-label">PROJECT CHAT</div>
      <div className="chat-rail-icon" style={{ opacity: 0.5 }}>
        <Icon name="chevronLeft" size={14}/>
      </div>
    </div>
  );
}

window.ChatRail = ChatRail;
