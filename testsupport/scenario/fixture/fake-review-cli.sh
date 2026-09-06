#!/usr/bin/env sh
set -eu
node --test test.mjs
printf '%s\n' 'fake-review-cli=passed'
