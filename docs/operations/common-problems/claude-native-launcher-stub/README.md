---
id: claude-native-launcher-stub
title: "claude CLI not available right after an auto-update"
status: fixed
first-seen: 2026-09-06T07:44:00Z
last-seen: 2026-09-06T16:32:00Z
severity: blocker
category: cli
tags: [claude, cli, npm, auto-update, launcher, windows, self-heal]
affects: [backend/Features/Cli/Pty, backend/Features/Cli/Quota, backend/Features/Cli/Repair]
related-tasks: [AGT-2706]
related-adrs: []
---

# claude CLI not available right after an auto-update

**Symptom.** On Windows, quota refreshes and model discovery report `claude CLI
not available` immediately after Claude Code auto-updates. Running
`claude --version` prints `Error: claude native binary not installed.` The
global package directory and `claude.cmd` can both still exist, so a shim-only
installation check appears healthy.

**Cause.** Claude Code 2.1.263 uses its npm package as a launcher. Its
`install.cjs` postinstall copies or hard-links the platform executable from the
nested `@anthropic-ai/claude-code-win32-x64` optional dependency over the small
placeholder at `bin/claude.exe`. If postinstall does not replace that placeholder,
the command shim resolves correctly but the launcher cannot start the native
binary. An observability PTY without updater guards can trigger the update and
create this failure while merely checking quota or discovering models.

**Immediate heal.** From PowerShell, replay the package postinstall and verify
the CLI:

```powershell
Set-Location "$env:APPDATA\npm\node_modules\@anthropic-ai\claude-code"
node install.cjs
claude --version
```

The final command should print a Claude Code version instead of the missing
native-binary error.

**Long-term.** Fixed in AGT-2706. Every quota, model-discovery, and diagnostic
PTY now disables CLI self-update. Windows local CLI repair classifies a Claude
launcher below 4096 bytes, or the missing-native-binary version output, as an
active launcher-stub failure. It replays `node install.cjs`, falls back to an
exact-version global npm install when the script is absent, verifies the shim
and `--version`, journals the launcher and nested-native-package evidence, and
keeps a failed repair visible in runner status until a later healthy probe.
