#!/usr/bin/env bash
# Regression tests for the isolated worktree test stack (ASS-1715).
#
# Covers the parts that DON'T require building/booting the .NET backend:
#   - the free-port allocator (self-test + distinctness)
#   - the dynamic proxy config resolving its target from BACKEND_PORT
#   - the api.sh safety guards (worktree mode refuses without an isolated
#     TaskRepository; the dev-backend gate still refuses a bare start)
#
# The full boot/teardown path is verified end-to-end separately (it needs a
# real dotnet build). Run: bash scripts/worktree-test-stack.test.sh

set -u
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

pass=0; fail=0
ok()   { echo "  ok: $*"; pass=$((pass+1)); }
bad()  { echo "FAIL: $*"; fail=$((fail+1)); }

echo "== find-free-port =="
if node "${SCRIPT_DIR}/find-free-port.mjs" --self-test >/dev/null 2>&1; then
  ok "self-test passes"
else
  bad "self-test failed"
fi

ports="$(node "${SCRIPT_DIR}/find-free-port.mjs" --count 3)"
n_fields=$(echo "${ports}" | wc -w | tr -d ' ')
n_distinct=$(echo "${ports}" | tr ' ' '\n' | sort -u | wc -l | tr -d ' ')
if [[ "${n_fields}" == "3" && "${n_distinct}" == "3" ]]; then
  ok "allocated 3 distinct ports (${ports})"
else
  bad "expected 3 distinct ports, got fields=${n_fields} distinct=${n_distinct} (${ports})"
fi

echo "== proxy.dynamic.cjs =="
target="$(BACKEND_PORT=54321 node -e 'const p=require(process.argv[1]); process.stdout.write(p["/api"].target)' "${REPO_ROOT}/frontend/proxy.dynamic.cjs")"
if [[ "${target}" == "http://127.0.0.1:54321" ]]; then
  ok "resolves /api target from BACKEND_PORT (${target})"
else
  bad "expected http://127.0.0.1:54321, got '${target}'"
fi
ws_target="$(BACKEND_PORT=54321 node -e 'const p=require(process.argv[1]); process.stdout.write(String(p["/hubs"].ws))' "${REPO_ROOT}/frontend/proxy.dynamic.cjs")"
if [[ "${ws_target}" == "true" ]]; then
  ok "/hubs keeps websocket proxying enabled"
else
  bad "/hubs ws flag expected true, got '${ws_target}'"
fi

echo "== api.sh worktree safety guard =="
# Unset any inherited PORT so the port-pin guard (guard 1) doesn't pre-empt the
# dev-backend / worktree guard (guard 2) we want to exercise. An agent spawned
# by the stable backend inherits PORT=5031, which would otherwise fire first.
# worktree mode without an isolated TaskRepository must refuse (and NOT boot).
out="$(env -u PORT -u API_PORT_OVERRIDE ATP_WORKTREE_TEST_BACKEND=1 TaskRepository= \
        bash "${REPO_ROOT}/api.sh" start 2>&1)"; rc=$?
if [[ "${rc}" -ne 0 ]] && echo "${out}" | grep -qi "requires an isolated TaskRepository"; then
  ok "refuses worktree boot without isolated TaskRepository (exit ${rc})"
else
  bad "expected refusal without TaskRepository; rc=${rc} out=${out}"
fi

# the normal dev-backend gate must still refuse a bare start in a non-stable checkout.
out2="$(env -u PORT -u API_PORT_OVERRIDE -u ATP_WORKTREE_TEST_BACKEND -u ATP_ALLOW_DEV_BACKEND -u ATP_DEV_BACKEND_FROM_FIXTURE \
        bash "${REPO_ROOT}/api.sh" start 2>&1)"; rc2=$?
if [[ "${rc2}" -ne 0 ]] && echo "${out2}" | grep -qi "refusing to start the dev backend"; then
  ok "dev-backend gate still refuses a bare start (exit ${rc2})"
else
  bad "expected dev-gate refusal; rc=${rc2} out=${out2}"
fi

echo
echo "== summary: ${pass} passed, ${fail} failed =="
[[ "${fail}" -eq 0 ]]
