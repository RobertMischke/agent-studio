#!/usr/bin/env bash

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
driver_source="$repo_root/scripts/release/promote-develop-to-main.sh"
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-tests)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

make_fixture() {
  local name=$1
  local gate_mode=$2
  local topology_mode=${3:-linear}
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
remote_url=$(git -C "$candidate" remote get-url origin)
advance_checkout=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-develop-advance)
trap 'rm -rf -- "$advance_checkout"' EXIT HUP INT TERM
git clone --quiet --branch develop "$remote_url" "$advance_checkout"
git -C "$advance_checkout" config user.name 'Promotion Test Gate'
git -C "$advance_checkout" config user.email 'promotion-test-gate@example.invalid'
git -C "$advance_checkout" commit --quiet --allow-empty -m 'develop advances during gate'
git -C "$advance_checkout" push --quiet origin HEAD:refs/heads/develop
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    move-main)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
candidate=$2
remote_url=$(git -C "$candidate" remote get-url origin)
advance_checkout=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-main-advance)
trap 'rm -rf -- "$advance_checkout"' EXIT HUP INT TERM
git clone --quiet --branch main "$remote_url" "$advance_checkout"
git -C "$advance_checkout" config user.name 'Promotion Test Gate'
git -C "$advance_checkout" config user.email 'promotion-test-gate@example.invalid'
git -C "$advance_checkout" commit --quiet --allow-empty -m 'main advances during gate'
git -C "$advance_checkout" push --quiet origin HEAD:refs/heads/main
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

  if [[ "$topology_mode" == diverged ]]; then
    printf '%s\n' main > "$seed/payload.txt"
    git -C "$seed" commit --quiet -am 'main-only change'
    git -C "$seed" push --quiet origin main
    git -C "$seed" checkout --quiet -b develop HEAD~1
  else
    git -C "$seed" checkout --quiet -b develop
  fi
  printf '%s\n' develop > "$seed/payload.txt"
  printf '%s\n' "develop-$name" > "$seed/develop.txt"
  # Historical integrated content may contain whitespace findings. Promotion
  # records them for review but leaves pass/fail authority with the full gate.
  printf '%s  \n' "historical-$name" > "$seed/historical-whitespace.txt"
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

# A normal execute run promotes the exact develop tip, produces one annotated
# marker, records full-gate evidence, and performs an atomic remote update.
green_operator=$(make_fixture green pass)
green_remote="$test_root/green/remote.git"
green_evidence="$test_root/green/evidence"
old_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
develop=$(git --git-dir="$green_remote" rev-parse refs/heads/develop)
"$green_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-green --required-ancestor "$develop" \
  --evidence-dir "$green_evidence" >/dev/null
new_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
test "$new_main" = "$develop"
git --git-dir="$green_remote" merge-base --is-ancestor "$old_main" "$new_main"
test "$(git --git-dir="$green_remote" cat-file -t refs/tags/release/test-green)" = tag
test "$(git --git-dir="$green_remote" rev-parse refs/tags/release/test-green^{})" = "$new_main"
grep -q '"status":"promoted"' "$green_evidence/promotion-record.json"
grep -q '"atomicPush":true' "$green_evidence/promotion-record.json"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$green_evidence/full-gate.log"
grep -q 'historical-whitespace.txt' "$green_evidence/candidate-whitespace-review.txt"

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

# A develop advance during the gate is informational. The exact candidate that
# started the gate is promoted, while the newer develop commit waits for the
# next train.
move_operator=$(make_fixture ref-moved move-develop)
move_remote="$test_root/ref-moved/remote.git"
move_evidence="$test_root/ref-moved/evidence"
move_main=$(git --git-dir="$move_remote" rev-parse refs/heads/main)
move_candidate=$(git --git-dir="$move_remote" rev-parse refs/heads/develop)
"$move_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-ref-moved --evidence-dir "$move_evidence" >/dev/null
advanced_develop=$(git --git-dir="$move_remote" rev-parse refs/heads/develop)
test "$advanced_develop" != "$move_candidate"
test "$(git --git-dir="$move_remote" rev-parse refs/heads/main)" = "$move_candidate"
test "$(git --git-dir="$move_remote" rev-parse refs/tags/release/test-ref-moved^{})" = "$move_candidate"
git --git-dir="$move_remote" merge-base --is-ancestor "$move_main" "$move_candidate"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$move_evidence/full-gate.log"
grep -Fq "develop advanced to $advanced_develop during gate; promoting gated candidate $move_candidate" \
  "$move_evidence/promotion.log"
grep -Fq "promoted develop=$move_candidate to main=$move_candidate with tag=release/test-ref-moved candidate=$move_candidate" \
  "$move_evidence/promotion.log"
grep -q '"status":"promoted"' "$move_evidence/promotion-record.json"

# A concurrent main advance that is not an ancestor of the gated candidate
# fails the final ancestry check. The external main commit remains untouched.
main_move_operator=$(make_fixture main-moved move-main)
main_move_remote="$test_root/main-moved/remote.git"
main_move_evidence="$test_root/main-moved/evidence"
main_move_candidate=$(git --git-dir="$main_move_remote" rev-parse refs/heads/develop)
run_expect_rc 5 "$main_move_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-main-moved --evidence-dir "$main_move_evidence" >/dev/null 2>&1
main_after_gate=$(git --git-dir="$main_move_remote" rev-parse refs/heads/main)
test "$main_after_gate" != "$main_move_candidate"
! git --git-dir="$main_move_remote" merge-base --is-ancestor "$main_after_gate" "$main_move_candidate"
! git --git-dir="$main_move_remote" show-ref --verify --quiet refs/tags/release/test-main-moved
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$main_move_evidence/full-gate.log"
grep -q '"status":"blocked-non-fast-forward"' "$main_move_evidence/promotion-record.json"
grep -q '"gate":"passed"' "$main_move_evidence/promotion-record.json"

# A develop tip that is not a descendant of main is never gated or pushed.
diverged_operator=$(make_fixture diverged pass diverged)
diverged_remote="$test_root/diverged/remote.git"
diverged_evidence="$test_root/diverged/evidence"
diverged_main=$(git --git-dir="$diverged_remote" rev-parse refs/heads/main)
diverged_candidate=$(git --git-dir="$diverged_remote" rev-parse refs/heads/develop)
if git --git-dir="$diverged_remote" merge-base --is-ancestor "$diverged_main" "$diverged_candidate"; then
  printf '%s\n' 'Diverged fixture unexpectedly produced a fast-forward candidate.' >&2
  exit 1
fi
run_expect_rc 3 "$diverged_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-diverged --evidence-dir "$diverged_evidence" >/dev/null 2>&1
test "$(git --git-dir="$diverged_remote" rev-parse refs/heads/main)" = "$diverged_main"
! git --git-dir="$diverged_remote" show-ref --verify --quiet refs/tags/release/test-diverged
grep -q '"status":"blocked-non-fast-forward"' "$diverged_evidence/promotion-record.json"
test ! -e "$diverged_evidence/full-gate.log"

printf '%s\n' 'develop -> main promotion tests passed'
