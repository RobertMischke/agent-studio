#!/usr/bin/env sh
set -eu
test "$(tr -d '\r\n' < answer.txt)" = "42"
