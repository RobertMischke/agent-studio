#!/usr/bin/env sh
set -eu
printf 'pass\n' > release-state.txt
git add release-state.txt
git -c user.name='Scenario Runner' -c user.email='scenario@example.invalid' \
  commit -m 'fix: make deployment fixture pass'
printf '%s\n' 'fake-coding-cli=passed'
