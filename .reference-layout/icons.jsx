// Inline SVG icons — VS-Code-style line icons (24/16 grid)
const Icon = ({ name, size = 16, color = "currentColor", strokeWidth = 1.5 }) => {
  const props = { width: size, height: size, viewBox: "0 0 24 24", fill: "none", stroke: color, strokeWidth, strokeLinecap: "round", strokeLinejoin: "round" };
  const paths = {
    folder: <><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></>,
    list: <><line x1="8" y1="6" x2="20" y2="6"/><line x1="8" y1="12" x2="20" y2="12"/><line x1="8" y1="18" x2="20" y2="18"/><circle cx="4" cy="6" r="1"/><circle cx="4" cy="12" r="1"/><circle cx="4" cy="18" r="1"/></>,
    filter: <><path d="M4 5h16l-6 8v6l-4 2v-8z"/></>,
    search: <><circle cx="11" cy="11" r="7"/><line x1="20" y1="20" x2="16.5" y2="16.5"/></>,
    cli: <><rect x="3" y="4" width="18" height="16" rx="2"/><polyline points="7 10 10 13 7 16"/><line x1="13" y1="16" x2="17" y2="16"/></>,
    activity: <><polyline points="4 12 8 12 11 5 13 19 16 12 20 12"/></>,
    runbook: <><circle cx="12" cy="12" r="8"/><polygon points="10 8 16 12 10 16 10 8" fill={color}/></>,
    settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1A2 2 0 1 1 4.3 17l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1A1.7 1.7 0 0 0 4.6 9 1.7 1.7 0 0 0 4.3 7.2L4.2 7A2 2 0 1 1 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6 1.7 1.7 0 0 0 10 3.1V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1A2 2 0 1 1 19.7 7l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></>,
    chevronRight: <><polyline points="9 6 15 12 9 18"/></>,
    chevronDown: <><polyline points="6 9 12 15 18 9"/></>,
    chevronLeft: <><polyline points="15 6 9 12 15 18"/></>,
    close: <><line x1="6" y1="6" x2="18" y2="18"/><line x1="18" y1="6" x2="6" y2="18"/></>,
    plus: <><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></>,
    dot: <><circle cx="12" cy="12" r="4" fill={color} stroke="none"/></>,
    check: <><polyline points="5 12 10 17 19 7"/></>,
    expand: <><polyline points="15 3 21 3 21 9"/><polyline points="9 21 3 21 3 15"/><line x1="21" y1="3" x2="14" y2="10"/><line x1="3" y1="21" x2="10" y2="14"/></>,
    collapse: <><polyline points="4 14 10 14 10 20"/><polyline points="20 10 14 10 14 4"/><line x1="14" y1="10" x2="21" y2="3"/><line x1="3" y1="21" x2="10" y2="14"/></>,
    play: <><polygon points="6 4 20 12 6 20 6 4" fill={color} stroke="none"/></>,
    pause: <><rect x="6" y="5" width="4" height="14" fill={color} stroke="none"/><rect x="14" y="5" width="4" height="14" fill={color} stroke="none"/></>,
    branch: <><circle cx="6" cy="6" r="2"/><circle cx="6" cy="18" r="2"/><circle cx="18" cy="9" r="2"/><path d="M6 8v8"/><path d="M18 11v1a4 4 0 0 1-4 4H7"/></>,
    file: <><path d="M6 3h9l5 5v13a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"/><polyline points="14 3 14 9 20 9"/></>,
    refresh: <><polyline points="21 4 21 10 15 10"/><path d="M21 10a9 9 0 1 0-2.5 7.5"/></>,
    more: <><circle cx="6" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="18" cy="12" r="1"/></>,
    bell: <><path d="M6 8a6 6 0 0 1 12 0c0 7 3 8 3 8H3s3-1 3-8z"/><path d="M10 21a2 2 0 0 0 4 0"/></>,
    warn: <><path d="M12 3 2 21h20z"/><line x1="12" y1="10" x2="12" y2="15"/><circle cx="12" cy="18" r="0.5" fill={color}/></>,
    diff: <><polyline points="6 3 6 21"/><polyline points="18 3 18 21"/><line x1="3" y1="9" x2="9" y2="9"/><line x1="15" y1="15" x2="21" y2="15"/></>,
    book: <><path d="M4 5a2 2 0 0 1 2-2h13v18H6a2 2 0 0 0-2 2V5z"/><line x1="8" y1="8" x2="15" y2="8"/><line x1="8" y1="12" x2="15" y2="12"/></>,
    eye: <><path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/></>,
    panelLeft: <><rect x="3" y="4" width="18" height="16" rx="2"/><line x1="10" y1="4" x2="10" y2="20"/></>,
    panelRight: <><rect x="3" y="4" width="18" height="16" rx="2"/><line x1="14" y1="4" x2="14" y2="20"/></>,
    layout: <><rect x="3" y="4" width="18" height="16" rx="2"/><line x1="3" y1="10" x2="21" y2="10"/></>,
    sliders: <><line x1="4" y1="6" x2="20" y2="6"/><line x1="4" y1="12" x2="20" y2="12"/><line x1="4" y1="18" x2="20" y2="18"/><circle cx="8" cy="6" r="2" fill="#1e1e1e"/><circle cx="14" cy="12" r="2" fill="#1e1e1e"/><circle cx="10" cy="18" r="2" fill="#1e1e1e"/></>,
    bot: <><rect x="4" y="8" width="16" height="12" rx="2"/><line x1="12" y1="4" x2="12" y2="8"/><circle cx="9" cy="14" r="1" fill={color}/><circle cx="15" cy="14" r="1" fill={color}/></>,
    grid: <><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/></>,
    archive: <><polyline points="3 7 21 7 21 20 3 20 3 7"/><polyline points="3 7 5 3 19 3 21 7"/><line x1="10" y1="12" x2="14" y2="12"/></>,
    send: <><polyline points="3 12 21 4 13 21 11 13 3 12"/></>,
    sun: <><circle cx="12" cy="12" r="4"/><line x1="12" y1="3" x2="12" y2="5"/><line x1="12" y1="19" x2="12" y2="21"/><line x1="3" y1="12" x2="5" y2="12"/><line x1="19" y1="12" x2="21" y2="12"/><line x1="5.6" y1="5.6" x2="7" y2="7"/><line x1="17" y1="17" x2="18.4" y2="18.4"/><line x1="5.6" y1="18.4" x2="7" y2="17"/><line x1="17" y1="7" x2="18.4" y2="5.6"/></>,
    moon: <><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></>,
  };
  return <svg {...props}>{paths[name] || null}</svg>;
};

window.Icon = Icon;
