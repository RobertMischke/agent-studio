#!/usr/bin/env bash

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
driver_source="$repo_root/scripts/release/promote-develop-to-main.sh"
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-tests)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

make_fixture() {
  local name=$1
  local gate_mode=$2
  local conflict_mode=${3:-none}
  local fixture="$test_root/$name"
  local remote="$fixture/remote.git"
  local seed="$fixture/seed"
  local operator="$fixture/operator"

  mkdir -p "$fixture"
  git init --bare --quiet "$remote"
  git init --quiet "$seed"
  git -C "$seed" config user.name 'Promotion Test'
  git -C "$seed" config user.email 'promotion-test@example.invalid'
  mkdir -p "$seed/scripts/release"
  cp "$driver_source" "$seed/scripts/release/promote-develop-to-main.sh"
  chmod +x "$seed/scripts/release/promote-develop-to-main.sh"

  case "$gate_mode" in
    pass)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
test -d "${2:-}"
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    fail)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
printf '%s\n' 'fixture gate failed' >&2
exit 9
EOF
      ;;
    move-develop)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
candidate=$2
git -C "$candidate" push origin HEAD:refs/heads/develop >/dev/null
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    incomplete)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
printf '%s\n' 'fixture omitted the completion marker'
EOF
      ;;
    *)
      printf 'Unknown gate fixture: %s\n' "$gate_mode" >&2
      return 2
      ;;
  esac
  chmod +x "$seed/scripts/release/promotion-full-gate.sh"
  printf '%s\n' base > "$seed/payload.txt"
  git -C "$seed" add .
  git -C "$seed" commit --quiet -m 'base'
  git -C "$seed" branch -M main
  git -C "$seed" remote add origin "$remote"
  git -C "$seed" push --quiet -u origin main
  git --git-dir="$remote" symbolic-ref HEAD refs/heads/main

  if [[ "$conflict_mode" == conflict ]]; then
    printf '%s\n' main > "$seed/payload.txt"
    git -C "$seed" commit --quiet -am 'main-only change'
    git -C "$seed" push --quiet origin main
    git -C "$seed" checkout --quiet -b develop HEAD~1
  else
    git -C "$seed" checkout --quiet -b develop
  fi
  printf '%s\n' develop > "$seed/payload.txt"
  printf '%s\n' "develop-$name" > "$seed/develop.txt"
  git -C "$seed" add .
  git -C "$seed" commit --quiet -m 'develop work'
  git -C "$seed" push --quiet -u origin develop

  git clone --quiet --branch develop "$remote" "$operator"
  git -C "$operator" config user.name 'Promotion Operator'
  git -C "$operator" config user.email 'promotion-operator@example.invalid'
  printf '%s\n' "$operator"
}

run_expect_rc() {
  local expected=$1
  shift
  set +e
  "$@"
  local actual=$?
  set -e
  if [[ "$actual" != "$expected" ]]; then
    printf 'Expected rc=%s, got rc=%s: %s\n' "$expected" "$actual" "$*" >&2
    return 1
  fi
}

# A normal execute run produces one two-parent merge, one annotated marker,
# full-gate evidence, and an atomic remote update.
green_operator=$(make_fixture green pass)
green_remote="$test_root/green/remote.git"
green_evidence="$test_root/green/evidence"
old_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
develop=$(git --git-dir="$green_remote" rev-parse refs/heads/develop)
"$green_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-green --required-ancestor "$develop" \
  --evidence-dir "$green_evidence" >/dev/null
new_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
test "$new_main" != "$old_main"
test "$(git --git-dir="$green_remote" rev-parse "$new_main^1")" = "$old_main"
test "$(git --git-dir="$green_remote" rev-parse "$new_main^2")" = "$develop"
test "$(git --git-dir="$green_remote" cat-file -t refs/tags/release/test-green)" = tag
test "$(git --git-dir="$green_remote" rev-parse refs/tags/release/test-green^{})" = "$new_main"
grep -q '"status":"promoted"' "$green_evidence/promotion-record.json"
grep -q '"atomicPush":true' "$green_evidence/promotion-record.json"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$green_evidence/full-gate.log"

# A red or nominally green but incomplete gate cannot advance either ref.
for gate_mode in fail incomplete; do
  operator=$(make_fixture "gate-$gate_mode" "$gate_mode")
  remote="$test_root/gate-$gate_mode/remote.git"
  evidence="$test_root/gate-$gate_mode/evidence"
  before=$(git --git-dir="$remote" rev-parse refs/heads/main)
  run_expect_rc 4 "$operator/scripts/release/promote-develop-to-main.sh" \
    --execute --tag "release/test-$gate_mode" --evidence-dir "$evidence" >/dev/null 2>&1
  test "$(git --git-dir="$remote" rev-parse refs/heads/main)" = "$before"
  ! git --git-dir="$remote" show-ref --verify --quiet "refs/tags/release/test-$gate_mode"
done

# Ref movement after a green gate invalidates the tested candidate.
move_operator=$(make_fixture ref-moved move-develop)
move_remote="$test_root/ref-moved/remote.git"
move_evidence="$test_root/ref-moved/evidence"
move_main=$(git --git-dir="$move_remote" rev-parse refs/heads/main)
run_expect_rc 5 "$move_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-ref-moved --evidence-dir "$move_evidence" >/dev/null 2>&1
test "$(git --git-dir="$move_remote" rev-parse refs/heads/main)" = "$move_main"
! git --git-dir="$move_remote" show-ref --verify --quiet refs/tags/release/test-ref-moved
grep -q '"status":"blocked-ref-moved"' "$move_evidence/promotion-record.json"

# Conflicts block by default. The bootstrap-only policy resolves the same
# fixture from develop and records the exceptional choice without pushing.
conflict_operator=$(make_fixture conflict pass conflict)
conflict_evidence="$test_root/conflict/evidence-blocked"
run_expect_rc 3 "$conflict_operator/scripts/release/promote-develop-to-main.sh" \
  --dry-run --tag release/test-conflict-blocked \
  --evidence-dir "$conflict_evidence" >/dev/null 2>&1
grep -q '"status":"blocked-conflict"' "$conflict_evidence/promotion-record.json"
grep -Fxq payload.txt "$conflict_evidence/conflicts.txt"

resolved_evidence="$test_root/conflict/evidence-resolved"
"$conflict_operator/scripts/release/promote-develop-to-main.sh" \
  --dry-run --prefer-develop-conflicts --tag release/test-conflict-resolved \
  --evidence-dir "$resolved_evidence" >/dev/null
grep -q '"status":"preview"' "$resolved_evidence/promotion-record.json"
grep -q '"conflictPolicy":"develop"' "$resolved_evidence/promotion-record.json"
grep -Fxq payload.txt "$resolved_evidence/conflicts-resolved-from-develop.txt"

printf '%s\n' 'develop -> main promotion tests passed'
