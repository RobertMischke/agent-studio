# Connection health

**No supervising system.** Every actor keeps itself stable (systemd for processes, reconnect-with-backoff for links). The Task Server passively records *last-seen* per actor; the Deck shows it. "Runner silent for 4 minutes" is a display, not an action.

Why no leader: active mutual supervision buys ping-pong and split-brain questions that a management layer does not need. The truth already exists (leases, last-seen); making it visible is enough — humans or the Engine can escalate on top of visible staleness.

**Interim, while the tunnel lives:** the supervised tunnel unit (autossh, `ExitOnForwardFailure`, keep-alives) plus a one-minute health probe from the host (`curl 15031` → restart on red). The tunnel maps host `15031` → studio `5031`; it disappears entirely with the migration — the whole zombie-listener class of 23–24 Jul is a property of the interim, not of the target.
