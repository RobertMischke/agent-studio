// Dynamic dev-server proxy for isolated worktree test stacks (ASS-1715).
//
// proxy.conf.json / proxy.stable.json hard-code the backend port (:5030 /
// :5031). A worktree run boots its own backend on a DYNAMIC free port, so the
// proxy target cannot be a constant. Angular's dev-server accepts a JS/CJS
// proxy module, evaluated when `ng serve` starts, so we read the backend port
// from the environment that scripts/worktree-test-stack.sh exports.
//
// Usage:
//   BACKEND_PORT=53210 ng serve frontend --port 4xxxx --proxy-config proxy.dynamic.cjs
//
// Falls back to the dev default (5030) so a bare `ng serve --proxy-config
// proxy.dynamic.cjs` still works for a developer who just wants env-driven
// targeting without standing up the full worktree stack.

const backendPort = Number(process.env.BACKEND_PORT || 5030);
const backendHost = process.env.BACKEND_HOST || '127.0.0.1';
const target = `http://${backendHost}:${backendPort}`;

module.exports = {
  '/api': {
    target,
    secure: false,
    changeOrigin: true,
  },
  '/hubs': {
    target,
    secure: false,
    changeOrigin: true,
    ws: true,
  },
};
